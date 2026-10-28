using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WheelWizard.MiiImages.Domain;
using WheelWizard.MiiImages.Views;
using WheelWizard.WiiManagement.MiiManagement.Domain.Mii;

namespace WheelWizard.StreamOverlay;

/// <summary>
/// A live copy of the overlay badge for the settings tab, drawn from the same values the OBS link carries. It follows the
/// page layout and colors closely; the background picture is not drawn here.
/// </summary>
public sealed class OverlayPreview : Border
{
    private const double SampleGain = 41;
    private double _targetVr = 87747;

    // The change the card shows: a sample until a real player is shown, then what their VR did since the card first saw it.
    private double _gain = SampleGain;
    private string _deltaText = "+41";
    private int? _firstVr;
    private int? _lastVr;
    private int _rank = 279;

    private readonly OverlayConfig _config;
    private readonly Viewbox _viewbox = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private string _playerName;
    private Mii? _mii;
    private Bitmap? _image;
    private LinearGradientBrush? _spinBrush;
    private Border? _glowShell;
    private TextBlock? _vrText;
    private Border? _vrHost;
    private double _angle;
    private double _pulse;
    private long _animStart = -1;
    private string _borderEffect = "off";
    private double _speedSeconds = 5.5;

    public OverlayPreview(OverlayConfig config, string playerName)
    {
        _config = config;
        _playerName = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName;
        Padding = new Thickness(0, 6);
        Child = _viewbox;
        _config.Changed += Rebuild;
        _timer.Tick += (_, _) => Tick();
        Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Shows another player name (picking a different license).</summary>
    public void SetPlayerName(string name, Mii? mii = null)
    {
        _playerName = string.IsNullOrWhiteSpace(name) ? "Player" : name;
        _mii = mii;
        Rebuild();
    }

    /// <summary>Shows the chosen background picture (the first frame for a GIF), or none.</summary>
    public void SetBackgroundImage(Bitmap? image)
    {
        _image = image;
        Rebuild();
    }

    private Control? ImageLayer()
    {
        if (_image is null)
            return null;

        var x = Math.Clamp(_config.GetNumber("background.x"), 0, 100) / 100;
        var y = Math.Clamp(_config.GetNumber("background.y"), 0, 100) / 100;
        var zoom = Math.Max(1, _config.GetNumber("background.zoom"));
        var blur = _config.GetNumber("background.blur");
        var image = new Image
        {
            Source = _image,
            Stretch = _config.GetString("background.fit") switch
            {
                "contain" => Stretch.Uniform,
                "stretch" => Stretch.Fill,
                _ => Stretch.UniformToFill,
            },
            HorizontalAlignment =
                x < 0.34 ? HorizontalAlignment.Left
                : x > 0.66 ? HorizontalAlignment.Right
                : HorizontalAlignment.Center,
            VerticalAlignment =
                y < 0.34 ? VerticalAlignment.Top
                : y > 0.66 ? VerticalAlignment.Bottom
                : VerticalAlignment.Center,
            RenderTransformOrigin = new RelativePoint(x, y, RelativeUnit.Relative),
            RenderTransform = new ScaleTransform(zoom, zoom),
            Effect = blur > 0 ? new BlurEffect { Radius = blur } : null,
        };
        return image;
    }

    /// <summary>Shows a real player's name, VR and rank; null goes back to the sample values.</summary>
    public void SetPlayer(string? name, int? vr, int? rank, Mii? mii = null, bool samePlayer = false)
    {
        _mii = mii;
        if (!string.IsNullOrWhiteSpace(name))
            _playerName = name;
        TrackChange(vr, samePlayer);
        _targetVr = vr ?? 87747;
        _rank = rank is > 0 ? rank.Value : 279;
        Rebuild();
    }

    // A real VR gives a real change: the step from the last VR, and the total since the card first saw the player.
    private void TrackChange(int? vr, bool samePlayer)
    {
        if (vr is not { } now)
        {
            _firstVr = _lastVr = null;
            _gain = SampleGain;
            _deltaText = "+41";
            return;
        }

        if (!samePlayer || _firstVr is null || _lastVr is null)
        {
            _firstVr = now;
            _gain = 0;
        }
        else
            _gain = now - _lastVr.Value;

        _lastVr = now;
        var total = now - _firstVr.Value;
        _deltaText = total >= 0 ? $"+{total:N0}" : $"-{-total:N0}";
    }

    /// <summary>Plays the VR change the way the OBS overlay would.</summary>
    public void Replay() => _animStart = Environment.TickCount64;

    private static Color Parse(string hex, Color fallback) => Color.TryParse(hex, out var color) ? color : fallback;

    private static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B);

    private void Rebuild()
    {
        var width = _config.GetNumber("layout.width");
        var height = _config.CardHeight();
        var compact = _config.GetBool("layout.compact");
        var radius = _config.GetNumber("border.radius");
        _borderEffect = _config.GetString("border.effect");
        _speedSeconds = Math.Max(0.5, _config.GetNumber("border.speed"));
        var borderWidth = _borderEffect == "off" ? 0 : _config.GetNumber("border.width");
        var c1 = Parse(_config.GetString("border.color1"), Colors.Cyan);
        var c2 = Parse(_config.GetString("border.color2"), Colors.MediumPurple);
        var c3 = Parse(_config.GetString("border.color3"), Colors.Orange);

        _spinBrush = null;
        IBrush borderBrush = Brushes.Transparent;
        switch (_borderEffect)
        {
            case "rainbow":
            case "wave":
                _spinBrush = Gradient(c1, c2, c3, c1);
                borderBrush = _spinBrush;
                break;
            case "snake":
                _spinBrush = Gradient(Colors.Transparent, c1, c2, Colors.Transparent, Colors.Transparent);
                borderBrush = _spinBrush;
                break;
            case "ghost":
                _spinBrush = Gradient(WithAlpha(c1, .45), WithAlpha(c2, .45), WithAlpha(c3, .45), WithAlpha(c1, .45));
                borderBrush = _spinBrush;
                break;
            case "pulse":
            case "solid":
                borderBrush = new SolidColorBrush(c1);
                break;
        }

        var strength = _config.GetNumber("border.glowStrength");
        var glow = _config.GetBool("border.glow") && _borderEffect != "off";
        var shadows = glow
            ? new BoxShadows(new BoxShadow { Blur = 10 + 28 * strength, Color = WithAlpha(c1, .55 * strength + .15) })
            : default;

        var inner = new Border
        {
            CornerRadius = new CornerRadius(Math.Max(0, radius - borderWidth)),
            ClipToBounds = true,
            Background = Backdrop(),
            Child = Layers(Content(compact, height)),
        };

        _glowShell = new Border
        {
            Width = width,
            Height = height + borderWidth * 2,
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(borderWidth),
            Background = borderBrush,
            BoxShadow = shadows,
            Child = inner,
            Margin = new Thickness(18),
        };
        _viewbox.Child = _glowShell;
    }

    private static LinearGradientBrush Gradient(params Color[] colors)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };
        for (var i = 0; i < colors.Length; i++)
            brush.GradientStops.Add(new GradientStop(colors[i], i / (double)(colors.Length - 1)));
        return brush;
    }

    // Picture first, then the tint, then the badge itself.
    private Control Layers(Control content)
    {
        var grid = new Grid();
        if (ImageLayer() is { } image)
        {
            grid.Children.Add(image);
            var tint = Parse(_config.GetString("background.overlayColor"), Color.Parse("#07111f"));
            grid.Children.Add(
                new Border
                {
                    IsHitTestVisible = false,
                    Background = new SolidColorBrush(WithAlpha(tint, _config.GetNumber("background.overlayOpacity"))),
                }
            );
        }

        grid.Children.Add(content);
        return grid;
    }

    private IBrush Backdrop()
    {
        var overlay = Parse(_config.GetString("background.overlayColor"), Color.Parse("#07111f"));
        var tint = _config.GetNumber("background.overlayOpacity");
        var brightness = Math.Clamp(_config.GetNumber("background.brightness"), 0, 2);

        byte Mix(byte baseValue, byte over)
        {
            var dim = baseValue * Math.Min(1, brightness / 0.58);
            return (byte)Math.Clamp(dim * (1 - tint) + over * tint, 0, 255);
        }

        var top = Color.FromRgb(Mix(0x1b, overlay.R), Mix(0x2a, overlay.G), Mix(0x4a, overlay.B));
        var bottom = Color.FromRgb(Mix(0x0a, overlay.R), Mix(0x10, overlay.G), Mix(0x20, overlay.B));
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        };
    }

    private Control Content(bool compact, int height)
    {
        var text = Parse(_config.GetString("typography.textColor"), Colors.White);
        var muted = Parse(_config.GetString("typography.mutedColor"), Colors.LightGray);
        var weight = (FontWeight)(int)Math.Clamp(_config.GetNumber("typography.weight"), 100, 900);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = compact ? new Thickness(22, 14) : new Thickness(28, 20),
            Height = height,
        };

        // Left: avatar, name and the small info chips.
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (_config.GetBool("visibility.avatar"))
            left.Children.Add(Avatar(compact));

        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 7 };
        if (_config.GetBool("visibility.name"))
        {
            var tag = _config.GetBool("visibility.tag") ? _config.GetString("identity.tag") : string.Empty;
            copy.Children.Add(
                new TextBlock
                {
                    Text = string.Join(' ', new[] { tag, _playerName }.Where(s => s.Length > 0)),
                    FontSize = compact ? 22 : 26,
                    FontWeight = weight,
                    Foreground = new SolidColorBrush(text),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 260,
                }
            );
        }

        if (_config.GetBool("visibility.connection"))
            copy.Children.Add(Chip("In a room", muted, false));
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        if (_config.GetBool("visibility.room"))
            chips.Children.Add(Chip("Retro Tracks", muted));
        if (_config.GetBool("visibility.track"))
            chips.Children.Add(Chip("GBA Rainbow Road", muted));
        if (_config.GetBool("visibility.sessionDelta"))
            chips.Children.Add(Chip("SESSION +528", Color.Parse("#5ef0a5")));
        if (_config.GetBool("visibility.dailyDelta"))
            chips.Children.Add(Chip("24H +1,240", Color.Parse("#5ef0a5")));
        if (chips.Children.Count > 0)
            copy.Children.Add(chips);
        left.Children.Add(copy);
        grid.Children.Add(left);

        // Right: VR with its change, then rank.
        var right = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        _vrText = null;
        _vrHost = null;
        if (_config.GetBool("visibility.vr"))
        {
            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            _vrText = new TextBlock
            {
                Text = _targetVr.ToString("N0"),
                FontSize = compact ? 38 : 46,
                FontWeight = FontWeight.Black,
                Foreground = new SolidColorBrush(Color.Parse("#8effc6")),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _vrHost = new Border { Child = _vrText, RenderTransformOrigin = RelativePoint.Center };
            line.Children.Add(_vrHost);
            if (_config.GetBool("visibility.delta"))
                line.Children.Add(
                    _deltaText.StartsWith('-')
                        ? Pill(_deltaText, Color.Parse("#ff9d9d"), Color.Parse("#30c0392b"))
                        : Pill(_deltaText, Color.Parse("#7bffbd"), Color.Parse("#3020b26d"))
                );
            right.Children.Add(line);
        }

        var meta = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (_config.GetBool("visibility.rank"))
        {
            var rank = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            rank.Children.Add(
                new TextBlock
                {
                    Text = $"#{_rank:N0}",
                    FontSize = 15,
                    FontWeight = FontWeight.ExtraBold,
                    Foreground = new SolidColorBrush(Color.Parse("#ffe88f")),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
            if (_config.GetBool("visibility.rankDelta"))
            {
                rank.Children.Add(
                    new TextBlock
                    {
                        Text = "+2",
                        FontSize = 10,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#5ef0a5")),
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                );
            }

            meta.Children.Add(
                new Border
                {
                    Padding = new Thickness(11, 5),
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.Parse("#c4090b12")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#25ffffff")),
                    BorderThickness = new Thickness(1),
                    Child = rank,
                }
            );
        }

        meta.Children.Add(
            new TextBlock
            {
                Text = "VR",
                FontSize = 10,
                FontWeight = FontWeight.ExtraBold,
                Foreground = new SolidColorBrush(muted),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        right.Children.Add(meta);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private Control Avatar(bool compact)
    {
        var size = compact ? 48 : 58;
        var mode = _config.GetString("avatar.background");
        IBrush fill = mode switch
        {
            "solid" => new SolidColorBrush(Parse(_config.GetString("avatar.color1"), Colors.Cyan)),
            "transparent" => Brushes.Transparent,
            _ => new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Parse(_config.GetString("avatar.color1"), Colors.Cyan), 0),
                    new GradientStop(Parse(_config.GetString("avatar.color2"), Colors.Blue), 1),
                },
            },
        };
        var initials = string.Concat(
            _playerName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]))
        );
        if (initials.Length == 0)
            initials = "RR";
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size),
            Background = fill,
            BorderBrush = new SolidColorBrush(Color.Parse("#42ffffff")),
            BorderThickness = new Thickness(2),
            ClipToBounds = true,
            Child = _mii is null
                ? new TextBlock
                {
                    Text = initials[..Math.Min(2, initials.Length)],
                    Foreground = Brushes.White,
                    FontSize = 19,
                    FontWeight = FontWeight.Black,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : new MiiImageLoader
                {
                    Mii = _mii,
                    ImageVariant = MiiImageVariants.OnlinePlayerSmall,
                    ReloadMethod = BaseMiiImage.ReloadMethodType.KeepAllUntilNew,
                },
        };
    }

    private static Control Chip(string text, Color color, bool box = true) =>
        new Border
        {
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(6, 3),
            CornerRadius = new CornerRadius(5),
            Background = box ? new SolidColorBrush(Color.Parse("#8f090b12")) : Brushes.Transparent,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(color),
            },
        };

    private static Control Pill(string text, Color foreground, Color background) =>
        new Border
        {
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(99),
            Background = new SolidColorBrush(background),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = FontWeight.ExtraBold,
                Foreground = new SolidColorBrush(foreground),
            },
        };

    private void Tick()
    {
        var step = 33.0 / (_speedSeconds * 1000);
        _angle = (_angle + step * 360) % 360;
        _pulse = (_pulse + step * Math.PI * 2) % (Math.PI * 2);

        if (_spinBrush is not null)
        {
            var radians = _angle * Math.PI / 180;
            var dx = Math.Cos(radians) * 0.5;
            var dy = Math.Sin(radians) * 0.5;
            _spinBrush.StartPoint = new RelativePoint(0.5 - dx, 0.5 - dy, RelativeUnit.Relative);
            _spinBrush.EndPoint = new RelativePoint(0.5 + dx, 0.5 + dy, RelativeUnit.Relative);
        }

        if (_glowShell is not null)
            _glowShell.Opacity = _borderEffect == "pulse" ? 0.65 + 0.35 * (0.5 + 0.5 * Math.Sin(_pulse)) : 1;

        if (_animStart < 0 || _vrText is null || _vrHost is null)
            return;

        var duration = Math.Max(100, _config.GetNumber("animations.durationMs"));
        var t = Math.Min(1, (Environment.TickCount64 - _animStart) / duration);
        var eased = 1 - Math.Pow(1 - t, 4);
        var mode = _config.GetString("animations.vr");
        if (_config.GetBool("animations.reducedMotion") || mode == "none")
        {
            FinishAnimation();
            return;
        }

        _vrText.Text = (mode == "count" ? (_targetVr - _gain) + _gain * eased : _targetVr).ToString("N0");
        var scale = mode switch
        {
            "spring" => 0.65 + 0.35 * eased + Math.Sin(t * Math.PI) * 0.08,
            "burst" => 1.75 - 0.75 * eased,
            _ => 1,
        };
        _vrHost.RenderTransform = new ScaleTransform(scale, mode == "flip" ? Math.Max(0.05, eased) : scale);
        _vrHost.Opacity = mode == "count" ? 1 : Math.Max(0.1, eased);
        if (t >= 1)
            FinishAnimation();
    }

    private void FinishAnimation()
    {
        _animStart = -1;
        if (_vrText is not null)
            _vrText.Text = _targetVr.ToString("N0");
        if (_vrHost is null)
            return;
        _vrHost.RenderTransform = null;
        _vrHost.Opacity = 1;
    }
}
