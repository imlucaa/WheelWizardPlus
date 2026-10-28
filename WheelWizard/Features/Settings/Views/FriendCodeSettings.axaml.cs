using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WheelWizard.CustomDistributions;
using WheelWizard.DiscImages;
using WheelWizard.Models.Enums;
using WheelWizard.RrRooms;
using WheelWizard.Shared.Desktop.Storage;
using WheelWizard.Shared.Downloads;
using WheelWizard.Shared.Services;
using WheelWizard.Views.Dialogs;
using WheelWizard.Views.Shell;
using WheelWizard.WiiManagement.FriendCodes;
using WheelWizard.WiiManagement.GameLicense;

namespace WheelWizard.Settings.Views;

/// <summary>
/// Checks a friend code and, once it passes, gives it to the license you pick. Nothing changes without a backup of
/// rksys.dat, and a code that is taken is refused.
/// </summary>
public partial class FriendCodeSettings : UserControl
{
    /// <summary>Every profile ID below this was already given out (community figure as of 8 September 2026).</summary>
    private const uint FirstFreeProfileId = 183_000;

    private const string DatabaseUrl = "https://github.com/ImpactCoding/rr-player-database/blob/main/rr-players.json";
    private const string GodtoolUrl = "https://gabrlel.github.io/godtool.html";

    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#ff6d7e"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#5ef0a5"));

    private readonly IApiCaller<IRwfcApi> _api;
    private readonly IGameLicenseSingletonService _licenses;
    private readonly IPopupFactory _popups;
    private readonly ISettingsManager _settings;
    private readonly ISaveRegionService _saveRegions;
    private readonly ICustomDistributionPaths _paths;
    private readonly IFilePickerService? _filePicker;
    private readonly IPlayerDatabase? _database;
    private readonly IDiscImageService? _discs;
    private readonly List<MarioKartWiiEnums.Regions> _regions = [];
    private bool _syncingRegion;
    private readonly List<LicenseIdentity> _identities = [];
    private int _version;
    private int _selected;
    private string _shownProfileId = string.Empty;

    // The code that passed the last check; only this one can be applied.
    private uint? _approvedProfileId;
    private string _approvedCode = string.Empty;

    public FriendCodeSettings(
        IApiCaller<IRwfcApi> api,
        IGameLicenseSingletonService licenses,
        IPopupFactory popups,
        ISettingsManager settings,
        ISaveRegionService saveRegions,
        ICustomDistributionPaths paths,
        IFilePickerService? filePicker = null,
        IPlayerDatabase? database = null,
        IDiscImageService? discs = null
    )
    {
        _database = database;
        _discs = discs;
        _settings = settings;
        _saveRegions = saveRegions;
        _paths = paths;
        _filePicker = filePicker;
        _api = api;
        _licenses = licenses;
        _popups = popups;
        InitializeComponent();
        AckBox.IsCheckedChanged += (_, _) => UpdateApplyButton();
        CodeInput.TextChanged += (_, _) => HideApply();
        RegionPicker.SelectionChanged += RegionPicker_OnChanged;
        RefreshRegions();
        RefreshLicenses();
        RefreshUndo();
    }

    #region Region

    // Every region the game came in, with the disc code its save folder is named after.
    private static readonly MarioKartWiiEnums.Regions[] AllRegions =
    [
        MarioKartWiiEnums.Regions.America,
        MarioKartWiiEnums.Regions.Europe,
        MarioKartWiiEnums.Regions.Japan,
        MarioKartWiiEnums.Regions.Korea,
    ];

    private static string RegionName(MarioKartWiiEnums.Regions region) =>
        region switch
        {
            MarioKartWiiEnums.Regions.America => "America (RMCE)",
            MarioKartWiiEnums.Regions.Europe => "Europe (RMCP)",
            MarioKartWiiEnums.Regions.Japan => "Japan (RMCJ)",
            MarioKartWiiEnums.Regions.Korea => "South Korea (RMCK)",
            _ => "None",
        };

    private readonly HashSet<MarioKartWiiEnums.Regions> _regionsWithSave = [];

    private void RefreshRegions()
    {
        _syncingRegion = true;
        _regionsWithSave.Clear();
        try
        {
            foreach (var region in _saveRegions.GetAvailableRegions(_paths.SaveFolderPath))
                _regionsWithSave.Add(region);
        }
        catch (Exception)
        {
            // No save folder yet.
        }

        _regions.Clear();
        _regions.AddRange(AllRegions);
        RegionPicker.ItemsSource = _regions
            .Select(r => _regionsWithSave.Contains(r) ? RegionName(r) : RegionName(r) + " - no save")
            .ToList();
        var current = _settings.Get<MarioKartWiiEnums.Regions>(_settings.RR_REGION);
        RegionPicker.SelectedIndex = _regions.IndexOf(current);
        _syncingRegion = false;
        ShowRegionHint(current);
    }

    // The game starts from the disc in the game location, so that is the save it plays on. When the region picked here is
    // another one, the licenses and friends shown (and used in the sidebar and Rooms) are not the ones the game uses.
    private MarioKartWiiEnums.Regions _discRegion = MarioKartWiiEnums.Regions.None;

    private void ShowRegionHint(MarioKartWiiEnums.Regions current)
    {
        _discRegion = DiscRegion();
        var mismatch = _discRegion != MarioKartWiiEnums.Regions.None && _discRegion != current && _regionsWithSave.Contains(_discRegion);
        RegionHintRow.IsVisible = mismatch;
        if (!mismatch)
            return;

        var game = RegionName(_discRegion).Split(' ')[0];
        RegionHintText.Text = $"Your game file is the {game} disc, but this shows the {RegionName(current).Split(' ')[0]} save.";
        UseDiscRegionButton.Text = $"Use {game}";
    }

    private MarioKartWiiEnums.Regions DiscRegion()
    {
        var path = _settings.Get<string>(_settings.GAME_LOCATION);
        if (_discs is null || string.IsNullOrWhiteSpace(path))
            return MarioKartWiiEnums.Regions.None;

        try
        {
            var info = _discs.ReadInfo(path);
            return info.IsSuccess ? GameRegion.FromGameId(info.Value.GameId) : MarioKartWiiEnums.Regions.None;
        }
        catch (Exception)
        {
            return MarioKartWiiEnums.Regions.None;
        }
    }

    private void UseDiscRegion_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_discRegion == MarioKartWiiEnums.Regions.None)
            return;

        _settings.Set(_settings.RR_REGION, _discRegion);
        _licenses.LoadLicense();
        RefreshRegions();
        AfterSaveChanged();
    }

    private void RegionPicker_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingRegion || RegionPicker.SelectedIndex < 0 || RegionPicker.SelectedIndex >= _regions.Count)
            return;

        var region = _regions[RegionPicker.SelectedIndex];
        if (!_regionsWithSave.Contains(region))
        {
            // Nothing to edit yet: the game makes the save the first time that disc is played.
            ViewUtils.ShowSnackbar(
                $"No {RegionName(region)} save yet. Play that disc once and it appears.",
                ViewUtils.SnackbarType.Warning
            );
            RefreshRegions();
            return;
        }

        _settings.Set(_settings.RR_REGION, region);
        _licenses.LoadLicense();
        AfterSaveChanged();
    }

    #endregion

    #region Licenses

    private void RefreshLicenses()
    {
        _identities.Clear();
        try
        {
            _identities.AddRange(_licenses.GetLicenseIdentities());
        }
        catch (Exception)
        {
            // No save loaded yet.
        }

        if (_selected >= _identities.Count)
            _selected = 0;
        RebuildLicenseList();
        LicenseNote.Text = _identities.Count == 0 ? "No licenses found. Start the game once first." : string.Empty;
        LicenseNote.IsVisible = _identities.Count == 0;
        ExportLicenseButton.IsEnabled = _identities.Count > 0;
        RenameLicenseButton.IsEnabled = _identities.Count > 0;
        ImportLicenseButton.IsEnabled = _identities.Count > 0;
        UpdateApplyButton();
    }

    private void RebuildLicenseList()
    {
        LicenseList.Children.Clear();
        for (var i = 0; i < _identities.Count; i++)
        {
            var index = i;
            var license = _identities[i];
            var problems = LicenseIds.Check(license, _identities);
            var clashes = problems.SharesWith.Select(Label).Distinct().ToList();
            var selected = index == _selected;

            // Name small on top, the friend code under it; a red line only when IDs clash.
            var row = new StackPanel { Spacing = 1 };
            // Every card has a name line, so the cards stay the same height.
            row.Children.Add(
                new TextBlock
                {
                    Text = HasName(license) ? license.Name : "No name",
                    FontSize = 11,
                    Opacity = HasName(license) ? 0.7 : 0.45,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }
            );
            row.Children.Add(
                new TextBlock
                {
                    Text = license.FriendCode,
                    FontWeight = selected ? FontWeight.Bold : FontWeight.Medium,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }
            );
            if (clashes.Count > 0 || problems.IdsDiffer)
            {
                var status = new TextBlock
                {
                    Text = clashes.Count > 0 ? "Duplicate IDs" : "IDs differ",
                    FontSize = 11,
                    Foreground = Bad,
                };
                ToolTip.SetTip(status, ProblemText(license, problems));
                row.Children.Add(status);
            }

            var tile = new Border
            {
                Margin = new Thickness(3),
                Padding = new Thickness(10, 7),
                CornerRadius = new CornerRadius(8),
                Cursor = new Cursor(StandardCursorType.Hand),
                Background = new SolidColorBrush(Color.Parse(selected ? "#30ffffff" : "#14ffffff")),
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = new SolidColorBrush(Color.Parse(selected ? "#ccffffff" : "#18ffffff")),
                Child = row,
            };
            tile.PointerPressed += (_, _) =>
            {
                _selected = index;
                RebuildLicenseList();
                UpdateApplyButton();
            };
            ToolTip.SetTip(tile, "Double-click to copy the friend code");
            tile.DoubleTapped += async (_, _) => await CopyFriendCodeAsync(license.FriendCode);
            LicenseList.Children.Add(tile);
        }

        UpdateIdStatus();
    }

    private async Task CopyFriendCodeAsync(string friendCode)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || string.IsNullOrWhiteSpace(friendCode))
            return;

        await clipboard.SetTextAsync(friendCode);
        ViewUtils.ShowSnackbar("Friend code copied.");
    }

    private static string ProblemText(LicenseIdentity license, LicenseIdProblems problems)
    {
        var lines = new List<string>();
        if (problems.SameFriendCode(license))
            lines.Add(
                $"Has the same friend code as {string.Join(", ", problems.SharesWith.Where(o => o.ProfileId == license.ProfileId).Select(Label))}. Give one of them a new code."
            );
        var sharing = problems.SharesWith.Where(o => o.ProfileId != license.ProfileId).Select(Label).ToList();
        if (sharing.Count > 0)
            lines.Add($"Shares IDs with {string.Join(", ", sharing)}.");
        if (problems.IdsDiffer)
            lines.Add("Its pseudo and authentic IDs differ; they have to match.");
        return string.Join(" ", lines);
    }

    /// <summary>
    /// Tells whether the picked license's IDs are in order: its friend code and its pseudo and authentic IDs shared with no
    /// other license, and the last two equal. Problems that new IDs can fix get a button for it.
    /// </summary>
    private void UpdateIdStatus()
    {
        if (_selected < 0 || _selected >= _identities.Count)
        {
            IdStatusRow.IsVisible = false;
            return;
        }

        var license = _identities[_selected];
        var problems = LicenseIds.Check(license, _identities);
        IdStatusRow.IsVisible = true;
        IdStatusText.Foreground = problems.Any ? Bad : Good;
        IdStatusText.Text = problems.Any ? ProblemText(license, problems) : "IDs are unique and match.";
        FixIdsButton.IsVisible = problems.Any && !problems.SameFriendCode(license);
    }

    private async void RenameLicense_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_selected < 0 || _selected >= _identities.Count || BlockWhileGameRuns())
            return;

        var license = _identities[_selected];
        if (!HasName(license))
        {
            ViewUtils.ShowSnackbar(
                "This license has no name to change yet. Make a Mii for it in the game first.",
                ViewUtils.SnackbarType.Warning
            );
            return;
        }

        var input = await _popups
            .Create<TextInputWindow>()
            .SetMainText($"Rename {license.Name}")
            .SetExtraText("1 to 10 characters. Your save is backed up first. Close the game before you continue.")
            .SetPlaceholderText("New name")
            .SetInitialText(license.Name)
            .SetButtonText("Cancel", "Rename")
            .ShowDialog();
        var name = input?.Trim();
        if (string.IsNullOrEmpty(name) || name == license.Name)
            return;
        if (name.Length > 10)
        {
            ViewUtils.ShowSnackbar("A name can be 10 characters at most.", ViewUtils.SnackbarType.Warning);
            return;
        }

        var backup = _licenses.BackupSaveNow();
        if (backup.IsFailure)
        {
            ViewUtils.ShowSnackbar(backup.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        var result = _licenses.ChangeMiiName(license.Slot, name);
        if (result.IsFailure)
        {
            ViewUtils.ShowSnackbar(result.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        AfterSaveChanged();
        ViewUtils.ShowSnackbar($"Renamed to {name}.");
    }

    private async void FixIds_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_selected < 0 || _selected >= _identities.Count || BlockWhileGameRuns())
            return;

        var license = _identities[_selected];
        var yes = await _popups
            .Create<YesNoWindow>()
            .SetMainText($"Fix the IDs of {Label(license)}?")
            .SetExtraText(
                $"It keeps {license.FriendCode} and gets new pseudo and authentic IDs, and so does the same license in your other regions' saves. Your saves are backed up first, and Undo takes it back. Close the game before you continue."
            )
            .AwaitAnswer();
        if (!yes)
            return;

        var result = _licenses.MakeIdsUnique(license.Slot);
        if (result.IsFailure)
        {
            ViewUtils.ShowSnackbar(result.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        ViewUtils.ShowSnackbar("IDs fixed.");
        AfterSaveChanged();
    }

    #endregion

    #region Check

    private void CodeInput_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Check_OnClick(sender, e);
    }

    private void HideApply()
    {
        _approvedProfileId = null;
        ApplyPanel.IsVisible = false;
    }

    // A license without a name (the game calls it "no name") goes by its friend code instead.
    private static bool HasName(LicenseIdentity license) => license.Name != "no name" && license.Name.Any(IsVisibleGlyph);

    // Mii names can be made of blank-looking characters (format characters and the Hangul fillers and the Wii's blank character).
    private static bool IsVisibleGlyph(char c) =>
        !char.IsControl(c)
        && !char.IsWhiteSpace(c)
        && c is not ('\u3164' or '\u115F' or '\u1160' or '\uFFA0' or '\uF060')
        && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format;

    private static string Label(LicenseIdentity license) => HasName(license) ? license.Name : license.FriendCode;

    private void UpdateApplyButton()
    {
        var name = _selected >= 0 && _selected < _identities.Count ? Label(_identities[_selected]) : string.Empty;
        ApplyButton.Text = name.Length == 0 ? "Replace" : $"Replace {name}";
        ApplyButton.IsEnabled = _approvedProfileId is not null && AckBox.IsChecked == true && name.Length > 0;
    }

    private async void Check_OnClick(object? sender, RoutedEventArgs e)
    {
        var version = ++_version;
        HideApply();
        var digits = new string((CodeInput.Text ?? string.Empty).Where(char.IsDigit).ToArray());
        ResultPanel.IsVisible = true;
        CopyIdButton.IsVisible = false;
        ResultDetail.Text = string.Empty;
        ResultVerdict.Text = string.Empty;

        if (digits.Length == 0 || digits.Length > 12)
        {
            ResultCode.Text = "Type a friend code or a profile ID.";
            return;
        }

        uint profileId;
        ulong value;
        bool valid;
        if (digits.Length == 12)
        {
            value = ulong.Parse(digits);
            profileId = (uint)(value & 0xFFFFFFFF);
            valid = profileId != 0 && FriendCode.ProfileIdToFriendCode(profileId) == value;
        }
        else
        {
            if (!uint.TryParse(digits, out profileId) || profileId == 0)
            {
                ResultCode.Text = "That is not a profile ID.";
                return;
            }

            value = FriendCode.ProfileIdToFriendCode(profileId);
            valid = true;
        }

        var text = value.ToString("D12");
        var code = $"{text[..4]}-{text.Substring(4, 4)}-{text.Substring(8, 4)}";
        ResultCode.Text = $"{code}   ID {profileId}";
        _shownProfileId = profileId.ToString();
        CopyIdButton.IsVisible = true;

        if (!valid)
        {
            ResultVerdict.Foreground = Bad;
            ResultVerdict.Text = "Not a valid friend code.";
            return;
        }

        if (profileId < FirstFreeProfileId)
        {
            ResultVerdict.Foreground = Bad;
            ResultVerdict.Text = "Taken. IDs below 183,000 are all in use.";
            return;
        }

        if (_identities.FirstOrDefault(i => i.ProfileId == profileId) is { } mine)
        {
            ResultVerdict.Foreground = Bad;
            ResultVerdict.Text = $"{Label(mine)} already has this code.";
            return;
        }

        ResultDetail.Text = "Checking rwfc.net...";
        var result = await _api.CallApiAsync(api => api.GetPlayerProfileAsync(code));
        if (version != _version)
            return;

        // A player with a blank name (only spaces) still exists, so go by the profile, not the name.
        var found = result.IsSuccess && result.Value is not null && (result.Value.Pid.Length > 0 || result.Value.FriendCode.Length > 0);
        if (found)
        {
            var name = string.IsNullOrWhiteSpace(result.Value!.Name) ? "a player" : result.Value.Name;
            ResultDetail.Text = $"{name}, {result.Value.Vr:N0} VR, rank {result.Value.Rank:N0}";
            ResultVerdict.Foreground = Bad;
            ResultVerdict.Text = "Taken. This code belongs to a player.";
            return;
        }

        ResultDetail.Text = string.Empty;
        ResultVerdict.Foreground = Good;
        ResultVerdict.Text = "Looks free on rwfc.net.";
        _approvedProfileId = profileId;
        _approvedCode = code;
        AckBox.IsChecked = false;
        ResetDatabaseRow();
        ApplyPanel.IsVisible = _identities.Count > 0;
        UpdateApplyButton();
    }

    #endregion

    #region Player database

    private void ResetDatabaseRow()
    {
        DatabaseRow.IsVisible = _database is not null;
        DatabaseText.ClearValue(TextBlock.ForegroundProperty);
        DatabaseText.Text = "Not searched in the player database yet.";
        DatabaseSearchButton.IsVisible = true;
        DatabaseSearchButton.IsEnabled = true;
        ToolTip.SetTip(
            DatabaseSearchButton,
            _database?.IsReady == true
                ? "Search the saved list of players"
                : "Downloads the community player list once (about 29 MB) and keeps it for a week"
        );
    }

    private async void SearchDatabase_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_database is null || _approvedProfileId is null)
            return;

        var code = _approvedCode;
        DatabaseSearchButton.IsEnabled = false;
        DatabaseText.ClearValue(TextBlock.ForegroundProperty);
        DatabaseText.Text = "Searching the player database...";
        var progress = new Progress<DownloadProgress>(download =>
        {
            if (code == _approvedCode)
                DatabaseText.Text = download.TotalBytes is > 0
                    ? $"Downloading the player list... {download.BytesReceived / 1_000_000} of {download.TotalBytes / 1_000_000} MB"
                    : "Downloading the player list...";
        });
        var result = await _database.FindAsync(code, progress);
        // The code may have been changed while this was running; then this answer is about another code.
        if (code != _approvedCode || _approvedProfileId is null)
            return;

        if (result.IsFailure)
        {
            DatabaseText.Foreground = Bad;
            DatabaseText.Text = result.Error.Message;
            DatabaseSearchButton.IsEnabled = true;
            return;
        }

        if (result.Value is { } entry)
        {
            var who = string.IsNullOrWhiteSpace(entry.Name) ? "a player" : entry.Name;
            HideApply();
            ResultVerdict.Foreground = Bad;
            ResultVerdict.Text = $"Taken. This code is in the player database ({who}{(entry.Banned ? ", banned" : string.Empty)}).";
            return;
        }

        DatabaseText.Foreground = Good;
        DatabaseText.Text = "Not in the player database.";
        DatabaseSearchButton.IsVisible = false;
    }

    #endregion

    #region Replace

    /// <summary>
    /// Dolphin and WiiCompiled keep the save in memory while the game runs and write it back, so a change made then is
    /// lost.
    /// </summary>
    internal static bool IsGameRunning()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            return processes.Any(p =>
            {
                try
                {
                    var name = p.ProcessName;
                    return name.StartsWith("Dolphin", StringComparison.OrdinalIgnoreCase)
                            && !name.Contains("Tool", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("dolphin-emu", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("WiiCompiled", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("RetroRewind", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                    // The process ended while the list was read.
                    return false;
                }
            });
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    internal static bool BlockWhileGameRuns()
    {
        if (!IsGameRunning())
            return false;

        ViewUtils.ShowSnackbar("Close the game first. It would write its old save back over the change.", ViewUtils.SnackbarType.Warning);
        return true;
    }

    private async void Apply_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_approvedProfileId is not { } profileId || AckBox.IsChecked != true)
            return;
        if (BlockWhileGameRuns())
            return;
        if (_selected < 0 || _selected >= _identities.Count)
            return;

        var license = _identities[_selected];
        var yes = await _popups
            .Create<YesNoWindow>()
            .SetMainText($"Replace the code of {Label(license)}?")
            .SetExtraText(
                $"{license.FriendCode} becomes {_approvedCode}. It is also set on the same license in your other regions' saves. Your saves are backed up first. Close the game before you continue."
            )
            .AwaitAnswer();
        if (!yes)
            return;

        var result = _licenses.ChangeProfileId(license.Slot, profileId);
        if (result.IsFailure)
        {
            ViewUtils.ShowSnackbar(result.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        ViewUtils.ShowSnackbar($"Friend code changed to {_approvedCode}.");
        // The sidebar and the friends count keep their own copy of the active license, so refresh them.
        ViewUtils.GetLayout().UpdateSidebarProfile();
        ViewUtils.GetLayout().UpdateFriendCount();
        HideApply();
        RefreshLicenses();
        RefreshUndo();
    }

    #endregion

    #region License files

    private static readonly FilePickerFileType RkpFile = new("Mario Kart Wii license")
    {
        Patterns = ["*.rkp"],
        MimeTypes = ["application/octet-stream"],
    };

    /// <summary>
    /// A Mii name as it can be read outside the game: its own symbols (private-use characters, which show as dots) and
    /// control characters are left out.
    /// </summary>
    public static string ReadableName(string name) =>
        new string(name.Where(c => !char.IsControl(c) && c is < '\uE000' or > '\uF8FF').ToArray()).Trim();

    /// <summary>
    /// The suggested name for an exported license: its Mii name and friend code. A name with nothing readable left is just
    /// the friend code.
    /// </summary>
    public static string ExportFileName(string name, string friendCode)
    {
        // Characters no file name may hold on Windows, Linux or macOS.
        const string invalid = "\\/:*?\"<>|";
        var readable = new string(ReadableName(name).Where(c => !invalid.Contains(c)).ToArray()).Trim(' ', '.');
        if (name.Equals("no name", StringComparison.OrdinalIgnoreCase) || name == friendCode)
            readable = string.Empty;
        return readable.Length == 0 ? $"{friendCode}.rkp" : $"{readable} {friendCode}.rkp";
    }

    private async void ExportLicense_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_filePicker is null || _selected < 0 || _selected >= _identities.Count)
            return;

        var license = _identities[_selected];
        var path = await _filePicker.SaveFileAsync("Export license", [RkpFile], ExportFileName(license.Name, license.FriendCode));
        if (path is null)
            return;

        var result = _licenses.ExportLicense(license.Slot, path);
        if (result.IsFailure)
        {
            ViewUtils.ShowSnackbar(result.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        ViewUtils.ShowSnackbar($"Saved {Label(license)} as a .rkp file.");
    }

    private async void ExportAll_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_filePicker is null || _identities.Count == 0)
            return;

        var folder = StoragePaths.TryResolveLocalPath(
            (await _filePicker.SelectFolderAsync("Save the licenses in a folder")).FirstOrDefault()
        );
        if (folder is null)
            return;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var saved = 0;
        foreach (var license in _identities)
        {
            // Two licenses can share a name and code; the second gets its slot number so it does not overwrite the first.
            var fileName = ExportFileName(license.Name, license.FriendCode);
            if (!used.Add(fileName))
                fileName = $"{Path.GetFileNameWithoutExtension(fileName)} (license {license.Slot + 1}).rkp";
            used.Add(fileName);

            var result = _licenses.ExportLicense(license.Slot, Path.Combine(folder, fileName));
            if (result.IsFailure)
            {
                ViewUtils.ShowSnackbar(result.Error.Message, ViewUtils.SnackbarType.Danger);
                return;
            }

            saved++;
        }

        ViewUtils.ShowSnackbar($"Saved {saved} {(saved == 1 ? "license" : "licenses")} as .rkp files.");
    }

    /// <summary>A license that can be brought in: who it is, and how to put it into a slot.</summary>
    private sealed record ImportSource(
        string Name,
        string FriendCode,
        uint PseudoId,
        uint AuthenticId,
        Func<int, OperationResult>? Import,
        SaveBundle? Bundle = null,
        IReadOnlyList<string>? Files = null
    );

    /// <summary>Every license of one save, to be brought back together, each into its own slot.</summary>
    private sealed record SaveBundle(string Path, IReadOnlyList<BackupLicense> Licenses);

    private static readonly FilePickerFileType SaveFile = new("Mario Kart Wii save")
    {
        Patterns = ["*.dat"],
        MimeTypes = ["application/octet-stream"],
    };

    private static string DisplayName(string name) => ReadableName(name) is { Length: > 0 } readable ? readable : "No name";

    /// <summary>Import in three steps: where the license is from, which one it is, and which slot it goes into.</summary>
    private async void ImportLicense_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_filePicker is null || BlockWhileGameRuns())
            return;

        var from = await _popups
            .CreateChoiceList(
                "Import license",
                "Where is it from?",
                "Take one license from a file or from an earlier save.",
                [
                    new ChoiceOption("A license file", ".rkp", Detail: "One or more licenses saved on their own"),
                    new ChoiceOption("An earlier save", "Backups", Detail: "Saves kept before each change"),
                    new ChoiceOption("A save file", "rksys.dat", Detail: "Any Mario Kart Wii save"),
                ]
            )
            .AwaitAnswer();
        var source = from switch
        {
            0 => await PickFromRkpAsync(),
            1 => await PickFromBackupAsync(),
            2 => await PickFromSaveAsync(),
            _ => null,
        };
        if (source is null)
            return;

        if (source.Bundle is { } bundle)
            await ImportAllAsync(bundle);
        else if (source.Files is { } files)
            await ImportFilesAsync(files);
        else
            await ImportIntoSlotAsync(source);
    }

    private async Task<ImportSource?> PickFromRkpAsync()
    {
        var paths = await _filePicker!.OpenFilePickerAsync(RkpFile, allowMultiple: true, title: "Import licenses (.rkp)");
        if (paths.Count == 0)
            return null;
        // Several files go into empty slots together; one file asks which slot.
        if (paths.Count > 1)
            return new("", "", 0, 0, null, Files: paths);
        var path = paths[0];

        var read = _licenses.ReadLicenseFile(path);
        if (read.IsFailure)
        {
            ViewUtils.ShowSnackbar(read.Error.Message, ViewUtils.SnackbarType.Danger);
            return null;
        }

        return new(
            read.Value.Name,
            read.Value.FriendCode,
            read.Value.PseudoId,
            read.Value.AuthenticId,
            slot => _licenses.ImportLicense(slot, path)
        );
    }

    private async Task<ImportSource?> PickFromBackupAsync()
    {
        // Only saves that hold a license are worth listing; the friend codes in each show before one is chosen.
        var backups = _licenses
            .ListBackups()
            .Select(backup => (Backup: backup, Licenses: _licenses.GetBackupLicenses(backup.Name)))
            .Where(item => item.Licenses.Count > 0)
            .ToList();
        if (backups.Count == 0)
        {
            ViewUtils.ShowSnackbar("No earlier saves yet. One is kept each time the save is changed.", ViewUtils.SnackbarType.Warning);
            return null;
        }

        var options = backups
            .Select(item => new ChoiceOption(
                BackupSettings.Title(item.Backup),
                BackupSettings.KindText(item.Backup.Kind),
                Detail: string.Join("\n", item.Licenses.Select(license => license.FriendCode))
            ))
            .ToList();
        var chosen = await _popups
            .CreateChoiceList("Import license", "Which save?", "You see the friend codes in each one.", options)
            .AwaitAnswer();
        if (chosen is not { } index || index < 0 || index >= backups.Count)
            return null;

        var (backup, licenses) = backups[index];
        return await PickLicenseAsync(licenses, slot => (backup.FilePath, slot));
    }

    private async Task<ImportSource?> PickFromSaveAsync()
    {
        var path = await _filePicker!.OpenSingleFileAsync("Pick a save (rksys.dat)", [SaveFile]);
        if (path is null)
            return null;

        var licenses = _licenses.ReadSaveLicenses(path);
        if (licenses.Count == 0)
        {
            ViewUtils.ShowSnackbar("That is not a Mario Kart Wii save with any licenses in it.", ViewUtils.SnackbarType.Warning);
            return null;
        }

        return await PickLicenseAsync(licenses, slot => (path, slot));
    }

    /// <summary>Which license of a save to take, or all of them. A save with just one is taken without asking.</summary>
    private async Task<ImportSource?> PickLicenseAsync(IReadOnlyList<BackupLicense> licenses, Func<int, (string Path, int Slot)> where)
    {
        var license = licenses[0];
        if (licenses.Count > 1)
        {
            var options = licenses
                .Select(item => new ChoiceOption(DisplayName(item.Name), item.FriendCode, Detail: $"License {item.Slot + 1}"))
                .Prepend(new ChoiceOption($"All {licenses.Count} licenses", "Together", Detail: "Each goes back into its own slot"))
                .ToList();
            var chosen = await _popups
                .CreateChoiceList("Import license", "Which license?", "Take one of them, or all.", options)
                .AwaitAnswer();
            if (chosen is not { } index || index < 0 || index >= options.Count)
                return null;
            if (index == 0)
                return new("", "", 0, 0, null, new SaveBundle(where(licenses[0].Slot).Path, licenses));
            license = licenses[index - 1];
        }

        var (path, sourceSlot) = where(license.Slot);
        return new(
            license.Name,
            license.FriendCode,
            license.PseudoId,
            license.AuthenticId,
            slot => _licenses.ImportLicenseFromSave(slot, path, sourceSlot)
        );
    }

    /// <summary>Several .rkp files at once: each goes into an empty slot, and nothing that is there is replaced.</summary>
    private async Task ImportFilesAsync(IReadOnlyList<string> files)
    {
        if (BlockWhileGameRuns())
            return;

        var free = Enumerable.Range(0, 4).Where(n => _identities.All(i => i.Slot != n)).ToList();
        if (files.Count > free.Count)
        {
            ViewUtils.ShowSnackbar(
                $"Only {free.Count} empty {(free.Count == 1 ? "slot" : "slots")} for {files.Count} files. Pick fewer, or import one at a time to replace a license.",
                ViewUtils.SnackbarType.Warning
            );
            return;
        }

        var placed = new List<(int Slot, string Path)>();
        var lines = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var read = _licenses.ReadLicenseFile(files[i]);
            if (read.IsFailure)
            {
                ViewUtils.ShowSnackbar($"{Path.GetFileName(files[i])}: {read.Error.Message}", ViewUtils.SnackbarType.Danger);
                return;
            }

            placed.Add((free[i], files[i]));
            lines.Add($"License {free[i] + 1}: {DisplayName(read.Value.Name)} ({read.Value.FriendCode}) is added");
        }

        var yes = await _popups
            .Create<YesNoWindow>()
            .SetMainText($"Add {files.Count} licenses?")
            .SetExtraText(string.Join("\n", lines) + "\n\nYour save is backed up first. Close the game before you continue.")
            .AwaitAnswer();
        if (!yes)
            return;

        var result = _licenses.ImportLicenseFiles(placed);
        if (result.IsFailure)
        {
            ViewUtils.ShowSnackbar(result.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        _selected = 0;
        AfterSaveChanged();
        ViewUtils.ShowSnackbar($"Added {files.Count} licenses.");
    }

    /// <summary>Brings every license of a save back, each into the slot it had there.</summary>
    private async Task ImportAllAsync(SaveBundle bundle)
    {
        if (BlockWhileGameRuns())
            return;

        var lines = bundle.Licenses.Select(item =>
        {
            var incoming = $"{DisplayName(item.Name)} ({item.FriendCode})";
            return _identities.FirstOrDefault(i => i.Slot == item.Slot) is { } used
                ? $"License {item.Slot + 1}: {Label(used)} ({used.FriendCode}) becomes {incoming}"
                : $"License {item.Slot + 1}: {incoming} is added";
        });
        var yes = await _popups
            .Create<YesNoWindow>()
            .SetMainText($"Bring back {bundle.Licenses.Count} licenses?")
            .SetExtraText(string.Join("\n", lines) + "\n\nYour save is backed up first. Close the game before you continue.")
            .AwaitAnswer();
        if (!yes)
            return;

        var result = _licenses.ImportLicensesFromSave(bundle.Path, bundle.Licenses.Select(item => item.Slot).ToList());
        if (result.IsFailure)
        {
            ViewUtils.ShowSnackbar(result.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        _selected = 0;
        AfterSaveChanged();
        ViewUtils.ShowSnackbar($"Brought back {bundle.Licenses.Count} licenses.");
    }

    private async Task ImportIntoSlotAsync(ImportSource source)
    {
        if (source.Import is null)
            return;
        var name = DisplayName(source.Name);

        // All four slots: a used one is replaced, an empty one gets the license added.
        var options = Enumerable
            .Range(0, 4)
            .Select(n =>
                _identities.FirstOrDefault(i => i.Slot == n) is { } used
                    ? new ChoiceOption($"Replace {Label(used)}", $"License {n + 1}", Detail: used.FriendCode)
                    : new ChoiceOption("Empty slot", $"License {n + 1}", Detail: "Add it here")
            )
            .ToList();
        var chosen = await _popups
            .CreateChoiceList("Import license", "Where should it go?", $"{name}   {source.FriendCode}", options)
            .AwaitAnswer();
        if (chosen is not { } slot || slot is < 0 or >= 4)
            return;
        if (BlockWhileGameRuns())
            return;

        var replaced = _identities.FirstOrDefault(i => i.Slot == slot);
        var duplicate = _identities.FirstOrDefault(i => i.Slot != slot && i.FriendCode == source.FriendCode);
        var extra = replaced is null
            ? $"{name} ({source.FriendCode}) is added as license {slot + 1}."
            : $"{Label(replaced)} ({replaced.FriendCode}) is replaced by {name} ({source.FriendCode}).";
        if (duplicate is not null)
            extra += $"\n\n{Label(duplicate)} already has this friend code. Give one of them a new code before playing online.";
        else if (
            _identities.FirstOrDefault(i => i.Slot != slot && (i.PseudoId == source.PseudoId || i.AuthenticId == source.AuthenticId))
                is { } sharing
            && source.PseudoId != 0
        )
            extra += $"\n\n{Label(sharing)} already uses the same IDs. After importing, use Fix IDs so the server can tell them apart.";
        extra += "\n\nYour save is backed up first. Close the game before you continue.";

        var yes = await _popups
            .Create<YesNoWindow>()
            .SetMainText(replaced is null ? $"Add {name}?" : $"Replace {Label(replaced)}?")
            .SetExtraText(extra)
            .AwaitAnswer();
        if (!yes)
            return;

        var result = source.Import(slot);
        if (result.IsFailure)
        {
            ViewUtils.ShowSnackbar(result.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        _selected = 0;
        AfterSaveChanged();
        var index = _identities.FindIndex(i => i.Slot == slot);
        if (index >= 0)
        {
            _selected = index;
            RebuildLicenseList();
            UpdateApplyButton();
        }

        ViewUtils.ShowSnackbar($"Imported {name} as license {slot + 1}.");
    }

    #endregion

    private async void CopyId_OnClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || _shownProfileId.Length == 0)
            return;

        await clipboard.SetTextAsync(_shownProfileId);
        ViewUtils.ShowSnackbar("Profile ID copied.");
    }

    private void AfterSaveChanged()
    {
        ViewUtils.GetLayout().UpdateSidebarProfile();
        ViewUtils.GetLayout().UpdateFriendCount();
        HideApply();
        RefreshLicenses();
        RefreshUndo();
    }

    /// <summary>Shows the undo row when there is a backup from an earlier change.</summary>
    private void RefreshUndo()
    {
        DateTime? when = null;
        try
        {
            when = _licenses.GetLastBackupTime();
        }
        catch (Exception)
        {
            // No save folder yet.
        }

        UndoButton.IsVisible = when is not null;
        UndoText.Text = when is { } time ? $"Undo goes back to {time:d MMM, HH:mm}." : "See every license's friend codes in History.";

        var hasHistory = false;
        try
        {
            hasHistory = _licenses.GetFriendCodeHistory().Count > 0;
        }
        catch (Exception)
        {
            // No save loaded yet.
        }

        // History lists all four licenses, so it is worth opening even before the first change.
        HistoryButton.IsVisible = hasHistory || _identities.Count > 0;
        UndoCard.IsVisible = when is not null || HistoryButton.IsVisible;
    }

    private async void History_OnClick(object? sender, RoutedEventArgs e)
    {
        var change = await _popups
            .Create<FriendCodeHistoryWindow>(_licenses.GetFriendCodeHistory(), (IReadOnlyList<LicenseIdentity>)_identities.ToList())
            .AwaitAnswer();
        if (change is null || BlockWhileGameRuns())
            return;

        var who = change.Name.Length > 0 ? change.Name : $"License {change.Slot + 1}";
        var current = _identities.FirstOrDefault(i => i.Slot == change.Slot)?.FriendCode ?? change.NewCode;
        var yes = await _popups
            .Create<YesNoWindow>()
            .SetMainText($"Restore {change.OldCode}?")
            .SetExtraText(
                $"{who} goes from {current} back to {change.OldCode}, and so does the same license in your other regions' saves. Your saves are backed up first, and Undo takes it back. Close the game before you continue."
            )
            .AwaitAnswer();
        if (!yes)
            return;

        var result = _licenses.RestoreFriendCode(change.BackupName, change.Slot);
        if (result.IsFailure)
        {
            ViewUtils.ShowSnackbar(result.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        ViewUtils.ShowSnackbar($"Friend code restored to {change.OldCode}.");
        AfterSaveChanged();
    }

    private async void Undo_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_licenses.GetLastBackupTime() is not { } when)
            return;
        if (BlockWhileGameRuns())
            return;

        var yes = await _popups
            .Create<YesNoWindow>()
            .SetMainText("Undo the friend code change?")
            .SetExtraText(
                $"The friend codes go back to how they were on {when:d MMM, HH:mm}. Your races, VR and Miis stay as they are. Close the game before you continue."
            )
            .AwaitAnswer();
        if (!yes)
            return;

        var result = _licenses.UndoLastProfileIdChange();
        if (result.IsFailure)
        {
            ViewUtils.ShowSnackbar(result.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        ViewUtils.ShowSnackbar("Friend codes put back.");
        ViewUtils.GetLayout().UpdateSidebarProfile();
        ViewUtils.GetLayout().UpdateFriendCount();
        HideApply();
        RefreshLicenses();
        RefreshUndo();
    }

    private void Database_OnClick(object? sender, RoutedEventArgs e) => ViewUtils.OpenLink(DatabaseUrl);

    private void Godtool_OnClick(object? sender, RoutedEventArgs e) => ViewUtils.OpenLink(GodtoolUrl);
}
