using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Svg.Skia;

namespace WheelWizard.LoungeStats;

/// <summary>
/// A <see cref="HudArt"/> picture at a set height, drawn sharp for the screen's scaling. Pictures are kept by content
/// and size, so the count-up animation reuses the frames it already drew.
/// </summary>
public sealed class HudSvgImage : Control
{
    private static readonly Dictionary<(string, int), Bitmap?> Cache = [];
    private const int CacheSize = 96;

    private HudSvg? _svg;
    private double _height = 32;
    private Bitmap? _bitmap;

    /// <summary>What the picture shows ("4275", or "emblem"), for tests and tooltips.</summary>
    public string Text { get; private set; } = string.Empty;

    public HudSvg? Svg => _svg;

    public void Set(HudSvg svg, double height, string text)
    {
        _svg = svg;
        _height = height;
        Text = text;
        Draw();
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Draw();
    }

    private void Draw()
    {
        if (_svg is null)
            return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        // The component can be scaled up by the Size setting, so draw for that size, and draw twice as large as needed so
        // shrinking it on screen keeps the edges sharp.
        var magnify = 1.0;
        foreach (var ancestor in Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(this))
            if (ancestor is LayoutTransformControl { LayoutTransform: Avalonia.Media.ScaleTransform scale })
                magnify *= Math.Abs(scale.ScaleX);
        var pixels = (int)Math.Clamp(Math.Ceiling(_height * scaling * magnify * 2), 4, 4096);
        var key = (_svg.Markup, pixels);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var known))
            {
                _bitmap = known;
                return;
            }
        }

        Bitmap? bitmap = null;
        try
        {
            if (Png(_svg, pixels) is { } png)
            {
                using var stream = new MemoryStream(png);
                bitmap = new Bitmap(stream);
            }
        }
        catch (Exception)
        {
            // No renderer (as in tests): the space is kept and nothing is drawn.
        }

        lock (Cache)
        {
            if (Cache.Count >= CacheSize)
                Cache.Clear();
            Cache[key] = bitmap;
        }

        _bitmap = bitmap;
    }

    /// <summary>The picture as a PNG that is <paramref name="pixelHeight"/> tall, or null when it can't be drawn.</summary>
    public static byte[]? Png(HudSvg svg, int pixelHeight)
    {
        using var reader = new SKSvg();
        if (reader.FromSvg(svg.Markup) is not { CullRect: { Width: > 0, Height: > 0 } } picture)
            return null;

        var bounds = picture.CullRect;
        var height = Math.Max(1, pixelHeight);
        var width = Math.Max(1, (int)Math.Ceiling(height * svg.Width / svg.Height));
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(width / bounds.Width, height / bounds.Height);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        _svg is null ? default : new Size(_height * _svg.Width / _svg.Height, _height);

    public override void Render(Avalonia.Media.DrawingContext context)
    {
        if (_bitmap is not null)
        {
            // Its own shape, at the left: a panel that stretches it must not pull the picture wide.
            var width = _svg is null ? Bounds.Width : Math.Min(Bounds.Width, Bounds.Height * _svg.Width / _svg.Height);
            using (
                context.PushRenderOptions(
                    new Avalonia.Media.RenderOptions
                    {
                        BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality,
                    }
                )
            )
                context.DrawImage(_bitmap, new Rect(0, 0, width, Bounds.Height));
        }
    }
}
