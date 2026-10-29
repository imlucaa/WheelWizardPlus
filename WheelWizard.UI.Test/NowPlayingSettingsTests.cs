using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using NSubstitute;
using WheelWizard.NowPlaying;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using WheelWizard.Settings.Views;
using WheelWizard.Shared.Desktop.Storage;
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
        return new NowPlayingSettings(
            service,
            new NowPlayingConfig(settings),
            Substitute.For<IPopupFactory>(),
            Substitute.For<IFilePickerService>()
        );
    }

    [AvaloniaFact]
    public void ThePresetsAndBarSectionsStartOpen_AndTheOthersClosed()
    {
        var page = Page(out var service);
        var window = new Window { Content = page };
        window.Show();

        // OptionsHost holds a header and a panel for each section, in that order.
        var host = page.FindControl<StackPanel>("OptionsHost")!;
        var panels = host.Children.OfType<StackPanel>().ToList();

        Assert.Equal(7, panels.Count);
        Assert.Equal([true, false, false, true, false, false, false], panels.Select(panel => panel.IsVisible));
        window.Close();
        service.Dispose();
    }

    [AvaloniaFact]
    public void ThePresetTiles_ListEveryPreset_AndClickingOneAppliesIt()
    {
        var settings = Substitute.For<ISettingsManager>();
        var look = new MemorySetting("NowPlayingConfig");
        settings.NOW_PLAYING_CONFIG.Returns(look);
        settings.NOW_PLAYING_LOOKS.Returns(new MemorySetting("NowPlayingLooks"));
        settings.Get<string>(Arg.Any<Setting<string>>()).Returns(call => call.Arg<Setting<string>>().Get());
        settings.Set(Arg.Any<Setting<string>>(), Arg.Any<string>()).Returns(call => call.Arg<Setting<string>>().Set(call.ArgAt<string>(1)));
        var config = new NowPlayingConfig(settings);
        var service = new NowPlayingService(Substitute.For<IMediaSessionReader>(), TimeProvider.System);
        var page = new NowPlayingSettings(service, config, Substitute.For<IPopupFactory>(), Substitute.For<IFilePickerService>());
        var window = new Window { Content = page };
        window.Show();

        var tiles = page.GetVisualDescendants().OfType<UniformGrid>().First().Children.OfType<Border>().ToList();
        Assert.Equal(NowPlayingPresets.All.Count, tiles.Count);

        var vinyl = tiles[NowPlayingPresets.All.ToList().FindIndex(preset => preset.Name == "Vinyl")];
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        vinyl.RaiseEvent(
            new PointerPressedEventArgs(
                vinyl,
                pointer,
                (Visual)vinyl,
                new Point(1, 1),
                0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None
            )
            {
                RoutedEvent = InputElement.PointerPressedEvent,
            }
        );

        Assert.Equal("vertical", config.GetString("layout.style"));
        Assert.True(config.GetBool("effects.spin"));
        window.Close();
        service.Dispose();
    }

    private sealed class MemorySetting(string name) : Setting<string>(name, "")
    {
        protected override void ApplyValue(bool skipSave) { }
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
