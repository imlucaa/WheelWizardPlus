using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Testably.Abstractions;
using WheelWizard.Localization;
using WheelWizard.MiiAnimations.Library;
using WheelWizard.MiiRendering.Services;
using WheelWizard.Models;
using WheelWizard.RrRooms;
using WheelWizard.RrRooms.Views;
using WheelWizard.Settings;
using WheelWizard.Shared.Calendar;
using WheelWizard.Shared.Services;
using WheelWizard.Views.Dialogs;
using WheelWizard.Views.Shell;
using WheelWizard.Views.Shell.Navigation;
using WheelWizard.WheelWizardData;
using WheelWizard.WheelWizardData.Domain;
using WheelWizard.WheelWizardData.Views.Dialogs;
using WheelWizard.WiiManagement.FriendCodes;
using WheelWizard.WiiManagement.GameLicense;
using WheelWizard.WiiManagement.MiiManagement;
using WheelWizard.WiiManagement.MiiManagement.Domain.Mii;
using WheelWizard.WiiManagement.MiiManagement.Views.Editor;

namespace WheelWizard.WheelWizardData.Views;

public sealed record LeaderboardPlayerItem
{
    public required int Rank { get; init; }
    public required string PlacementLabel { get; init; }
    public required string Name { get; init; }
    public required string FriendCode { get; init; }
    public required string VrText { get; init; }
    public Mii? Mii { get; init; }
    public BadgeVariant[] BadgeVariants { get; init; } = [];
    public BadgeVariant PrimaryBadge { get; init; }
    public bool HasBadge { get; init; }
    public bool IsSuspicious { get; init; }
    public bool IsEvenRow { get; init; }
    public bool IsFriend { get; init; }
    public bool IsOnline { get; init; }

    // Keep parity with RoomDetailsPage player template bindings.
    public string VrDisplay => VrText;
    public Mii? FirstMii => Mii;
    public bool HasBadges => HasBadge;
    public bool IsTopLeaderboardPlayer => true;
    public WheelWizard.Views.Components.StatusVariant RankBadgeVariant => WheelWizard.Views.Components.StatusVariant.Gray;

    public string TopLabel => t("placement.other", new { rank = Rank });
    public bool IsOpenHost => false;
}

public sealed record LeaderboardSearchMatch(int Rank, string Name, string FriendCode, string VrText, Mii? Mii, bool IsSuspicious)
{
    public string RankText => Rank > 0 ? $"Rank #{Rank:N0}" : "Unranked";
}

public partial class LeaderboardPage : UserControl, INotifyPropertyChanged
{
    private static readonly LeaderboardPlayerItem EmptyPodiumPlayer = new()
    {
        Rank = 0,
        PlacementLabel = string.Empty,
        Name = string.Empty,
        FriendCode = string.Empty,
        VrText = string.Empty,
        PrimaryBadge = BadgeVariant.None,
        HasBadge = false,
        IsSuspicious = false,
        IsEvenRow = true,
    };

    private CancellationTokenSource? _loadCts;

    private IRrLeaderboardSingletonService LeaderboardService { get; }
    private IRrBadgeSingletonService BadgeService { get; }
    private IGameLicenseSingletonService GameDataService { get; }
    private ISettingsManager SettingsManager { get; }
    private IApiCaller<IRwfcApi> ApiCaller { get; }
    private IMiiDbService MiiDbService { get; }
    private LiveRoomsService LiveRooms { get; }
    private IPopupFactory Popups { get; }
    private INavigationService Navigation { get; }

    private bool _hasLoadedOnce;
    private bool _isLoading;
    private bool _hasError;
    private bool _hasNoData;
    private bool _hasData;
    private string _errorMessage = string.Empty;
    private LeaderboardPlayerItem? _podiumFirst;
    private LeaderboardPlayerItem? _podiumSecond;
    private LeaderboardPlayerItem? _podiumThird;
    private readonly List<LeaderboardSearchMatch> _searchMatches = [];
    private int _selectedSearchMatchIndex = -1;
    private int _currentLeaderboardPage = 1;
    private int _totalLeaderboardPages = 1;
    private string _leaderboardSearchStatus = string.Empty;
    private bool _isSearching;

    public ObservableCollection<LeaderboardPlayerItem> RemainingPlayers { get; } = [];

    public bool HasError
    {
        get => _hasError;
        private set
        {
            if (_hasError == value)
                return;
            _hasError = value;
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value)
                return;
            _isLoading = value;
            OnPropertyChanged(nameof(IsLoading));
        }
    }

    public bool HasNoData
    {
        get => _hasNoData;
        private set
        {
            if (_hasNoData == value)
                return;
            _hasNoData = value;
            OnPropertyChanged(nameof(HasNoData));
        }
    }

    public bool HasData
    {
        get => _hasData;
        private set
        {
            if (_hasData == value)
                return;
            _hasData = value;
            OnPropertyChanged(nameof(HasData));
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (_errorMessage == value)
                return;
            _errorMessage = value;
            OnPropertyChanged(nameof(ErrorMessage));
        }
    }

    public string RemainingCountText => $"{RemainingPlayers.Count} players";
    public string LeaderboardPageText => $"Page {_currentLeaderboardPage} / {_totalLeaderboardPages}";
    public bool CanGoToPreviousPage => _currentLeaderboardPage > 1 && !IsLoading;
    public bool CanGoToNextPage => _currentLeaderboardPage < _totalLeaderboardPages && !IsLoading;
    public bool HasActiveSearchResult => _selectedSearchMatchIndex >= 0 && _selectedSearchMatchIndex < _searchMatches.Count;
    public bool HasLeaderboardSearchStatus => !string.IsNullOrWhiteSpace(_leaderboardSearchStatus);
    public string LeaderboardSearchStatus => _leaderboardSearchStatus;
    public bool CanSearchLeaderboard => !_isSearching;
    public IReadOnlyList<LeaderboardSearchMatch> SearchMatches => _searchMatches.Take(MaxShownMatches).ToList();
    private const int MaxShownMatches = 20;

    public string SearchResultMatchIndex =>
        _searchMatches.Count > MaxShownMatches
            ? $"Showing {MaxShownMatches} of {Math.Max(_leaderboardSearchTotalMatches, _searchMatches.Count)}"
            : $"{_searchMatches.Count} players";
    private LeaderboardSearchMatch? CurrentSearchMatch => HasActiveSearchResult ? _searchMatches[_selectedSearchMatchIndex] : null;
    public string PodiumTitleText =>
        HasActiveSearchResult ? "Player"
        : _currentLeaderboardPage == 1 ? "Top 3 Podium"
        : "Page leaders";
    public string PodiumCaptionText =>
        HasActiveSearchResult ? string.Empty
        : _currentLeaderboardPage == 1 ? "Live from RWFC"
        : LeaderboardPageText;

    public LeaderboardPlayerItem? PodiumFirst
    {
        get => _podiumFirst ?? EmptyPodiumPlayer;
        private set => SetPodiumPlayer(ref _podiumFirst, value, nameof(PodiumFirst), nameof(HasPodiumFirst));
    }

    public LeaderboardPlayerItem? PodiumSecond
    {
        get => _podiumSecond ?? EmptyPodiumPlayer;
        private set => SetPodiumPlayer(ref _podiumSecond, value, nameof(PodiumSecond), nameof(HasPodiumSecond));
    }

    public LeaderboardPlayerItem? PodiumThird
    {
        get => _podiumThird ?? EmptyPodiumPlayer;
        private set => SetPodiumPlayer(ref _podiumThird, value, nameof(PodiumThird), nameof(HasPodiumThird));
    }

    public bool HasPodiumFirst => _podiumFirst != null;
    public bool HasPodiumSecond => _podiumSecond != null;
    public bool HasPodiumThird => _podiumThird != null;

    public LeaderboardPage(
        IApiCaller<IRwfcApi> apiCaller,
        IMiiDbService miiDbService,
        LiveRoomsService liveRooms,
        IPopupFactory popups,
        INavigationService navigation,
        IRrLeaderboardSingletonService leaderboardService,
        IRrBadgeSingletonService badgeService,
        IGameLicenseSingletonService gameDataService,
        ISettingsManager settingsManager,
        IMiiNativeRenderer miiRenderer,
        IMiiAnimationLibrary miiAnimations,
        ISeasonalCalendar calendar,
        IRandomSystem random
    )
    {
        LeaderboardService = leaderboardService;
        BadgeService = badgeService;
        GameDataService = gameDataService;
        SettingsManager = settingsManager;
        ApiCaller = apiCaller;
        MiiDbService = miiDbService;
        LiveRooms = liveRooms;
        Popups = popups;
        Navigation = navigation;
        InitializeComponent();
        DataContext = this;
        PodiumStage.Initialize(miiRenderer, miiAnimations, random.Random.Shared, calendar, settingsManager.ENABLE_ANIMATIONS.Get());
        RemainingPlayers.CollectionChanged += RemainingPlayers_OnCollectionChanged;

        Loaded += LeaderboardPage_Loaded;
        Unloaded += LeaderboardPage_Unloaded;
    }

    private async void LeaderboardPage_Loaded(object? sender, RoutedEventArgs e)
    {
        if (_hasLoadedOnce)
            return;

        _hasLoadedOnce = true;
        await ReloadLeaderboardAsync();
    }

    private void LeaderboardPage_Unloaded(object? sender, RoutedEventArgs e)
    {
        CancelCurrentLoad();
    }

    private void RemainingPlayers_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(RemainingCountText));
    }

    private async void SearchLeaderboardPlayer_OnClick(object? sender, RoutedEventArgs e)
    {
        var input = await Popups
            .Create<TextInputWindow>()
            .SetMainText("Search leaderboard")
            .SetExtraText("Search by player name or 12-digit friend code.")
            .SetPlaceholderText("Player or 0000-0000-0000")
            .SetButtonText("Cancel", "Search")
            .ShowDialog();

        if (input == null)
            return;

        await SearchLeaderboardPlayer(input);
    }

    private async Task SearchLeaderboardPlayer(string? requestedQuery = null)
    {
        var query = requestedQuery?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            ClearLeaderboardSearchResult();
            if (_currentLeaderboardPage != 1 || !HasData)
                await ReloadLeaderboardAsync(1);
            return;
        }

        var normalizedFriendCode = TryNormalizeFriendCode(query);
        _isSearching = true;
        OnPropertyChanged(nameof(CanSearchLeaderboard));

        SetLeaderboardSearchStatus(
            normalizedFriendCode != null ? $"Looking up {normalizedFriendCode}…" : $"Searching RWFC for \"{query}\"…",
            false
        );

        try
        {
            if (normalizedFriendCode != null)
            {
                // The profile popup loads the profile itself and says so when there is none.

                ClearLeaderboardSearchResult();

                OpenProfile(normalizedFriendCode);

                return;
            }

            var searchResult = await ApiCaller.CallApiAsync(api => api.SearchLeaderboardAsync(query));
            if (searchResult.IsFailure || searchResult.Value == null)
            {
                SetLeaderboardSearchStatus("RWFC leaderboard search failed. Try again in a moment.", true);
                return;
            }

            var matches = searchResult
                .Value.Players.Select((entry, index) => new { Entry = entry, Rank = ResolveRank(entry, index) })
                .Where(item => !string.IsNullOrWhiteSpace(item.Entry.FriendCode))
                .Select(item => new LeaderboardSearchMatch(
                    item.Rank,
                    string.IsNullOrWhiteSpace(item.Entry.Name) ? "Unknown Player" : item.Entry.Name,
                    item.Entry.FriendCode,
                    item.Entry.Vr?.ToString("N0") ?? "--",
                    DeserializeMii(item.Entry.MiiData),
                    item.Entry.IsSuspicious
                ))
                .ToList();

            if (matches.Count == 0)
            {
                SetLeaderboardSearchStatus($"No leaderboard players matched \"{query}\".", true);
                return;
            }

            SetLeaderboardSearchMatches(matches, searchResult.Value.TotalCount);
        }
        finally
        {
            _isSearching = false;
            OnPropertyChanged(nameof(CanSearchLeaderboard));
        }
    }

    private async void RetryButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ReloadLeaderboardAsync();
    }

    private async void PreviousLeaderboardPage_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_currentLeaderboardPage <= 1)
            return;

        ClearLeaderboardSearchResult();
        await ReloadLeaderboardAsync(_currentLeaderboardPage - 1);
    }

    private async void NextLeaderboardPage_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_currentLeaderboardPage >= _totalLeaderboardPages)
            return;

        ClearLeaderboardSearchResult();
        await ReloadLeaderboardAsync(_currentLeaderboardPage + 1);
    }

    private int _leaderboardSearchTotalMatches;

    private void SetLeaderboardSearchMatches(List<LeaderboardSearchMatch> matches, int totalMatches)
    {
        if (matches.Count == 1)
        {
            ClearLeaderboardSearchResult();
            OpenProfile(matches[0].FriendCode);
            return;
        }

        _searchMatches.Clear();
        _searchMatches.AddRange(matches);
        _leaderboardSearchTotalMatches = totalMatches > 0 ? totalMatches : matches.Count;
        _selectedSearchMatchIndex = matches.Count > 0 ? 0 : -1;
        ApplyCurrentLeaderboardSearchMatch();
    }

    private void ClearLeaderboardSearchResult()
    {
        _searchMatches.Clear();
        _selectedSearchMatchIndex = -1;
        _leaderboardSearchTotalMatches = 0;
        _leaderboardSearchStatus = string.Empty;
        ApplyCurrentLeaderboardSearchMatch();
        OnPropertyChanged(nameof(HasLeaderboardSearchStatus));
        OnPropertyChanged(nameof(LeaderboardSearchStatus));
        OnPropertyChanged(nameof(HasActiveSearchResult));
        OnPropertyChanged(nameof(PodiumTitleText));
        OnPropertyChanged(nameof(PodiumCaptionText));
    }

    private void ApplyCurrentLeaderboardSearchMatch()
    {
        if (_selectedSearchMatchIndex < 0 || _selectedSearchMatchIndex >= _searchMatches.Count)
        {
            NotifySearchResultChanged();
            return;
        }

        _leaderboardSearchStatus = string.Empty;
        NotifySearchResultChanged();
    }

    private void NotifySearchResultChanged()
    {
        OnPropertyChanged(nameof(HasActiveSearchResult));
        OnPropertyChanged(nameof(HasLeaderboardSearchStatus));
        OnPropertyChanged(nameof(LeaderboardSearchStatus));
        OnPropertyChanged(nameof(SearchMatches));
        OnPropertyChanged(nameof(SearchResultMatchIndex));
        OnPropertyChanged(nameof(PodiumTitleText));
        OnPropertyChanged(nameof(PodiumCaptionText));
    }

    private void SetLeaderboardSearchStatus(string message, bool isError)
    {
        _leaderboardSearchStatus = message;
        OnPropertyChanged(nameof(HasLeaderboardSearchStatus));
        OnPropertyChanged(nameof(LeaderboardSearchStatus));
        OnPropertyChanged(nameof(PodiumCaptionText));
    }

    /// <summary>Opens the leaderboard profile of the license that is selected in the launcher.</summary>
    private void MyRank_OnClick(object? sender, RoutedEventArgs e)
    {
        string? friendCode;
        try
        {
            friendCode = GameDataService.ActiveUser.FriendCode;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException or NullReferenceException)
        {
            friendCode = null;
        }

        if (string.IsNullOrWhiteSpace(friendCode) || TryNormalizeFriendCode(friendCode) is not { } normalized)
        {
            ViewUtils.ShowSnackbar("Your selected license has no friend code yet.", ViewUtils.SnackbarType.Warning);
            return;
        }

        OpenProfile(normalized);
    }

    private void OpenProfile(string friendCode)
    {
        if (!string.IsNullOrWhiteSpace(friendCode))
            Popups.Create<PlayerProfileWindow>(friendCode).Show();
    }

    private object? _pressedMatchRow;

    private void SearchMatchRow_OnPointerPressed(object? sender, PointerPressedEventArgs e) => _pressedMatchRow = sender;

    private void SearchMatchRow_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pressed = _pressedMatchRow;
        _pressedMatchRow = null;
        if (!ReferenceEquals(pressed, sender) || e.InitialPressMouseButton != MouseButton.Left)
            return;

        if ((sender as Control)?.DataContext is LeaderboardSearchMatch match)
            OpenProfile(match.FriendCode);
    }

    private void CloseSearchResult_OnClick(object? sender, RoutedEventArgs e)
    {
        ClearLeaderboardSearchResult();
    }

    /// <summary>
    /// The profile ids of the selected license's friends, used to mark them on the leaderboard. Empty when no save is
    /// loaded yet (a fresh install), so the leaderboard still shows instead of failing half way.
    /// </summary>
    private HashSet<uint> CurrentFriendProfileIds()
    {
        try
        {
            return GameDataService
                .ActiveCurrentFriends.Select(friend => FriendCode.FriendCodeToProfileId(friend.FriendCode))
                .Where(profileId => profileId != 0)
                .ToHashSet();
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException or NullReferenceException)
        {
            return [];
        }
    }

    private async Task ReloadLeaderboardAsync(int page = 1)
    {
        CancelCurrentLoad();
        _loadCts = new();
        var cancellationToken = _loadCts.Token;

        SetLoadingState();
        ClearLeaderboardData();
        await Task.Yield();

        var leaderboardResult = await ApiCaller.CallApiAsync(api => api.GetLeaderboardPageAsync(page));
        if (cancellationToken.IsCancellationRequested)
            return;

        if (leaderboardResult.IsFailure)
        {
            SetErrorState(leaderboardResult.Error?.Message ?? "Unable to fetch leaderboard.");
            return;
        }

        var leaderboardPage = leaderboardResult.Value;
        if (leaderboardPage == null)
        {
            SetErrorState("Unable to fetch leaderboard.");
            return;
        }

        _currentLeaderboardPage = leaderboardPage.CurrentPage > 0 ? leaderboardPage.CurrentPage : page;
        _totalLeaderboardPages = leaderboardPage.TotalPages > 0 ? leaderboardPage.TotalPages : 1;
        OnPropertyChanged(nameof(LeaderboardPageText));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
        OnPropertyChanged(nameof(PodiumTitleText));
        OnPropertyChanged(nameof(PodiumCaptionText));

        var orderedEntries = leaderboardPage
            .Players.Select((entry, index) => new { Entry = entry, Rank = ResolveRank(entry, index) })
            .OrderBy(entry => entry.Rank)
            .ToList();

        var friendProfileIds = CurrentFriendProfileIds();
        var onlineProfileIds = LiveRooms
            .CurrentRooms.SelectMany(room => room.Players)
            .Select(player => FriendCode.FriendCodeToProfileId(player.FriendCode))
            .Where(profileId => profileId != 0)
            .ToHashSet();

        if (orderedEntries.Count == 0)
        {
            SetEmptyState();
            return;
        }

        List<LeaderboardPlayerItem> mappedPlayers;
        try
        {
            mappedPlayers = await Task.Run(
                () =>
                    orderedEntries
                        .Select(
                            (entry, index) => CreateLeaderboardPlayer(entry.Entry, entry.Rank, index, friendProfileIds, onlineProfileIds)
                        )
                        .ToList(),
                cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
            return;

        PodiumFirst = mappedPlayers.ElementAtOrDefault(0);
        PodiumSecond = mappedPlayers.ElementAtOrDefault(1);
        PodiumThird = mappedPlayers.ElementAtOrDefault(2);

        if (cancellationToken.IsCancellationRequested)
            return;

        foreach (var player in mappedPlayers.Skip(3))
        {
            RemainingPlayers.Add(player);
        }

        SetDataState();
    }

    private LeaderboardPlayerItem CreateLeaderboardPlayer(
        RwfcLeaderboardEntry entry,
        int rank,
        int index,
        IReadOnlySet<uint> friendProfileIds,
        IReadOnlySet<uint> onlineProfileIds
    )
    {
        var friendCode = entry.FriendCode ?? string.Empty;
        var profileId = FriendCode.FriendCodeToProfileId(friendCode);
        var badges = string.IsNullOrWhiteSpace(friendCode) ? [] : BadgeService.GetBadges(friendCode);
        var primaryBadge = badges.FirstOrDefault(BadgeVariant.None);

        return new()
        {
            Rank = rank,
            PlacementLabel = GetPlacementLabel(rank),
            Name = string.IsNullOrWhiteSpace(entry.Name) ? t("empty_content.no_mii_name") : entry.Name,
            FriendCode = friendCode,
            VrText = entry.Vr?.ToString("N0") ?? "--",
            Mii = DeserializeMii(entry.MiiData),
            PrimaryBadge = primaryBadge,
            HasBadge = primaryBadge != BadgeVariant.None,
            IsSuspicious = entry.IsSuspicious,
            IsEvenRow = index % 2 == 0,
            IsFriend = profileId != 0 && friendProfileIds.Contains(profileId),
            IsOnline = profileId != 0 && onlineProfileIds.Contains(profileId),
        };
    }

    public static int ResolveRank(RwfcLeaderboardEntry entry, int index)
    {
        if (entry.Rank is > 0)
            return entry.Rank.Value;

        if (entry.ActiveRank is > 0)
            return entry.ActiveRank.Value;

        return index + 1;
    }

    private string GetPlacementLabel(int rank)
    {
        return rank switch
        {
            1 => t("placement.first"),
            2 => t("placement.second"),
            3 => t("placement.third"),
            _ => t("placement.other", new { rank = rank }),
        };
    }

    private static Mii? DeserializeMii(string? miiData)
    {
        if (string.IsNullOrWhiteSpace(miiData))
            return null;

        // Mii block payload should be ~100 base64 chars (74 bytes decoded).
        // Guarding the size avoids expensive decode failures for large non-Mii payloads.
        if (miiData.Length is < 90 or > 120)
            return null;

        var buffer = new byte[MiiSerializer.MiiBlockSize];
        if (!Convert.TryFromBase64String(miiData, buffer, out var bytesWritten) || bytesWritten != MiiSerializer.MiiBlockSize)
            return null;

        var result = MiiSerializer.Deserialize(buffer);
        return result.IsSuccess ? result.Value : null;
    }

    private void SetPodiumPlayer(
        ref LeaderboardPlayerItem? field,
        LeaderboardPlayerItem? value,
        string propertyName,
        string hasPropertyName
    )
    {
        if (field == value)
            return;

        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(hasPropertyName);
    }

    private void SetLoadingState()
    {
        IsLoading = true;
        HasError = false;
        HasNoData = false;
        HasData = false;
        ErrorMessage = string.Empty;
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
    }

    private void SetErrorState(string message)
    {
        IsLoading = false;
        HasError = true;
        HasNoData = false;
        HasData = false;
        ErrorMessage = string.IsNullOrWhiteSpace(message) ? "Failed to load leaderboard." : message;
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
    }

    private void SetEmptyState()
    {
        IsLoading = false;
        HasError = false;
        HasNoData = true;
        HasData = false;
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
    }

    private void SetDataState()
    {
        IsLoading = false;
        HasError = false;
        HasNoData = false;
        HasData = true;
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
    }

    private void ClearLeaderboardData()
    {
        PodiumFirst = null;
        PodiumSecond = null;
        PodiumThird = null;
        RemainingPlayers.Clear();
        OnPropertyChanged(nameof(RemainingCountText));
    }

    private void CancelCurrentLoad()
    {
        if (_loadCts == null)
            return;

        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = null;
    }

    private void CopyFriendCode_OnClick(object sender, RoutedEventArgs e)
    {
        var player = GetContextPlayer(sender);
        if (player == null || string.IsNullOrWhiteSpace(player.FriendCode))
            return;

        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(player.FriendCode);
        ViewUtils.ShowSnackbar(t("snackbar_success.copied_fc"));
    }

    private void ViewProfile_OnClick(object sender, RoutedEventArgs e)
    {
        var player = GetContextPlayer(sender);
        if (player == null || string.IsNullOrWhiteSpace(player.FriendCode))
            return;

        Popups.Create<PlayerProfileWindow>(player.FriendCode).Show();
    }

    private void ViewOnRwfc_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetContextPlayer(sender) is { } player)
            ViewUtils.OpenRwfcPlayer(player.FriendCode);
    }

    private async void AddFriend_OnClick(object sender, RoutedEventArgs e)
    {
        var player = GetContextPlayer(sender);
        if (player == null)
            return;

        if (player.FirstMii == null)
        {
            ViewUtils.ShowSnackbar(t("snackbar_warning.mii_invalid"), ViewUtils.SnackbarType.Warning);
            return;
        }

        var focusedUserIndex = SettingsManager.Get<int>(SettingsManager.FOCUSED_USER);
        if (focusedUserIndex is < 0 or > 3)
        {
            ViewUtils.ShowSnackbar(t("snackbar_warning.license_invalid"), ViewUtils.SnackbarType.Warning);
            return;
        }

        var activeUserPid = FriendCode.FriendCodeToProfileId(GameDataService.ActiveUser.FriendCode);
        if (activeUserPid == 0)
        {
            ViewUtils.ShowSnackbar(t("snackbar_warning.license_invalid_self"), ViewUtils.SnackbarType.Warning);
            return;
        }

        if (GameDataService.ActiveCurrentFriends.Count >= 30)
        {
            ViewUtils.ShowSnackbar(t("snackbar_warning.friend_list_full"), ViewUtils.SnackbarType.Warning);
            return;
        }

        var normalizedFriendCodeResult = NormalizeFriendCode(player.FriendCode);
        if (normalizedFriendCodeResult.IsFailure)
        {
            ViewUtils.ShowSnackbar(normalizedFriendCodeResult.Error.Message, ViewUtils.SnackbarType.Warning);
            return;
        }

        var normalizedFriendCode = normalizedFriendCodeResult.Value;
        var friendProfileId = FriendCode.FriendCodeToProfileId(normalizedFriendCode);
        if (activeUserPid == friendProfileId)
        {
            ViewUtils.ShowSnackbar(t("snackbar_warning.add_own_fc"), ViewUtils.SnackbarType.Warning);
            return;
        }

        var duplicateFriend = GameDataService.ActiveCurrentFriends.Any(friend =>
        {
            var existingPid = FriendCode.FriendCodeToProfileId(friend.FriendCode);
            return existingPid != 0 && existingPid == friendProfileId;
        });

        if (duplicateFriend)
        {
            ViewUtils.ShowSnackbar(t("snackbar_warning.add_existing_friend"), ViewUtils.SnackbarType.Warning);
            return;
        }

        var profile = new PlayerProfileResponse
        {
            Name = player.Name,
            FriendCode = normalizedFriendCode,
            Vr = int.TryParse(player.VrText.Replace(",", string.Empty), out var vr) ? vr : 0,
        };

        var shouldAdd = await new AddFriendConfirmationWindow(profile, player.FirstMii).AwaitAnswer();
        if (!shouldAdd)
            return;

        var addResult = GameDataService.AddFriend(focusedUserIndex, normalizedFriendCode, player.FirstMii, (uint)Math.Max(profile.Vr, 0));

        if (addResult.IsFailure)
        {
            ViewUtils.ShowSnackbar(addResult.Error.Message, ViewUtils.SnackbarType.Warning);
            return;
        }

        ViewUtils.GetLayout().UpdateFriendCount();
        ViewUtils.ShowSnackbar(t("snackbar_success.friend_added", new { name = player.Name }));
    }

    private void CopyMii_OnClick(object sender, RoutedEventArgs e)
    {
        var player = GetContextPlayer(sender);
        if (player is not { FirstMii: not null })
        {
            ViewUtils.ShowSnackbar("This player has no valid Mii data.", ViewUtils.SnackbarType.Warning);
            return;
        }

        var serialized = MiiSerializer.Serialize(player.FirstMii);
        if (serialized.IsFailure)
        {
            ViewUtils.ShowSnackbar(serialized.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        var copy = MiiSerializer.Deserialize(serialized.Value);
        if (copy.IsFailure)
        {
            ViewUtils.ShowSnackbar(copy.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        var save = MiiDbService.AddToDatabase(copy.Value, "02:11:11:11:11:11");
        if (save.IsFailure)
        {
            ViewUtils.ShowSnackbar(save.Error.Message, ViewUtils.SnackbarType.Danger);
            return;
        }

        ViewUtils.ShowSnackbar($"Copied {player.Name}'s Mii to My Miis.");
    }

    private void ViewPlayerOnRwfc_OnClick(object sender, RoutedEventArgs e)
    {
        var player = GetContextPlayer(sender);
        if (player == null)
            return;

        var normalizedFriendCode = NormalizeFriendCode(player.FriendCode);
        if (normalizedFriendCode.IsFailure)
        {
            ViewUtils.ShowSnackbar("This player has no valid RWFC profile link.", ViewUtils.SnackbarType.Warning);
            return;
        }

        ViewUtils.OpenLink($"https://rwfc.net/player/{Uri.EscapeDataString(normalizedFriendCode.Value)}");
    }

    private void JoinRoom_OnClick(string friendCode)
    {
        if (string.IsNullOrWhiteSpace(friendCode))
            return;

        foreach (var room in LiveRooms.CurrentRooms)
        {
            if (room.Players.All(player => player.FriendCode != friendCode))
                continue;

            Navigation.NavigateTo<RoomDetailsPage>(room);
            return;
        }

        ViewUtils.ShowSnackbar(t("snackbar_warning.no_active_room"), ViewUtils.SnackbarType.Warning);
    }

    private static LeaderboardPlayerItem? GetContextPlayer(object sender)
    {
        if (sender is not Control control)
            return null;
        return control.DataContext as LeaderboardPlayerItem;
    }

    private static OperationResult<string> NormalizeFriendCode(string friendCode)
    {
        if (string.IsNullOrWhiteSpace(friendCode))
            return Fail(t("snackbar_warning.empty_fc"));

        var digits = new string(friendCode.Where(char.IsDigit).ToArray());
        if (digits.Length != 12 || !ulong.TryParse(digits, out _))
            return Fail(t("snackbar_warning.invalid_fc"));

        var formatted = $"{digits[..4]}-{digits.Substring(4, 4)}-{digits.Substring(8, 4)}";
        var profileId = FriendCode.FriendCodeToProfileId(formatted);
        if (profileId == 0)
            return Fail(t("snackbar_warning.nonexisting_fc"));

        return formatted;
    }

    private static string? TryNormalizeFriendCode(string? friendCode)
    {
        if (string.IsNullOrWhiteSpace(friendCode))
            return null;

        var digits = new string(friendCode.Where(char.IsDigit).ToArray());
        return digits.Length == 12 ? $"{digits[..4]}-{digits.Substring(4, 4)}-{digits.Substring(8, 4)}" : null;
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new(propertyName));
    }
}
