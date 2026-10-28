using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WheelWizard.Settings;
using WheelWizard.Settings.Views;
using WheelWizard.Views.Shell.Controls;
using WheelWizard.Views.Shell.Views;

namespace WheelWizard.Views.Shell;

/// <summary>
/// Settings tabs pinned to the sidebar (added and removed in the sidebar editor). Each pinned tab opens as a page of its
/// own; they are sidebar buttons like the others, so they follow compact and wide.
/// </summary>
public partial class Layout
{
    private sealed record PinnableTab(
        string Tag,
        Type Page,
        Func<string> Label,
        string Icon,
        Func<ISettingsManager, bool>? Available = null
    );

    private static readonly PinnableTab[] PinnableTabs =
    [
        new("WhWzSettings", typeof(WhWzSettings), () => t("value.wheel_wizard"), "WheelIcon"),
        new("VideoSettings", typeof(VideoSettings), () => t("category.dolphin"), "DolphinIcon", settings => !settings.IsRecompModeActive()),
        new("RecompSettings", typeof(RecompSettings), () => "WiiCompiled", "Gear", settings => settings.IsRecompModeActive()),
        new("AppearanceSettings", typeof(AppearanceSettings), () => t("category.appearance"), "Eye"),
        new("OtherSettings", typeof(OtherSettings), () => t("category.other"), "Gear"),
        new("DiscordSettings", typeof(DiscordSettings), () => "Discord RPC", "DiscordIcon"),
        new("OverlaySettings", typeof(OverlaySettings), () => "VR overlay", "Play"),
        new("FriendCodeSettings", typeof(FriendCodeSettings), () => "Friend codes", "UserCouple"),
        new("LineColorSettings", typeof(LineColorSettings), () => "Line colour", "ThemePalette"),
        new("BackupSettings", typeof(BackupSettings), () => t("category.backup"), "FileExport"),
    ];

    private readonly List<SidebarRadioButton> _pinButtons = [];
    private readonly Dictionary<SidebarRadioButton, string> _pinKeys = [];
    private bool _pinsReady;

    private bool IsAvailable(PinnableTab tab) => tab.Available?.Invoke(SettingsService) ?? true;

    private List<PinnableTab> PinnedTabs()
    {
        var tags = SettingsService
            .SIDEBAR_PINNED_TABS.Get()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<PinnableTab>();
        foreach (var tag in tags)
        {
            var tab = PinnableTabs.FirstOrDefault(candidate => candidate.Tag == tag);
            if (tab is not null && result.All(existing => existing.Tag != tab.Tag))
                result.Add(tab);
        }

        return result;
    }

    private void SavePinned(IEnumerable<PinnableTab> tabs) =>
        SettingsEditing.Set(SettingsService, SettingsService.SIDEBAR_PINNED_TABS, string.Join(',', tabs.Select(tab => tab.Tag)));

    // The palette entries are colours (or brushes), so both are accepted.
    private IBrush? PinBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var found)
            ? found switch
            {
                IBrush brush => brush,
                Color color => new SolidColorBrush(color),
                _ => null,
            }
            : null;

    private Geometry? PinIcon(string key) => this.TryFindResource(key, ActualThemeVariant, out var found) ? found as Geometry : null;

    private void InitializePins()
    {
        _pinsReady = true;
        RebuildPins();
    }

    /// <summary>Makes a button for every pinned tab, then lays the whole sidebar out again.</summary>
    private void RebuildPins()
    {
        if (!_pinsReady)
            return;

        foreach (var button in _pinButtons)
            SidePanelButtons.Children.Remove(button);
        _pinButtons.Clear();
        _pinKeys.Clear();

        var pinned = PinnedTabs();
        foreach (var tab in pinned)
        {
            var button = new SidebarRadioButton
            {
                IconData = PinIcon(tab.Icon) ?? PinIcon("Settings")!,
                Text = tab.Label(),
                PageType = tab.Page,
            };
            button.Classes.Set("compact", _sidebarCollapsed);
            var page = tab.Page;
            button.NavigationRequested += (_, _) => NavigateFromPin(page);
            _pinButtons.Add(button);
            _pinKeys[button] = PinKey(tab);
        }

        ArrangeSidebar();
        UpdatePinSelection(ContentArea.Content as UserControl);
    }

    private static string PinKey(PinnableTab tab) => "pin:" + tab.Tag;

    private void NavigateFromPin(Type tab)
    {
        Navigation.NavigateTo<PinnedTabPage>(tab);
        // If the current page asked to stay (unsaved work), keep highlighting where you really are.
        if (Navigation.CurrentPage is { } current && !(current is PinnedTabPage pinned && pinned.Tab == tab))
            UpdateSidebarSelection(current);
    }

    /// <summary>A pinned tab is highlighted while its page is open.</summary>
    private void UpdatePinSelection(UserControl? page)
    {
        var tab = (page as PinnedTabPage)?.Tab;
        foreach (var button in _pinButtons)
            button.IsChecked = tab is not null && button.PageType == tab;
    }
}
