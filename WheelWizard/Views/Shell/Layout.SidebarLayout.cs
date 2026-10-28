using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using WheelWizard.Settings;
using WheelWizard.Settings.Views;
using WheelWizard.Views.Components;
using WheelWizard.Views.Shell.Controls;

namespace WheelWizard.Views.Shell;

/// <summary>
/// The sidebar's categories (any number), their names, the order of the items in them and which items are hidden. It is
/// saved as JSON in one setting; a missing or damaged value means the default sidebar, and new items always find a place.
/// </summary>
public partial class Layout
{
    // The sidebar buttons that are always there, in the order they are written in the layout file.
    private static readonly string[] BuiltInKeys =
    [
        "home",
        "profiles",
        "patches",
        "miis",
        "themes",
        "rooms",
        "leaderboard",
        "friends",
        // Named for their category: before it existed they were saved under Online, and a new name lets them move.
        "competitive-lounge",
        "competitive-ghosts",
    ];
    private const int GeneralItemCount = 5;
    private const string TestingKey = "testing";

    // RR Lounge and Ghosts have a category of their own by default.
    private const string CompetitiveName = "Competitive";
    private static readonly Dictionary<string, string> BuiltInCategories = new()
    {
        ["competitive-lounge"] = CompetitiveName,
        ["competitive-ghosts"] = CompetitiveName,
    };

    private Dictionary<string, SidebarRadioButton>? _builtInButtons;
    private readonly List<TextBlock> _extraHeaders = [];
    private Window? _sidebarEditor;

    private SidebarLayoutModel LoadSidebarLayout() =>
        SidebarLayoutModel.Parse(
            SettingsService.SIDEBAR_LAYOUT.Get(),
            BuiltInKeys,
            GeneralItemCount,
            // Testing Mode is arranged like a pinned tab: it only exists once tester mode is on.
            SettingsService.Get<bool>(SettingsService.TESTING_MODE_ENABLED)
                ? _pinKeys.Values.Append(TestingKey)
                : _pinKeys.Values,
            BuiltInCategories
        );

    private void SaveSidebarLayout(SidebarLayoutModel data) =>
        SettingsEditing.Set(SettingsService, SettingsService.SIDEBAR_LAYOUT, data.ToJson());

    private string DefaultSectionName(int section) =>
        section switch
        {
            0 => t("category.general"),
            1 => t("attribute.status_online"),
            _ => "New category",
        };

    private string SectionName(SidebarSection section, int index) =>
        string.IsNullOrWhiteSpace(section.Name) ? DefaultSectionName(index) : section.Name.Trim();

    private TextBlock HeaderFor(int section)
    {
        if (section == 0)
            return GeneralSectionText;
        if (section == 1)
            return OnlineSectionText;
        while (_extraHeaders.Count < section - 1)
        {
            var header = new TextBlock();
            header.Classes.Add("SidebarSectionText");
            _extraHeaders.Add(header);
        }

        return _extraHeaders[section - 2];
    }

    /// <summary>Puts every sidebar button where the saved layout says, and applies the category names and hidden items.</summary>
    private void ArrangeSidebar()
    {
        if (_builtInButtons is null)
        {
            var found = SidePanelButtons
                .Children.OfType<SidebarRadioButton>()
                .Where(button => button != TestingButton && !_pinButtons.Contains(button))
                .ToList();
            if (found.Count != BuiltInKeys.Length)
                return;
            _builtInButtons = BuiltInKeys.Zip(found).ToDictionary(pair => pair.First, pair => pair.Second);
        }

        var layout = LoadSidebarLayout();
        var buttons = new Dictionary<string, SidebarRadioButton>(_builtInButtons) { [TestingKey] = TestingButton };
        foreach (var (button, key) in _pinKeys)
            buttons[key] = button;

        SidePanelButtons.Children.Clear();
        var hidden = new List<HiddenSidebarTab>();
        for (var section = 0; section < layout.Sections.Count; section++)
        {
            var header = HeaderFor(section);
            header.Text = SectionName(layout.Sections[section], section);
            if (section > 0)
                SidePanelButtons.Children.Add(header);

            var anyShown = false;
            foreach (var key in layout.Sections[section].Items)
            {
                var button = buttons[key];
                var pinned = PinnableTabs.FirstOrDefault(tab => tab.Page == button.PageType && _pinKeys.ContainsKey(button));
                button.IsVisible = !layout.IsHidden(key) && (pinned is null || IsAvailable(pinned));
                // A hidden page stays reachable from Settings; a pinned settings tab is in Settings anyway.
                if (layout.IsHidden(key) && pinned is null && !_pinKeys.ContainsKey(button) && button.PageType is { } page)
                    hidden.Add(new HiddenSidebarTab(button.Text ?? key, page));
                anyShown |= button.IsVisible;
                SidePanelButtons.Children.Add(button);
            }

            // A category with nothing to show (empty, or every item hidden) would leave a heading over a gap.
            header.IsVisible = anyShown;
        }

        // Until tester mode is on Testing Mode has no place in the layout; it stays in the panel, out of sight.
        if (!SidePanelButtons.Children.Contains(TestingButton))
        {
            TestingButton.IsVisible = false;
            SidePanelButtons.Children.Add(TestingButton);
        }

        HiddenSidebarTabs.Set(hidden);
    }

    private void SidebarEditButton_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(SidebarEditButton).Properties.IsLeftButtonPressed)
            OpenSidebarEditor();
    }

    private SidebarRadioButton? SidebarButtonFor(string key) =>
        key == TestingKey ? TestingButton
        : _builtInButtons is not null && _builtInButtons.TryGetValue(key, out var built) ? built
        : _pinKeys.FirstOrDefault(pair => pair.Value == key).Key;

    private string SidebarItemLabel(string key) => SidebarButtonFor(key)?.Text ?? key;

    private Geometry? SidebarItemIcon(string key) => SidebarButtonFor(key)?.IconData;

    private void OpenSidebarEditor()
    {
        if (_sidebarEditor is not null)
        {
            _sidebarEditor.Activate();
            return;
        }

        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var window = new Window
        {
            Title = "Arrange sidebar",
            Width = 400,
            Height = 700,
            MinWidth = 340,
            MinHeight = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = scroll,
        };
        // A solid window: the app's default window style is see-through, which makes the lists hard to read.
        window.TransparencyLevelHint = [WindowTransparencyLevel.None];
        window.Background = PinBrush("Neutral950") is SolidColorBrush solid
            ? new SolidColorBrush(Color.FromRgb(solid.Color.R, solid.Color.G, solid.Color.B))
            : Brushes.Black;
        void Render() => scroll.Content = BuildSidebarEditor(Render);
        Render();
        window.Closed += (_, _) => _sidebarEditor = null;
        _sidebarEditor = window;
        window.Show(this);
    }

    private sealed class EditorRow(Border control, int section, int index, string key)
    {
        public Border Control { get; } = control;
        public int Section { get; } = section;
        public int Index { get; } = index;
        public string Key { get; } = key;
    }

    private Control BuildSidebarEditor(Action refresh)
    {
        var data = LoadSidebarLayout();
        var host = new Grid();
        var overlay = new Canvas { IsHitTestVisible = false };
        var indicator = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(2),
            Background = PinBrush("Brand400"),
            IsVisible = false,
        };
        overlay.Children.Add(indicator);

        var root = new StackPanel { Margin = new Thickness(22, 20), Spacing = 14 };
        root.Children.Add(
            new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Arrange sidebar",
                        FontSize = 18,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = PinBrush("Neutral50"),
                    },
                    new TextBlock
                    {
                        Text = "Drag items to move them. Click the eye to hide one. Rename a category by typing in its name.",
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = PinBrush("Neutral400"),
                    },
                },
            }
        );

        var rows = new List<EditorRow>();
        var lists = new List<StackPanel>();
        var cards = new List<Border>();

        // Where a dragged item would land: the gap nearest to the pointer, as (category, position).
        (int Section, int Index, double Y) NearestSlot(double pointerY)
        {
            var best = (Section: 0, Index: 0, Y: 0d);
            var bestDistance = double.MaxValue;
            for (var section = 0; section < lists.Count; section++)
            {
                var sectionRows = rows.Where(row => row.Section == section).OrderBy(row => row.Index).ToList();
                for (var position = 0; position <= sectionRows.Count; position++)
                {
                    double y;
                    if (position < sectionRows.Count)
                        y = LayoutY(sectionRows[position].Control, host);
                    else if (sectionRows.Count > 0)
                    {
                        var last = sectionRows[^1].Control;
                        y = LayoutY(last, host) + last.Bounds.Height;
                    }
                    else
                        y = LayoutY(lists[section], host);

                    var distance = Math.Abs(y - pointerY);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = (section, position, y);
                    }
                }
            }

            return best;
        }

        for (var sectionIndex = 0; sectionIndex < data.Sections.Count; sectionIndex++)
        {
            var section = sectionIndex;
            var sectionData = data.Sections[section];
            var name = new InputField { Text = sectionData.Name, Placeholder = DefaultSectionName(section) };
            name.TextChanged += (_, _) =>
            {
                sectionData.Name = name.Text ?? "";
                SaveSidebarLayout(data);
            };

            var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
            var grip = new Border
            {
                Width = 22,
                Height = 30,
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeAll),
                Child = new PathIcon
                {
                    Data = PinIcon("Grip"),
                    Width = 10,
                    Height = 14,
                    Foreground = PinBrush("Neutral500"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            ToolTip.SetTip(grip, "Drag to move this category");
            headerRow.Children.Add(grip);
            Grid.SetColumn(name, 1);
            headerRow.Children.Add(name);
            if (section > 0)
            {
                var remove = new ActionButton
                {
                    Text = "Remove",
                    Tone = ActionButtonTone.Secondary,
                    Size = ButtonSize.Compact,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                remove.Click += (_, _) =>
                {
                    data.RemoveSection(section);
                    SaveSidebarLayout(data);
                    refresh();
                };
                Grid.SetColumn(remove, 2);
                headerRow.Children.Add(remove);
            }

            var list = new StackPanel { Spacing = 3, MinHeight = 14 };
            lists.Add(list);
            for (var itemIndex = 0; itemIndex < sectionData.Items.Count; itemIndex++)
            {
                var row = BuildEditorRow(data, section, itemIndex, host, indicator, rows, NearestSlot, refresh);
                list.Children.Add(row.Control);
                rows.Add(row);
            }

            var addTab = new ActionButton
            {
                Text = "+ Add tab",
                Tone = ActionButtonTone.Secondary,
                Size = ButtonSize.Compact,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            addTab.Click += (_, _) => ShowAddTabMenu(addTab, section, refresh);

            var card = new Border
            {
                Background = PinBrush("Neutral900"),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12),
                Child = new StackPanel { Spacing = 10, Children = { headerRow, list, addTab } },
            };
            cards.Add(card);
            root.Children.Add(card);
            WireCategoryDrag(grip, card, section, cards, data, host, indicator, refresh);
        }

        var add = new ActionButton
        {
            Text = "Add category",
            Tone = ActionButtonTone.Secondary,
            Size = ButtonSize.Compact,
        };
        add.Click += (_, _) =>
        {
            data.AddSection();
            SaveSidebarLayout(data);
            refresh();
        };
        var reset = new ActionButton
        {
            Text = "Reset sidebar",
            Tone = ActionButtonTone.Secondary,
            Size = ButtonSize.Compact,
        };
        reset.Click += (_, _) =>
        {
            SettingsEditing.Set(SettingsService, SettingsService.SIDEBAR_LAYOUT, "");
            refresh();
        };
        root.Children.Add(
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { add, reset },
            }
        );

        host.Children.Add(root);
        host.Children.Add(overlay);
        return host;
    }

    /// <summary>Lists the settings tabs that are not in the sidebar yet; picking one adds it to this category.</summary>
    private void ShowAddTabMenu(Control anchor, int section, Action refresh)
    {
        var pinned = PinnedTabs();
        var items = new List<Control>();
        foreach (var tab in PinnableTabs.Where(tab => IsAvailable(tab) && pinned.All(existing => existing.Tag != tab.Tag)))
        {
            var item = new MenuItem { Header = tab.Label() };
            var chosen = tab;
            item.Click += (_, _) =>
            {
                SavePinned([.. pinned, chosen]);
                RebuildPins();
                var data = LoadSidebarLayout();
                var key = PinKey(chosen);
                foreach (var other in data.Sections)
                    other.Items.Remove(key);
                data.Sections[Math.Min(section, data.Sections.Count - 1)].Items.Add(key);
                SaveSidebarLayout(data);
                refresh();
            };
            items.Add(item);
        }

        if (items.Count == 0)
        {
            items.Add(new MenuItem { Header = "Every tab is already here", IsEnabled = false });
        }

        new ContextMenu { Placement = PlacementMode.Bottom, ItemsSource = items }.Open(anchor);
    }

    /// <summary>Lets a category be dragged by its handle to a new place among the categories.</summary>
    private void WireCategoryDrag(
        Border grip,
        Border card,
        int section,
        List<Border> cards,
        SidebarLayoutModel data,
        Grid host,
        Border indicator,
        Action refresh
    )
    {
        Point? start = null;
        var dragging = false;
        var releasing = false;

        // Where the dragged category would land: the number of other categories whose middle is above the pointer.
        int TargetIndex(double pointerY) =>
            cards.Count(other => other != card && LayoutY(other, host) + other.Bounds.Height / 2 < pointerY);

        void Reset()
        {
            start = null;
            dragging = false;
            indicator.IsVisible = false;
            card.RenderTransform = null;
            card.Opacity = 1;
            card.ZIndex = 0;
        }

        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
                return;
            start = e.GetPosition(host);
            dragging = false;
            e.Pointer.Capture(grip);
        };
        grip.PointerMoved += (_, e) =>
        {
            if (start is not { } origin)
                return;
            var point = e.GetPosition(host);
            if (!dragging && Math.Abs(point.Y - origin.Y) > 4)
            {
                dragging = true;
                card.ZIndex = 5;
                card.Opacity = 0.85;
            }

            if (!dragging)
                return;
            card.RenderTransform = new TranslateTransform(0, point.Y - origin.Y);
            var others = cards.Where(other => other != card).ToList();
            var target = TargetIndex(point.Y);
            var y = target < others.Count ? LayoutY(others[target], host) - 7 : LayoutY(others[^1], host) + others[^1].Bounds.Height + 7;
            indicator.Width = Math.Max(0, host.Bounds.Width - 44);
            Canvas.SetLeft(indicator, 22);
            Canvas.SetTop(indicator, y - 1.5);
            indicator.IsVisible = true;
        };
        grip.PointerReleased += (_, e) =>
        {
            if (start is null)
                return;
            var wasDragging = dragging;
            var target = TargetIndex(e.GetPosition(host).Y);
            releasing = true;
            e.Pointer.Capture(null);
            releasing = false;
            Reset();
            if (!wasDragging || target == section)
                return;

            data.MoveSection(section, target, DefaultSectionName);
            SaveSidebarLayout(data);
            refresh();
        };
        grip.PointerCaptureLost += (_, _) =>
        {
            if (!releasing && start is not null)
                Reset();
        };
    }

    // The place a control has in the layout, ignoring any drag offset it currently has.
    private static double LayoutY(Control control, Control host)
    {
        var y = 0d;
        for (Visual? current = control; current is Control item && !ReferenceEquals(item, host); current = current.GetVisualParent())
            y += item.Bounds.Y;
        return y;
    }

    private EditorRow BuildEditorRow(
        SidebarLayoutModel data,
        int section,
        int itemIndex,
        Grid host,
        Border indicator,
        List<EditorRow> rows,
        Func<double, (int Section, int Index, double Y)> nearestSlot,
        Action refresh
    )
    {
        var key = data.Sections[section].Items[itemIndex];
        var hidden = data.IsHidden(key);

        var grip = new PathIcon
        {
            Data = PinIcon("Grip"),
            Width = 10,
            Height = 14,
            Foreground = PinBrush("Neutral500"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = new PathIcon
        {
            Data = SidebarItemIcon(key),
            Width = 16,
            Height = 16,
            Foreground = PinBrush("Neutral300"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = SidebarItemLabel(key),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = PinBrush("Neutral100"),
        };
        var eye = new Border
        {
            Width = 30,
            Height = 26,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new PathIcon
            {
                Data = PinIcon("Eye"),
                Width = 16,
                Height = 14,
                Foreground = PinBrush(hidden ? "Neutral600" : "Neutral200"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(eye, hidden ? "Show in the sidebar" : "Hide from the sidebar");
        eye.PointerEntered += (_, _) => eye.Background = PinBrush("Neutral700");
        eye.PointerExited += (_, _) => eye.Background = Brushes.Transparent;
        eye.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(eye).Properties.IsLeftButtonPressed)
                return;
            e.Handled = true;
            data.ToggleHidden(key);
            SaveSidebarLayout(data);
            refresh();
        };

        Border? removeTab = null;
        if (key.StartsWith("pin:", StringComparison.Ordinal))
        {
            removeTab = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = "✕",
                    FontSize = 12,
                    Foreground = PinBrush("Neutral400"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            ToolTip.SetTip(removeTab, "Remove this tab from the sidebar");
            removeTab.PointerEntered += (_, _) => removeTab.Background = PinBrush("Neutral700");
            removeTab.PointerExited += (_, _) => removeTab.Background = Brushes.Transparent;
            removeTab.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(removeTab).Properties.IsLeftButtonPressed)
                    return;
                e.Handled = true;
                var tag = key["pin:".Length..];
                SavePinned(PinnedTabs().Where(tab => tab.Tag != tag));
                RebuildPins();
                refresh();
            };
        }

        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            ColumnSpacing = 10,
            Opacity = hidden ? 0.5 : 1,
        };
        content.Children.Add(grip);
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(label, 2);
        content.Children.Add(icon);
        content.Children.Add(label);
        var rowBorder = new Border
        {
            MinHeight = 38,
            Padding = new Thickness(10, 4, 6, 4),
            CornerRadius = new CornerRadius(8),
            Background = PinBrush("Neutral800"),
            Cursor = new Cursor(StandardCursorType.SizeAll),
        };
        var layer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 4 };
        layer.Children.Add(content);
        Grid.SetColumn(eye, 1);
        layer.Children.Add(eye);
        if (removeTab is not null)
        {
            Grid.SetColumn(removeTab, 2);
            layer.Children.Add(removeTab);
        }
        rowBorder.Child = layer;

        // Dragging: move the row with the pointer, show where it will land, and drop it there.
        Point? start = null;
        var dragging = false;
        var releasing = false;
        rowBorder.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(rowBorder).Properties.IsLeftButtonPressed)
                return;
            start = e.GetPosition(host);
            dragging = false;
            e.Pointer.Capture(rowBorder);
        };
        rowBorder.PointerMoved += (_, e) =>
        {
            if (start is not { } origin)
                return;
            var point = e.GetPosition(host);
            if (!dragging && Math.Abs(point.Y - origin.Y) > 4)
            {
                dragging = true;
                rowBorder.ZIndex = 5;
                rowBorder.Opacity = 0.85;
            }

            if (!dragging)
                return;
            rowBorder.RenderTransform = new TranslateTransform(0, point.Y - origin.Y);
            var slot = nearestSlot(point.Y);
            indicator.Width = Math.Max(0, host.Bounds.Width - 44);
            Canvas.SetLeft(indicator, 22);
            Canvas.SetTop(indicator, slot.Y - 2);
            indicator.IsVisible = true;
        };
        rowBorder.PointerReleased += (_, e) =>
        {
            if (start is null)
                return;
            var wasDragging = dragging;
            releasing = true;
            e.Pointer.Capture(null);
            releasing = false;
            start = null;
            dragging = false;
            indicator.IsVisible = false;
            rowBorder.RenderTransform = null;
            rowBorder.Opacity = 1;
            rowBorder.ZIndex = 0;
            if (!wasDragging)
                return;

            var target = nearestSlot(e.GetPosition(host).Y);
            data.MoveItem(section, itemIndex, target.Section, target.Index);
            SaveSidebarLayout(data);
            refresh();
        };
        rowBorder.PointerCaptureLost += (_, _) =>
        {
            if (releasing || start is null)
                return;
            start = null;
            dragging = false;
            indicator.IsVisible = false;
            rowBorder.RenderTransform = null;
            rowBorder.Opacity = 1;
            rowBorder.ZIndex = 0;
        };
        return new EditorRow(rowBorder, section, itemIndex, key);
    }
}
