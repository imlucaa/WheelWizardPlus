using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using WheelWizard.Models;
using WheelWizard.Models.RRInfo;
using WheelWizard.Mods.Views;
using WheelWizard.RrRooms;
using WheelWizard.Settings;
using WheelWizard.Shared.Polling;
using WheelWizard.Views.DesignTime;
using WheelWizard.Views.Dialogs;
using WheelWizard.Views.Shell;
using WheelWizard.Views.Shell.Navigation;
using WheelWizard.WheelWizardData;
using WheelWizard.WheelWizardData.Views.Dialogs;
using WheelWizard.WiiManagement.FriendCodes;
using WheelWizard.WiiManagement.GameLicense;
using WheelWizard.WiiManagement.MiiManagement;

namespace WheelWizard.RrRooms.Views;

public partial class RoomDetailsPage : UserControl, INotifyPropertyChanged, IPollingListener
{
    private IPopupFactory Popups { get; } = null!;

    private INavigationService Navigation { get; } = null!;

    private LiveRoomsService LiveRooms { get; } = null!;

    private IGameLicenseSingletonService GameDataService { get; } = null!;

    private IMiiDbService MiiDbService { get; } = null!;

    private ISettingsManager SettingsService { get; } = null!;

    private RrRoom _room = null!;

    public RrRoom Room
    {
        get => _room;
        set
        {
            _room = value;
            OnPropertyChanged(nameof(Room));
        }
    }

    private readonly ObservableCollection<RrPlayer> _playersList = [];

    public ObservableCollection<RrPlayer> PlayersList
    {
        get => _playersList;
        init
        {
            _playersList = value;
            OnPropertyChanged(nameof(PlayersList));
        }
    }

    public RoomDetailsPage()
    {
        InitializeComponent();
        DataContext = this;
        Room = RrRoomFactory.Instance.Create(); // Create a fake room for design-time preview
        PlayersList = new(Room.Players);
    }

    public RoomDetailsPage(
        IPopupFactory popups,
        INavigationService navigation,
        LiveRoomsService liveRooms,
        IGameLicenseSingletonService gameDataService,
        IMiiDbService miiDbService,
        ISettingsManager settingsService,
        RrRoom room
    )
    {
        Popups = popups;
        Navigation = navigation;
        LiveRooms = liveRooms;
        GameDataService = gameDataService;
        MiiDbService = miiDbService;
        SettingsService = settingsService;
        // Set before the page is built, or every Room.* binding is evaluated against null and logs a warning.
        _room = room;
        InitializeComponent();
        DataContext = this;

        PlayerFlags.Apply([Room], PlayerFlags.Load(SettingsService));
        PlayersList = new(Room.Players);
        WirePlayerClicks();

        LiveRooms.Subscribe(this);
        Unloaded += RoomsDetailPage_Unloaded;
    }

    public void OnUpdate(ObservablePollingService sender)
    {
        if (sender is not LiveRoomsService liveRooms)
            return;

        var room = liveRooms.CurrentRooms.Find(r => r.Id == Room.Id);

        if (room == null)
        {
            // Reason we do this incase room gets disbanded or something idk
            Navigation.NavigateTo<RoomsPage>();
            return;
        }

        Room = room;
        ShowPlayers();
    }

    private void ShowPlayers()
    {
        PlayerFlags.Apply([Room], PlayerFlags.Load(SettingsService));
        PlayersList.Clear();
        foreach (var p in Room.Players)
        {
            PlayersList.Add(p);
        }
    }

    /// <summary>The flag sub-menu: each note flags the selected player with one click; "Other…" asks for a note.</summary>
    // The player menu is opened by hand (left click), so it is taken off the list; otherwise a right click would open it too.
    private ContextMenu? _playerMenu;

    /// <summary>The menu shown when a player is left-clicked (copy, add friend, flag...).</summary>
    public ContextMenu? PlayerMenu => _playerMenu;

    /// <summary>
    /// Left click opens the player's stats straight away. Right click opens the player menu, every time (also on the
    /// player that is already selected).
    /// </summary>
    private void WirePlayerClicks()
    {
        _playerMenu = PlayersListView.ContextMenu;
        PlayersListView.ContextMenu = null;
        // Both are caught on the way in (tunnel), so no control inside the row and no pointer capture elsewhere in the
        // window can keep them from the list. The right-click menu opens when the button is let go: opened while it is
        // still held, letting go would land on the first menu item and pick it straight away.
        PlayersListView.AddHandler(PointerPressedEvent, PlayersList_OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        PlayersListView.AddHandler(PointerReleasedEvent, PlayersList_OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static RrPlayer? PlayerUnder(RoutedEventArgs e) =>
        (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as RrPlayer;

    private RrPlayer? _rightPressed;

    private void PlayersList_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _rightPressed = null;
        if (!e.GetCurrentPoint(PlayersListView).Properties.IsRightButtonPressed || PlayerUnder(e) is not { } player)
            return;

        e.Handled = true;
        _rightPressed = player;
        PlayersListView.SelectedItem = player;
    }

    private void PlayersList_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var player = PlayerUnder(e);
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            var pressed = _rightPressed;
            _rightPressed = null;
            if (player is null || !ReferenceEquals(player, pressed))
                return;

            e.Handled = true;
            OpenPlayerMenu(player);
            return;
        }

        if (e.InitialPressMouseButton != MouseButton.Left || player is null)
            return;

        PlayersListView.SelectedItem = player;
        if (!string.IsNullOrEmpty(player.FriendCode))
            Popups.Create<PlayerProfileWindow>(player.FriendCode).Show();
    }

    /// <summary>Shows the player menu for a player, with the flag items set up for them.</summary>
    public void OpenPlayerMenu(RrPlayer player)
    {
        if (MenuItemTagged("unflag") is { } unflag)
            unflag.IsVisible = player.IsFlagged;
        FillFlagMenu(player);
        FillColorMenu(player);
        _playerMenu?.Open(PlayersListView);
    }

    // Names inside a context menu are not reliably found, so the two flag items are looked up by their tag.
    private MenuItem? MenuItemTagged(string tag) => _playerMenu?.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, tag));

    private void FillFlagMenu(RrPlayer? player)
    {
        if (MenuItemTagged("flag") is not { } flagMenu)
            return;

        var items = new List<object>();
        foreach (var note in PlayerFlags.NoteChoices(PlayerFlags.Load(SettingsService)))
        {
            var item = new MenuItem { Header = note };
            if (player is { IsFlagged: true } && player.FlagNote == note)
                item.Icon = new TextBlock { Text = "✓" };
            var chosen = note;
            item.Click += (_, _) => FlagSelected(chosen);
            items.Add(item);
        }

        items.Add(new Separator());
        var other = new MenuItem { Header = "Other…" };
        other.Click += FlagPlayerOther_OnClick;
        items.Add(other);
        flagMenu.ItemsSource = items;
    }

    /// <summary>The colour sub-menu, only for a flagged player: a dot and a name per colour, the current one ticked.</summary>
    private void FillColorMenu(RrPlayer player)
    {
        if (MenuItemTagged("color") is not { } colorMenu)
            return;

        colorMenu.IsVisible = player.IsFlagged;
        var items = new List<object>();
        foreach (var (name, hex) in PlayerFlags.Colors)
        {
            var item = new MenuItem
            {
                Header = player.FlagColor.Equals(hex, StringComparison.OrdinalIgnoreCase) ? $"{name}  ✓" : name,
                Icon = new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(hex)),
                },
            };
            var chosen = hex;
            item.Click += (_, _) =>
            {
                if (PlayersListView.SelectedItem is not RrPlayer selected)
                    return;
                PlayerFlags.SaveColor(SettingsService, selected.FriendCode, chosen);
                ShowPlayers();
            };
            items.Add(item);
        }

        colorMenu.ItemsSource = items;
    }

    private void FlagSelected(string note)
    {
        if (PlayersListView.SelectedItem is not RrPlayer player || string.IsNullOrWhiteSpace(player.FriendCode))
            return;

        PlayerFlags.Save(SettingsService, player.FriendCode, note);
        ShowPlayers();
    }

    private async void FlagPlayerOther_OnClick(object? sender, RoutedEventArgs e)
    {
        if (PlayersListView.SelectedItem is not RrPlayer player || string.IsNullOrWhiteSpace(player.FriendCode))
            return;

        var note = await Popups
            .Create<TextInputWindow>()
            .SetMainText($"Flag {player.Name}")
            .SetExtraText("They show in red in rooms, with your note. Only you see it.")
            .SetPlaceholderText("Wii off, VR farmer…")
            .SetInitialText(player.FlagNote)
            .SetButtonText("Cancel", "Flag")
            .ShowDialog();
        if (string.IsNullOrWhiteSpace(note))
            return;

        PlayerFlags.Save(SettingsService, player.FriendCode, note);
        ShowPlayers();
    }

    private void UnflagPlayer_OnClick(object sender, RoutedEventArgs e)
    {
        if (PlayersListView.SelectedItem is not RrPlayer player || !player.IsFlagged)
            return;

        PlayerFlags.Save(SettingsService, player.FriendCode, null);
        ShowPlayers();
    }

    private void GoBackClick(object? sender, RoutedEventArgs eventArgs) => Navigation.NavigateTo<RoomsPage>();

    private void CopyFriendCode_OnClick(object sender, RoutedEventArgs e)
    {
        if (PlayersListView.SelectedItem is not RrPlayer selectedPlayer)
            return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(selectedPlayer.FriendCode);
        ViewUtils.ShowSnackbar(t("snackbar_success.copied_fc"));
    }

    private void ViewProfile_OnClick(object sender, RoutedEventArgs e)
    {
        if (PlayersListView.SelectedItem is not RrPlayer selectedPlayer)
            return;
        if (string.IsNullOrEmpty(selectedPlayer.FriendCode))
            return;
        Popups.Create<PlayerProfileWindow>(selectedPlayer.FriendCode).Show();
    }

    private void ViewOnRwfc_OnClick(object sender, RoutedEventArgs e)
    {
        if (PlayersListView.SelectedItem is RrPlayer selectedPlayer)
            ViewUtils.OpenRwfcPlayer(selectedPlayer.FriendCode);
    }

    private async void AddFriend_OnClick(object sender, RoutedEventArgs e)
    {
        if (PlayersListView.SelectedItem is not RrPlayer selectedPlayer)
            return;

        if (selectedPlayer.FirstMii == null)
        {
            ViewUtils.ShowSnackbar("This player has no valid Mii data.", ViewUtils.SnackbarType.Warning);
            return;
        }

        var focusedUserIndex = SettingsService.Get<int>(SettingsService.FOCUSED_USER);
        if (focusedUserIndex is < 0 or > 3)
        {
            ViewUtils.ShowSnackbar("Invalid license selected.", ViewUtils.SnackbarType.Warning);
            return;
        }

        var activeUserPid = FriendCode.FriendCodeToProfileId(GameDataService.ActiveUser.FriendCode);
        if (activeUserPid == 0)
        {
            ViewUtils.ShowSnackbar("Select a valid license before adding friends.", ViewUtils.SnackbarType.Warning);
            return;
        }

        if (GameDataService.ActiveCurrentFriends.Count >= 30)
        {
            ViewUtils.ShowSnackbar("Your friend list is full.", ViewUtils.SnackbarType.Warning);
            return;
        }

        var normalizedFriendCodeResult = NormalizeFriendCode(selectedPlayer.FriendCode);
        if (normalizedFriendCodeResult.IsFailure)
        {
            ViewUtils.ShowSnackbar(normalizedFriendCodeResult.Error.Message, ViewUtils.SnackbarType.Warning);
            return;
        }

        var normalizedFriendCode = normalizedFriendCodeResult.Value;
        var friendProfileId = FriendCode.FriendCodeToProfileId(normalizedFriendCode);
        if (activeUserPid == friendProfileId)
        {
            ViewUtils.ShowSnackbar("You cannot add your own friend code.", ViewUtils.SnackbarType.Warning);
            return;
        }

        var duplicateFriend = GameDataService.ActiveCurrentFriends.Any(friend =>
        {
            var existingPid = FriendCode.FriendCodeToProfileId(friend.FriendCode);
            return existingPid != 0 && existingPid == friendProfileId;
        });

        if (duplicateFriend)
        {
            ViewUtils.ShowSnackbar("This friend is already in your list.", ViewUtils.SnackbarType.Warning);
            return;
        }

        var profile = new PlayerProfileResponse
        {
            Name = selectedPlayer.Name,
            FriendCode = normalizedFriendCode,
            Vr = Math.Max(selectedPlayer.Vr ?? 0, 0),
        };

        var shouldAdd = await new AddFriendConfirmationWindow(profile, selectedPlayer.FirstMii).AwaitAnswer();
        if (!shouldAdd)
            return;

        var addResult = GameDataService.AddFriend(
            focusedUserIndex,
            normalizedFriendCode,
            selectedPlayer.FirstMii,
            (uint)Math.Max(selectedPlayer.Vr ?? 0, 0)
        );

        if (addResult.IsFailure)
        {
            ViewUtils.ShowSnackbar(addResult.Error.Message, ViewUtils.SnackbarType.Warning);
            return;
        }

        ViewUtils.GetLayout().UpdateFriendCount();
        ViewUtils.ShowSnackbar($"Added {selectedPlayer.Name} to your friend list.");
    }

    private static OperationResult<string> NormalizeFriendCode(string friendCode)
    {
        if (string.IsNullOrWhiteSpace(friendCode))
            return Fail("Friend code cannot be empty.");

        var digits = new string(friendCode.Where(char.IsDigit).ToArray());
        if (digits.Length != 12 || !ulong.TryParse(digits, out _))
            return Fail("Friend code must be exactly 12 digits.");

        var formatted = $"{digits[..4]}-{digits.Substring(4, 4)}-{digits.Substring(8, 4)}";
        var profileId = FriendCode.FriendCodeToProfileId(formatted);
        if (profileId == 0)
            return Fail("Invalid friend code.");

        return formatted;
    }

    private void RoomsDetailPage_Unloaded(object? sender, RoutedEventArgs e)
    {
        LiveRooms.Unsubscribe(this);
    }

    #region PropertyChanged

    public new event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new(propertyName));
    }

    #endregion
}
