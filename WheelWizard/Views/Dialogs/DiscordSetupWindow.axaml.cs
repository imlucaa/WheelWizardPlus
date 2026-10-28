using Avalonia.Controls;
using Avalonia.Interactivity;
using WheelWizard.DiscordPresence;
using WheelWizard.Settings;
using WheelWizard.Views.Dialogs.Base;
using WheelWizard.Views.Shell;

namespace WheelWizard.Views.Dialogs;

/// <summary>Asks for the Discord app, image and text one step at a time, then saves everything and turns the status on.</summary>
public partial class DiscordSetupWindow : PopupContent
{
    private const string Portal = "https://discord.com/developers/applications";
    private const int LastStep = 5;
    private readonly ISettingsManager _settings;
    private readonly DiscordPresenceService _presence;
    private readonly Control[] _steps;
    private TaskCompletionSource? _completion;
    private int _step = 1;

    public DiscordSetupWindow(ISettingsManager settings, DiscordPresenceService presence)
        : base(true, false, true, "Discord RPC setup")
    {
        _settings = settings;
        _presence = presence;
        InitializeComponent();
        IsDismissible = true;
        _steps = [Step1, Step2, Step3, Step4, Step5];

        AppIdBox.Text = settings.Get<string>(settings.DISCORD_APP_ID);
        ImageKeyBox.Text = settings.Get<string>(settings.DISCORD_LARGE_IMAGE);
        ShowStep();
    }

    public async Task AwaitCompletion()
    {
        _completion = new();
        Show();
        await _completion.Task;
    }

    protected override void BeforeClose() => _completion?.TrySetResult();

    private string AppId => (AppIdBox.Text ?? "").Trim();

    private string ImageKey => (ImageKeyBox.Text ?? "").Trim();

    // With a saved Application ID these open the exact page of that app; without one they open the app list.
    private string AppPage(string page) => DiscordPresenceService.IsValidApplicationId(AppId) ? $"{Portal}/{AppId}/{page}" : Portal;

    private void OpenPortal_OnClick(object? sender, RoutedEventArgs e) => ViewUtils.OpenLink(Portal);

    private void OpenInformation_OnClick(object? sender, RoutedEventArgs e) => ViewUtils.OpenLink(AppPage("information"));

    private void OpenAssets_OnClick(object? sender, RoutedEventArgs e) => ViewUtils.OpenLink(AppPage("rich-presence/assets"));

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close();

    private async void CheckIcon_OnClick(object? sender, RoutedEventArgs e) => await CheckIconAsync();

    /// <summary>Says whether the app has an App Icon yet; it can still be skipped, but Discord then shows a ?.</summary>
    private async Task CheckIconAsync()
    {
        IconCheckText.Text = "Checking…";
        IconCheckText.Text = await DiscordPresenceService.HasApplicationIconAsync(AppId) switch
        {
            true => "App icon found.",
            false => "No App icon yet. Add one and press Check again, or press Next to skip it.",
            null => "Could not check. Press Next to go on.",
        };
    }

    private async void RefreshImages_OnClick(object? sender, RoutedEventArgs e) => await LoadImagesAsync();

    private void ImagePicker_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ImagePicker.SelectedItem is string name)
            ImageKeyBox.Text = name;
    }

    /// <summary>Shows the images already uploaded to the app, so any of them can be picked by name.</summary>
    private async Task LoadImagesAsync()
    {
        if (!DiscordPresenceService.IsValidApplicationId(AppId))
            return;

        ImageListText.Text = "Looking for your images…";
        var names = await DiscordPresenceService.GetImageNamesAsync(AppId);
        ImagePicker.ItemsSource = names;
        ImagePicker.IsVisible = names.Count > 0;
        // The image already chosen stays selected; with none chosen yet the app's own first upload is suggested.
        var suggested =
            names.Contains(ImageKey) ? ImageKey
            : ImageKey.Length == 0 ? names.FirstOrDefault()
            : null;
        ImagePicker.SelectedItem = suggested;
        ImageListText.Text =
            names.Count > 0 ? "Pick one, or type another name below." : "No images found yet. Upload one, then press Refresh list.";
    }

    private void Back_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_step > 1)
            _step--;
        ShowStep();
    }

    private async void Next_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!StepIsValid())
            return;

        if (_step == 2 && !await AppIsNamedCorrectlyAsync())
            return;

        if (_step == LastStep)
        {
            await FinishAsync();
            return;
        }

        _step++;
        ShowStep();
    }

    private async Task<bool> AppIsNamedCorrectlyAsync()
    {
        NextButton.IsEnabled = false;
        try
        {
            var name = await DiscordPresenceService.GetApplicationNameAsync(AppId);
            if (name is null)
            {
                AppIdBox.ErrorText = "That ID was not found on Discord. Check you copied the Application ID.";
                return false;
            }

            if (!name.Trim().Equals(DiscordPresenceService.RequiredName, StringComparison.OrdinalIgnoreCase))
            {
                AppIdBox.ErrorText =
                    $"Your Discord app is named '{name}'. Rename it to {DiscordPresenceService.RequiredName} on General Information, save, then press Next again.";
                return false;
            }

            return true;
        }
        finally
        {
            NextButton.IsEnabled = true;
        }
    }

    private bool StepIsValid()
    {
        AppIdBox.ErrorText = null;
        ImageKeyBox.ErrorText = null;

        if (_step == 2 && !DiscordPresenceService.IsValidApplicationId(AppId))
        {
            AppIdBox.ErrorText = "Paste the Application ID: 15 to 20 digits.";
            return false;
        }

        if (_step == 4 && !ImageKey.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-'))
        {
            ImageKeyBox.ErrorText = "Use only lowercase letters, numbers, _ and -.";
            return false;
        }

        return true;
    }

    private void ShowStep()
    {
        for (var i = 0; i < _steps.Length; i++)
            _steps[i].IsVisible = i == _step - 1;

        StepCounter.Text = $"Step {_step} of {LastStep}";
        if (_step == 3)
            _ = CheckIconAsync();
        if (_step == 4)
            _ = LoadImagesAsync();
        BackButton.IsVisible = _step > 1;
        NextButton.Text = _step == LastStep ? "Finish" : "Next";
        if (_step == LastStep)
            SummaryText.Text =
                $"Application ID: {AppId}{Environment.NewLine}"
                + $"Image: {(ImageKey.Length == 0 ? "(none, your app icon is used)" : ImageKey)}";
    }

    private async Task FinishAsync()
    {
        _settings.Set(_settings.DISCORD_APP_ID, AppId);
        _settings.Set(_settings.DISCORD_LARGE_IMAGE, ImageKey);
        _settings.Set(_settings.DISCORD_DETAILS, DiscordPresenceService.RequiredName);
        _settings.Set(_settings.DISCORD_LIVE_INFO, true);
        _settings.Set(_settings.DISCORD_PRESENCE_ENABLED, true);
        // Dolphin ignores the game's own presence while its Discord setting is off, so only this one shows.
        _settings.Set(_settings.DOLPHIN_DISCORD_PRESENCE, false);
        if (_settings.IsRecompModeActive())
            _settings.Set(_settings.RECOMP_DISCORD_PRESENCE, false);

        NextButton.IsEnabled = false;
        ImageCheckText.Text = "Saved. Checking your image…";
        ImageCheckText.Text = $"Saved. {await _presence.CheckImageAsync()}";
        NextButton.Text = "Close";
        NextButton.IsEnabled = true;
        NextButton.Click -= Next_OnClick;
        NextButton.Click += (_, _) => Close();
        BackButton.IsVisible = false;
    }
}
