using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WheelWizard.DiscImages;
using WheelWizard.GameDiscs;
using WheelWizard.Models.Enums;
using WheelWizard.Shared.Desktop.Storage;
using WheelWizard.Views.Dialogs;
using WheelWizard.Views.Shell;

namespace WheelWizard.Settings.Views;

/// <summary>Changes the colour of the line under the player's name online, inside a game disc.</summary>
public partial class LineColorSettings : UserControl
{
    private static readonly MarioKartWiiEnums.Regions[] Regions =
    [
        MarioKartWiiEnums.Regions.America,
        MarioKartWiiEnums.Regions.Europe,
        MarioKartWiiEnums.Regions.Japan,
        MarioKartWiiEnums.Regions.Korea,
    ];

    private readonly ISettingsManager _settings;
    private readonly IFilePickerService _filePicker;
    private readonly IDiscImageService _discs;
    private readonly ILineColorService _line;
    private readonly IPopupFactory _popups;
    private readonly IWitDownloader _downloader;

    public LineColorSettings(
        ISettingsManager settings,
        IFilePickerService filePicker,
        IDiscImageService discs,
        ILineColorService line,
        IPopupFactory popups,
        IWitDownloader downloader
    )
    {
        _settings = settings;
        _filePicker = filePicker;
        _discs = discs;
        _line = line;
        _popups = popups;
        _downloader = downloader;
        InitializeComponent();
        ShowTool();
    }

    private static string DiscRegionName(MarioKartWiiEnums.Regions region) =>
        region switch
        {
            MarioKartWiiEnums.Regions.America => "America",
            MarioKartWiiEnums.Regions.Europe => "Europe",
            MarioKartWiiEnums.Regions.Japan => "Japan",
            _ => "South Korea",
        };

    private void ShowTool()
    {
        var tool = _line.FindWit();
        ToolText.Text = tool is null ? "Not set" : tool;
        ToolButton.Text = tool is null ? "Set" : "Change";
        // A tool that is already set does not need downloading again.
        DownloadToolButton.Text = tool is not null && tool == _downloader.Installed() ? "Update" : "Download";
    }

    private async void DownloadTool_OnClick(object? sender, RoutedEventArgs e)
    {
        await DownloadToolAsync();
        ShowTool();
    }

    private async void Tool_OnClick(object? sender, RoutedEventArgs e)
    {
        await PickToolAsync();
        ShowTool();
    }

    private static readonly (LineColor Color, string Name, string Hex, string Region)[] LineColors =
    [
        (LineColor.Red, "Red", "#E5484D", "Japan"),
        (LineColor.Blue, "Blue", "#3E63DD", "Americas"),
        (LineColor.Green, "Green", "#30A46C", "Europe"),
        (LineColor.Yellow, "Yellow", "#FFC53D", "Australia and New Zealand"),
        (LineColor.White, "White", "#F2F2F2", "Taiwan"),
        (LineColor.Purple, "Purple", "#8E4EC6", "South Korea"),
    ];

    private async void LineColour_OnClick(object? sender, RoutedEventArgs e)
    {
        // Wiimm's ISO Tool is asked for once; after that it is remembered.
        if (_line.FindWit() is null && !await AskForWitAsync())
            return;

        var disc = await PickLineDiscAsync();
        if (disc is null)
            return;

        var info = _discs.ReadInfo(disc);
        if (info.IsFailure)
        {
            ViewUtils.ShowSnackbar(info.Error.Message, ViewUtils.SnackbarType.Warning);
            return;
        }

        if (!RegionLine.Supports(info.Value.GameId))
        {
            ViewUtils.ShowSnackbar("That does not look like a Mario Kart Wii disc.", ViewUtils.SnackbarType.Warning);
            return;
        }

        var extension = System.IO.Path.GetExtension(disc).ToLowerInvariant();
        if (!_line.SupportedExtensions.Contains(extension))
        {
            ViewUtils.ShowSnackbar(
                $"Wiimm's ISO Tool cannot read {extension.TrimStart('.').ToUpperInvariant()} files. Turn the disc into an ISO or WBFS first.",
                ViewUtils.SnackbarType.Warning
            );
            return;
        }

        var current = await _line.CurrentAsync(disc, CancellationToken.None);
        // A disc this app replaced before remembers the colour it had, so that can be put back.
        var original = LineColorHistory.Get(_settings.Get<string>(_settings.LINE_COLOR_ORIGINALS), disc);
        var colour = await PickLineColorAsync(current, original);
        if (colour is null)
            return;
        if (colour == current)
        {
            ViewUtils.ShowSnackbar(
                $"That disc already has the {colour.ToString()!.ToLowerInvariant()} line.",
                ViewUtils.SnackbarType.Warning
            );
            return;
        }

        var file = System.IO.Path.GetFileName(disc);
        var how = await _popups
            .CreateChoiceList(
                "Line colour",
                "Replace the disc or make a copy?",
                "This takes a few minutes and needs about 5 GB free while it works.",
                [
                    new ChoiceOption("Make a copy", "Safe", Highlight: true, Detail: "The original stays as it is"),
                    new ChoiceOption("Replace the original", "Replace", Detail: file),
                ]
            )
            .AwaitAnswer();
        if (how is not (0 or 1))
            return;

        await ChangeLineAsync(disc, colour.Value, replaceOriginal: how == 1, current, original);
    }

    private async Task ChangeLineAsync(string disc, LineColor colour, bool replaceOriginal, LineColor? before, LineColor? original)
    {
        using var cancel = new CancellationTokenSource();
        var progress = new ProgressWindow("Changing the line colour").SetIndeterminate().SetCancellationTokenSource(cancel);
        progress.Show();
        OperationResult<string> result;
        try
        {
            var status = new Progress<string>(text => progress.SetExtraText(text));
            result = await _line.ChangeAsync(disc, colour, replaceOriginal, status, cancel.Token);
        }
        finally
        {
            progress.Close();
        }

        if (result.IsFailure)
        {
            await new MessageBoxWindow()
                .SetMessageType(MessageBoxWindow.MessageType.Error)
                .SetTitleText("Could not change the line colour")
                .SetInfoText(result.Error.Message)
                .ShowDialog();
            return;
        }

        if (replaceOriginal)
        {
            // The first replacement keeps what the disc had; putting that back means nothing is left to put back.
            var kept = colour == original ? null : original ?? before;
            _settings.Set(
                _settings.LINE_COLOR_ORIGINALS,
                LineColorHistory.Set(_settings.Get<string>(_settings.LINE_COLOR_ORIGINALS), disc, kept)
            );
        }

        ViewUtils.ShowSnackbar(
            replaceOriginal ? "The disc now has the new line colour." : $"Saved as {System.IO.Path.GetFileName(result.Value)}."
        );
    }

    private async Task<bool> DownloadToolAsync()
    {
        if (!_downloader.CanDownload)
        {
            ViewUtils.OpenLink(WitDownloader.DownloadPage);
            return false;
        }

        var yes = await _popups
            .Create<YesNoWindow>()
            .SetMainText("Download Wiimm's ISO Tool?")
            .SetExtraText("About 15 MB from wit.wiimm.de. It is kept in Wheel Wizard+'s own folder and used for the line colour.")
            .AwaitAnswer();
        if (!yes)
            return false;

        using var cancel = new CancellationTokenSource();
        var progress = new ProgressWindow("Downloading Wiimm's ISO Tool").SetCancellationTokenSource(cancel);
        progress.Show();
        OperationResult<string> result;
        try
        {
            result = await _downloader.DownloadAsync(new Progress<double>(value => progress.UpdateProgress((int)value)), cancel.Token);
        }
        finally
        {
            progress.Close();
        }

        var problem = result.IsFailure ? result.Error.Message : null;
        if (problem is null)
        {
            var saved = await _line.SetWitAsync(result.Value);
            problem = saved.IsFailure ? saved.Error.Message : null;
        }

        if (problem is not null)
        {
            ViewUtils.ShowSnackbar(problem, ViewUtils.SnackbarType.Warning);
            return false;
        }

        ViewUtils.ShowSnackbar("Wiimm's ISO Tool is ready.");
        return true;
    }

    private async Task<bool> AskForWitAsync()
    {
        var pick = await _popups
            .CreateChoiceList(
                "Wiimm's ISO Tool",
                "Wheel Wizard needs this tool",
                "It takes the disc apart and puts it back together. You only have to do this once.",
                [
                    new ChoiceOption("I have it", "Pick wit", Highlight: true, Detail: "Choose the wit file"),
                    _downloader.CanDownload
                        ? new ChoiceOption("Download it for me", "About 15 MB", Detail: "From wit.wiimm.de, kept in Wheel Wizard+'s folder")
                        : new ChoiceOption("Get it", "wit.wiimm.de", Detail: "Opens the download page, then come back"),
                ]
            )
            .AwaitAnswer();
        if (pick == 1)
            return await DownloadToolAsync();
        return pick == 0 && await PickToolAsync();
    }

    private async Task<bool> PickToolAsync()
    {
        var path = await _filePicker.OpenSingleFileAsync(
            "Pick wit",
            [new FilePickerFileType("Wiimm's ISO Tool") { Patterns = ["wit", "wit.exe"] }]
        );
        if (path is null)
            return false;

        var saved = await _line.SetWitAsync(path);
        ShowTool();
        if (saved.IsFailure)
            ViewUtils.ShowSnackbar(saved.Error.Message, ViewUtils.SnackbarType.Warning);
        return saved.IsSuccess;
    }

    // The discs added per region, and the game file that is set (when it is not one of them), that Wiimm's ISO Tool can read.
    private List<(string Label, string Path)> KnownDiscs()
    {
        var stored = _settings.Get<string>(_settings.GAME_DISCS);
        var discs = Regions.Select(region => (Label: DiscRegionName(region), Path: RegionDiscs.PathFor(stored, region))).ToList();
        var current = _settings.Get<string>(_settings.GAME_LOCATION);
        if (!string.IsNullOrWhiteSpace(current) && discs.All(disc => disc.Path != current))
            discs.Add(("Game file", current));

        return discs
            .Where(disc =>
                disc.Path is not null
                && System.IO.File.Exists(disc.Path)
                && _line.SupportedExtensions.Contains(System.IO.Path.GetExtension(disc.Path).ToLowerInvariant())
            )
            .Select(disc => (disc.Label, disc.Path!))
            .ToList();
    }

    private async Task<string?> PickLineDiscAsync()
    {
        var added = KnownDiscs();
        if (added.Count > 0)
        {
            var options = added
                .Select(disc => new ChoiceOption(
                    disc.Label,
                    System.IO.Path.GetExtension(disc.Path).TrimStart('.').ToUpperInvariant(),
                    Detail: System.IO.Path.GetFileName(disc.Path)
                ))
                .Append(new ChoiceOption("Another disc", "Browse", Detail: "Pick a disc file"))
                .ToList();
            var index = await _popups
                .CreateChoiceList("Line colour", "Which disc?", "Pick one of your discs, or browse for another.", options)
                .AwaitAnswer();
            if (index is null)
                return null;
            if (index < added.Count)
                return added[index.Value].Path;
        }

        return await _filePicker.OpenSingleFileAsync(
            "Pick the Mario Kart Wii disc",
            [
                new FilePickerFileType("Mario Kart Wii disc")
                {
                    Patterns = _line.SupportedExtensions.Select(extension => "*" + extension).ToList(),
                    MimeTypes = ["application/octet-stream"],
                },
            ]
        );
    }

    private async Task<LineColor?> PickLineColorAsync(LineColor? current, LineColor? original)
    {
        var choices = new List<LineColor>();
        var options = new List<ChoiceOption>();
        if (original is { } back && back != current)
        {
            var info = LineColors.First(c => c.Color == back);
            choices.Add(back);
            options.Add(
                new ChoiceOption($"Back to the original ({info.Name.ToLowerInvariant()})", "Revert", Highlight: true, Swatches: [info.Hex])
            );
        }

        foreach (var c in LineColors)
        {
            choices.Add(c.Color);
            options.Add(new ChoiceOption(c.Name, current == c.Color ? "Now" : c.Region, Highlight: current == c.Color, Swatches: [c.Hex]));
        }

        var index = await _popups
            .CreateChoiceList("Line colour", "Which colour?", "The line under your name when you play online.", options)
            .AwaitAnswer();
        return index is >= 0 && index < choices.Count ? choices[index.Value] : null;
    }
}
