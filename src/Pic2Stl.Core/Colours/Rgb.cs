namespace Pic2Stl.Core.Colours;

/// <summary>An 8-bit-per-channel colour, used for the 3MF material palette.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static Rgb FromHsv(double hueDegrees, double saturation, double value)
    {
        var h = ((hueDegrees % 360) + 360) % 360 / 60.0;
        var c = value * saturation;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var m = value - c;

        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        static byte Scale(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        return new Rgb(Scale(r + m), Scale(g + m), Scale(b + m));
    }
}
