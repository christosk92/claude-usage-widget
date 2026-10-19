using System.Runtime.InteropServices;
using ClaudeUsageWidget.Usage;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.WindowsApi.Shell;

namespace ClaudeUsageWidget;

/// <summary>
/// The overlay: polls every account (a polling <c>UseResource</c>), renders the picked layout, and owns the window
/// extras — backdrop, topmost, the remembered position and the tray icon whose badge shows the best account's headroom.
/// Dragging, sizing and the menus are declarative (<c>WindowDragArea</c>, <c>SizeToContent</c>, <c>IconButton.Menu</c>).
/// </summary>
sealed partial class Widget(IReadOnlyList<Account> accounts) : Component
{
    // The tray glyph (Segoe Fluent "LightningBolt") and the badge state the icon source reads on every re-render.
    const string TrayGlyph = "";
    readonly UsageClient _client = new();
    readonly ResourceOptions _poll = new()
    {
        RefetchIntervalMs = Settings.PollMinutes * 60_000f,
        RefetchWhileInactive = true,   // keep the tray badge live while the widget is hidden
        KeepPreviousData = true,
    };
    NotifyIcon? _tray;
    string? _badge;
    ColorF _badgeFill;

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var usage = UseResource(ct => _client.FetchAllAsync(accounts, ct), Loading(accounts), DepKey.Empty, _poll);
        var layout = UseSignal(Settings.Layout);
        var topmost = UseSignal(Settings.Topmost);
        var acrylic = UseSignal(Settings.Acrylic);
        var (now, setNow) = UseState(DateTimeOffset.Now);

        UseEffect(() =>
        {
            RestorePosition();
            FluentApp.CloseRequested = _ => { SavePosition(); return false; };
            _tray = CreateTray(usage.Refresh);
            return () => { _tray?.Dispose(); _tray = null; };
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
            PollMinutes: Settings.PollMinutes);

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
}

/// <summary>What every layout needs from the widget: the header buttons and the clock.</summary>
sealed record Chrome(BoxEl Buttons, bool Fetching, DateTimeOffset Now, int PollMinutes);
