using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ClaudeUsageWidget;
using ClaudeUsageWidget.Usage;
using FluentGpu;
using FluentGpu.Animation;
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
Diag.Sink = ErrorLog.Engine;   // the engine's device-lost / adapter evidence, otherwise lost on a windowed app's stderr

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

        /// <summary>Accounts whose weekly bar is expanded into its model limits (Fable …).</summary>
        public static ImmutableHashSet<string> Expanded { get => GetNames("expanded"); set => SetNames("expanded", value); }

        /// <summary>Accounts collapsed to their one-line summary.</summary>
        public static ImmutableHashSet<string> Collapsed { get => GetNames("collapsed"); set => SetNames("collapsed", value); }

        static ImmutableHashSet<string> GetNames(string key) => [.. (Store.GetString(key) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)];
        static void SetNames(string key, ImmutableHashSet<string> names) => Store.SetString(key, string.Join(',', names.Order(StringComparer.Ordinal)));

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
            var expanded = UseSignal(Settings.Expanded);
            var collapsed = UseSignal(Settings.Collapsed);
            var (now, setNow) = UseState(DateTimeOffset.Now);
            var post = UseContext(HostDispatch.Post);

            UseEffect(() =>
            {
                RestorePosition();
                FluentApp.CloseRequested = _ => { SavePosition(); return false; };
                _tray = CreateTray(usage.Refresh);
                // A second launch signals this one: show the widget (it may be hidden to the tray).
                var showWait = ShowRequests is { } ev
                    ? ThreadPool.RegisterWaitForSingleObject(ev, (_, _) => post?.Invoke(Show), null, -1, executeOnlyOnce: false)
                    : null;
                return () => { showWait?.Unregister(null); _tray?.Dispose(); _tray = null; };
            }, DepKey.Empty);
            UseInterval(() => { setNow(DateTimeOffset.Now); SavePosition(); }, 30_000f);
            // An unplugged monitor can leave the window where no screen is; the engine doesn't move it back.
            UseInterval(EnsureOnScreen, 2_000f);

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
                .. CollapseAll(),
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

            // Only the layouts with a section per account collapse.
            IEnumerable<MenuFlyoutItem> CollapseAll()
            {
                if (current is not (WidgetLayout.Compact or WidgetLayout.Detailed)) yield break;
                yield return data.All(u => collapsed.Peek().Contains(u.Name))
                    ? new MenuFlyoutItem("Expand all accounts", Icons.ChevronDown, Invoke: () => SetCollapsed([]))
                    : new MenuFlyoutItem("Collapse all accounts", Icons.ChevronUp, Invoke: () => SetCollapsed([.. data.Select(u => u.Name)]));
            }

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
                ShowErrors: name => errorsFor.Value = name,
                Expanded: expanded.Value,
                ToggleExpanded: name => SetExpanded(Toggle(expanded.Peek(), name)),
                Collapsed: collapsed.Value,
                ToggleCollapsed: name => SetCollapsed(Toggle(collapsed.Peek(), name)));

            static ImmutableHashSet<string> Toggle(ImmutableHashSet<string> set, string name) => set.Contains(name) ? set.Remove(name) : set.Add(name);
            void SetExpanded(ImmutableHashSet<string> set) { expanded.Value = set; Settings.Expanded = set; }
            void SetCollapsed(ImmutableHashSet<string> set) { collapsed.Value = set; Settings.Collapsed = set; }

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
                if (e is NotifyIconEvent.Select or NotifyIconEvent.KeySelect) ToggleVisible();
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
                case 1: ToggleVisible(); break;
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

        /// <summary>Shown from the tray or by a second launch: also back on a screen, so showing it is always a way out.</summary>
        static void Show() { FluentApp.SetWindowVisible(true); EnsureOnScreen(); }

        static void ToggleVisible() { if (FluentApp.WindowVisible) FluentApp.SetWindowVisible(false); else Show(); }

        /// <summary>
        /// Puts the window back on a screen when its title band (the drag area, the same probe points as
        /// <see cref="RestorePosition"/>) is on none — the monitor it sat on was unplugged and Windows left it there:
        /// clamped into the nearest monitor's work area, so it lands on the edge closest to where it was.
        /// </summary>
        static void EnsureOnScreen()
        {
            RectF b = FluentApp.WindowBoundsPx;
            if (b.W <= 0 || !FluentApp.WindowVisible) return;
            int left = (int)b.X, top = (int)b.Y, right = (int)(b.X + b.W), bottom = (int)(b.Y + b.H);
            if (MonitorFromPoint(new POINT { X = left + 40, Y = top + 20 }, MonitorDefaultToNull) != 0
                || MonitorFromPoint(new POINT { X = right - 40, Y = top + 20 }, MonitorDefaultToNull) != 0)
                return;
            var rect = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            nint monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
            if (monitor == 0 || !GetMonitorInfoW(monitor, ref info)) return;
            RECT work = info.Work;
            FluentApp.MoveWindowPx(
                Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - (int)b.W)),
                Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - (int)b.H)));
            SavePosition();
        }

        const uint MonitorDefaultToNull = 0, MonitorDefaultToNearest = 2;
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
        [LibraryImport("user32.dll")] private static partial nint MonitorFromPoint(POINT pt, uint flags);
        [LibraryImport("user32.dll")] private static partial nint MonitorFromRect(ref RECT rect, uint flags);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);

        // ── Layouts ─────────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>What every layout needs from the widget: the header buttons, the clock, opening an account's errors, and
        /// which accounts show their model limits under the weekly bar.</summary>
        sealed record Chrome(BoxEl Buttons, bool Fetching, DateTimeOffset Now, int PollMinutes, Action<string> ShowErrors,
            ImmutableHashSet<string> Expanded, Action<string> ToggleExpanded, ImmutableHashSet<string> Collapsed, Action<string> ToggleCollapsed);

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
                foreach (var u in usage) if (u.HasData && (best is null || u.Headroom > best.Headroom)) best = u;
                return best;
            }

            /// <summary>One bar as the layouts draw it. The week carries the model limits (Fable): their slice of the week is
            /// stacked into its bar and clicking it expands them into rows of their own (<see cref="IsModel"/>).</summary>
            public sealed record Row(string Label, LimitWindow? Window, IReadOnlyList<ModelLimit> Models, bool IsModel = false)
            {
                public bool Expandable => Models.Count > 0;

                /// <summary>The models' combined use as percent of the week, or null when the endpoint gave no shares.</summary>
                public double? ModelShare
                {
                    get
                    {
                        double? sum = null;
                        foreach (var m in Models) if (m.OfWeek is { } of) sum = (sum ?? 0) + of;
                        return sum;
                    }
                }
            }

            /// <summary>
            /// The bars to show for an account: the 5h session (unless the account has data but no 5h window), the
            /// all-models week with its model limits stacked in, and — expanded — each model's weekly limit under its name.
            /// </summary>
            public static List<Row> Rows(AccountUsage u, string session, string weekly, bool expanded)
            {
                var rows = new List<Row>(4);
                if (u.Session is not null || !u.HasData) rows.Add(new Row(session, u.Session, []));
                var models = u.Models ?? [];
                rows.Add(new Row(weekly, u.Weekly, models));
                if (expanded)
                    foreach (var m in models) rows.Add(new Row(m.Name, m.Window, [], IsModel: true));
                return rows;
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
                    var lines = new List<Element> { AccountLine(u, 14f, c, collapsible: true) };
                    if (!c.Collapsed.Contains(u.Name)) lines.Add(BarsSection(u, c, "5h", "7d", 136f, 20f));
                    rows[i] = new BoxEl { Direction = 1, Children = [.. lines] };
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
                    // Outer ring the first window (5h, or the week when there's no 5h), inner ring the second.
                    var rows = Rows(u, "5h", "7d", c.Expanded.Contains(u.Name));
                    Row outer = rows[0];
                    Row? inner = rows.Count > 1 && !rows[1].IsModel ? rows[1] : null;
                    var lines = new List<Element>
                    {
                        new BoxEl
                        {
                            ZStack = true, Width = 96, Height = 96,
                            Children =
                            [
                                Ring(outer, 96f),
                                Centered(96, inner is null ? new BoxEl() : Ring(inner, 64f)),
                                Centered(96, new TextEl(Pct(outer.Window)) { Size = 14f, Weight = 600, Color = Tok.TextPrimary }),
                            ],
                        },
                        AccountLine(u, 13f, c, center: true),
                    };
                    foreach (var row in rows)
                    {
                        var text = new TextEl(WindowLine(row.Label, row.Window, c.Now)) { Size = 12f, Color = row.IsModel ? Tok.TextTertiary : Tok.TextSecondary };
                        lines.Add(row.Expandable ? Expander(u, row, text, c) : text);
                    }
                    cols[i] = new BoxEl { Direction = 1, Width = 128, Gap = 6, AlignItems = FlexAlign.Center, Children = [.. lines] };
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
                    var rows = Rows(u, "5h", "7d", c.Expanded.Contains(u.Name));
                    var bars = new Element[rows.Count];
                    var nums = new string[rows.Count];
                    for (int k = 0; k < rows.Count; k++)
                    {
                        bars[k] = StackedBar(rows[k], 56f);
                        nums[k] = Num(rows[k].Window);
                    }
                    var barColumn = new BoxEl { Direction = 1, Gap = 4, Children = bars };
                    if (i > 0) items.Add(new BoxEl { Width = 1, Height = 24, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(12, 0, 12, 0) });
                    items.Add(new BoxEl
                    {
                        Direction = 0, Gap = 8, AlignItems = FlexAlign.Center, Width = 176,
                        Children =
                        [
                            new TextEl(u.Name) { Size = 12f, Weight = 600, Color = Tok.TextPrimary, Width = 56, Trim = TextTrim.CharacterEllipsis, MaxLines = 1 },
                            rows.Find(r => r.Expandable) is { } week ? Expander(u, week, barColumn, c) : barColumn,
                            u.Error is { } e && !u.HasData
                                ? ErrorText(u, e, c.ShowErrors) with { Shrink = 1f, MinWidth = 0f }
                                : new TextEl(string.Join(" · ", nums))
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
                        new TextEl(best is null ? " "
                            : best.Session is { } s ? $"{100 - s.Percent:0}% of the 5h window and {100 - (best.Weekly?.Percent ?? 0):0}% of the week left"
                            : $"{100 - (best.Weekly?.Percent ?? 0):0}% of the week left")
                        { Size = 12f, Color = Tok.TextSecondary }),
                };
                foreach (var u in usage)
                {
                    var lines = new List<Element> { AccountLine(u, 14f, c, collapsible: true) };
                    if (!c.Collapsed.Contains(u.Name)) lines.Add(BarsSection(u, c, "Session", "Weekly", 96f, 52f));
                    children.Add(Card(new Edges4(14, 10, 14, 10), 0, [.. lines]));
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

            // ── disclosure motion (Wavee's Design.Reveal; fluent-gpu docs/plans/smooth-reveal-implementation.md) ─────────────

            /// <summary>Wavee's <c>Design.Reveal.Drawer</c>: a section that mounts to open and unmounts to close. Layout lands
            /// once, the presented height springs under <c>MotionTok.Reveal</c> (critically damped, the same both ways,
            /// interruptible from where it stands) and everything below — the next rows, the next accounts — rides it at
            /// paint time. A clip reveal, never a fade: nothing pops and nothing re-lays out per frame.</summary>
            static readonly LayoutTransition Unfold = new(TransitionChannels.Size, MotionTok.Reveal.ToDynamics(),
                Size: SizeMode.FlowReveal, Enter: new EnterExit(Active: true), Exit: new EnterExit(Active: true),
                Anchor: SizeAnchor.Leading, SuppressDescendantTransitions: true);

            /// <summary>The folded line's summary: in from 8 DIP to the right, out in place, on the reveal's spring.</summary>
            static readonly LayoutTransition FadeIn = new(TransitionChannels.Opacity, MotionTok.Reveal.ToDynamics(),
                Enter: new EnterExit(Dx: 8f, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true));

            /// <summary>An account's bars as one <see cref="Drawer"/> (Compact, Detailed): the 5h and week rows, then the model
            /// rows in a drawer of their own under the week.</summary>
            static BoxEl BarsSection(AccountUsage u, Chrome c, string session, string weekly, float barWidth, float labelWidth)
            {
                var rows = new List<Element>(3);
                foreach (var r in Rows(u, session, weekly, expanded: false)) rows.Add(Spaced(Bar(u, r, barWidth, labelWidth, c)));
                if (c.Expanded.Contains(u.Name) && u.Models is { Count: > 0 } models)
                    rows.Add(Drawer("models:" + u.Name, [.. models.Select(m => Spaced(Bar(u, new Row(m.Name, m.Window, [], IsModel: true), barWidth, labelWidth, c)))]));
                return Drawer("bars:" + u.Name, [.. rows]);
            }

            /// <summary>A row with its gap above it as its own padding, so a row that folds away takes its gap along.</summary>
            static BoxEl Spaced(Element row) => new() { Direction = 1, Padding = new Edges4(0, 6, 0, 0), Children = [row] };

            /// <summary>A mount-to-open section on <see cref="Unfold"/>. It clips while it moves, so it pads out by the rows'
            /// hover plates (<see cref="Clickable"/> overhangs 4 / 1 DIP) and takes the room back with a negative margin.</summary>
            static BoxEl Drawer(string key, Element[] children) => new()
            {
                Key = key, Direction = 1, ClipToBounds = true, Animate = Unfold,
                Padding = new Edges4(4, 0, 4, 1), Margin = new Edges4(-4, 0, -4, -1),
                Children = children,
            };

            /// <summary>One chevron glyph, turned to <paramref name="rotation"/> on the reveal's spring (Expander's chevron).</summary>
            static BoxEl Chevron(float rotation) => new()
            {
                Width = 10, Height = 10, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Rotation = rotation, Transition = MotionTok.Reveal,
                Children = [Ui.Icon(Icons.ChevronDown) with { Size = 10f, Color = Tok.TextTertiary }],
            };

            /// <summary>
            /// "ohsnap  ULTRA" — the alias name, its plan as a WinUI-style tag, and the account's error if it has one.
            /// <paramref name="collapsible"/> (Compact, Detailed): a chevron, and clicking the title folds the account to this
            /// one line, which then ends in its <see cref="Summary"/> (the error only while there are no numbers to show).
            /// </summary>
            static BoxEl AccountLine(AccountUsage u, float size, Chrome c, bool center = false, bool collapsible = false)
            {
                bool folded = collapsible && c.Collapsed.Contains(u.Name);
                var title = new List<Element>(3);
                if (collapsible) title.Add(Chevron(folded ? -90f : 0f));
                title.Add(new TextEl(u.Name) { Size = size, Weight = 600, Color = Tok.TextPrimary });
                if (u.Plan.Length > 0)
                {
                    title.Add(new BoxEl
                    {
                        Padding = new Edges4(6, 1, 6, 1), Corners = Radii.ControlAll, Fill = Tok.FillControlSecondary,
                        BorderColor = Tok.StrokeControlDefault, BorderWidth = 1f,
                        Children = [new TextEl(u.Plan.ToUpperInvariant()) { Size = 10f, Weight = 600, Color = Tok.TextSecondary }],
                    });
                }
                var parts = new List<Element>(4);
                if (collapsible)
                {
                    var titleRow = new BoxEl { Direction = 0, Gap = 8, AlignItems = FlexAlign.Center, Children = [.. title] };
                    parts.Add(Clickable(titleRow, () => c.ToggleCollapsed(u.Name), folded ? "Show the bars" : "Fold to one line"));
                }
                else parts.AddRange(title);
                if (folded && u.HasData)
                {
                    parts.Add(new BoxEl { Grow = 1f });
                    var glance = new List<Element>(2);
                    if (u.Failure is not null)
                        glance.Add(Clickable(Ui.Icon(Icons.Important) with { Size = 12f, Color = Tok.SystemFillCritical }, () => c.ShowErrors(u.Name), u.Error));
                    glance.Add(Summary(u, c));
                    // Fades in from the right as the bars fold away under it, and out again as they unfold.
                    parts.Add(new BoxEl { Key = "glance:" + u.Name, Direction = 0, Gap = 8, AlignItems = FlexAlign.Center, Animate = FadeIn, Children = [.. glance] });
                }
                else if (u.Error is { } err && !center)
                {
                    parts.Add(new BoxEl { Grow = 1f });
                    parts.Add(ErrorText(u, err, c.ShowErrors));
                }
                return new BoxEl
                {
                    Direction = 0, Gap = 8, AlignItems = FlexAlign.Center, Justify = center ? FlexJustify.Center : FlexJustify.Start,
                    Children = [.. parts],
                };
            }

            /// <summary>
            /// A folded account at a glance: its bars in miniature (5h over the week, Fable's slice stacked in) and what's
            /// left as a status pill — green / amber / red like the tray badge — with every number in the tooltip.
            /// </summary>
            static Element Summary(AccountUsage u, Chrome c)
            {
                var rows = Rows(u, "5h", "7d", expanded: false);
                var bars = new Element[rows.Count];
                for (int i = 0; i < rows.Count; i++) bars[i] = StackedBar(rows[i], 56f);

                double left = Math.Max(0, u.Headroom);
                ColorF tone = HeadroomColor(left);
                var pill = new BoxEl
                {
                    Width = 64, Padding = new Edges4(0, 1, 0, 2), Corners = Radii.ControlAll, Justify = FlexJustify.Center,
                    Fill = tone with { A = tone.A * 0.16f }, BorderColor = tone with { A = tone.A * 0.35f }, BorderWidth = 1f,
                    Children = [new TextEl($"{left:0}% left") { Size = 11f, Weight = 600, Color = tone, FontFeatures = FontFeatures.TabularFigures }],
                };

                var tip = new List<string>(rows.Count + 2);
                foreach (var r in rows) tip.Add(WindowLine(r.Label, r.Window, c.Now));
                foreach (var m in u.Models ?? [])
                    tip.Add($"{m.Name} {m.Window.Percent:0}% of its limit{(m.OfWeek is { } of ? $" · {of:0}% of the week" : "")}");
                return ToolTip.Wrap(new BoxEl
                {
                    Direction = 0, Gap = 10, AlignItems = FlexAlign.Center,
                    Children = [new BoxEl { Direction = 1, Gap = 3, Children = bars }, pill],
                }, string.Join("\n", tip));
            }

            /// <summary>A subtle button around <paramref name="content"/> (hover / pressed fill, hand cursor), with a tooltip.</summary>
            static Element Clickable(Element content, Action onClick, string? tip)
            {
                var button = new BoxEl
                {
                    Padding = new Edges4(4, 1, 4, 1), Margin = new Edges4(-4, -1, -4, -1), Corners = Radii.ControlAll,
                    Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                    Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = onClick,
                    Children = [content],
                }.NoWindowDrag();
                return tip is { Length: > 0 } ? ToolTip.Wrap(button, tip) : button;
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

            /// <summary>label · bar · percent · reset · chevron — one limit window. The week's row expands / collapses its
            /// model rows; a model row is indented under it with a swatch of its slice's colour.</summary>
            /// <summary>Room for a model row's swatch + name ("■ Fable").</summary>
            const float ModelLabelWidth = 44f;

            static Element Bar(AccountUsage u, Row r, float barWidth, float labelWidth, Chrome c)
            {
                var w = r.Window;
                // The label column fits "5h" / "7d"; a model row's "■ Fable" takes the extra room from its own bar, so it
                // sits indented under the week instead of every row reserving the space.
                if (r.IsModel && labelWidth < ModelLabelWidth)
                {
                    barWidth -= ModelLabelWidth - labelWidth;
                    labelWidth = ModelLabelWidth;
                }
                Element label = r.IsModel
                    ? new BoxEl
                    {
                        Direction = 0, Gap = 5, AlignItems = FlexAlign.Center, Width = labelWidth,
                        Children =
                        [
                            new BoxEl { Width = 6, Height = 6, Corners = CornerRadius4.All(1.5f), Fill = ModelTint(Severity(u.Weekly)) },
                            new TextEl(r.Label) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f },
                        ],
                    }
                    : new TextEl(r.Label) { Size = 12f, Color = Tok.TextSecondary, Width = labelWidth };
                var row = new BoxEl
                {
                    Direction = 0, Gap = 8, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        label,
                        StackedBar(r, barWidth),
                        new TextEl(Pct(w)) { Size = 12f, Weight = 600, Color = Tok.TextPrimary, Width = 36, TextAlign = TextAlign.End, FontFeatures = FontFeatures.TabularFigures },
                        new TextEl(Countdown.Reset(w?.ResetsAt, c.Now)) { Size = 12f, Color = Tok.TextSecondary, Width = 64, TextAlign = TextAlign.End, FontFeatures = FontFeatures.TabularFigures },
                        r.Expandable ? Chevron(c.Expanded.Contains(u.Name) ? 180f : 0f) with { Width = 12 } : new BoxEl { Width = 12 },
                    ],
                };
                return r.Expandable ? Expander(u, r, row, c) : row;
            }

            /// <summary>
            /// The week's bar: the fill in its status colour, the model limits' slice of the week stacked at its end in a
            /// faded tint of that colour (1px apart). Without shares (or with nothing used on the models) a plain ProgressBar.
            /// </summary>
            static BoxEl StackedBar(Row r, float width)
            {
                if (r.ModelShare is not { } share || share <= 0 || r.Window is null) return ProgressBar.Determinate(Frac(r.Window), width, State(r.Window));
                var st = ProgressBar.DefaultStyle;
                ColorF fill = st.ForegroundFor(State(r.Window));
                float total = Frac(r.Window) * width, model = Math.Min(total, (float)Math.Clamp(share / 100, 0, 1) * width), rest = total - model;
                float gap = rest >= 1f && model >= 2f ? 1f : 0f;
                var radius = CornerRadius4.All(st.IndicatorRadius);
                return new BoxEl
                {
                    ZStack = true, Width = width, Height = st.Height, Role = AutomationRole.ProgressBar,
                    Children =
                    [
                        new BoxEl { Width = width, Height = st.TrackHeight, OffsetY = (st.Height - st.TrackHeight) / 2f, Corners = CornerRadius4.All(st.TrackRadius), Fill = st.TrackFill },
                        new BoxEl { Width = rest, Height = st.Height, Corners = radius, Fill = fill },
                        new BoxEl { OffsetX = rest + gap, Width = model - gap, Height = st.Height, Corners = radius, Fill = ModelTint(fill) },
                    ],
                };
            }

            /// <summary>A ring for the Rings layout, stacked like <see cref="StackedBar"/>: the whole fill in the model tint
            /// with the other models' part drawn solid over it from 12 o'clock.</summary>
            static BoxEl Ring(Row r, float size)
            {
                ColorF fill = Severity(r.Window);
                if (r.ModelShare is not { } share || share <= 0 || r.Window is null)
                    return ProgressRing.Determinate(Frac(r.Window), size, foreground: fill, track: Tok.FillControlSecondary);
                float rest = Math.Max(0f, Frac(r.Window) - (float)Math.Clamp(share / 100, 0, 1));
                return new BoxEl
                {
                    ZStack = true, Width = size, Height = size,
                    Children =
                    [
                        ProgressRing.Determinate(Frac(r.Window), size, foreground: ModelTint(fill), track: Tok.FillControlSecondary),
                        ProgressRing.Determinate(rest, size, foreground: fill),
                    ],
                };
            }

            /// <summary>The colour of the model limits' slice: the bar's status colour, faded.</summary>
            static ColorF ModelTint(ColorF fill) => fill with { A = fill.A * 0.45f };

            /// <summary>Makes the week's row (or the strip's bars) a button that shows / hides the model rows, with a tooltip
            /// that spells out the split: "Other models 56% · Fable 35% of the week (up to 50%)".</summary>
            static Element Expander(AccountUsage u, Row week, Element content, Chrome c)
            {
                bool open = c.Expanded.Contains(u.Name);
                var parts = new List<string>(week.Models.Count + 1);
                if (week.ModelShare is { } share && week.Window is { } w) parts.Add($"Other models {Math.Max(0, w.Percent - share):0}%");
                foreach (var m in week.Models)
                    parts.Add(m.OfWeek is { } of
                        ? $"{m.Name} {of:0}% of the week{(m.AllowanceOfWeek is { } a ? $" (up to {a:0}%)" : "")}"
                        : $"{m.Name} {m.Window.Percent:0}% of its limit");
                string tip = $"{string.Join(" · ", parts)}\nClick to {(open ? "hide" : "show")} {string.Join(", ", week.Models.Select(m => m.Name))}";
                return Clickable(content, () => c.ToggleExpanded(u.Name), tip);
            }

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
                sb.AppendLine($"CLI's `/usage`. It polls every {Settings.PollMinutes} min; a failure backs off 1, 2, 4 … 30 min (429: 5, 10, 20, 40, 60 min;");
                sb.AppendLine("at least the server's Retry-After) and keeps showing the last good numbers. It skips the call when it already");
                sb.AppendLine("has numbers under 2 min old: its own (saved across restarts) or Claude Code's cached copy in");
                sb.AppendLine("`~/.claude-<name>/.claude.json` (`cachedUsageUtilization`, stamped `fetchedAtMs`). The windows come from the");
                sb.AppendLine("response's `limits` list (`session`, `weekly_all`, `weekly_scoped` per model), else `five_hour` / `seven_day`; the");
                sb.AppendLine("plan from the CLI's cached profile (`oauthAccount.planDisplayName`), else the credentials' `subscriptionType`.");
                sb.AppendLine();
                sb.AppendLine($"- Generated: {now:yyyy-MM-dd HH:mm:ss zzz}");
                sb.AppendLine($"- OS: {Environment.OSVersion.VersionString}, .NET {Environment.Version}");
                sb.AppendLine($"- Logs: `{ErrorLog.FilePath}`, `{ErrorLog.RequestsPath}`");
                sb.AppendLine();
                sb.AppendLine("## Accounts");
                sb.AppendLine();
                sb.AppendLine("| Account | Plan | 5h window | 7d window | Model limits | Status | Numbers from | Fetched |");
                sb.AppendLine("|---|---|---|---|---|---|---|---|");
                foreach (var u in usage)
                {
                    string models = u.Models is { Count: > 0 } ms ? string.Join(", ", ms.Select(m => $"{m.Name} {Window(m.Window, now)}")) : "–";
                    sb.AppendLine($"| {u.Name} | {(u.Plan.Length > 0 ? u.Plan : "–")} | {(u.HasData && u.Session is null ? "none" : Window(u.Session, now))} | {Window(u.Weekly, now)} | {models} | {u.Error ?? "OK"} | {(u.HasData ? u.Source : "–")} | {(u.HasData ? u.FetchedAt.ToString("HH:mm:ss") : "never")} |");
                }
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
