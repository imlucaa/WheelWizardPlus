using Testably.Abstractions.Testing;
using WheelWizard.ApplicationData;
using WheelWizard.Shared.Downloads;
using WheelWizard.WiiManagement.FriendCodes;

namespace WheelWizard.Test.Features.WiiManagement;

public class PlayerDatabaseTests
{
    private const string Json = """
        {
          "last_refresh": 1792890240292,
          "2625-9515-5302": { "fc": "2625-9515-5302", "name": "It It It", "pid": "602150246", "banned": false, "mii": [ { "data": "data:image/webp;base64,AAAA", "name": "It" } ] },
          "1111-2222-3333": { "fc": "1111-2222-3333", "name": "Cheater", "pid": "12345", "banned": true }
        }
        """;

    private sealed class Fixture
    {
        public MockFileSystem Fs { get; } = new();
        public IDownloadService Downloads { get; } = Substitute.For<IDownloadService>();
        public string File => Fs.Path.Combine("/data", "player-database", "rr-players.json");
        public PlayerDatabase Database { get; }

        public Fixture(string? download = Json)
        {
            var location = Substitute.For<IApplicationDataLocation>();
            location.DirectoryPath.Returns("/data");
            Downloads
                .DownloadAsync(
                    PlayerDatabase.Url,
                    Arg.Any<string>(),
                    true,
                    Arg.Any<IProgress<DownloadProgress>>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(call =>
                {
                    if (download is null)
                        return Fail("Offline");
                    var path = call.ArgAt<string>(1);
                    Fs.Directory.CreateDirectory(Fs.Path.GetDirectoryName(path)!);
                    Fs.File.WriteAllText(path, download);
                    return Ok(path);
                });
            Database = new(Downloads, location, Fs, System.TimeProvider.System);
        }

        public void SaveCopy(string json, TimeSpan age)
        {
            Fs.Directory.CreateDirectory(Fs.Path.GetDirectoryName(File)!);
            Fs.File.WriteAllText(File, json);
            Fs.File.SetLastWriteTimeUtc(File, DateTime.UtcNow - age);
        }

        public int Downloaded => Downloads.ReceivedCalls().Count();
    }

    [Fact]
    public async Task AFriendCodeInTheDatabase_IsFound_WithItsNameProfileIdAndBanFlag()
    {
        var fixture = new Fixture();

        var found = await fixture.Database.FindAsync("1111-2222-3333");
        var other = await fixture.Database.FindAsync("2625-9515-5302");

        Assert.Equal(new PlayerDatabaseEntry("1111-2222-3333", "Cheater", "12345", true), found.Value);
        Assert.Equal(new PlayerDatabaseEntry("2625-9515-5302", "It It It", "602150246", false), other.Value);
    }

    [Fact]
    public async Task AFriendCodeThatIsNotThere_IsNotAnError_JustNothingFound()
    {
        var result = await new Fixture().Database.FindAsync("9999-9999-9999");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task ARecentCopy_IsUsedWithoutDownloadingAgain_AndAnOldOneIsRenewed()
    {
        var fresh = new Fixture();
        fresh.SaveCopy(Json, TimeSpan.FromDays(1));
        Assert.True(fresh.Database.IsReady);
        Assert.NotNull((await fresh.Database.FindAsync("1111-2222-3333")).Value);
        Assert.Equal(0, fresh.Downloaded);

        var old = new Fixture();
        old.SaveCopy("""{ "1111-2222-3333": { "name": "Old copy" } }""", PlayerDatabase.RefreshAfter + TimeSpan.FromDays(1));
        Assert.False(old.Database.IsReady);
        Assert.Equal("Cheater", (await old.Database.FindAsync("1111-2222-3333")).Value!.Name);
        Assert.Equal(1, old.Downloaded);
    }

    [Fact]
    public async Task WhenItCannotBeRenewed_AnOldCopyIsStillUsed_AndWithoutOneItIsAFailure()
    {
        var withCopy = new Fixture(download: null);
        withCopy.SaveCopy(Json, TimeSpan.FromDays(30));
        Assert.Equal("Cheater", (await withCopy.Database.FindAsync("1111-2222-3333")).Value!.Name);

        var without = await new Fixture(download: null).Database.FindAsync("1111-2222-3333");
        Assert.True(without.IsFailure);
    }

    [Fact]
    public async Task ADamagedCopy_IsReportedAndRemoved_SoTheNextSearchDownloadsItAgain()
    {
        var fixture = new Fixture();
        fixture.SaveCopy("{ this is not json", TimeSpan.FromDays(1));

        var result = await fixture.Database.FindAsync("1111-2222-3333");

        Assert.True(result.IsFailure);
        Assert.False(fixture.Fs.File.Exists(fixture.File));
        Assert.Equal("Cheater", (await fixture.Database.FindAsync("1111-2222-3333")).Value!.Name);
    }
}
