using WheelWizard.AutoUpdating;
using WheelWizard.AutoUpdating.Platforms;
using WheelWizard.Branding;
using WheelWizard.GitHub;
using WheelWizard.GitHub.Domain;
using WheelWizard.Shared;
using WheelWizard.Shared.Downloads;
using WheelWizard.Shared.Platform;

namespace WheelWizard.Test.Features.AutoUpdating;

public class AutoUpdaterTests
{
    [Fact]
    public async Task FindsNewestCompatibleStableRelease_AndHonorsPostpone()
    {
        var fixture = new Fixture();
        fixture.Releases([Release("v4.0.0", prerelease: true), Release("v3.0.0", compatible: false), Release("v1.1.0"), Release("v2.0.0")]);

        await fixture.Service.CheckForUpdatesAsync();

        await fixture.Presentation.Received(1).ConfirmUpdateAsync("2.0.0", "1.0.0");
        await fixture
            .Platform.DidNotReceive()
            .ExecuteUpdateAsync(Arg.Any<string>(), Arg.Any<IProgress<DownloadProgress>>(), Arg.Any<CancellationToken>());
        await fixture
            .Presentation.DidNotReceive()
            .RunUpdateAsync(Arg.Any<Func<IProgress<DownloadProgress>, CancellationToken, Task<OperationResult>>>());
    }

    [Fact]
    public async Task ReleaseNotes_AreShownInThePrompt_WhenTheReleaseHasThem()
    {
        var fixture = new Fixture();
        fixture.Releases([Release("v2.0.0", body: "## New\n- **Mod presets**")]);

        await fixture.Service.CheckForUpdatesAsync();

        await fixture.Presentation.Received(1).ConfirmUpdateAsync("2.0.0", "1.0.0", "New\n• Mod presets");
        await fixture.Presentation.DidNotReceive().ConfirmUpdateAsync("2.0.0", "1.0.0");
    }

    [Fact]
    public async Task ReleaseWithoutNotes_UsesThePlainPrompt()
    {
        var fixture = new Fixture();
        fixture.Releases([Release("v2.0.0", body: "  ")]);

        await fixture.Service.CheckForUpdatesAsync();

        await fixture.Presentation.Received(1).ConfirmUpdateAsync("2.0.0", "1.0.0");
        await fixture.Presentation.DidNotReceive().ConfirmUpdateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CheckNow_WithANewerRelease_ReportsItWithoutOpeningAnyPrompt()
    {
        var fixture = new Fixture();
        fixture.Releases([Release("v1.0.0"), Release("v2.0.0")]);
        var raised = 0;
        fixture.Service.UpdateAvailable += (_, _) => raised++;

        var result = await fixture.Service.CheckNowAsync();

        Assert.Equal(new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, "2.0.0"), result);
        Assert.True(fixture.Service.IsUpdateAvailable);
        Assert.Equal(1, raised);
        Assert.Empty(fixture.Presentation.ReceivedCalls());
    }

    [Fact]
    public async Task CheckNow_WhenNothingIsNewer_SaysUpToDate()
    {
        var fixture = new Fixture();
        fixture.Releases([Release("v1.0.0"), Release("v0.9.0")]);

        var result = await fixture.Service.CheckNowAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.False(fixture.Service.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckNow_IgnoresPreReleasesAndReleasesWithoutAFileForThisPlatform()
    {
        var fixture = new Fixture();
        fixture.Releases([Release("v4.0.0", prerelease: true), Release("v3.0.0", compatible: false)]);

        var result = await fixture.Service.CheckNowAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task CheckNow_WhenGitHubCannotBeReached_ReportsTheErrorQuietly()
    {
        var fixture = new Fixture();
        fixture.GitHub.GetReleasesAsync().Returns(Fail("offline"));

        var result = await fixture.Service.CheckNowAsync();

        Assert.Equal(new UpdateCheckResult(UpdateCheckStatus.Failed, Message: "offline"), result);
        Assert.Empty(fixture.Presentation.ReceivedCalls());
    }

    [Fact]
    public async Task CheckNow_WithAnUnreadableVersionTag_SkipsThatRelease()
    {
        var fixture = new Fixture();
        fixture.Releases([Release("vnot-a-version")]);

        var result = await fixture.Service.CheckNowAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task AfterACheckNow_ShowingTheUpdateOpensTheNormalPrompt()
    {
        var fixture = new Fixture();
        fixture.Releases([Release("v2.0.0")]);
        await fixture.Service.CheckNowAsync();

        await fixture.Service.ShowAvailableUpdateAsync();

        await fixture.Presentation.Received(1).ConfirmUpdateAsync("2.0.0", "1.0.0");
    }

    [Fact]
    public async Task LastCheckedAt_IsSetByEveryKindOfCheck()
    {
        var fixture = new Fixture();
        fixture.Releases([Release("v1.0.0")]);
        Assert.Null(fixture.Service.LastCheckedAt);

        await fixture.Service.CheckNowAsync();
        var first = fixture.Service.LastCheckedAt;
        Assert.NotNull(first);

        await fixture.Service.CheckForUpdatesAsync();
        Assert.True(fixture.Service.LastCheckedAt >= first);
    }

    [Theory]
    [InlineData(null, "Not checked yet")]
    [InlineData(10, "Last checked just now")]
    [InlineData(5 * 60, "Last checked 5 min ago")]
    [InlineData(3 * 3600, "Last checked 3 h ago")]
    [InlineData(2 * 86400, "Last checked 2 d ago")]
    public void DescribeLastChecked_ReadsInPlainWords(int? secondsAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset? at = secondsAgo is { } seconds ? now.AddSeconds(-seconds) : null;

        Assert.Equal(expected, WheelWizard.Settings.Views.WhWzSettings.DescribeLastChecked(at, now));
    }

    [Fact]
    public async Task ConfirmedUpdate_ReportsPlatformFailure()
    {
        var fixture = new Fixture();
        fixture.Releases([Release("v2.0.0")]);
        fixture.Presentation.ConfirmUpdateAsync("2.0.0", "1.0.0").Returns(true);
        fixture
            .Platform.ExecuteUpdateAsync(Arg.Any<string>(), Arg.Any<IProgress<DownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Fail("disk full"));
        fixture
            .Presentation.RunUpdateAsync(Arg.Any<Func<IProgress<DownloadProgress>, CancellationToken, Task<OperationResult>>>())
            .Returns(call =>
                call.Arg<Func<IProgress<DownloadProgress>, CancellationToken, Task<OperationResult>>>()(
                    new Progress<DownloadProgress>(),
                    CancellationToken.None
                )
            );

        await fixture.Service.CheckForUpdatesAsync();

        await fixture
            .Platform.Received(1)
            .ExecuteUpdateAsync("https://example.test/v2.0.0.exe", Arg.Any<IProgress<DownloadProgress>>(), CancellationToken.None);
        await fixture.Presentation.Received(1).ShowUpdateFailureAsync("disk full");
    }

    [Fact]
    public async Task UnsupportedPlatform_ShowsNewestManualUpdateOnce()
    {
        var fixture = new Fixture();
        fixture.Platform.SupportsAutomaticUpdate.Returns(false);
        fixture.Releases([Release("v2.0.0", compatible: false), Release("v3.0.0", compatible: false)]);

        await fixture.Service.CheckForUpdatesAsync();
        await fixture.Service.CheckForUpdatesAsync();

        await fixture.Presentation.Received(1).ShowManualUpdateAsync("3.0.0", "1.0.0");
        await fixture.Presentation.DidNotReceive().ConfirmUpdateAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ReleaseLookupFailure_IsPresentedWithoutStartingAnUpdate()
    {
        var fixture = new Fixture();
        fixture.GitHub.GetReleasesAsync().Returns(Fail("offline"));

        await fixture.Service.CheckForUpdatesAsync();

        await fixture.Presentation.Received(1).ShowCheckFailureAsync("offline");
        Assert.Empty(fixture.Platform.ReceivedCalls());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PostponedUpdate_CanBeReopenedWithoutAnotherLookup(bool automatic)
    {
        var fixture = new Fixture();
        fixture.Platform.SupportsAutomaticUpdate.Returns(automatic);
        fixture.Releases([Release("v2.0.0")]);
        var notifications = 0;
        fixture.Service.UpdateAvailable += (_, _) => notifications++;

        Assert.False(fixture.Service.IsUpdateAvailable);
        await fixture.Service.ShowAvailableUpdateAsync();
        Assert.Empty(fixture.Presentation.ReceivedCalls());

        await fixture.Service.CheckForUpdatesAsync();
        Assert.True(fixture.Service.IsUpdateAvailable);
        Assert.Equal(1, notifications);
        await fixture.Service.ShowAvailableUpdateAsync();

        await fixture.GitHub.Received(1).GetReleasesAsync();
        if (automatic)
            await fixture.Presentation.Received(2).ConfirmUpdateAsync("2.0.0", "1.0.0");
        else
            await fixture.Presentation.Received(2).ShowManualUpdateAsync("2.0.0", "1.0.0");
    }

    [Fact]
    public async Task Linux_SelectsNewestReleasePastDelay()
    {
        var fixture = new Fixture();
        fixture.Platform.SupportsAutomaticUpdate.Returns(false);
        fixture.Environment.IsLinux.Returns(true);
        var recent = Release("v3.0.0");
        recent.PublishedAt = fixture.Now;
        var available = Release("v2.0.0");
        available.PublishedAt = fixture.Now.AddDays(-1);
        fixture.Releases([recent, Release("invalid"), available]);

        await fixture.Service.CheckForUpdatesAsync();

        Assert.True(fixture.Service.IsUpdateAvailable);
        await fixture.Presentation.Received(1).ShowManualUpdateAsync("2.0.0", "1.0.0");
        await fixture.Presentation.DidNotReceive().ConfirmUpdateAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PackageManagedLinuxReleaseWithoutPublicationDate_DoesNotSetBadgeOrShowPopup()
    {
        var fixture = new Fixture();
        fixture.Platform.SupportsAutomaticUpdate.Returns(false);
        fixture.Environment.IsLinux.Returns(true);
        fixture.Releases([Release("v2.0.0")]);
        var notifications = 0;
        fixture.Service.UpdateAvailable += (_, _) => notifications++;

        await fixture.Service.CheckForUpdatesAsync();
        await fixture.Service.ShowAvailableUpdateAsync();

        Assert.False(fixture.Service.IsUpdateAvailable);
        Assert.Equal(0, notifications);
        Assert.Empty(fixture.Presentation.ReceivedCalls());
    }

    [Theory]
    // Package-managed builds wait a day; the self-updating standalone builds don't wait at all.
    [InlineData(23.999, true, true)]
    [InlineData(24, true, true)]
    [InlineData(48, true, true)]
    [InlineData(-1, true, true)]
    [InlineData(23.999, false, false)]
    [InlineData(24, true, false)]
    [InlineData(48, true, false)]
    [InlineData(-1, false, false)]
    public async Task PackageManagedLinuxBuilds_Wait24HoursBeforeBadgeAndPopup(double hours, bool expected, bool automatic)
    {
        var fixture = new Fixture();
        fixture.Environment.IsLinux.Returns(true);
        fixture.Platform.SupportsAutomaticUpdate.Returns(automatic);
        var release = Release("v2.0.0");
        release.PublishedAt = fixture.Now.AddHours(-hours);
        fixture.Releases([release]);
        var notifications = 0;
        fixture.Service.UpdateAvailable += (_, _) => notifications++;

        await fixture.Service.CheckForUpdatesAsync();

        Assert.Equal(expected, fixture.Service.IsUpdateAvailable);
        Assert.Equal(expected ? 1 : 0, notifications);
        if (!expected)
        {
            await fixture.Service.ShowAvailableUpdateAsync();
            Assert.Empty(fixture.Presentation.ReceivedCalls());
        }
        else if (automatic)
            await fixture.Presentation.Received(1).ConfirmUpdateAsync("2.0.0", "1.0.0");
        else
            await fixture.Presentation.Received(1).ShowManualUpdateAsync("2.0.0", "1.0.0");
    }

    private static GithubRelease Release(string version, bool prerelease = false, bool compatible = true, string? body = null) =>
        new()
        {
            TagName = version,
            Body = body,
            Prerelease = prerelease,
            Assets = compatible ? [new() { Name = "update.exe", BrowserDownloadUrl = $"https://example.test/{version}.exe" }] : [],
        };

    private sealed class Fixture
    {
        public IUpdatePlatform Platform { get; } = Substitute.For<IUpdatePlatform>();
        public IGitHubSingletonService GitHub { get; } = Substitute.For<IGitHubSingletonService>();
        public IUpdatePresentation Presentation { get; } = Substitute.For<IUpdatePresentation>();
        public IRuntimeEnvironment Environment { get; } = Substitute.For<IRuntimeEnvironment>();
        public DateTimeOffset Now { get; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        public AutoUpdaterSingletonService Service { get; }

        public Fixture()
        {
            var branding = Substitute.For<IBrandingSingletonService>();
            branding.Branding.Returns(
                new Branding.Branding
                {
                    DisplayName = "WheelWizard",
                    Identifier = "WheelWizard",
                    Version = "1.0.0",
                    RepositoryUrl = new Uri("https://example.test"),
                    DiscordUrl = new Uri("https://example.test"),
                    SupportUrl = new Uri("https://example.test"),
                }
            );
            Platform.SupportsAutomaticUpdate.Returns(true);
            Platform
                .GetAssetForCurrentPlatform(Arg.Any<GithubRelease>())
                .Returns(call => call.Arg<GithubRelease>().Assets.FirstOrDefault());
            var time = Substitute.For<TimeProvider>();
            time.GetUtcNow().Returns(Now);
            Service = new AutoUpdaterSingletonService(Platform, branding, GitHub, Presentation, Environment, time);
        }

        public void Releases(List<GithubRelease> releases) => GitHub.GetReleasesAsync().Returns(Ok(releases));
    }
}
