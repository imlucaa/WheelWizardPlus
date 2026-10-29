using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WheelWizard.NowPlaying;
using WheelWizard.Shared.Desktop.Storage;
using WheelWizard.Shared.Services;
using WheelWizard.Views.Components;
using WheelWizard.Views.Dialogs;
using WheelWizard.Views.Shell;

namespace WheelWizard.Settings.Views;

/// <summary>
/// The Now playing tab: a live card of the song playing on this PC, every option of how it looks, and the window and OBS
/// link for a stream. Works with Spotify, Apple Music, SoundCloud in a browser and the other players Windows can read.
/// </summary>
public partial class NowPlayingSettings : UserControl
{
    private readonly NowPlayingService _service;
    private readonly NowPlayingConfig _config;
    private readonly IPopupFactory _popups;
    private readonly IFilePickerService _filePicker;
    private StackPanel _presetGrid = null!;
    private readonly NowPlayingCard _card = new();
    private readonly List<Action> _syncs = [];
    private readonly List<(NowPlayingPreset Preset, Border Tile)> _presetTiles = [];
    private bool _syncing;
    private IDisposable? _lease;

    public NowPlayingSettings(NowPlayingService service, NowPlayingConfig config, IPopupFactory popups, IFilePickerService filePicker)
    {
        _filePicker = filePicker;
        _service = service;
        _config = config;
        _popups = popups;
        InitializeComponent();
        PreviewHost.Content = _card;
        LinkText.Text = NowPlayingPage.Url;
        BuildOptions();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _lease = _service.Use();
        _service.Changed += OnChanged;
        _config.Changed += OnChanged;
        NowPlayingWindow.OpenChanged += UpdateWindowButton;
        UpdateWindowButton();
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _service.Changed -= OnChanged;
        _config.Changed -= OnChanged;
        NowPlayingWindow.OpenChanged -= UpdateWindowButton;
        _lease?.Dispose();
        _lease = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged() => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        UpdatePresetTiles();
        _card.Show(_service.Current, _config, preview: true);
        // Say why nothing shows when the song cannot be read, instead of leaving a card that says nothing is playing.
        var problem = _service.IsSupported ? _service.Problem : "Reading the song works on Windows for now.";
        StatusText.Text = problem ?? string.Empty;
        StatusText.IsVisible = problem is not null;
        StatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse(_service.IsSupported ? "#FF6D7E" : "#9AA3BB"));
    }

    private void UpdateWindowButton() => WindowButton.Text = NowPlayingWindow.Current is null ? "Open as window" : "Close window";

    private void OpenWindow_OnClick(object? sender, RoutedEventArgs e)
    {
        if (NowPlayingWindow.Current is not null)
            NowPlayingWindow.CloseIfOpen();
        else
            NowPlayingWindow.ShowOrActivate(_service, _config);
    }

    private async void Copy_OnClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(NowPlayingPage.Url);

        ViewUtils.ShowSnackbar("Link copied. Paste it into an OBS Browser source.");
    }

    private void Reset_OnClick(object? sender, RoutedEventArgs e)
    {
        _config.Reset();
        SyncAll();
    }

    #region Options

    private void BuildOptions()
    {
        var presets = Section("Presets", expanded: true);
        presets.Add(PresetTiles());

        var style = Section("Style");
        style.Add(
            Row(
                "Style",
                "Classic has the cards and the sound bar, Vertical puts the cover on top, Compact is one card, Strip one line, Text has no cards.",
                Choice("layout.style", NowPlayingConfig.Styles)
            )
        );
        style.Add(
            Row(
                "Cover side",
                "Left or right of the cards, or above and below in Vertical.",
                Choice("layout.coverSide", NowPlayingConfig.CoverSides)
            )
        );
        style.Add(Row("Cover shape", null, Choice("layout.coverShape", NowPlayingConfig.CoverShapes)));
        style.Add(Row("Cover size", null, Slider("layout.coverSize", 40, 220, 2, " px")));
        style.Add(Row("Width", null, Slider("layout.width", 240, 700, 10, " px")));
        style.Add(Row("Size", "Scales the whole card.", Slider("layout.scale", 0.5, 2.5, 0.05, "x")));
        style.Add(Row("Roundness", null, Slider("layout.radius", 0, 24, 1, " px")));
        style.Add(Row("Spacing", "The gap between the cover and the cards.", Slider("layout.gap", 0, 40, 1, " px")));
        style.Add(Row("Text size", null, Slider("layout.textSize", 0.6, 2, 0.05, "x")));
        style.Add(Row("Text align", null, Choice("layout.align", NowPlayingConfig.Aligns)));
        style.Add(Row("Font", null, Choice("layout.fontFamily", NowPlayingConfig.FontFamilies)));
        style.Add(Row("Title weight", null, Choice("layout.titleWeight", NowPlayingConfig.TitleWeights)));
        style.Add(Row("Title in capitals", null, Toggle("layout.uppercase")));

        var show = Section("Show");
        foreach (
            var (path, label) in new[]
            {
                ("cover", "Album cover"),
                ("artist", "Artist"),
                ("app", "Name of the app"),
                ("time", "Time played and length"),
                ("album", "Album name"),
                ("remaining", "Time left instead of length"),
                ("paused", "Say Paused"),
            }
        )
            show.Add(Row(label, null, Toggle($"show.{path}")));

        var bars = Section("Sound bar and line", expanded: true);
        bars.Add(Row("Sound bar", "The moving bars next to the time. Off hides them.", Toggle("show.bars")));
        bars.Add(Row("Progress line", "The line that fills as the song plays. Off hides it.", Toggle("show.progress")));
        bars.Add(Row("Bars", "How many bars the sound bar has.", Slider("layout.barCount", 6, 48, 1, "")));
        bars.Add(Row("Bar color", null, Color("colors.bars")));
        bars.Add(Row("Line thickness", null, Slider("layout.progressHeight", 2, 14, 1, " px")));
        bars.Add(
            Row("Line position", "Under the cards or above them.", Choice("layout.progressPosition", NowPlayingConfig.ProgressPositions))
        );

        var colors = Section("Colors");
        colors.Add(
            Row("Card color", "From the album cover, or one color you pick.", Choice("colors.source", NowPlayingConfig.ColorSources))
        );
        colors.Add(Row("Your color", "Used when the card color is custom.", Color("colors.custom")));
        colors.Add(Row("Card strength", null, Slider("colors.cardOpacity", 0.2, 1, 0.02, "")));
        colors.Add(Row("Text", null, Color("colors.text")));
        colors.Add(Row("Progress line", null, Color("colors.progress")));

        var effects = Section("Effects");
        effects.Add(Row("Glow", "A soft glow around the cover in the card color.", Toggle("effects.glow")));
        effects.Add(Row("Spinning cover", "A round cover turns like a record while the song plays.", Toggle("effects.spin")));
        effects.Add(Row("Text shadow", "A dark shadow behind the words, easier to read on a busy scene.", Toggle("effects.textShadow")));
        effects.Add(Row("Card shadow", null, Toggle("effects.cardShadow")));
        effects.Add(Row("Gradient cards", "The cards fade from the card color to a darker one.", Toggle("effects.gradient")));
        effects.Add(Row("Border", null, Slider("effects.borderWidth", 0, 8, 1, " px")));
        effects.Add(Row("Border color", null, Color("effects.borderColor")));

        var behavior = Section("When it shows");
        behavior.Add(
            Row(
                "Hide when nothing plays",
                "The card fades out while the music is paused or stopped.",
                Toggle("behavior.hideWhenNotPlaying")
            )
        );
    }

    // The ready-made looks and your saved ones as tiles (the one that matches the settings is outlined), with buttons to
    // save the current look, and to export or import one as a file.
    private Border PresetTiles()
    {
        _presetGrid = new StackPanel();
        RebuildPresetTiles();

        var save = new WheelWizard.Views.Components.Button
        {
            Text = "Save this look",
            ButtonSize = WheelWizard.Views.Components.Button.ButtonsSizeType.Compact,
        };
        save.Click += async (_, _) => await SaveLookAsync();
        var export = new WheelWizard.Views.Components.Button
        {
            Text = "Export",
            ButtonSize = WheelWizard.Views.Components.Button.ButtonsSizeType.Compact,
        };
        export.Click += async (_, _) => await ExportAsync();
        var import = new WheelWizard.Views.Components.Button
        {
            Text = "Import",
            ButtonSize = WheelWizard.Views.Components.Button.ButtonsSizeType.Compact,
        };
        import.Click += async (_, _) => await ImportAsync();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 6, 0, 0),
        };
        buttons.Children.Add(save);
        buttons.Children.Add(export);
        buttons.Children.Add(import);

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(_presetGrid);
        panel.Children.Add(buttons);
        var row = new Border { Child = panel, Padding = new Thickness(0, 4) };
        row.Classes.Add("SettingsBlockStyle");
        row.Classes.Add("SettingsRowStyle");
        return row;
    }

    private void RebuildPresetTiles()
    {
        _presetTiles.Clear();
        _presetGrid.Children.Clear();
        _presetGrid.Children.Add(TileGrid(NowPlayingPresets.All, deletable: false));
        var saved = _config.SavedLooks();
        if (saved.Count > 0)
        {
            _presetGrid.Children.Add(
                new TextBlock
                {
                    Text = "Your looks",
                    Classes = { "TinyText" },
                    Margin = new Thickness(4, 8, 0, 2),
                }
            );
            _presetGrid.Children.Add(TileGrid(saved, deletable: true));
        }

        UpdatePresetTiles();
    }

    private UniformGrid TileGrid(IReadOnlyList<NowPlayingPreset> presets, bool deletable)
    {
        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(-3, 0) };
        foreach (var preset in presets)
        {
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(
                new TextBlock
                {
                    Text = preset.Name,
                    FontWeight = FontWeight.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }
            );
            text.Children.Add(
                new TextBlock
                {
                    Text = preset.Description,
                    TextWrapping = TextWrapping.Wrap,
                    Classes = { "TinyText" },
                }
            );
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
            content.Children.Add(text);
            if (deletable)
            {
                var delete = new TextBlock
                {
                    Text = "✕",
                    FontSize = 12,
                    Opacity = 0.6,
                    VerticalAlignment = VerticalAlignment.Top,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                ToolTip.SetTip(delete, "Delete this look");
                var name = preset.Name;
                delete.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    _config.DeleteLook(name);
                    RebuildPresetTiles();
                };
                Grid.SetColumn(delete, 1);
                content.Children.Add(delete);
            }

            var tile = new Border
            {
                Margin = new Thickness(3),
                Padding = new Thickness(10, 8),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1.5),
                BorderBrush = Brushes.Transparent,
                Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#14FFFFFF")),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = content,
            };
            var chosen = preset;
            tile.PointerPressed += (_, e) =>
            {
                if (e.Handled || !e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed)
                    return;
                _config.Apply(chosen.Values);
                SyncAll();
            };
            _presetTiles.Add((preset, tile));
            grid.Children.Add(tile);
        }

        return grid;
    }

    private void SyncAll()
    {
        _syncing = true;
        foreach (var sync in _syncs)
            sync();
        _syncing = false;
    }

    private void UpdatePresetTiles()
    {
        var accent = this.TryFindResource("Brand400", ActualThemeVariant, out var found) && found is IBrush brush ? brush : Brushes.White;
        foreach (var (preset, tile) in _presetTiles)
            tile.BorderBrush = _config.Matches(preset.Values) ? accent : Brushes.Transparent;
    }

    private async Task SaveLookAsync()
    {
        var input = await _popups
            .Create<TextInputWindow>()
            .SetMainText("Name this look")
            .SetExtraText("Keeps the card's style, options and colors as they are now. A name you already used is replaced.")
            .SetPlaceholderText("My look")
            .SetButtonText("Cancel", "Save")
            .ShowDialog();
        var name = input?.Trim();
        if (string.IsNullOrEmpty(name))
            return;

        if (_config.SaveLook(name))
        {
            RebuildPresetTiles();
            ViewUtils.ShowSnackbar($"Saved as {name}.");
        }
        else
        {
            ViewUtils.ShowSnackbar(
                $"A name can be 1 to 30 characters, and you can keep {NowPlayingConfig.MaxSavedLooks} looks.",
                ViewUtils.SnackbarType.Warning
            );
        }
    }

    private static FilePickerFileType LookFile => new("Now playing look") { Patterns = ["*.json"], MimeTypes = ["application/json"] };

    private async Task ExportAsync()
    {
        var path = await _filePicker.SaveFileAsync("Export this look", [LookFile], "now-playing-look");
        if (path is null)
            return;
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            path += ".json";
        try
        {
            await File.WriteAllTextAsync(path, _config.Export());
            ViewUtils.ShowSnackbar("Look exported.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewUtils.ShowSnackbar("Could not save that file.", ViewUtils.SnackbarType.Danger);
        }
    }

    private async Task ImportAsync()
    {
        var path = await _filePicker.OpenSingleFileAsync("Import a look", [LookFile]);
        if (path is null)
            return;

        bool ok;
        try
        {
            ok = _config.TryImport(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ok = false;
        }

        if (ok)
        {
            SyncAll();
            ViewUtils.ShowSnackbar("Look imported. Use Save this look to keep it.");
        }
        else
        {
            ViewUtils.ShowSnackbar("That file is not a Now playing look.", ViewUtils.SnackbarType.Danger);
        }
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

    private Control Color(string path)
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
            if (NowPlayingConfig.IsHex(hex))
                _config.Set(path, JsonValue.Create(NowPlayingConfig.Normalize(hex)));
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(swatch);
        panel.Children.Add(input);
        return panel;
    }

    #endregion
}
