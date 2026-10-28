using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using WheelWizard.DiscordPresence;
using WheelWizard.Settings;
using WheelWizard.Settings.Views;
using WheelWizard.Views.Dialogs.Base;

namespace WheelWizard.Views.Dialogs;

/// <summary>
/// A mock of the Discord activity card. The images uploaded to the Discord application are listed under it, and
/// picking one shows it in the card, so you can compare them before choosing one for the real status.
/// </summary>
public partial class DiscordPreviewWindow : PopupContent
{
    private readonly DiscordPresenceService _presence;
    private readonly Dictionary<string, Bitmap> _images = [];
    private IReadOnlyList<DiscordAsset> _assets = [];
    private string? _selected;
    private string _savedKey = "";
    private readonly ISettingsManager _settings;
    private TaskCompletionSource? _completion;

    public DiscordPreviewWindow(DiscordPresenceService presence, ISettingsManager settings, bool live, bool playerButton, bool roomButton)
        : base(true, false, true, "Discord preview")
    {
        _presence = presence;
        _settings = settings;
        InitializeComponent();
        IsDismissible = true;
        DetailsText.Text = live ? "Racing in a WiFi VS (VR: 12000 BR: 9500)" : "Retro Rewind";
        StateText.Text = live ? "Mushroom Gorge" : settings.Get<string>(settings.DISCORD_STATE);
        StateText.IsVisible = !string.IsNullOrEmpty(StateText.Text);
        PartyText.Text = live ? "(3 of 12)" : string.Empty;
        PartyText.IsVisible = live;

        var showRoom = live && roomButton;
        PlayerButton.IsVisible = playerButton;
        RoomButton.IsVisible = showRoom;
        ButtonsGrid.IsVisible = playerButton || showRoom;
        ButtonsGrid.Columns = playerButton && showRoom ? 2 : 1;

        _savedKey = settings.Get<string>(settings.DISCORD_LARGE_IMAGE).Trim();
        LoadAssets(_savedKey);
    }

    private async void LoadAssets(string savedKey)
    {
        var assets = await _presence.ListAssetsAsync();
        if (assets.Count == 0)
        {
            AssetStatus.Text = "No uploaded images found for your application.";
            return;
        }

        AssetStatus.IsVisible = false;
        _assets = assets;
        AssetPicker.ItemsSource = assets.Select(asset => asset.Name).ToList();
        var start = Math.Max(0, assets.ToList().FindIndex(a => a.Name == savedKey));
        AssetPicker.SelectedIndex = start;
        AssetPicker.IsVisible = true;
        UseButton.IsVisible = true;
        AssetPicker.SelectionChanged += (_, _) =>
        {
            if (AssetPicker.SelectedIndex >= 0 && AssetPicker.SelectedIndex < _assets.Count)
                Select(_assets[AssetPicker.SelectedIndex]);
        };

        Select(assets[start]);
    }

    private async void Select(DiscordAsset asset)
    {
        _selected = asset.Name;
        UpdateUseButton();
        if (!_images.TryGetValue(asset.Name, out var bitmap))
        {
            var bytes = await _presence.DownloadAssetAsync(asset);
            if (bytes is null)
                return;
            try
            {
                using var stream = new MemoryStream(bytes);
                bitmap = new Bitmap(stream);
                _images[asset.Name] = bitmap;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                return;
            }
        }

        // The user may have picked another image while this one was downloading.
        if (_selected != asset.Name)
            return;
        PreviewImage.Source = bitmap;
        PreviewImage.IsVisible = true;
        FallbackIcon.IsVisible = false;
    }

    private void UpdateUseButton()
    {
        var current = _selected == _savedKey;
        UseButton.Text = current ? "Current image" : "Use this image";
        UseButton.IsEnabled = !current && _selected is not null;
    }

    private void Use_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is null || !SettingsEditing.Set(_settings, _settings.DISCORD_LARGE_IMAGE, _selected))
            return;

        _savedKey = _selected;
        UpdateUseButton();
    }

    /// <summary>Shows the popup and finishes when it closes, so the settings page can refresh what it shows.</summary>
    public async Task AwaitCompletion()
    {
        _completion = new();
        Show();
        await _completion.Task;
    }

    protected override void BeforeClose() => _completion?.TrySetResult();
}
