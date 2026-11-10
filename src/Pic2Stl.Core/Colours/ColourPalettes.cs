namespace Pic2Stl.Core.Colours;

/// <summary>The colours offered by the print-colour picker.</summary>
public static class ColourPalettes
{
    /// <summary>Eight hue families in the custom picker, laid out as columns.</summary>
    public const int ShadeColumns = 8;

    /// <summary>Brightness steps in each hue family, laid out as rows: 8 x 32 = 256 shades.</summary>
    public const int ShadeRows = 32;

    /// <summary>Sixteen common filament colours, shown as a 4x4 grid before the custom shades.</summary>
    public static IReadOnlyList<Rgb> Presets { get; } =
    [
        new(0xF4, 0xF5, 0xF6), // white
        new(0xBD, 0xC2, 0xC9), // light grey
        new(0x63, 0x6A, 0x74), // dark grey
        new(0x11, 0x12, 0x14), // black
        new(0xD6, 0x30, 0x2A), // red
        new(0xF0, 0x7C, 0x22), // orange
        new(0xF4, 0xD2, 0x2B), // yellow
        new(0x3F, 0xA8, 0x45), // green
        new(0x1E, 0x9E, 0x96), // teal
        new(0x2D, 0x6B, 0xD8), // blue
        new(0x1C, 0x2E, 0x5A), // navy
        new(0x7B, 0x4B, 0xC9), // purple
        new(0xE4, 0x6F, 0xA5), // pink
        new(0xF2, 0xC6, 0xA0), // light skin
        new(0xC9, 0x8D, 0x62), // mid skin
        new(0x7A, 0x4A, 0x2B), // brown
    ];

    /// <summary>
    /// The 256 custom shades, row by row: the lower half darkens a hue, the upper half fades it to a pale tint.
    /// Each column is one hue family, so a user picking a custom colour can move across for hue and down for
    /// brightness or pastel.
    /// </summary>
    public static IReadOnlyList<Rgb> Shades { get; } = BuildShades();

    private static Rgb[] BuildShades()
    {
        const double saturated = 0.92;
        var shades = new Rgb[ShadeColumns * ShadeRows];
        var split = ShadeRows / 2;

        for (var column = 0; column < ShadeColumns; column++)
        {
            var hue = column * 360.0 / ShadeColumns;

            for (var row = 0; row < ShadeRows; row++)
            {
                var (value, saturation) = row < split
                    ? (0.15 + row * (0.85 / (split - 1)), saturated)
                    : (1.0, saturated - (row - split + 1) * (0.87 / split));

                shades[row * ShadeColumns + column] = Rgb.FromHsv(hue, saturation, value);
            }
        }

        return shades;
    }
}
