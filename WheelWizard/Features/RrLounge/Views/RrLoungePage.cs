using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WheelWizard.Shared.Services;
using WheelWizard.Views.Components;
using WheelWizard.Views.Shell;

namespace WheelWizard.RrLounge.Views;

/// <summary>
/// The RR Lounge leaderboard (rr-lounge.com/leaderboard), like the site: the three ladders, their seasons, a player
/// search and the table, 50 players a page. Headers sort the table; clicking a player opens their profile.
/// </summary>
public sealed partial class RrLoungePage : UserControl
{
    public const int PageSize = 50;

    /// <summary>How often the open page fetches the ladder again, so new mogis show up without leaving it.</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    // Rank, player, MMR, win %, last 10, mogis.
    private const string Columns = "34,*,70,48,40,40";

    private readonly IApiCaller<IRrLoungeApi> _api;
    private readonly SegmentedControl _ladders;
    private readonly SegmentedControl _seasons;
    private readonly InputField _search;
    private readonly TextBlock _showing;
    private readonly ActionButton _previous;
    private readonly ActionButton _next;
    private readonly TextBlock _pageText;
    private StackPanel _rows = new();
    private int _tableVersion;
    private const int FirstRows = 12;
    private const int RowsPerStep = 8;
    private readonly ScrollViewer _scroll;
    private readonly Grid _body;
    private readonly ContentControl _state = new();

    private RrLoungeLadder _ladder = RrLoungeLadder.Rr;
    private string? _season;
    private RrLoungeLadderData? _data;
    private int _page = 1;
    private string _sort = "mmr";
    private bool _descending = true;
    private int _loadVersion;
    private bool _loading;
    private DateTime _loadedAt;
    private readonly DispatcherTimer _refresh = new() { Interval = RefreshEvery };
    private bool _fillingSeasons;

    // Whether the season was picked by hand; otherwise every refresh follows the site's current season, so a new
    // season shows up by itself.
    private bool _seasonPicked;

    // What the last refresh changed: each player's MMR change since the refresh before, or null for someone new.
    private Dictionary<string, int?> _changes = [];

    private readonly RrLoungeImages _images;

    // The last ladder each tab showed (current season only), kept with the app's one picture cache (so for the app's
    // lifetime; the API caller itself is made new for every page).
    // Coming back to the page shows it at once instead of a spinner and a new download; a refresh in the background then
    // brings it up to date.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        RrLoungeImages,
        Dictionary<RrLoungeLadder, (RrLoungeLadderData Data, DateTime At)>
    > Remembered = new();

    private Dictionary<RrLoungeLadder, (RrLoungeLadderData Data, DateTime At)> LastLadders => Remembered.GetOrCreateValue(_images);

    public RrLoungePage(IApiCaller<IRrLoungeApi> api, RrLoungeImages? images = null)
    {
        _api = api;
        _images = images ?? new RrLoungeImages(null);

        var title = new TextBlock
        {
            Text = "RR Lounge",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6),
        };
        title.Classes.Add("PageTitleText");

        _ladders = Segments();
        _ladders.ItemsSource = new[] { Option("Retro Rewind"), Option("Custom Tracks"), Option("Time Trial") };
        _ladders.SelectedIndex = 0;
        _ladders.SelectionChanged += (_, _) =>
        {
            if (_ladders.SelectedIndex < 0 || (RrLoungeLadder)_ladders.SelectedIndex == _ladder)
                return;
            _ladder = (RrLoungeLadder)_ladders.SelectedIndex;
            _season = null;
            _seasonPicked = false;
            _ = LoadAsync();
        };

        _seasons = Segments();
        _seasons.IsVisible = false;
        _seasons.SelectionChanged += (_, _) =>
        {
            if (_fillingSeasons || _seasons.SelectedItem is not SegmentOption { Tag: string slug } || slug == _season)
                return;
            _season = slug;
            _seasonPicked = true;
            _ = LoadAsync();
        };

        _search = new InputField
        {
            Placeholder = "Search player",
            IconData = Application.Current?.TryFindResource("Search", out var icon) == true ? icon as Geometry : null,
        };
        _search.TextChanged += (_, _) =>
        {
            _page = 1;
            ShowTable();
        };

        _showing = Cell("", "Neutral400", HorizontalAlignment.Left);
        _showing.FontSize = 12;
        _previous = PagerButton("Prev", -1);
        _next = PagerButton("Next", 1);
        _pageText = Cell("", "Neutral300");
        _pageText.FontSize = 12;
        _pageText.MinWidth = 54;
        var pager = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _previous, _pageText, _next },
        };
        Grid.SetColumn(pager, 1);
        var paging = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(2, 8, 0, 8),
            Children = { _showing, pager },
        };

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _rows,
        };
        var header = HeaderRow();
        DockPanel.SetDock(header, Dock.Top);
        var table = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = new DockPanel { Children = { header, _scroll } },
        };
        Themed(table, Border.BorderBrushProperty, "Neutral800");

        Grid.SetRow(paging, 1);
        Grid.SetRow(table, 2);
        _body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Children = { _search, paging, table } };

        var content = new Grid { Children = { _body, _profileView, _state } };
        Grid.SetRow(_ladders, 1);
        Grid.SetRow(_seasons, 2);
        Grid.SetRow(content, 3);
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("52,Auto,Auto,*"),
            Margin = new Thickness(12, 0, 12, 10),
            Children = { title, _ladders, _seasons, content },
        };

        // Live while the page is open: it refreshes every minute, and straight away when coming back to a stale page.
        InitProfile();
        _refresh.Tick += (_, _) =>
        {
            _ = LoadAsync(quiet: true);
            if (_profileName is not null)
                _ = LoadProfileAsync(quiet: true);
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (_data is null && LastLadders.TryGetValue(_ladder, out var last))
            {
                _data = last.Data;
                _season = last.Data.Season;
                _loadedAt = last.At;
                FillSeasons(last.Data.Seasons);
                ShowState(null);
                ShowTable();
            }

            if (_data is null)
                _ = LoadAsync();
            else if (DateTime.UtcNow - _loadedAt >= RefreshEvery)
                _ = LoadAsync(quiet: true);
            _refresh.Start();
        };
        DetachedFromVisualTree += (_, _) => _refresh.Stop();
    }

    private static SegmentedControl Segments() =>
        new()
        {
            Variant = SegmentedControlVariant.Light,
            HasBorder = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };

    private static SegmentOption Option(string text, string? tag = null) =>
        new()
        {
            Text = text,
            Tag = tag,
            Padding = new Thickness(12, 6),
        };

    private ActionButton PagerButton(string text, int step)
    {
        var button = new ActionButton
        {
            Text = text,
            Size = ButtonSize.Compact,
            Tone = ActionButtonTone.Secondary,
        };
        button.Click += (_, _) =>
        {
            _page += step;
            ShowTable();
        };
        return button;
    }

    #region Colours and cells

    // Follows the theme: the resource is looked up again whenever the theme changes.
    private static void Themed(AvaloniaObject target, AvaloniaProperty property, string key)
    {
        if (target is IResourceHost host)
            target.Bind(property, host.GetResourceObservable(key, value => value is Color color ? new SolidColorBrush(color) : value));
    }

    private static TextBlock Cell(string text, string? brush = "Neutral100", HorizontalAlignment align = HorizontalAlignment.Center)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 13,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (brush is not null)
            Themed(block, TextBlock.ForegroundProperty, brush);
        return block;
    }

    private static TextBlock Colored(string text, string hex)
    {
        var block = Cell(text, null);
        block.Foreground = new SolidColorBrush(Color.TryParse(hex, out var color) ? Readable(color) : Colors.White);
        return block;
    }

    // Some of the site's rank colours (Legend is #470000) are too dark to read on the dark table; they are made lighter,
    // keeping their hue.
    public static Color Readable(Color color)
    {
        var hsl = color.ToHsl();
        return hsl.L >= 0.45 ? color : new HslColor(1, hsl.H, hsl.S, 0.45).ToRgb();
    }

    // MMR in the colour of its rank, with the rank's name on hover, like the site.
    private TextBlock Mmr(int? mmr)
    {
        if (mmr is not { } value)
            return Cell("-", "Neutral400");
        if (RrLoungeData.TierFor(value, _data?.Tiers ?? []) is not { } tier)
            return Cell(value.ToString());
        var cell = Colored(value.ToString(), tier.Color);
        ToolTip.SetTip(cell, tier.Name);
        return cell;
    }

    // The MMR with the rank's badge in front, for the table.
    private Control MmrWithIcon(int mmr)
    {
        var number = Mmr(mmr);
        if (RankIcon(RrLoungeData.TierFor(mmr, _data?.Tiers ?? []), 18) is not { } icon)
            return number;
        number.HorizontalAlignment = HorizontalAlignment.Left;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { icon, number },
        };
    }

    #endregion

    #region Loading

    /// <summary>
    /// Loads the ladder. A quiet load is the live refresh: no spinner, and the page, search, sort, open player and scroll
    /// stay where they are; when it fails the last table stays up and the next refresh tries again.
    /// </summary>
    private async Task LoadAsync(bool quiet = false)
    {
        if (quiet && (_loading || _data is null))
            return;
        var version = ++_loadVersion;
        _loading = true;
        if (!quiet)
        {
            _page = 1;
            ShowState(Loading());
        }

        RrLoungeLadderData? data;
        try
        {
            data = await RrLoungeData.LoadAsync(_api, _ladder, _seasonPicked ? _season : null);
        }
        finally
        {
            if (version == _loadVersion)
                _loading = false;
        }

        if (version != _loadVersion)
            return;

        if (data is null || (quiet && data.Players.Count == 0))
        {
            if (!quiet)
                ShowState(Problem());
            return;
        }

        _changes = quiet && _data is not null && data.Season == _season ? RrLoungeData.Changes(_data.Players, data.Players) : [];
        _data = data;
        _season = data.Season;
        _loadedAt = DateTime.UtcNow;
        if (!_seasonPicked)
            LastLadders[_ladder] = (data, _loadedAt);
        FillSeasons(data.Seasons);
        ShowState(null);
        ShowTable(keepScroll: quiet);
    }

    /// <summary>The live refresh, done now.</summary>
    public Task RefreshAsync() => LoadAsync(quiet: true);

    private void FillSeasons(List<RrLoungeSeasonLink> seasons)
    {
        _fillingSeasons = true;
        var options = seasons.Select(season => Option(RrLoungeData.ShortSeasonName(season.Name), season.Slug)).ToList();
        _seasons.ItemsSource = options;
        _seasons.SelectedIndex = options.FindIndex(option => Equals(option.Tag, _season));
        _seasons.IsVisible = options.Count > 1 && _profileName is null;
        _fillingSeasons = false;
    }

    private void ShowState(Control? state)
    {
        _state.Content = state;
        _state.IsVisible = state is not null;
        UpdateViews();
    }

    // The table, or a player's profile over it; the ladder and season tabs belong to the table.
    private void UpdateViews()
    {
        var ready = !_state.IsVisible;
        var profile = _profileName is not null;
        _body.IsVisible = ready && !profile;
        _profileView.IsVisible = profile;
        _ladders.IsVisible = !profile;
        _seasons.IsVisible = !profile && _seasons.ItemCount > 1;
        if (profile)
            _state.IsVisible = false;
    }

    private static Control Loading() =>
        new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new WheelWizard.Views.Components.Spinner
                {
                    Width = 56,
                    Height = 56,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                Cell("Loading the leaderboard…", "Neutral300"),
            },
        };

    private Control Problem()
    {
        var retry = new ActionButton
        {
            Text = "Retry",
            Tone = ActionButtonTone.Brand,
            Width = 150,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        retry.Click += (_, _) => _ = LoadAsync();
        var heading = Cell("Could not reach rr-lounge.com", "Neutral50");
        heading.FontSize = 17;
        return new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { heading, Cell("Check your connection and try again.", "Neutral400"), retry },
        };
    }

    #endregion

    #region Table

    private static readonly (string Title, string? Sort, string Tip)[] Headers =
    [
        ("#", null, "Rank"),
        ("Player", "name", "Sort by name"),
        ("MMR", "mmr", "Sort by MMR"),
        ("Win %", "win", "Sort by win %"),
        ("L10", "last10", "Wins and losses in the last 10 mogis"),
        ("Mogis", "mogis", "Sort by mogis played"),
    ];

    private Grid HeaderRow()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(Columns),
            ColumnSpacing = 4,
            MinHeight = 36,
        };
        Themed(grid, Panel.BackgroundProperty, "Neutral900");
        for (var i = 0; i < Headers.Length; i++)
        {
            var (title, sort, tip) = Headers[i];
            var heading = Cell(title, "Neutral200", i == 1 ? HorizontalAlignment.Left : HorizontalAlignment.Center);
            heading.FontWeight = FontWeight.SemiBold;
            heading.FontSize = 12;
            var cell = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(i == 0 ? 8 : 2, 0, 2, 0),
                Child = heading,
            };
            ToolTip.SetTip(cell, tip);
            if (sort is not null)
            {
                cell.Cursor = new Cursor(StandardCursorType.Hand);
                cell.PointerReleased += (_, e) =>
                {
                    if (e.InitialPressMouseButton != MouseButton.Left)
                        return;
                    // Like the site: a new column sorts high to low, the same one again flips it.
                    _descending = _sort != sort || !_descending;
                    _sort = sort;
                    _page = 1;
                    ShowTable();
                };
            }

            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }

        return grid;
    }

    private IEnumerable<RrLoungePlayer> Sorted(IEnumerable<RrLoungePlayer> players)
    {
        // Missing stats go last whichever way the column is sorted.
        IEnumerable<RrLoungePlayer> By(Func<RrLoungePlayer, double?> key) =>
            players.OrderBy(player => key(player) is null).ThenBy(player => (_descending ? -1 : 1) * (key(player) ?? 0));

        return _sort switch
        {
            "name" => _descending
                ? players.OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
                : players.OrderByDescending(player => player.Name, StringComparer.OrdinalIgnoreCase),
            "win" => By(player => player.WinPercentage),
            "last10" => By(player => player.Last10Wins is { } wins ? (wins - (player.Last10Losses ?? 0)) * 100 + wins : null),
            "mogis" => By(player => player.MogiCount),
            _ => By(player => player.Mmr),
        };
    }

    private void ShowTable(bool keepScroll = false)
    {
        var offset = _scroll.Offset;
        // The rows go into a new panel that is not on screen yet and is swapped in at once: adding rows one by one to
        // the shown table restyled it after every row, which froze the page for a moment.
        _rows = new StackPanel();
        _scroll.Content = _rows;
        if (_data is null)
            return;

        var found = Sorted(RrLoungeData.Search(_data.Players, _search.Text)).ToList();
        var pages = Math.Max(1, (found.Count + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 1, pages);
        var shown = found.Skip((_page - 1) * PageSize).Take(PageSize).ToList();
        var first = found.Count == 0 ? 0 : (_page - 1) * PageSize + 1;
        _showing.Text =
            found.Count == 0
                ? "No players"
                : $"{first}-{first + shown.Count - 1} of {found.Count} players"
                    + (found.Count != _data.Players.Count ? $" ({_data.Players.Count} total)" : "");
        _pageText.Text = $"{_page} / {pages}";
        _previous.IsEnabled = _page > 1;
        _next.IsEnabled = _page < pages;

        if (shown.Count == 0)
        {
            var empty = Cell(_data.Players.Count == 0 ? "No players yet." : "No players match that search.", "Neutral400");
            empty.Margin = new Thickness(0, 24);
            _rows.Children.Add(empty);
        }

        // The rows in view first; the rest follow a few at a time between frames, so switching to the page never freezes
        // while a whole page of rows is styled at once.
        var panel = _rows;
        var version = ++_tableVersion;
        for (var i = 0; i < Math.Min(FirstRows, shown.Count); i++)
            panel.Children.Add(Row(shown[i], i % 2 == 1));
        void AddMore(int from)
        {
            if (version != _tableVersion)
                return;
            for (var i = from; i < Math.Min(from + RowsPerStep, shown.Count); i++)
                panel.Children.Add(Row(shown[i], i % 2 == 1));
            if (from + RowsPerStep < shown.Count)
                Dispatcher.UIThread.Post(() => AddMore(from + RowsPerStep), DispatcherPriority.Background);
        }

        if (shown.Count > FirstRows)
            Dispatcher.UIThread.Post(() => AddMore(FirstRows), DispatcherPriority.Background);
        ToolTip.SetTip(
            _showing,
            _changes.Count == 0
                ? "Refreshes every minute"
                : $"Refreshes every minute; {_changes.Count} {(_changes.Count == 1 ? "player" : "players")} changed in the last update"
        );
        if (keepScroll)
            Dispatcher.UIThread.Post(() => _scroll.Offset = offset, DispatcherPriority.Loaded);
        else
            _scroll.Offset = default;
    }

    private Border Row(RrLoungePlayer player, bool striped)
    {
        var hasStats = player.HasStats;
        var name = Cell(player.Name, "Neutral50", HorizontalAlignment.Left);
        name.FontWeight = FontWeight.SemiBold;
        var who = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (Flag(player.CountryCode) is { } flag)
            who.Children.Add(flag);
        who.Children.Add(name);

        var rank = Cell(player.Rank.ToString(), "Neutral300");
        rank.Margin = new Thickness(6, 0, 0, 0);
        Control[] cells =
        [
            rank,
            who,
            MmrWithIcon(player.Mmr),
            hasStats ? Colored(RrLoungeData.Number(player.WinPercentage), "#B0F442") : Cell("-", "Neutral400"),
            Cell(hasStats ? $"{player.Last10Wins ?? 0}-{player.Last10Losses ?? 0}" : "-"),
            Cell(player.MogiCount?.ToString() ?? "-", "Neutral300"),
        ];

        var line = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(Columns),
            ColumnSpacing = 4,
            MinHeight = 34,
        };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            line.Children.Add(cells[i]);
        }

        var row = new Border
        {
            Padding = new Thickness(2, 0),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = line,
        };
        Themed(row, Border.BorderBrushProperty, "Neutral900");
        // Someone who just played (or just joined) is tinted until the next refresh: green up, red down, blue new.
        var changed = _changes.TryGetValue(player.Id, out var change);
        var tint =
            !changed ? (Color?)null
            : change is null ? Color.FromArgb(55, 0x53, 0xA5, 0xED)
            : change >= 0 ? Color.FromArgb(55, 0x4C, 0x7A, 0x01)
            : Color.FromArgb(65, 0x75, 0x29, 0x1B);
        void Paint(bool hovered)
        {
            if (hovered)
                Themed(row, Border.BackgroundProperty, "Neutral800");
            else if (tint is { } color)
                row.Background = new SolidColorBrush(color);
            else if (striped)
                Themed(row, Border.BackgroundProperty, "Neutral900");
            else
                row.Background = Brushes.Transparent;
        }

        Paint(false);
        row.PointerEntered += (_, _) => Paint(true);
        row.PointerExited += (_, _) => Paint(false);
        ToolTip.SetTip(
            row,
            !changed ? "Open profile"
                : change is null ? "New on the leaderboard · open profile"
                : $"{(change >= 0 ? "+" : "")}{change} MMR in the last update · open profile"
        );
        row.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
                OpenProfile(player.Name);
        };
        return row;
    }

    #endregion

    #region Flags

    // The same small flag pictures the site uses; Windows has no flag emoji.
    private Control? Flag(string? countryCode)
    {
        if (RrLoungeData.FlagUrl(countryCode) is not { } url)
            return null;
        var image = RemoteImage(url, 16, 12, 0);
        ToolTip.SetTip(image, countryCode!.ToUpperInvariant());
        return image;
    }

    // The rank's badge from the site, or null when the rank has none.
    private Image? RankIcon(RrLoungeTier? tier, double size)
    {
        if (RrLoungeData.IconUrl(tier) is not { } url)
            return null;
        // The site's badges are 2600 pixels wide; a small copy is all the launcher shows (and keeps on disk).
        var image = RemoteImage(url, size, size, 160);
        ToolTip.SetTip(image, tier!.Name);
        return image;
    }

    // A picture from the web, shown once it is loaded (from disk after the first time).
    private Image RemoteImage(string url, double width, double height, int decodeWidth)
    {
        var image = new Image
        {
            Width = width,
            Height = height,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _ = ShowImageAsync(image, url, decodeWidth);
        return image;
    }

    private async Task ShowImageAsync(Image image, string url, int decodeWidth)
    {
        if (await _images.GetAsync(url, decodeWidth) is { } bitmap)
            Dispatcher.UIThread.Post(() => image.Source = bitmap);
    }

    #endregion
}
