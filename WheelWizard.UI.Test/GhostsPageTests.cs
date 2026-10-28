using System.Linq.Expressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using Testably.Abstractions.Testing;
using WheelWizard.CustomDistributions;
using WheelWizard.Ghosts;
using WheelWizard.Ghosts.Views;
using WheelWizard.RrRooms;
using WheelWizard.Shared;
using WheelWizard.Shared.Desktop.Storage;
using WheelWizard.Shared.Services;
using WheelWizard.Views.Components;

namespace WheelWizard.UI.Test;

public class GhostsPageTests
{
    private sealed class DirectCaller(IRwfcApi api) : IApiCaller<IRwfcApi>
    {
        public async Task<OperationResult<TResult>> CallApiAsync<TResult>(Expression<Func<IRwfcApi, Task<TResult>>> apiCall) =>
            await OperationResult.TryCatch(async () => await apiCall.Compile()(api));
    }

    private static byte[] Ghost(int minutes, int seconds, int milliseconds)
    {
        var data = new byte[0x100];
        "RKGD"u8.CopyTo(data);
        var packed = (minutes << 17) | (seconds << 10) | milliseconds;
        data[4] = (byte)(packed >> 16);
        data[5] = (byte)(packed >> 8);
        data[6] = (byte)packed;
        data[0x10] = 3;
        return data;
    }

    private static void Settle()
    {
        for (var i = 0; i < 10; i++)
            Dispatcher.UIThread.RunJobs();
    }

    private static List<string> Texts(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text ?? "").ToList();

    private static void Click(Window window, string text)
    {
        var target = window.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == text && block.IsEffectivelyVisible);
        var point = target.TranslatePoint(new Point(4, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle();
    }

    private static void Press(Window window, string text) =>
        window
            .GetVisualDescendants()
            .OfType<ActionButton>()
            .First(button => button.Text == text && button.IsEffectivelyVisible)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

    private static SegmentedControl Filter(Window window, string firstOption) =>
        window
            .GetVisualDescendants()
            .OfType<SegmentedControl>()
            .First(control => control.Items.OfType<SegmentOption>().FirstOrDefault()?.Text == firstOption);

    private static RwfcTrackRecord Record(string track, int ms, string display) =>
        new()
        {
            TrackId = 1,
            TrackName = track,
            ActiveWorldRecord = new()
            {
                PlayerName = "Ann",
                FinishTimeMs = ms,
                FinishTimeDisplay = display,
                HasGhostFile = true,
            },
        };

    [Theory]
    [InlineData(83860, 83860, "WR")]
    [InlineData(83000, 83860, "WR")]
    [InlineData(86201, 83860, "+2.341")]
    [InlineData(150000, 83860, "+1:06.140")]
    public void Gap_IsHowFarBehindTheRecord(int yoursMs, int recordMs, string gap) =>
        Assert.Equal(gap, GhostsPage.Gap(TimeSpan.FromMilliseconds(yoursMs), recordMs));

    [AvaloniaFact]
    public void Export_AsksWhichGhost_OrSavesTheOpenTracks()
    {
        var files = new MockFileSystem();
        var root = files.Path.GetFullPath("/rr6");
        files.Directory.CreateDirectory(files.Path.Combine(root, "Ghosts", "ExpertsRT"));
        files.File.WriteAllText(
            files.Path.Combine(root, "Ghosts", GhostTrackTable.RetroFile),
            "SNES Mario Circuit 1 = 12345678\nGCN Peach Beach = 23456789\n"
        );
        files.File.WriteAllText(files.Path.Combine(root, "Ghosts", GhostTrackTable.CustomFile), "");
        var paths = Substitute.For<ICustomDistributionPaths>();
        paths.RetroRewindFolderPath.Returns(root);
        var importer = new GhostImporter(paths, files);
        var mario = importer.LoadTracks(GhostKind.Retro)[0];
        Assert.True(importer.ImportData(Ghost(1, 25, 0), mario, 150).Success);
        Assert.True(importer.ImportData(Ghost(1, 10, 0), mario, 200).Success);

        var picker = Substitute.For<IFilePickerService>();
        var page = new GhostsPage(importer, picker, new DirectCaller(Substitute.For<IRwfcApi>()));
        var window = new Window
        {
            Content = page,
            Width = 440,
            Height = 860,
        };
        window.Show();
        Settle();

        // Nothing open: a pop-up lists every ghost, both speeds.
        Press(window, "Export");
        Settle();
        var popup = Assert.Single(window.OwnedWindows);
        var texts = Texts(popup);
        Assert.Contains("Export a ghost", texts);
        Assert.Contains("150cc", texts);
        Assert.Contains("200cc", texts);
        Assert.Contains("1:10.000", texts);
        Assert.DoesNotContain("GCN Peach Beach", texts);
        popup.Close();
        Settle();

        // A track open: its ghost is saved straight away.
        Click(window, "SNES Mario Circuit 1");
        Press(window, "Export");
        Settle();
        Assert.Empty(window.OwnedWindows);
        picker
            .Received(1)
            .SaveFileAsync(Arg.Any<string>(), Arg.Any<IEnumerable<FilePickerFileType>>(), importer.ExportFileName(mario, 150));
        window.Close();
    }

    [AvaloniaFact]
    public void ClickingGap_ListsTheFurthestTracksFirst_AndUndoPutsBackTheReplacedGhost()
    {
        var files = new MockFileSystem();
        var root = files.Path.GetFullPath("/rr6");
        files.Directory.CreateDirectory(files.Path.Combine(root, "Ghosts", "ExpertsRT"));
        files.File.WriteAllText(
            files.Path.Combine(root, "Ghosts", GhostTrackTable.RetroFile),
            "SNES Mario Circuit 1 = 12345678\nGCN Peach Beach = 23456789\n"
        );
        files.File.WriteAllText(files.Path.Combine(root, "Ghosts", GhostTrackTable.CustomFile), "");
        var paths = Substitute.For<ICustomDistributionPaths>();
        paths.RetroRewindFolderPath.Returns(root);
        var importer = new GhostImporter(paths, files);
        var mario = importer.LoadTracks(GhostKind.Retro)[0];
        var peach = importer.LoadTracks(GhostKind.Retro)[1];
        Assert.True(importer.ImportData(Ghost(1, 25, 0), mario, 150).Success); // +1.140 behind
        Assert.True(importer.ImportData(Ghost(2, 0, 0), peach, 150).Success); // +20.000 behind

        var marioRecord = Record("SNES Mario Circuit 1", 83860, "1:23.860");
        var peachRecord = Record("GCN Peach Beach", 100000, "1:40.000");
        peachRecord.TrackId = 2;
        var api = Substitute.For<IRwfcApi>();
        api.GetTimeTrialTracksAsync()
            .Returns(
                [
                    new RwfcTimeTrialTrack
                    {
                        Id = 1,
                        Name = "SNES Mario Circuit 1",
                        Category = "retro",
                        Laps = 3,
                    },
                    new RwfcTimeTrialTrack
                    {
                        Id = 2,
                        Name = "GCN Peach Beach",
                        Category = "retro",
                        Laps = 3,
                    },
                ]
            );
        api.GetTimeTrialRecordsAsync(150, null, null, null).Returns([marioRecord, peachRecord]);

        var page = new GhostsPage(importer, Substitute.For<IFilePickerService>(), new DirectCaller(api));
        var window = new Window
        {
            Content = page,
            Width = 440,
            Height = 860,
        };
        window.Show();
        Settle();

        double Top(string track) =>
            window
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .First(block => block.Text == track && block.IsEffectivelyVisible)
                .TranslatePoint(default, window)!
                .Value.Y;

        Assert.True(Top("SNES Mario Circuit 1") < Top("GCN Peach Beach"));
        Assert.Contains("+20.000", Texts(window));

        Click(window, "Gap");
        Assert.Contains("Gap ▼", Texts(window));
        Assert.True(Top("GCN Peach Beach") < Top("SNES Mario Circuit 1"));

        // Replace Peach Beach's ghost; its row now offers Undo for the old one.
        Assert.True(importer.ImportData(Ghost(1, 50, 0), peach, 150).Success);
        Click(window, "GCN Peach Beach");
        Press(window, "Undo 150cc");
        Settle();
        Assert.Equal(new TimeSpan(0, 0, 2, 0, 0), importer.CurrentTime(peach, 150));
        Assert.Contains("2:00.000", Texts(window));

        // Gap again goes back to the game's order.
        Click(window, "Gap ▼");
        Assert.True(Top("SNES Mario Circuit 1") < Top("GCN Peach Beach"));
        window.Close();
    }

    [AvaloniaFact]
    public void ADownloadedGhost_ShowsItsTimeOnThePage_StraightAway()
    {
        var files = new MockFileSystem();
        var root = files.Path.GetFullPath("/rr6");
        files.Directory.CreateDirectory(files.Path.Combine(root, "Ghosts", "ExpertsRT"));
        files.File.WriteAllText(files.Path.Combine(root, "Ghosts", GhostTrackTable.RetroFile), "SNES Mario Circuit 1 = 12345678\n");
        files.File.WriteAllText(files.Path.Combine(root, "Ghosts", GhostTrackTable.CustomFile), "");
        var paths = Substitute.For<ICustomDistributionPaths>();
        paths.RetroRewindFolderPath.Returns(root);
        var importer = new GhostImporter(paths, files);
        var mario = importer.LoadTracks(GhostKind.Retro)[0];
        Assert.True(importer.ImportData(Ghost(1, 25, 0), mario, 150).Success);

        var record = Record("SNES Mario Circuit 1", 83860, "1:23.860");
        record.ActiveWorldRecord!.Id = 5117;
        record.ActiveWorldRecord.Rank = 1;
        record.ActiveWorldRecord.Cc = 150;
        var api = Substitute.For<IRwfcApi>();
        api.GetTimeTrialTracksAsync()
            .Returns(
                [
                    new RwfcTimeTrialTrack
                    {
                        Id = 1,
                        Name = "SNES Mario Circuit 1",
                        Category = "retro",
                        Laps = 3,
                    },
                ]
            );
        api.GetTimeTrialRecordsAsync(150, null, null, null).Returns([record]);
        api.GetTimeTrialBoardAsync(1, 150, null, null, null, 1, TimeTrials.PageSize)
            .Returns(new RwfcTimeTrialBoard { Submissions = [record.ActiveWorldRecord], TotalPages = 1 });
        api.DownloadTimeTrialGhostAsync(5117).Returns(_ => new ByteArrayContent(Ghost(1, 23, 860)));

        var page = new GhostsPage(importer, Substitute.For<IFilePickerService>(), new DirectCaller(api));
        var window = new Window
        {
            Content = page,
            Width = 440,
            Height = 860,
        };
        window.Show();
        Settle();
        Assert.Contains("1:25.000", Texts(window));
        Assert.Contains("+1.140", Texts(window));

        // Get top ghost opens the track's board; taking the record puts it on the track, and the page shows it at once.
        Click(window, "SNES Mario Circuit 1");
        Press(window, "Get top ghost");
        Settle();
        var board = Assert.Single(window.OwnedWindows);
        var first = board.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "#1");
        var point = first.TranslatePoint(new Point(4, first.Bounds.Height / 2), board)!.Value;
        board.MouseDown(point, MouseButton.Left);
        board.MouseUp(point, MouseButton.Left);
        Settle();

        Assert.Equal(new TimeSpan(0, 0, 1, 23, 860), importer.CurrentTime(mario, 150));
        var texts = Texts(window);
        Assert.DoesNotContain("1:25.000", texts);
        Assert.Contains("WR", texts);
        board.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void ShowsYourGhostsNextToTheWorldRecords_AndAFileGoesOnTheTrackItIsNamedAfter()
    {
        // Retro Rewind with two retro tracks and one custom track; the first retro track has a 150cc ghost.
        var files = new MockFileSystem();
        var root = files.Path.GetFullPath("/rr6");
        files.Directory.CreateDirectory(files.Path.Combine(root, "Ghosts", "ExpertsRT"));
        files.Directory.CreateDirectory(files.Path.Combine(root, "Ghosts", "ExpertsCT"));
        files.File.WriteAllText(
            files.Path.Combine(root, "Ghosts", GhostTrackTable.RetroFile),
            "SNES Mario Circuit 1 = 12345678\nGCN Peach Beach = 23456789\n"
        );
        files.File.WriteAllText(files.Path.Combine(root, "Ghosts", GhostTrackTable.CustomFile), "Dark Matter Fortress = 34567890\n");
        var paths = Substitute.For<ICustomDistributionPaths>();
        paths.RetroRewindFolderPath.Returns(root);
        var importer = new GhostImporter(paths, files);
        var mario = importer.LoadTracks(GhostKind.Retro)[0];
        Assert.True(importer.ImportData(Ghost(1, 25, 0), mario, 150).Success);

        // The world records: 150cc in any category, and shroomless bikes.
        var api = Substitute.For<IRwfcApi>();
        api.GetTimeTrialRecordsAsync(150, null, null, null).Returns([Record("SNES Mario Circuit 1", 83860, "1:23.860")]);
        api.GetTimeTrialRecordsAsync(150, null, "only", "bikes").Returns([Record("SNES Mario Circuit 1", 84500, "1:24.500")]);

        var picker = Substitute.For<IFilePickerService>();
        var page = new GhostsPage(importer, picker, new DirectCaller(api));
        var window = new Window
        {
            Content = page,
            Width = 440,
            Height = 860,
        };
        window.Show();
        Settle();

        var texts = Texts(window);
        Assert.Contains("Get top ghosts", texts);
        Assert.Contains("2 tracks · 1 with a 150cc ghost", texts);
        Assert.Contains("1:25.000", texts);
        Assert.Contains("1:23.860", texts);
        Assert.Contains("+1.140", texts);
        Assert.DoesNotContain("Dark Matter Fortress", texts);

        // Other filters compare with another record.
        Filter(window, "All").SelectedIndex = 2;
        Filter(window, "Any").SelectedIndex = 1;
        Settle();
        texts = Texts(window);
        Assert.Contains("1:24.500", texts);
        Assert.Contains("+0.500", texts);
        Filter(window, "All").SelectedIndex = 0;
        Filter(window, "Any").SelectedIndex = 0;
        Settle();

        // Clicking a track shows what can be done with it.
        Click(window, "SNES Mario Circuit 1");
        texts = Texts(window);
        Assert.Contains("Save 150cc", texts);
        Assert.DoesNotContain("Save 200cc", texts);
        Assert.Contains("Get top ghost", texts);

        // Search, and the custom tracks.
        window.GetVisualDescendants().OfType<InputField>().Single().Text = "peach";
        Settle();
        texts = Texts(window);
        Assert.Contains("GCN Peach Beach", texts);
        Assert.DoesNotContain("SNES Mario Circuit 1", texts);
        window.GetVisualDescendants().OfType<InputField>().Single().Text = "";
        Filter(window, "Retro tracks").SelectedIndex = 1;
        Settle();
        Assert.Contains("Dark Matter Fortress", Texts(window));
        Filter(window, "Retro tracks").SelectedIndex = 0;
        Settle();

        // A ghost file named after a track opens that track, ready to replace its ghost.
        var folder = Path.Combine(Path.GetTempPath(), "ghost-test-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "SNES Mario Circuit 1.rkg");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(file, Ghost(1, 23, 500));
        files.Directory.CreateDirectory(folder);
        files.File.WriteAllBytes(file, Ghost(1, 23, 500));
        picker.OpenSingleFileAsync(Arg.Any<string>(), Arg.Any<IEnumerable<FilePickerFileType>>()).Returns(file);
        try
        {
            Press(window, "Import");
            Settle();
            texts = Texts(window);
            Assert.Contains("SNES Mario Circuit 1.rkg", texts);
            Assert.Contains("1:25.000 → 1:23.500", texts);

            Press(window, "Use this ghost");
            Settle();
            Assert.Equal(new TimeSpan(0, 0, 1, 23, 500), importer.CurrentTime(mario, 150));
            texts = Texts(window);
            Assert.Contains("1:23.500", texts);
            // Faster than the record.
            Assert.Contains("WR", texts);
            Assert.DoesNotContain("SNES Mario Circuit 1.rkg", texts);
        }
        finally
        {
            Directory.Delete(folder, true);
        }

        window.Close();
    }
}
