using System.IO.Abstractions;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WheelWizard.ApplicationData;
using WheelWizard.Settings;
using WheelWizard.Shared.Desktop.Storage;
using WheelWizard.Themes;
using WheelWizard.Views.Components;
using WheelWizard.Views.Dialogs;

namespace WheelWizard.Views.Pages;

public sealed record LauncherThemeFile(string Name, string Accent, string Branding, string Text, string Background)
{
    public bool HasValidNameAndColors() =>
        !string.IsNullOrWhiteSpace(Name) && new[] { Accent, Branding, Text, Background }.All(LauncherThemeService.IsValidHexColor);
}

public static class LauncherThemeShareCode
{
    private const string Prefix = "BWW1";

    public static string Encode(LauncherThemeFile theme) =>
        $"{Prefix}:{WithoutHash(theme.Accent)}:{WithoutHash(theme.Branding)}:{WithoutHash(theme.Text)}:{WithoutHash(theme.Background)}";

    public static bool TryDecode(string? code, out LauncherThemeFile theme)
    {
        theme = new LauncherThemeFile(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var parts = code.Trim().Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 5 || !string.Equals(parts[0], Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var colors = parts.Skip(1).Select(value => $"#{WithoutHash(value).ToUpperInvariant()}").ToArray();
        if (!colors.All(LauncherThemeService.IsValidHexColor))
            return false;

        theme = new LauncherThemeFile(string.Empty, colors[0], colors[1], colors[2], colors[3]);
        return true;
    }

    private static string WithoutHash(string value) => value.Trim().TrimStart('#');
}

public partial class ThemesPage : UserControl
{
    private sealed record ThemeChoice(string DisplayName, LauncherThemeFile Theme, string? FilePath, bool IsBuiltIn)
    {
        public override string ToString() => DisplayName;
    }

    private static readonly LauncherThemeFile[] BuiltInThemes =
    [
        new(
            LauncherThemeDefaults.Name,
            LauncherThemeDefaults.Accent,
            LauncherThemeDefaults.Branding,
            LauncherThemeDefaults.Text,
            LauncherThemeDefaults.Background
        ),
        new("Red", "#F04444", "#F04444", "#F8DADA", "#260909"),
        new("Orange", "#FF8A3D", "#FF8A3D", "#FFE0CC", "#281006"),
        new("Yellow", "#FFD80D", "#FFD80D", "#FFF4B8", "#211B02"),
        new("Green", "#25C982", "#25C982", "#D0F5E3", "#062418"),
        new("Cyan", "#20D4E8", "#20D4E8", "#D0F8FC", "#05252A"),
        new("Blue", "#249AF3", "#249AF3", "#DDF2FF", "#071A2D"),
        new("Purple", "#9B6DFF", "#9B6DFF", "#E4D9FF", "#160A2C"),
        new("Pink", "#FF4FA3", "#FF4FA3", "#FFD8EA", "#290817"),
        new("Coral", "#FF6B6B", "#FF6B6B", "#FFE1E1", "#271011"),
        new("Peach", "#FF9F68", "#FF9F68", "#FFE8D8", "#28160C"),
        new("Mint", "#52D6A8", "#52D6A8", "#D9F8ED", "#09251D"),
        new("Emerald", "#10B981", "#10B981", "#D1FAE5", "#05251A"),
        new("Teal", "#14B8A6", "#14B8A6", "#CCFBF1", "#052522"),
        new("Sky", "#38BDF8", "#38BDF8", "#DDF5FF", "#082330"),
        new("Indigo", "#6366F1", "#6366F1", "#E0E1FF", "#11132E"),
        new("Lavender", "#A78BFA", "#A78BFA", "#EDE7FF", "#1B1230"),
        new("Violet", "#8B5CF6", "#8B5CF6", "#E8DEFF", "#170C2D"),
        new("Rose", "#F43F5E", "#F43F5E", "#FFE0E6", "#2A0810"),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly IFileSystem _fileSystem;
    private readonly IPopupFactory _popups;
    private readonly ILauncherBackgroundService _backgrounds;
    private readonly IFilePickerService _filePicker;
    private bool _loadingAppearance;
    private string ThemesFolderPath { get; }
    private string DeletedBuiltInThemesPath => _fileSystem.Path.Combine(ThemesFolderPath, "deleted-built-in-themes.json");
    private readonly List<ThemeChoice> _themeChoices = [];
    private ThemeChoice? _selectedTheme;
    private string _editorThemeName = string.Empty;
    private bool _isLoadingTheme;

    private ISettingsManager SettingsService { get; }

    public ThemesPage(
        ISettingsManager settingsService,
        IApplicationDataLocation applicationData,
        IFileSystem fileSystem,
        IPopupFactory popups,
        ILauncherBackgroundService backgrounds,
        IFilePickerService filePicker
    )
    {
        SettingsService = settingsService;
        _fileSystem = fileSystem;
        _popups = popups;
        _backgrounds = backgrounds;
        _filePicker = filePicker;
        ThemesFolderPath = fileSystem.Path.Combine(applicationData.DirectoryPath, "Themes");
        InitializeComponent();
        MakeCollapsible();
        LoadCurrentTheme();
        ReloadThemeChoices();
        UpdatePreview();
        LoadAppearance();
    }

    private void LoadAppearance()
    {
        _loadingAppearance = true;
        try
        {
            var state = _backgrounds.Current;
            BackgroundFileText.Text = state.Path == null ? t("theme.no_background") : _fileSystem.Path.GetFileName(state.Path);
            BackgroundOpacitySlider.Value = SettingsService.LAUNCHER_BACKGROUND_OPACITY.Get();
            SurfaceOpacitySlider.Value = SettingsService.LAUNCHER_SURFACE_OPACITY.Get();
            BackgroundBlurSlider.Value = SettingsService.LAUNCHER_BACKGROUND_BLUR.Get();
            BackgroundZoomSlider.Value = SettingsService.LAUNCHER_BACKGROUND_ZOOM.Get();
            BackgroundFocusXSlider.Value = SettingsService.LAUNCHER_BACKGROUND_FOCUS_X.Get();
            BackgroundFocusYSlider.Value = SettingsService.LAUNCHER_BACKGROUND_FOCUS_Y.Get();
            ImagePaletteButton.IsEnabled = state.Path != null;
            MinimalHomeArtworkCheckBox.IsChecked = SettingsService.LAUNCHER_MINIMAL_HOME_ARTWORK.Get();
            HideHomeTitleCheckBox.IsChecked = SettingsService.LAUNCHER_HIDE_HOME_TITLE.Get();
            CleanSidebarCheckBox.IsChecked = SettingsService.LAUNCHER_CLEAN_SIDEBAR.Get();
            ProfileOutlineCheckBox.IsChecked = SettingsService.LAUNCHER_PROFILE_OUTLINE.Get();
            ProfileOutlineOpacitySlider.Value = SettingsService.LAUNCHER_PROFILE_OUTLINE_OPACITY.Get();
            ProfileOutlineOpacitySlider.IsEnabled = ProfileOutlineCheckBox.IsChecked == true;
            FontFileText.Text = string.IsNullOrWhiteSpace(SettingsService.LAUNCHER_FONT.Get())
                ? t("theme.default")
                : SettingsService.LAUNCHER_FONT.Get();
            var stretch = SettingsService.LAUNCHER_BACKGROUND_STRETCH.Get();
            BackgroundFitBox.SelectedItem =
                BackgroundFitBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, stretch))
                ?? BackgroundFitBox.Items[0];
        }
        finally
        {
            _loadingAppearance = false;
        }
    }

    private async void ChooseBackground_OnClick(object? sender, RoutedEventArgs e)
    {
        var types = new[]
        {
            new FilePickerFileType(t("theme.images")) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"] },
        };
        var path = await _filePicker.OpenSingleFileAsync(t("theme.choose_background"), types);
        if (path == null)
            return;
        await RunBackgroundAction(async () => await _backgrounds.ImportFileAsync(path), t("theme.background_imported"));
    }

    private async void BackgroundUrl_OnClick(object? sender, RoutedEventArgs e)
    {
        var url = await _popups
            .Create<TextInputWindow>()
            .SetMainText(t("theme.background_url"))
            .SetExtraText(t("theme.background_url_description"))
            .SetPlaceholderText("https://example.com/background.gif")
            .SetButtonText(t("action.cancel"), t("action.import"))
            .ShowDialog();
        if (url == null)
            return;
        await RunBackgroundAction(async () => await _backgrounds.ImportUrlAsync(url), t("theme.background_downloaded"));
    }

    private void RemoveBackground_OnClick(object? sender, RoutedEventArgs e)
    {
        _backgrounds.Remove();
        BackgroundStatusText.Text = t("theme.background_removed");
        LoadAppearance();
    }

    private async Task RunBackgroundAction(Func<Task> action, string success)
    {
        try
        {
            await action();
            BackgroundStatusText.Text = success;
            LoadAppearance();
        }
        catch (Exception ex)
        {
            BackgroundStatusText.Text = ex.Message;
        }
    }

    private void BackgroundFit_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingAppearance || BackgroundFitBox.SelectedItem is not ComboBoxItem { Tag: string value })
            return;
        SettingsService.LAUNCHER_BACKGROUND_STRETCH.Set(value);
    }

    private void BackgroundOpacity_OnChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_loadingAppearance)
            SettingsService.LAUNCHER_BACKGROUND_OPACITY.Set(e.NewValue);
    }

    private void MoreImageOptions_OnClick(object? sender, RoutedEventArgs e)
    {
        MoreImageOptions.IsVisible = !MoreImageOptions.IsVisible;
        MoreImageOptionsButton.Text = MoreImageOptions.IsVisible ? "Fewer image options" : "More image options";
    }

    private void BackgroundBlur_OnChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_loadingAppearance)
            SettingsService.LAUNCHER_BACKGROUND_BLUR.Set(e.NewValue);
    }

    private void BackgroundZoom_OnChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_loadingAppearance)
            SettingsService.LAUNCHER_BACKGROUND_ZOOM.Set(e.NewValue);
    }

    private void BackgroundFocusX_OnChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_loadingAppearance)
            SettingsService.LAUNCHER_BACKGROUND_FOCUS_X.Set(e.NewValue);
    }

    private void BackgroundFocusY_OnChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_loadingAppearance)
            SettingsService.LAUNCHER_BACKGROUND_FOCUS_Y.Set(e.NewValue);
    }

    /// <summary>Fills the four colors from the image that is set as the background, as an unsaved theme.</summary>
    private async void ImagePalette_OnClick(object? sender, RoutedEventArgs e)
    {
        var path = _backgrounds.Current.Path;
        if (path == null)
        {
            BackgroundStatusText.Text = "Choose a background image first.";
            return;
        }

        ImagePaletteButton.IsEnabled = false;
        try
        {
            var colors = await Task.Run(() => ImagePalette.FromFile(path));
            if (colors is null)
            {
                BackgroundStatusText.Text = "Could not read colors from this image.";
                return;
            }

            LoadColorsIntoEditor(
                colors.Value.Accent,
                colors.Value.Branding,
                colors.Value.Text,
                colors.Value.Background,
                "Colors from image"
            );
            ThemeStatusText.Text = "Colors taken from your image. Preview them, then Apply or Save.";
            BackgroundStatusText.Text = "Colors taken from the image.";
        }
        finally
        {
            ImagePaletteButton.IsEnabled = _backgrounds.Current.Path != null;
        }
    }

    private void SurfaceOpacity_OnChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_loadingAppearance)
            SettingsService.Set(SettingsService.LAUNCHER_SURFACE_OPACITY, e.NewValue);
    }

    private void ProfileOutlineOpacity_OnChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_loadingAppearance)
            SettingsService.Set(SettingsService.LAUNCHER_PROFILE_OUTLINE_OPACITY, e.NewValue);
    }

    private void AppearanceToggle_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingAppearance)
            return;
        SettingsService.Set(SettingsService.LAUNCHER_MINIMAL_HOME_ARTWORK, MinimalHomeArtworkCheckBox.IsChecked == true);
        SettingsService.Set(SettingsService.LAUNCHER_HIDE_HOME_TITLE, HideHomeTitleCheckBox.IsChecked == true);
        SettingsService.Set(SettingsService.LAUNCHER_CLEAN_SIDEBAR, CleanSidebarCheckBox.IsChecked == true);
        SettingsService.Set(SettingsService.LAUNCHER_PROFILE_OUTLINE, ProfileOutlineCheckBox.IsChecked == true);
        ProfileOutlineOpacitySlider.IsEnabled = ProfileOutlineCheckBox.IsChecked == true;
    }

    private async void ImportFont_OnClick(object? sender, RoutedEventArgs e)
    {
        var types = new[] { new FilePickerFileType(t("theme.fonts")) { Patterns = ["*.ttf", "*.otf"] } };
        var path = await _filePicker.OpenSingleFileAsync(t("theme.choose_font"), types);
        if (path == null)
            return;
        try
        {
            var folder = _fileSystem.Path.Combine(_fileSystem.Path.GetDirectoryName(ThemesFolderPath)!, "Appearance", "Fonts");
            _fileSystem.Directory.CreateDirectory(folder);
            var fileName = $"font-{Guid.NewGuid():N}{_fileSystem.Path.GetExtension(path).ToLowerInvariant()}";
            _fileSystem.File.Copy(path, _fileSystem.Path.Combine(folder, fileName), true);
            SettingsService.LAUNCHER_FONT.Set(fileName);
            BackgroundStatusText.Text = t("theme.font_imported");
            LoadAppearance();
        }
        catch (Exception ex)
        {
            BackgroundStatusText.Text = ex.Message;
        }
    }

    private void ResetFont_OnClick(object? sender, RoutedEventArgs e)
    {
        SettingsService.LAUNCHER_FONT.Set(string.Empty);
        BackgroundStatusText.Text = t("theme.font_reset");
        LoadAppearance();
    }

    private void ReloadThemeChoices(string? selectName = null)
    {
        _themeChoices.Clear();
        _fileSystem.Directory.CreateDirectory(ThemesFolderPath);
        var deletedBuiltIns = LoadDeletedBuiltInThemes();
        RestorePresetsButton.IsVisible = deletedBuiltIns.Count > 0;
        foreach (var theme in BuiltInThemes.Where(theme => !deletedBuiltIns.Contains(theme.Name)))
            _themeChoices.Add(new ThemeChoice(FormatThemeName(theme, true), theme, null, true));

        foreach (
            var file in _fileSystem
                .Directory.EnumerateFiles(ThemesFolderPath, "*.bwwtheme")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        )
        {
            var theme = TryReadTheme(file);
            if (theme != null)
                _themeChoices.Add(new ThemeChoice(FormatThemeName(theme, false), theme, file, false));
        }

        if (!string.IsNullOrWhiteSpace(selectName))
            SelectTheme(_themeChoices.LastOrDefault(item => item.Theme.Name == selectName), loadEditor: false);
    }

    private void LoadCurrentTheme()
    {
        ColorTextBox.Text = SettingsService.Get<string>(SettingsService.LAUNCHER_THEME_COLOR);
        BrandColorTextBox.Text = SettingsService.Get<string>(SettingsService.LAUNCHER_TEXT_COLOR);
        TextColorTextBox.Text = SettingsService.Get<string>(SettingsService.LAUNCHER_BODY_TEXT_COLOR);
        BackgroundColorTextBox.Text = SettingsService.Get<string>(SettingsService.LAUNCHER_BACKGROUND_COLOR);
    }

    private async void ChooseTheme_OnClick(object? sender, RoutedEventArgs e)
    {
        // Each row shows the theme's own accent, text and background colors.
        var swatches = _themeChoices.Select(choice => new[] { choice.Theme.Accent, choice.Theme.Text, choice.Theme.Background }).ToArray();
        var selectedIndex = await _popups
            .CreateThemeLibrary(_themeChoices.Select(choice => choice.DisplayName).ToArray(), swatches)
            .AwaitAnswer();
        if (selectedIndex is not >= 0 || selectedIndex >= _themeChoices.Count)
            return;

        SelectTheme(_themeChoices[selectedIndex.Value], loadEditor: true);
    }

    private void SelectTheme(ThemeChoice? choice, bool loadEditor)
    {
        _selectedTheme = choice;
        DeleteThemeButton.IsEnabled = choice != null;
        SelectedThemeText.Text = choice?.Theme.Name ?? t("theme.current_colors");
        if (choice == null || !loadEditor)
            return;

        SetEditor(choice.Theme);
        ThemeStatusText.Text = t("theme.loaded", new { name = choice.Theme.Name });
    }

    private async void DeleteTheme_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedTheme is not ThemeChoice choice)
            return;

        var confirmed = await _popups
            .Create<YesNoWindow>()
            .SetMainText(t("theme.delete_confirm", new { name = choice.Theme.Name }))
            .SetExtraText(choice.IsBuiltIn ? t("theme.restore_builtin_later") : t("theme.saved_theme_removed"))
            .SetButtonText(t("action.delete"), t("action.cancel"))
            .SetButtonTones(ActionButtonTone.Danger, ActionButtonTone.Secondary)
            .AwaitAnswer();
        if (!confirmed)
            return;

        if (choice.IsBuiltIn)
        {
            var deletedBuiltIns = LoadDeletedBuiltInThemes();
            deletedBuiltIns.Add(choice.Theme.Name);
            _fileSystem.File.WriteAllText(DeletedBuiltInThemesPath, JsonSerializer.Serialize(deletedBuiltIns.Order(), JsonOptions));
        }
        else if (choice.FilePath != null)
        {
            _fileSystem.File.Delete(choice.FilePath);
        }
        _selectedTheme = null;
        ReloadThemeChoices();
        DeleteThemeButton.IsEnabled = false;
        SelectedThemeText.Text = t("theme.current_colors");
        ThemeStatusText.Text = t("theme.deleted", new { name = choice.Theme.Name });
    }

    private async void RestorePresets_OnClick(object? sender, RoutedEventArgs e)
    {
        var confirmed = await _popups
            .Create<YesNoWindow>()
            .SetMainText(t("theme.restore_all"))
            .SetExtraText(t("theme.restore_all_description"))
            .SetButtonText(t("theme.restore"), t("action.cancel"))
            .SetButtonTones(ActionButtonTone.Brand, ActionButtonTone.Secondary)
            .AwaitAnswer();
        if (!confirmed)
            return;

        if (_fileSystem.File.Exists(DeletedBuiltInThemesPath))
            _fileSystem.File.Delete(DeletedBuiltInThemesPath);
        ReloadThemeChoices();
        ThemeStatusText.Text = t("theme.restored");
    }

    private void SetEditor(LauncherThemeFile theme)
    {
        _isLoadingTheme = true;
        try
        {
            _editorThemeName = theme.Name;
            ColorTextBox.Text = theme.Accent;
            BrandColorTextBox.Text = theme.Branding;
            TextColorTextBox.Text = theme.Text;
            BackgroundColorTextBox.Text = theme.Background;
        }
        finally
        {
            _isLoadingTheme = false;
        }
        UpdatePreview();
    }

    private void ApplyTheme_OnClick(object? sender, RoutedEventArgs e)
    {
        var theme = ReadEditor(requireName: false);
        if (theme == null)
            return;
        SettingsService.Set(SettingsService.LAUNCHER_THEME_COLOR, theme.Accent);
        SettingsService.Set(SettingsService.LAUNCHER_TEXT_COLOR, theme.Branding);
        SettingsService.Set(SettingsService.LAUNCHER_BODY_TEXT_COLOR, theme.Text);
        SettingsService.Set(SettingsService.LAUNCHER_BACKGROUND_COLOR, theme.Background);
        var appliedName = string.IsNullOrWhiteSpace(theme.Name) ? t("theme.unnamed") : theme.Name;
        ThemeStatusText.Text = t("theme.applied", new { name = appliedName });
    }

    private PreviewHomeWindow? _homePreview;

    private void PreviewHome_OnClick(object? sender, RoutedEventArgs e)
    {
        var theme = ReadEditor(requireName: false);
        if (theme == null)
            return;
        if (_homePreview?.IsOpen == true)
        {
            RefreshOpenPreview();
            _homePreview.Focus();
            return;
        }

        _homePreview = new PreviewHomeWindow(
            theme,
            _backgrounds.Current,
            SurfaceOpacitySlider.Value,
            MinimalHomeArtworkCheckBox.IsChecked == true,
            HideHomeTitleCheckBox.IsChecked == true
        );
        _homePreview.Show();
    }

    private async void SaveTheme_OnClick(object? sender, RoutedEventArgs e)
    {
        var requestedName = await _popups
            .Create<TextInputWindow>()
            .SetMainText(t("theme.save_colors"))
            .SetExtraText(t("theme.save_colors_description"))
            .SetPlaceholderText(t("theme.name_placeholder"))
            .SetInitialText(_editorThemeName)
            .SetButtonText(t("action.cancel"), t("action.save"))
            .ShowDialog();
        if (requestedName == null)
            return;

        _editorThemeName = requestedName.Trim();
        var theme = ReadEditor(requireName: true);
        if (theme == null)
            return;
        _fileSystem.Directory.CreateDirectory(ThemesFolderPath);
        var existing = FindSavedTheme(theme.Name);
        if (existing != null)
        {
            var replace = await _popups
                .Create<YesNoWindow>()
                .SetMainText(t("theme.replace_confirm", new { name = theme.Name }))
                .SetExtraText(t("theme.replace_description"))
                .SetButtonText(t("theme.replace"), t("action.cancel"))
                .SetButtonTones(ActionButtonTone.Brand, ActionButtonTone.Secondary)
                .AwaitAnswer();
            if (!replace)
                return;
        }
        var path = existing ?? _fileSystem.Path.Combine(ThemesFolderPath, $"{MakeSafeFileName(theme.Name)}.bwwtheme");
        _fileSystem.File.WriteAllText(path, JsonSerializer.Serialize(theme, JsonOptions));
        ReloadThemeChoices(theme.Name);
        SelectedThemeText.Text = theme.Name;
        ThemeStatusText.Text = t(existing == null ? "theme.saved" : "theme.updated", new { name = theme.Name });
    }

    private async void CopyThemeCode_OnClick(object? sender, RoutedEventArgs e)
    {
        var theme = ReadEditor(requireName: false);
        if (theme == null)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
        {
            ThemeStatusText.Text = t("theme.clipboard_unavailable");
            return;
        }

        await clipboard.SetTextAsync(LauncherThemeShareCode.Encode(theme));
        ThemeStatusText.Text = t("theme.code_copied");
    }

    private async void PasteThemeCode_OnClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        var code = clipboard == null ? null : await clipboard.TryGetTextAsync();
        if (!LauncherThemeShareCode.TryDecode(code, out var theme))
        {
            ThemeStatusText.Text = t("theme.invalid_code");
            return;
        }

        LoadColorsIntoEditor(theme.Accent, theme.Branding, theme.Text, theme.Background, t("theme.imported_colors"));
        ThemeStatusText.Text = t("theme.code_loaded");
    }

    private void RandomTheme_OnClick(object? sender, RoutedEventArgs e)
    {
        var colors = WheelWizard.Themes.RandomTheme.Create(Random.Shared);
        LoadColorsIntoEditor(colors.Accent, colors.Branding, colors.Text, colors.Background, "Random colors");
        ThemeStatusText.Text = "Random colors. Press again for another, or Preview to see them.";
    }

    /// <summary>Puts four colors into the editor as an unsaved theme, so nothing already saved is overwritten.</summary>
    private void LoadColorsIntoEditor(string accent, string branding, string text, string background, string label)
    {
        ColorTextBox.Text = accent;
        BrandColorTextBox.Text = branding;
        TextColorTextBox.Text = text;
        BackgroundColorTextBox.Text = background;
        _editorThemeName = string.Empty;
        _selectedTheme = null;
        SelectedThemeText.Text = label;
        DeleteThemeButton.IsEnabled = false;
        UpdatePreview();
    }

    private LauncherThemeFile? ReadEditor(bool requireName)
    {
        var name = _editorThemeName.Trim();
        var theme = new LauncherThemeFile(
            name,
            NormalizeHex(ColorTextBox.Text),
            NormalizeHex(BrandColorTextBox.Text),
            NormalizeHex(TextColorTextBox.Text),
            NormalizeHex(BackgroundColorTextBox.Text)
        );
        ColorError.IsVisible = (requireName && string.IsNullOrWhiteSpace(name)) || !IsValidTheme(theme);
        return ColorError.IsVisible ? null : theme;
    }

    private LauncherThemeFile? TryReadTheme(string path)
    {
        try
        {
            var theme = JsonSerializer.Deserialize<LauncherThemeFile>(_fileSystem.File.ReadAllText(path), JsonOptions);
            return theme?.HasValidNameAndColors() == true ? theme : null;
        }
        catch
        {
            return null;
        }
    }

    private HashSet<string> LoadDeletedBuiltInThemes()
    {
        try
        {
            if (!_fileSystem.File.Exists(DeletedBuiltInThemesPath))
                return new(StringComparer.OrdinalIgnoreCase);
            var names = JsonSerializer.Deserialize<string[]>(_fileSystem.File.ReadAllText(DeletedBuiltInThemesPath), JsonOptions) ?? [];
            return new(names, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsValidTheme(LauncherThemeFile theme) =>
        new[] { theme.Accent, theme.Branding, theme.Text, theme.Background }.All(LauncherThemeService.IsValidHexColor);

    private string FormatThemeName(LauncherThemeFile theme, bool isBuiltIn)
    {
        var suffix = isBuiltIn ? t("theme.builtin_suffix") : string.Empty;
        return ThemesMatchCurrentSettings(theme) ? $"✓ {theme.Name}{suffix}{t("theme.active_suffix")}" : $"{theme.Name}{suffix}";
    }

    private bool ThemesMatchCurrentSettings(LauncherThemeFile theme) =>
        string.Equals(theme.Accent, SettingsService.Get<string>(SettingsService.LAUNCHER_THEME_COLOR), StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            theme.Branding,
            SettingsService.Get<string>(SettingsService.LAUNCHER_TEXT_COLOR),
            StringComparison.OrdinalIgnoreCase
        )
        && string.Equals(
            theme.Text,
            SettingsService.Get<string>(SettingsService.LAUNCHER_BODY_TEXT_COLOR),
            StringComparison.OrdinalIgnoreCase
        )
        && string.Equals(
            theme.Background,
            SettingsService.Get<string>(SettingsService.LAUNCHER_BACKGROUND_COLOR),
            StringComparison.OrdinalIgnoreCase
        );

    private string? FindSavedTheme(string name) =>
        _fileSystem
            .Directory.EnumerateFiles(ThemesFolderPath, "*.bwwtheme")
            .FirstOrDefault(path => string.Equals(TryReadTheme(path)?.Name, name, StringComparison.OrdinalIgnoreCase));

    private string MakeSafeFileName(string name)
    {
        var invalid = _fileSystem.Path.GetInvalidFileNameChars();
        var safe = new string(name.Trim().Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(safe) ? "theme" : safe;
    }

    private void Color_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isLoadingTheme)
            return;

        ColorError.IsVisible = false;
        UpdatePreview();
    }

    // Each card becomes a section with a centered title and a chevron, like the VR overlay editor.
    private readonly List<(Border Card, Avalonia.Controls.Shapes.Path Chevron)> _sections = [];

    private void MakeCollapsible()
    {
        Collapsible(LibraryCard, "Theme library", expanded: false, hideFirstChild: false);
        Collapsible(ColorsCard, "Colors", expanded: true, hideFirstChild: true);
        Collapsible(LookCard, "Background and look", expanded: false, hideFirstChild: true);
    }

    private void Collapsible(Border card, string title, bool expanded, bool hideFirstChild)
    {
        if (hideFirstChild && card.Child is StackPanel inner && inner.Children.Count > 0)
        {
            // The header already names the section.
            if (inner.Children[0] is StackPanel heading && heading.Children.Count == 2 && heading.Children[0] is TextBlock)
                heading.IsVisible = false;
        }

        var chevron = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 1 3 L 6 8 L 11 3"),
            Stroke = new SolidColorBrush(Color.Parse("#99ffffff")),
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 12,
            Height = 11,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            RenderTransformOrigin = Avalonia.RelativePoint.Center,
            RenderTransform = new RotateTransform(expanded ? 0 : -90),
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,20"), Background = Brushes.Transparent };
        var label = new FormFieldLabel
        {
            Text = title,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        Grid.SetColumn(chevron, 2);
        row.Children.Add(chevron);
        var header = new Border
        {
            MinHeight = 44,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = row,
        };
        header.Classes.Add("SettingsBlockStyle");
        header.Classes.Add("SettingsRowStyle");
        card.IsVisible = expanded;
        _sections.Add((card, chevron));
        header.PointerPressed += (_, _) =>
        {
            var open = !card.IsVisible;
            // One section open at a time keeps the page short.
            foreach (var (other, arrow) in _sections)
            {
                other.IsVisible = ReferenceEquals(other, card) && open;
                arrow.RenderTransform = new RotateTransform(other.IsVisible ? 0 : -90);
            }
        };

        var index = CardsHost.Children.IndexOf(card);
        CardsHost.Children.Insert(index, header);
    }

    private void PaintPalette()
    {
        void Paint(Border target, string? text)
        {
            var hex = NormalizeHex(text);
            if (LauncherThemeService.IsValidHexColor(hex))
                target.Background = new SolidColorBrush(Color.Parse(hex));
        }

        Paint(PaletteAccent, ColorTextBox.Text);
        Paint(PaletteBrand, BrandColorTextBox.Text);
        Paint(PaletteText, TextColorTextBox.Text);
        Paint(PaletteBackground, BackgroundColorTextBox.Text);
    }

    // The preview pop-up follows the colors while it is open.
    private void RefreshOpenPreview()
    {
        if (_homePreview?.IsOpen != true)
            return;
        var theme = ReadEditor(requireName: false);
        if (theme == null)
            return;
        _homePreview.UpdatePreview(
            theme,
            _backgrounds.Current,
            SurfaceOpacitySlider.Value,
            MinimalHomeArtworkCheckBox.IsChecked == true,
            HideHomeTitleCheckBox.IsChecked == true
        );
    }

    private void UpdatePreview()
    {
        RefreshOpenPreview();
        PaintPalette();
        var color = NormalizeHex(ColorTextBox.Text);
        if (LauncherThemeService.IsValidHexColor(color))
        {
            ColorPreview.Background = new SolidColorBrush(Color.Parse(color));
            UpdateColorChoice(AccentSwatch, color);
        }

        var brandColor = NormalizeHex(BrandColorTextBox.Text);
        if (LauncherThemeService.IsValidHexColor(brandColor))
            UpdateColorChoice(BrandSwatch, brandColor);

        var textColor = NormalizeHex(TextColorTextBox.Text);
        var backgroundColor = NormalizeHex(BackgroundColorTextBox.Text);
        if (LauncherThemeService.IsValidHexColor(textColor))
            UpdateColorChoice(TextSwatch, textColor);
        if (LauncherThemeService.IsValidHexColor(backgroundColor))
            UpdateColorChoice(BackgroundSwatch, backgroundColor);
        UpdateContrastNotice(brandColor, textColor, backgroundColor);
    }

    private ContrastReport? _contrastSuggestion;

    /// <summary>
    /// Colors are always used exactly as typed, so this only warns when the main, title or text color is hard to see
    /// against the background, and offers easier-to-see versions that keep each color's own shade.
    /// </summary>
    private void UpdateContrastNotice(string brandColor, string textColor, string backgroundColor)
    {
        _contrastSuggestion = ThemeContrast.Check(NormalizeHex(ColorTextBox.Text), brandColor, textColor, backgroundColor);
        TextContrastNotice.IsVisible = _contrastSuggestion is not null;
        if (_contrastSuggestion is null)
            return;

        TextContrastMessage.Text = string.Join(" ", _contrastSuggestion.Problems);
        EffectiveTextColorPreview.Background = new SolidColorBrush(Color.Parse(_contrastSuggestion.Text));
    }

    private void FixContrast_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_contrastSuggestion is not { } fix)
            return;

        ColorTextBox.Text = fix.Accent;
        BrandColorTextBox.Text = fix.Branding;
        TextColorTextBox.Text = fix.Text;
        ThemeStatusText.Text = "Colors adjusted so they are easier to see. Preview them, then Apply or Save.";
    }

    private async void AccentColor_OnClick(object? sender, RoutedEventArgs e) =>
        ColorTextBox.Text = await PickColor(t("theme.main_color_picker"), ColorTextBox.Text) ?? ColorTextBox.Text;

    private async void BrandColor_OnClick(object? sender, RoutedEventArgs e) =>
        BrandColorTextBox.Text = await PickColor(t("theme.wheel_title_picker"), BrandColorTextBox.Text) ?? BrandColorTextBox.Text;

    private async void TextColor_OnClick(object? sender, RoutedEventArgs e) =>
        TextColorTextBox.Text = await PickColor(t("theme.text_color_picker"), TextColorTextBox.Text) ?? TextColorTextBox.Text;

    private async void BackgroundColor_OnClick(object? sender, RoutedEventArgs e) =>
        BackgroundColorTextBox.Text =
            await PickColor(t("theme.background_color_picker"), BackgroundColorTextBox.Text) ?? BackgroundColorTextBox.Text;

    private async Task<string?> PickColor(string title, string? currentValue)
    {
        var normalized = NormalizeHex(currentValue);
        var initialColor = LauncherThemeService.IsValidHexColor(normalized) ? Color.Parse(normalized) : Colors.White;
        var selected = await _popups.Create<ThemeColorPickerWindow>(title, initialColor).AwaitColor();
        return selected is { } color ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : null;
    }

    private static void UpdateColorChoice(Border swatch, string hex)
    {
        swatch.Background = new SolidColorBrush(Color.Parse(hex));
    }

    private static string NormalizeHex(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 6 && text[0] != '#')
            text = $"#{text}";
        return text.ToUpperInvariant();
    }
}
