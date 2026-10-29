using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace WheelWizard.NowPlaying;

/// <summary>
/// The song as a widget: the cover on one side, the title and artist and a row with the time and a moving sound bar
/// next to it, and a progress line under it. The cards take the cover's colour. Everything about it (style, size, which
/// parts show, colours) comes from <see cref="NowPlayingConfig"/>.
/// </summary>
public sealed class NowPlayingCard : LayoutTransformControl
{
    private const int BarCount = 22;

    private readonly Border _cover = new()
    {
        ClipToBounds = true,
        Background = new SolidColorBrush(Color.Parse("#33FFFFFF")),
        VerticalAlignment = VerticalAlignment.Top,
    };

    private readonly Image _picture = new() { Stretch = Stretch.UniformToFill };
    private readonly Border _titleCard = Card();
    private readonly Border _timeCard = Card();
    private readonly TextBlock _title = new()
    {
        FontSize = 15,
        FontWeight = FontWeight.Bold,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly TextBlock _artist = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _elapsed = Time(HorizontalAlignment.Left);
    private readonly TextBlock _total = Time(HorizontalAlignment.Right);
    private readonly StackPanel _barsPanel = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 3,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Border[] _bars = new Border[BarCount];
    private readonly Border _track = new() { Height = 5, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Border _fill = new() { HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private readonly Grid _top = new() { ColumnSpacing = 8 };
    private readonly StackPanel _right = new() { Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private NowPlayingInfo? _info;
    private Bitmap? _bitmap;
    private double _phase;
    private double _angle;
    private bool _circle;
    private bool _spin;

    public NowPlayingCard()
    {
        for (var i = 0; i < BarCount; i++)
        {
            _bars[i] = new Border
            {
                Width = 3,
                Height = 4,
                CornerRadius = new CornerRadius(1.5),
                Opacity = 0.9,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _barsPanel.Children.Add(_bars[i]);
        }

        _cover.Child = _picture;

        var titles = new StackPanel { Spacing = 1 };
        titles.Children.Add(_title);
        titles.Children.Add(_artist);
        _titleCard.Child = titles;

        var times = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8,
            Height = 22,
        };
        times.Children.Add(_elapsed);
        Grid.SetColumn(_barsPanel, 1);
        times.Children.Add(_barsPanel);
        Grid.SetColumn(_total, 2);
        times.Children.Add(_total);
        _timeCard.Padding = new Thickness(12, 5);
        _timeCard.Child = times;

        _right.Children.Add(_titleCard);
        _right.Children.Add(_timeCard);
        _top.Children.Add(_cover);
        _top.Children.Add(_right);

        _track.Child = _fill;
        var root = new StackPanel();
        root.Children.Add(_top);
        root.Children.Add(_track);
        Child = root;

        _track.SizeChanged += (_, _) => UpdateProgress();
        _tick.Tick += (_, _) => OnTick();
    }

    private static Border Card() => new() { Padding = new Thickness(12, 7) };

    private static TextBlock Time(HorizontalAlignment align) =>
        new()
        {
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>Shows the song in the look from <paramref name="config"/>; with none, the card says nothing is playing. A preview never fades out.</summary>
    public void Show(NowPlayingInfo? info, NowPlayingConfig config, bool preview = false)
    {
        _info = info;
        var style = config.GetString("layout.style");
        var cards = style is "classic" or "vertical";
        var plain = style == "text";
        var strip = style == "strip";
        var radius = config.GetNumber("layout.radius");
        var textColor = ParseColor(config.GetString("colors.text"), Colors.White);
        var progressColor = ParseColor(config.GetString("colors.progress"), Colors.White);
        var textSize = Math.Clamp(config.GetNumber("layout.textSize"), 0.6, 2);
        var gap = Math.Clamp(config.GetNumber("layout.gap"), 0, 40);
        var centered = config.GetString("layout.align") == "center";

        // Size and place
        var scale = Math.Clamp(config.GetNumber("layout.scale"), 0.3, 3);
        Width = Math.Clamp(config.GetNumber("layout.width"), 200, 900);
        LayoutTransform = new ScaleTransform(scale, scale);
        var coverSize = Math.Clamp(config.GetNumber("layout.coverSize"), 40, 220);
        if (strip)
            coverSize = Math.Min(coverSize, 64);
        _circle = config.GetString("layout.coverShape") == "circle";
        _cover.Width = coverSize;
        _cover.Height = coverSize;
        _cover.CornerRadius = new CornerRadius(_circle ? coverSize / 2 : radius);
        _titleCard.CornerRadius = new CornerRadius(radius);
        _timeCard.CornerRadius = new CornerRadius(radius);
        var coverLate = config.GetString("layout.coverSide") == "right";
        _top.ColumnSpacing = gap;
        _top.RowSpacing = gap;
        _right.Spacing = gap * 0.75;
        if (style == "vertical")
        {
            // The cover above the cards (or below them, with the cover on the right).
            _top.ColumnDefinitions = new ColumnDefinitions("*");
            _top.RowDefinitions = new RowDefinitions("Auto,Auto");
            Grid.SetColumn(_cover, 0);
            Grid.SetColumn(_right, 0);
            Grid.SetRow(_cover, coverLate ? 1 : 0);
            Grid.SetRow(_right, coverLate ? 0 : 1);
            _cover.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else
        {
            _top.RowDefinitions = new RowDefinitions();
            _top.ColumnDefinitions = new ColumnDefinitions(coverLate ? "*,Auto" : "Auto,*");
            Grid.SetRow(_cover, 0);
            Grid.SetRow(_right, 0);
            Grid.SetColumn(_cover, coverLate ? 1 : 0);
            Grid.SetColumn(_right, coverLate ? 0 : 1);
            _cover.HorizontalAlignment = HorizontalAlignment.Left;
        }

        // Words
        _title.FontSize = 15 * textSize;
        _artist.FontSize = 12 * textSize;
        _elapsed.FontSize = 12 * textSize;
        _total.FontSize = 12 * textSize;
        var alignment = centered ? TextAlignment.Center : TextAlignment.Left;
        _title.TextAlignment = alignment;
        _artist.TextAlignment = alignment;
        _right.VerticalAlignment = style is "classic" or "vertical" ? VerticalAlignment.Top : VerticalAlignment.Center;
        _title.Foreground = new SolidColorBrush(textColor);
        _artist.Foreground = new SolidColorBrush(textColor, 0.9);
        _artist.Inlines ??= new InlineCollection();
        _artist.Inlines.Clear();
        var artist = info is not null && config.GetBool("show.artist") ? info.Artist : string.Empty;
        if (info is null)
        {
            _title.Text = "Nothing is playing";
            _artist.Inlines.Add(new Run("Play a song in Spotify, Apple Music, SoundCloud or another player."));
        }
        else if (strip)
        {
            // One line: the title, then the artist.
            _title.Text = artist.Length > 0 ? $"{info.Title}  ·  {artist}" : info.Title;
        }
        else
        {
            _title.Text = info.Title;
            if (artist.Length > 0)
                _artist.Inlines.Add(new Run(artist));
            var source = config.GetBool("show.app") ? info.App : string.Empty;
            if (!info.IsPlaying)
                source = source.Length == 0 ? "Paused" : source + " · Paused";
            if (source.Length > 0)
                _artist.Inlines.Add(
                    new Run((artist.Length > 0 ? "  ·  " : string.Empty) + source) { Foreground = new SolidColorBrush(textColor, 0.6) }
                );
        }

        _artist.IsVisible = _artist.Inlines.Count > 0;

        // Colours and effects
        var tint = ParseColor(config.CardColor(info?.Cover), Color.Parse(CoverColor.Neutral));
        IBrush cardBrush = plain
            ? Brushes.Transparent
            : new SolidColorBrush(tint, Math.Clamp(config.GetNumber("colors.cardOpacity"), 0, 1));
        _titleCard.Background = cardBrush;
        _timeCard.Background = cardBrush;
        _titleCard.Padding = plain ? new Thickness(2, 4) : new Thickness(12, 7);
        _cover.BoxShadow =
            plain || !config.GetBool("effects.glow")
                ? default
                : new BoxShadows(new BoxShadow { Blur = 22, Color = Color.FromArgb(150, tint.R, tint.G, tint.B) });
        var borderWidth = Math.Clamp(config.GetNumber("effects.borderWidth"), 0, 8);
        var border = new SolidColorBrush(ParseColor(config.GetString("effects.borderColor"), Colors.White));
        foreach (var piece in new[] { _cover, _titleCard, _timeCard })
        {
            piece.BorderThickness = new Thickness(borderWidth);
            piece.BorderBrush = border;
        }

        _elapsed.Foreground = new SolidColorBrush(textColor);
        _total.Foreground = new SolidColorBrush(textColor);
        foreach (var bar in _bars)
            bar.Background = new SolidColorBrush(textColor);
        _track.Background = new SolidColorBrush(progressColor, 0.25);
        _fill.Background = new SolidColorBrush(progressColor);
        _track.CornerRadius = new CornerRadius(Math.Min(radius, 2.5));
        _fill.CornerRadius = _track.CornerRadius;
        _spin = _circle && config.GetBool("effects.spin");
        if (!_spin)
        {
            _angle = 0;
            _cover.RenderTransform = null;
        }

        // Parts
        var showCover = config.GetBool("show.cover");
        SetPicture(showCover ? info?.Cover : null);
        _cover.IsVisible = showCover;
        var hasLength = info?.HasLength == true;
        var showTime = config.GetBool("show.time") && hasLength;
        var showBars = config.GetBool("show.bars");
        _timeCard.IsVisible = cards && (showTime || showBars);
        _elapsed.IsVisible = showTime;
        _total.IsVisible = showTime;
        _total.Text = hasLength ? NowPlayingInfo.FormatTime(info!.Duration) : string.Empty;
        _barsPanel.IsVisible = showBars;
        _track.IsVisible = config.GetBool("show.progress") && hasLength;

        // With the option on, the card fades out while nothing plays; with no song at all it still says so.
        Opacity = !preview && info is not null && config.IsHidden(info) ? 0 : 1;
        UpdateProgress();
        UpdateBars();
    }

    private static Color ParseColor(string hex, Color fallback) => Color.TryParse(hex, out var color) ? color : fallback;

    private void SetPicture(byte[]? bytes)
    {
        Bitmap? bitmap = null;
        if (bytes is { Length: > 0 })
        {
            try
            {
                bitmap = new Bitmap(new MemoryStream(bytes));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // A picture the app sent that cannot be read; the card shows its plain square.
            }
        }

        _picture.Source = bitmap;
        _bitmap?.Dispose();
        _bitmap = bitmap;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _tick.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _tick.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTick()
    {
        _phase += 0.35;
        if (_spin && _info is { IsPlaying: true })
        {
            // A record turning: the cover rotates while the song plays and stays where it is when paused.
            _angle = (_angle + 3) % 360;
            _cover.RenderTransformOrigin = RelativePoint.Center;
            _cover.RenderTransform = new RotateTransform(_angle);
        }

        UpdateProgress();
        UpdateBars();
    }

    private void UpdateProgress()
    {
        if (_info is not { HasLength: true } info)
            return;

        var elapsed = info.ElapsedAt(DateTimeOffset.UtcNow);
        _elapsed.Text = NowPlayingInfo.FormatTime(elapsed);
        _fill.Width = Math.Max(0, _track.Bounds.Width * elapsed / info.Duration);
    }

    // A few waves of different speeds, so the bars move like a sound level and not in step; flat when nothing plays.
    private void UpdateBars()
    {
        var playing = _info is { IsPlaying: true };
        for (var i = 0; i < BarCount; i++)
        {
            var wave = Math.Abs(Math.Sin(_phase * (0.6 + i % 5 * 0.17) + i * 1.3)) * 0.7 + Math.Abs(Math.Sin(_phase * 0.4 + i * 0.5)) * 0.3;
            _bars[i].Height = playing ? 4 + 12 * wave : 4;
        }
    }
}
