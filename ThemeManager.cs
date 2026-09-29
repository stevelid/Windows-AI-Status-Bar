using System.Windows.Media;
using StatusBar.Core.Usage;
using Color = System.Windows.Media.Color;

namespace ClaudeUsageWidget;

/// <summary>Color palette for the dark / light themes.</summary>
public static class ThemeManager
{
    public static bool IsLight { get; private set; }
    public static event Action? Changed;

    public static void Init(bool light) => IsLight = light;

    public static void Set(bool light)
    {
        if (IsLight == light) return;
        IsLight = light;
        Changed?.Invoke();
    }

    public static Color WindowBg => IsLight ? C(0xF7, 0xF7, 0xFA) : C(0x19, 0x19, 0x22);
    public static Color TitleText => IsLight ? C(0x1E, 0x1E, 0x28) : C(0xDD, 0xDD, 0xE4);
    public static Color LabelText => IsLight ? C(0x4A, 0x4A, 0x55) : C(0xB8, 0xB8, 0xC2);
    public static Color SubtleText => IsLight ? C(0x8A, 0x8A, 0x94) : C(0x77, 0x77, 0x82);
    public static Color StatusText => IsLight ? C(0x6E, 0x6E, 0x78) : C(0x8A, 0x8A, 0x96);
    public static Color TrackBg => IsLight ? C(0xDC, 0xDC, 0xE2) : C(0x33, 0x33, 0x3E);
    public static Color ErrorText => IsLight ? C(0xC2, 0x3B, 0x22) : C(0xFF, 0x9E, 0x80);

    public static Color ColorFor(double utilization) => utilization switch
    {
        >= 90 => IsLight ? C(0xD4, 0x38, 0x35) : C(0xF4, 0x51, 0x4E), // red
        >= 70 => IsLight ? C(0xD9, 0x82, 0x0B) : C(0xF5, 0xA9, 0x3B), // orange
        _ => IsLight ? C(0x1F, 0x6F, 0xD4) : C(0x4C, 0x9F, 0xF0),     // blue
    };

    /// <summary>Returns the color used for a remaining allowance level.</summary>
    public static Color ColorForAllowance(AllowanceLevel level) => level switch
    {
        AllowanceLevel.Low => IsLight ? C(0xD4, 0x38, 0x35) : C(0xF4, 0x51, 0x4E),
        AllowanceLevel.Approaching => IsLight ? C(0xD9, 0x82, 0x0B) : C(0xF5, 0xA9, 0x3B),
        _ => IsLight ? C(0x1F, 0x6F, 0xD4) : C(0x4C, 0x9F, 0xF0),
    };

    /// <summary>
    /// Softer amber and red for the strip's percentage text, so a warning is noticeable without
    /// drawing the eye; the normal colour is unchanged.
    /// </summary>
    public static Color SoftColorForAllowance(AllowanceLevel level) => level switch
    {
        AllowanceLevel.Low => IsLight ? C(0xC0, 0x5A, 0x55) : C(0xEE, 0x96, 0x92),
        AllowanceLevel.Approaching => IsLight ? C(0xB9, 0x7F, 0x2E) : C(0xE9, 0xBD, 0x82),
        _ => ColorForAllowance(level),
    };

    // Frozen brushes are immutable, shareable and skip per-element change tracking.
    static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();

    public static SolidColorBrush Brush(Color c)
    {
        if (!BrushCache.TryGetValue(c, out var brush))
        {
            brush = new SolidColorBrush(c);
            brush.Freeze();
            BrushCache[c] = brush;
        }
        return brush;
    }

    static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
