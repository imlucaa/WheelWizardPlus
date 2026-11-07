using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WheelWizard.Models.RRInfo;
using WheelWizard.RrRooms;
using WheelWizard.RrRooms.Views;
using WheelWizard.Settings;
using WheelWizard.Shared.Polling;
using WheelWizard.Views.Dialogs;
using WheelWizard.Views.Shell.Navigation;
using WheelWizard.WheelWizardData;
using WheelWizard.WheelWizardData.Views.Dialogs;
using WheelWizard.WiiManagement.GameLicense;
using WheelWizard.WiiManagement.MiiManagement;

namespace WheelWizard.UI.Test;

public class PlayerFlagMenuTests
{
    [AvaloniaFact]
    public void SelectingAPlayer_FillsFlagPlayer_WithOneClickNotes()
    {
        var licenses = Substitute.For<IGameLicenseSingletonService>();
        var liveRooms = new LiveRoomsService(
            Substitute.For<IRrBadgeSingletonService>(),
            Substitute.For<IRrRoomsSingletonService>(),
            Substitute.For<IRrLeaderboardSingletonService>(),
            licenses,
            new RoomPresence(),
            Substitute.For<IPollingScheduler>(),
            TimeProvider.System,
            NullLogger<LiveRoomsService>.Instance
        );
        var room = new RrRoom
        {
            Id = "room",
            Created = DateTime.UtcNow,
            Type = "public",
            Suspend = false,
            Players =
            [
                new()
                {
                    Pid = "1",
                    Name = "Ann",
                    FriendCode = "1111-2222-3333",
                },
            ],
        };
        var page = new RoomDetailsPage(
            Substitute.For<IPopupFactory>(),
            Substitute.For<INavigationService>(),
            liveRooms,
            licenses,
            Substitute.For<IMiiDbService>(),
            Substitute.For<ISettingsManager>(),
            room
        );
        var window = new Window { Content = page };
        window.Show();

        var list = page.FindControl<ListBox>("PlayersListView")!;
        // The menu is opened by a left click, so a right click on the list must not open it by itself.
        Assert.Null(list.ContextMenu);

        page.OpenPlayerMenu(room.Players[0]);

        var menu = page.PlayerMenu!;
        var flag = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "flag"));
        var notes = flag.ItemsSource!.OfType<MenuItem>().Select(item => item.Header as string).ToList();
        Assert.Equal(["Wii off", "VR farmer", "Troll", "Cheater", "Other…"], notes);

        // Not flagged yet, so there is nothing to remove.
        Assert.False(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "unflag")).IsVisible);

        // The notes list opens in a popup of its own; it has the same dark background as the menu.
        flag.IsSubMenuOpen = true;
        var subMenu = flag.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single().Child as Border;
        var background = Assert.IsType<Avalonia.Media.SolidColorBrush>(subMenu!.Background);
        Assert.Equal(Avalonia.Media.Color.Parse("#090A0C"), background.Color);
        menu.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void LeftClickingAPlayer_OpensTheirStats_AndRightClickOpensTheMenu()
    {
        var licenses = Substitute.For<IGameLicenseSingletonService>();
        var liveRooms = new LiveRoomsService(
            Substitute.For<IRrBadgeSingletonService>(),
            Substitute.For<IRrRoomsSingletonService>(),
            Substitute.For<IRrLeaderboardSingletonService>(),
            licenses,
            new RoomPresence(),
            Substitute.For<IPollingScheduler>(),
            TimeProvider.System,
            NullLogger<LiveRoomsService>.Instance
        );
        var room = new RrRoom
        {
            Id = "room",
            Created = DateTime.UtcNow,
            Type = "public",
            Suspend = false,
            Players =
            [
                new()
                {
                    Pid = "1",
                    Name = "Ann",
                    FriendCode = "1111-2222-3333",
                },
            ],
        };
        var popups = Substitute.For<IPopupFactory>();
        var profiles = 0;
        popups.When(factory => factory.Create<PlayerProfileWindow>(Arg.Any<object[]>())).Do(_ => profiles++);
        var page = new RoomDetailsPage(
            popups,
            Substitute.For<INavigationService>(),
            liveRooms,
            licenses,
            Substitute.For<IMiiDbService>(),
            Substitute.For<ISettingsManager>(),
            room
        );
        var window = new Window
        {
            Content = page,
            Width = 600,
            Height = 700,
        };
        window.Show();
        window.UpdateLayout();

        var item = page.GetVisualDescendants().OfType<ListBoxItem>().First();
        var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;

        // The substitute factory has no real window to show, so showing it throws; the call is what matters here.
        try
        {
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }
        catch (NullReferenceException) { }

        Assert.Equal(1, profiles);
        Assert.False(page.PlayerMenu!.IsOpen);

        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Assert.True(page.PlayerMenu.IsOpen);
        Assert.Equal(1, profiles);
        page.PlayerMenu.Close();
        window.Close();
    }
}
