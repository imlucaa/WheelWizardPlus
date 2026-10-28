using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NSubstitute;
using WheelWizard.Ghosts.Views;
using WheelWizard.RrLounge.Views;
using WheelWizard.Settings;
using WheelWizard.Settings.Views;
using WheelWizard.Views.Components;
using WheelWizard.Views.Dialogs;
using WheelWizard.Views.Shell;
using WheelWizard.Views.Shell.Navigation;

namespace WheelWizard.UI.Test;

public class HiddenSidebarTabsTests
{
    [AvaloniaFact]
    public void TabsHiddenFromTheSidebar_AreListedInSettings_AndOpenFromThere()
    {
        var pages = Substitute.For<IPageFactory>();
        pages.Create(Arg.Any<Type>(), Arg.Any<object[]>()).Returns(_ => new UserControl());
        var navigation = Substitute.For<INavigationService>();
        HiddenSidebarTabs.Set([new("RR Lounge", typeof(RrLoungePage)), new("Ghosts", typeof(GhostsPage))]);
        try
        {
            var settings = new SettingsPage(
                Substitute.For<IPopupFactory>(),
                Substitute.For<ISettingsManager>(),
                Substitute.For<ISettingsSignalBus>(),
                pages,
                navigation
            );
            var window = new Window { Content = settings };
            window.Show();

            var tabs = settings.FindControl<SegmentedControl>("SettingPages")!;
            // They sit at the end of the same list as the other settings tabs.
            Assert.Equal(["RR Lounge", "Ghosts"], tabs.Items.OfType<SegmentOption>().TakeLast(2).Select(option => option.Text));
            var before = tabs.SelectedItem;

            tabs.SelectedItem = tabs.Items.OfType<SegmentOption>().Last();
            navigation.Received(1).NavigateTo(typeof(GhostsPage));
            // The tab you were on stays selected, so the same tab can be opened again.
            Assert.Same(before, tabs.SelectedItem);

            // Showing every tab again takes them off the list.
            HiddenSidebarTabs.Set([]);
            Assert.DoesNotContain(tabs.Items.OfType<SegmentOption>(), option => option.Text == "Ghosts");
            window.Close();
        }
        finally
        {
            HiddenSidebarTabs.Set([]);
        }
    }
}
