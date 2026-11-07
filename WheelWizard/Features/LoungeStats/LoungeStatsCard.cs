using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WheelWizard.RrLounge;

namespace WheelWizard.LoungeStats;

/// <summary>
/// The RR Lounge stats as one component, in the look from <see cref="LoungeStatsConfig"/>: a stats card, or the compact
/// Mario Kart Wii HUD look (the rank badge and big yellow digits). Shown in the settings preview and the window; the OBS
/// page draws the same thing in HTML. It is only rebuilt when the numbers or the look change, never on a timer, and the
/// change animations play once per change.
/// </summary>
public sealed class LoungeStatsCard : Border
{
    private readonly RrLoungeImages? _images;
    private int _shownChange = -1;
    private DispatcherTimer? _countUp;

    // Pieces the change animations reach for, set by the last build.
    private Border? _mmrCell;
    private Border? _winsCell;
    private Border? _lossesCell;
    private Border? _rankCell;
    private Action<int>? _setMmr;
    private TextBlock? _deltaPill;

    public LoungeStatsCard(RrLoungeImages? images = null)
    {
        _images = images;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
    }

    /// <summary>
    /// Draws the stats. <paramref name="changeNumber"/> is the service's count of changes: a number this card has not
    /// played yet starts the animations for <paramref name="change"/>. With <paramref name="preview"/> a card with no stats
    /// says why instead of hiding.
    /// </summary>

    public void Show(
        LoungeStatsSnapshot? stats,
        LoungeStatsConfig config,
        LoungeStatsChange? change = null,
        int changeNumber = 0,
        bool preview = false
    )
    {
        _countUp?.Stop();
        var hidden = !config.GetBool("show.component");
        if (stats is null || (hidden && !preview))
        {
            Child = preview ? Note(stats is null ? "No stats yet. Pick your RR Lounge name below." : "Hidden") : null;
            Background = null;
            BorderThickness = default;
            IsVisible = preview;
            _shownChange = changeNumber;
            return;
        }

        IsVisible = true;
        Opacity = Math.Clamp(config.GetNumber("layout.opacity"), 0.1, 1) * (hidden ? 0.35 : 1);
        var content = config.GetString("layout.style") == "hud" ? BuildHud(stats, config) : BuildCard(stats, config);
        var scale = Math.Clamp(config.GetNumber("layout.scale"), 0.4, 3);
        Child = new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = content };
        PaintBackground(config);

        var animate = config.GetBool("effects.animations") && change is not null && _shownChange >= 0 && changeNumber > _shownChange;
        _shownChange = changeNumber;
        if (animate)
            Animate(stats, change!, config);
    }

    private void PaintBackground(LoungeStatsConfig config)
    {
        var mode = config.GetString("background.mode");
        CornerRadius = new CornerRadius(config.GetNumber("background.radius"));
        Padding = mode == "transparent" ? new Thickness(4) : new Thickness(14, 10);
        BorderThickness = default;
        if (mode == "transparent")
        {
            Background = null;
            return;
        }

        var color = Color.Parse(config.Color("background.color", "#0E1220"));
        var opacity = Math.Clamp(config.GetNumber("background.opacity"), 0, 1);
        if (mode == "blurred")
        {
            // Frosted glass: a lighter, see-through fill with a soft edge. The window adds a real blur of what is behind it.
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(WithAlpha(Blend(color, Colors.White, 0.18), opacity * 0.62), 0),
                    new GradientStop(WithAlpha(color, opacity * 0.5), 1),
                },
            };
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
            BorderThickness = new Thickness(1);
            return;
        }

        Background = new SolidColorBrush(WithAlpha(color, opacity));
    }

    #region Card style

    private Control BuildCard(LoungeStatsSnapshot stats, LoungeStatsConfig config)
    {
        var text = Math.Clamp(config.GetNumber("layout.textSize"), 0.5, 3);
        var gap = Math.Clamp(config.GetNumber("layout.spacing"), 0, 60);
        var labels = config.GetBool("show.labels");
        var horizontal = config.GetString("layout.direction") == "horizontal";
        var textColor = Brush(config.Color("colors.text", "#FFFFFF"));
        var labelColor = Brush(config.Color("colors.label", "#9AA3BB"));

        Control? identity = null;
        if (config.GetBool("show.flag") || config.GetBool("show.tag") || config.GetBool("show.place"))
        {
            var row = Row(gap * 0.6);
            if (config.GetBool("show.flag") && stats.FlagUrl is { } flag)
                row.Children.Add(RemoteImage(flag, 24 * text, 16 * text, 0));
            if (config.GetBool("show.tag"))
                row.Children.Add(Text(stats.Name, 17 * text, textColor, FontWeight.Bold));
            if (config.GetBool("show.place") && stats.Place is { } place)
                row.Children.Add(Text($"#{place}", 14 * text, labelColor, FontWeight.SemiBold));
            identity = row.Children.Count > 0 ? row : null;
        }

        _rankCell = null;
        if (config.GetBool("show.rank") && (stats.Division is not null || stats.BadgeUrl is not null))
        {
            var row = Row(6 * text);
            if (config.GetBool("show.badge") && stats.BadgeUrl is { } badge)
                row.Children.Add(RemoteImage(badge, 26 * text, 26 * text, 160));
            if (stats.Division is { } division)
                row.Children.Add(Text(division.ToUpperInvariant(), 14 * text, Brush(config.RankColor(stats)), FontWeight.Black));
            _rankCell = Cell(row, 3);
        }

        _mmrCell = _winsCell = _lossesCell = null;
        _deltaPill = null;
        _setMmr = null;
        var stat = new List<Control>();
        if (config.GetBool("show.mmr"))
        {
            var value = Text(
                Format(stats.Mmr),
                (horizontal ? 20 : 26) * text,
                Brush(config.Color("colors.mmr", "#FFFFFF")),
                FontWeight.Black
            );
            _setMmr = mmr => value.Text = Format(mmr);
            _deltaPill = Text(string.Empty, 12 * text, textColor, FontWeight.Bold);
            _deltaPill.Opacity = 0;
            _deltaPill.VerticalAlignment = VerticalAlignment.Center;
            _deltaPill.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(400) }];
            var line = Row(6 * text);
            line.Children.Add(value);
            line.Children.Add(_deltaPill);
            _mmrCell = Cell(Labeled(labels ? "MMR" : null, line, text, labelColor), 4);
            stat.Add(_mmrCell);
        }

        if (config.GetBool("show.wins"))
        {
            _winsCell = Cell(
                Labeled(
                    labels ? "WINS" : null,
                    Text(Count(stats.Wins), 18 * text, Brush(config.Color("colors.wins", "#5EF0A5")), FontWeight.Black),
                    text,
                    labelColor
                ),
                4
            );
            stat.Add(_winsCell);
        }

        if (config.GetBool("show.losses"))
        {
            _lossesCell = Cell(
                Labeled(
                    labels ? "LOSSES" : null,
                    Text(Count(stats.Losses), 18 * text, Brush(config.Color("colors.losses", "#FF6D7E")), FontWeight.Black),
                    text,
                    labelColor
                ),
                4
            );
            stat.Add(_lossesCell);
        }

        if (horizontal)
        {
            var all = Row(gap * 1.6);
            if (identity is not null)
                all.Children.Add(identity);
            if (_rankCell is not null)
                all.Children.Add(_rankCell);
            foreach (var piece in stat)
                all.Children.Add(piece);
            return all.Children.Count > 0 ? all : Note("Nothing to show. Turn on a stat.");
        }

        var stack = new StackPanel { Spacing = gap };
        var top = Row(gap);
        if (identity is not null)
            top.Children.Add(identity);
        if (_rankCell is not null)
            top.Children.Add(_rankCell);
        if (top.Children.Count > 0)
            stack.Children.Add(top);
        if (_mmrCell is not null)
            stack.Children.Add(_mmrCell);
        var record = Row(gap * 2);
        if (_winsCell is not null)
            record.Children.Add(_winsCell);
        if (_lossesCell is not null)
            record.Children.Add(_lossesCell);
        if (record.Children.Count > 0)
            stack.Children.Add(record);
        return stack.Children.Count > 0 ? stack : Note("Nothing to show. Turn on a stat.");
    }

    private static Control Labeled(string? label, Control value, double text, IBrush labelColor)
    {
        if (label is null)
            return value;
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(Text(label, 10.5 * text, labelColor, FontWeight.Bold, letterSpacing: 1.2));
        stack.Children.Add(value);
        return stack;
    }

    #endregion

    #region HUD style

    // The compact Mario Kart Wii look: the rank badge, then the MMR in big HUD digits, with the record in smaller ones.
    private Control BuildHud(LoungeStatsSnapshot stats, LoungeStatsConfig config)
    {
        var text = Math.Clamp(config.GetNumber("layout.textSize"), 0.5, 3);
        var gap = Math.Clamp(config.GetNumber("layout.spacing"), 0, 60);
        var labels = config.GetBool("show.labels");
        var horizontal = config.GetString("layout.direction") == "horizontal";
        var textColor = Brush(config.Color("colors.text", "#FFFFFF"));

        var look = HudLook.From(config, stats);

        HudSvgImage Digits(string value, double size)
        {
            var image = new HudSvgImage { VerticalAlignment = VerticalAlignment.Center };
            image.Set(HudArt.Number(value, look), size, value);
            return image;
        }

        _rankCell = null;
        if (config.GetBool("show.rank"))
        {
            Control? rank =
                !config.GetBool("hud.emblem") ? null
                : stats.BadgeUrl is { } badge
                    ? OutlinedBadge(badge, 64 * text, look.EmblemEdge, (int)Math.Clamp(config.GetNumber("hud.badgeOutline"), 0, 24))
                : Emblem(look, 60 * text);
            if (rank is not null)
                _rankCell = Cell(rank, 6);
        }

        // The number sits a little below the emblem's middle, as on the race HUD.
        var numbers = new StackPanel
        {
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 10 * text, 0, 0),
        };
        if (config.GetBool("show.flag") || config.GetBool("show.tag") || config.GetBool("show.place"))
        {
            var who = Row(6 * text);
            if (config.GetBool("show.flag") && stats.FlagUrl is { } flag)
                who.Children.Add(RemoteImage(flag, 21 * text, 14 * text, 0));
            if (config.GetBool("show.tag"))
                who.Children.Add(Text(stats.Name, 14 * text, textColor, FontWeight.Bold, shadow: true));
            if (config.GetBool("show.place") && stats.Place is { } place)
                who.Children.Add(Text($"#{place}", 13 * text, textColor, FontWeight.Bold, shadow: true));
            if (who.Children.Count > 0)
                numbers.Children.Add(who);
        }

        _mmrCell = _winsCell = _lossesCell = null;
        _deltaPill = null;
        _setMmr = null;
        var line = Row(gap);
        if (config.GetBool("show.mmr"))
        {
            var size = 34 * text;
            var mmr = Digits(stats.Mmr.ToString(CultureInfo.InvariantCulture), size);
            _setMmr = value =>
            {
                var shown = value.ToString(CultureInfo.InvariantCulture);
                mmr.Set(HudArt.Number(shown, look), size, shown);
            };
            _mmrCell = Cell(mmr, 6);
            line.Children.Add(_mmrCell);
            _deltaPill = Text(string.Empty, 14 * text, textColor, FontWeight.Black, shadow: true);
            _deltaPill.Opacity = 0;
            _deltaPill.VerticalAlignment = VerticalAlignment.Center;
            _deltaPill.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(400) }];
            line.Children.Add(_deltaPill);
        }

        if (config.GetBool("show.wins"))
        {
            _winsCell = Cell(Digits((labels ? "W" : "") + Count(stats.Wins), 20 * text), 5);
            line.Children.Add(_winsCell);
        }

        if (config.GetBool("show.losses"))
        {
            _lossesCell = Cell(Digits((labels ? "L" : "") + Count(stats.Losses), 20 * text), 5);
            line.Children.Add(_lossesCell);
        }

        if (line.Children.Count > 0)
            numbers.Children.Add(line);

        if (horizontal)
        {
            var all = Row(gap * 0.4);
            if (_rankCell is not null)
                all.Children.Add(_rankCell);
            if (numbers.Children.Count > 0)
                all.Children.Add(numbers);
            return all.Children.Count > 0 ? all : Note("Nothing to show. Turn on a stat.");
        }

        var stack = new StackPanel { Spacing = gap * 0.5, HorizontalAlignment = HorizontalAlignment.Center };
        if (_rankCell is not null)
        {
            _rankCell.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(_rankCell);
        }

        if (numbers.Children.Count > 0)
        {
            numbers.HorizontalAlignment = HorizontalAlignment.Center;
            foreach (var child in numbers.Children.OfType<Control>())
                child.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(numbers);
        }

        return stack.Children.Count > 0 ? stack : Note("Nothing to show. Turn on a stat.");
    }

    // The site's rank picture for the division, with a solid outline of any color drawn around its shape.
    private Image OutlinedBadge(string url, double height, string outlineColor, int outline)
    {
        var image = new Image
        {
            Width = height * 1.4,
            Height = height,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        if (_images is not null)
            _ = ShowOutlinedAsync(image, url, outlineColor, outline);
        return image;
    }

    private async Task ShowOutlinedAsync(Image image, string url, string outlineColor, int outline)
    {
        try
        {
            if (await _images!.GetAsync(url, 220) is not Bitmap bitmap)
                return;
            // The plain picture first, so there is always an icon; the outlined one replaces it.
            Dispatcher.UIThread.Post(() => image.Source = bitmap);
            var outlined = HudArt.Outlined(bitmap, outlineColor, outline);
            Dispatcher.UIThread.Post(() => image.Source = outlined);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Rank badge outline failed: {ex}");
            // No picture; the number still shows.
        }
    }

    private static HudSvgImage Emblem(HudLook look, double height)
    {
        var image = new HudSvgImage { VerticalAlignment = VerticalAlignment.Center };
        image.Set(HudArt.Emblem(look), height, "emblem");
        return image;
    }

    #endregion

    #region Change animations

    private void Animate(LoungeStatsSnapshot stats, LoungeStatsChange change, LoungeStatsConfig config)
    {
        var gain = Color.Parse(config.Color("colors.wins", "#5EF0A5"));
        var loss = Color.Parse(config.Color("colors.losses", "#FF6D7E"));

        if (change.MmrDelta != 0 && _mmrCell is not null)
        {
            var up = change.MmrDelta > 0;
            Flash(_mmrCell, up ? gain : loss);
            CountUp(stats.Mmr - change.MmrDelta, stats.Mmr);
            if (_deltaPill is not null)
            {
                _deltaPill.Text = (up ? "+" : "") + change.MmrDelta.ToString(CultureInfo.InvariantCulture);
                _deltaPill.Foreground = new SolidColorBrush(up ? gain : loss);
                Later(TimeSpan.FromMilliseconds(30), () => _deltaPill.Opacity = 1);
                var pill = _deltaPill;
                Later(TimeSpan.FromSeconds(6), () => pill.Opacity = 0);
            }
        }

        if (change.WinsUp && _winsCell is not null)
            Flash(_winsCell, gain);
        if (change.LossesUp && _lossesCell is not null)
            Flash(_lossesCell, loss);
        if (change.RankDirection != 0 && _rankCell is not null)
        {
            // Promotion grows and glows in the rank's colour, a demotion dips; both settle back.
            var cell = _rankCell;
            Flash(cell, Color.Parse(config.RankColor(stats)), TimeSpan.FromMilliseconds(1600));
            cell.RenderTransformOrigin = RelativePoint.Center;
            cell.Transitions =
            [
                .. cell.Transitions ?? [],
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(450) },
            ];
            cell.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse(
                change.RankDirection > 0 ? "scale(1.25)" : "scale(0.8)"
            );
            Later(
                TimeSpan.FromMilliseconds(600),
                () => cell.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)")
            );
        }
    }

    // The MMR runs from the old number to the new one in a little under a second.
    private void CountUp(int from, int to)
    {
        if (_setMmr is not { } set)
            return;
        var started = DateTime.UtcNow;
        set(from);
        _countUp = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        var timer = _countUp;
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / 900);
            var eased = 1 - Math.Pow(1 - t, 3);
            set((int)Math.Round(from + (to - from) * eased));
            if (t >= 1)
                timer.Stop();
        };
        timer.Start();
    }

    private static void Flash(Border cell, Color color, TimeSpan? hold = null)
    {
        cell.Transitions = [new BrushTransition { Property = BackgroundProperty, Duration = TimeSpan.FromMilliseconds(700) }];
        cell.Background = new SolidColorBrush(WithAlpha(color, 0.38));
        Later(hold ?? TimeSpan.FromMilliseconds(900), () => cell.Background = new SolidColorBrush(WithAlpha(color, 0)));
    }

    private static void Later(TimeSpan wait, Action action) => DispatcherTimer.RunOnce(action, wait);

    #endregion

    #region Pieces

    private static Border Cell(Control child, double padding) =>
        new()
        {
            Child = child,
            Padding = new Thickness(padding, padding * 0.5),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
        };

    private static StackPanel Row(double spacing) =>
        new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = spacing,
            VerticalAlignment = VerticalAlignment.Center,
        };

    private static TextBlock Text(string text, double size, IBrush color, FontWeight weight, double letterSpacing = 0, bool shadow = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = color,
            FontWeight = weight,
            LetterSpacing = letterSpacing,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (shadow)
            block.Effect = new DropShadowEffect
            {
                OffsetX = 1.5,
                OffsetY = 1.5,
                BlurRadius = 2,
                Color = Color.FromArgb(220, 0, 0, 0),
            };
        return block;
    }

    private static TextBlock Note(string text) =>
        new()
        {
            Text = text,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#9AA3BB")),
            Margin = new Thickness(6),
        };

    private Image RemoteImage(string url, double width, double height, int decodeWidth)
    {
        var image = new Image
        {
            Width = width,
            Height = height,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (_images is not null)
            _ = ShowImageAsync(image, url, decodeWidth);
        return image;
    }

    private async Task ShowImageAsync(Image image, string url, int decodeWidth)
    {
        try
        {
            if (await _images!.GetAsync(url, decodeWidth) is Bitmap bitmap)
                Dispatcher.UIThread.Post(() => image.Source = bitmap);
        }
        catch (Exception)
        {
            // No picture; the text next to it still shows.
        }
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

    private static Color Blend(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>"4,275" for an MMR.</summary>
    public static string Format(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"121", or "-" when the page did not give the number.</summary>
    public static string Count(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    #endregion
}
