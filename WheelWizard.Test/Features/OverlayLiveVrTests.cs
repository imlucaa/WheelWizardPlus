using WheelWizard.Models.RRInfo;
using WheelWizard.RrRooms;
using WheelWizard.StreamOverlay;

namespace WheelWizard.Test.Features;

public class OverlayLiveVrTests
{
    [Fact]
    public void CurrentVr_PrefersTheRoomsRating_AndFallsBackToTheProfile()
    {
        var presence = new RoomPresence();
        Assert.Equal(9000, OverlayLive.CurrentVr(presence, "1111-2222-3333", 9000));
        Assert.Equal(9000, OverlayLive.CurrentVr(null, "1111-2222-3333", 9000));

        presence.SetRatings(
            [
                new RrPlayer
                {
                    FriendCode = "1111-2222-3333",
                    Pid = "1",
                    Name = "A",
                    Vr = 9150,
                },
            ]
        );

        Assert.Equal(9150, OverlayLive.CurrentVr(presence, "1111-2222-3333", 9000));
        Assert.Equal(9000, OverlayLive.CurrentVr(presence, "4444-5555-6666", 9000));
    }
}
