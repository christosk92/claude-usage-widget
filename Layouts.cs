using ClaudeUsageWidget.Usage;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace ClaudeUsageWidget;

/// <summary>
/// The four pickable layouts (the design canvas: A compact card, B rings, C strip, D detailed). WinUI language throughout:
/// Mica/acrylic window, card fill + card stroke, 8px overlay radius, Segoe UI Variable type ramp, and the ProgressBar's own
/// Normal/Paused/Error states for ok / getting close / nearly out.
/// </summary>
static class Layouts
{
    const float HeaderHeight = 40f;

    /// <summary>The account with the most room left on its tighter window (the one to switch to), or null before data.</summary>
    public static AccountUsage? BestPick(IReadOnlyList<AccountUsage> usage)
    {
        AccountUsage? best = null;
        foreach (var u in usage) if (u.Session is not null && (best is null || u.Headroom > best.Headroom)) best = u;
        return best;
    }

    /// <summary>Headroom (percent left) → the WinUI status fill: critical under 10, caution under 25, success otherwise.</summary>
    public static ColorF HeadroomColor(double headroom)
        => headroom < 10 ? Tok.SystemFillCritical : headroom < 25 ? Tok.SystemFillCaution : Tok.SystemFillSuccess;

    // ── A · Compact ─────────────────────────────────────────────────────────────────────────────────────────────────

    public static BoxEl Compact(IReadOnlyList<AccountUsage> usage, Chrome c)
    {
        var rows = new Element[usage.Count];
        for (int i = 0; i < usage.Count; i++)
        {
            var u = usage[i];
            rows[i] = new BoxEl
            {
                Direction = 1, Gap = 6,
                Children =
                [
                    AccountLine(u, 14f),
                    Bar("5h", u.Session, 156f, 20f, c.Now),
                    Bar("7d", u.Weekly, 156f, 20f, c.Now),
                ],
            };
        }
        return Page(Header("Claude Code usage", c), new BoxEl { Direction = 1, Gap = 8, Padding = new Edges4(14, 0, 14, 8), Children = rows });
    }

    // ── B · Rings ───────────────────────────────────────────────────────────────────────────────────────────────────

    public static BoxEl Rings(IReadOnlyList<AccountUsage> usage, Chrome c)
    {
        var cols = new Element[usage.Count];
        for (int i = 0; i < usage.Count; i++)
        {
            var u = usage[i];
            float s = (float)(u.Session?.Percent ?? 0) / 100f, w = (float)(u.Weekly?.Percent ?? 0) / 100f;
            cols[i] = new BoxEl
            {
                Direction = 1, Width = 128, Gap = 6, AlignItems = FlexAlign.Center,
                Children =
                [
                    new BoxEl
                    {
                        ZStack = true, Width = 96, Height = 96,
                        Children =
                        [
                            ProgressRing.Determinate(s, 96f, foreground: Severity(u.Session), track: Tok.FillControlSecondary),
                            Centered(96, ProgressRing.Determinate(w, 64f, foreground: Severity(u.Weekly), track: Tok.FillControlSecondary)),
                            Centered(96, new TextEl(Pct(u.Session)) { Size = 14f, Weight = 600, Color = Tok.TextPrimary }),
                        ],
                    },
                    AccountLine(u, 13f, center: true),
                    new TextEl(WindowLine("5h", u.Session, c.Now)) { Size = 12f, Color = Tok.TextSecondary },
                    new TextEl(WindowLine("7d", u.Weekly, c.Now)) { Size = 12f, Color = Tok.TextSecondary },
                ],
            };
        }
        return Page(Header("Claude Code usage", c),
            new BoxEl { Direction = 0, Gap = 8, Padding = new Edges4(12, 4, 12, 12), Justify = FlexJustify.Center, Children = cols });
    }

    // ── C · Strip ───────────────────────────────────────────────────────────────────────────────────────────────────

    public static BoxEl Strip(IReadOnlyList<AccountUsage> usage, Chrome c)
    {
        var items = new List<Element>(usage.Count * 2 + 3)
        {
            // The gripper is the drag band: the rest of the strip stays client so right-click opens the widget menu.
            new BoxEl
            {
                Width = 32, Height = 48, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Ui.Icon(Icons.GripperBar) with { Size = 14f, Color = Tok.TextTertiary }],
            }.WindowDragArea(),
        };
        for (int i = 0; i < usage.Count; i++)
        {
            var u = usage[i];
            if (i > 0) items.Add(new BoxEl { Width = 1, Height = 24, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(12, 0, 12, 0) });
            items.Add(new BoxEl
            {
                Direction = 0, Gap = 8, AlignItems = FlexAlign.Center, Width = 176,
                Children =
                [
                    new TextEl(u.Name) { Size = 12f, Weight = 600, Color = Tok.TextPrimary, Width = 56, Trim = TextTrim.CharacterEllipsis, MaxLines = 1 },
                    new BoxEl
                    {
                        Direction = 1, Gap = 4,
                        Children = [ProgressBar.Determinate(Frac(u.Session), 56f, State(u.Session)), ProgressBar.Determinate(Frac(u.Weekly), 56f, State(u.Weekly))],
                    },
                    new TextEl(u.Error is { } e && u.Session is null ? e : $"{Num(u.Session)} · {Num(u.Weekly)}")
                    { Size = 12f, Color = Tok.TextSecondary, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, Shrink = 1f, MinWidth = 0f },
                ],
            });
        }
        items.Add(new BoxEl { Grow = 1f });
        items.Add(c.Buttons with { Margin = new Edges4(0, 0, 8, 0) });
        return new BoxEl { Direction = 0, Height = 48, AlignItems = FlexAlign.Center, Children = [.. items] };
    }

    // ── D · Detailed (with best pick) ───────────────────────────────────────────────────────────────────────────────

    public static BoxEl Detailed(IReadOnlyList<AccountUsage> usage, Chrome c)
    {
        AccountUsage? best = BestPick(usage);

        var children = new List<Element>(usage.Count + 2)
        {
            Card(new Edges4(14, 10, 14, 10), 2,
                new TextEl("Best pick now") { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary },
                new TextEl(best is null ? "Waiting for data…" : "claude-" + best.Name) { Size = 16f, Weight = 600, LineHeight = 22f, Color = Tok.TextPrimary },
                new TextEl(best is null ? " " : $"{100 - best.Session!.Percent:0}% of the 5h window and {100 - (best.Weekly?.Percent ?? 0):0}% of the week left")
                { Size = 12f, Color = Tok.TextSecondary }),
        };
        foreach (var u in usage)
        {
            children.Add(Card(new Edges4(14, 10, 14, 12), 6,
                AccountLine(u, 14f),
                Bar("Session", u.Session, 112f, 52f, c.Now),
                Bar("Weekly", u.Weekly, 112f, 52f, c.Now)));
        }
        DateTimeOffset fetched = usage.Count > 0 ? usage[0].FetchedAt : c.Now;
        children.Add(new TextEl(c.Fetching ? "Updating…" : $"Updated {Ago(c.Now - fetched)} · every {c.PollMinutes} min")
            { Size = 12f, Color = Tok.TextTertiary, Margin = new Edges4(2, 0, 0, 0) });
        return Page(Header("Claude Code usage", c),
            new BoxEl { Direction = 1, Gap = 8, Padding = new Edges4(12, 0, 12, 12), Children = [.. children] });
    }

    // ── shared parts ────────────────────────────────────────────────────────────────────────────────────────────────

    static BoxEl Page(BoxEl header, BoxEl body) => new() { Direction = 1, Children = [header, body] };

    /// <summary>The drag band: title + the client button island (refresh, more).</summary>
    static BoxEl Header(string title, Chrome c) => new BoxEl
    {
        Direction = 0, Height = HeaderHeight, AlignItems = FlexAlign.Center, Padding = new Edges4(14, 0, 6, 0),
        Children =
        [
            new TextEl(title) { Size = 12f, Color = Tok.TextSecondary, Grow = 1f },
            c.Fetching ? ProgressRing.Create(null, 16f) : new BoxEl { Width = 16 },
            c.Buttons,
        ],
    }.WindowDragArea();

    static BoxEl Card(Edges4 padding, float gap, params Element[] children) => new()
    {
        Direction = 1, Gap = gap, Padding = padding, Corners = Radii.OverlayAll,
        Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Children = children,
    };

    /// <summary>"work  MAX" — the alias name, its plan as a WinUI-style tag, and the account's error if it has one.</summary>
    static BoxEl AccountLine(AccountUsage u, float size, bool center = false)
    {
        var parts = new List<Element>(3)
        {
            new TextEl(u.Name) { Size = size, Weight = 600, Color = Tok.TextPrimary },
        };
        if (u.Plan.Length > 0)
        {
            parts.Add(new BoxEl
            {
                Padding = new Edges4(6, 1, 6, 1), Corners = Radii.ControlAll, Fill = Tok.FillControlSecondary,
                BorderColor = Tok.StrokeControlDefault, BorderWidth = 1f,
                Children = [new TextEl(u.Plan.ToUpperInvariant()) { Size = 10f, Weight = 600, Color = Tok.TextSecondary }],
            });
        }
        if (u.Error is { } err && !center)
        {
            parts.Add(new BoxEl { Grow = 1f });
            parts.Add(new TextEl(err) { Size = 12f, Color = u.Session is null ? Tok.TextSecondary : Tok.TextTertiary });
        }
        return new BoxEl
        {
            Direction = 0, Gap = 8, AlignItems = FlexAlign.Center, Justify = center ? FlexJustify.Center : FlexJustify.Start,
            Children = [.. parts],
        };
    }

    /// <summary>label · bar · percent · reset — one limit window.</summary>
    static BoxEl Bar(string label, LimitWindow? w, float barWidth, float labelWidth, DateTimeOffset now) => new()
    {
        Direction = 0, Gap = 8, AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(label) { Size = 12f, Color = Tok.TextSecondary, Width = labelWidth },
            ProgressBar.Determinate(Frac(w), barWidth, State(w)),
            new TextEl(Pct(w)) { Size = 12f, Weight = 600, Color = Tok.TextPrimary, Width = 36, TextAlign = TextAlign.End, FontFeatures = FontFeatures.TabularFigures },
            new TextEl(Countdown.Reset(w?.ResetsAt, now)) { Size = 12f, Color = Tok.TextSecondary, Width = 64, TextAlign = TextAlign.End, FontFeatures = FontFeatures.TabularFigures },
        ],
    };

    static BoxEl Centered(float size, Element child) => new()
    {
        Direction = 0, Width = size, Height = size, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [child],
    };

    static string WindowLine(string label, LimitWindow? w, DateTimeOffset now)
        => Countdown.Reset(w?.ResetsAt, now) is { Length: > 0 } reset ? $"{label} {Pct(w)} · {reset}" : $"{label} {Pct(w)}";

    static float Frac(LimitWindow? w) => (float)Math.Clamp((w?.Percent ?? 0) / 100.0, 0, 1);
    static string Pct(LimitWindow? w) => w is null ? "–" : $"{w.Percent:0}%";
    static string Num(LimitWindow? w) => w is null ? "–" : $"{w.Percent:0}";

    // ok / getting close / nearly out — the ProgressBar's own WinUI states (accent / caution / critical).
    static ProgressBarState State(LimitWindow? w) => (w?.Percent ?? 0) switch
    {
        >= 90 => ProgressBarState.Error,
        >= 75 => ProgressBarState.Paused,
        _ => ProgressBarState.Normal,
    };

    static ColorF Severity(LimitWindow? w) => State(w) switch
    {
        ProgressBarState.Error => Tok.SystemFillCritical,
        ProgressBarState.Paused => Tok.SystemFillCaution,
        _ => Tok.AccentDefault,
    };

    static string Ago(TimeSpan t) => t.TotalMinutes < 1 ? "just now" : $"{Countdown.Format(t)} ago";
}
