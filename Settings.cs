using FluentGpu.WindowsApi.Storage;

namespace ClaudeUsageWidget;

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
