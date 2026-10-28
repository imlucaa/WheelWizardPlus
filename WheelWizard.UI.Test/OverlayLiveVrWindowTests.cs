using System.Linq.Expressions;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using NSubstitute;
using WheelWizard.ApplicationData;
using WheelWizard.Models;
using WheelWizard.Models.RRInfo;
using WheelWizard.RrRooms;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using WheelWizard.Shared;
using WheelWizard.Shared.Services;
using WheelWizard.StreamOverlay;
using WheelWizard.WiiManagement.GameLicense;

namespace WheelWizard.UI.Test;

/// <summary>Plays a race against the overlay window: the profile and the room change, and the card has to follow.</summary>
public class OverlayLiveVrWindowTests
{
    private const string FriendCode = "1111-2222-3333";

    private sealed class FakeApi(IRwfcApi api) : IApiCaller<IRwfcApi>
    {
        public async Task<OperationResult<TResult>> CallApiAsync<TResult>(Expression<Func<IRwfcApi, Task<TResult>>> apiCall) =>
            await apiCall.Compile()(api);
    }

    [AvaloniaFact]
    public async Task TheWindow_FollowsTheProfile_AndPrefersTheVrOfTheRoomThePlayerIsIn()
    {
        var profile = new PlayerProfileResponse
        {
            Name = "Racer",
            FriendCode = FriendCode,
            Vr = 10000,
            Rank = 5,
        };
        var rwfc = Substitute.For<IRwfcApi>();
        rwfc.GetPlayerProfileAsync(FriendCode).Returns(_ => Task.FromResult(profile));

        var friendSetting = new WhWzSetting<string>("OverlayFriendCode", "");
        var settings = Substitute.For<ISettingsManager>();
        settings.OVERLAY_FRIEND_CODE.Returns(friendSetting);
        settings
            .Get<string>(Arg.Any<Setting<string>>())
            .Returns(call => ReferenceEquals(call.Arg<Setting<string>>(), friendSetting) ? FriendCode : "");

        var presence = new RoomPresence();
        var api = new FakeApi(rwfc);
        var live = new OverlayLive(settings, Substitute.For<IGameLicenseSingletonService>());
        OverlayWindow.ShowOrActivate(
            new OverlayConfig(settings),
            api,
            live,
            new OverlayBackground(Substitute.For<IApplicationDataLocation>()),
            presence
        );
        var window = OverlayWindow.Current!;
        try
        {
            await SettleAsync(window);
            await Shows(window, 10000.ToString("N0"), "+0");

            // The race ends and the profile catches up.
            profile.Vr = 10050;
            await RefreshAsync(window);
            await Shows(window, 10050.ToString("N0"), "+50");

            // The room shows the new VR first; the profile still has the old one.
            presence.SetRatings(
                [
                    new RrPlayer
                    {
                        FriendCode = FriendCode,
                        Pid = "1",
                        Name = "Racer",
                        Vr = 10100,
                    },
                ]
            );
            await RefreshAsync(window);
            await Shows(window, 10100.ToString("N0"), "+100");

            // The room list itself is read too, so it counts even when the room poll has not run: 150 more than the profile.
            presence.SetRatings([]);
            rwfc.GetRoomStatusAsync()
                .Returns(
                    Task.FromResult(
                        new RwfcRoomStatusResponse
                        {
                            Rooms =
                            [
                                new RwfcRoomStatusRoom
                                {
                                    Id = "ROOM01",
                                    Type = "public",
                                    Created = DateTime.UtcNow,
                                    Players =
                                    [
                                        new RwfcRoomStatusPlayer
                                        {
                                            Pid = "1",
                                            FriendCode = FriendCode,
                                            Vr = 10200,
                                        },
                                    ],
                                },
                            ],
                        }
                    )
                );
            await RefreshAsync(window);
            await Shows(window, 10200.ToString("N0"), "+200");
            rwfc.GetRoomStatusAsync().Returns(Task.FromResult<RwfcRoomStatusResponse>(null!));

            // The player leaves the room: the profile is the source again.
            presence.SetRatings([]);
            await RefreshAsync(window);
            await Shows(window, 10050.ToString("N0"), "+50");
        }
        finally
        {
            window.Close();
        }
    }

    // The VR counts up over a moment, so the card is given a few seconds to arrive at the numbers.
    private static async Task Shows(OverlayWindow window, params string[] expected)
    {
        for (var attempt = 0; attempt < 120 && !expected.All(Texts(window).Contains); attempt++)
            await Dispatcher.UIThread.InvokeAsync(() => Task.Delay(25), DispatcherPriority.Background);
        foreach (var text in expected)
            Assert.Contains(text, Texts(window));
    }

    private static async Task SettleAsync(OverlayWindow window)
    {
        // The window loads the player once when it opens; wait for that to land.
        for (var attempt = 0; attempt < 40 && !Texts(window).Contains(10000.ToString("N0")); attempt++)
            await Dispatcher.UIThread.InvokeAsync(() => Task.Delay(25), DispatcherPriority.Background);
    }

    private static async Task RefreshAsync(OverlayWindow window)
    {
        var method = typeof(OverlayWindow).GetMethod("RefreshPlayerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(window, null)!;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static List<string> Texts(OverlayWindow window) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty).ToList();
}
