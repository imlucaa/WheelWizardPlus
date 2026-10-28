using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Abstractions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Testably.Abstractions;
using WheelWizard.CustomCharacters;
using WheelWizard.MiiAnimations.Library;
using WheelWizard.MiiRendering.Services;
using WheelWizard.RrRooms;
using WheelWizard.Settings;
using WheelWizard.Shared.Calendar;
using WheelWizard.Shared.Desktop.Storage;
using WheelWizard.Shared.MessageTranslations;
using WheelWizard.Shared.Services;
using WheelWizard.Views.DesignTime;
using WheelWizard.Views.Dialogs;
using WheelWizard.Views.Shell;
using WheelWizard.Views.Shell.Navigation;
using WheelWizard.WiiManagement;
using WheelWizard.WiiManagement.MiiManagement;
using WheelWizard.WiiManagement.MiiManagement.Domain.Mii;
using WheelWizard.WiiManagement.MiiManagement.Views;
using WheelWizard.WiiManagement.MiiManagement.Views.Dialogs;
using WheelWizard.WiiManagement.MiiManagement.Views.Editor;

namespace WheelWizard.WiiManagement.MiiManagement.Views;

public sealed record SuggestedOnlineMii(Mii Mii, string Name, string FriendCode);

public sealed record OnlineSearchMatch(Mii Mii, string Name, string FriendCode, int? Vr);

public enum OnlineMiiPreviewSource
{
    None,
    Search,
    Suggested,
}

public partial class MiiListPage : UserControl
{
    public ObservableCollection<MiiListRow> MiiRows { get; } = [];
    public ObservableCollection<SuggestedOnlineMii> SuggestedOnlineMiis { get; } = [];
    private readonly List<MiiListEntry> _miiEntries = [];
    private readonly List<OnlineSearchMatch> _onlineSearchMatches = [];
    private Mii? _selectedOnlineMii;
    private string _savedMiiSearchText = string.Empty;
    private int _selectedOnlineMatchIndex = -1;
    private int _selectedSuggestedMiiIndex = -1;
    private OnlineMiiPreviewSource _onlinePreviewSource;
    private bool _isPageReady;

    private ICustomCharactersService CustomCharactersService { get; }
    private IMiiDbService MiiDbService { get; }
    private IMiiRepositoryService MiiRepositoryService { get; }
    private IFileSystem FileSystem { get; }

    private ISettingsManager SettingsService { get; }
    private MiiTagStore Tags { get; }
    private string? _activeTag;
    private MiiBlock? _pressedBlock;
    private IApiCaller<IRwfcApi> ApiCaller { get; }
    private IFilePickerService FilePicker { get; }
    private IPopupFactory Popups { get; }

    private bool _suggestionsLoaded;

    private readonly IMiiNativeRenderer _stageRenderer;
    private readonly IMiiAnimationLibrary _stageAnimations;
    private readonly ISeasonalCalendar _stageCalendar;
    private readonly IRandomSystem _stageRandom;
    private MiiEditorScene? _onlineStage;
    private bool _onlineStageStarted;

    private INavigationService Navigation { get; }

    public MiiListPage(
        IFilePickerService filePicker,
        ICustomCharactersService customCharactersService,
        IMiiDbService miiDbService,
        IMiiRepositoryService miiRepositoryService,
        IFileSystem fileSystem,
        ISettingsManager settingsService,
        INavigationService navigation,
        IApiCaller<IRwfcApi> apiCaller,
        IPopupFactory popups,
        IMiiNativeRenderer renderer,
        IMiiAnimationLibrary animations,
        ISeasonalCalendar calendar,
        IRandomSystem random
    )
    {
        _stageRenderer = renderer;
        _stageAnimations = animations;
        _stageCalendar = calendar;
        _stageRandom = random;
        Navigation = navigation;
        Tags = new MiiTagStore(settingsService);
        FilePicker = filePicker;
        CustomCharactersService = customCharactersService;
        MiiDbService = miiDbService;
        MiiRepositoryService = miiRepositoryService;
        FileSystem = fileSystem;
        SettingsService = settingsService;
        ApiCaller = apiCaller;
        FilePicker = filePicker;
        Popups = popups;
        InitializeComponent();
        DataContext = this;
        // MiiBlock marks its pointer events handled, so the drag-to-reorder hooks listen on the way down.
        MiiList.AddHandler(PointerPressedEvent, MiiList_OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        MiiList.AddHandler(PointerMovedEvent, MiiList_OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        MiiList.AddHandler(PointerReleasedEvent, MiiList_OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        MiiList.AddHandler(PointerCaptureLostEvent, MiiList_OnCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
        MyMiisTabButton.Classes.Set("Active", true);
        _isPageReady = true;

        var miiDbExists = MiiDbService.Exists();
        if (!miiDbExists)
        {
            if (SettingsService.PathsSetupCorrectly())
            {
                var creationResult = MiiRepositoryService.ForceCreateDatabase();
                if (creationResult.IsFailure)
                {
                    MessageTranslationHelper.ShowMessage(creationResult.Error);
                    VisibleWhenNoDb.IsVisible = !miiDbExists;
                }
            }
            else
            {
                VisibleWhenDb.IsVisible = false;
                VisibleWhenNoDb.IsVisible = true;
            }
        }

        miiDbExists = MiiDbService.Exists();
        if (!miiDbExists)
            return;

        VisibleWhenDb.IsVisible = true;
        VisibleWhenNoDb.IsVisible = false;
        ReloadMiiList();
    }

    private void MyMiisTab_OnClick(object? sender, RoutedEventArgs e) => ShowOnlineMiis(false);

    private void OnlineMiisTab_OnClick(object? sender, RoutedEventArgs e)
    {
        ShowOnlineMiis(true);
        if (!_suggestionsLoaded)
            _ = LoadSuggestedMiisAsync();
    }

    private void ShowOnlineMiis(bool showOnline)
    {
        MyMiisTabButton.Classes.Set("Active", !showOnline);
        OnlineMiisTabButton.Classes.Set("Active", showOnline);
        // The "My Miis" heading belongs to the saved-Miis view only.
        MiiPageTitle.IsVisible = !showOnline;
        MiiList.IsVisible = !showOnline;
        OnlineMiiPanel.IsVisible = showOnline;
        SavedMiiFilterPanel.IsVisible = !showOnline;
        SearchOnlineMiiButton.IsVisible = showOnline;
        // The selection buttons (favorite, edit, delete...) belong to the saved Miis.
        ClearSelectedEntries();
        ChangeTopButtons();
    }

    private void SavedMiiSearch_OnTextChanged(object? sender, TextChangedEventArgs e) { }

    private async void SearchSavedMiiButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var input = await Popups
            .Create<TextInputWindow>()
            .SetMainText("Filter My Miis")
            .SetExtraText("Search your saved Miis by name.")
            .SetPlaceholderText("Mii name")
            .SetInitialText(_savedMiiSearchText)
            .SetButtonText("Clear", "Search")
            .ShowDialog();

        if (input == null)
        {
            if (string.IsNullOrWhiteSpace(_savedMiiSearchText))
                return;

            _savedMiiSearchText = string.Empty;
            ClearSelectedEntries();
            RebuildRows();
            ChangeTopButtons();
            return;
        }

        _savedMiiSearchText = input.Trim();

        if (!_isPageReady)
            return;

        ClearSelectedEntries();
        RebuildRows(_savedMiiSearchText);
        ChangeTopButtons();
    }

    private void OnlineMiiSearch_OnTextChanged(object? sender, TextChangedEventArgs e) => OnlineMiiLookupStatus.Text = string.Empty;

    private void OnlineMiiSearch_OnKeyDown(object? sender, KeyEventArgs e) { }

    private async void SearchFriendCode_OnClick(object? sender, RoutedEventArgs e)
    {
        var input = await Popups
            .Create<TextInputWindow>()
            .SetMainText("Search RWFC Miis")
            .SetExtraText("Search by player name or 12-digit friend code.")
            .SetPlaceholderText("Player or 0000-0000-0000")
            .SetButtonText("Cancel", "Search")
            .ShowDialog();

        if (input == null)
            return;

        await SearchOnlineMii(input);
    }

    private async Task SearchOnlineMii(string? requestedQuery = null)
    {
        var query = requestedQuery?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            SetOnlineLookupStatus("Enter a player name or all 12 digits of the friend code.", isError: true);
            return;
        }

        var normalizedFriendCode = NormalizeFriendCode(query);

        SearchOnlineMiiButton.IsEnabled = false;
        SetOnlineLookupStatus(
            normalizedFriendCode != null ? $"Looking up {normalizedFriendCode}…" : $"Searching RWFC for \"{query}\"…",
            isError: false
        );

        try
        {
            if (normalizedFriendCode != null)
            {
                var result = await ApiCaller.CallApiAsync(api => api.GetPlayerProfileAsync(normalizedFriendCode));
                if (result.IsFailure || result.Value == null)
                {
                    SetOnlineLookupStatus($"No RWFC profile was found for {normalizedFriendCode}.", isError: true);
                    return;
                }

                if (string.IsNullOrWhiteSpace(result.Value.MiiData))
                {
                    SetOnlineLookupStatus("That RWFC profile does not have Mii data.", isError: true);
                    return;
                }

                var miiResult = MiiSerializer.Deserialize(result.Value.MiiData);
                if (miiResult.IsFailure)
                {
                    SetOnlineLookupStatus("RWFC returned Mii data that Wheel Wizard could not read.", isError: true);
                    return;
                }

                SetOnlineMii(miiResult.Value, result.Value.Name, normalizedFriendCode);
                SetOnlineSearchMatches([new OnlineSearchMatch(miiResult.Value, result.Value.Name, normalizedFriendCode, result.Value.Vr)]);
                SetOnlineLookupStatus($"Loaded from RWFC ┬╖ {result.Value.Vr:N0} VR", isError: false);
                return;
            }

            var searchResult = await ApiCaller.CallApiAsync(api => api.SearchLeaderboardAsync(query));
            if (searchResult.IsFailure || searchResult.Value == null)
            {
                SetOnlineLookupStatus("RWFC name search failed. Try again in a moment.", isError: true);
                return;
            }

            var matchingPlayers = searchResult
                .Value.Players.Where(entry => !string.IsNullOrWhiteSpace(entry.MiiData) && !string.IsNullOrWhiteSpace(entry.FriendCode))
                .Select(entry =>
                {
                    var parsed = MiiSerializer.Deserialize(entry.MiiData!);
                    if (parsed.IsFailure)
                        return null;

                    return new OnlineSearchMatch(
                        parsed.Value,
                        string.IsNullOrWhiteSpace(entry.Name) ? parsed.Value.Name.ToString() : entry.Name,
                        entry.FriendCode,
                        entry.Vr
                    );
                })
                .Where(match => match != null)
                .Cast<OnlineSearchMatch>()
                .ToList();

            if (matchingPlayers.Count == 0)
            {
                SetOnlineLookupStatus($"No RWFC players with downloadable Mii data matched \"{query}\".", isError: true);
                return;
            }

            SetOnlineSearchMatches(matchingPlayers, searchResult.Value.TotalCount);
        }
        finally
        {
            SearchOnlineMiiButton.IsEnabled = true;
        }
    }

    private static string? NormalizeFriendCode(string? friendCode)
    {
        var digits = new string((friendCode ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length == 12 ? $"{digits[..4]}-{digits.Substring(4, 4)}-{digits.Substring(8, 4)}" : null;
    }

    private async void OnlineMiiFriendCode_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var friendCode = OnlineMiiFriendCode.Text;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || string.IsNullOrWhiteSpace(friendCode))
            return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;

        await clipboard.SetTextAsync(friendCode);
        ViewUtils.ShowSnackbar("Friend code copied.");
    }

    private void SetOnlineMii(Mii mii, string playerName, string friendCode)
    {
        _selectedOnlineMii = mii;
        ShowOnlineStage(mii);
        OnlineMiiName.Text = string.IsNullOrWhiteSpace(playerName) ? mii.Name.ToString() : playerName;
        OnlineMiiFriendCode.Text = friendCode;
        ImportOnlineMiiButton.IsEnabled = true;
    }

    /// <summary>
    /// Shows the previewed Mii on the editor's 3D stage. The stage is built on first use (not when the page opens),
    /// then every later Mii is swapped onto it so it keeps its camera and animation.
    /// </summary>
    private void ShowOnlineStage(Mii mii)
    {
        if (_onlineStage is null)
        {
            _onlineStage = new MiiEditorScene(
                _stageRenderer,
                _stageAnimations,
                _stageCalendar,
                _stageRandom.Random.Shared,
                SettingsService.ENABLE_ANIMATIONS.Get()
            );
            OnlineMiiStageHost.Children.Add(_onlineStage);
        }

        if (!_onlineStageStarted)
        {
            _onlineStage.ShowEditor(mii);
            // The editor's head close-up, but the Mii keeps moving instead of holding still.
            _onlineStage.SetFraming(MiiEditorFraming.Head);
            KeepStageAnimated();
            _onlineStageStarted = true;
            return;
        }

        _onlineStage.Present(mii);
        KeepStageAnimated();
    }

    private void KeepStageAnimated()
    {
        if (_onlineStage?.Director is { } director)
            director.HoldStill = false;
    }

    private int _onlineSearchTotalMatches;

    private void SetOnlineSearchMatches(List<OnlineSearchMatch> matches, int totalMatches = 0)
    {
        _onlinePreviewSource = matches.Count > 0 ? OnlineMiiPreviewSource.Search : OnlineMiiPreviewSource.None;
        _selectedSuggestedMiiIndex = -1;
        _onlineSearchMatches.Clear();
        _onlineSearchMatches.AddRange(matches);
        _onlineSearchTotalMatches = totalMatches > 0 ? totalMatches : matches.Count;
        _selectedOnlineMatchIndex = matches.Count > 0 ? 0 : -1;
        ApplyCurrentOnlineSearchMatch();
    }

    private void ApplyCurrentOnlineSearchMatch()
    {
        if (_selectedOnlineMatchIndex < 0 || _selectedOnlineMatchIndex >= _onlineSearchMatches.Count)
        {
            RefreshOnlinePreviewNavigation();
            return;
        }

        var match = _onlineSearchMatches[_selectedOnlineMatchIndex];
        SetOnlineMii(match.Mii, match.Name, match.FriendCode);
        var vrText = match.Vr.HasValue ? $" ┬╖ {match.Vr.Value:N0} VR" : string.Empty;
        SetOnlineLookupStatus($"{_selectedOnlineMatchIndex + 1} of {_onlineSearchTotalMatches}{vrText}", isError: false);

        RefreshOnlinePreviewNavigation();
    }

    private void RefreshOnlinePreviewNavigation()
    {
        if (_onlinePreviewSource == OnlineMiiPreviewSource.Search)
        {
            PreviousOnlineMiiButton.IsEnabled = _onlineSearchMatches.Count > 1;
            NextOnlineMiiButton.IsEnabled = _onlineSearchMatches.Count > 1;
            OnlineMiiMatchIndex.Text =
                _onlineSearchMatches.Count > 1 ? $"{_selectedOnlineMatchIndex + 1}/{_onlineSearchMatches.Count}" : string.Empty;
            return;
        }

        if (_onlinePreviewSource == OnlineMiiPreviewSource.Suggested)
        {
            PreviousOnlineMiiButton.IsEnabled = SuggestedOnlineMiis.Count > 1;
            NextOnlineMiiButton.IsEnabled = SuggestedOnlineMiis.Count > 1;
            OnlineMiiMatchIndex.Text =
                SuggestedOnlineMiis.Count > 1 ? $"{_selectedSuggestedMiiIndex + 1}/{SuggestedOnlineMiis.Count}" : string.Empty;
            return;
        }

        PreviousOnlineMiiButton.IsEnabled = false;
        NextOnlineMiiButton.IsEnabled = false;
        OnlineMiiMatchIndex.Text = string.Empty;
    }

    private void PreviousOnlineMii_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_onlinePreviewSource == OnlineMiiPreviewSource.Search)
        {
            if (_onlineSearchMatches.Count <= 1)
                return;

            _selectedOnlineMatchIndex = (_selectedOnlineMatchIndex - 1 + _onlineSearchMatches.Count) % _onlineSearchMatches.Count;
            ApplyCurrentOnlineSearchMatch();
            return;
        }

        if (_onlinePreviewSource != OnlineMiiPreviewSource.Suggested || SuggestedOnlineMiis.Count <= 1)
            return;

        _selectedSuggestedMiiIndex = (_selectedSuggestedMiiIndex - 1 + SuggestedOnlineMiis.Count) % SuggestedOnlineMiis.Count;
        ApplySuggestedOnlineMiiSelection();
    }

    private void NextOnlineMii_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_onlinePreviewSource == OnlineMiiPreviewSource.Search)
        {
            if (_onlineSearchMatches.Count <= 1)
                return;

            _selectedOnlineMatchIndex = (_selectedOnlineMatchIndex + 1) % _onlineSearchMatches.Count;
            ApplyCurrentOnlineSearchMatch();
            return;
        }

        if (_onlinePreviewSource != OnlineMiiPreviewSource.Suggested || SuggestedOnlineMiis.Count <= 1)
            return;

        _selectedSuggestedMiiIndex = (_selectedSuggestedMiiIndex + 1) % SuggestedOnlineMiis.Count;
        ApplySuggestedOnlineMiiSelection();
    }

    private void SetOnlineLookupStatus(string message, bool isError)
    {
        OnlineMiiLookupStatus.Text = message;
        OnlineMiiLookupStatus.Foreground = new Avalonia.Media.SolidColorBrush(
            isError ? ViewUtils.Colors.Danger400 : ViewUtils.Colors.Neutral400
        );
    }

    private async void RefreshSuggestedMiis_OnClick(object? sender, RoutedEventArgs e) => await LoadSuggestedMiisAsync();

    private const int SuggestedMiiCount = 9;
    private const int SuggestedPages = 3;

    // How many pages the leaderboard has, learned from the first page fetched (it only grows).
    private static int _leaderboardPages;

    /// <summary>
    /// <paramref name="count"/> different random page numbers between 1 and <paramref name="pages"/>, so the Miis come
    /// from all over the leaderboard rather than from its top.
    /// </summary>
    public static List<int> RandomPages(int pages, int count, Random random)
    {
        pages = Math.Max(1, pages);
        var picked = new HashSet<int>();
        while (picked.Count < Math.Min(count, pages))
            picked.Add(random.Next(1, pages + 1));
        return picked.ToList();
    }

    private async Task<List<RwfcLeaderboardEntry>?> RandomLeaderboardPlayersAsync()
    {
        // The first look, before the size is known, is anywhere in the first hundred pages (the board has thousands).
        var known = _leaderboardPages;
        var pages = RandomPages(known > 0 ? known : 100, SuggestedPages, Random.Shared);
        var results = await Task.WhenAll(pages.Select(page => ApiCaller.CallApiAsync(api => api.GetLeaderboardPageAsync(page))));

        var entries = new List<RwfcLeaderboardEntry>();
        foreach (var result in results.Where(result => result.IsSuccess && result.Value is not null))
        {
            _leaderboardPages = Math.Max(_leaderboardPages, result.Value.TotalPages);
            entries.AddRange(result.Value.Players);
        }

        return entries.Count > 0 ? entries : null;
    }

    private async Task LoadSuggestedMiisAsync()
    {
        RefreshSuggestedMiisButton.IsEnabled = false;
        SuggestedMiisStatus.Text = "Finding random public Miis…";
        try
        {
            // Random players from anywhere on the leaderboard, not just the top: a few random pages of it.
            var entries = await RandomLeaderboardPlayersAsync();
            if (entries is null)
            {
                SuggestedMiisStatus.Text = "Random Miis could not be loaded from RWFC. Try again.";
                return;
            }

            var suggestions = entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.MiiData) && !string.IsNullOrWhiteSpace(entry.FriendCode))
                .DistinctBy(entry => entry.FriendCode)
                .OrderBy(_ => Random.Shared.Next())
                .Select(entry =>
                {
                    var parsed = MiiSerializer.Deserialize(entry.MiiData!);
                    return parsed.IsSuccess
                        ? new SuggestedOnlineMii(
                            parsed.Value,
                            string.IsNullOrWhiteSpace(entry.Name) ? parsed.Value.Name.ToString() : entry.Name,
                            entry.FriendCode
                        )
                        : null;
                })
                .Where(item => item != null)
                .Cast<SuggestedOnlineMii>()
                // Nine fill the three-by-three grid; picked after reading, so a Mii that fails to load leaves no gap.
                .Take(SuggestedMiiCount)
                .ToList();

            SuggestedOnlineMiis.Clear();
            foreach (var suggestion in suggestions)
                SuggestedOnlineMiis.Add(suggestion);

            if (_onlinePreviewSource == OnlineMiiPreviewSource.Suggested)
            {
                _selectedSuggestedMiiIndex = SuggestedOnlineMiis.Count > 0 ? 0 : -1;
                ApplySuggestedOnlineMiiSelection();
            }

            _suggestionsLoaded = suggestions.Count > 0;
            SuggestedMiisStatus.Text =
                suggestions.Count > 0 ? "Click a Mii to preview it." : "RWFC did not return any downloadable Miis. Try again later.";
        }
        finally
        {
            RefreshSuggestedMiisButton.IsEnabled = true;
        }
    }

    private void SuggestedMiiBlock_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MiiBlock { DataContext: SuggestedOnlineMii suggestion })
            return;

        _onlinePreviewSource = OnlineMiiPreviewSource.Suggested;
        _selectedOnlineMatchIndex = -1;
        _selectedSuggestedMiiIndex = SuggestedOnlineMiis.IndexOf(suggestion);
        ApplySuggestedOnlineMiiSelection();
    }

    private void ApplySuggestedOnlineMiiSelection()
    {
        if (_selectedSuggestedMiiIndex < 0 || _selectedSuggestedMiiIndex >= SuggestedOnlineMiis.Count)
        {
            _onlinePreviewSource = OnlineMiiPreviewSource.None;
            RefreshOnlinePreviewNavigation();
            return;
        }

        var suggestion = SuggestedOnlineMiis[_selectedSuggestedMiiIndex];
        SetOnlineMii(suggestion.Mii, suggestion.Name, suggestion.FriendCode);
        SetOnlineLookupStatus(string.Empty, isError: false);
        RefreshOnlinePreviewNavigation();
    }

    private void ImportOnlineMii_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedOnlineMii == null)
            return;

        ImportOnlineMii(_selectedOnlineMii);
    }

    private void ImportOnlineMii(Mii sourceMii)
    {
        var serialized = MiiSerializer.Serialize(sourceMii);
        if (serialized.IsFailure)
        {
            ViewUtils.ShowSnackbar(serialized.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        var copyResult = MiiSerializer.Deserialize(serialized.Value);
        if (copyResult.IsFailure)
        {
            ViewUtils.ShowSnackbar(copyResult.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        var saveResult = MiiDbService.AddToDatabase(copyResult.Value, "02:11:11:11:11:11");
        if (saveResult.IsFailure)
        {
            ViewUtils.ShowSnackbar(t("snackbar_error.mii_failure_save", saveResult.Error.Message)!, ViewUtils.SnackbarType.Danger);
            return;
        }

        ReloadMiiList();
        ViewUtils.ShowSnackbar(t("snackbar_success.mii_added"));
    }

    #region Tags

    private void RebuildTagChips()
    {
        if (_activeTag is not null && !Tags.Tags.Contains(_activeTag, StringComparer.OrdinalIgnoreCase))
            _activeTag = null;

        ActiveTagLabel.Text = _activeTag is null ? string.Empty : _activeTag;
        ActiveTagLabel.IsVisible = _activeTag is not null;
    }

    private void TagsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        var all = new MenuItem { Header = _activeTag is null ? "All Miis (showing)" : "All Miis" };
        all.Click += (_, _) => SelectTag(null);
        menu.Items.Add(all);

        foreach (var tag in Tags.Tags)
        {
            var item = new MenuItem
            {
                Header = string.Equals(_activeTag, tag, StringComparison.OrdinalIgnoreCase) ? $"{tag} (showing)" : tag,
            };
            item.Click += (_, _) => SelectTag(tag);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var sort = new MenuItem { Header = "Sort Miis" };
        var byName = new MenuItem { Header = "Name (A-Z)" };
        byName.Click += SortByName_OnClick;
        var newest = new MenuItem { Header = "Newest first" };
        newest.Click += SortNewest_OnClick;
        var oldest = new MenuItem { Header = "Oldest first" };
        oldest.Click += SortOldest_OnClick;
        sort.Items.Add(byName);
        sort.Items.Add(newest);
        sort.Items.Add(oldest);
        menu.Items.Add(sort);
        menu.Items.Add(new Separator());

        var create = new MenuItem { Header = "New tag..." };
        create.Click += async (_, _) =>
        {
            var name = await CreateTagAsync();
            if (name is not null)
                SelectTag(name);
        };
        menu.Items.Add(create);

        if (Tags.Tags.Count > 0)
        {
            var rename = new MenuItem { Header = "Rename tag" };
            var delete = new MenuItem { Header = "Delete tag" };
            foreach (var tag in Tags.Tags)
            {
                var renameItem = new MenuItem { Header = tag };
                renameItem.Click += async (_, _) => await RenameTagAsync(tag);
                rename.Items.Add(renameItem);

                var deleteItem = new MenuItem { Header = tag };
                deleteItem.Click += async (_, _) => await DeleteTagAsync(tag);
                delete.Items.Add(deleteItem);
            }

            menu.Items.Add(rename);
            menu.Items.Add(delete);
        }

        menu.Open(TagsButton);
    }

    private void SelectTag(string? tag)
    {
        _activeTag = tag;
        ClearSelectedEntries();
        RebuildTagChips();
        RebuildRows(_savedMiiSearchText);
        ChangeTopButtons();
    }

    private async Task<string?> AskTagNameAsync(string title, string initial = "")
    {
        var input = await Popups
            .Create<TextInputWindow>()
            .SetMainText(title)
            .SetExtraText("For example: Friends, Racing, Favorites.")
            .SetPlaceholderText("Tag name")
            .SetInitialText(initial)
            .SetButtonText("Cancel", "Save")
            .ShowDialog();
        return string.IsNullOrWhiteSpace(input) ? null : input.Trim();
    }

    private async Task<string?> CreateTagAsync()
    {
        var name = await AskTagNameAsync("New tag");
        if (name is null)
            return null;

        if (!Tags.Create(name))
        {
            ViewUtils.ShowSnackbar("A tag with that name already exists.", ViewUtils.SnackbarType.Warning);
            return null;
        }

        RebuildTagChips();
        return name;
    }

    private async Task RenameTagAsync(string tag)
    {
        var name = await AskTagNameAsync("Rename tag", tag);
        if (name is null)
            return;

        Tags.Rename(tag, name);
        if (string.Equals(_activeTag, tag, StringComparison.OrdinalIgnoreCase))
            _activeTag = name;
        RebuildTagChips();
    }

    private async Task DeleteTagAsync(string tag)
    {
        var yes = await Popups
            .Create<YesNoWindow>()
            .SetMainText($"Delete the tag \"{tag}\"?")
            .SetExtraText("Your Miis stay as they are, only the tag is removed.")
            .AwaitAnswer();
        if (!yes)
            return;

        Tags.Delete(tag);
        if (string.Equals(_activeTag, tag, StringComparison.OrdinalIgnoreCase))
            _activeTag = null;
        RebuildTagChips();
        RebuildRows(_savedMiiSearchText);
    }

    private void TagContextMenu_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetMenuMii(sender, out var mii))
            return;

        var miis = GetSelectedMiis();
        if (!miis.Contains(mii))
            miis = [mii];

        var menu = new ContextMenu();
        foreach (var tag in Tags.Tags)
        {
            var allIn = miis.All(m => Tags.Contains(tag, m.MiiId));
            var item = new MenuItem { Header = allIn ? $"Remove from {tag}" : $"Add to {tag}" };
            item.Click += (_, _) =>
            {
                Tags.SetMembership(tag, miis.Select(m => m.MiiId), !allIn);
                RebuildRows(_savedMiiSearchText);
            };
            menu.Items.Add(item);
        }

        var create = new MenuItem { Header = "New tag..." };
        create.Click += async (_, _) =>
        {
            var name = await CreateTagAsync();
            if (name is null)
                return;

            Tags.SetMembership(name, miis.Select(m => m.MiiId), true);
            RebuildRows(_savedMiiSearchText);
        };
        menu.Items.Add(create);
        menu.Open((Control?)_pressedBlock ?? MiiList);
    }

    #endregion

    #region Drag to reorder

    private const double DragThreshold = 8;
    private MiiListEntry? _dragEntry;
    private Point _dragStart;
    private bool _isDragging;
    private MiiBlock? _dragBlock;
    private MiiBlock? _hoverBlock;

    private static MiiBlock? FindMiiBlock(object? source) => (source as Visual)?.FindAncestorOfType<MiiBlock>(includeSelf: true);

    private void MiiList_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragEntry = null;
        _isDragging = false;
        _pressedBlock = FindMiiBlock(e.Source);
        if (
            !e.GetCurrentPoint(MiiList).Properties.IsLeftButtonPressed
            || !string.IsNullOrWhiteSpace(_savedMiiSearchText)
            || _activeTag is not null
        )
            return;

        if (FindMiiBlock(e.Source) is { DataContext: MiiListEntry { Mii: not null } entry })
        {
            _dragEntry = entry;
            _dragBlock = FindMiiBlock(e.Source);
            _dragStart = e.GetPosition(MiiList);
        }
    }

    private void MiiList_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragEntry is null)
            return;

        if (!_isDragging)
        {
            var delta = e.GetPosition(MiiList) - _dragStart;
            if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
                return;

            _isDragging = true;
            Cursor = new Cursor(StandardCursorType.DragMove);
            _dragBlock?.Classes.Set("Dragging", true);
        }

        var over = FindMiiBlock(MiiList.InputHitTest(e.GetPosition(MiiList)));
        if (over is { DataContext: MiiListEntry { Mii: not null } } && over != _dragBlock)
        {
            if (over != _hoverBlock)
            {
                _hoverBlock?.Classes.Set("DropTarget", false);
                _hoverBlock = over;
                _hoverBlock.Classes.Set("DropTarget", true);
            }
        }
        else if (_hoverBlock is not null)
        {
            _hoverBlock.Classes.Set("DropTarget", false);
            _hoverBlock = null;
        }
    }

    private void MiiList_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var entry = _dragEntry;
        var wasDragging = _isDragging;
        EndDrag();
        if (!wasDragging || entry?.Mii is null)
            return;

        var target = FindMiiBlock(MiiList.InputHitTest(e.GetPosition(MiiList)));
        if (target?.DataContext is not MiiListEntry { Mii: { } targetMii } || targetMii.MiiId == entry.Mii.MiiId)
            return;

        var result = MiiDbService.Move(entry.Mii.MiiId, targetMii.MiiId);
        if (result.IsFailure)
        {
            ViewUtils.ShowSnackbar(result.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        ReloadMiiList();
    }

    private void MiiList_OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _dragBlock?.Classes.Set("Dragging", false);
        _hoverBlock?.Classes.Set("DropTarget", false);
        _dragBlock = null;
        _hoverBlock = null;
        _dragEntry = null;
        _isDragging = false;
        Cursor = Cursor.Default;
    }

    private void SortMiiButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { ContextMenu: { } menu } control)
            menu.Open(control);
    }

    private void SortByName_OnClick(object? sender, RoutedEventArgs e) =>
        SortMiis(miis => miis.OrderBy(m => m.Name.ToString(), StringComparer.CurrentCultureIgnoreCase));

    private void SortNewest_OnClick(object? sender, RoutedEventArgs e) => SortMiis(miis => miis.OrderByDescending(m => m.MiiId));

    private void SortOldest_OnClick(object? sender, RoutedEventArgs e) => SortMiis(miis => miis.OrderBy(m => m.MiiId));

    private void SortMiis(Func<IEnumerable<Mii>, IOrderedEnumerable<Mii>> order)
    {
        var sorted = order(MiiDbService.GetAllMiis()).Select(m => m.MiiId).ToList();
        var result = MiiDbService.Reorder(sorted);
        if (result.IsFailure)
        {
            ViewUtils.ShowSnackbar(result.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        ReloadMiiList();
    }

    #endregion

    #region Multi and Single select

    private bool _isShiftPressed;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Subscribe to keyboard events to track Shift key state
        KeyDown += MiiListPage_KeyDown;
        KeyUp += MiiListPage_KeyUp;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Unsubscribe from keyboard events when control is detached
        KeyDown -= MiiListPage_KeyDown;
        KeyUp -= MiiListPage_KeyUp;

        base.OnDetachedFromVisualTree(e);
    }

    private void MiiListPage_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl))
            return;

        _isShiftPressed = true;
    }

    private void MiiListPage_KeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl))
            return;

        _isShiftPressed = false;
    }

    #endregion

    private void ReloadMiiList()
    {
        _miiEntries.Clear();
        foreach (var mii in MiiDbService.GetAllMiis().OrderByDescending(m => m.IsFavorite))
        {
            _miiEntries.Add(new MiiListEntry(mii));
        }

        var count = _miiEntries.Count;
        ListItemCount.Text = count.ToString();

        if (count < 100)
            _miiEntries.Add(MiiListEntry.CreateAddEntry());

        RebuildTagChips();
        RebuildRows(_savedMiiSearchText);
        ChangeTopButtons();
    }

    private void RebuildRows(string? searchText = null)
    {
        var query = searchText?.Trim();
        var normalizedQuery = string.IsNullOrWhiteSpace(query) ? null : CustomCharactersService.NormalizeToAscii(query);
        IEnumerable<MiiListEntry> source = _miiEntries;
        if (_activeTag is { } tag)
            source = source.Where(entry => entry.Mii is { } m && Tags.Contains(tag, m.MiiId));

        var visibleEntries = (
            string.IsNullOrWhiteSpace(query) ? source : source.Where(entry => MiiNameMatchesQuery(entry, query, normalizedQuery))
        ).ToList();

        MiiRows.Clear();
        for (var i = 0; i < visibleEntries.Count; i += 3)
        {
            var chunk = visibleEntries.Skip(i).Take(3).ToList();
            MiiRows.Add(new MiiListRow(chunk));
        }
    }

    private bool MiiNameMatchesQuery(MiiListEntry entry, string query, string? normalizedQuery)
    {
        var miiName = entry.Mii?.Name.ToString();
        if (string.IsNullOrWhiteSpace(miiName))
            return false;

        if (miiName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;

        var normalizedMiiName = CustomCharactersService.NormalizeToAscii(miiName);
        return normalizedMiiName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearSelectedEntries()
    {
        foreach (var entry in _miiEntries)
            entry.IsSelected = false;
    }

    private void DeleteMii_OnClick(object? sender, RoutedEventArgs e) => DeleteMii(GetSelectedMiis());

    private void EditMii_OnClick(object? sender, RoutedEventArgs e) => EditMii(GetSelectedMiis()[0]);

    private void FavMii_OnClick(object? sender, RoutedEventArgs e) => ToggleFavorite(GetSelectedMiis());

    private void ExportMii_OnClick(object? sender, RoutedEventArgs e) => ExportMultipleMiiFiles(GetSelectedMiis());

    private void DuplicateMii_OnClick(object? sender, RoutedEventArgs e) => DuplicateMii(GetSelectedMiis());

    private async void ImportMii_OnClick(object? sender, RoutedEventArgs e)
    {
        var miiFiles = await FilePicker.OpenFilePickerAsync(
            fileType: FilePickerFilters.Miis,
            allowMultiple: true,
            title: "Select Mii file(s)"
        );
        if (miiFiles.Count == 0)
            return;

        // The list is reloaded at the end even when a file fails, so the Miis that were added before it show up.
        try
        {
            foreach (var file in miiFiles)
            {
                byte[] miiData;
                try
                {
                    // A Mii file is under a hundred bytes; anything far bigger is not one, so it is not read into memory.
                    if (FileSystem.FileInfo.New(file).Length > 4096)
                        throw new InvalidDataException("That file is too big to be a Mii.");
                    miiData = FileSystem.File.ReadAllBytes(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    ViewUtils.ShowSnackbar(
                        t("snackbar_error.mii_failure_deserialize", new { error = ex.Message })!,
                        ViewUtils.SnackbarType.Danger
                    );
                    return;
                }

                var result = MiiSerializer.Deserialize(miiData);
                if (result.IsFailure)
                {
                    ViewUtils.ShowSnackbar(
                        t("snackbar_error.mii_failure_deserialize", new { error = result.Error.Message })!,
                        ViewUtils.SnackbarType.Danger
                    );
                    return;
                }

                var mii = result.Value;

                //We duplicate to make sure it does not actually have the original MiiId
                var macAddress = "02:11:11:11:11:11";
                var saveResult = MiiDbService.AddToDatabase(mii, macAddress);
                if (saveResult.IsFailure)
                {
                    ViewUtils.ShowSnackbar(
                        t("snackbar_error.mii_failure_save", new { error = saveResult.Error.Message })!,
                        ViewUtils.SnackbarType.Danger
                    );
                    return;
                }
            }
        }
        finally
        {
            ReloadMiiList();
        }
    }

    private void MiiBlock_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MiiBlock { DataContext: MiiListEntry entry })
            return;

        if (entry.IsAddEntry)
        {
            ClearSelectedEntries();
            ChangeTopButtons();
            CreateNewMii();
            return;
        }

        if (!_isShiftPressed)
        {
            var shouldSelectThis = entry.IsSelected;
            foreach (var item in _miiEntries)
                item.IsSelected = false;

            entry.IsSelected = shouldSelectThis;
        }

        ChangeTopButtons();
    }

    private void FavoriteContextMenu_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetMenuMii(sender, out var mii))
            ContextAction(mii, ToggleFavorite);
    }

    private void EditContextMenu_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetMenuMii(sender, out var mii))
            EditMii(mii);
    }

    private void DuplicateContextMenu_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetMenuMii(sender, out var mii))
            ContextAction(mii, DuplicateMii);
    }

    private void ExportContextMenu_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetMenuMii(sender, out var mii))
            ContextAction(mii, ExportMultipleMiiFiles);
    }

    private void DeleteContextMenu_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetMenuMii(sender, out var mii))
            ContextAction(mii, DeleteMii);
    }

    private static bool TryGetMenuMii(object? sender, out Mii mii)
    {
        if (sender is MenuItem { CommandParameter: MiiListEntry { Mii: not null } entry })
        {
            mii = entry.Mii!;
            return true;
        }

        mii = null!;
        return false;
    }

    private void ToggleFavorite(Mii[] miis)
    {
        var allFavorite = miis.All(m => m.IsFavorite);

        foreach (var mii in miis)
        {
            mii.IsFavorite = !allFavorite;
            var result = MiiDbService.Update(mii);
            if (result.IsFailure)
            {
                ViewUtils.ShowSnackbar(
                    t("snackbar_error.mii_failure_update", new { error = result.Error.Message })!,
                    ViewUtils.SnackbarType.Danger
                );
                return;
            }
        }

        ReloadMiiList();
    }

    private void ExportMultipleMiiFiles(Mii[] miis)
    {
        if (miis.Length == 0)
        {
            ViewUtils.ShowSnackbar(t("snackbar_warning.no_mii_export"), ViewUtils.SnackbarType.Warning);
            return;
        }

        foreach (var mii in miis)
        {
            ExportMiiAsFile(mii);
        }
    }

    public static string ReplaceInvalidFileNameChars(string filename)
    {
        var invalid = new Testably.Abstractions.RealFileSystem().Path.GetInvalidFileNameChars();
        return string.Join("_", filename.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }

    private async void ExportMiiAsFile(Mii mii)
    {
        var exportName = ReplaceInvalidFileNameChars(CustomCharactersService.NormalizeToAscii(mii.Name.ToString()));
        var diaglog = await FilePicker.SaveFileAsync(
            title: "Save Mii as file",
            fileTypes: [FilePickerFilters.Miis],
            defaultFileName: $"{exportName}"
        );
        if (diaglog == null)
            return;
        var result = MiiDbService.GetByAvatarId(mii.MiiId);
        if (result.IsFailure)
        {
            ViewUtils.ShowSnackbar(
                t("snackbar_error.mii_failure_get", new { error = result.Error.Message })!,
                ViewUtils.SnackbarType.Danger
            );
            return;
        }

        var miiToExport = result.Value;
        var saveResult = SaveMiiToDisk(miiToExport, diaglog);
        if (saveResult.IsFailure)
        {
            ViewUtils.ShowSnackbar(
                t("snackbar_error.mii_failure_save", new { error = saveResult.Error.Message })!,
                ViewUtils.SnackbarType.Danger
            );
        }

        // SaveMiiToDisk already says it was saved, so there is no second message here.
    }

    private OperationResult SaveMiiToDisk(Mii mii, string path)
    {
        var miiData = MiiSerializer.Serialize(mii);
        if (miiData.IsFailure)
        {
            ViewUtils.ShowSnackbar(
                t("snackbar_error.mii_failure_serialize", new { error = miiData.Error.Message })!,
                ViewUtils.SnackbarType.Danger
            );
            return miiData;
        }

        var file = FileSystem.FileInfo.New(path);
        // A read-only folder or a file that is open elsewhere must give a message, not take the launcher down.
        try
        {
            using var stream = file.Open(FileMode.Create, FileAccess.Write);
            stream.Write(miiData.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ex.Message);
        }

        ViewUtils.ShowSnackbar(
            t("snackbar_success.saved_mii", new { name = mii.Name, fileName = file.FullName }) ?? "Saved Mii successfully"
        );
        return Ok();
    }

    private async void DeleteMii(Mii[] miis)
    {
        if (miis.Length == 0)
        {
            ViewUtils.ShowSnackbar(t("snackbar_warning.no_mii_delete"), ViewUtils.SnackbarType.Warning);
            return;
        }

        // TODO: add a check that you cant remove a Mii that is in use by a licence,
        // I have no idea how tho

        if (miis.Any(mii => mii.IsFavorite))
        {
            await MessageTranslationHelper.AwaitMessageAsync(MessageTranslation.Warning_CantDeleteFavMii);
            return;
        }

        var mainText = t("question.sure_delete.title_miis", new { amount = miis.Length }) ?? $"Delete {miis.Length}?";
        var successMessage = t("snackbar_success.deleted_miis", new { amount = miis.Length }) ?? $"Deleted {miis.Length}";
        if (miis.Length == 1)
        {
            mainText = t("question.sure_delete.title", new { name = miis[0].Name }) ?? $"Delete {miis[0].Name}?";
            successMessage = t("snackbar_success.deleted", new { name = miis[0].Name }) ?? $"Deleted {miis[0].Name}";
        }

        var result = await Popups.Create<YesNoWindow>().SetMainText(mainText).SetExtraText(t("question.sure_delete.extra")).AwaitAnswer();
        if (!result)
            return;

        foreach (var mii in miis)
        {
            MiiDbService.Remove(mii.MiiId);
        }

        ReloadMiiList();
        ViewUtils.ShowSnackbar(successMessage);
    }

    // The editor saves the Mii itself and comes back to this page.
    private void EditMii(Mii mii) => Navigation.NavigateTo<MiiEditorPage>(new MiiEditorRequest(mii));

    private void CreateNewMii() => Navigation.NavigateTo<MiiEditorPage>(new MiiEditorRequest(null));

    private void DuplicateMii(Mii[] miis)
    {
        //assuming the mac address is already set correctly
        var macAddress = SettingsService.Get<string>(SettingsService.MACADDRESS);
        foreach (var mii in miis)
        {
            var result = MiiDbService.AddToDatabase(mii, macAddress);
            if (!result.IsFailure)
                continue;

            ViewUtils.ShowSnackbar(
                t("snackbar_error.mii_failure_duplicate", new { error = result.Error.Message })!,
                ViewUtils.SnackbarType.Danger
            );
            return;
        }

        var successMessage = t("snackbar_success.created_duplicates_miis", new { amount = miis.Length })!;
        if (miis.Length == 1)
            successMessage = t("snackbar_success.created_duplicate", new { name = miis[0].Name })!;

        ReloadMiiList();
        ViewUtils.ShowSnackbar(successMessage);
    }

    private Mii[] GetSelectedMiis()
    {
        return _miiEntries.Where(entry => entry is { IsSelected: true, Mii: not null }).Select(entry => entry.Mii!).ToArray();
    }

    private void ChangeTopButtons()
    {
        var selectedMiis = GetSelectedMiis();

        if (selectedMiis.Length == 0)
        {
            DeleteMiisButton.IsVisible = false;
            ExportMiisButton.IsVisible = false;
            EditMiisButton.IsVisible = false;
            DuplicateMiisButton.IsVisible = false;
            HeaderImportMiiButton.IsVisible = true;
            SearchSavedMiiButton.IsVisible = true;
            SavedMiiFilterPanel.IsVisible = !OnlineMiiPanel.IsVisible;
            FavoriteMiiButton.IsVisible = false;
            return;
        }

        FavoriteMiiButton.IsVisible = true;
        EditMiisButton.IsVisible = selectedMiis.Length == 1;
        HeaderImportMiiButton.IsVisible = false;
        SearchSavedMiiButton.IsVisible = true;
        DeleteMiisButton.IsVisible = true;
        ExportMiisButton.IsVisible = true;
        DuplicateMiisButton.IsVisible = true;

        FavoriteMiiButton.Classes.Remove("UnFav");
        if (selectedMiis.All(mii => mii.IsFavorite))
            FavoriteMiiButton.Classes.Add("UnFav");
    }

    private void ContextAction(Mii mii, Action<Mii[]> command)
    {
        var selectedMiis = GetSelectedMiis();
        // If the user right clicks and perform action on a selected Mii, that action applies for all the selected Miis
        // But if the user right clicks and perform actions on a mii that is not selected, than it only happens for that specific mii.
        command.Invoke(selectedMiis.Contains(mii) ? selectedMiis : [mii]);
    }

    public sealed class MiiListRow(List<MiiListEntry> items)
    {
        public IReadOnlyList<MiiListEntry> Items { get; } = items;
    }

    public sealed class MiiListEntry(Mii? mii, bool isAddEntry = false) : INotifyPropertyChanged
    {
        private bool _isSelected;

        public Mii? Mii { get; } = mii;
        public bool IsAddEntry { get; } = isAddEntry;
        public bool HasMii => Mii != null;
        public string SelectionGroup { get; } = Guid.NewGuid().ToString("N");
        public string FavoriteActionHeader => Mii?.IsFavorite == true ? t("action.unfavorite") : t("action.favorite");

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public static MiiListEntry CreateAddEntry() => new(null, true);

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
