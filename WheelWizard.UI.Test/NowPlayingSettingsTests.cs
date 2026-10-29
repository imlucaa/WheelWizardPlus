using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NSubstitute;
using WheelWizard.NowPlaying;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using WheelWizard.Settings.Views;
using WheelWizard.Shared.Services;
using WheelWizard.Views.Dialogs;

namespace WheelWizard.UI.Test;

public class NowPlayingSettingsTests
{
    private static NowPlayingSettings Page(out NowPlayingService service)
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Set(Arg.Any<Setting<string>>(), Arg.Any<string>()).Returns(true);
        var reader = Substitute.For<IMediaSessionReader>();
        service = new NowPlayingService(reader, TimeProvider.System);
        return new NowPlayingSettings(service, new NowPlayingConfig(settings), Substitute.For<IPopupFactory>());
    }

    [AvaloniaFact]
    public void TheStyleShowAndBarSectionsStartOpen_AndTheOthersClosed()
    {
        var page = Page(out var service);
        var window = new Window { Content = page };
        window.Show();

        // OptionsHost holds a header and a panel for each section, in that order.
        var host = page.FindControl<StackPanel>("OptionsHost")!;
        var panels = host.Children.OfType<StackPanel>().ToList();

        Assert.Equal(6, panels.Count);
        Assert.Equal([true, true, true, false, false, false], panels.Select(panel => panel.IsVisible));
        window.Close();
        service.Dispose();
    }

    [AvaloniaFact]
    public void ThePreviewShowsNothingPlayingUntilASongArrives()
    {
        var page = Page(out var service);
        var window = new Window { Content = page };
        window.Show();

        Assert.NotNull(page.FindControl<ContentControl>("PreviewHost")!.Content);
        Assert.Equal("http://127.0.0.1:47831/now-playing", page.FindControl<SelectableTextBlock>("LinkText")!.Text);
        window.Close();
        service.Dispose();
    }
}
