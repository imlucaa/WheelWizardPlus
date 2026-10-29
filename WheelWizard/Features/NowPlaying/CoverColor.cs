using SkiaSharp;

namespace WheelWizard.NowPlaying;

/// <summary>The colour a song's card is tinted with: the cover's main colour, kept dark enough for white text.</summary>
public static class CoverColor
{
    /// <summary>Used when there is no cover, or it cannot be read.</summary>
    public const string Neutral = "#4A4F5E";

    /// <summary>"#RRGGBB" for the cover picture, or <see cref="Neutral"/>.</summary>
    public static string From(byte[]? picture)
    {
        if (picture is not { Length: > 0 })
            return Neutral;

        try
        {
            using var decoded = SKBitmap.Decode(picture);
            if (decoded is null || decoded.Width == 0 || decoded.Height == 0)
                return Neutral;

            using var small = decoded.Resize(new SKImageInfo(24, 24), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (small is null)
                return Neutral;

            // Colourful pixels count more than grey ones, so a mostly white or black cover with one bright detail takes that colour.
            double r = 0,
                g = 0,
                b = 0,
                total = 0;
            for (var y = 0; y < small.Height; y++)
            {
                for (var x = 0; x < small.Width; x++)
                {
                    var pixel = small.GetPixel(x, y);
                    var max = Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue));
                    var min = Math.Min(pixel.Red, Math.Min(pixel.Green, pixel.Blue));
                    var weight = 0.15 + (max - min) / 255.0;
                    r += pixel.Red * weight;
                    g += pixel.Green * weight;
                    b += pixel.Blue * weight;
                    total += weight;
                }
            }

            return Darken(r / total, g / total, b / total);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Neutral;
        }
    }

    // White text must stay readable: the brightest channel is pulled into a middle range.
    private static string Darken(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var scale = max <= 0 ? 1 : Math.Clamp(max, 70, 150) / max;
        return $"#{(int)Math.Round(r * scale):X2}{(int)Math.Round(g * scale):X2}{(int)Math.Round(b * scale):X2}";
    }
}
