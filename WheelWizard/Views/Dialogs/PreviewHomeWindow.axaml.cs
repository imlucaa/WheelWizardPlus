using Avalonia.Controls;
using Avalonia.Media;
using WheelWizard.Themes;
using WheelWizard.Views.Dialogs.Base;
using WheelWizard.Views.Pages;
using WheelWizard.Views.Shell.Controls;

namespace WheelWizard.Views.Dialogs;

/// <summary>
/// A copy of the launcher that has its own theme resources, so it uses the launcher's real controls and brushes and shows
/// exactly what the chosen colors look like without changing the real launcher.
/// </summary>
public partial class PreviewHomeWindow : PopupContent
{
    public bool IsOpen { get; private set; } = true;
    private bool _hideHomeTitle;

    public PreviewHomeWindow(
        LauncherThemeFile theme,
        LauncherBackgroundState background,
        double surfaceOpacity,
        bool minimalHomeArtwork,
        bool hideHomeTitle
    )
        : base(true, false, true, "Launcher preview")
    {
        InitializeComponent();
        IsDismissible = true;
        foreach (var tab in PreviewTabs.Children.OfType<SidebarRadioButton>())
            tab.NavigationRequested += (_, _) => ShowPage((string?)tab.Tag ?? "Home");
        UpdatePreview(theme, background, surfaceOpacity, minimalHomeArtwork, hideHomeTitle);
    }

    public async void UpdatePreview(
        LauncherThemeFile theme,
        LauncherBackgroundState background,
        double surfaceOpacity,
        bool minimalHomeArtwork,
        bool hideHomeTitle
    )
    {
        if (!IsOpen)
            return;

        _hideHomeTitle = hideHomeTitle;
        LauncherThemeService.ApplyTo(
            PreviewRoot.Resources,
            Color.Parse(theme.Accent),
            Color.Parse(theme.Branding),
            Color.Parse(theme.Text),
            Color.Parse(theme.Background),
            surfaceOpacity,
            font: null
        );
        HomeArtwork.IsVisible = !minimalHomeArtwork;
        PreviewPageTitle.IsVisible = PreviewPageTitle.Text != "Home" || !_hideHomeTitle;
        try
        {
            await PreviewBackground.LoadAsync(background);
        }
        catch
        {
            PreviewBackground.IsVisible = false;
        }
    }

    private void ShowPage(string page)
    {
        PreviewPageTitle.Text = page;
        PreviewPageTitle.IsVisible = page != "Home" || !_hideHomeTitle;
        HomePage.IsVisible = page == "Home";
        ListPage.IsVisible = page != "Home";
        ListItemOne.Text = page == "Rooms" ? "Worldwide room" : $"Example {page.ToLowerInvariant()} item";
        ListItemTwo.Text = page == "Leaderboard" ? "2. Another Player" : "Another item";
    }

    protected override void BeforeClose()
    {
        IsOpen = false;
        PreviewBackground.Dispose();
    }
}
