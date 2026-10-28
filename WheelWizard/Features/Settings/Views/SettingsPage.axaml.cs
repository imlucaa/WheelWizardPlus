using Avalonia.Controls;
using Avalonia.Interactivity;
using WheelWizard.Settings;
using WheelWizard.Settings.Views;
using WheelWizard.Shared;
using WheelWizard.Views.Components;
using WheelWizard.Views.DesignTime;
using WheelWizard.Views.Dialogs;
using WheelWizard.Views.Shell;
using WheelWizard.Views.Shell.Navigation;

namespace WheelWizard.Settings.Views;

public partial class SettingsPage : UserControl, ILockedSidebarPage
{
    // Reveal the navigation by clipping; keep the settings content stationary during the shell resize.
    public void UpdateLayoutWidth(double availableWidth) => SettingsRoot.Width = availableWidth;

    private IPopupFactory Popups { get; }

    private ISettingsManager SettingsService { get; }

    private ISettingsSignalBus SettingsSignalBus { get; }

    private IPageFactory Pages { get; }
    private INavigationService? Navigation { get; }
    private IDisposable? _settingsSignalSubscription;
    private bool _navigationReady;

    public SettingsPage(
        IPopupFactory popups,
        ISettingsManager settingsService,
        ISettingsSignalBus settingsSignalBus,
        IPageFactory pages,
        INavigationService? navigation = null,
        Type? initialPage = null
    )
    {
        Navigation = navigation;
        Popups = popups;
        SettingsService = settingsService;
        SettingsSignalBus = settingsSignalBus;
        Pages = pages;
        InitializeComponent();
        UpdateTabVisibility();
        _settingsSignalSubscription = SettingsSignalBus.Subscribe(OnSettingChanged);

        DevButton.IsVisible = DevelopmentMode.IsEnabled;

        var initialSettingsPage = Pages.Create(initialPage ?? typeof(WhWzSettings));
        SettingsContent.Content = initialSettingsPage;
        SetCheckedSidebarButton(initialSettingsPage);
        _selectedTab = SettingPages.SelectedItem as SegmentOption;
        _navigationReady = true;

        ShowHiddenTabs();
        HiddenSidebarTabs.Changed += ShowHiddenTabs;
    }

    private readonly List<SegmentOption> _hiddenTabOptions = [];
    private SegmentOption? _selectedTab;

    // The pages hidden from the sidebar join the end of the settings tabs; one opens like it would from the sidebar.
    private void ShowHiddenTabs()
    {
        foreach (var option in _hiddenTabOptions)
            SettingPages.Items.Remove(option);
        _hiddenTabOptions.Clear();

        foreach (var tab in HiddenSidebarTabs.Current)
        {
            var option = new SegmentOption
            {
                Text = tab.Label,
                Tag = tab.Page,
                Padding = new Avalonia.Thickness(10, 12),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            _hiddenTabOptions.Add(option);
            SettingPages.Items.Add(option);
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _settingsSignalSubscription?.Dispose();
        _settingsSignalSubscription = null;
        HiddenSidebarTabs.Changed -= ShowHiddenTabs;
        base.OnUnloaded(e);
    }

    private void OnSettingChanged(SettingChangedSignal signal)
    {
        if (signal.Setting == SettingsService.ENABLE_RECOMP)
            UpdateTabVisibility();
    }

    /// <summary>
    /// In recomp mode the WiiCompiled tab is the video/data settings surface and Dolphin's Video tab
    /// is hidden: those settings only affect Dolphin and would silently do nothing for the recomp.
    /// </summary>
    private void UpdateTabVisibility()
    {
        var recompMode = SettingsService.IsRecompModeActive();
        RecompSettingsTab.IsVisible = recompMode;
        VideoSettingsTab.IsVisible = !recompMode;

        // Never leave the content on a tab that just disappeared.
        var hiddenSelected =
            (recompMode && SettingsContent.Content is VideoSettings) || (!recompMode && SettingsContent.Content is RecompSettings);
        if (!hiddenSelected)
            return;

        var fallback = Pages.Create<WhWzSettings>();
        SettingsContent.Content = fallback;
        SetCheckedSidebarButton(fallback);
    }

    private void SettingsNavigation_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_navigationReady || SettingsContent is null || SettingPages.SelectedItem is not SegmentOption radioButton)
            return;

        // A page that is hidden from the sidebar opens as its own page; the tab you were on stays selected.
        if (radioButton.Tag is Type page)
        {
            SettingPages.SelectedItem = _selectedTab;
            Navigation?.NavigateTo(page);
            return;
        }

        _selectedTab = radioButton;

        // Settings sub-pages stay in the nested Settings namespace.
        var settingsSubPagesNamespace = typeof(WhWzSettings).Namespace;
        var typeName = $"{settingsSubPagesNamespace}.{radioButton.Tag}";
        var type = Type.GetType(typeName);
        if (type == null || !typeof(UserControl).IsAssignableFrom(type) || SettingsContent.Content?.GetType() == type)
            return;

        SettingsContent.Content = Pages.Create(type);
    }

    private void SetCheckedSidebarButton(UserControl settingsPage)
    {
        foreach (var child in SettingPages.Items)
        {
            if (child is not SegmentOption radioButton)
                continue;

            if (radioButton.Tag?.ToString() == settingsPage.GetType().Name)
                SettingPages.SelectedItem = radioButton;
        }
    }

    private void DevButton_OnClick(object? sender, RoutedEventArgs e) => Popups.Create<DevToolWindow>().Show();

    public void HideDevelopmentFeatures()
    {
        DevButton.IsVisible = false;
        if (SettingsContent.Content is AppInfo)
            SettingsContent.Content = Pages.Create<AppInfo>();
    }
}
