using System.Globalization;
using System.Text;

namespace WheelWizard.LoungeStats;

/// <summary>The colours and depth of the HUD look, all recolourable from the settings.</summary>
public sealed record HudLook(
    string FillTop,
    string FillBottom,
    string Outline,
    string Depth,
    int DepthSize,
    string EmblemColor,
    string EmblemDark,
    string EmblemEdge,
    string EmblemSymbol
)
{
    public static HudLook From(LoungeStatsConfig config, LoungeStatsSnapshot? stats = null) =>
        new(
            config.Color("hud.fill", "#F2F20A"),
            config.Color("hud.fillBottom", "#B4B800"),
            config.Color("hud.outline", "#0A0A0A"),
            config.Color("hud.depth", "#77751C"),
            (int)Math.Clamp(config.GetNumber("hud.depthSize"), 0, 10),
            config.GetBool("hud.emblemDivision") && stats?.DivisionColor is { } site && LoungeStatsConfig.IsHex(site)
                ? LoungeStatsConfig.Normalize(site)
                : config.Color("hud.emblemColor", "#E5173F"),
            config.Color("hud.emblemDark", "#2B0408"),
            config.Color("hud.emblemEdge", "#000000"),
            config.Color("hud.emblemSymbol", "#FF7896")
        );
}

/// <summary>An SVG picture and the size of its drawing, so a view can keep its shape.</summary>
public sealed record HudSvg(string Markup, double Width, double Height);

/// <summary>
/// Draws the race-HUD look as SVG: blocky digits with a thick dark outline, a solid 3D edge down and to the right and a
/// two-tone fill, and a crowned shield emblem. The digits are drawn here from strokes on a small grid (no game font or
/// picture is used), so they look the same in the app and on the OBS page.
/// </summary>
public static class HudArt
{
    // Each glyph is a few round-ended bars on an 8 × 12 grid (the segments of a seven-segment digit). Every bar gets its
    // own thin dark outline, so the joins show as the dark cuts the game's digits have.
    private const double Pen = 4.4;
    private const double Edge = 1.0;
    private const double Advance = 8 + Pen + 3.2;
    private const double StepX = 0.55;
    private const double StepY = 0.65;

    private const string A = "M0 0H8";
    private const string B = "M8 0V6";
    private const string C = "M8 6V12";
    private const string D = "M0 12H8";
    private const string E = "M0 6V12";
    private const string F = "M0 0V6";
    private const string G = "M0 6H8";

    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = [A, B, C, D, E, F],
        ['1'] = [B, C],
        ['2'] = [A, B, G, E, D],
        ['3'] = [A, B, G, C, D],
        ['4'] = [F, G, B, C],
        ['5'] = [A, F, G, C, D],
        ['6'] = [A, F, G, E, D, C],
        ['7'] = [A, B, C],
        ['8'] = [A, B, C, D, E, F, G],
        ['9'] = [A, B, C, D, F, G],
        ['-'] = ["M1 6H7"],
        ['W'] = [F, E, D, C, B, "M4 6V12"],
        ['L'] = [F, E, D],
    };

    /// <summary>
    /// A folder of digit pictures ("0.png" to "9.png" and "-.png") to draw the numbers with instead of the drawn bars, such
    /// as the Mario Kart Wii HUD digits. They are the user's own files and are not shipped with the app. Colors do not apply
    /// to them.
    /// </summary>
    public static string? FontFolder { get; set; }

    private static readonly Dictionary<string, (string Uri, int W, int H)?> FontCache = [];

    private const int GlyphScale = 4;

    /// <summary>The data address of a recolored game digit picture, or null when there is none for it.</summary>
    public static string? GlyphUri(char c, HudLook look) => FontGlyph(c, look)?.Uri;

    /// <summary>The recolored game digit as PNG bytes, or null when there is none.</summary>
    public static byte[]? GlyphPng(char c, HudLook look) =>
        GlyphUri(c, look) is { } uri ? Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]) : null;

    /// <summary>How wide the recolored game digit is, in the same units as its height of 84.</summary>
    public static int GlyphWidth(char c, HudLook look) => FontGlyph(c, look)?.W ?? 0;

    /// <summary>How much each digit overlaps the next, in the same units.</summary>
    public const int GlyphOverlap = 2;

    private static (string Uri, int W, int H)? FontGlyph(char c, HudLook look)
    {
        if (FontFolder is null)
            return null;
        var key = $"{FontFolder}|{c}|{look.FillTop}|{look.FillBottom}";
        lock (FontCache)
        {
            if (FontCache.TryGetValue(key, out var hit))
                return hit;
            (string, int, int)? glyph = null;
            try
            {
                var file = System.IO.Path.Combine(FontFolder, char.ToUpperInvariant(c) + ".png");
                if (System.IO.File.Exists(file) && Prepare(file) is { } prepared)
                {
                    var (png, width) = Recolor(prepared, look);
                    glyph = (
                        "data:image/png;base64," + Convert.ToBase64String(png),
                        (int)Math.Ceiling(width / (double)GlyphScale),
                        prepared.Height / GlyphScale
                    );
                }
            }
            catch (Exception) { }

            return FontCache[key] = glyph;
        }
    }

    // A game digit made ready once (enlarged, its shape and shading sharpened, the empty sides found); only the colour
    // is added per look, which is quick.
    private sealed record PreparedGlyph(float[] Alpha, float[] Light, int Width, int Height, int Left, int Right);

    private static readonly Dictionary<string, PreparedGlyph?> Prepared = [];

    private static PreparedGlyph? Prepare(string file)
    {
        lock (Prepared)
        {
            if (Prepared.TryGetValue(file, out var known))
                return known;
        }

        PreparedGlyph? ready = null;
        using (var source = SkiaSharp.SKBitmap.Decode(file))
        {
            if (source is not null)
            {
                // Four times larger with a smooth resize, so it stays sharp when the HUD is big or captured.
                var info = new SkiaSharp.SKImageInfo(
                    source.Width * GlyphScale,
                    source.Height * GlyphScale,
                    SkiaSharp.SKColorType.Rgba8888,
                    SkiaSharp.SKAlphaType.Unpremul
                );
                using var big = source.Resize(info, new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
                if (big is not null)
                {
                    var w = big.Width;
                    var h = big.Height;
                    var pixels = new byte[w * h * 4];
                    System.Runtime.InteropServices.Marshal.Copy(big.GetPixels(), pixels, 0, pixels.Length);
                    var alpha = new float[w * h];
                    var light = new float[w * h];
                    for (var i = 0; i < alpha.Length; i++)
                    {
                        alpha[i] = pixels[i * 4 + 3] / 255f;
                        light[i] = Math.Min(1f, Math.Max(pixels[i * 4], pixels[i * 4 + 1]) / 235f);
                    }

                    // The upscale is smooth but soft: steepen the shape's edge and sharpen the shading so it looks drawn
                    // for this size, keeping the game's shading so the digit stays whole in any colour.
                    var soft = Blur(light, w, h, GlyphScale / 2);
                    for (var i = 0; i < light.Length; i++)
                    {
                        light[i] = Math.Clamp(light[i] + 1.2f * (light[i] - soft[i]), 0f, 1f);
                        alpha[i] = Math.Clamp(0.5f + (alpha[i] - 0.5f) * 3.2f, 0f, 1f);
                    }

                    // The empty space either side is cut, so digits sit close together like in the game.
                    var first = w;
                    var last = -1;
                    for (var y = 0; y < h; y += 2)
                    for (var x = 0; x < w; x++)
                        if (alpha[y * w + x] >= 0.5f)
                        {
                            first = Math.Min(first, x);
                            last = Math.Max(last, x);
                        }

                    if (last < first)
                    {
                        first = 0;
                        last = w - 1;
                    }

                    ready = new PreparedGlyph(
                        alpha,
                        light,
                        w,
                        h,
                        Math.Max(0, first - GlyphScale / 2),
                        Math.Min(w, last + 1 + GlyphScale / 2)
                    );
                }
            }
        }

        lock (Prepared)
            Prepared[file] = ready;
        return ready;
    }

    // The game's digits are yellow with a darker olive edge and black outline. Each pixel's brightness is kept and the
    // yellow is swapped for the chosen top-to-bottom colours, so the edge stays darker and the outline stays black.
    private static (byte[] Png, int Width) Recolor(PreparedGlyph glyph, HudLook look)
    {
        static (int R, int G, int B) Rgb(string hex) =>
            (
                int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber),
                int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber),
                int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber)
            );
        var top = Rgb(LoungeStatsConfig.Normalize(look.FillTop));
        var bottom = Rgb(LoungeStatsConfig.Normalize(look.FillBottom));
        var (w, h) = (glyph.Width, glyph.Height);
        var width = glyph.Right - glyph.Left;
        var pixels = new byte[width * h * 4];
        for (var y = 0; y < h; y++)
        {
            var t = h <= 1 ? 0f : (float)y / (h - 1);
            var r = top.R + (bottom.R - top.R) * t;
            var g = top.G + (bottom.G - top.G) * t;
            var b = top.B + (bottom.B - top.B) * t;
            for (var x = 0; x < width; x++)
            {
                var i = y * w + glyph.Left + x;
                var o = (y * width + x) * 4;
                var light = glyph.Light[i];
                pixels[o] = (byte)Math.Clamp(r * light, 0, 255);
                pixels[o + 1] = (byte)Math.Clamp(g * light, 0, 255);
                pixels[o + 2] = (byte)Math.Clamp(b * light, 0, 255);
                pixels[o + 3] = (byte)Math.Clamp(glyph.Alpha[i] * 255, 0, 255);
            }
        }

        using var result = new SkiaSharp.SKBitmap(
            new SkiaSharp.SKImageInfo(width, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul)
        );
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, result.GetPixels(), pixels.Length);
        using var image = SkiaSharp.SKImage.FromBitmap(result);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return (data.ToArray(), width);
    }

    // A box blur of the given radius, done as two passes with a running sum (quick at any radius).
    private static float[] Blur(float[] input, int w, int h, int radius)
    {
        var temp = new float[input.Length];
        var output = new float[input.Length];
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            float sum = 0;
            for (var k = 0; k <= Math.Min(w - 1, radius); k++)
                sum += input[row + k];
            for (var x = 0; x < w; x++)
            {
                var from = Math.Max(0, x - radius);
                var to = Math.Min(w - 1, x + radius);
                temp[row + x] = sum / (to - from + 1);
                if (x + radius + 1 < w)
                    sum += input[row + x + radius + 1];
                if (x - radius >= 0)
                    sum -= input[row + x - radius];
            }
        }

        for (var x = 0; x < w; x++)
        {
            float sum = 0;
            for (var k = 0; k <= Math.Min(h - 1, radius); k++)
                sum += temp[k * w + x];
            for (var y = 0; y < h; y++)
            {
                var from = Math.Max(0, y - radius);
                var to = Math.Min(h - 1, y + radius);
                output[y * w + x] = sum / (to - from + 1);
                if (y + radius + 1 < h)
                    sum += temp[(y + radius + 1) * w + x];
                if (y - radius >= 0)
                    sum -= temp[(y - radius) * w + x];
            }
        }

        return output;
    }

    /// <summary>A picture with a solid outline of the given color drawn around its shape, <paramref name="size"/> pixels wide.</summary>
    public static Avalonia.Media.Imaging.Bitmap Outlined(Avalonia.Media.Imaging.Bitmap source, string color, int size)
    {
        if (size <= 0)
            return source;
        // Drawn four times larger with smooth scaling, so the picture and its outline stay sharp when shown big or captured.
        const int scale = 4;
        var line = size * scale / 2;
        var pad = line + 2;
        var width = source.PixelSize.Width * scale;
        var height = source.PixelSize.Height * scale;
        var target = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new Avalonia.PixelSize(width + pad * 2, height + pad * 2),
            new Avalonia.Vector(96, 96)
        );
        var brush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(LoungeStatsConfig.Normalize(color)));
        var mask = new Avalonia.Media.ImageBrush(source) { Stretch = Avalonia.Media.Stretch.Fill };
        using (var context = target.CreateDrawingContext())
        using (
            context.PushRenderOptions(
                new Avalonia.Media.RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality }
            )
        )
        {
            for (var i = 0; i < 32; i++)
            {
                var angle = i * Math.PI / 16;
                var rect = new Avalonia.Rect(pad + Math.Cos(angle) * line, pad + Math.Sin(angle) * line, width, height);
                using (context.PushOpacityMask(mask, rect))
                    context.DrawRectangle(brush, null, rect);
            }

            context.DrawImage(source, new Avalonia.Rect(pad, pad, width, height));
        }

        return target;
    }

    /// <summary>True when every character can be drawn (digits, "-", "W" and "L").</summary>
    public static bool CanDraw(string text) => text.Length is > 0 and <= 12 && text.All(Glyphs.ContainsKey);

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The text in HUD digits. Characters that can't be drawn are left out.</summary>
    /// <summary>
    /// Text in the game font (digits, A to Z, "+" and "-"; spaces leave a gap), or null when a character has no picture
    /// in the hud-font folder, so the caller can keep plain text.
    /// </summary>
    public static HudSvg? Text(string text, HudLook look)
    {
        var upper = text.Trim().ToUpperInvariant();
        if (upper.Length == 0 || upper.Any(c => c != ' ' && FontGlyph(c, look) is null))
            return null;
        const int space = 26;
        var parts = upper.Select(c => c == ' ' ? ((string Uri, int W, int H)?)null : FontGlyph(c, look)).ToList();
        var tall = parts.Where(p => p is not null).Max(p => p!.Value.H);
        var total = parts.Sum(p => p?.W - GlyphOverlap ?? space) + GlyphOverlap;
        var picture = new StringBuilder();
        picture.Append(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 {total} {tall}\" width=\"{total}\" height=\"{tall}\">"
        );
        var at = 0;
        foreach (var part in parts)
        {
            if (part is not { } g)
            {
                at += space;
                continue;
            }

            picture.Append($"<image x=\"{at}\" y=\"0\" width=\"{g.W}\" height=\"{g.H}\" href=\"{g.Uri}\" xlink:href=\"{g.Uri}\"/>");
            at += g.W - GlyphOverlap;
        }

        picture.Append("</svg>");
        return new HudSvg(picture.ToString(), total, tall);
    }

    public static HudSvg Number(string text, HudLook look)
    {
        var chars = text.Where(Glyphs.ContainsKey).ToList();
        if (chars.Count == 0)
            chars = ['-'];

        if (chars.All(c => FontGlyph(c, look) is not null))
        {
            var glyphs = chars.Select(c => FontGlyph(c, look)!.Value).ToList();
            var total = glyphs.Sum(g => g.W) - (glyphs.Count - 1) * GlyphOverlap;
            var tall = glyphs.Max(g => g.H);
            var picture = new StringBuilder();
            picture.Append(
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 {total} {tall}\" width=\"{total}\" height=\"{tall}\">"
            );
            var at = 0;
            foreach (var g in glyphs)
            {
                picture.Append($"<image x=\"{at}\" y=\"0\" width=\"{g.W}\" height=\"{g.H}\" href=\"{g.Uri}\" xlink:href=\"{g.Uri}\"/>");
                at += g.W - GlyphOverlap;
            }

            picture.Append("</svg>");
            return new HudSvg(picture.ToString(), total, tall);
        }

        var bars = new List<string>();
        for (var i = 0; i < chars.Count; i++)
            bars.AddRange(Glyphs[chars[i]].Select(bar => Shift(bar, i * Advance)));
        var all = string.Concat(bars);

        var depth = Math.Clamp(look.DepthSize, 0, 10);
        var pad = Pen / 2 + Edge;
        var width = (chars.Count - 1) * Advance + 8 + pad * 2 + depth * StepX;
        var height = 12 + pad * 2 + depth * StepY;

        var svg = new StringBuilder();
        svg.Append(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{N(-pad)} {N(-pad)} {N(width)} {N(height)}\" width=\"{N(width * 4)}\" height=\"{N(height * 4)}\">"
        );
        svg.Append(
            $"<defs><linearGradient id=\"f\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"{N(-Pen / 2)}\" x2=\"0\" y2=\"{N(12 + Pen / 2)}\">"
        );
        svg.Append($"<stop offset=\"0\" stop-color=\"{Mix(look.FillTop, "#FFFFFF", 0.3)}\"/>");
        svg.Append($"<stop offset=\"0.5\" stop-color=\"{look.FillTop}\"/>");
        svg.Append($"<stop offset=\"1\" stop-color=\"{look.FillBottom}\"/>");
        svg.Append("</linearGradient></defs>");

        string Stroke(string d, string color, double width, string extra = "") =>
            $"<path d=\"{d}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{N(width)}\" stroke-linejoin=\"round\" stroke-linecap=\"round\"{extra}/>";

        // The 3D edge: every step's outline first, then every step's colour, so it reads as one solid block.
        for (var k = depth; k >= 1; k--)
            svg.Append(Stroke(all, look.Outline, Pen + Edge * 2, $" transform=\"translate({N(k * StepX)} {N(k * StepY)})\""));
        for (var k = depth; k >= 1; k--)
            svg.Append(Stroke(all, look.Depth, Pen, $" transform=\"translate({N(k * StepX)} {N(k * StepY)})\""));

        // Then each bar on its own, outline and fill, so the next bar's outline cuts into the one before it.
        foreach (var bar in bars)
        {
            svg.Append(Stroke(bar, look.Outline, Pen + Edge * 2));
            svg.Append(Stroke(bar, "url(#f)", Pen));
        }

        svg.Append("</svg>");
        return new HudSvg(svg.ToString(), width, height);
    }

    /// <summary>The crowned shield emblem that sits left of the number.</summary>
    public static HudSvg Emblem(HudLook look)
    {
        const string shield = "M5 30L0 2L16 16L26 0L43 14L60 0L70 16L86 2L81 30V56L43 110L5 56Z";
        const string crown = "M30 57V40L37 47L43 34L49 47L56 40V57Z";
        var svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-6 -8 98 126\" width=\"196\" height=\"252\">");
        svg.Append("<defs>");
        svg.Append("<linearGradient id=\"b\" x1=\"0\" y1=\"0\" x2=\"0.7\" y2=\"1\">");
        svg.Append($"<stop offset=\"0\" stop-color=\"{Mix(look.EmblemColor, "#FFFFFF", 0.22)}\"/>");
        svg.Append($"<stop offset=\"0.42\" stop-color=\"{look.EmblemColor}\"/>");
        svg.Append($"<stop offset=\"1\" stop-color=\"{look.EmblemDark}\"/></linearGradient>");
        svg.Append("<linearGradient id=\"s\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">");
        svg.Append($"<stop offset=\"0\" stop-color=\"{Mix(look.EmblemSymbol, "#FFFFFF", 0.35)}\"/>");
        svg.Append($"<stop offset=\"1\" stop-color=\"{Mix(look.EmblemSymbol, look.EmblemDark, 0.35)}\"/></linearGradient>");
        svg.Append($"<clipPath id=\"c\"><path d=\"{shield}\"/></clipPath>");
        svg.Append("</defs>");
        // A soft dark rim outside the light edge keeps it readable on a bright track.
        svg.Append(
            $"<path d=\"{shield}\" fill=\"none\" stroke=\"#000000\" stroke-opacity=\"0.55\" stroke-width=\"10\" stroke-linejoin=\"miter\"/>"
        );
        svg.Append($"<path d=\"{shield}\" fill=\"url(#b)\" stroke=\"{look.EmblemEdge}\" stroke-width=\"5\" stroke-linejoin=\"miter\"/>");
        svg.Append("<g clip-path=\"url(#c)\">");
        svg.Append("<path d=\"M-6 -8H43V112L-6 60Z\" fill=\"#FFFFFF\" fill-opacity=\"0.12\"/>");
        svg.Append(
            $"<path d=\"{crown}\" fill=\"url(#s)\" stroke=\"{look.EmblemDark}\" stroke-opacity=\"0.6\" stroke-width=\"2\" stroke-linejoin=\"miter\"/>"
        );
        svg.Append("</g></svg>");
        return new HudSvg(svg.ToString(), 98, 126);
    }

    private static string Shift(string glyph, double x)
    {
        // Moves every absolute x coordinate in a glyph path (after M, L, H and the first number of each pair) by x.
        var result = new StringBuilder();
        var tokens = Tokenize(glyph);
        var command = ' ';
        var index = 0;
        foreach (var token in tokens)
        {
            if (token.Length == 1 && char.IsLetter(token[0]))
            {
                command = token[0];
                index = 0;
                result.Append(command);
                continue;
            }

            var value = double.Parse(token, CultureInfo.InvariantCulture);
            var isX = command switch
            {
                'H' => true,
                'V' => false,
                _ => index % 2 == 0,
            };
            if (index > 0)
                result.Append(' ');
            result.Append(N(isX ? value + x : value));
            index++;
        }

        return result.ToString();
    }

    private static IEnumerable<string> Tokenize(string path)
    {
        var number = new StringBuilder();
        foreach (var c in path)
        {
            if (char.IsLetter(c))
            {
                if (number.Length > 0)
                    yield return number.ToString();
                number.Clear();
                yield return c.ToString();
            }
            else if (c == ' ')
            {
                if (number.Length > 0)
                    yield return number.ToString();
                number.Clear();
            }
            else
            {
                number.Append(c);
            }
        }

        if (number.Length > 0)
            yield return number.ToString();
    }

    /// <summary>"#RRGGBB" between two colours: 0 is the first, 1 the second.</summary>
    public static string Mix(string a, string b, double t)
    {
        static (int R, int G, int B) Rgb(string hex) =>
            (
                int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber),
                int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber),
                int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber)
            );
        var (ar, ag, ab) = Rgb(LoungeStatsConfig.Normalize(a));
        var (br, bg, bb) = Rgb(LoungeStatsConfig.Normalize(b));
        int Lerp(int from, int to) => (int)Math.Round(from + (to - from) * Math.Clamp(t, 0, 1));
        return $"#{Lerp(ar, br):X2}{Lerp(ag, bg):X2}{Lerp(ab, bb):X2}";
    }
}
