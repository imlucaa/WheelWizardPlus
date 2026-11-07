using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WheelWizard.RrRooms;
using WheelWizard.Shared.Desktop.Storage;
using WheelWizard.Shared.Services;
using WheelWizard.StreamOverlay;
using WheelWizard.Views.Components;
using WheelWizard.Views.Dialogs;
using WheelWizard.Views.Shell;
using WheelWizard.WiiManagement.GameLicense;
using WheelWizard.WiiManagement.MiiManagement;
using WheelWizard.WiiManagement.MiiManagement.Domain.Mii;

namespace WheelWizard.Settings.Views;

/// <summary>
/// Styles the Rewind VR overlay (a web page by MichaelAccount1) with a live preview, and makes the OBS Browser source
/// link. Every option writes the same name the page reads from the link.
/// </summary>
public partial class OverlaySettings : UserControl
{
    private readonly OverlayConfig _config;
    private readonly OverlayPreview _preview;
    private readonly InputField _friendCodeInput = new()
    {
        Placeholder = "0000-0000-0000",
        Variant = InputFieldVariant.Bordered,
        Width = 170,
    };
    private readonly IPopupFactory _popups;
    private readonly IFilePickerService _filePicker;
    private readonly IApiCaller<IRwfcApi> _api;
    private readonly OverlayLive _live;
    private readonly OverlayBackground _background;
    private readonly IRoomPresence _presence;
    private readonly DispatcherTimer _poll = new();
    private int? _lastVr;
    private string _loadedImage = string.Empty;
    private int _lookupVersion;
    private readonly List<Action> _syncs = [];
    private readonly List<LicenseChoice> _licenseChoices = [];
    private readonly Dropdown _licensePicker = new()
    {
        Width = 230,
        PlaceholderText = "Pick a license",
        ZIndex = 2,
    };

    private sealed record LicenseChoice(string Name, string FriendCode)
    {
        public string Digits => new(FriendCode.Where(char.IsDigit).ToArray());

        public override string ToString() => $"{Name}  -  {FriendCode}";
    }

    private bool _syncing;
    private readonly WheelWizard.LoungeStats.LoungeStatsService? _loungeStats;
    private readonly WheelWizard.LoungeStats.LoungeStatsConfig? _loungeConfig;
    private readonly WheelWizard.RrLounge.RrLoungeImages? _images;
    private IDisposable? _loungeLease;
    private readonly List<Control> _vrRows = [];
    private readonly List<Control> _mmrRows = [];
    private readonly Dropdown _sourcePicker = new() { Width = 120, ZIndex = 3 };
    private readonly Dropdown _ladderPicker = new() { Width = 90, ZIndex = 2 };
    private readonly InputField _loungeName = new()
    {
        Placeholder = "Your RR Lounge name",
        Variant = InputFieldVariant.Bordered,
        Width = 150,
    };
    private string? _loungeBadgeUrl;

    public OverlaySettings(
        ISettingsManager settings,
        IGameLicenseSingletonService licenses,
        IPopupFactory popups,
        IFilePickerService filePicker,
        IApiCaller<IRwfcApi> api,
        OverlayLive live,
        OverlayBackground background,
        IRoomPresence presence,
        WheelWizard.LoungeStats.LoungeStatsService? loungeStats = null,
        WheelWizard.LoungeStats.LoungeStatsConfig? loungeConfig = null,
        WheelWizard.RrLounge.RrLoungeImages? images = null
    )
    {
        _loungeStats = loungeStats;
        _loungeConfig = loungeConfig;
        _images = images;
        _presence = presence;
        _background = background;
        _api = api;
        _live = live;
        _popups = popups;
        _filePicker = filePicker;
        InitializeComponent();
        _config = new OverlayConfig(settings);

        var friendCode = string.Empty;
        var name = string.Empty;
        try
        {
            friendCode = licenses.ActiveUser?.FriendCode ?? string.Empty;
            name = licenses.ActiveUser?.Mii?.Name.ToString() ?? string.Empty;
            foreach (var user in licenses.LicenseCollection.Users)
            {
                var code = user.FriendCode ?? string.Empty;
                if (code.Count(char.IsDigit) != 12 || code.All(c => c is '0' or '-'))
                    continue;
                var userName = user.Mii?.Name.ToString();
                _licenseChoices.Add(new LicenseChoice(string.IsNullOrWhiteSpace(userName) ? "No name" : userName, code));
            }
        }
        catch (Exception) { }

        var saved = live.FriendCodeDigits();
        _friendCodeInput.Text = saved.Length == 12 ? saved : friendCode;
        _friendCodeInput.TextChanged += (_, _) =>
        {
            RefreshLink();
            LookupPlayerAsync();
        };

        _preview = new OverlayPreview(_config, name);
        _licensePicker.ItemsSource = _licenseChoices;
        _licensePicker.SelectedItem = _licenseChoices.FirstOrDefault(c => c.Digits == Digits);
        _licensePicker.SelectionChanged += (_, _) =>
        {
            if (_licensePicker.SelectedItem is not LicenseChoice choice)
                return;
            _friendCodeInput.Text = choice.FriendCode;
            _preview.SetPlayerName(choice.Name);
        };
        PreviewHost.Content = _preview;
        _config.Changed += RefreshLink;
        _config.Changed += UpdatePreviewImage;
        ReplayWhenAnimationChanged();
        _config.Changed += ReplayWhenAnimationChanged;

        BuildOptions();
        ShowLook();
        RefreshLink();
        UpdatePreviewImage();
        LookupPlayerAsync();
    }

    private string Digits => new((_friendCodeInput.Text ?? string.Empty).Where(char.IsDigit).ToArray());

    private bool HasValidCode => Digits.Length == 12;

    /// <summary>The steady local link: it follows whatever friend code and look are picked here.</summary>
    private static string Link => OverlayLive.LocalUrl;

    /// <summary>Loads the name, VR and rank of the typed friend code, so the preview shows that player.</summary>
    private async void LookupPlayerAsync(bool refresh = false)
    {
        // A refresh waits its turn: it must not cancel a lookup of a code that was just typed.
        var version = refresh ? _lookupVersion : ++_lookupVersion;
        // MMR: the RR Lounge stats the app keeps (no rwfc.net needed).
        if (_config.ShowsMmr && _loungeStats is not null)
        {
            var lounge = _loungeStats.Current;
            var mmrMoved = refresh && lounge is not null && _lastVr is { } shown && lounge.Mmr != shown;
            _lastVr = lounge?.Mmr;
            _preview.SetPlayer(lounge?.Name, lounge?.Mmr, lounge?.Place, null, samePlayer: refresh);
            if (mmrMoved)
                _preview.Replay();
            UpdateLoungeBadge();
            return;
        }

        if (!HasValidCode)
        {
            _preview.SetPlayer(null, null, null);
            return;
        }

        // A refresh keeps what is shown when it fails, and is not held back by typing.
        if (!refresh)
            await Task.Delay(500);
        if (version != _lookupVersion)
            return;

        var digits = Digits;
        var code = $"{digits[..4]}-{digits.Substring(4, 4)}-{digits.Substring(8, 4)}";
        var result = await _api.CallApiAsync(api => api.GetPlayerProfileAsync(code));
        if (version != _lookupVersion)
            return;

        if (result.IsFailure || result.Value is null)
        {
            if (!refresh)
                _preview.SetPlayer(null, null, null);
            return;
        }

        Mii? mii = null;
        if (
            !string.IsNullOrWhiteSpace(result.Value.MiiData)
            && MiiSerializer.Deserialize(result.Value.MiiData) is { IsSuccess: true } parsed
        )
            mii = parsed.Value;
        var vr = OverlayLive.CurrentVr(_presence, code, result.Value.Vr, await OverlayLive.RoomVrAsync(_api, code));
        var moved = refresh && _lastVr is { } before && vr != before;
        _lastVr = vr;
        _preview.SetPlayer(result.Value.Name, vr, result.Value.Rank, mii, samePlayer: refresh);
        if (moved)
            _preview.Replay();
    }

    /// <summary>
    /// The overlay in a window of its own (always on top), next to the OBS browser source. The same button closes it
    /// again while it is open.
    /// </summary>
    private void OpenWindow_OnClick(object? sender, RoutedEventArgs e)
    {
        if (OverlayWindow.Current is not null)
            OverlayWindow.CloseIfOpen();
        else
            OverlayWindow.ShowOrActivate(_config, _api, _live, _background, _presence);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_loungeStats is not null)
            _loungeStats.Changed += OnLoungeChanged;
        ShowSource();
        // The preview follows the player like the overlay does, instead of showing the VR from when the page opened.
        _poll.Tick += Poll_OnTick;
        _poll.Interval = PollInterval();
        _poll.Start();
    }

    private TimeSpan PollInterval() => TimeSpan.FromSeconds(Math.Clamp(_config.GetNumber("data.pollSeconds"), 5, 600));

    private void Poll_OnTick(object? sender, EventArgs e)
    {
        _poll.Interval = PollInterval();
        LookupPlayerAsync(refresh: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_loungeStats is not null)
            _loungeStats.Changed -= OnLoungeChanged;
        _loungeLease?.Dispose();
        _loungeLease = null;
        _poll.Stop();
        _poll.Tick -= Poll_OnTick;
        base.OnDetachedFromVisualTree(e);
    }

    private void RefreshLink()
    {
        var valid = HasValidCode || _config.ShowsMmr;
        LinkText.Text = valid ? Link : "Enter your 12-digit friend code to get the link.";
        if (valid)
            _live.SaveFriendCode(Digits);
        CopyButton.IsEnabled = valid;
    }

    #region Badge or race HUD

    private readonly List<Control> _badgeOnly = [];
    private readonly Dictionary<Control, bool> _hiddenForHud = [];

    private Border BadgeOnly(Border row)
    {
        _badgeOnly.Add(row);
        return row;
    }

    // Every part of the options list added since the given place (a whole section: its header and its rows).
    private void BadgeOnlyFrom(int start)
    {
        for (var i = start; i < OptionsHost.Children.Count; i++)
            _badgeOnly.Add(OptionsHost.Children[i]);
    }

    // Hides the badge-only settings for the race HUD and brings them back (open or closed as they were) for the badge.
    private void ShowLook()
    {
        if (_config.UsesHudLook)
        {
            foreach (var control in _badgeOnly)
            {
                _hiddenForHud.TryAdd(control, control.IsVisible);
                control.IsVisible = false;
            }
        }
        else
        {
            foreach (var (control, wasVisible) in _hiddenForHud)
                control.IsVisible = wasVisible;
            _hiddenForHud.Clear();
        }
    }

    #endregion

    #region VR or MMR

    private Border Vr(Border row)
    {
        _vrRows.Add(row);
        return row;
    }

    private IEnumerable<Border> LoungeRows()
    {
        _loungeName.Text = _loungeConfig!.PlayerName;
        var find = new WheelWizard.Views.Components.Button
        {
            Text = "Find",
            ButtonSize = WheelWizard.Views.Components.Button.ButtonsSizeType.Compact,
        };
        find.Click += async (_, _) => await FindLoungePlayerAsync();
        _loungeName.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
                await FindLoungePlayerAsync();
        };
        var picker = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        picker.Children.Add(_loungeName);
        picker.Children.Add(find);
        var name = Row("RR Lounge name", "Type it and press Find. It is kept.", picker);
        _mmrRows.Add(name);
        yield return name;

        _ladderPicker.ItemsSource = WheelWizard.LoungeStats.LoungeStatsConfig.Ladders;
        _ladderPicker.SelectedItem = _loungeConfig.Ladder;
        _ladderPicker.SelectionChanged += (_, _) =>
        {
            if (_ladderPicker.SelectedItem is string ladder && ladder != _loungeConfig.Ladder)
                _loungeConfig.Set("player.ladder", JsonValue.Create(ladder));
        };
        var ladderRow = Row("Ladder", "rr is Retro Rewind, ct Custom Tracks, tt Time Trial.", _ladderPicker);
        _mmrRows.Add(ladderRow);
        yield return ladderRow;

        var flagRow = Row("Country flag", "Your RR Lounge country, before your name.", Toggle("visibility.flag"));
        _mmrRows.Add(flagRow);
        yield return flagRow;
    }

    // Looks the name up on the ladder; an exact match is taken, otherwise the close names are offered.
    private async Task FindLoungePlayerAsync()
    {
        if (_loungeConfig is null || _loungeStats is null)
            return;
        var name = (_loungeName.Text ?? string.Empty).Trim();
        if (name.Length == 0)
            return;
        var found = await _loungeStats.FindAsync(name, _loungeConfig.Ladder);
        if (found is null)
        {
            ViewUtils.ShowSnackbar("RR Lounge could not be reached. Try again in a moment.", ViewUtils.SnackbarType.Warning);
            return;
        }

        var player = found.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (player is null && found.Count > 0)
        {
            var options = found
                .Select(p => new ChoiceOption(p.Name, $"#{p.Rank} · {WheelWizard.LoungeStats.LoungeStatsCard.Format(p.Mmr)} MMR"))
                .ToList();
            var picked = await _popups
                .CreateChoiceList(
                    "RR Lounge",
                    "Which player are you?",
                    $"No one is called exactly \"{name}\". These names are close.",
                    options
                )
                .AwaitAnswer();
            if (picked is not { } index)
                return;
            player = found[index];
        }

        if (player is null)
        {
            ViewUtils.ShowSnackbar(
                $"No one called \"{name}\" is on the {_loungeConfig.Ladder.ToUpperInvariant()} ladder.",
                ViewUtils.SnackbarType.Warning
            );
            return;
        }

        _loungeName.Text = player.Name;
        _loungeConfig.SetPlayer(player.Name, player.Id);
        await _loungeStats.RefreshNowAsync();
    }

    // The rows, the word after the number and the RR Lounge reads follow the VR or MMR choice.
    private void ShowSource()
    {
        var mmr = _config.ShowsMmr && _loungeStats is not null;
        foreach (var row in _vrRows)
            row.IsVisible = !mmr;
        foreach (var row in _mmrRows)
            row.IsVisible = mmr;
        _preview.Unit = mmr ? "MMR" : "VR";
        if (mmr && _loungeLease is null && TopLevel.GetTopLevel(this) is not null)
            _loungeLease = _loungeStats!.Use();
        else if (!mmr)
        {
            _loungeLease?.Dispose();
            _loungeLease = null;
        }

        UpdateLoungeBadge();
    }

    // The RR Lounge division badge for the preview's rank badge while it shows MMR.
    private string? _flagUrl;

    // The RR Lounge country flag for the preview, while it shows MMR with the flag on.
    private async void UpdateFlag()
    {
        var url =
            _config.ShowsMmr && _config.GetBool("visibility.flag")
                ? WheelWizard.RrLounge.RrLoungeData.FlagUrl(_loungeStats?.Current?.CountryCode)?.Replace("/16x12/", "/w80/")
                : null;
        if (url == _flagUrl)
            return;
        _flagUrl = url;
        var bitmap = url is null || _images is null ? null : await _images.GetAsync(url, 0);
        if (url == _flagUrl)
            _preview.Flag = bitmap;
    }

    private async void UpdateLoungeBadge()
    {
        UpdateFlag();
        var url = _config.ShowsMmr ? _loungeStats?.Current?.BadgeUrl : null;
        if (url == _loungeBadgeUrl)
            return;
        _loungeBadgeUrl = url;
        var bitmap = url is null || _images is null ? null : await _images.GetAsync(url, 220);
        if (url == _loungeBadgeUrl)
            _preview.LoungeBadge = bitmap;
    }

    private void OnLoungeChanged() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_config.ShowsMmr)
                LookupPlayerAsync(refresh: true);
        });

    #endregion

    #region Options

    private void BuildOptions()
    {
        // The race HUD look comes with the "race hud" VR change (Animation): the settings that only change the badge are
        // hidden while it is picked; they keep their values and come back with any other VR change.
        _syncs.Add(ShowLook);
        _config.Changed += ShowLook;
        // The flag toggle shows or hides the preview's flag at once.
        _config.Changed += UpdateFlag;

        var presets = Section("Presets", expanded: true);
        presets.Add(PresetCards());

        presets.Add(Row("Share", "Save or load a look file.", ShareControls()));

        var identity = Section("Player", expanded: true);
        // VR from a license or friend code, or the RR Lounge MMR; every look option works the same for both.
        if (_loungeStats is not null && _loungeConfig is not null)
        {
            _sourcePicker.ItemsSource = new List<string> { "VR", "MMR" };
            _sourcePicker.SelectedItem = _config.ShowsMmr ? "MMR" : "VR";
            _sourcePicker.SelectionChanged += (_, _) =>
            {
                var mmr = _sourcePicker.SelectedItem as string == "MMR";
                if (mmr == _config.ShowsMmr)
                    return;
                _config.Set("data.source", JsonValue.Create(mmr ? "mmr" : "vr"));
                ShowSource();
                // A different number now: the change count starts again, so the switch is not shown as a gain or loss.
                _lastVr = null;
                _preview.SetPlayer(null, null, null);
                LookupPlayerAsync();
            };
            identity.Add(Row("Show", "Your VR, or your RR Lounge MMR.", _sourcePicker));
            foreach (var row in LoungeRows())
                identity.Add(row);
        }

        if (_licenseChoices.Count > 0)
            identity.Add(Vr(Row("License", "Your licenses in Wheel Wizard.", _licensePicker)));
        identity.Add(Vr(Row("Friend code", "From the license, or type another.", _friendCodeInput)));
        identity.Add(Row("Tag", "Shown before your name.", Text("identity.tag", 90)));
        identity.Add(Vr(Row("Update every", "How often VR is refreshed.", Slider("data.pollSeconds", 3, 60, 1, " s"))));

        var show = Section("Show");
        foreach (
            var (path, label) in new[]
            {
                ("avatar", "Mii picture"),
                ("name", "Name"),
                ("tag", "Tag"),
                ("vr", "VR"),
                ("delta", "VR change"),
                ("rank", "Rank"),
                ("rankDelta", "Rank change"),
                ("connection", "Connection"),
                ("room", "Room"),
                ("track", "Track"),
                ("sessionDelta", "Session VR"),
                ("dailyDelta", "24 hour VR"),
                ("rankBadge", "Rank badge (VR: E to Crown, MMR: your division)"),
                ("rankOutline", "Rank badge outline"),
            }
        )
        {
            var row = Row(label, null, Toggle($"visibility.{path}"));
            // The race HUD never shows the change pill.
            show.Add(path == "delta" ? BadgeOnly(row) : row);
        }

        show.Add(Row("Outline strength", "How thick the rank badge outline is.", Slider("typography.rankOutlineWidth", 1, 10, 1, "")));
        show.Add(Row("Rank badge size", "Compared with the number.", Slider("typography.rankBadgeSize", 30, 300, 5, " %")));
        show.Add(
            Row("Rank badge spacing", "The gap between the badge and the number.", Slider("typography.rankBadgeGap", 0, 100, 1, " %"))
        );

        var layout = Section("Size");
        layout.Add(BadgeOnly(Row("Width", null, Slider("layout.width", 320, 900, 10, " px"))));
        layout.Add(BadgeOnly(Row("Compact", "A shorter badge.", Toggle("layout.compact"))));
        layout.Add(BadgeOnly(Row("Card", "Off shows only the text and numbers, like the race HUD.", Toggle("layout.card"))));
        layout.Add(Row("Text weight", null, Slider("typography.weight", 400, 900, 100, "")));

        var borderStart = OptionsHost.Children.Count;
        var border = Section("Border");
        border.Add(Row("Effect", null, Choice("border.effect", "rainbow", "snake", "pulse", "wave", "ghost", "solid", "off")));
        border.Add(Row("Thickness", null, Slider("border.width", 0, 16, 1, " px")));
        border.Add(Row("Roundness", null, Slider("border.radius", 0, 60, 1, " px")));
        border.Add(Row("Speed", "Seconds per turn, lower is faster.", Slider("border.speed", 1, 20, 0.5, " s")));
        border.Add(Row("Glow", null, Toggle("border.glow")));
        border.Add(Row("Glow strength", null, Slider("border.glowStrength", 0, 1, 0.05, "")));
        border.Add(Row("Color 1", null, Color("border.color1")));
        border.Add(Row("Color 2", null, Color("border.color2")));
        border.Add(Row("Color 3", null, Color("border.color3")));

        BadgeOnlyFrom(borderStart);
        var colors = Section("Colors");
        colors.Add(Row("Text", null, Color("typography.textColor")));
        colors.Add(Row("Secondary text", null, Color("typography.mutedColor")));
        colors.Add(Row("VR number", null, Color("typography.vrColor")));
        colors.Add(
            BadgeOnly(
                Row(
                    "Race HUD digits",
                    "The VR number in the Mario Kart Wii race HUD digits, in the VR number color.",
                    Toggle("typography.hudDigits")
                )
            )
        );
        colors.Add(BadgeOnly(Row("Gains", "+VR, session and rank up.", Color("typography.gainColor"))));
        colors.Add(BadgeOnly(Row("Losses", "-VR.", Color("typography.lossColor"))));
        colors.Add(Row("Rank", null, Color("typography.rankColor")));
        colors.Add(Row("Rank badge outline", "The line around the rank badge.", Color("typography.rankOutlineColor")));
        colors.Add(Row("Mii backing", null, Choice("avatar.background", "gradient", "solid", "transparent")));
        colors.Add(Row("Mii color 1", null, Color("avatar.color1")));
        colors.Add(Row("Mii color 2", null, Color("avatar.color2")));

        var backgroundStart = OptionsHost.Children.Count;
        var background = Section("Background");
        background.Add(Row("Tint color", null, Color("background.overlayColor")));
        background.Add(Row("Tint strength", null, Slider("background.overlayOpacity", 0, 1, 0.02, "")));
        background.Add(Row("Brightness", null, Slider("background.brightness", 0, 2, 0.02, "")));
        background.Add(Row("Contrast", null, Slider("background.contrast", 0, 2, 0.02, "")));
        background.Add(Row("Saturation", null, Slider("background.saturation", 0, 2, 0.02, "")));
        background.Add(Row("Blur", null, Slider("background.blur", 0, 30, 1, " px")));
        background.Add(Row("Glass", null, Slider("background.glass", 0, 1, 0.02, "")));
        background.Add(Row("Picture", "A picture or GIF from your PC.", PictureButtons()));
        background.Add(Row("Picture link", "Or a direct link (https) to a picture or GIF.", Text("background.imageUrl", 190)));
        background.Add(Row("Fit", "How the picture fills the badge.", Choice("background.fit", "cover", "contain", "stretch")));
        background.Add(Row("Zoom", null, Slider("background.zoom", 1, 4, 0.05, "x")));
        background.Add(Row("Position left/right", "Which part of the picture stays in view.", Slider("background.x", 0, 100, 1, " %")));
        background.Add(Row("Position up/down", null, Slider("background.y", 0, 100, 1, " %")));

        BadgeOnlyFrom(backgroundStart);
        var motion = Section("Animation");
        motion.Add(Row("Preview", "Plays the VR change on the badge above.", PlayButton()));
        motion.Add(Row("VR change", null, Choice("animations.vr", "count", "race hud", "spring", "flip", "burst", "none")));
        motion.Add(Row("Rank change", null, Choice("animations.rank", "flip", "count", "spring", "burst", "none")));
        motion.Add(Row("Length", null, Slider("animations.durationMs", 200, 3000, 50, " ms")));
        motion.Add(Row("Big gain bursts at", "VR gained in one race.", Slider("animations.celebrateThreshold", 50, 1000, 10, " VR")));
        motion.Add(Row("Reduce motion", null, Toggle("animations.reducedMotion")));
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

        // The title is centered; the chevron sits at the right with an equal gap on the left.
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

    /// <summary>A list that adds each row to its panel and styles it as the top, middle or bottom of the block.</summary>
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
        };
        var input = new InputField
        {
            Variant = InputFieldVariant.Bordered,
            Width = 104,
            Placeholder = "#rrggbb",
        };

        swatch.Cursor = new Cursor(StandardCursorType.Hand);
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
            if (hex.StartsWith('#') && hex.Length is 4 or 7 or 9 && Avalonia.Media.Color.TryParse(hex, out _))
                _config.Set(path, JsonValue.Create(hex));
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(swatch);
        panel.Children.Add(input);
        return panel;
    }

    private Control Text(string path, double width)
    {
        var input = new InputField { Variant = InputFieldVariant.Bordered, Width = width };
        void Sync() => input.Text = _config.GetString(path);
        Sync();
        _syncs.Add(Sync);
        input.TextChanged += (_, _) =>
        {
            if (!_syncing)
                _config.Set(path, JsonValue.Create((input.Text ?? string.Empty).Trim()));
        };
        return input;
    }

    #endregion

    #region Buttons

    private void SyncAll()
    {
        _syncing = true;
        foreach (var sync in _syncs)
            sync();
        _syncing = false;
    }

    private void Reset_OnClick(object? sender, RoutedEventArgs e)
    {
        _config.Reset();
        SyncAll();
        _selectedPreset = null;
        RebuildPresetCards();
    }

    private static FilePickerFileType LookFile => new("VR overlay look") { Patterns = ["*.json"], MimeTypes = ["application/json"] };

    private Control ShareControls()
    {
        var export = new WheelWizard.Views.Components.Button
        {
            Text = "Export",
            ButtonSize = WheelWizard.Views.Components.Button.ButtonsSizeType.Compact,
        };
        export.Click += async (_, _) =>
        {
            var path = await _filePicker.SaveFileAsync("Export overlay look", [LookFile], "vr-overlay-look");
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
        };

        var import = new WheelWizard.Views.Components.Button
        {
            Text = "Import",
            ButtonSize = WheelWizard.Views.Components.Button.ButtonsSizeType.Compact,
        };
        import.Click += async (_, _) =>
        {
            var path = await _filePicker.OpenSingleFileAsync("Import overlay look", [LookFile]);
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
                _selectedPreset = null;
                RebuildPresetCards();
            }

            if (ok)
                ViewUtils.ShowSnackbar("Look imported.");
            else
                ViewUtils.ShowSnackbar("That file is not an overlay look.", ViewUtils.SnackbarType.Danger);
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(export);
        panel.Children.Add(import);
        return panel;
    }

    private static FilePickerFileType PictureFile =>
        new("Pictures and GIFs")
        {
            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"],
            MimeTypes = ["image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"],
        };

    private Control PlayButton()
    {
        var play = new WheelWizard.Views.Components.Button
        {
            Text = "Play",
            ButtonSize = WheelWizard.Views.Components.Button.ButtonsSizeType.Compact,
        };
        play.Click += (_, _) => _preview.Replay();
        return play;
    }

    // Changing how the VR change looks plays it once, so the new style can be judged straight away.
    private string _animationSignature = string.Empty;

    private void ReplayWhenAnimationChanged()
    {
        var signature =
            $"{_config.GetString("animations.vr")}|{_config.GetNumber("animations.durationMs")}|{_config.GetBool("animations.reducedMotion")}";
        if (signature == _animationSignature)
            return;
        var first = _animationSignature.Length == 0;
        _animationSignature = signature;
        if (!first)
            _preview.Replay();
    }

    private Control PictureButtons()
    {
        var upload = new WheelWizard.Views.Components.Button
        {
            Text = "Upload",
            ButtonSize = WheelWizard.Views.Components.Button.ButtonsSizeType.Compact,
        };
        upload.Click += async (_, _) =>
        {
            var path = await _filePicker.OpenSingleFileAsync("Pick a background picture", [PictureFile]);
            if (path is null)
                return;

            var error = _background.Import(path);
            if (error is not null)
            {
                ViewUtils.ShowSnackbar(error, ViewUtils.SnackbarType.Danger);
                return;
            }

            _config.Set("background.imageUrl", JsonValue.Create(_background.Url(DateTime.UtcNow.Ticks)));
            SyncAll();
        };

        var remove = new WheelWizard.Views.Components.Button
        {
            Text = "Remove",
            ButtonSize = WheelWizard.Views.Components.Button.ButtonsSizeType.Compact,
        };
        remove.Click += (_, _) =>
        {
            _background.Clear();
            _config.Set("background.imageUrl", JsonValue.Create(string.Empty));
            SyncAll();
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(upload);
        panel.Children.Add(remove);
        return panel;
    }

    /// <summary>Loads the picture in the preview whenever the picture setting changes.</summary>
    private async void UpdatePreviewImage()
    {
        var url = _config.GetString("background.imageUrl");
        if (url == _loadedImage)
            return;
        _loadedImage = url;

        var bitmap = await _background.LoadPreviewImageAsync(url);
        if (url == _loadedImage)
            _preview.SetBackgroundImage(bitmap);
    }

    private string? _selectedPreset;

    private Border PresetCards() => PresetButtons();

    private readonly TextBlock _presetName = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = FontWeight.SemiBold,
        Opacity = 0.75,
    };
    private WheelWizard.Views.Components.Button? _deletePresetButton;

    /// <summary>One tidy block: the title with the current look on the right, then three equal buttons.</summary>
    private Border PresetButtons()
    {
        WheelWizard.Views.Components.Button Make(string text) =>
            new()
            {
                Text = text,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(3, 0),
            };

        var choose = Make("Choose");
        choose.Click += async (_, _) => await ChoosePresetAsync();
        var save = Make("Save as");
        save.Click += async (_, _) => await SavePresetAsync();
        _deletePresetButton = Make("Delete");
        _deletePresetButton.Click += async (_, _) => await DeletePresetAsync();

        var buttons = new UniformGrid { Columns = 3, Margin = new Thickness(-3, 0) };
        buttons.Children.Add(choose);
        buttons.Children.Add(save);
        buttons.Children.Add(_deletePresetButton);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new FormFieldLabel { Text = "Preset", VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_presetName, 1);
        header.Children.Add(_presetName);

        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(0, 8) };
        stack.Children.Add(header);
        stack.Children.Add(buttons);

        var block = new Border { Child = stack };
        block.Classes.Add("SettingsBlockStyle");
        block.Classes.Add("SettingsRowStyle");
        RebuildPresetCards();
        return block;
    }

    /// <summary>Updates the name shown next to the buttons.</summary>
    private void RebuildPresetCards()
    {
        _presetName.Text = _selectedPreset ?? "Custom";
        if (_deletePresetButton is not null)
            _deletePresetButton.IsEnabled = _selectedPreset is { } name && !OverlayConfig.IsBuiltIn(name);
    }

    /// <summary>The same list popup as the Themes tab: built-in looks first, then the ones you saved.</summary>
    private async Task ChoosePresetAsync()
    {
        var names = _config.PresetNames;
        var options = names
            .Select(name => new ChoiceOption(
                name,
                name == _selectedPreset ? "Active"
                    : OverlayConfig.IsBuiltIn(name) ? "Built-in"
                    : "Saved",
                name == _selectedPreset,
                PresetColors(name)
            ))
            .ToList();
        var index = await _popups
            .CreateChoiceList("Preset library", "Choose a look", "Built-in color looks, or ones you saved.", options)
            .AwaitAnswer();
        if (index is not { } chosen || chosen < 0 || chosen >= names.Count)
            return;

        if (!_config.LoadPreset(names[chosen]))
            return;
        _selectedPreset = names[chosen];
        SyncAll();
        RebuildPresetCards();
    }

    /// <summary>The three border colors of a look, for the small strip in the list.</summary>
    private string[] PresetColors(string name)
    {
        var border = _config.PresetPatch(name)?["border"];
        string Read(string key, string fallback) => border?[key]?.GetValue<string>() ?? fallback;
        return [Read("color1", "#22d3ee"), Read("color2", "#8b5cf6"), Read("color3", "#f97316")];
    }

    private async Task DeletePresetAsync()
    {
        if (_selectedPreset is not { } name || OverlayConfig.IsBuiltIn(name))
            return;

        var yes = await _popups
            .Create<YesNoWindow>()
            .SetMainText($"Delete the preset \"{name}\"?")
            .SetExtraText("The current look stays as it is.")
            .AwaitAnswer();
        if (!yes)
            return;

        _config.DeletePreset(name);
        _selectedPreset = null;
        RebuildPresetCards();
    }

    private async Task SavePresetAsync()
    {
        var name = await _popups
            .Create<TextInputWindow>()
            .SetMainText("Save preset")
            .SetExtraText("Give this look a name, for example Stream.")
            .SetPlaceholderText("Preset name")
            .SetInitialText(_selectedPreset is { } s && !OverlayConfig.IsBuiltIn(s) ? s : string.Empty)
            .SetButtonText("Cancel", "Save")
            .ShowDialog();
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (OverlayConfig.IsBuiltIn(name.Trim()))
        {
            ViewUtils.ShowSnackbar("That name is used by a built-in preset.", ViewUtils.SnackbarType.Warning);
            return;
        }

        _config.SavePreset(name.Trim());
        _selectedPreset = name.Trim();
        RebuildPresetCards();
    }

    private async void Copy_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!HasValidCode && !_config.ShowsMmr)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(Link);

        ViewUtils.ShowSnackbar("Link copied. Paste it into an OBS Browser source.");
    }

    #endregion
}
