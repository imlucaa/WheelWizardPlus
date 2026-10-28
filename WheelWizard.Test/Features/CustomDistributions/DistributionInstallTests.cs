using System.IO.Compression;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging.Abstractions;
using Testably.Abstractions.Testing;
using WheelWizard.ApplicationData;
using WheelWizard.CustomDistributions;
using WheelWizard.CustomDistributions.Domain;
using WheelWizard.Dolphin.Paths;
using WheelWizard.Settings;
using WheelWizard.Shared;
using WheelWizard.Shared.Downloads;
using WheelWizard.Shared.Services;

namespace WheelWizard.Test.Features.CustomDistributions;

public class DistributionInstallTests
{
    [Fact]
    public async Task StableInstall_ExtractsThroughInjectedFilesystem_AndReportsProgressWithoutUi()
    {
        var fixture = new Fixture();
        fixture.Archive = Zip(
            ("RetroRewind6/version.txt", "6.0.0"),
            ("RetroRewind6/data.bin", "payload"),
            ("riivolution/RetroRewind6.xml", "xml")
        );
        var progress = new Collector();

        var result = await fixture.Stable.InstallAsync(new(progress));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : "");
        Assert.Equal("payload", fixture.Fs.File.ReadAllText(fixture.Fs.Path.Combine(fixture.Paths.RetroRewindFolderPath, "data.bin")));
        Assert.Equal("6.0.0", fixture.Stable.GetCurrentVersion()!.ToString());
        Assert.False(fixture.Fs.Directory.Exists(fixture.Paths.DownloadFolderPath));
        Assert.Contains(progress.Values, value => value.Percent == 100);
    }

    [Fact]
    public async Task BetaInstall_PreservesStableXml_AndWritesItsOwnManifest()
    {
        var fixture = new Fixture();
        fixture.Fs.Directory.CreateDirectory(fixture.Fs.Path.GetDirectoryName(fixture.Paths.XmlFilePath)!);
        fixture.Fs.File.WriteAllText(fixture.Paths.XmlFilePath, "stable xml");
        fixture.Archive = Zip(
            ("RRBeta/data.bin", "beta"),
            ("riivolution/RRBeta.xml", "beta xml"),
            ("riivolution/RetroRewind6.xml", "unwanted")
        );

        var result = await fixture.Beta.InstallAsync(new());

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : "");
        Assert.Equal("stable xml", fixture.Fs.File.ReadAllText(fixture.Paths.XmlFilePath));
        Assert.Equal("beta xml", fixture.Fs.File.ReadAllText(fixture.Paths.BetaXmlFilePath));
        Assert.Contains("data.bin", fixture.Fs.File.ReadAllText(fixture.Paths.BetaManifestFilePath));
        Assert.DoesNotContain("RetroRewind6.xml", fixture.Fs.File.ReadAllText(fixture.Paths.BetaManifestFilePath));
        Assert.False(fixture.Fs.Directory.Exists(fixture.Paths.BetaDownloadFolderPath));
    }

    [Fact]
    public async Task StableArchiveTraversal_IsRejectedBeforePublishingInstallation()
    {
        var fixture = new Fixture();
        fixture.Archive = Zip(("../escaped.txt", "outside"));

        Assert.True((await fixture.Stable.InstallAsync(new())).IsFailure);

        Assert.False(
            fixture.Fs.File.Exists(
                fixture.Fs.Path.Combine(fixture.Fs.Path.GetDirectoryName(fixture.Paths.DownloadFolderPath)!, "escaped.txt")
            )
        );
        Assert.False(fixture.Fs.Directory.Exists(fixture.Paths.RetroRewindFolderPath));
    }

    [Fact]
    public async Task BetaPasswordDismissal_ReturnsFailureAndCleansTemporaryFiles()
    {
        var fixture = new Fixture();
        fixture.Archive = Zip(("RRBeta/data.bin", "beta"));
        fixture.Prompts.RequestBetaPasswordAsync().Returns((string?)null);

        var result = await fixture.Beta.InstallAsync(new());

        Assert.True(result.IsFailure);
        Assert.Contains("Password", result.Error.Message);
        Assert.False(fixture.Fs.Directory.Exists(fixture.Paths.BetaDownloadFolderPath));
        Assert.False(fixture.Fs.File.Exists(fixture.Paths.BetaManifestFilePath));
    }

    [Fact]
    public async Task DownloadCancellation_PropagatesTokenAndEndsCancellablePhase()
    {
        var downloads = Substitute.For<IDownloadService>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        downloads
            .DownloadAsync(
                "https://example.com/archive.zip",
                "archive.zip",
                false,
                Arg.Any<IProgress<DownloadProgress>>(),
                cancellation.Token
            )
            .Returns(Task.FromCanceled<OperationResult<string>>(cancellation.Token));
        var progress = new Collector();

        var result = await downloads.DownloadDistributionAsync(
            "https://example.com/archive.zip",
            "archive.zip",
            new(progress, cancellation.Token)
        );

        Assert.True(result.IsFailure);
        Assert.Equal(new bool?[] { true, false }, progress.Values.Select(value => value.CanCancel));
    }

    private static byte[] Zip(params (string Name, string Contents)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry.Name).Open());
                writer.Write(entry.Contents);
            }
        return stream.ToArray();
    }

    private sealed class Collector : IProgress<DistributionProgress>
    {
        public List<DistributionProgress> Values { get; } = [];

        public void Report(DistributionProgress value)
        {
            lock (Values)
                Values.Add(value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledInstallation_IsNotReportedAsCompleted(bool beta)
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture
            .Downloads.DownloadAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<IProgress<DownloadProgress>>(),
                cancellation.Token
            )
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<OperationResult<string>>(cancellation.Token);
            });
        IDistribution distribution = beta ? fixture.Beta : fixture.Stable;

        var result = await distribution.InstallAsync(new(CancellationToken: cancellation.Token));

        Assert.True(result.IsFailure);
        Assert.False(fixture.Fs.Directory.Exists(beta ? fixture.Paths.BetaFolderPath : fixture.Paths.RetroRewindFolderPath));
    }

    // An existing stable install with a mod patch inside it, as a user would have.
    private static void ExistingInstall(Fixture fixture)
    {
        fixture.Fs.Directory.CreateDirectory(fixture.Fs.Path.Combine(fixture.Paths.RetroRewindFolderPath, "Patches"));
        fixture.Fs.File.WriteAllText(fixture.Fs.Path.Combine(fixture.Paths.RetroRewindFolderPath, "version.txt"), "5.0.0");
        fixture.Fs.File.WriteAllText(fixture.Fs.Path.Combine(fixture.Paths.RetroRewindFolderPath, "Patches", "MyPatch.szs"), "patch");
        fixture.Fs.Directory.CreateDirectory(fixture.Fs.Path.GetDirectoryName(fixture.Paths.XmlFilePath)!);
        fixture.Fs.File.WriteAllText(fixture.Paths.XmlFilePath, "old xml");
    }

    private static void AssertExistingInstallIsIntact(Fixture fixture)
    {
        Assert.Equal("5.0.0", fixture.Fs.File.ReadAllText(fixture.Fs.Path.Combine(fixture.Paths.RetroRewindFolderPath, "version.txt")));
        Assert.Equal(
            "patch",
            fixture.Fs.File.ReadAllText(fixture.Fs.Path.Combine(fixture.Paths.RetroRewindFolderPath, "Patches", "MyPatch.szs"))
        );
        Assert.Equal("old xml", fixture.Fs.File.ReadAllText(fixture.Paths.XmlFilePath));
        Assert.False(fixture.Fs.Directory.Exists(fixture.Paths.RetroRewindFolderPath + ".old"));
        Assert.False(fixture.Fs.File.Exists(fixture.Paths.XmlFilePath + ".old"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reinstall_KeepsTheInstallAndItsPatches_WhenTheServerIsUnreachable(bool useReinstall)
    {
        var fixture = new Fixture();
        ExistingInstall(fixture);
        fixture.ServerDown = true;

        var result = useReinstall ? await fixture.Stable.ReinstallAsync(new()) : await fixture.Stable.InstallAsync(new());

        Assert.True(result.IsFailure);
        AssertExistingInstallIsIntact(fixture);
    }

    [Fact]
    public async Task Reinstall_KeepsTheInstall_WhenTheDownloadFails()
    {
        var fixture = new Fixture();
        ExistingInstall(fixture);
        fixture
            .Downloads.DownloadAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<IProgress<DownloadProgress>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Fail("Connection lost"));

        Assert.True((await fixture.Stable.ReinstallAsync(new())).IsFailure);

        AssertExistingInstallIsIntact(fixture);
    }

    [Fact]
    public async Task Reinstall_KeepsTheInstall_WhenTheDownloadIsNotARetroRewindArchive()
    {
        var fixture = new Fixture();
        ExistingInstall(fixture);
        fixture.Archive = Zip(("something-else/readme.txt", "no"));

        Assert.True((await fixture.Stable.ReinstallAsync(new())).IsFailure);

        AssertExistingInstallIsIntact(fixture);
    }

    [Fact]
    public async Task Reinstall_ReplacesTheInstall_AndLeavesNoBackup_WhenItWorks()
    {
        var fixture = new Fixture();
        ExistingInstall(fixture);
        fixture.Archive = Zip(
            ("RetroRewind6/version.txt", "6.0.0"),
            ("RetroRewind6/data.bin", "new"),
            ("riivolution/RetroRewind6.xml", "new xml")
        );

        var result = await fixture.Stable.ReinstallAsync(new());

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : "");
        Assert.Equal("6.0.0", fixture.Stable.GetCurrentVersion()!.ToString());
        Assert.Equal("new xml", fixture.Fs.File.ReadAllText(fixture.Paths.XmlFilePath));
        Assert.False(fixture.Fs.Directory.Exists(fixture.Paths.RetroRewindFolderPath + ".old"));
        Assert.False(fixture.Fs.File.Exists(fixture.Paths.XmlFilePath + ".old"));
    }

    [Fact]
    public async Task Install_RecoversAnInstallLeftInTheBackup_ByAnInterruptedRun()
    {
        var fixture = new Fixture();
        ExistingInstall(fixture);
        // An earlier run was closed right after it moved the install aside.
        fixture.Fs.Directory.Move(fixture.Paths.RetroRewindFolderPath, fixture.Paths.RetroRewindFolderPath + ".old");
        fixture.Fs.File.Move(fixture.Paths.XmlFilePath, fixture.Paths.XmlFilePath + ".old");
        fixture.ServerDown = true;

        Assert.True((await fixture.Stable.ReinstallAsync(new())).IsFailure);

        AssertExistingInstallIsIntact(fixture);
    }

    [Fact]
    public async Task Remove_ReportsFailure_InsteadOfClaimingSuccess_WhenTheInstallCannotBeDeleted()
    {
        var fixture = new Fixture();
        ExistingInstall(fixture);
        fixture.Fs.Intercept.Event(_ => throw new IOException("The install is in use."));

        var result = await fixture.Stable.RemoveAsync(new());

        Assert.True(result.IsFailure);
        Assert.Contains("Failed to remove", result.Error.Message);
    }

    [Fact]
    public async Task BetaReinstall_KeepsTheOldBuild_WhenTheDownloadOrPasswordFails()
    {
        var fixture = new Fixture();
        fixture.Archive = Zip(("RRBeta/data.bin", "old beta"), ("riivolution/RRBeta.xml", "old beta xml"));
        Assert.True((await fixture.Beta.InstallAsync(new())).IsSuccess);

        // A wrong password and nobody retrying: the new build never unpacks.
        fixture.Prompts.RequestBetaPasswordAsync().Returns((string?)null);
        Assert.True((await fixture.Beta.ReinstallAsync(new())).IsFailure);

        Assert.Equal("old beta", fixture.Fs.File.ReadAllText(fixture.Fs.Path.Combine(fixture.Paths.BetaFolderPath, "data.bin")));
        Assert.Equal("old beta xml", fixture.Fs.File.ReadAllText(fixture.Paths.BetaXmlFilePath));
        Assert.True(fixture.Fs.File.Exists(fixture.Paths.BetaManifestFilePath));
    }

    private sealed class Fixture
    {
        public MockFileSystem Fs { get; } = new();
        public CustomDistributionPaths Paths { get; }
        public IDistributionPrompts Prompts { get; } = Substitute.For<IDistributionPrompts>();
        public IDownloadService Downloads { get; } = Substitute.For<IDownloadService>();
        public byte[] Archive { get; set; } = [];
        public bool ServerDown { get; set; }
        public RetroRewind Stable { get; }
        public RetroRewindBeta Beta { get; }

        public Fixture()
        {
            var root = Path.Combine(Path.GetTempPath(), "distribution-fixture");
            Fs.Directory.CreateDirectory(root);
            var location = Substitute.For<IApplicationDataLocation>();
            location.DirectoryPath.Returns(Fs.Path.Combine(root, "app"));
            var dolphin = Substitute.For<IDolphinPaths>();
            dolphin.UserFolderPath.Returns(Fs.Path.Combine(root, "dolphin"));
            dolphin.WiiFolderPath.Returns(Fs.Path.Combine(root, "nand"));
            dolphin.LoadFolderPath.Returns(Fs.Path.Combine(root, "load"));
            Paths = new(location, dolphin, Fs);
            var downloads = Downloads;
            downloads
                .DownloadAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<bool>(),
                    Arg.Any<IProgress<DownloadProgress>>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(call =>
                {
                    var path = call.ArgAt<string>(1);
                    Fs.Directory.CreateDirectory(Fs.Path.GetDirectoryName(path)!);
                    Fs.File.WriteAllBytes(path, Archive);
                    return Ok(path);
                });
            var api = Substitute.For<IRetroRewindApi>();
            api.Ping().Returns("online");
            api.GetInstallUrl().Returns("https://example.com/archive.zip");
            api.GetVersionFile().Returns("6.0.0");
            var caller = Substitute.For<IApiCaller<IRetroRewindApi>>();
            caller
                .CallApiAsync(Arg.Any<Expression<Func<IRetroRewindApi, Task<string>>>>())
                .Returns(async call =>
                    ServerDown
                        ? Fail("Server offline")
                        : Ok(await call.Arg<Expression<Func<IRetroRewindApi, Task<string>>>>().Compile()(api))
                );
            Prompts.RequestBetaPasswordAsync().Returns("password");
            var settings = Substitute.For<ISettingsManager>();
            Stable = new(Fs, caller, NullLogger<IDistribution>.Instance, settings, downloads, Paths, dolphin, Prompts);
            Beta = new(Fs, NullLogger<IDistribution>.Instance, settings, downloads, Paths, Prompts);
        }
    }
}
