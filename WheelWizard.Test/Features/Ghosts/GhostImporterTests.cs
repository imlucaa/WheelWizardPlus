using Testably.Abstractions.Testing;
using WheelWizard.CustomDistributions;
using WheelWizard.Ghosts;

namespace WheelWizard.Test.Features.Ghosts;

public class GhostImporterTests
{
    private const string RetroList =
        "\\c{yellow}SNES \\c{off}Mario Circuit 1 = 44E5205F\n\\c{green}N64 \\c{off}Luigi Raceway = D233950F\n\n";
    private const string CustomList = "Phendrana Frostbite = 57F96209\nHoneymoon Lune = 0BB85D95\nPoké Floats = 605EFF4B\n";

    [Fact]
    public void Parse_NumbersTracksFromZero_AndRemovesTheMenuColourCodes()
    {
        var tracks = GhostTrackTable.Parse(GhostKind.Retro, RetroList);

        Assert.Equal(2, tracks.Count);
        Assert.Equal(new GhostTrack(GhostKind.Retro, 0, "SNES Mario Circuit 1", "44E5205F"), tracks[0]);
        Assert.Equal(new GhostTrack(GhostKind.Retro, 1, "N64 Luigi Raceway", "D233950F"), tracks[1]);
        Assert.Equal("ExpertsRT", tracks[0].FolderName);
        Assert.Equal("0_150.rkg", tracks[0].FileName(150));
    }

    [Fact]
    public void Parse_SkipsLinesThatAreNotATrack()
    {
        var tracks = GhostTrackTable.Parse(GhostKind.Custom, "not a track\nBotania = 15154efb\n");

        var track = Assert.Single(tracks);
        Assert.Equal(new GhostTrack(GhostKind.Custom, 0, "Botania", "15154EFB"), track);
        Assert.Equal("ExpertsCT", track.FolderName);
    }

    [Fact]
    public void GhostFile_ReadsTheFinishTimeFromTheHeader()
    {
        // 1:34.086 packed as 7 bits of minutes, 7 of seconds and 10 of milliseconds, taken from a real ghost.
        var data = Ghost(0x02, 0x88, 0x56);

        Assert.True(GhostFile.TryRead(data, out var time));
        Assert.Equal(new TimeSpan(0, 0, 1, 34, 86), time);
        Assert.Equal("1:34.086", GhostFile.Format(time));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("wrong-magic")]
    public void GhostFile_RejectsAnythingThatIsNotAGhost(string kind)
    {
        var data = kind == "short" ? new byte[10] : Ghost(0, 0, 0, magic: "NOPE");

        Assert.False(GhostFile.TryRead(data, out _));
    }

    [Fact]
    public void GhostFile_RejectsAnImpossibleTime()
    {
        // 61 seconds does not exist.
        var data = Ghost(0x00, 0xF4, 0x00);

        Assert.False(GhostFile.TryRead(data, out _));
    }

    [Fact]
    public void Import_ReplacesTheTracksGhost_KeepsTheOldOne_AndTouchesNothingElse()
    {
        var fixture = new Fixture();
        var track = fixture.Importer.LoadTracks(GhostKind.Custom)[1];
        fixture.Write("ExpertsCT/1_150.rkg", Ghost(0x02, 0x88, 0x56));
        fixture.Write("ExpertsCT/0_150.rkg", Ghost(0x00, 0x78, 0x01));
        var source = fixture.Source("mine.rkg", Ghost(0x02, 0x00, 0x7B)); // 1:00.123

        var result = fixture.Importer.Import(source, track, 150);

        Assert.True(result.Success);
        Assert.Equal(new TimeSpan(0, 0, 1, 34, 86), result.Replaced);
        Assert.Equal(new TimeSpan(0, 0, 1, 0, 123), result.Imported);
        Assert.Equal(new TimeSpan(0, 0, 1, 0, 123), fixture.Importer.CurrentTime(track, 150));
        Assert.Equal(new TimeSpan(0, 0, 0, 30, 1), fixture.Importer.CurrentTime(fixture.Importer.LoadTracks(GhostKind.Custom)[0], 150));
        var kept = Assert.Single(fixture.Files.Directory.GetFiles(fixture.Path("Replaced/ExpertsCT")));
        Assert.StartsWith("1_150-", fixture.Files.Path.GetFileName(kept));
        Assert.Empty(fixture.Files.Directory.GetFiles(fixture.Path("ExpertsCT"), "*.importing"));
    }

    [Fact]
    public void Import_IntoAnEmptySlot_JustAddsTheGhost()
    {
        var fixture = new Fixture();
        var track = fixture.Importer.LoadTracks(GhostKind.Custom)[2];
        var source = fixture.Source("new.rkg", Ghost(0x02, 0x00, 0x7B));

        var result = fixture.Importer.Import(source, track, 150);

        Assert.True(result.Success);
        Assert.Null(result.Replaced);
        Assert.True(fixture.Files.File.Exists(fixture.Importer.TargetPath(track, 150)));
        Assert.False(fixture.Files.Directory.Exists(fixture.Path("Replaced")));
    }

    [Fact]
    public void ImportData_FromADownload_ReplacesTheGhost_AsAnExpertGhost_AndKeepsTheOldOne()
    {
        var fixture = new Fixture();
        var track = fixture.Importer.LoadTracks(GhostKind.Custom)[0];
        fixture.Write("ExpertsCT/0_200.rkg", Ghost(0x00, 0x78, 0x01));

        var result = fixture.Importer.ImportData(Ghost(0x02, 0x00, 0x7B), track, 200);

        Assert.True(result.Success);
        var installed = fixture.Files.File.ReadAllBytes(fixture.Importer.TargetPath(track, 200));
        Assert.Equal(GhostFile.ExpertGhostType, ((installed[0x0C] & 1) << 6) | (installed[0x0D] >> 2));
        Assert.Equal(new TimeSpan(0, 0, 1, 0, 123), fixture.Importer.CurrentTime(track, 200));
        Assert.Single(fixture.Files.Directory.GetFiles(fixture.Path("Replaced/ExpertsCT")));
    }

    [Fact]
    public void ImportData_OfSomethingThatIsNotAGhost_ChangesNothing()
    {
        var fixture = new Fixture();
        var track = fixture.Importer.LoadTracks(GhostKind.Custom)[0];
        var original = Ghost(0x00, 0x78, 0x01);
        fixture.Write("ExpertsCT/0_150.rkg", original);

        var result = fixture.Importer.ImportData("<html>error</html>"u8.ToArray(), track, 150);

        Assert.False(result.Success);
        Assert.Equal(original, fixture.Files.File.ReadAllBytes(fixture.Importer.TargetPath(track, 150)));
    }

    [Fact]
    public void Import_OfAFileThatIsNotAGhost_ChangesNothing()
    {
        var fixture = new Fixture();
        var track = fixture.Importer.LoadTracks(GhostKind.Custom)[0];
        var original = Ghost(0x00, 0x78, 0x01);
        fixture.Write("ExpertsCT/0_150.rkg", original);
        var source = fixture.Source("notes.rkg", "this is text"u8.ToArray());

        var result = fixture.Importer.Import(source, track, 150);

        Assert.False(result.Success);
        Assert.Equal(original, fixture.Files.File.ReadAllBytes(fixture.Importer.TargetPath(track, 150)));
    }

    [Fact]
    public void Import_WithoutRetroRewindInstalled_SaysSo()
    {
        var fixture = new Fixture(installed: false);

        var result = fixture.Importer.Import("whatever.rkg", new GhostTrack(GhostKind.Custom, 0, "x", "00000000"), 150);

        Assert.False(fixture.Importer.IsAvailable);
        Assert.False(result.Success);
    }

    [Fact]
    public void Import_KeepsOnlyTheLastFewReplacedGhostsPerSlot()
    {
        var fixture = new Fixture();
        var track = fixture.Importer.LoadTracks(GhostKind.Custom)[0];
        fixture.Write("ExpertsCT/0_150.rkg", Ghost(0x00, 0x78, 0x01));
        fixture.Files.Directory.CreateDirectory(fixture.Path("Replaced/ExpertsCT"));
        for (var i = 0; i < 8; i++)
        {
            fixture.Files.File.WriteAllBytes(fixture.Path($"Replaced/ExpertsCT/0_150-2020010{i + 1}-000000.rkg"), Ghost(0x00, 0x78, 0x01));
        }
        var source = fixture.Source("mine.rkg", Ghost(0x02, 0x00, 0x7B));

        fixture.Importer.Import(source, track, 150);

        Assert.Equal(5, fixture.Files.Directory.GetFiles(fixture.Path("Replaced/ExpertsCT")).Length);
    }

    [Fact]
    public void RestorePrevious_PutsTheReplacedGhostBack_AndKeepsTheOneItReplaces()
    {
        var fixture = new Fixture();
        var track = fixture.Importer.LoadTracks(GhostKind.Custom)[1];
        fixture.Write("ExpertsCT/1_150.rkg", Ghost(0x02, 0x88, 0x56)); // 1:34.086
        fixture.Importer.Import(fixture.Source("mine.rkg", Ghost(0x02, 0x00, 0x7B)), track, 150); // 1:00.123

        Assert.Equal(new TimeSpan(0, 0, 1, 34, 86), fixture.Importer.Previous(track, 150)?.Time);
        Assert.Null(fixture.Importer.Previous(track, 200));

        var result = fixture.Importer.RestorePrevious(track, 150);

        Assert.True(result.Success);
        Assert.Contains("1:34.086", result.Message);
        Assert.Equal(new TimeSpan(0, 0, 1, 34, 86), fixture.Importer.CurrentTime(track, 150));
        Assert.Equal(new TimeSpan(0, 0, 1, 0, 123), fixture.Importer.Previous(track, 150)?.Time);
    }

    [Fact]
    public void RestorePrevious_WithNothingReplacedBefore_SaysSoAndChangesNothing()
    {
        var fixture = new Fixture();
        var track = fixture.Importer.LoadTracks(GhostKind.Custom)[0];
        fixture.Write("ExpertsCT/0_150.rkg", Ghost(0x00, 0x78, 0x01));

        var result = fixture.Importer.RestorePrevious(track, 150);

        Assert.False(result.Success);
        Assert.Equal(new TimeSpan(0, 0, 0, 30, 1), fixture.Importer.CurrentTime(track, 150));
    }

    [Fact]
    public void Export_SavesACopyOfTheTracksGhost_ThatCanBeImportedAgain()
    {
        var fixture = new Fixture();
        var track = fixture.Importer.LoadTracks(GhostKind.Custom)[1];
        var original = Ghost(0x02, 0x88, 0x56, course: 5);
        fixture.Write("ExpertsCT/1_150.rkg", original);
        var destination = fixture.Files.Path.GetFullPath("/out/ghost.rkg");
        fixture.Files.Directory.CreateDirectory(fixture.Files.Path.GetDirectoryName(destination)!);

        var result = fixture.Importer.Export(track, 150, destination);

        Assert.True(result.Success);
        Assert.Equal(new TimeSpan(0, 0, 1, 34, 86), result.Imported);
        Assert.Equal(original, fixture.Files.File.ReadAllBytes(destination));
        Assert.Equal(original, fixture.Files.File.ReadAllBytes(fixture.Importer.TargetPath(track, 150)));
    }

    [Fact]
    public void Export_OfATrackWithoutAGhost_SaysSoAndWritesNothing()
    {
        var fixture = new Fixture();
        var track = fixture.Importer.LoadTracks(GhostKind.Custom)[2];
        var destination = fixture.Files.Path.GetFullPath("/out/none.rkg");
        fixture.Files.Directory.CreateDirectory(fixture.Files.Path.GetDirectoryName(destination)!);

        var result = fixture.Importer.Export(track, 150, destination);

        Assert.False(result.Success);
        Assert.False(fixture.Files.File.Exists(destination));
    }

    [Fact]
    public void ExportFileName_IsJustTheFinishTime()
    {
        var fixture = new Fixture();
        var track = new GhostTrack(GhostKind.Retro, 5, "Great ? Block: Ruins", "00000000");
        fixture.Write("ExpertsRT/5_150.rkg", Ghost(0x02, 0x88, 0x56));
        fixture.Write("ExpertsRT/5_200.rkg", Ghost(0x00, 0x78, 0x01));

        Assert.Equal("1m34s086.rkg", fixture.Importer.ExportFileName(track, 150));
        Assert.Equal("0m30s001.rkg", fixture.Importer.ExportFileName(track, 200));
        Assert.Equal("9_150.rkg", fixture.Importer.ExportFileName(track with { Index = 9 }, 150));
    }

    [Fact]
    public void Speeds_OffersOnlyTheSpeedsThatHaveAGhost_AndDefaultsToFiveHundredFifty()
    {
        var fixture = new Fixture();
        var track = fixture.Importer.LoadTracks(GhostKind.Custom)[0];
        Assert.Equal([150], fixture.Importer.Speeds(track));

        fixture.Write("ExpertsCT/0_150.rkg", Ghost(0x00, 0x78, 0x01));
        fixture.Write("ExpertsCT/0_200.rkg", Ghost(0x00, 0x78, 0x01));

        Assert.Equal([150, 200], fixture.Importer.Speeds(track));
    }

    [Theory]
    [InlineData("57F96209.rkg", "Phendrana Frostbite")]
    [InlineData("my-ghost_0bb85d95_final.rkg", "Honeymoon Lune")]
    [InlineData("Poké Floats.rkg", "Poké Floats")]
    [InlineData("poke floats.rkg", null)] // the accent matters: a close guess could replace the wrong ghost
    [InlineData("random.rkg", null)]
    public void GuessTrack_UsesTheCodeOrTheExactName(string fileName, string? expected)
    {
        var tracks = GhostTrackTable.Parse(GhostKind.Custom, CustomList);

        Assert.Equal(expected, GhostImporter.GuessTrack(fileName, tracks)?.Name);
    }

    [Fact]
    public void ToExpertGhost_SetsTheExpertType_FixesTheChecksum_AndKeepsTheRest()
    {
        // Byte 0x0C/0x0D of a ghost the player recorded: compressed, type 1 ("player best time").
        var recorded = Ghost(0x02, 0x88, 0x56, course: 4);
        recorded[0x0C] = 0x0A;
        recorded[0x0D] = 0x06;
        recorded[0x50] = 0x77;

        var expert = GhostFile.ToExpertGhost(recorded);

        Assert.Equal(recorded.Length, expert.Length);
        Assert.Equal(38, ((expert[0x0C] & 1) << 6) | (expert[0x0D] >> 2));
        Assert.Equal(0x0A, expert[0x0C]); // the other bits of the byte are the recording's own
        Assert.Equal(0x02, expert[0x0D] & 0x03);
        Assert.Equal(0x77, expert[0x50]);
        Assert.Equal(0x0A, recorded[0x0C]); // the original array is not touched
        Assert.Equal(0x06, recorded[0x0D]);
        // The last four bytes are the CRC-32 (big endian) of everything before them.
        Assert.Equal(
            Crc32Of(expert.AsSpan(0, expert.Length - 4)),
            (uint)((expert[^4] << 24) | (expert[^3] << 16) | (expert[^2] << 8) | expert[^1])
        );
    }

    [Fact]
    public void Crc32_MatchesTheStandardCheckValue()
    {
        // "123456789" is the standard test input for CRC-32.
        Assert.Equal(0xCBF43926u, Crc32Of("123456789"u8));
    }

    [Fact]
    public void Import_WritesTheGhostAsAnExpertGhost()
    {
        var fixture = new Fixture();
        var track = fixture.Importer.LoadTracks(GhostKind.Custom)[0];
        var recorded = Ghost(0x02, 0x00, 0x7B);
        recorded[0x0D] = 0x04;
        var source = fixture.Source("mine.rkg", recorded);

        fixture.Importer.Import(source, track, 150);

        var written = fixture.Files.File.ReadAllBytes(fixture.Importer.TargetPath(track, 150));
        Assert.Equal(38, ((written[0x0C] & 1) << 6) | (written[0x0D] >> 2));
        Assert.Equal(new TimeSpan(0, 0, 1, 0, 123), fixture.Importer.CurrentTime(track, 150));
    }

    [Fact]
    public void GhostFile_ReadsTheTrackSlotAndLapCount()
    {
        Assert.True(GhostFile.TryReadHeader(Ghost(0x02, 0x88, 0x56, course: 17, laps: 3), out var header));

        Assert.Equal(17, header!.CourseId);
        Assert.Equal(3, header.Laps);
    }

    [Fact]
    public void FindMatches_ListsTracksWithTheSameSlotAndLaps_ClosestTimeFirst()
    {
        var fixture = new Fixture();
        // Custom tracks 0 and 1 share slot 5; track 2 is in another slot. Retro track 0 shares the slot too.
        fixture.Write("ExpertsCT/0_150.rkg", Ghost(0x00, 0x78, 0x01, course: 5, laps: 3)); // 0:30.001
        fixture.Write("ExpertsCT/1_150.rkg", Ghost(0x02, 0x88, 0x56, course: 5, laps: 3)); // 1:34.086
        fixture.Write("ExpertsCT/2_150.rkg", Ghost(0x02, 0x88, 0x56, course: 9, laps: 3));
        fixture.Write("ExpertsRT/0_150.rkg", Ghost(0x02, 0x00, 0x7B, course: 5, laps: 3)); // 1:00.123
        fixture.Write("ExpertsRT/0_200.rkg", Ghost(0x00, 0xEC, 0x01, course: 5, laps: 3)); // 0:59.001
        var mine = Ghost(0x02, 0x40, 0x00, course: 5, laps: 3); // 1:16.000

        var matches = fixture.Importer.FindMatches(mine);

        Assert.Equal(4, matches.Count);
        Assert.DoesNotContain(matches, match => match.Track.Kind == GhostKind.Custom && match.Track.Index == 2);
        // 1:16.000 against each current time: 1:34.086 is closest in pace, then 1:00.123, 0:59.001 and last 0:30.001.
        Assert.Equal(
            [(GhostKind.Custom, 1, 150), (GhostKind.Retro, 0, 150), (GhostKind.Retro, 0, 200), (GhostKind.Custom, 0, 150)],
            matches.Select(match => (match.Track.Kind, match.Track.Index, match.Speed)).ToArray()
        );
    }

    [Fact]
    public void FindMatches_IgnoresTracksWithADifferentLapCount_AndFilesThatAreNotGhosts()
    {
        var fixture = new Fixture();
        fixture.Write("ExpertsCT/0_150.rkg", Ghost(0x02, 0x88, 0x56, course: 5, laps: 2));
        fixture.Write("ExpertsCT/1_150.rkg", "not a ghost"u8.ToArray());

        Assert.Empty(fixture.Importer.FindMatches(Ghost(0x02, 0x40, 0x00, course: 5, laps: 3)));
        Assert.Empty(fixture.Importer.FindMatches("nope"u8.ToArray()));
    }

    // The same CRC-32 the game checks, written out separately so the test does not lean on the code it checks.
    private static uint Crc32Of(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }

    private static byte[] Ghost(byte b4, byte b5, byte b6, string magic = "RKGD", int course = 0, int laps = 3)
    {
        var data = new byte[0x100];
        System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(data, 0);
        data[4] = b4;
        data[5] = b5;
        data[6] = b6;
        data[7] = (byte)(course << 2);
        data[0x10] = (byte)laps;
        return data;
    }

    private sealed class Fixture
    {
        public MockFileSystem Files { get; } = new();
        public GhostImporter Importer { get; }
        private readonly string _root;

        public Fixture(bool installed = true)
        {
            _root = Files.Path.GetFullPath("/rr6");
            var paths = Substitute.For<ICustomDistributionPaths>();
            paths.RetroRewindFolderPath.Returns(_root);
            Importer = new GhostImporter(paths, Files);
            if (!installed)
                return;

            Files.Directory.CreateDirectory(Path("ExpertsCT"));
            Files.Directory.CreateDirectory(Path("ExpertsRT"));
            Files.File.WriteAllText(Path(GhostTrackTable.CustomFile), CustomList);
            Files.File.WriteAllText(Path(GhostTrackTable.RetroFile), RetroList);
        }

        public string Path(string relative) =>
            Files.Path.Combine(_root, "Ghosts", relative.Replace('/', Files.Path.DirectorySeparatorChar));

        public void Write(string relative, byte[] data) => Files.File.WriteAllBytes(Path(relative), data);

        public string Source(string name, byte[] data)
        {
            var path = Files.Path.GetFullPath("/downloads/" + name);
            Files.Directory.CreateDirectory(Files.Path.GetDirectoryName(path)!);
            Files.File.WriteAllBytes(path, data);
            return path;
        }
    }
}
