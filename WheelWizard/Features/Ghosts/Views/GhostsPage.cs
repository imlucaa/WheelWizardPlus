using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WheelWizard.RrRooms;
using WheelWizard.Settings.Views;
using WheelWizard.Shared.Desktop.Storage;
using WheelWizard.Shared.Services;
using WheelWizard.Views.Components;

namespace WheelWizard.Ghosts.Views;

/// <summary>
/// Your time trial ghosts next to the world records, laid out like the RR Lounge tab. The filters are the ones on
/// rwfc.net/tt: engine class picks which of your two ghosts (150cc or 200cc) is compared, the others pick which world
/// record it is compared with. Clicking a track shows what can be done with it: save its ghost as a file, or get a
/// faster one from the board, opened with the same filters. Import puts a ghost file you have on a track.
/// </summary>
public sealed class GhostsPage : UserControl
{
    // Track, your time, world record, how far off.
    private const string Columns = "*,62,62,54";

    // Kept while the launcher runs, so the page opens the way it was left.
    private static GhostKind _kind = GhostKind.Retro;
    private static TimeTrialFilter _filter = new();
    private static bool _noGlitch;

    // Whether the tracks are listed by how far your ghost is from the record, the furthest first, instead of in the game's order.
    private static bool _sortByGap;

    private readonly GhostImporter _importer;
    private readonly IFilePickerService _filePicker;
    private readonly IApiCaller<IRwfcApi> _api;

    private readonly SegmentedControl _kinds;
    private readonly InputField _search;
    private readonly TextBlock _summary;
    private readonly StackPanel _rows = new();
    private readonly ContentControl _importCard = new() { IsVisible = false };
    private readonly TextBlock _status;
    private readonly TextBlock _yoursHeading;
    private readonly TextBlock _gapHeading;

    private GhostTrack? _open;
    private Dictionary<(GhostKind, int), TimeSpan?> _times = [];

    // The world record on each of your tracks for the current filters, by track; null until loaded, or when rwfc.net
    // could not be reached.
    private Dictionary<(GhostKind, int), RwfcTimeTrialTime?>? _records;
    private int _recordsVersion;
    private bool _recordsFailed;

    // A ghost file waiting to be put on a track: its path and time, the speed it goes in at, and the tracks it could be
    // for (null when any track can be picked).
    private string? _filePath;
    private TimeSpan? _fileTime;
    private int _fileSpeed = 150;
    private List<GhostTrack>? _candidates;

    private Window? _downloads;

    public GhostsPage(GhostImporter importer, IFilePickerService filePicker, IApiCaller<IRwfcApi> api)
    {
        _importer = importer;
        _filePicker = filePicker;
        _api = api;

        var title = new TextBlock
        {
            Text = "Ghosts",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6),
        };
        title.Classes.Add("PageTitleText");

        _kinds = Segments(
            ["Retro tracks", "Custom tracks"],
            _kind == GhostKind.Custom ? 1 : 0,
            index =>
            {
                _kind = index == 1 ? GhostKind.Custom : GhostKind.Retro;
                _open = null;
                ShowTable();
            }
        );
        _kinds.HorizontalAlignment = HorizontalAlignment.Center;
        _kinds.Margin = new Thickness(0, 0, 0, 10);

        // The board's filters, two by two.
        var filters = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 10,
            RowSpacing = 8,
            Margin = new Thickness(0, 0, 0, 10),
        };
        void Place(Control control, int column, int row)
        {
            Grid.SetColumn(control, column);
            Grid.SetRow(control, row);
            filters.Children.Add(control);
        }

        Place(
            Labelled(
                "Engine class",
                Segments(["150cc", "200cc"], _filter.Cc == 200 ? 1 : 0, index => Refilter(_filter with { Cc = index == 1 ? 200 : 150 }))
            ),
            0,
            0
        );
        Place(
            Labelled(
                "Category",
                Segments(
                    ["Unrestricted", "No glitch"],
                    _noGlitch ? 1 : 0,
                    index =>
                    {
                        _noGlitch = index == 1;
                        Refilter(_filter);
                    }
                )
            ),
            1,
            0
        );
        Place(
            Labelled(
                "Vehicles",
                Segments(
                    ["All", "Karts", "Bikes"],
                    (int)_filter.Vehicles,
                    index => Refilter(_filter with { Vehicles = (TimeTrialVehicles)index })
                )
            ),
            0,
            1
        );
        Place(
            Labelled(
                "Shroomless",
                Segments(
                    ["Any", "Only", "None"],
                    (int)_filter.Shrooms,
                    index => Refilter(_filter with { Shrooms = (TimeTrialShrooms)index })
                )
            ),
            1,
            1
        );

        _search = new InputField
        {
            Placeholder = "Search tracks",
            IconData = Icon("Search"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        _search.TextChanged += (_, _) => ShowTable();

        // The count on the left, the three actions on the right, all in the same plain style.
        var import = Button("Import", "FileImport", "Put a ghost file you have on a track");
        import.Click += async (_, _) => await ChooseFileAsync();
        var export = Button("Export", "FileExport", "Save a ghost as a file");
        export.Click += (_, _) => Export();
        var download = Button("Get top ghosts", "Download", "Download the fastest ghosts from rwfc.net/tt");
        download.Click += (_, _) => OpenDownloads(null);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { import, export, download },
        };
        _summary = Cell("", "Neutral400", HorizontalAlignment.Left);
        _summary.FontSize = 12;
        _summary.Margin = new Thickness(2, 0, 0, 0);
        Grid.SetColumn(actions, 1);
        var tools = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(0, 0, 0, 8),
            Children = { _summary, actions },
        };

        var columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(Columns),
            ColumnSpacing = 6,
            MinHeight = 34,
            Margin = new Thickness(12, 0),
        };
        _yoursHeading = Heading("Yours", HorizontalAlignment.Center);
        _gapHeading = Heading("Gap", HorizontalAlignment.Center);
        _gapHeading.Background = Brushes.Transparent;
        _gapHeading.Cursor = new Cursor(StandardCursorType.Hand);
        _gapHeading.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left)
                return;
            _sortByGap = !_sortByGap;
            ShowTable();
        };
        Control[] headings =
        [
            Heading("Track", HorizontalAlignment.Left),
            _yoursHeading,
            Heading("WR", HorizontalAlignment.Center),
            _gapHeading,
        ];
        ToolTip.SetTip(headings[2], "The world record for these filters");
        ToolTip.SetTip(headings[3], "How far your ghost is from the world record. Click to list the furthest first.");
        for (var i = 0; i < headings.Length; i++)
        {
            Grid.SetColumn(headings[i], i);
            columns.Children.Add(headings[i]);
        }

        var headerBack = new Border { Child = columns };
        Themed(headerBack, Border.BackgroundProperty, "Neutral900");
        DockPanel.SetDock(headerBack, Dock.Top);
        var table = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = new DockPanel
            {
                Children =
                {
                    headerBack,
                    new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = _rows,
                    },
                },
            },
        };
        Themed(table, Border.BorderBrushProperty, "Neutral800");

        _status = Cell("", "Neutral300");
        _status.FontSize = 12;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 6, 0, 0);

        Control body;
        if (_importer.IsAvailable)
        {
            _importCard.Margin = new Thickness(0, 0, 0, 8);
            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto"),
                Children = { _kinds, filters, _search, tools, _importCard, table, _status },
            };
            Control[] order = [_kinds, filters, _search, tools, _importCard, table, _status];
            for (var i = 0; i < order.Length; i++)
                Grid.SetRow(order[i], i);
            body = grid;
        }
        else
        {
            body = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { Cell("Install Retro Rewind first.", "Neutral100"), Cell("Its ghosts show up here.", "Neutral400") },
            };
        }

        Grid.SetRow(body, 1);
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("52,*"),
            Margin = new Thickness(12, 0, 12, 10),
            Children = { title, body },
        };

        if (_importer.IsAvailable)
        {
            ShowTable();
            AttachedToVisualTree += (_, _) =>
            {
                if (_records is null)
                    _ = LoadRecordsAsync();
            };
        }
    }

    #region Building blocks

    // Follows the theme: the resource is looked up again whenever the theme changes.
    private static void Themed(AvaloniaObject target, AvaloniaProperty property, string key)
    {
        if (target is IResourceHost host)
            target.Bind(property, host.GetResourceObservable(key, value => value is Color color ? new SolidColorBrush(color) : value));
    }

    private static TextBlock Cell(string text, string brush = "Neutral100", HorizontalAlignment align = HorizontalAlignment.Center)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 13,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Themed(block, TextBlock.ForegroundProperty, brush);
        return block;
    }

    private static TextBlock Colored(string text, string hex)
    {
        var block = Cell(text);
        block.Foreground = new SolidColorBrush(Color.Parse(hex));
        return block;
    }

    private static TextBlock Heading(string text, HorizontalAlignment align)
    {
        var heading = Cell(text, "Neutral200", align);
        heading.FontSize = 12;
        heading.FontWeight = FontWeight.SemiBold;
        return heading;
    }

    private static Geometry? Icon(string key) => Application.Current?.TryFindResource(key, out var icon) == true ? icon as Geometry : null;

    // Every button on the page has the same plain look: the theme's brand colour can be white, which stood out.
    private static ActionButton Button(string text, string? icon, string? tip = null)
    {
        var button = new ActionButton
        {
            Text = text,
            IconData = icon is null ? null : Icon(icon),
            Size = ButtonSize.Compact,
            Tone = ActionButtonTone.Secondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (tip is not null)
            ToolTip.SetTip(button, tip);
        return button;
    }

    private static SegmentedControl Segments(string[] options, int selected, Action<int> picked)
    {
        var control = new SegmentedControl
        {
            Variant = SegmentedControlVariant.Light,
            HasBorder = false,
            ItemsSource = options.Select(option => new SegmentOption { Text = option, Padding = new Thickness(10, 5) }).ToArray(),
            SelectedIndex = selected,
        };
        control.SelectionChanged += (_, _) =>
        {
            if (control.SelectedIndex >= 0 && control.SelectedIndex != selected)
            {
                selected = control.SelectedIndex;
                picked(selected);
            }
        };
        return control;
    }

    private static Control Labelled(string label, Control control)
    {
        var title = Cell(label, "Neutral400", HorizontalAlignment.Left);
        title.FontSize = 11;
        control.HorizontalAlignment = HorizontalAlignment.Left;
        return new StackPanel { Spacing = 4, Children = { title, control } };
    }

    private void Say(string text) => _status.Text = text;

    #endregion

    #region World records

    private void Refilter(TimeTrialFilter filter)
    {
        _filter = filter;
        _fileSpeed = filter.Cc;
        _ = LoadRecordsAsync();
    }

    private async Task LoadRecordsAsync()
    {
        var version = ++_recordsVersion;
        _records = null;
        _recordsFailed = false;
        ShowTable();

        var filter = _filter;
        bool? glitch = _noGlitch ? false : null;
        var result = await _api.CallApiAsync(api =>
            api.GetTimeTrialRecordsAsync(filter.Cc, glitch, filter.ShroomlessQuery, filter.VehicleQuery)
        );
        if (version != _recordsVersion)
            return;

        if (result.IsFailure || result.Value is null)
        {
            _recordsFailed = true;
            _records = [];
            ShowTable();
            Say("Could not reach rwfc.net, so the world records are missing. They load again when a filter changes.");
            return;
        }

        // The board names its tracks the same way as Retro Rewind's ghost lists.
        var boardTracks = result.Value.Select(record => new RwfcTimeTrialTrack { Id = record.TrackId, Name = record.TrackName }).ToList();
        var byId = result
            .Value.GroupBy(record => record.TrackId)
            .ToDictionary(group => group.Key, group => group.First().ActiveWorldRecord);
        var records = new Dictionary<(GhostKind, int), RwfcTimeTrialTime?>();
        foreach (var kind in new[] { GhostKind.Retro, GhostKind.Custom })
        {
            foreach (var track in _importer.LoadTracks(kind))
            {
                if (TimeTrials.BoardTrackFor(track, boardTracks) is { } board)
                    records[(kind, track.Index)] = byId.GetValueOrDefault(board.Id);
            }
        }

        _records = records;
        if (_status.Text?.StartsWith("Could not reach rwfc.net", StringComparison.Ordinal) == true)
            Say(string.Empty);
        ShowTable();
    }

    /// <summary>"+2.341" behind the record, "WR" when the ghost is the record time or faster.</summary>
    public static string Gap(TimeSpan yours, int recordMs)
    {
        var behind = (int)Math.Round(yours.TotalMilliseconds) - recordMs;
        if (behind <= 0)
            return "WR";
        var span = TimeSpan.FromMilliseconds(behind);
        return span.TotalMinutes >= 1
            ? $"+{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds:000}"
            : $"+{span.Seconds}.{span.Milliseconds:000}";
    }

    #endregion

    #region Table

    private void ShowTable()
    {
        _rows.Children.Clear();
        _yoursHeading.Text = $"{_filter.Cc}cc";
        var query = _search.Text?.Trim() ?? string.Empty;
        var all = _candidates ?? _importer.LoadTracks(_kind).ToList();
        var tracks = all.Where(track => query.Length == 0 || track.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        // Each ghost file is read once per redraw: the count and the rows share it.
        _times = all.ToDictionary(track => (track.Kind, track.Index), track => _importer.CurrentTime(track, _filter.Cc));
        _gapHeading.Text = _sortByGap ? "Gap ▼" : "Gap";
        if (_sortByGap && _records is not null)
            tracks = tracks.OrderBy(track => GapMs(track) is null).ThenByDescending(track => GapMs(track) ?? 0).ToList();
        var withGhost = _times.Values.Count(time => time is not null);
        _summary.Text =
            _candidates is not null ? $"{tracks.Count} {(tracks.Count == 1 ? "track fits" : "tracks fit")} this ghost"
            : all.Count == 0 ? "No track list found."
            : $"{all.Count} tracks · {withGhost} with a {_filter.Cc}cc ghost" + (_records is null ? " · loading records…" : "");

        if (tracks.Count == 0 && all.Count > 0)
        {
            var none = Cell("No track matches that.", "Neutral400");
            none.Margin = new Thickness(0, 18);
            _rows.Children.Add(none);
        }

        for (var i = 0; i < tracks.Count; i++)
            _rows.Children.Add(Row(tracks[i], i % 2 == 1));
    }

    // Milliseconds your ghost is behind the record (zero or less when it is the record), or null without a ghost or a record.
    private int? GapMs(GhostTrack track)
    {
        var key = (track.Kind, track.Index);
        if (_records is null || !_records.TryGetValue(key, out var record) || record is null)
            return null;
        return _times.TryGetValue(key, out var time) && time is { } yours
            ? (int)Math.Round(yours.TotalMilliseconds) - record.FinishTimeMs
            : null;
    }

    private static bool Same(GhostTrack? a, GhostTrack? b) => a is not null && b is not null && a.Kind == b.Kind && a.Index == b.Index;

    private Control Row(GhostTrack track, bool striped)
    {
        var open = Same(track, _open);
        var name = Cell(track.Name, "Neutral50", HorizontalAlignment.Left);
        name.FontWeight = open ? FontWeight.SemiBold : FontWeight.Normal;
        var yours = _times.TryGetValue((track.Kind, track.Index), out var known) ? known : _importer.CurrentTime(track, _filter.Cc);
        var record = _records is not null && _records.TryGetValue((track.Kind, track.Index), out var found) ? found : null;

        Control recordCell;
        Control gapCell;
        if (_records is null)
        {
            recordCell = Cell("…", "Neutral500");
            gapCell = Cell("", "Neutral500");
        }
        else if (record is null)
        {
            recordCell = Cell("-", "Neutral500");
            gapCell = Cell("", "Neutral500");
            ToolTip.SetTip(recordCell, _recordsFailed ? "Could not load the records" : "No record for these filters");
        }
        else
        {
            recordCell = Cell(record.FinishTimeDisplay, "Neutral300");
            ToolTip.SetTip(
                recordCell,
                $"{record.PlayerName} · {TimeTrials.Details(record)}" + (record.HasGhostFile ? " · ghost available" : " · no ghost file")
            );
            gapCell =
                yours is not { } time ? Cell("", "Neutral500")
                : Gap(time, record.FinishTimeMs) is var gap && gap == "WR" ? Colored("WR", "#FBBF24")
                : Colored(gap, "#FF8A6B");
        }

        Control[] cells =
        [
            name,
            yours is { } mine ? Cell(GhostFile.Format(mine), "Neutral100") : Cell("-", "Neutral500"),
            recordCell,
            gapCell,
        ];
        var line = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(Columns),
            ColumnSpacing = 6,
            MinHeight = 34,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            line.Children.Add(cells[i]);
        }

        var body = new StackPanel { Children = { line } };
        if (open)
            body.Children.Add(_filePath is not null ? ImportHere(track) : Actions(track));

        var row = new Border
        {
            Padding = new Thickness(12, 0),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = body,
        };
        Themed(row, Border.BorderBrushProperty, "Neutral900");
        void Paint(bool hovered)
        {
            if (open || hovered)
                Themed(row, Border.BackgroundProperty, "Neutral800");
            else if (striped)
                Themed(row, Border.BackgroundProperty, "Neutral900");
            else
                row.Background = Brushes.Transparent;
        }

        Paint(false);
        row.PointerEntered += (_, _) => Paint(true);
        row.PointerExited += (_, _) => Paint(false);
        line.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left)
                return;
            _open = open ? null : track;
            ShowTable();
        };
        return row;
    }

    // What can be done with an open track: save its ghost, or get a faster one.
    private Control Actions(GhostTrack track)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 2, 0, 10) };
        foreach (var speed in new[] { 150, 200 })
        {
            if (_importer.CurrentTime(track, speed) is null)
                continue;
            var export = Button($"Save {speed}cc", "FileExport", "Save this ghost as a file");
            export.Margin = new Thickness(0, 0, 6, 6);
            var chosen = speed;
            export.Click += async (_, _) => await ExportAsync(track, chosen);
            panel.Children.Add(export);
        }

        foreach (var speed in new[] { 150, 200 })
        {
            if (_importer.Previous(track, speed) is not { } previous)
                continue;
            var undo = Button(
                $"Undo {speed}cc",
                null,
                $"Put back the ghost this track had before ({GhostFile.Format(previous.Time)}); the current one is kept"
            );
            undo.Margin = new Thickness(0, 0, 6, 6);
            var chosen = speed;
            undo.Click += (_, _) =>
            {
                Say(_importer.RestorePrevious(track, chosen).Message);
                ShowTable();
            };
            panel.Children.Add(undo);
        }

        var top = Button("Get top ghost", "Download", "This track's fastest times on rwfc.net/tt, with these filters");
        top.Margin = new Thickness(0, 0, 6, 6);
        top.Click += (_, _) => OpenDownloads(track);
        panel.Children.Add(top);
        return panel;
    }

    #endregion

    #region Import

    private static readonly FilePickerFileType GhostFiles = new("Mario Kart Wii ghost")
    {
        Patterns = ["*.rkg"],
        MimeTypes = ["application/octet-stream"],
    };

    private async Task ChooseFileAsync()
    {
        var path = await _filePicker.OpenSingleFileAsync("Choose a ghost file", [GhostFiles]);
        if (path is null)
            return;

        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say("Could not read that file.");
            return;
        }

        if (!GhostFile.TryRead(data, out var time))
        {
            Say("That is not a ghost file.");
            return;
        }

        _filePath = path;
        _fileTime = time;
        _fileSpeed = _filter.Cc;
        _candidates = null;
        Say(string.Empty);

        // A file named after a track (or with its code in the name) is taken at its word; otherwise the ghost's own
        // header narrows it down to the tracks it fits, closest time first.
        var named =
            GhostImporter.GuessTrack(path, _importer.LoadTracks(GhostKind.Retro))
            ?? GhostImporter.GuessTrack(path, _importer.LoadTracks(GhostKind.Custom));
        if (named is not null)
            Pick(named, _importer.Speeds(named).Contains(_filter.Cc) ? _filter.Cc : _importer.Speeds(named)[0]);
        else if (_importer.FindMatches(data) is { Count: > 0 } matches)
        {
            _candidates = matches.Select(match => match.Track).DistinctBy(track => (track.Kind, track.Index)).ToList();
            Pick(matches[0].Track, matches[0].Speed);
        }
        else
        {
            _open = null;
            Say("Could not tell the track. Pick it in the list.");
        }

        ShowImportCard();
        ShowTable();
    }

    private void Pick(GhostTrack track, int speed)
    {
        _open = track;
        _fileSpeed = speed;
        if (track.Kind != _kind)
        {
            _kind = track.Kind;
            _kinds.SelectedIndex = track.Kind == GhostKind.Custom ? 1 : 0;
        }
    }

    // The card above the table while a ghost file waits for its track.
    private void ShowImportCard()
    {
        if (_filePath is null || _fileTime is not { } time)
        {
            _importCard.IsVisible = false;
            _importCard.Content = null;
            return;
        }

        var file = Cell(Path.GetFileName(_filePath), "Neutral50", HorizontalAlignment.Left);
        file.FontWeight = FontWeight.SemiBold;
        var hint = Cell(
            _candidates is null
                ? $"Ghost {GhostFile.Format(time)}. Pick its track below."
                : $"Ghost {GhostFile.Format(time)}. These tracks fit it; the best one is open.",
            "Neutral400",
            HorizontalAlignment.Left
        );
        hint.FontSize = 12;
        hint.TextWrapping = TextWrapping.Wrap;
        var cancel = Button("Cancel", null);
        cancel.Click += (_, _) => CancelImport();
        Grid.SetColumn(cancel, 1);
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 10,
                Children =
                {
                    new StackPanel { Spacing = 2, Children = { file, hint } },
                    cancel,
                },
            },
        };
        Themed(card, Border.BackgroundProperty, "Neutral900");
        Themed(card, Border.BorderBrushProperty, "Primary400");
        _importCard.Content = card;
        _importCard.IsVisible = true;
    }

    // Inside the open track while importing: the speed, what changes, and the button that does it.
    private Control ImportHere(GhostTrack track)
    {
        var change = Cell("", "Neutral300", HorizontalAlignment.Left);
        change.FontSize = 12;
        void Describe()
        {
            var now = _importer.CurrentTime(track, _fileSpeed);
            var incoming = GhostFile.Format(_fileTime ?? TimeSpan.Zero);
            change.Text = now is { } current ? $"{GhostFile.Format(current)} → {incoming}" : $"New ghost {incoming}";
        }

        var speeds = Segments(
            ["150cc", "200cc"],
            _fileSpeed == 200 ? 1 : 0,
            index =>
            {
                _fileSpeed = index == 1 ? 200 : 150;
                Describe();
            }
        );
        speeds.VerticalAlignment = VerticalAlignment.Center;
        Describe();

        var replace = Button("Use this ghost", null, "Put the ghost on this track; the old one is kept in the Replaced folder");
        replace.Click += (_, _) => Import(track);
        Grid.SetColumn(change, 1);
        Grid.SetColumn(replace, 2);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(0, 2, 0, 10),
            Children = { speeds, change, replace },
        };
    }

    private void Import(GhostTrack track)
    {
        if (_filePath is null)
            return;
        var result = _importer.Import(_filePath, track, _fileSpeed);
        if (!result.Success)
        {
            Say(result.Message);
            return;
        }

        CancelImport();
        _open = track;
        ShowTable();
        Say(result.Message);
    }

    private void CancelImport()
    {
        _filePath = null;
        _fileTime = null;
        _candidates = null;
        Say(string.Empty);
        ShowImportCard();
        ShowTable();
    }

    #endregion

    private async Task ExportAsync(GhostTrack track, int speed)
    {
        var path = await _filePicker.SaveFileAsync("Save the ghost", [GhostFiles], _importer.ExportFileName(track, speed));
        if (path is not null)
            Say(_importer.Export(track, speed, path).Message);
    }

    /// <summary>
    /// Export saves the open track's ghost (at the engine class shown); with no track open it asks which one, in a
    /// pop-up of every ghost you have where typing finds a track and Enter takes the first.
    /// </summary>
    private void Export()
    {
        if (_open is { } track && _importer.CurrentTime(track, _filter.Cc) is not null)
        {
            _ = ExportAsync(track, _filter.Cc);
            return;
        }

        var entries = new List<GhostMatch>();
        foreach (var kind in new[] { GhostKind.Retro, GhostKind.Custom })
        {
            foreach (var each in _importer.LoadTracks(kind))
            {
                foreach (var speed in new[] { 150, 200 })
                {
                    if (_importer.CurrentTime(each, speed) is { } time)
                        entries.Add(new GhostMatch(each, speed, time));
                }
            }
        }

        if (entries.Count == 0)
        {
            Say("There are no ghosts to export yet.");
            return;
        }

        ShowExportPicker(entries);
    }

    private Window? _exportPicker;

    private void ShowExportPicker(List<GhostMatch> entries)
    {
        _exportPicker?.Close();
        var search = new InputField { Placeholder = "Type a track name", IconData = Icon("Search") };
        var list = new StackPanel();
        var empty = Cell("No track matches that.", "Neutral400");
        empty.Margin = new Thickness(0, 14);
        var window = new Window
        {
            Title = "Export a ghost",
            Width = 380,
            Height = 520,
            MinWidth = 300,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            // Solid like the other pop-ups: the default window style is see-through.
            TransparencyLevelHint = [WindowTransparencyLevel.None],
        };
        Themed(window, Window.BackgroundProperty, "Neutral950");

        var visible = new List<GhostMatch>();
        void Pick(GhostMatch entry)
        {
            window.Close();
            _ = ExportAsync(entry.Track, entry.Speed);
        }

        void Fill()
        {
            var query = search.Text?.Trim() ?? string.Empty;
            visible = entries
                .Where(entry => query.Length == 0 || entry.Track.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            list.Children.Clear();
            for (var i = 0; i < visible.Count; i++)
            {
                var entry = visible[i];
                var name = Cell(entry.Track.Name, "Neutral50", HorizontalAlignment.Left);
                var speed = Cell($"{entry.Speed}cc", "Neutral400");
                speed.FontSize = 12;
                var time = Cell(GhostFile.Format(entry.CurrentTime), "Neutral200");
                Grid.SetColumn(speed, 1);
                Grid.SetColumn(time, 2);
                var row = new Border
                {
                    Padding = new Thickness(12, 0),
                    MinHeight = 34,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,50,70"),
                        ColumnSpacing = 6,
                        Children = { name, speed, time },
                    },
                };
                var striped = i % 2 == 1;
                void Paint(bool hovered)
                {
                    if (hovered)
                        Themed(row, Border.BackgroundProperty, "Neutral800");
                    else if (striped)
                        Themed(row, Border.BackgroundProperty, "Neutral900");
                    else
                        row.Background = Brushes.Transparent;
                }

                Paint(false);
                row.PointerEntered += (_, _) => Paint(true);
                row.PointerExited += (_, _) => Paint(false);
                row.PointerReleased += (_, args) =>
                {
                    if (args.InitialPressMouseButton == MouseButton.Left)
                        Pick(entry);
                };
                list.Children.Add(row);
            }

            empty.IsVisible = visible.Count == 0;
        }

        search.TextChanged += (_, _) => Fill();
        // Enter takes the first match, so typing a name and pressing Enter is enough.
        search.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter && visible.Count > 0)
                Pick(visible[0]);
            else if (args.Key == Key.Escape)
                window.Close();
        };

        var heading = Cell("Export a ghost", "Neutral50", HorizontalAlignment.Left);
        heading.FontSize = 18;
        heading.FontWeight = FontWeight.SemiBold;
        heading.Margin = new Thickness(0, 0, 0, 12);
        var table = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Margin = new Thickness(0, 10, 0, 0),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new StackPanel { Children = { list, empty } },
            },
        };
        Themed(table, Border.BorderBrushProperty, "Neutral800");
        Grid.SetRow(search, 1);
        Grid.SetRow(table, 2);
        window.Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Margin = new Thickness(18, 16),
            Children = { heading, search, table },
        };

        Fill();
        window.Opened += (_, _) => search.Focus();
        window.Closed += (_, _) =>
        {
            if (_exportPicker == window)
                _exportPicker = null;
        };
        _exportPicker = window;
        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }

    /// <summary>Opens the time trial board (rwfc.net/tt) with this page's filters, on a track's own times when one is given.</summary>
    private void OpenDownloads(GhostTrack? track)
    {
        _downloads?.Close();
        var window = new GhostDownloadWindow(_importer, _api, this, ShowTable, track, _filter, _noGlitch, _kind == GhostKind.Custom);
        window.Closed += (_, _) =>
        {
            if (_downloads == window)
                _downloads = null;
        };
        _downloads = window;
        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }
}
