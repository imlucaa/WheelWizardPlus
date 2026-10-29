using WheelWizard.DiscordPresence;

namespace WheelWizard.Test.Features.DiscordPresence;

public class DiscordPresenceTests
{
    [Theory]
    [InlineData(30, "In a Grand Prix")]
    [InlineData(31, "In Time Trials")]
    [InlineData(85, "In a WiFi menu")]
    [InlineData(88, "Voting for a WiFi VS")]
    [InlineData(104, "Racing in a WiFi VS")]
    [InlineData(106, "Spectating a WiFi VS")]
    [InlineData(119, "Racing in a WiFi Friend Coin Runners")]
    public void Describe_UsesTheGamesOwnWording(int sectionId, string expected) =>
        Assert.Equal(expected, RetroRewindSections.Describe(sectionId));

    [Theory]
    [InlineData(72)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(500)]
    public void Describe_FallsBackToMenuForAnyOtherScreen(int sectionId) =>
        Assert.Equal("In a Menu", RetroRewindSections.Describe(sectionId));

    [Theory]
    [InlineData(84, false)]
    [InlineData(85, true)]
    [InlineData(119, true)]
    [InlineData(120, false)]
    public void IsOnline_CoversOnlyTheWifiScreens(int sectionId, bool expected) =>
        Assert.Equal(expected, RetroRewindSections.IsOnline(sectionId));

    [Theory]
    [InlineData(85, false)] // WiFi menu
    [InlineData(88, false)] // voting
    [InlineData(103, false)] // voting in a friend room
    [InlineData(104, true)] // racing
    [InlineData(111, true)] // spectating
    [InlineData(119, true)]
    [InlineData(120, false)]
    public void IsOnlineRace_ExcludesMenusAndVoting(int sectionId, bool expected) =>
        Assert.Equal(expected, RetroRewindSections.IsOnlineRace(sectionId));

    [Theory]
    [InlineData("123456789012345678", true)]
    [InlineData("123456789012345", true)]
    [InlineData("12345678901234", false)]
    [InlineData("123456789012345678901", false)]
    [InlineData("15612801638687047x9", false)]
    [InlineData("", false)]
    public void IsValidApplicationId_AcceptsOnlyDiscordSizedNumbers(string id, bool expected) =>
        Assert.Equal(expected, DiscordPresenceService.IsValidApplicationId(id));

    [Fact]
    public void MomentOf_ChangesWithTheRoomAndWithEachRace_SoTheStatusTimerStartsOverPerTrack()
    {
        Assert.Equal("", DiscordPresenceService.MomentOf(null, 3));
        Assert.Equal(DiscordPresenceService.MomentOf("room", 4), DiscordPresenceService.MomentOf("room", 4));
        Assert.NotEqual(DiscordPresenceService.MomentOf("room", 4), DiscordPresenceService.MomentOf("room", 5));
        Assert.NotEqual(DiscordPresenceService.MomentOf("room", 4), DiscordPresenceService.MomentOf("other", 4));
        Assert.Equal(DiscordPresenceService.MomentOf("room", 0), DiscordPresenceService.MomentOf("room", null));
    }

    [Fact]
    public void IsIdle_OnlyAfterTheSameThingHasBeenUpForTheIdleTime()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        long Started(TimeSpan ago) => now.Add(-ago).ToUnixTimeSeconds();

        Assert.False(DiscordPresenceService.IsIdle(0, now));
        Assert.False(DiscordPresenceService.IsIdle(Started(TimeSpan.FromMinutes(14)), now));
        Assert.True(DiscordPresenceService.IsIdle(Started(DiscordPresenceService.IdleAfter), now));
        Assert.True(DiscordPresenceService.IsIdle(Started(TimeSpan.FromMinutes(42)), now));
    }

    [Theory]
    [InlineData("GCN Dry Dry Desert / Wii U Dry Dry Desert", "Wii U Dry Dry Desert", "Wii U Dry Dry Desert")]
    [InlineData("GCN Dry Dry Desert / Wii U Dry Dry Desert", "gcn dry dry desert", "GCN Dry Dry Desert")]
    [InlineData("GCN Dry Dry Desert / Wii U Dry Dry Desert", " DS Desert Hills ", "DS Desert Hills")]
    [InlineData("GCN Dry Dry Desert / Wii U Dry Dry Desert", null, "GCN Dry Dry Desert / Wii U Dry Dry Desert")]
    [InlineData("GCN Dry Dry Desert / Wii U Dry Dry Desert", "  ", "GCN Dry Dry Desert / Wii U Dry Dry Desert")]
    [InlineData("Luigi Circuit", "Mario Circuit", "Luigi Circuit")]
    [InlineData("", "Mario Circuit", "")]
    public void ChooseTrack_ShowsTheOneTrackTheGameLoaded_NotBothNamesTheRoomLists(string roomTrack, string? gameTrack, string expected) =>
        Assert.Equal(expected, DiscordPresenceService.ChooseTrack(roomTrack, gameTrack));

    [Fact]
    public void RequiredName_IsRetroRewind() => Assert.Equal("Retro Rewind", DiscordPresenceService.RequiredName);

    [Theory]
    [InlineData("5073-9912-7254", "https://rwfc.net/player/5073-9912-7254")]
    [InlineData(" 0050-0070-1000 ", "https://rwfc.net/player/0050-0070-1000")]
    [InlineData("0000-0000-0000", null)]
    [InlineData("507399127254", null)]
    [InlineData("5073-9912-72x4", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void BuildPlayerUrl_OnlyAcceptsFriendCodes(string? friendCode, string? expected) =>
        Assert.Equal(expected, DiscordPresenceService.BuildPlayerUrl(friendCode));

    [Fact]
    public void Activity_WithoutLinks_HasNoButtons() => Assert.Empty(new DiscordActivity("a", "b", "key", "", 1).Buttons);

    [Fact]
    public void Activity_WithBothLinks_HasViewPlayerThenViewRoom()
    {
        var activity = new DiscordActivity(
            "a",
            "b",
            "key",
            "",
            1,
            0,
            0,
            "https://rwfc.net/player/5073-9912-7254",
            "https://rwfc.net/rooms"
        );

        Assert.Equal(
            [
                new DiscordButton("View Player", "https://rwfc.net/player/5073-9912-7254"),
                new DiscordButton("View Room", "https://rwfc.net/rooms"),
            ],
            activity.Buttons
        );
    }

    [Fact]
    public void Activity_WithOnlyAPlayerLink_HasJustViewPlayer()
    {
        var activity = new DiscordActivity("a", "b", "key", "", 1, 0, 0, "https://rwfc.net/player/5073-9912-7254");

        Assert.Equal("View Player", Assert.Single(activity.Buttons).Label);
    }
}
