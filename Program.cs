using ClaudeUsageWidget;
using ClaudeUsageWidget.Usage;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Hooks;
using FluentGpu.Hosting;

var accounts = Account.Discover();

FluentApp.Run(() => new OverlayHost { Child = Embed.Comp(() => new Widget(accounts)) }, new AppOptions
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
