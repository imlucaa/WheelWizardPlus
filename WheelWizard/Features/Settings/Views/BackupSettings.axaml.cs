using System.IO.Abstractions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WheelWizard.CustomDistributions;
using WheelWizard.Dolphin.Paths;
using WheelWizard.Features.Backups;
using WheelWizard.Models.Enums;
using WheelWizard.Shared.Desktop.Storage;
using WheelWizard.Views.Dialogs;
using WheelWizard.Views.Shell;
using WheelWizard.WiiManagement;
using WheelWizard.WiiManagement.GameLicense;

namespace WheelWizard.Settings.Views;

public partial class BackupSettings : UserControl
{
    private int _rksysCount;
    private bool _ratingExists;
    private readonly GameDataBackupService _backups;
    private readonly ICustomDistributionPaths _distributionPaths;
    private readonly IDolphinPaths _dolphinPaths;
    private readonly IFilePickerService _filePicker;
    private readonly IFileSystem _fileSystem;
    private readonly IPopupFactory _popups;
    private readonly IGameLicenseSingletonService _licenses;

    public BackupSettings(
        GameDataBackupService backups,
        ICustomDistributionPaths distributionPaths,
        IDolphinPaths dolphinPaths,
        IFilePickerService filePicker,
        IFileSystem fileSystem,
        IPopupFactory popups,
        IGameLicenseSingletonService licenses
    )
    {
        _backups = backups;
        _distributionPaths = distributionPaths;
        _dolphinPaths = dolphinPaths;
        _filePicker = filePicker;
        _fileSystem = fileSystem;
        _popups = popups;
        _licenses = licenses;
        InitializeComponent();
        RefreshFileStatus();
    }

    private void RefreshFileStatus()
    {
        _rksysCount = _backups.FindRksysFiles(_distributionPaths.SaveFolderPath).Count;
        _ratingExists = _backups.RatingFileExists(RatingFilePath);
        RksysStatusText.Text =
            _rksysCount == 0 ? "Not found"
            : _rksysCount == 1 ? "1 save found"
            : $"{_rksysCount} saves found";
        RatingStatusText.Text = _ratingExists ? "Found" : "Not found";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        BackupRksysButton.IsEnabled = _rksysCount > 0;
        BackupRatingButton.IsEnabled = _ratingExists;
        ImportRksysButton.IsEnabled = true;
        ImportRatingButton.IsEnabled = true;
    }

    private async void BackupRksys_OnClick(object? sender, RoutedEventArgs e)
    {
        var saves = _backups.FindRksysSaves(_distributionPaths.SaveFolderPath);
        IReadOnlyCollection<MarioKartWiiEnums.Regions>? regions = null;
        if (saves.Count > 1)
        {
            regions = await ChooseRegionsAsync(saves);
            if (regions == null)
                return;
        }

        var destination = await ChooseDestinationAsync("Choose where to back up rksys.dat");
        if (destination == null)
            return;
        RunBackup(() => _backups.BackupRksysFiles(destination, _distributionPaths.SaveFolderPath, regions), "rksys.dat");
    }

    /// <summary>Asks which region to back up; the most recently played save is offered first as the current one.</summary>
    private async Task<IReadOnlyCollection<MarioKartWiiEnums.Regions>?> ChooseRegionsAsync(IReadOnlyList<RksysSave> saves)
    {
        var current = saves.OrderByDescending(save => save.LastWriteUtc).First();
        var options = new List<ChoiceOption> { new($"Current region — {RegionName(current.Region)}", "Detected", true) };
        var results = new List<IReadOnlyCollection<MarioKartWiiEnums.Regions>> { new[] { current.Region } };

        options.Add(new("All regions", $"{saves.Count} saves"));
        results.Add(saves.Select(save => save.Region).ToArray());

        foreach (var save in saves)
        {
            options.Add(new(RegionName(save.Region), GameRegion.GetGameId(save.Region)));
            results.Add(new[] { save.Region });
        }

        var index = await _popups
            .CreateChoiceList(
                "Back up rksys.dat",
                "Which region?",
                "Pick the save to back up, or let it use the region you played last.",
                options
            )
            .AwaitAnswer();
        return index is >= 0 && index < results.Count ? results[index.Value] : null;
    }

    private static string RegionName(MarioKartWiiEnums.Regions region) =>
        region switch
        {
            MarioKartWiiEnums.Regions.America => "Americas",
            MarioKartWiiEnums.Regions.Europe => "Europe",
            MarioKartWiiEnums.Regions.Japan => "Japan",
            MarioKartWiiEnums.Regions.Korea => "Korea",
            _ => region.ToString(),
        };

    private async void BackupRating_OnClick(object? sender, RoutedEventArgs e)
    {
        var destination = await ChooseDestinationAsync("Choose where to back up RRRating.pul");
        if (destination == null)
            return;
        RunBackup(() => _backups.BackupRatingFile(destination, RatingFilePath), "RRRating.pul");
    }

    private async Task<string?> ChooseDestinationAsync(string title)
    {
        var folders = await _filePicker.SelectFolderAsync(title);
        return StoragePaths.TryResolveLocalPath(folders.FirstOrDefault());
    }

    private string RatingFilePath => _fileSystem.Path.GetRrRatingFilePath(_dolphinPaths.WiiFolderPath);

    private void RunBackup(Func<FileBackupResult> backup, string label)
    {
        BackupRksysButton.IsEnabled = false;
        BackupRatingButton.IsEnabled = false;
        ImportRksysButton.IsEnabled = false;
        ImportRatingButton.IsEnabled = false;
        BackupResultText.IsVisible = true;
        BackupResultText.Text = $"Backing up {label}…";
        try
        {
            var result = backup();
            BackupResultText.Text = $"Backed up {result.CopiedFiles.Count} file(s) to {result.DestinationFolder}.";
        }
        catch (Exception exception)
        {
            BackupResultText.Text = exception.Message;
        }
        finally
        {
            RefreshFileStatus();
        }
    }

    #region Import

    private const int RksysSize = 0x2BC000;

    private static readonly MarioKartWiiEnums.Regions[] Regions =
    [
        MarioKartWiiEnums.Regions.America,
        MarioKartWiiEnums.Regions.Europe,
        MarioKartWiiEnums.Regions.Japan,
        MarioKartWiiEnums.Regions.Korea,
    ];

    internal static string KindText(string kind) =>
        kind switch
        {
            "manual" => "Manual",
            "change" => "Before a friend code change",
            "undo" => "Before an undo",
            "before-restore" => "Before a restore",
            "before-import" => "Before an import",
            "before-license-import" => "Before a license import",
            "restore-change" => "Before a friend code restore",
            _ => kind,
        };

    private const int KeepWhenCleaning = 10;

    /// <summary>A backup is listed by the name it was given, or by when it was made.</summary>
    internal static string Title(SaveBackup backup) => backup.Label ?? $"{backup.Time:d MMM yyyy, HH:mm}";

    private string Detail(SaveBackup backup)
    {
        var codes = string.Join("\n", _licenses.GetBackupLicenses(backup.Name).Select(l => l.FriendCode));
        return backup.Label is null ? codes : $"{backup.Time:d MMM yyyy, HH:mm}\n{codes}".TrimEnd();
    }

    /// <summary>Save a copy of the save now, name an earlier save, delete one, or clear out the old ones.</summary>
    private async void ManageBackups_OnClick(object? sender, RoutedEventArgs e)
    {
        var backups = _licenses.ListBackups();
        var options = backups
            .Select(b => new ChoiceOption(Title(b), KindText(b.Kind), Detail: Detail(b)))
            .Prepend(
                new ChoiceOption(
                    "Clear out old saves",
                    $"Keep the newest {KeepWhenCleaning}",
                    Detail: "Saves with a name and friend code changes stay"
                )
            )
            .Prepend(new ChoiceOption("Back up now", "Copy", Detail: "Keeps the save as it is; name it afterwards"))
            .ToList();
        var picked = await _popups
            .CreateChoiceList(
                "Earlier saves",
                "Choose a save",
                "Make a copy now, name or delete an earlier save, or clear out the old ones.",
                options
            )
            .AwaitAnswer();
        if (picked is not { } index || index < 0 || index >= options.Count)
            return;

        if (index == 0)
        {
            var made = _licenses.BackupSaveNow();
            Report(made.IsFailure ? made.Error.Message : "Backed up the save. Use Manage to give it a name.");
            return;
        }

        if (index == 1)
        {
            var removed = _licenses.PruneBackups(KeepWhenCleaning);
            Report(removed == 0 ? "Nothing to clear out." : $"Deleted {removed} old {(removed == 1 ? "save" : "saves")}.");
            return;
        }

        var backup = backups[index - 2];
        var action = await _popups
            .CreateChoiceList(
                "Earlier saves",
                Title(backup),
                KindText(backup.Kind),
                [
                    new ChoiceOption(
                        backup.Label is null ? "Give it a name" : "Change the name",
                        "Name",
                        Detail: "A named save is never cleared out"
                    ),
                    new ChoiceOption("Delete it", "Delete", Detail: "This cannot be undone"),
                ]
            )
            .AwaitAnswer();
        if (action == 0)
        {
            var label = await _popups
                .Create<TextInputWindow>()
                .SetMainText("Name this save")
                .SetExtraText("Leave it empty to remove the name.")
                .SetPlaceholderText("For example: before the tournament")
                .SetInitialText(backup.Label ?? "")
                .SetButtonText("Cancel", "Save")
                .ShowDialog();
            if (label is null)
                return;
            var named = _licenses.NameBackup(backup.Name, label);
            Report(named.IsFailure ? named.Error.Message : "Saved.");
        }
        else if (action == 1)
        {
            var yes = await _popups
                .Create<YesNoWindow>()
                .SetMainText($"Delete {Title(backup)}?")
                .SetExtraText("This save is removed for good.")
                .AwaitAnswer();
            if (!yes)
                return;
            var deleted = _licenses.DeleteBackup(backup.Name);
            Report(deleted.IsFailure ? deleted.Error.Message : "Deleted.");
        }
    }

    /// <summary>Puts back one of the copies kept before each change to the save, after showing its friend codes.</summary>
    private async void RestoreRksys_OnClick(object? sender, RoutedEventArgs e)
    {
        var backups = _licenses.ListBackups();
        if (backups.Count == 0)
        {
            Report("No earlier saves yet. One is kept each time the save is changed.");
            return;
        }

        var options = backups.Select(b => new ChoiceOption(Title(b), KindText(b.Kind), Detail: Detail(b))).ToList();
        var index = await _popups
            .CreateChoiceList("Restore a save", "Choose a save", "You see its friend codes before anything changes.", options)
            .AwaitAnswer();
        if (index is not { } chosen || chosen < 0 || chosen >= backups.Count)
            return;
        if (FriendCodeSettings.BlockWhileGameRuns())
            return;

        var backup = backups[chosen];
        var licenses = _licenses.GetBackupLicenses(backup.Name);
        var codes =
            licenses.Count == 0
                ? "No licenses found in this save."
                : string.Join("\n", licenses.Select(l => $"{(string.IsNullOrWhiteSpace(l.Name) ? "No name" : l.Name)}   {l.FriendCode}"));
        var yes = await _popups
            .Create<YesNoWindow>()
            .SetMainText(backup.Label is null ? $"Restore the save from {backup.Time:d MMM, HH:mm}?" : $"Restore \"{backup.Label}\"?")
            .SetExtraText($"{codes}\n\nYour current save is kept first.")
            .AwaitAnswer();
        if (!yes)
            return;

        var result = _licenses.RestoreBackup(backup.Name);
        if (result.IsFailure)
        {
            Report(result.Error.Message);
            return;
        }

        Report("Save restored.");
        ViewUtils.GetLayout().UpdateSidebarProfile();
        ViewUtils.GetLayout().UpdateFriendCount();
        RefreshFileStatus();
    }

    private void Report(string text)
    {
        BackupResultText.IsVisible = true;
        BackupResultText.Text = text;
    }

    private async void ImportRksys_OnClick(object? sender, RoutedEventArgs e)
    {
        if (FriendCodeSettings.BlockWhileGameRuns())
            return;

        var path = await _filePicker.OpenSingleFileAsync(
            "Import a save (rksys.dat)",
            [new FilePickerFileType("Mario Kart Wii save") { Patterns = ["*.dat"], MimeTypes = ["application/octet-stream"] }]
        );
        if (path is null)
            return;

        byte[] data;
        try
        {
            data = _fileSystem.File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report("Could not read that file.");
            return;
        }

        if (data.Length != RksysSize || System.Text.Encoding.ASCII.GetString(data, 0, 8) != "RKSD0006")
        {
            Report("That is not a Mario Kart Wii save file.");
            return;
        }

        var options = Regions.Select(r => new ChoiceOption(RegionName(r), GameRegion.GetGameId(r))).ToList();
        var index = await _popups
            .CreateChoiceList(
                "Import save",
                "Which region?",
                "The save goes to this region. The save already there is backed up first.",
                options
            )
            .AwaitAnswer();
        if (index is not >= 0 || index >= Regions.Length)
            return;

        try
        {
            var folder = _fileSystem.Path.Combine(_distributionPaths.SaveFolderPath, GameRegion.GetGameId(Regions[index.Value]));
            _fileSystem.Directory.CreateDirectory(folder);
            var target = _fileSystem.Path.Combine(folder, "rksys.dat");
            if (_fileSystem.File.Exists(target))
            {
                var backup = _fileSystem.Path.Combine(folder, "backup", $"{DateTime.Now:yyyyMMdd-HHmmss}-before-import");
                _fileSystem.Directory.CreateDirectory(backup);
                _fileSystem.File.Copy(target, _fileSystem.Path.Combine(backup, "rksys.dat"), overwrite: true);
            }

            GameLicenseSingletonService.FixRksysCrc(data);
            _fileSystem.File.WriteAllBytes(target, data);
            Report($"Imported into {RegionName(Regions[index.Value])}.");
            // The launcher keeps the licenses it read, so read them again in case this was the region in use.
            _licenses.LoadLicense();
            ViewUtils.GetLayout().UpdateSidebarProfile();
            ViewUtils.GetLayout().UpdateFriendCount();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report("Could not write the save.");
        }
        finally
        {
            RefreshFileStatus();
        }
    }

    private async void ImportRating_OnClick(object? sender, RoutedEventArgs e)
    {
        var path = await _filePicker.OpenSingleFileAsync(
            "Import RRRating.pul",
            [new FilePickerFileType("Rating file") { Patterns = ["*.pul"], MimeTypes = ["application/octet-stream"] }]
        );
        if (path is null)
            return;

        try
        {
            var target = RatingFilePath;
            _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(target)!);
            if (_fileSystem.File.Exists(target))
                _fileSystem.File.Copy(target, $"{target}.before-import-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);

            _fileSystem.File.Copy(path, target, overwrite: true);
            Report("Imported RRRating.pul. The old one was kept next to it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report("Could not copy that file.");
        }
        finally
        {
            RefreshFileStatus();
        }
    }

    #endregion
}
