using NSubstitute;
using Testably.Abstractions.Testing;
using WheelWizard.DiscImages;
using WheelWizard.DiscImages.Domain;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using WheelWizard.Shared.Platform;

namespace WheelWizard.Test.Features.DiscImages;

public class LineColorTests
{
    private const string WitPath = "/tools/wit";

    [Fact]
    public void Apply_SetsBothBytesOfAPalDisc_AndRead_FindsThemAgain()
    {
        var dol = Dol(0x614F, 0x61AF);

        var result = RegionLine.Apply(dol, "RMCP01", LineColor.Purple);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, dol[0x614F]);
        Assert.Equal(5, dol[0x61AF]);
        Assert.Equal(LineColor.Purple, RegionLine.Read(dol, "RMCP01"));
    }

    [Fact]
    public void Apply_ChangesNothing_WhenTheFileIsNotTheGamesOwnMainDol()
    {
        var dol = Dol(0x6113);
        dol[0x6112] = 0x7F; // The value is no longer the end of a "li" instruction.

        var result = RegionLine.Apply(dol, "RMCE01", LineColor.Green);

        Assert.True(result.IsFailure);
        Assert.Equal(2, dol[0x6113]);
        Assert.Null(RegionLine.Read(dol, "RMCE01"));
    }

    [Fact]
    public void Supports_OnlyTheFourMarioKartWiiRegions()
    {
        Assert.All(new[] { "RMCE01", "RMCP01", "RMCJ01", "RMCK01" }, id => Assert.True(RegionLine.Supports(id)));
        Assert.False(RegionLine.Supports("GALE01"));
        Assert.False(RegionLine.Supports("RMC"));
    }

    [Fact]
    public async Task Change_MakingACopy_LeavesTheOriginalAndNamesTheCopyByColour()
    {
        var test = new Setup();

        var result = await test.Service.ChangeAsync(test.Disc, LineColor.Blue, replaceOriginal: false, null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        var copy = test.Path("mkw (blue line).wbfs");
        Assert.Equal(copy, result.Value);
        Assert.Equal("new disc", test.Fs.File.ReadAllText(copy));
        Assert.Equal("original", test.Fs.File.ReadAllText(test.Disc));
        Assert.Equal(1, test.Wit.BuiltDol![0x614F]);
        Assert.Equal(1, test.Wit.BuiltDol![0x61AF]);
        test.AssertNothingLeftBehind(copy);
    }

    [Fact]
    public async Task Change_ReplacingTheOriginal_PutsTheNewDiscInItsPlace()
    {
        var test = new Setup();

        var result = await test.Service.ChangeAsync(test.Disc, LineColor.Red, replaceOriginal: true, null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(test.Disc, result.Value);
        Assert.Equal("new disc", test.Fs.File.ReadAllText(test.Disc));
        test.AssertNothingLeftBehind(test.Disc);
    }

    [Fact]
    public async Task Change_WhenTheDiscCannotBeBuilt_KeepsTheOriginalAndSaysWhy()
    {
        var test = new Setup { FailCopy = true };

        var result = await test.Service.ChangeAsync(test.Disc, LineColor.Red, replaceOriginal: true, null, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Contains("disk full", result.Error.Message);
        Assert.Equal("original", test.Fs.File.ReadAllText(test.Disc));
        test.AssertNothingLeftBehind(test.Disc);
    }

    [Fact]
    public async Task Change_RefusesADiscThatIsNotMarioKartWii_WithoutStartingTheTool()
    {
        var test = new Setup { GameId = "GALE01" };

        var result = await test.Service.ChangeAsync(test.Disc, LineColor.Red, replaceOriginal: false, null, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Empty(test.Wit.Calls);
    }

    [Fact]
    public async Task SetWit_RemembersATool_ThatAnswersAsWit_AndRefusesAnythingElse()
    {
        var test = new Setup();
        test.Wit.VersionOutput = "hello";
        Assert.True((await test.Service.SetWitAsync(WitPath)).IsFailure);
        test.Settings.DidNotReceive().Set(Arg.Any<Setting<string>>(), Arg.Any<string>(), Arg.Any<bool>());

        test.Wit.VersionOutput = "wit: Wiimms ISO Tool v3.05a";
        Assert.True((await test.Service.SetWitAsync(WitPath)).IsSuccess);
        test.Settings.Received(1).Set(test.WitSetting, WitPath, Arg.Any<bool>());
    }

    // main.dol is 0x7000 bytes of padding with a "li r0,2" ending on each of the value offsets.
    private static byte[] Dol(params int[] valueOffsets)
    {
        var dol = new byte[0x7000];
        foreach (var offset in valueOffsets)
        {
            dol[offset - 3] = 0x38;
            dol[offset] = 2;
        }

        return dol;
    }

    private sealed class FakeWit(MockFileSystem fs) : IWitRunner
    {
        public List<string> Calls { get; } = [];
        public bool FailCopy { get; init; }
        public byte[] Dol { get; init; } = [];
        public string VersionOutput { get; set; } = "wit: Wiimms ISO Tool v3.05a";
        public byte[]? BuiltDol { get; private set; }

        public Task<WitRunResult> RunAsync(string wit, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken token)
        {
            Calls.Add(string.Join(' ', arguments));
            switch (arguments[0])
            {
                case "extract":
                    var folder = fs.Path.Combine(workingDirectory!, arguments[2]);
                    fs.Directory.CreateDirectory(fs.Path.Combine(folder, "DATA", "sys"));
                    fs.File.WriteAllBytes(fs.Path.Combine(folder, "DATA", "sys", "main.dol"), Dol);
                    break;
                case "copy" when FailCopy:
                    return Task.FromResult(new WitRunResult(1, "banner\n!! wit: ERROR disk full"));
                case "copy":
                    BuiltDol = fs.File.ReadAllBytes(fs.Path.Combine(arguments[1], "DATA", "sys", "main.dol"));
                    fs.File.WriteAllText(fs.Path.Combine(workingDirectory!, arguments[2]), "new disc");
                    break;
                case "version":
                    return Task.FromResult(new WitRunResult(0, VersionOutput));
            }

            return Task.FromResult(new WitRunResult(0, string.Empty));
        }
    }

    private sealed class Setup
    {
        public Setup()
        {
            Fs = new MockFileSystem();
            Fs.WithDrive(drive => drive.SetTotalSize(500L * 1024 * 1024 * 1024));
            Fs.Initialize().WithFile(WitPath);
            Fs.Directory.CreateDirectory(Fs.Path.GetFullPath("roms"));
            Disc = Path("mkw.wbfs");
            Fs.File.WriteAllText(Disc, "original");
        }

        public MockFileSystem Fs { get; }
        public string Disc { get; }
        public string GameId { get; init; } = "RMCP01";
        public bool FailCopy { get; init; }
        public WhWzSetting<string> WitSetting { get; } = new("WitLocation", "");
        public ISettingsManager Settings { get; } = Substitute.For<ISettingsManager>();

        private FakeWit? _wit;
        public FakeWit Wit => _wit ??= new FakeWit(Fs) { FailCopy = FailCopy, Dol = LineColorTests.Dol(0x614F, 0x61AF) };

        public LineColorService Service
        {
            get
            {
                Settings.WIT_LOCATION.Returns(WitSetting);
                Settings.Get<string>(WitSetting).Returns(WitPath);
                var discs = Substitute.For<IDiscImageService>();
                discs.ReadInfo(Arg.Any<string>()).Returns(new DiscImageInfo(GameId, "MarioKartWii", 0, DiscImageFormat.Wbfs));
                var environment = Substitute.For<IRuntimeEnvironment>();
                return new LineColorService(Fs, discs, Wit, Settings, environment);
            }
        }

        public string Path(string name) => Fs.Path.Combine(Fs.Path.GetFullPath("roms"), name);

        /// <summary>The disc folder holds the discs and nothing else, and the temporary folder is gone.</summary>
        public void AssertNothingLeftBehind(string expectedDisc)
        {
            var files = Fs.Directory.GetFiles(Fs.Path.GetFullPath("roms"));
            Assert.All(files, file => Assert.DoesNotContain(".wheelwizard-line-", file));
            Assert.All(files, file => Assert.False(file.EndsWith(".old")));
            Assert.Contains(expectedDisc, files);
            Assert.Empty(Fs.Directory.GetDirectories(Fs.Path.GetTempPath(), "wheelwizard-line-*"));
        }
    }
}
