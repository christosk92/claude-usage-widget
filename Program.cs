using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ClaudeUsageWidget;
using ClaudeUsageWidget.Usage;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.WindowsApi.Shell;
using FluentGpu.WindowsApi.Storage;

// One widget per user session: two would each poll every account, and the usage endpoint answers a second call within
// seconds with 429. A second launch just asks the running one to show itself.
using var instance = new Mutex(true, @"Local\ClaudeUsageWidget", out bool first);
using var showRequests = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClaudeUsageWidget.Show");
if (!first)
{
    showRequests.Set();
    return;
}
Widget.ShowRequests = showRequests;

FluentApp.Run(() => new OverlayHost { Child = Embed.Comp(() => new Widget()) }, new AppOptions
{
    Title = "Claude usage",
    Width = 320,
    Height = 240,
    SizeToContent = SizeToContent.WidthAndHeight,   // each layout sizes the window; shown after the first fit
    Mica = true,
    Acrylic = Settings.Acrylic,
    CustomFrame = true,
    Topmost = Settings.Topmost,
    ToolWindow = true,
    Resizable = false,
});

namespace ClaudeUsageWidget
{
    public enum WidgetLayout { Compact = 0, Rings = 1, Strip = 2, Detailed = 3 }

    /// <summary>The user's picks, persisted under HKCU\Software\ClaudeUsageWidget\Widget\Settings.</summary>
    public static class Settings
    {
        static readonly AppDataStore Store = AppDataStore.ForUnpackaged("ClaudeUsageWidget", "Widget");

        public static WidgetLayout Layout
        {
            get => (WidgetLayout)Math.Clamp(Store.GetInt("layout", (int)WidgetLayout.Compact), 0, 3);
            set => Store.SetInt("layout", (int)value);
        }

        public static bool Topmost { get => Store.GetBool("topmost", true); set => Store.SetBool("topmost", value); }
        public static bool Acrylic { get => Store.GetBool("acrylic", false); set => Store.SetBool("acrylic", value); }
        public static int PollMinutes { get => Math.Clamp(Store.GetInt("pollMinutes", 5), 1, 60); set => Store.SetInt("pollMinutes", value); }

        /// <summary>The outer window origin (physical px) the user last left it at; null before the first move.</summary>
        public static (int X, int Y)? Position
        {
            get => Store.Contains("x") && Store.Contains("y") ? (Store.GetInt("x"), Store.GetInt("y")) : null;
            set
            {
                if (value is not { } p) { Store.Remove("x"); Store.Remove("y"); return; }
                Store.SetInt("x", p.X); Store.SetInt("y", p.Y);
            }
        }
    }

    /// <summary>
    /// The overlay: polls every account (a polling <c>UseResource</c>), renders the picked layout, and owns the window
    /// extras — backdrop, topmost, the remembered position and the tray icon whose badge shows the best account's headroom.
    /// Dragging, sizing and the menus are declarative (<c>WindowDragArea</c>, <c>SizeToContent</c>, <c>IconButton.Menu</c>).
    /// </summary>
    sealed partial class Widget : Component
    {
        // The tray glyph (Segoe Fluent "LightningBolt") and the badge state the icon source reads on every re-render.
        const string TrayGlyph = "";
        readonly UsageClient _client = new();
        /// <summary>Signalled by a second launch of the widget (see the top of this file); null when not single-instance.</summary>
        public static EventWaitHandle? ShowRequests;
        readonly ResourceOptions _poll = new()
        {
            RefetchIntervalMs = Settings.PollMinutes * 60_000f,
            RefetchWhileInactive = true,   // keep the tray badge live while the widget is hidden
            KeepPreviousData = true,
        };
        // Re-discovered after the accounts page changes something; the version re-keys the usage resource.
        IReadOnlyList<Account> _accounts = AccountStore.Discover();
        int _accountsVersion;
        NotifyIcon? _tray;
        string? _badge;
        ColorF _badgeFill;

        public override Element Render()
        {
            var svc = UseContext(Overlay.Service);
            var accounts = _accounts;
            var (accountsVersion, setAccountsVersion) = UseState(0);
            var usage = UseResource(ct => _client.FetchAllAsync(accounts, ct), Loading(accounts), DepKey.From(accountsVersion), _poll);
            var managing = UseSignal(false);
            // Whose errors the errors page shows ("" = all accounts); null = the page is closed.
            var errorsFor = UseSignal<string?>(null);
            var layout = UseSignal(Settings.Layout);
            var topmost = UseSignal(Settings.Topmost);
            var acrylic = UseSignal(Settings.Acrylic);
            var (now, setNow) = UseState(DateTimeOffset.Now);
            var post = UseContext(HostDispatch.Post);

            UseEffect(() =>
            {
                RestorePosition();
                FluentApp.CloseRequested = _ => { SavePosition(); return false; };
                _tray = CreateTray(usage.Refresh);
                // A second launch signals this one: show the widget (it may be hidden to the tray).
                var showWait = ShowRequests is { } ev
                    ? ThreadPool.RegisterWaitForSingleObject(ev, (_, _) => post?.Invoke(() => FluentApp.SetWindowVisible(true)), null, -1, executeOnlyOnce: false)
                    : null;
                return () => { showWait?.Unregister(null); _tray?.Dispose(); _tray = null; };
            }, DepKey.Empty);
            UseInterval(() => { setNow(DateTimeOffset.Now); SavePosition(); }, 30_000f);

            IReadOnlyList<AccountUsage> data = usage.Loadable.Value.Value;
            bool fetching = usage.IsFetching.Value;

            // Tray badge = the best pick's headroom (percent left), coloured ok / getting close / nearly out.
            UseEffect(() => UpdateTray(usage.Loadable.Value.Value));

            WidgetLayout current = layout.Value;

            IReadOnlyList<MenuFlyoutItem> MenuItems() =>
            [
                new MenuFlyoutItem("Refresh now", Icons.Refresh, Invoke: usage.Refresh),
                new MenuFlyoutItem("Manage accounts…", Icons.Contact, Invoke: () => managing.Value = true),
                new MenuFlyoutItem("Errors…", Icons.Important, Invoke: () => errorsFor.Value = ""),
                MenuFlyoutItem.SubMenu("Layout",
                [
                    MenuFlyoutItem.RadioItem("Compact", current == WidgetLayout.Compact, () => Pick(WidgetLayout.Compact)),
                    MenuFlyoutItem.RadioItem("Rings", current == WidgetLayout.Rings, () => Pick(WidgetLayout.Rings)),
                    MenuFlyoutItem.RadioItem("Strip", current == WidgetLayout.Strip, () => Pick(WidgetLayout.Strip)),
                    MenuFlyoutItem.RadioItem("Detailed", current == WidgetLayout.Detailed, () => Pick(WidgetLayout.Detailed)),
                ], Icons.ViewGrid),
                MenuFlyoutItem.Separator,
                MenuFlyoutItem.Toggle("Always on top", topmost.Peek(), () =>
                {
                    bool on = !topmost.Peek();
                    topmost.Value = on; Settings.Topmost = on; FluentApp.SetTopmost(on);
                }, Icons.Pin),
                MenuFlyoutItem.Toggle("Acrylic backdrop", acrylic.Peek(), () =>
                {
                    bool on = !acrylic.Peek();
                    acrylic.Value = on; Settings.Acrylic = on; FluentApp.SetWindowMaterialAcrylic(on);
                }),
                MenuFlyoutItem.Separator,
                new MenuFlyoutItem("Hide to tray", Invoke: () => FluentApp.SetWindowVisible(false)),
                new MenuFlyoutItem("Exit", Icons.ChromeClose, Invoke: Exit),
            ];

            void Pick(WidgetLayout l) { layout.Value = l; Settings.Layout = l; }

            void ReloadAccounts()
            {
                _accounts = AccountStore.Discover();
                setAccountsVersion(++_accountsVersion);
            }

            if (managing.Value)
                return Embed.Comp(new AccountsProps(accounts, data, ReloadAccounts, () => managing.Value = false), () => new AccountsPage());
            if (errorsFor.Value is { } who)
                return Embed.Comp(new ErrorsProps(who.Length == 0 ? null : who, data, () => errorsFor.Value = null), () => new ErrorsPage());

            var chrome = new Chrome(
                Buttons: new BoxEl
                {
                    Direction = 0, Gap = 2, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        IconButton.Create(Icons.Refresh, usage.Refresh, size: ControlSize.Small),
                        IconButton.Menu(Icons.More, MenuItems, toolTip: "More", size: ControlSize.Small),
                    ],
                }.NoWindowDrag(),
                Fetching: fetching,
                Now: now,
                PollMinutes: Settings.PollMinutes,
                ShowErrors: name => errorsFor.Value = name);

            BoxEl body = current switch
            {
                WidgetLayout.Rings => Layouts.Rings(data, chrome),
                WidgetLayout.Strip => Layouts.Strip(data, chrome),
                WidgetLayout.Detailed => Layouts.Detailed(data, chrome),
                _ => Layouts.Compact(data, chrome),
            };
            return body.WithContextMenu(svc, () => new ContextMenuModel(MenuItems()));
        }

        NotifyIcon? CreateTray(Action refresh)
        {
            NotifyIcon tray;
            try { tray = new NotifyIcon(Guid.Empty, "Claude Code usage"); }
            catch (Exception) { return null; }   // no notification area this session: the widget still works
            tray.SetIconSource(r => TrayIconArt.Glyph(r, TrayGlyph, badge: _badge, badgeFill: _badge is null ? null : _badgeFill));
            tray.Activated += (e, x, y) =>
            {
                if (e is NotifyIconEvent.Select or NotifyIconEvent.KeySelect) FluentApp.SetWindowVisible(!FluentApp.WindowVisible);
                else if (e == NotifyIconEvent.ContextMenu) TrayMenu(tray, x, y, refresh);
            };
            tray.Show();
            return tray;
        }

        static void TrayMenu(NotifyIcon tray, int x, int y, Action refresh)
        {
            bool shown = FluentApp.WindowVisible;
            ReadOnlySpan<NotifyMenuItem> items =
            [
                new(1, shown ? "Hide widget" : "Show widget", true, false, true),
                new(2, "Refresh now", true, false, false),
                NotifyMenuItem.Separator,
                new(3, "Exit", true, false, false),
            ];
            switch (tray.ShowMenu(items, x, y))
            {
                case 1: FluentApp.SetWindowVisible(!shown); break;
                case 2: refresh(); break;
                case 3: Exit(); break;
            }
        }

        void UpdateTray(IReadOnlyList<AccountUsage> data)
        {
            if (_tray is not { } tray) return;
            AccountUsage? best = Layouts.BestPick(data);
            string? badge = best is null ? null : $"{Math.Max(0, best.Headroom):0}";
            _badgeFill = best is null ? default : Layouts.HeadroomColor(best.Headroom);
            tray.SetTip(best is null ? "Claude Code usage" : $"Best pick: claude-{best.Name} ({badge}% left)");
            if (badge == _badge) return;
            _badge = badge;
            tray.RefreshIconSource();
        }

        static void Exit() { SavePosition(); FluentApp.CloseWindow(); }

        static IReadOnlyList<AccountUsage> Loading(IReadOnlyList<Account> accounts)
        {
            var list = new AccountUsage[accounts.Count];
            for (int i = 0; i < list.Length; i++) list[i] = new AccountUsage(accounts[i].Name, "", null, null, "Loading…", DateTimeOffset.Now);
            return list;
        }

        static void SavePosition()
        {
            RectF b = FluentApp.WindowBoundsPx;
            if (b.W <= 0 || !FluentApp.WindowVisible) return;
            var p = ((int)b.X, (int)b.Y);
            if (Settings.Position != p) Settings.Position = p;
        }

        static void RestorePosition()
        {
            // Only onto a monitor that still exists: a remembered spot on an unplugged screen would hide the widget.
            if (Settings.Position is { } p && MonitorFromPoint(new POINT { X = p.X + 40, Y = p.Y + 20 }, MonitorDefaultToNull) != 0)
                FluentApp.MoveWindowPx(p.X, p.Y);
        }

        const uint MonitorDefaultToNull = 0;
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [LibraryImport("user32.dll")] private static partial nint MonitorFromPoint(POINT pt, uint flags);

        // ── Layouts ─────────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>What every layout needs from the widget: the header buttons, the clock, and opening an account's errors.</summary>
        sealed record Chrome(BoxEl Buttons, bool Fetching, DateTimeOffset Now, int PollMinutes, Action<string> ShowErrors);

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
                            AccountLine(u, 14f, c.ShowErrors),
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
                            AccountLine(u, 13f, c.ShowErrors, center: true),
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
                            u.Error is { } e && u.Session is null
                                ? ErrorText(u, e, c.ShowErrors) with { Shrink = 1f, MinWidth = 0f }
                                : new TextEl($"{Num(u.Session)} · {Num(u.Weekly)}")
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
                        AccountLine(u, 14f, c.ShowErrors),
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

            static BoxEl Header(string title, Chrome c) => Header(title, c.Buttons, c.Fetching);

            /// <summary>The drag band: an optional leading button (back), title + the client button island (refresh, more).</summary>
            public static BoxEl Header(string title, Element buttons, bool fetching = false, Element? leading = null) => new BoxEl
            {
                Direction = 0, Height = HeaderHeight, AlignItems = FlexAlign.Center, Padding = new Edges4(leading is null ? 14 : 6, 0, 6, 0),
                Gap = leading is null ? 0 : 4,
                Children = leading is null
                    ? [new TextEl(title) { Size = 12f, Color = Tok.TextSecondary, Grow = 1f }, fetching ? ProgressRing.Create(null, 16f) : new BoxEl { Width = 16 }, buttons]
                    : [leading, new TextEl(title) { Size = 12f, Color = Tok.TextSecondary, Grow = 1f }, buttons],
            }.WindowDragArea();

            public static BoxEl Card(Edges4 padding, float gap, params Element[] children) => new()
            {
                Direction = 1, Gap = gap, Padding = padding, Corners = Radii.OverlayAll,
                Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Children = children,
            };

            /// <summary>"ohsnap  MAX" — the alias name, its plan as a WinUI-style tag, and the account's error if it has one.</summary>
            static BoxEl AccountLine(AccountUsage u, float size, Action<string> showErrors, bool center = false)
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
                    parts.Add(ErrorText(u, err, showErrors));
                }
                return new BoxEl
                {
                    Direction = 0, Gap = 8, AlignItems = FlexAlign.Center, Justify = center ? FlexJustify.Center : FlexJustify.Start,
                    Children = [.. parts],
                };
            }

            /// <summary>
            /// An account's error label. A logged failure (rate limited, HTTP, offline …) is critical-red and opens the errors
            /// page on click; a plain state ("Loading…", "Not signed in") stays quiet text.
            /// </summary>
            static BoxEl ErrorText(AccountUsage u, string err, Action<string> showErrors)
            {
                var text = new TextEl(err) { Size = 12f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
                if (u.Failure is null) return new BoxEl { MinWidth = 0f, Children = [text with { Color = Tok.TextSecondary }] };
                return new BoxEl
                {
                    Direction = 0, Gap = 4, AlignItems = FlexAlign.Center, MinWidth = 0f, Padding = new Edges4(4, 1, 4, 1), Margin = new Edges4(-4, 0, -4, 0),
                    Corners = Radii.ControlAll, Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                    Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = () => showErrors(u.Name),
                    Children = [Ui.Icon(Icons.Important) with { Size = 12f, Color = Tok.SystemFillCritical }, text with { Color = Tok.SystemFillCritical, Shrink = 1f }],
                }.NoWindowDrag();
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

        // ── Accounts page ───────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>What the widget hands the accounts page: the accounts, their last usage (for the plan tag), and callbacks.</summary>
        sealed record AccountsProps(IReadOnlyList<Account> Accounts, IReadOnlyList<AccountUsage> Usage, Action Changed, Action Back);

        /// <summary>
        /// "Manage accounts": add, rename and delete <c>claude-&lt;name&gt;</c> aliases (the profile function and its
        /// <c>~/.claude-&lt;name&gt;</c> folder together), and open a terminal on an account to sign it in. Rename and delete
        /// happen inline on the account's card; failures (a session still running, a name taken) show under the list.
        /// </summary>
        sealed class AccountsPage : Component
        {
            const float PageWidth = 380f;

            public override Element Render()
            {
                var p = UseProps<AccountsProps>();
                var (editing, setEditing) = UseState<string?>(null);
                var (confirming, setConfirming) = UseState<string?>(null);
                var (error, setError) = UseState<string?>(null);
                var renameText = UseSignal("");
                var newName = UseSignal("");

                void Run(Action op)
                {
                    try { op(); setError(null); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
                    {
                        setError(ex.Message);
                    }
                    p.Changed();
                }

                void Add()
                {
                    string name = newName.Peek().Trim();
                    if (AccountStore.Validate(name) is { } why) { setError(why); return; }
                    Run(() => AccountStore.Create(name));
                    newName.Value = "";
                }

                void Rename(Account a)
                {
                    string name = renameText.Peek().Trim();
                    if (AccountStore.Validate(name, renaming: a.Name) is { } why) { setError(why); return; }
                    setEditing(null);
                    Run(() => AccountStore.Rename(a, name));
                }

                void Delete(Account a)
                {
                    setConfirming(null);
                    Run(() => AccountStore.Delete(a));
                }

                var children = new List<Element>(p.Accounts.Count + 3);
                foreach (var a in p.Accounts)
                {
                    if (editing == a.Name) children.Add(RenameCard(a, renameText, () => Rename(a), () => { setEditing(null); setError(null); }));
                    else if (confirming == a.Name) children.Add(DeleteCard(a, () => Delete(a), () => setConfirming(null)));
                    else children.Add(AccountCard(a, Plan(p.Usage, a.Name),
                        open: () => Run(() => AccountStore.OpenTerminal(a)),
                        rename: () => { renameText.Value = a.Name; setConfirming(null); setError(null); setEditing(a.Name); },
                        delete: () => { setEditing(null); setError(null); setConfirming(a.Name); }));
                }
                if (p.Accounts.Count == 0)
                    children.Add(new TextEl("No accounts yet. Add one below.") { Size = 12f, Color = Tok.TextSecondary });

                children.Add(Layouts.Card(new Edges4(14, 10, 14, 12), 8,
                    new TextEl("Add account") { Size = 12f, Weight = 600, Color = Tok.TextPrimary },
                    NameRow(newName, "name", Add, () => newName.Value = "", Button.Accent("Add", Add))));

                if (error is not null)
                    children.Add(new TextEl(error) { Size = 12f, Color = Tok.SystemFillCritical, Wrap = TextWrap.Wrap, MaxWidth = PageWidth - 24 });
                children.Add(new TextEl("Aliases live in your PowerShell profile — open a new terminal to use a new or renamed one.")
                    { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxWidth = PageWidth - 24, Margin = new Edges4(2, 0, 0, 0) });

                return new BoxEl
                {
                    Direction = 1, Width = PageWidth,
                    Children =
                    [
                        Layouts.Header("Accounts", leading: IconButton.Create(Icons.Back, p.Back, size: ControlSize.Small).NoWindowDrag(),
                            buttons: IconButton.Create(Icons.Refresh, p.Changed, size: ControlSize.Small).NoWindowDrag()),
                        new BoxEl { Direction = 1, Gap = 8, Padding = new Edges4(12, 0, 12, 12), Children = [.. children] },
                    ],
                };
            }

            static string Plan(IReadOnlyList<AccountUsage> usage, string name)
            {
                foreach (var u in usage) if (u.Name == name) return u.Plan;
                return "";
            }

            static BoxEl AccountCard(Account a, string plan, Action open, Action rename, Action delete)
            {
                int running = AccountStore.RunningSessions(a);
                var status = new List<string>(3)
                {
                    a.SignedIn ? (plan.Length > 0 ? $"Signed in · {plan.ToUpperInvariant()}" : "Signed in") : "Not signed in",
                };
                if (running > 0) status.Add(running == 1 ? "1 session running" : $"{running} sessions running");
                if (!a.HasAlias) status.Add("no alias");

                return Layouts.Card(new Edges4(14, 8, 8, 8), 0, new BoxEl
                {
                    Direction = 0, Gap = 4, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Gap = 2, Grow = 1f, MinWidth = 0f,
                            Children =
                            [
                                new TextEl("claude-" + a.Name) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                                new TextEl(string.Join(" · ", status)) { Size = 12f, Color = a.SignedIn ? Tok.TextSecondary : Tok.SystemFillCaution, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            ],
                        },
                        a.SignedIn
                            ? IconButton.Create(Icons.OpenInNewWindow, open, size: ControlSize.Small)
                            : Button.Create("Sign in", open, ButtonAppearance.Accent, ControlSize.Small),
                        IconButton.Create(Icons.Edit, rename, size: ControlSize.Small),
                        IconButton.Create(Icons.Delete, delete, size: ControlSize.Small),
                    ],
                });
            }

            static BoxEl RenameCard(Account a, FluentGpu.Signals.Signal<string> text, Action save, Action cancel)
                => Layouts.Card(new Edges4(14, 10, 14, 12), 8,
                    new TextEl($"Rename claude-{a.Name}") { Size = 12f, Weight = 600, Color = Tok.TextPrimary },
                    NameRow(text, a.Name, save, cancel, Button.Accent("Save", save)),
                    new BoxEl
                    {
                        Direction = 0, Justify = FlexJustify.End,
                        Children = [Button.Subtle("Cancel", cancel)],
                    });

            static BoxEl DeleteCard(Account a, Action delete, Action cancel)
                => Layouts.Card(new Edges4(14, 10, 14, 12), 8,
                    new TextEl($"Delete claude-{a.Name}?") { Size = 14f, Weight = 600, Color = Tok.TextPrimary },
                    new TextEl(a.HasFolder
                            ? "The alias is removed and its folder (sign-in, history, settings) goes to the Recycle Bin."
                            : "The alias is removed from your PowerShell profile.")
                        { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = PageWidth - 54 },
                    new BoxEl
                    {
                        Direction = 0, Gap = 8, Justify = FlexJustify.End,
                        Children = [Button.Subtle("Cancel", cancel), Button.Accent("Delete", delete)],
                    });

            /// <summary>"claude-" · name field · action button. Enter commits, Escape cancels.</summary>
            static BoxEl NameRow(FluentGpu.Signals.Signal<string> text, string placeholder, Action commit, Action cancel, Element action) => new()
            {
                Direction = 0, Gap = 8, AlignItems = FlexAlign.Center,
                Children =
                [
                    new TextEl("claude-") { Size = 14f, Color = Tok.TextSecondary },
                    TextBox.Create(text, options: new TextBox.TextBoxOptions
                    {
                        Placeholder = placeholder, Width = 180f, MaxLength = 32,
                        OnCommit = _ => commit(), OnCancel = cancel,
                    }),
                    action,
                ],
            };
        }

        // ── Errors page ─────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>What the widget hands the errors page: whose errors to show first, every account's state, and back.</summary>
        sealed record ErrorsProps(string? Account, IReadOnlyList<AccountUsage> Usage, Action Back);

        /// <summary>
        /// The failures behind an account's error label: each fetch's request, status, rate-limit headers, body and stack trace,
        /// newest first. "Copy all" puts one self-contained Markdown report on the clipboard (every account's state plus every
        /// logged failure) — the thing to paste into an LLM or an issue. No tokens are ever in it.
        /// </summary>
        sealed class ErrorsPage : Component
        {
            const float PageWidth = 460f;
            const float ListHeight = 380f;

            public override Element Render()
            {
                var p = UseProps<ErrorsProps>();
                var hooks = UseContext(InputHooks.Current);
                var (all, setAll) = UseState(p.Account is null);
                var (copied, setCopied) = UseState<string?>(null);

                var entries = ErrorLog.For(all ? null : p.Account);

                void Copy(string key, string text) { hooks.Clipboard?.SetText(text); setCopied(key); }

                var cards = new List<Element>(entries.Count + 1);
                foreach (var e in entries)
                {
                    string key = $"{e.Account}@{e.At.UtcTicks}";
                    cards.Add(Layouts.Card(new Edges4(12, 8, 8, 10), 6,
                        new BoxEl
                        {
                            Direction = 0, Gap = 8, AlignItems = FlexAlign.Center,
                            Children =
                            [
                                new TextEl(e.Account) { Size = 12f, Weight = 600, Color = Tok.TextPrimary },
                                new TextEl(e.Summary) { Size = 12f, Color = Tok.SystemFillCritical, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                                new TextEl(e.At.ToString("HH:mm:ss")) { Size = 12f, Color = Tok.TextTertiary, FontFeatures = FontFeatures.TabularFigures },
                                CopyButton(copied == key, () => Copy(key, EntryMarkdown(e))),
                            ],
                        },
                        new TextEl(e.Detail)
                        {
                            Size = 11f, FontFamily = "Cascadia Mono", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap,
                            MaxWidth = PageWidth - 50, IsTextSelectionEnabled = true,
                        }));
                }
                if (entries.Count == 0)
                    cards.Add(new TextEl(all ? "No failures since the widget started." : $"No failures logged for {p.Account} since the widget started.")
                        { Size = 12f, Color = Tok.TextSecondary, Margin = new Edges4(2, 4, 0, 4) });

                var buttons = new BoxEl
                {
                    Direction = 0, Gap = 2, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Button.Create(copied == "*" ? "Copied" : "Copy all for LLM", () => Copy("*", Report(p.Usage)), ButtonAppearance.Subtle, ControlSize.Small),
                        IconButton.Create(Icons.Document, OpenLog, size: ControlSize.Small),
                    ],
                }.NoWindowDrag();

                var filter = p.Account is null ? null : new BoxEl
                {
                    Direction = 0, Gap = 4, Padding = new Edges4(12, 0, 12, 8),
                    Children =
                    [
                        Button.Create(p.Account, () => setAll(false), all ? ButtonAppearance.Subtle : ButtonAppearance.Accent, ControlSize.Small),
                        Button.Create("All accounts", () => setAll(true), all ? ButtonAppearance.Accent : ButtonAppearance.Subtle, ControlSize.Small),
                    ],
                };

                return new BoxEl
                {
                    Direction = 1, Width = PageWidth,
                    Children =
                    [
                        Layouts.Header("Errors", leading: IconButton.Create(Icons.Back, p.Back, size: ControlSize.Small).NoWindowDrag(), buttons: buttons),
                        .. filter is null ? Array.Empty<Element>() : [filter],
                        new BoxEl
                        {
                            Height = ListHeight, Padding = new Edges4(12, 0, 12, 12),
                            Children = [Ui.ScrollView(new BoxEl { Direction = 1, Gap = 8, Children = [.. cards] })],
                        },
                    ],
                };
            }

            static BoxEl CopyButton(bool copied, Action copy)
                => IconButton.Create(copied ? Icons.Accept : Icons.Copy, copy, size: ControlSize.Small);

            static void OpenLog()
            {
                if (!File.Exists(ErrorLog.FilePath)) return;
                Process.Start(new ProcessStartInfo(ErrorLog.FilePath) { UseShellExecute = true })?.Dispose();
            }

            static string EntryMarkdown(ErrorEntry e)
                => $"### {e.Account}: {e.Summary} ({e.At:yyyy-MM-dd HH:mm:ss zzz})\n\n```text\n{e.Detail.TrimEnd()}\n```\n";

            /// <summary>Everything an LLM needs to diagnose the widget's state in one paste: what the tool does, each account's
            /// windows and error, and every logged failure with its HTTP exchange and stack trace.</summary>
            static string Report(IReadOnlyList<AccountUsage> usage)
            {
                var now = DateTimeOffset.Now;
                var sb = new StringBuilder();
                sb.AppendLine("# Claude usage widget: error report");
                sb.AppendLine();
                sb.AppendLine("A Windows desktop widget that shows Claude Code rate-limit usage for several accounts. Per account it reads the");
                sb.AppendLine("OAuth credentials in `~/.claude-<name>/.credentials.json`, refreshes an expired access token via");
                sb.AppendLine("`POST https://platform.claude.com/v1/oauth/token` (fallback `console.anthropic.com`), then calls");
                sb.AppendLine("`GET https://api.anthropic.com/api/oauth/usage` (header `anthropic-beta: oauth-2025-04-20`), the endpoint behind the");
                sb.AppendLine($"CLI's `/usage`. It polls every {Settings.PollMinutes} min; a failure backs off 1, 2, 4 … 30 min (at least 2 min on 429,");
                sb.AppendLine("at least the server's Retry-After) and keeps showing the last good numbers. It skips the call when it already");
                sb.AppendLine("has numbers under 2 min old: its own (saved across restarts) or Claude Code's cached copy in");
                sb.AppendLine("`~/.claude-<name>/.claude.json` (`cachedUsageUtilization`, stamped `fetchedAtMs`).");
                sb.AppendLine();
                sb.AppendLine($"- Generated: {now:yyyy-MM-dd HH:mm:ss zzz}");
                sb.AppendLine($"- OS: {Environment.OSVersion.VersionString}, .NET {Environment.Version}");
                sb.AppendLine($"- Logs: `{ErrorLog.FilePath}`, `{ErrorLog.RequestsPath}`");
                sb.AppendLine();
                sb.AppendLine("## Accounts");
                sb.AppendLine();
                sb.AppendLine("| Account | Plan | 5h window | 7d window | Status | Numbers from | Fetched |");
                sb.AppendLine("|---|---|---|---|---|---|---|");
                foreach (var u in usage)
                    sb.AppendLine($"| {u.Name} | {(u.Plan.Length > 0 ? u.Plan : "–")} | {Window(u.Session, now)} | {Window(u.Weekly, now)} | {u.Error ?? "OK"} | {(u.Session is null ? "–" : u.Source)} | {(u.Session is null ? "never" : u.FetchedAt.ToString("HH:mm:ss"))} |");
                sb.AppendLine();
                var entries = ErrorLog.For(null);
                sb.AppendLine($"## Failures ({entries.Count}, newest first)");
                sb.AppendLine();
                if (entries.Count == 0) sb.AppendLine("None since the widget started.");
                foreach (var e in entries) sb.AppendLine(EntryMarkdown(e));
                var requests = ErrorLog.RecentRequests(40);
                sb.AppendLine($"## HTTP calls (last {requests.Length}, oldest first)");
                sb.AppendLine();
                sb.AppendLine("```text");
                foreach (string line in requests) sb.AppendLine(line);
                sb.AppendLine("```");
                return sb.ToString();
            }

            static string Window(LimitWindow? w, DateTimeOffset now)
                => w is null ? "–" : Countdown.Reset(w.ResetsAt, now) is { Length: > 0 } reset ? $"{w.Percent:0}% (resets {reset})" : $"{w.Percent:0}%";
        }
    }
}
