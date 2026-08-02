using Windows.UI;
using Windows.UI.ViewManagement;

namespace Chargle.Services;

public sealed record AccentChoice(string Name, Color? Colour);

/// <summary>
/// The colours the on-screen panel can be tinted with.
///
/// The first entry follows the Windows accent, which is the right default: most people have
/// already chosen a colour they like once, and asking them to choose again is a small rudeness.
/// The rest are there for the people who want the charging indicator to look like charging
/// rather than like the rest of their desktop.
/// </summary>
public static class IndicatorPalette
{
    public static readonly AccentChoice[] All =
    [
        new("Windows accent", null),
        new("Blue", Rgb(0x25, 0x63, 0xEB)),
        new("Cyan", Rgb(0x06, 0xB6, 0xD4)),
        new("Green", Rgb(0x16, 0xA3, 0x4A)),
        new("Amber", Rgb(0xD9, 0x77, 0x06)),
        new("Rose", Rgb(0xE1, 0x1D, 0x48)),
        new("Violet", Rgb(0x7C, 0x3A, 0xED)),
        new("Slate", Rgb(0x47, 0x55, 0x69)),
    ];

    /// <summary>Turns a stored name into a colour, falling back to the Windows accent.</summary>
    public static Color Resolve(string? name)
    {
        foreach (var choice in All)
        {
            if (choice.Colour is { } colour && string.Equals(choice.Name, name, StringComparison.OrdinalIgnoreCase))
                return colour;
        }

        return SystemAccent();
    }

    public static int IndexOf(string? name)
    {
        for (int i = 0; i < All.Length; i++)
        {
            if (string.Equals(All[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return 0;
    }

    private static Color SystemAccent()
    {
        try
        {
            return new UISettings().GetColorValue(UIColorType.Accent);
        }
        catch
        {
            // No shell to ask, which happens on some server SKUs. Fall back to our own blue.
            return Rgb(0x25, 0x63, 0xEB);
        }
    }

    /// <summary>
    /// Picks black or white text for a given background, using the relative luminance formula
    /// rather than a brightness guess, so the amber and cyan swatches stay readable.
    /// </summary>
    public static Color ForegroundOn(Color background)
    {
        double luminance =
            (0.2126 * Channel(background.R) +
             0.7152 * Channel(background.G) +
             0.0722 * Channel(background.B));

        return luminance > 0.4 ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255);

        static double Channel(byte value)
        {
            double v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
}
