using System.Linq.Expressions;
using NSubstitute;
using WheelWizard.Models;
using WheelWizard.RrRooms;
using WheelWizard.Shared;
using WheelWizard.Shared.Services;

namespace WheelWizard.Test.Features.RrRooms;

public class FriendVrRefresherTests
{
    private sealed class FakeApi(IRwfcApi api) : IApiCaller<IRwfcApi>
    {
        public async Task<OperationResult<TResult>> CallApiAsync<TResult>(Expression<Func<IRwfcApi, Task<TResult>>> apiCall) =>
            await apiCall.Compile()(api);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task OfflineFriends_GetTheirProfileVr_OnlineAndUnknownOnesAreSkipped_AndNobodyIsAskedTwiceSoSoon()
    {
        var rwfc = Substitute.For<IRwfcApi>();
        rwfc.GetPlayerProfileAsync("1111-1111-1111").Returns(Task.FromResult(new PlayerProfileResponse { Vr = 12345 }));
        rwfc.GetPlayerProfileAsync("2222-2222-2222").Returns<Task<PlayerProfileResponse>>(_ => throw new HttpRequestException("404"));
        var presence = new RoomPresence();
        presence.Replace(["3333-3333-3333"]);
        var clock = new Clock();
        var refresher = new FriendVrRefresher(new FakeApi(rwfc), presence, clock);
        string[] friends = ["1111-1111-1111", "2222-2222-2222", "3333-3333-3333", "0000-0000-0000"];

        await refresher.RefreshAsync(friends, CancellationToken.None);

        Assert.True(presence.TryGetProfileVr("1111-1111-1111", out var vr));
        Assert.Equal(12345, vr);
        Assert.False(presence.TryGetProfileVr("2222-2222-2222", out _));
        Assert.False(presence.TryGetProfileVr("3333-3333-3333", out _));
        await rwfc.DidNotReceive().GetPlayerProfileAsync("3333-3333-3333");
        await rwfc.DidNotReceive().GetPlayerProfileAsync("0000-0000-0000");

        await refresher.RefreshAsync(friends, CancellationToken.None);
        await rwfc.Received(1).GetPlayerProfileAsync("1111-1111-1111");

        clock.Now += TimeSpan.FromMinutes(11);
        await refresher.RefreshAsync(friends, CancellationToken.None);
        await rwfc.Received(2).GetPlayerProfileAsync("1111-1111-1111");
    }

    [Fact]
    public async Task OnlyAFewFriendsAreAskedPerPass_TheRestOnTheNext()
    {
        var rwfc = Substitute.For<IRwfcApi>();
        rwfc.GetPlayerProfileAsync(Arg.Any<string>()).Returns(Task.FromResult(new PlayerProfileResponse { Vr = 100 }));
        var presence = new RoomPresence();
        var refresher = new FriendVrRefresher(new FakeApi(rwfc), presence, new Clock());
        var friends = Enumerable.Range(1, 10).Select(i => $"{i:0000}-0000-0000").ToList();

        await refresher.RefreshAsync(friends, CancellationToken.None);
        Assert.Equal(6, friends.Count(code => presence.TryGetProfileVr(code, out _)));

        await refresher.RefreshAsync(friends, CancellationToken.None);
        Assert.Equal(10, friends.Count(code => presence.TryGetProfileVr(code, out _)));
    }
}
