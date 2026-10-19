using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;

// The data side of the widget: which Claude Code accounts exist (and their PowerShell aliases), what each one's rate-limit
// windows are (the OAuth usage endpoint), and what went wrong when a fetch failed. No UI here; Program.cs renders it.
namespace ClaudeUsageWidget.Usage;

// ── Models ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A Claude Code account, keyed by its config dir (<c>~/.claude-&lt;name&gt;</c>, the CLAUDE_CONFIG_DIR of an alias).</summary>
public sealed record Account(string Name, string ConfigDir, bool HasAlias)
{
    public string CredentialsPath => Path.Combine(ConfigDir, ".credentials.json");
    public bool SignedIn => File.Exists(CredentialsPath);
    public bool HasFolder => Directory.Exists(ConfigDir);
}

/// <summary>One rate-limit window: utilization 0..100 and when it resets.</summary>
public sealed record LimitWindow(double Percent, DateTimeOffset? ResetsAt);

/// <summary>What the widget shows for one account. <see cref="Error"/> set ⇒ the windows may be the last good values;
/// <see cref="Failure"/> is the logged failure behind it (null for "Loading…" / "Not signed in"). <see cref="Source"/> says
/// where the numbers came from: "api" (this fetch), "cli" (Claude Code's own cache) or "saved" (the widget's last run).</summary>
public sealed record AccountUsage(string Name, string Plan, LimitWindow? Session, LimitWindow? Weekly, string? Error,
    DateTimeOffset FetchedAt, ErrorEntry? Failure = null, string Source = "api")
{
    /// <summary>Headroom for the "best pick": the tighter of the two windows, as percent left.</summary>
    public double Headroom => Error is not null && Session is null ? -1
        : 100 - Math.Max(Session?.Percent ?? 0, Weekly?.Percent ?? 0);
}

/// <summary>One failed fetch: the short label the widget shows and everything behind it (request, response, trace).</summary>
public sealed record ErrorEntry(DateTimeOffset At, string Account, string Summary, string Detail);

// ── Accounts ────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The accounts on disk and their aliases: every <c>~/.claude-*</c> dir with OAuth credentials plus every
/// <c>function claude-&lt;name&gt;</c> in the PowerShell profile(s). Create / rename / delete keep the folder and the alias
/// in step; rename and delete refuse while a Claude Code session or daemon is still running on the account.
/// </summary>
public static partial class AccountStore
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string DirFor(string name) => Path.Combine(Home, ".claude-" + name);

    /// <summary>Signed-in folders and aliased names (claude-ohsnap, claude-work2, …), name = the suffix, sorted.</summary>
    public static IReadOnlyList<Account> Discover()
    {
        var aliases = Profile.AliasNames();
        var found = new List<Account>();
        foreach (string dir in Directory.EnumerateDirectories(Home, ".claude-*"))
        {
            string name = Path.GetFileName(dir)[".claude-".Length..];
            if (File.Exists(Path.Combine(dir, ".credentials.json")) || aliases.Contains(name))
                found.Add(new Account(name, dir, aliases.Contains(name)));
        }
        foreach (string name in aliases)
            if (!found.Exists(a => a.Name == name)) found.Add(new Account(name, DirFor(name), true));
        found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return found;
    }

    /// <summary>Why <paramref name="name"/> can't be used for a new or renamed account, or null when it can.</summary>
    public static string? Validate(string name, string? renaming = null)
    {
        if (name.Length == 0) return "Enter a name.";
        if (!ValidName().IsMatch(name)) return "Use lowercase letters, digits, - or _ (max 32).";
        if (name == renaming) return "That's the current name.";
        if (Directory.Exists(DirFor(name)) || Profile.AliasNames().Contains(name)) return $"claude-{name} already exists.";
        return null;
    }

    public static void Create(string name)
    {
        Directory.CreateDirectory(DirFor(name));
        Profile.AddAlias(name);
    }

    public static void Rename(Account account, string newName)
    {
        EnsureIdle(account);
        if (account.HasFolder)
        {
            string target = DirFor(newName);
            try { Directory.Move(account.ConfigDir, target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"claude-{account.Name} is in use (a file inside it is open). Close what's using it and try again.", ex);
            }
            RepointPlugins(target, account.Name, newName);
        }
        if (account.HasAlias) Profile.RenameAlias(account.Name, newName);
        else Profile.AddAlias(newName);
    }

    /// <summary>Removes the alias and sends the folder (credentials, history, settings) to the Recycle Bin.</summary>
    public static void Delete(Account account)
    {
        EnsureIdle(account);
        if (account.HasFolder) RecycleBin.Send(account.ConfigDir);
        if (account.HasAlias) Profile.RemoveAlias(account.Name);
    }

    /// <summary>A new terminal running <c>claude</c> on this account — the CLI asks a fresh account to sign in.</summary>
    public static void OpenTerminal(Account account)
    {
        Directory.CreateDirectory(account.ConfigDir);
        var psi = new ProcessStartInfo("powershell.exe", "-NoExit -Command claude")
        {
            UseShellExecute = false,   // a console child of a GUI process gets its own (Windows Terminal) window
            WorkingDirectory = Home,
        };
        psi.Environment["CLAUDE_CONFIG_DIR"] = account.ConfigDir;
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Running Claude Code processes on the account: each session leaves <c>sessions/&lt;pid&gt;.json</c>, the
    /// daemon a <c>daemon.lock</c>. Stale markers (dead pid, or a pid reused by another program) don't count.</summary>
    public static int RunningSessions(Account account)
    {
        if (!account.HasFolder) return 0;
        var pids = new HashSet<int>();
        string sessions = Path.Combine(account.ConfigDir, "sessions");
        if (Directory.Exists(sessions))
            foreach (string f in Directory.EnumerateFiles(sessions, "*.json"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(f), out int pid)) pids.Add(pid);
        string daemonLock = Path.Combine(account.ConfigDir, "daemon.lock");
        if (File.Exists(daemonLock) && DaemonPid().Match(SafeRead(daemonLock)) is { Success: true } m) pids.Add(int.Parse(m.Groups[1].Value));

        int running = 0;
        foreach (int pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited && p.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase)) running++;
            }
            catch (ArgumentException) { }   // not running
        }
        return running;
    }

    static void EnsureIdle(Account account)
    {
        int n = RunningSessions(account);
        if (n > 0)
            throw new InvalidOperationException($"claude-{account.Name} has {n} running Claude Code {(n == 1 ? "session" : "sessions")}. Close {(n == 1 ? "it" : "them")} first.");
    }

    /// <summary>The plugin registries store absolute install paths; point them at the renamed dir.</summary>
    static void RepointPlugins(string dir, string oldName, string newName)
    {
        foreach (string file in (ReadOnlySpan<string>)["installed_plugins.json", "known_marketplaces.json"])
        {
            string path = Path.Combine(dir, "plugins", file);
            if (!File.Exists(path)) continue;
            string text = File.ReadAllText(path);
            // JSON-escaped backslash, forward slash, or the dir itself at the end of a string; never .claude-work2 for work.
            string updated = text.Replace($".claude-{oldName}\\\\", $".claude-{newName}\\\\")
                .Replace($".claude-{oldName}/", $".claude-{newName}/")
                .Replace($".claude-{oldName}\"", $".claude-{newName}\"");
            if (updated != text) File.WriteAllText(path, updated, new UTF8Encoding(false));
        }
    }

    static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return ""; }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,31}$")] private static partial Regex ValidName();
    [GeneratedRegex("\"pid\"\\s*:\\s*(\\d+)")] private static partial Regex DaemonPid();

    /// <summary>
    /// The <c>function claude-&lt;name&gt; { $env:CLAUDE_CONFIG_DIR = …; claude @args }</c> aliases in the Windows PowerShell
    /// and PowerShell 7 profiles. Only those blocks are touched; the rest of each profile is kept byte for byte, and a
    /// one-time <c>.widget-bak</c> copy is made before the first edit.
    /// </summary>
    static partial class Profile
    {
        static IEnumerable<string> Paths()
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            yield return Path.Combine(docs, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1");
            yield return Path.Combine(docs, "PowerShell", "Microsoft.PowerShell_profile.ps1");
        }

        public static HashSet<string> AliasNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in Paths())
                if (File.Exists(path))
                    foreach (Match m in AliasBlock().Matches(File.ReadAllText(path))) names.Add(m.Groups["name"].Value);
            return names;
        }

        public static void AddAlias(string name)
        {
            bool any = false;
            foreach (string path in Paths())
            {
                if (!File.Exists(path)) continue;
                any = true;
                string text = File.ReadAllText(path);
                if (Find(text, name) is not null) continue;
                string nl = NewLine(text);
                Write(path, text.TrimEnd() + nl + nl + Block(name, nl));
            }
            if (!any)
            {
                string path = Paths().First();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Write(path, "# Claude Code - per-account config isolation via CLAUDE_CONFIG_DIR\r\n\r\n" + Block(name, "\r\n"));
            }
        }

        public static void RenameAlias(string oldName, string newName)
            => Edit(oldName, (before, after, nl) => before + Block(newName, nl) + after);

        // Close the gap the block leaves: one blank line between its neighbours, none at the end of the file.
        public static void RemoveAlias(string name)
            => Edit(name, (before, after, nl) =>
            {
                before = before.TrimEnd('\r', '\n');
                after = after.TrimStart('\r', '\n');
                return before.Length == 0 ? after : after.Length == 0 ? before + nl : before + nl + nl + after;
            });

        static void Edit(string name, Func<string, string, string, string> splice)
        {
            foreach (string path in Paths())
            {
                if (!File.Exists(path)) continue;
                string text = File.ReadAllText(path);
                if (Find(text, name) is not { } m) continue;
                Write(path, splice(text[..m.Index], text[(m.Index + m.Length)..], NewLine(text)));
            }
        }

        // Written in the profile's own line endings, so an edit never mixes them.
        static string NewLine(string text) => text.Contains("\r\n") || !text.Contains('\n') ? "\r\n" : "\n";

        static Match? Find(string text, string name)
        {
            foreach (Match m in AliasBlock().Matches(text))
                if (m.Groups["name"].Value == name) return m;
            return null;
        }

        static string Block(string name, string nl)
            => $"function claude-{name} {{{nl}    $env:CLAUDE_CONFIG_DIR = \"$env:USERPROFILE\\.claude-{name}\"{nl}    claude @args{nl}}}{nl}";

        static void Write(string path, string text)
        {
            string bak = path + ".widget-bak";
            if (File.Exists(path) && !File.Exists(bak)) File.Copy(path, bak);
            // Keep a UTF-8 BOM if the profile had one (Windows PowerShell reads a BOM-less file as ANSI).
            bool bom = File.Exists(path) && File.ReadAllBytes(path) is [0xEF, 0xBB, 0xBF, ..];
            File.WriteAllText(path, text, new UTF8Encoding(bom));
        }

        // A whole alias function (one level of braces, must set CLAUDE_CONFIG_DIR) plus its trailing line break.
        [GeneratedRegex(@"^function\s+claude-(?<name>[A-Za-z0-9_-]+)\s*\{[^{}]*CLAUDE_CONFIG_DIR[^{}]*\}[ \t]*(\r?\n)?", RegexOptions.Multiline)]
        private static partial Regex AliasBlock();
    }

    static partial class RecycleBin
    {
        /// <summary>Deletes <paramref name="path"/> to the Recycle Bin; Windows asks before deleting anything too big to recycle.</summary>
        public static void Send(string path)
        {
            nint from = Marshal.StringToHGlobalUni(path + "\0");   // the API wants a double-NUL-terminated list
            try
            {
                var op = new SHFILEOPSTRUCTW
                {
                    wFunc = FO_DELETE, pFrom = from,
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
                };
                int rc = SHFileOperationW(ref op);
                if (rc != 0) throw new InvalidOperationException($"Couldn't move the folder to the Recycle Bin (error 0x{rc:X}). Is something still using it?");
                if (op.fAnyOperationsAborted != 0) throw new OperationCanceledException("Delete cancelled.");
            }
            finally { Marshal.FreeHGlobal(from); }
        }

        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        struct SHFILEOPSTRUCTW
        {
            public nint hwnd;
            public uint wFunc;
            public nint pFrom, pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public nint hNameMappings, lpszProgressTitle;
        }

        [LibraryImport("shell32.dll")] private static partial int SHFileOperationW(ref SHFILEOPSTRUCTW op);
    }
}

// ── Fetching usage ──────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Reads each account's Claude Code OAuth credentials, refreshes an expired access token (writing the rotated tokens back
/// so the CLI stays signed in), and fetches <c>/api/oauth/usage</c> — the endpoint behind the CLI's <c>/usage</c>.
/// A failed refresh (429 included) backs off per account instead of retrying every poll, at least as long as the
/// server's Retry-After. Every failure is written to <see cref="ErrorLog"/> with the request, response and trace.
/// <para>The endpoint allows very few calls per account (a second call a minute or two later can get 429), so the
/// widget calls it only when nothing fresh is known: Claude Code caches the same response in <c>.claude.json</c>
/// (<c>cachedUsageUtilization</c>), and the widget saves its own last results so a restart doesn't refetch.</para>
/// </summary>
public sealed class UsageClient : IDisposable
{
    const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    static readonly string[] TokenUrls = ["https://platform.claude.com/v1/oauth/token", "https://console.anthropic.com/v1/oauth/token"];
    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    /// <summary>Numbers younger than this are shown as they are instead of calling the endpoint again.</summary>
    static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);
    static readonly string SavedPath = Path.Combine(ErrorLog.Dir, "usage-cache.json");

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly Dictionary<string, AccountUsage> _last = new();
    readonly Dictionary<string, (DateTimeOffset Until, int Failures, ErrorEntry? Cause)> _backoff = new();

    public UsageClient() => LoadSaved();

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
        // An alias that hasn't signed in yet: nothing to fetch, and no backoff to sit out once it has.
        if (!account.SignedIn) return new AccountUsage(account.Name, plan, null, null, "Not signed in", DateTimeOffset.Now);
        try
        {
            JsonNode creds = ReadCreds(account);
            plan = (string?)creds["claudeAiOauth"]?["subscriptionType"] ?? "";
            // Fresh enough (ours, or the CLI's own fetch): don't spend the account's tight usage quota.
            if (Known(account, plan) is { } known && DateTimeOffset.Now - known.FetchedAt < FreshFor)
                return known with { Error = null, Failure = null };
            if (_backoff.TryGetValue(account.Name, out var b) && b.Until > DateTimeOffset.UtcNow)
                return Stale(account, plan, $"{b.Cause?.Summary ?? "Failed"} · retry in {Countdown.Format(b.Until - DateTimeOffset.UtcNow)}", b.Cause);

            if (IsExpired(creds)) creds = await RefreshAsync(account, ct).ConfigureAwait(false);
            using var resp = await GetUsageAsync(account, creds, ct).ConfigureAwait(false);
            HttpResponseMessage final = resp;
            HttpResponseMessage? retry = null;
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The CLI may have rotated the token since we read it, or it was revoked: refresh once and retry.
                creds = await RefreshAsync(account, ct, force: true).ConfigureAwait(false);
                final = retry = await GetUsageAsync(account, creds, ct).ConfigureAwait(false);
            }
            using (retry)
            {
                if (!final.IsSuccessStatusCode) throw await ApiException.FromAsync("GET", UsageUrl, final, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(await final.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
                var usage = new AccountUsage(account.Name, plan, Window(doc.RootElement, "five_hour"),
                    Window(doc.RootElement, "seven_day"), null, DateTimeOffset.Now);
                _backoff.Remove(account.Name);
                _last[account.Name] = usage;
                Save();
                return usage;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            string summary = Describe(ex);
            var cause = BackOff(account, ex, summary);
            return Stale(account, plan, summary, cause);
        }
    }

    AccountUsage Stale(Account account, string plan, string error, ErrorEntry? cause)
        => Known(account, plan) is { } last
            ? last with { Error = error, Failure = cause }
            : new AccountUsage(account.Name, plan, null, null, error, DateTimeOffset.Now, cause);

    /// <summary>The newest numbers known for the account without a request: the widget's own last result (this run or
    /// saved by the last one) or Claude Code's cached fetch, whichever is younger.</summary>
    AccountUsage? Known(Account account, string plan)
    {
        _last.TryGetValue(account.Name, out var ours);
        var cli = CliCache(account, plan);
        return ours is null ? cli : cli is null || ours.FetchedAt >= cli.FetchedAt ? ours : cli;
    }

    /// <summary>Claude Code's <c>cachedUsageUtilization</c> in <c>&lt;config&gt;/.claude.json</c>: the same usage response,
    /// stamped with when the CLI fetched it. Null when absent, unreadable or mid-write.</summary>
    static AccountUsage? CliCache(Account account, string plan)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(account.ConfigDir, ".claude.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("cachedUsageUtilization", out var c) || c.ValueKind != JsonValueKind.Object
                || !c.TryGetProperty("utilization", out var u) || !c.TryGetProperty("fetchedAtMs", out var at) || !at.TryGetInt64(out long ms))
                return null;
            return new AccountUsage(account.Name, plan, Window(u, "five_hour"), Window(u, "seven_day"), null,
                DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime(), Source: "cli");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// What a restart must not forget, per account: the last good result (in the usage response's own shape) and an
    /// unexpired backoff. Without the backoff a restart would call an endpoint that just answered 429 straight away.
    /// </summary>
    void Save()
    {
        var root = new JsonObject();
        foreach (var (name, u) in _last)
        {
            if (u.Source == "cli") continue;   // the CLI keeps its own copy
            root[name] = new JsonObject
            {
                ["plan"] = u.Plan,
                ["fetchedAtMs"] = u.FetchedAt.ToUnixTimeMilliseconds(),
                ["five_hour"] = WindowJson(u.Session),
                ["seven_day"] = WindowJson(u.Weekly),
            };
        }
        foreach (var (name, b) in _backoff)
        {
            if (b.Until <= DateTimeOffset.UtcNow) continue;
            var entry = root[name] as JsonObject ?? new JsonObject();
            entry["retryAtMs"] = b.Until.ToUnixTimeMilliseconds();
            entry["failures"] = b.Failures;
            entry["error"] = b.Cause?.Summary;
            root[name] = entry;
        }
        try
        {
            Directory.CreateDirectory(ErrorLog.Dir);
            File.WriteAllText(SavedPath, root.ToJsonString(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static JsonNode? WindowJson(LimitWindow? w) => w is null ? null : new JsonObject
    {
        ["utilization"] = w.Percent,
        ["resets_at"] = w.ResetsAt?.ToString("O"),
    };

    void LoadSaved()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SavedPath));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var v = p.Value;
                if (v.TryGetProperty("fetchedAtMs", out var at) && at.TryGetInt64(out long ms))
                    _last[p.Name] = new AccountUsage(p.Name, v.TryGetProperty("plan", out var plan) ? plan.GetString() ?? "" : "",
                        Window(v, "five_hour"), Window(v, "seven_day"), null,
                        DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime(), Source: "saved");
                if (v.TryGetProperty("retryAtMs", out var r) && r.TryGetInt64(out long retryMs)
                    && DateTimeOffset.FromUnixTimeMilliseconds(retryMs) is var until && until > DateTimeOffset.UtcNow)
                {
                    string summary = v.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } s ? s : "Failed";
                    var cause = new ErrorEntry(DateTimeOffset.Now, p.Name, summary,
                        $"Backoff carried over from the previous run (retry at {until.ToLocalTime():HH:mm:ss}). The original failure is in {ErrorLog.FilePath}.");
                    _backoff[p.Name] = (until, v.TryGetProperty("failures", out var f) && f.TryGetInt32(out int n) ? n : 1, cause);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
    }

    /// <summary>Schedules the next attempt and logs the failure; the entry is what the widget links to.</summary>
    ErrorEntry BackOff(Account account, Exception ex, string summary)
    {
        int failures = _backoff.TryGetValue(account.Name, out var b) ? b.Failures + 1 : 1;
        // 429 and network errors: 1, 2, 4 … 30 min. Not a polling storm against an endpoint that already said slow down.
        var delay = TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, failures - 1)));
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } && delay < TimeSpan.FromMinutes(2))
            delay = TimeSpan.FromMinutes(2);
        if (ex is ApiException { RetryAfter: { } after } && after > delay) delay = after;
        var cause = ErrorLog.Add(account.Name, summary, Detail(account, ex, failures, delay));
        _backoff[account.Name] = (DateTimeOffset.UtcNow + delay, failures, cause);
        Save();
        return cause;
    }

    /// <summary>The full story of one failure, plain text: account, files, the HTTP exchange and the stack trace.</summary>
    static string Detail(Account account, Exception ex, int failures, TimeSpan delay)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Account:      {account.Name} ({account.ConfigDir})");
        sb.AppendLine($"Credentials:  {account.CredentialsPath}");
        if (ex is ApiException api)
        {
            sb.AppendLine($"Request:      {api.Method} {api.Url}");
            sb.AppendLine($"Status:       {(int)api.Status} {api.Status}");
            foreach (var (k, v) in api.Headers) sb.AppendLine($"Header:       {k}: {v}");
            sb.AppendLine($"Body:         {(api.Body.Length == 0 ? "(empty)" : api.Body)}");
        }
        sb.AppendLine($"Failures:     {failures} in a row");
        sb.AppendLine($"Next retry:   in {Countdown.Format(delay)} ({DateTimeOffset.Now + delay:HH:mm:ss})");
        sb.AppendLine();
        sb.Append(ex.ToString());
        return sb.ToString();
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

    async Task<HttpResponseMessage> GetUsageAsync(Account account, JsonNode creds, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (string?)creds["claudeAiOauth"]?["accessToken"]);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        ErrorLog.Request(account.Name, "GET", UsageUrl, resp.StatusCode);
        return resp;
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
            ErrorLog.Request(account.Name, "POST", url, resp.StatusCode);
            if (!resp.IsSuccessStatusCode)
            {
                last = await ApiException.FromAsync("POST", url, resp, ct).ConfigureAwait(false);
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

    /// <summary>A non-success HTTP response with what's needed to diagnose it: the request, Retry-After, the rate-limit and
    /// request-id headers and the (truncated) body. Never the Authorization header or a token.</summary>
    public sealed class ApiException(string method, string url, HttpStatusCode status, TimeSpan? retryAfter,
        IReadOnlyList<(string, string)> headers, string body)
        : HttpRequestException($"{method} {new Uri(url).AbsolutePath} → {(int)status} {status}", null, status)
    {
        public string Method { get; } = method;
        public string Url { get; } = url;
        public HttpStatusCode Status { get; } = status;
        public TimeSpan? RetryAfter { get; } = retryAfter;
        public IReadOnlyList<(string Name, string Value)> Headers { get; } = headers;
        public string Body { get; } = body;

        static readonly HashSet<string> DiagnosticHeaders = new(StringComparer.OrdinalIgnoreCase) { "request-id", "retry-after", "x-should-retry" };

        public static async Task<ApiException> FromAsync(string method, string url, HttpResponseMessage resp, CancellationToken ct)
        {
            TimeSpan? retryAfter = resp.Headers.RetryAfter switch
            {
                { Delta: { } d } => d,
                { Date: { } at } => at - DateTimeOffset.UtcNow,
                _ => null,
            };
            var headers = new List<(string, string)>();
            foreach (var h in resp.Headers)
                if (h.Key.StartsWith("anthropic-ratelimit", StringComparison.OrdinalIgnoreCase) || DiagnosticHeaders.Contains(h.Key))
                    headers.Add((h.Key, string.Join(", ", h.Value)));
            string body;
            try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
            catch (HttpRequestException) { body = "(unreadable)"; }
            // The token endpoint echoes nothing secret on failure, but cap it: an HTML error page can be large.
            if (body.Length > 2000) body = body[..2000] + "…";
            return new ApiException(method, url, resp.StatusCode, retryAfter, headers, body);
        }
    }
}

// ── Error log ───────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Failed fetches, kept in memory for the errors page (newest first, per account) and appended to
/// <c>%LOCALAPPDATA%\ClaudeUsageWidget\errors.log</c>, which rolls over to <c>errors.log.1</c> at 1 MB.
/// </summary>
public static class ErrorLog
{
    const int Keep = 50;
    const long MaxBytes = 1 << 20;

    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageWidget");
    public static readonly string FilePath = Path.Combine(Dir, "errors.log");

    static readonly object Gate = new();
    static readonly LinkedList<ErrorEntry> Recent = new();

    public static ErrorEntry Add(string account, string summary, string detail)
    {
        var entry = new ErrorEntry(DateTimeOffset.Now, account, summary, detail);
        lock (Gate)
        {
            Recent.AddFirst(entry);
            if (Recent.Count > Keep) Recent.RemoveLast();
            try
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes) File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"[{entry.At:yyyy-MM-dd HH:mm:ss zzz}] {account}: {summary}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
            catch (IOException) { }   // the log is a convenience: a locked or full disk must not break polling
            catch (UnauthorizedAccessException) { }
        }
        return entry;
    }

    public static readonly string RequestsPath = Path.Combine(Dir, "requests.log");

    /// <summary>One line per HTTP call (time, account, method, URL, status) to <c>requests.log</c>: the call cadence that
    /// decides whether the endpoint rate-limits. Rolls over like the error log.</summary>
    public static void Request(string account, string method, string url, System.Net.HttpStatusCode status)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(RequestsPath) && new FileInfo(RequestsPath).Length > MaxBytes) File.Move(RequestsPath, RequestsPath + ".1", overwrite: true);
                File.AppendAllText(RequestsPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {account,-12} {method,-4} {url} -> {(int)status}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The last <paramref name="count"/> lines of <c>requests.log</c>, oldest first.</summary>
    public static string[] RecentRequests(int count)
    {
        try
        {
            using var stream = new FileStream(RequestsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var lines = new StreamReader(stream).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines[Math.Max(0, lines.Length - count)..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The account's recent failures, newest first (all accounts when <paramref name="account"/> is null).</summary>
    public static IReadOnlyList<ErrorEntry> For(string? account)
    {
        lock (Gate) return [.. Recent.Where(e => account is null || e.Account == account)];
    }
}

// ── Formatting ──────────────────────────────────────────────────────────────────────────────────────────────────────────

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
