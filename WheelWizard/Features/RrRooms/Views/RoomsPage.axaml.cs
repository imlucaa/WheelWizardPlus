using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WheelWizard.Models.RRInfo;
using WheelWizard.RrRooms;
using WheelWizard.Settings;
using WheelWizard.Shared.Polling;
using WheelWizard.Views.Dialogs;
using WheelWizard.Views.Shell.Navigation;
using WheelWizard.WheelWizardData;
using WheelWizard.WiiManagement.GameLicense;

namespace WheelWizard.RrRooms.Views;

public partial class RoomsPage : UserControl, INotifyPropertyChanged, IPollingListener
{
    private INavigationService Navigation { get; }

    private LiveRoomsService LiveRooms { get; }

    private string? _searchQuery;
    private int? _minimumAverageVr;
    private int? _maximumAverageVr;
    private int _averageVrSort;
    private bool _friendsOnly;
    private bool _refreshing;
    private readonly DispatcherTimer _autoTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _raceTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly ObservableCollection<RrRoom> _rooms = [];

    public ObservableCollection<RrRoom> Rooms
    {
        get => _rooms;
        init
        {
            _rooms = value;
            OnPropertyChanged(nameof(Rooms));
        }
    }

    private readonly ObservableCollection<RrPlayer> _players = [];

    public ObservableCollection<RrPlayer> Players
    {
        get => _players;
        init
        {
            _players = value;
            OnPropertyChanged(nameof(Players));
        }
    }

    private IPopupFactory Popups { get; }

    private IGameLicenseSingletonService GameLicenseService { get; }

    private ISettingsManager Settings { get; }

    public RoomsPage(
        INavigationService navigation,
        LiveRoomsService liveRooms,
        IPopupFactory popups,
        IGameLicenseSingletonService gameLicenseService,
        ISettingsManager settings
    )
    {
        Settings = settings;
        Navigation = navigation;
        LiveRooms = liveRooms;
        Popups = popups;
        GameLicenseService = gameLicenseService;
        InitializeComponent();
        DataContext = this;
        // The sort, the VR range and friends-only come back the way they were left.
        var saved = RoomsViewState.Parse(Settings.Get<string>(Settings.ROOMS_VIEW));
        _averageVrSort = saved.Sort;
        _minimumAverageVr = saved.MinimumVr;
        _maximumAverageVr = saved.MaximumVr;
        _friendsOnly = saved.FriendsOnly;
        ShowFriendsButton();
        ShowFilterButtons();
        _autoTimer.Tick += AutoTimer_OnTick;
        _autoTimer.Start();
        LiveRooms.Subscribe(this);
        // The friends come from the save, which is read after the first room list: the page redraws when it is.
        GameLicenseService.Subscribe(this);
        _raceTimer.Tick += RaceTimer_OnTick;
        _raceTimer.Start();

        OnUpdate(LiveRooms);
        Unloaded += RoomsPage_Unloaded;
    }

    public void OnUpdate(ObservablePollingService sender)
    {
        if (sender is not LiveRoomsService liveRooms)
        {
            if (_friendsOnly && LiveRooms.RoomCount > 0)
                ApplyFilters();
            return;
        }

        var count = liveRooms.RoomCount;
        EmptyRoomsView.IsVisible = count == 0;
        RoomsListViewContainer.IsVisible = count != 0;
        if (count == 0)
            return;

        ApplyFilters(keepScroll: true);
    }

    private void ApplyFilters(bool keepScroll = false)
    {
        var query = _searchQuery?.Trim();
        var friendCodes = _friendsOnly ? CurrentFriendCodes() : null;
        IEnumerable<RrRoom> filteredRooms = LiveRooms
            .CurrentRooms.Where(room => room.PlayerCount > 0)
            .Where(room => !_minimumAverageVr.HasValue || room.AverageVr >= _minimumAverageVr.Value)
            .Where(room => !_maximumAverageVr.HasValue || room.AverageVr <= _maximumAverageVr.Value)
            .Where(room => friendCodes is null || room.Players.Any(player => friendCodes.Contains(DigitsOf(player.FriendCode))))
            .Where(room =>
                string.IsNullOrWhiteSpace(query)
                || room.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || room.GameMode.Contains(query, StringComparison.OrdinalIgnoreCase)
                || room.GameModeAbbrev.Contains(query, StringComparison.OrdinalIgnoreCase)
                || room.Players.Any(player =>
                    player.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || player.FriendCode.Contains(query, StringComparison.OrdinalIgnoreCase)
                )
            );

        filteredRooms = _averageVrSort switch
        {
            1 => filteredRooms.OrderBy(room => room.AverageVr),
            2 => filteredRooms.OrderByDescending(room => room.AverageVr),
            3 => filteredRooms.OrderBy(room => room.AverageVr).Take(1),
            4 => filteredRooms.OrderByDescending(room => room.AverageVr).Take(1),
            _ => filteredRooms,
        };

        var visibleRooms = filteredRooms.ToList();
        // A refresh redraws the list; the place in it is kept so it does not jump to the top.
        var scroller = keepScroll ? RoomsListBox.Scroll as ScrollViewer : null;
        var offset = scroller?.Offset;
        // Players you flagged show in red, and their rooms get a red flag.
        PlayerFlags.Apply(LiveRooms.CurrentRooms, PlayerFlags.Load(Settings));
        Rooms.Clear();
        foreach (var room in visibleRooms)
            Rooms.Add(room);
        if (scroller is not null && offset is { Y: > 0 } restore)
            Dispatcher.UIThread.Post(() => scroller.Offset = restore, DispatcherPriority.Background);

        Players.Clear();
        RoomsListItemCount.Text = visibleRooms.Count.ToString();
        RoomsListViewContainer.IsVisible = visibleRooms.Count != 0;
        PlayerListViewContainer.IsVisible = false;
        EmptyRoomsView.IsVisible = visibleRooms.Count == 0;
        if (visibleRooms.Count == 0)
        {
            // Rooms exist but the search, VR range or sort hides all of them.
            EmptyRoomsView.Title = _friendsOnly ? "No friends in a room" : "No rooms match";
            EmptyRoomsView.BodyText = _friendsOnly
                ? "None of your friends are in a room right now."
                : "Try another search, VR range or sort.";
        }
    }

    private void PerformSearch(string? query)
    {
        var isStringEmpty = string.IsNullOrWhiteSpace(query);
        RoomsListViewContainer.IsVisible = isStringEmpty;
        PlayerListViewContainer.IsVisible = !isStringEmpty;

        if (isStringEmpty)
            return;

        var safeQuery = query ?? string.Empty;
        Players.Clear();
        var matchingPlayers = Rooms
            .SelectMany(r => r.Players)
            .Where(p =>
                (p.Name?.Contains(safeQuery, StringComparison.OrdinalIgnoreCase) ?? false)
                || (p.FriendCode?.Contains(safeQuery, StringComparison.OrdinalIgnoreCase) ?? false)
            )
            .Distinct()
            .ToList();

        foreach (var player in matchingPlayers)
            Players.Add(player);

        PlayerListItemCount.Text = matchingPlayers.Count.ToString();
    }

    private void RoomsPage_Unloaded(object? sender, RoutedEventArgs e)
    {
        _raceTimer.Stop();
        _autoTimer.Stop();
        LiveRooms.Unsubscribe(this);
        GameLicenseService.Unsubscribe(this);
    }

    private void RaceTimer_OnTick(object? sender, EventArgs e)
    {
        foreach (var room in Rooms)
            room.RefreshRaceTime();
    }

    private async void RoomsSearchButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var input = await Popups
            .Create<TextInputWindow>()
            .SetMainText("Search rooms")
            .SetExtraText("Search by room ID, game mode, player name, or friend code.")
            .SetPlaceholderText("Room, player, or friend code")
            .SetInitialText(_searchQuery ?? string.Empty)
            .SetButtonText("Clear", "Search")
            .ShowDialog();

        _searchQuery = string.IsNullOrWhiteSpace(input) ? null : input.Trim();
        ApplyFilters();
    }

    private void RoomsFriendsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _friendsOnly = !_friendsOnly;
        ShowFriendsButton();
        SaveView();
        ApplyFilters();
    }

    private void ShowFriendsButton() =>
        RoomsFriendsButton.Variant = _friendsOnly
            ? WheelWizard.Views.Components.Button.ButtonsVariantType.Primary
            : WheelWizard.Views.Components.Button.ButtonsVariantType.Default;

    // A saved sort or VR range is easy to forget, so the buttons stay lit while they are on.
    private void ShowFilterButtons()
    {
        RoomsVrFilterButton.Variant =
            _minimumAverageVr.HasValue || _maximumAverageVr.HasValue
                ? WheelWizard.Views.Components.Button.ButtonsVariantType.Primary
                : WheelWizard.Views.Components.Button.ButtonsVariantType.Default;
        RoomsSortButton.Variant =
            _averageVrSort != 0
                ? WheelWizard.Views.Components.Button.ButtonsVariantType.Primary
                : WheelWizard.Views.Components.Button.ButtonsVariantType.Default;
    }

    // The room list is read again every 10 seconds while this page is open (the background poll is every 40).
    private async void AutoTimer_OnTick(object? sender, EventArgs e)
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            await LiveRooms.RefreshNowAsync();
            OnUpdate(LiveRooms);
        }
        catch (Exception)
        {
            // A failed refresh keeps the list as it is; the next tick tries again.
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void SaveView() =>
        Settings.Set(
            Settings.ROOMS_VIEW,
            new RoomsViewState(_averageVrSort, _minimumAverageVr, _maximumAverageVr, _friendsOnly).Serialize()
        );

    /// <summary>The friend codes (digits only) of the focused license's friends.</summary>
    private HashSet<string> CurrentFriendCodes()
    {
        try
        {
            return GameLicenseService.ActiveCurrentFriends.Select(friend => DigitsOf(friend.FriendCode)).ToHashSet();
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException or NullReferenceException)
        {
            return [];
        }
    }

    private static string DigitsOf(string? friendCode) => new((friendCode ?? string.Empty).Where(char.IsDigit).ToArray());

    private async void RoomsVrFilterButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var range = await new VrFilterWindow(_minimumAverageVr, _maximumAverageVr).AwaitAnswer();
        if (range is null)
            return;

        _minimumAverageVr = range.Min;
        _maximumAverageVr = range.Max;
        ShowFilterButtons();
        SaveView();
        ApplyFilters();
    }

    private async void RoomsSortButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var selected = await new RoomSortWindow(_averageVrSort).AwaitAnswer();
        if (selected is null)
            return;

        _averageVrSort = selected.Value;
        ShowFilterButtons();
        SaveView();
        ApplyFilters();
    }

    public static int? ParseVrFilter(string? text) =>
        int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var value) && value >= 0
            ? value
            : null;

    private void RoomsView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not ListBox listBox)
            return;
        if (listBox.SelectedItem is not RrRoom selectedRoom)
            return;

        Navigation.NavigateTo<RoomDetailsPage>(selectedRoom);
        listBox.SelectedItem = null;
        // Deselect the item immediately after navigating. This is important
        // for a good user experience. Otherwise, the item stays selected,
        // and if you navigate back, you can't re-select the same item.
    }

    private void PlayerView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not ListBox listBox)
            return;
        if (listBox.SelectedItem is not RrPlayer selectedRoom)
            return;

        var room = Rooms.FirstOrDefault(r => r.Players.Any(p => p.Equals(selectedRoom)));

        if (room is not null)
            Navigation.NavigateTo<RoomDetailsPage>(room);
        listBox.SelectedItem = null;
        // Deselect the item immediately after navigating. This is important
        // for a good user experience. Otherwise, the item stays selected,
        // and if you navigate back, you can't re-select the same item.
    }

    #region PropertyChanged

    public new event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new(propertyName));
    }

    #endregion
}
