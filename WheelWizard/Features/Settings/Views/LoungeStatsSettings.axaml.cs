using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WheelWizard.LoungeStats;
using WheelWizard.RrLounge;
using WheelWizard.Views.Components;
using WheelWizard.Views.Dialogs;
using WheelWizard.Views.Shell;

namespace WheelWizard.Settings.Views;

/// <summary>
/// The Lounge stats tab: whose RR Lounge stats show, a live preview, every option of how they look, and the window and
/// OBS link for a stream. Every option is read by the preview, the window and the OBS page alike.
/// </summary>
public partial class LoungeStatsSettings : UserControl
{
    private readonly LoungeStatsService _service;
    private readonly LoungeStatsConfig _config;
    private readonly IPopupFactory _popups;
    private readonly RrLoungeImages? _images;
    private readonly LoungeStatsCard _card;
    private readonly List<Action> _syncs = [];
    private readonly InputField _nameInput = new()
    {
        Placeholder = "Your RR Lounge name",
        Variant = InputFieldVariant.Bordered,
        Width = 170,
    };
    private bool _syncing;

    private IDisposable? _lease;

    public LoungeStatsSettings(LoungeStatsService service, LoungeStatsConfig config, IPopupFactory popups, RrLoungeImages? images = null)
    {
        _service = service;
        _config = config;
        _popups = popups;
        _images = images;
        _card = new LoungeStatsCard(images);
        InitializeComponent();
        PreviewHost.Content = _card;
        LinkText.Text = LoungeStatsPage.Url;
        BuildOptions();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _lease = _service.Use();
        _service.Changed += OnChanged;
        _config.Changed += OnChanged;
        LoungeStatsWindow.OpenChanged += UpdateWindowButton;
        UpdateWindowButton();
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _service.Changed -= OnChanged;
        _config.Changed -= OnChanged;
        LoungeStatsWindow.OpenChanged -= UpdateWindowButton;
        _lease?.Dispose();
        _lease = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged() => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        _card.Show(_service.Current, _config, _service.LastChange, _service.ChangeNumber, preview: true);
        var problem = _service.Problem;
        StatusText.Text = problem ?? string.Empty;
        StatusText.IsVisible = problem is not null;
        StatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse(_service.Current is null ? "#9AA3BB" : "#FF6D7E"));
        if (!_nameInput.IsFocused)
            _nameInput.Text = _config.PlayerName;
    }

    private void UpdateWindowButton() => WindowButton.Text = LoungeStatsWindow.Current is null ? "Open as window" : "Close window";

    private void OpenWindow_OnClick(object? sender, RoutedEventArgs e)
    {
        if (LoungeStatsWindow.Current is not null)
            LoungeStatsWindow.CloseIfOpen();
        else
            LoungeStatsWindow.ShowOrActivate(_service, _config, _images);
    }

    private async void Copy_OnClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(LoungeStatsPage.Url);

        ViewUtils.ShowSnackbar("Link copied. Paste it into an OBS Browser source.");
    }

    private async void Refresh_OnClick(object? sender, RoutedEventArgs e) => await _service.RefreshNowAsync();

    private void Reset_OnClick(object? sender, RoutedEventArgs e)
    {
        _config.Reset();
        SyncAll();
    }

    private void CompactHud_OnClick(object? sender, RoutedEventArgs e)
    {
        _config.ApplyLook(LoungeStatsConfig.CompactHud);
        SyncAll();
    }

    #region Player

    // Looks the name up on the ladder: an exact match is taken at once, otherwise the close names are offered.
    private async Task FindPlayerAsync()
    {
        var name = (_nameInput.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            _config.SetPlayer(string.Empty, string.Empty);
            return;
        }

        var found = await _service.FindAsync(name, _config.Ladder);
        if (found is null)
        {
            ViewUtils.ShowSnackbar("RR Lounge could not be reached. Try again in a moment.", ViewUtils.SnackbarType.Warning);
            return;
        }

        var player = found.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (player is null && found.Count > 0)
        {
            var options = found.Select(p => new ChoiceOption(p.Name, $"#{p.Rank} · {LoungeStatsCard.Format(p.Mmr)} MMR")).ToList();
            var picked = await _popups
                .CreateChoiceList(
                    "RR Lounge",
                    "Which player are you?",
                    $"No one is called exactly \"{name}\". These names are close.",
                    options
                )
                .AwaitAnswer();
            if (picked is { } index)
                player = found[index];
            else
                return;
        }

        if (player is null)
        {
            ViewUtils.ShowSnackbar(
                $"No one called \"{name}\" is on the {_config.Ladder.ToUpperInvariant()} leaderboard this season.",
                ViewUtils.SnackbarType.Warning
            );
            return;
        }

        // A new player starts a read on its own.
        _nameInput.Text = player.Name;
        _config.SetPlayer(player.Name, player.Id);
    }

    private Control PlayerPicker()
    {
        var find = new WheelWizard.Views.Components.Button
        {
            Text = "Find",
            ButtonSize = WheelWizard.Views.Components.Button.ButtonsSizeType.Compact,
        };
        find.Click += async (_, _) => await FindPlayerAsync();
        _nameInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
                await FindPlayerAsync();
        };
        _nameInput.Text = _config.PlayerName;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(_nameInput);
        panel.Children.Add(find);
        return panel;
    }

    #endregion

    #region Options

    private void BuildOptions()
    {
        var player = Section("Player", expanded: true);
        player.Add(Row("RR Lounge name", "Type it and press Find. Your stats follow you if you change it on the site.", PlayerPicker()));
        player.Add(
            Row("Ladder", "rr is Retro Rewind, ct Custom Tracks, tt Time Trial.", Choice("player.ladder", LoungeStatsConfig.Ladders))
        );
        player.Add(
            Row(
                "Refresh every",
                "How often RR Lounge is asked. Your player page is only read again when your MMR moved.",
                Slider("data.refreshSeconds", LoungeStatsConfig.MinRefreshSeconds, LoungeStatsConfig.MaxRefreshSeconds, 10, " s")
            )
        );

        var show = Section("Show", expanded: true);
        show.Add(Row("Show the stats", "Off hides them in the window and on the OBS page.", Toggle("show.component")));
        foreach (
            var (path, label, hint) in new (string, string, string?)[]
            {
                ("rank", "Rank", "Your division, such as Master or Diamond."),
                ("badge", "Rank badge", "The division's picture from the site."),
                ("flag", "Country flag", null),
                ("tag", "Player tag", "Your RR Lounge name."),
                ("mmr", "MMR", null),
                ("wins", "Wins", null),
                ("losses", "Losses", null),
                ("place", "Leaderboard place", "Your place on the ladder, such as #12."),
                ("labels", "Labels", "Off shows the values only."),
            }
        )
            show.Add(Row(label, hint, Toggle($"show.{path}")));

        var style = Section("Style");
        style.Add(
            Row(
                "Style",
                "Card is a stats panel. Hud is the compact Mario Kart Wii look: the badge and big yellow digits.",
                Choice("layout.style", LoungeStatsConfig.Styles)
            )
        );
        style.Add(Row("Layout", "Stacked, or everything in one line.", Choice("layout.direction", LoungeStatsConfig.Directions)));
        style.Add(Row("Size", "Scales the whole component.", Slider("layout.scale", 0.5, 3, 0.05, "x")));
        style.Add(Row("Text size", null, Slider("layout.textSize", 0.6, 2, 0.05, "x")));
        style.Add(Row("Spacing", "The gap between the stats.", Slider("layout.spacing", 0, 40, 1, " px")));
        style.Add(Row("Opacity", null, Slider("layout.opacity", 0.1, 1, 0.05, "")));

        var position = Section("Position on the OBS page");
        position.Add(
            Row(
                "Corner",
                "Where it sits on the OBS page. Drag the window to move it there.",
                Choice("layout.anchor", LoungeStatsConfig.Anchors)
            )
        );
        position.Add(Row("Across", "Distance from that edge.", Slider("layout.x", 0, 800, 2, " px")));
        position.Add(Row("Down", "Distance from that edge.", Slider("layout.y", 0, 600, 2, " px")));

        var background = Section("Background");
        background.Add(
            Row(
                "Background",
                "Blurred is frosted glass. In the window it also blurs what is behind it.",
                Choice("background.mode", LoungeStatsConfig.BackgroundModes)
            )
        );
        background.Add(Row("Color", null, Color("background.color")));
        background.Add(Row("Strength", null, Slider("background.opacity", 0, 1, 0.02, "")));
        background.Add(Row("Roundness", null, Slider("background.radius", 0, 30, 1, " px")));

        var colors = Section("Colors");
        colors.Add(Row("Text", "The player tag.", Color("colors.text")));
        colors.Add(Row("Labels", null, Color("colors.label")));
        colors.Add(Row("MMR", "In the card style.", Color("colors.mmr")));
        colors.Add(Row("Division color", "Writes the rank in the site's color for your division.", Toggle("colors.divisionColor")));
        colors.Add(Row("Rank", "Used when the division color is off.", Color("colors.rank")));
        colors.Add(Row("Wins and gains", null, Color("colors.wins")));
        colors.Add(Row("Losses and drops", null, Color("colors.losses")));

        var hud = Section("HUD digits", expanded: true);
        hud.Add(Row("Rank badge", "Your rank picture from RR Lounge, left of the number.", Toggle("hud.emblem")));
        hud.Add(Row("Badge outline", "The line around the rank badge. Any color.", Color("hud.emblemEdge")));
        hud.Add(Row("Outline strength", "How thick the line is. 0 is none.", Slider("hud.badgeOutline", 0, 24, 1, "")));
        hud.Add(
            Row("Digit color", "The color of the number. Sets the top and a darker bottom for you.", Color("hud.fill", "hud.fillBottom"))
        );
        hud.Add(Row("Bottom shade", "Fine tune: the digits fade to this color.", Color("hud.fillBottom")));
        hud.Add(Row("Outline", "Only when the digits are drawn, not the game ones.", Color("hud.outline")));
        hud.Add(Row("3D edge", null, Color("hud.depth")));
        hud.Add(Row("3D depth", "0 is flat.", Slider("hud.depthSize", 0, 10, 1, "")));

        var effects = Section("Effects");
        effects.Add(
            Row(
                "Change animations",
                "MMR counts to the new number, wins and losses light up, and a new rank grows or dips.",
                Toggle("effects.animations")
            )
        );
    }

    private void SyncAll()
    {
        _syncing = true;
        foreach (var sync in _syncs)
            sync();
        _syncing = false;
    }

    private SectionList Section(string title, bool expanded = false)
    {
        var stack = new StackPanel { Spacing = 4, IsVisible = expanded };
        // A slim chevron: points down when open, right when closed.
        var chevron = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 1 3 L 6 8 L 11 3"),
            Stroke = new SolidColorBrush(Avalonia.Media.Color.Parse("#99ffffff")),
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 12,
            Height = 11,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = new RotateTransform(expanded ? 0 : -90),
        };

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,20"), Background = Brushes.Transparent };
        var label = new FormFieldLabel
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        headerRow.Children.Add(label);
        Grid.SetColumn(chevron, 2);
        headerRow.Children.Add(chevron);
        var header = new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            MinHeight = 44,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = headerRow,
        };
        header.Classes.Add("SettingsBlockStyle");
        header.Classes.Add("SettingsRowStyle");
        header.PointerPressed += (_, _) =>
        {
            stack.IsVisible = !stack.IsVisible;
            chevron.RenderTransform = new RotateTransform(stack.IsVisible ? 0 : -90);
        };

        OptionsHost.Children.Add(header);
        OptionsHost.Children.Add(stack);
        return new SectionList(stack);
    }

    /// <summary>A list that adds each row to its panel and styles it as the top or a middle row of the block.</summary>
    private sealed class SectionList(StackPanel panel) : List<Border>
    {
        public new void Add(Border row)
        {
            base.Add(row);
            panel.Children.Add(row);
            var first = Count == 1;
            row.Classes.Set("SettingsRowTop", first);
            row.Classes.Set("SettingsRowMiddle", !first);
        }
    }

    private static Border Row(string title, string? hint, Control control)
    {
        var text = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        text.Children.Add(new FormFieldLabel { Text = title });
        if (hint is not null)
            text.Children.Add(
                new TextBlock
                {
                    Text = hint,
                    TextWrapping = TextWrapping.Wrap,
                    Classes = { "TinyText" },
                }
            );

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 44 };
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        var row = new Border { Child = grid };
        row.Classes.Add("SettingsBlockStyle");
        row.Classes.Add("SettingsRowStyle");
        return row;
    }

    private Control Toggle(string path)
    {
        var toggle = new ToggleCheckBox { Variant = ToggleVariant.Switch };
        void Sync() => toggle.IsChecked = _config.GetBool(path);
        Sync();
        _syncs.Add(Sync);
        toggle.Click += (_, _) =>
        {
            if (!_syncing)
                _config.Set(path, JsonValue.Create(toggle.IsChecked == true));
        };
        return toggle;
    }

    private Control Slider(string path, double min, double max, double step, string unit)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Width = 140,
        };
        var value = new TextBlock
        {
            Width = 54,
            TextAlignment = TextAlignment.End,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "TinyText" },
        };
        string Format(double v) => (step < 1 ? v.ToString("0.##") : v.ToString("0")) + unit;
        void Sync()
        {
            slider.Value = _config.GetNumber(path);
            value.Text = Format(slider.Value);
        }

        Sync();
        _syncs.Add(Sync);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _syncing)
                return;
            var rounded = Math.Round(slider.Value / step) * step;
            value.Text = Format(rounded);
            _config.Set(path, JsonValue.Create(Math.Round(rounded, 4)));
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(slider);
        panel.Children.Add(value);
        return panel;
    }

    private Control Choice(string path, params string[] options)
    {
        var box = new Dropdown
        {
            ItemsSource = options,
            Width = 130,
            ZIndex = 2,
        };
        void Sync() => box.SelectedItem = _config.GetString(path);
        Sync();
        _syncs.Add(Sync);
        box.SelectionChanged += (_, _) =>
        {
            if (!_syncing && box.SelectedItem is string chosen)
                _config.Set(path, JsonValue.Create(chosen));
        };
        return box;
    }

    private Control Color(string path, string? shadePath = null)
    {
        var swatch = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#40ffffff")),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        var input = new InputField
        {
            Variant = InputFieldVariant.Bordered,
            Width = 104,
            Placeholder = "#rrggbb",
        };

        ToolTip.SetTip(swatch, "Pick a color");
        swatch.PointerPressed += async (_, _) =>
        {
            var current = (input.Text ?? string.Empty).Trim();
            var start = Avalonia.Media.Color.TryParse(current, out var parsed) ? parsed : Colors.White;
            var picked = await _popups.Create<ThemeColorPickerWindow>("Pick a color", start).AwaitColor();
            if (picked is { } color)
                input.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        };

        void Paint(string hex) =>
            swatch.Background = Avalonia.Media.Color.TryParse(hex, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;

        void Sync()
        {
            var current = _config.GetString(path);
            input.Text = current;
            Paint(current);
        }

        Sync();
        _syncs.Add(Sync);
        input.TextChanged += (_, _) =>
        {
            if (_syncing)
                return;
            var hex = (input.Text ?? string.Empty).Trim();
            Paint(hex);
            if (LoungeStatsConfig.IsHex(hex))
            {
                _config.Set(path, JsonValue.Create(LoungeStatsConfig.Normalize(hex)));
                if (shadePath is not null)
                {
                    _config.Set(shadePath, JsonValue.Create(HudArt.Mix(hex, "#000000", 0.25)));
                    SyncAll();
                }
            }
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(swatch);
        panel.Children.Add(input);
        return panel;
    }

    #endregion
}
