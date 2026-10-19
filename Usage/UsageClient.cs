using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeUsageWidget.Usage;

/// <summary>One rate-limit window: utilization 0..100 and when it resets.</summary>
public sealed record LimitWindow(double Percent, DateTimeOffset? ResetsAt);

/// <summary>A Claude Code account, keyed by its config dir (<c>~/.claude-&lt;name&gt;</c>, the CLAUDE_CONFIG_DIR of an alias).</summary>
public sealed record Account(string Name, string ConfigDir)
{
    public string CredentialsPath => Path.Combine(ConfigDir, ".credentials.json");

    /// <summary>Every <c>~/.claude-*</c> dir that holds OAuth credentials (claude-legacy, claude-work, …), name = the suffix.</summary>
    public static IReadOnlyList<Account> Discover()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var found = new List<Account>();
        foreach (string dir in Directory.EnumerateDirectories(home, ".claude-*"))
        {
            if (File.Exists(Path.Combine(dir, ".credentials.json")))
                found.Add(new Account(Path.GetFileName(dir)[".claude-".Length..], dir));
        }
        found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return found;
    }
}

/// <summary>What the widget shows for one account. <see cref="Error"/> set ⇒ the windows may be the last good values.</summary>
public sealed record AccountUsage(string Name, string Plan, LimitWindow? Session, LimitWindow? Weekly, string? Error,
    DateTimeOffset FetchedAt)
{
    /// <summary>Headroom for the "best pick": the tighter of the two windows, as percent left.</summary>
    public double Headroom => Error is not null && Session is null ? -1
        : 100 - Math.Max(Session?.Percent ?? 0, Weekly?.Percent ?? 0);
}

/// <summary>
/// Reads each account's Claude Code OAuth credentials, refreshes an expired access token (writing the rotated tokens back
/// so the CLI stays signed in), and fetches <c>/api/oauth/usage</c> — the endpoint behind the CLI's <c>/usage</c>.
/// A failed refresh (429 included) backs off per account instead of retrying every poll.
/// </summary>
public sealed class UsageClient : IDisposable
{
    const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    static readonly string[] TokenUrls = ["https://platform.claude.com/v1/oauth/token", "https://console.anthropic.com/v1/oauth/token"];
    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly Dictionary<string, AccountUsage> _last = new();
    readonly Dictionary<string, (DateTimeOffset Until, int Failures)> _backoff = new();

    public async Task<IReadOnlyList<AccountUsage>> FetchAllAsync(IReadOnlyList<Account> accounts, CancellationToken ct)
    {
        // Sequential: a handful of accounts, and the backoff/last-good maps stay single-threaded.
        var result = new AccountUsage[accounts.Count];
        for (int i = 0; i < accounts.Count; i++) result[i] = await FetchAsync(accounts[i], ct).ConfigureAwait(false);
        return result;
    }

    async Task<AccountUsage> FetchAsync(Account account, CancellationToken ct)
    {
        string plan = "";
        try
        {
            JsonNode creds = ReadCreds(account);
            plan = (string?)creds["claudeAiOauth"]?["subscriptionType"] ?? "";
            if (_backoff.TryGetValue(account.Name, out var b) && b.Until > DateTimeOffset.UtcNow)
                return Stale(account, plan, $"Retrying in {Countdown.Format(b.Until - DateTimeOffset.UtcNow)}");

            if (IsExpired(creds)) creds = await RefreshAsync(account, ct).ConfigureAwait(false);
            using var resp = await GetUsageAsync(creds, ct).ConfigureAwait(false);
            HttpResponseMessage final = resp;
            HttpResponseMessage? retry = null;
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The CLI may have rotated the token since we read it, or it was revoked: refresh once and retry.
                creds = await RefreshAsync(account, ct, force: true).ConfigureAwait(false);
                final = retry = await GetUsageAsync(creds, ct).ConfigureAwait(false);
            }
            using (retry)
            {
                if (!final.IsSuccessStatusCode) throw new HttpRequestException($"usage {(int)final.StatusCode}", null, final.StatusCode);
                using var doc = JsonDocument.Parse(await final.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
                var usage = new AccountUsage(account.Name, plan, Window(doc.RootElement, "five_hour"),
                    Window(doc.RootElement, "seven_day"), null, DateTimeOffset.Now);
                _backoff.Remove(account.Name);
                _last[account.Name] = usage;
                return usage;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            BackOff(account.Name, ex);
            return Stale(account, plan, Describe(ex));
        }
    }

    AccountUsage Stale(Account account, string plan, string error)
        => _last.TryGetValue(account.Name, out var last)
            ? last with { Error = error }
            : new AccountUsage(account.Name, plan, null, null, error, DateTimeOffset.Now);

    void BackOff(string name, Exception ex)
    {
        int failures = _backoff.TryGetValue(name, out var b) ? b.Failures + 1 : 1;
        // 429 and network errors: 1, 2, 4 … 30 min. Not a polling storm against an endpoint that already said slow down.
        var delay = TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, failures - 1)));
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } && delay < TimeSpan.FromMinutes(2))
            delay = TimeSpan.FromMinutes(2);
        _backoff[name] = (DateTimeOffset.UtcNow + delay, failures);
    }

    static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "Rate limited",
        HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized } => "Sign in again with the CLI",
        HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code}",
        HttpRequestException or TaskCanceledException => "Offline",
        FileNotFoundException => "Not signed in",
        _ => ex.Message,
    };

    static LimitWindow? Window(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        double pct = w.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;
        DateTimeOffset? resets = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), out var at) ? at : null;
        return new LimitWindow(pct, resets);
    }

    Task<HttpResponseMessage> GetUsageAsync(JsonNode creds, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (string?)creds["claudeAiOauth"]?["accessToken"]);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        return _http.SendAsync(req, ct);
    }

    static bool IsExpired(JsonNode creds)
        => ((long?)creds["claudeAiOauth"]?["expiresAt"] ?? 0) <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000;

    async Task<JsonNode> RefreshAsync(Account account, CancellationToken ct, bool force = false)
    {
        // Re-read right before refreshing: the CLI may already have rotated the token (a refresh token is single-use, so
        // refreshing a stale copy would sign the CLI out).
        JsonNode creds = ReadCreds(account);
        if (!force && !IsExpired(creds)) return creds;
        JsonNode oauth = creds["claudeAiOauth"] ?? throw new InvalidDataException("no claudeAiOauth");
        string body = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = (string?)oauth["refreshToken"],
            ["client_id"] = ClientId,
        }.ToJsonString();

        HttpRequestException? last = null;
        foreach (string url in TokenUrls)
        {
            using var resp = await _http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                last = new HttpRequestException($"refresh {(int)resp.StatusCode}", null, resp.StatusCode);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests) break;   // the same limiter sits behind both hosts
                continue;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            oauth["accessToken"] = root.GetProperty("access_token").GetString();
            if (root.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } refresh)
                oauth["refreshToken"] = refresh;
            oauth["expiresAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + root.GetProperty("expires_in").GetInt64() * 1000;
            WriteCreds(account, creds);
            return creds;
        }
        throw last ?? new HttpRequestException("refresh failed");
    }

    static JsonNode ReadCreds(Account account)
        => JsonNode.Parse(File.ReadAllText(account.CredentialsPath)) ?? throw new InvalidDataException("empty credentials");

    static void WriteCreds(Account account, JsonNode creds)
    {
        string path = account.CredentialsPath;
        string bak = path + ".widget-bak";
        if (!File.Exists(bak)) File.Copy(path, bak);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, creds.ToJsonString(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public void Dispose() => _http.Dispose();
}

public static class Countdown
{
    /// <summary>"3h 40m", "47m", "2d 4h" — the reset countdown the widget prints.</summary>
    public static string Format(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return "now";
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))}m";
    }

    /// <summary>A weekly reset more than a day out reads as a day + time ("Fri 02:00"); sooner, as a countdown.</summary>
    public static string Reset(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } a) return "";
        var left = a - now;
        return left.TotalHours > 24 ? a.ToLocalTime().ToString("ddd HH:mm") : Format(left);
    }
}
