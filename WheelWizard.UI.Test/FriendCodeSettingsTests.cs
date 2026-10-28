using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NSubstitute;
using WheelWizard.CustomDistributions;
using WheelWizard.RrRooms;
using WheelWizard.Settings;
using WheelWizard.Settings.Views;
using WheelWizard.Shared.Services;
using WheelWizard.Views.Dialogs;
using WheelWizard.WiiManagement.FriendCodes;
using WheelWizard.WiiManagement.GameLicense;

namespace WheelWizard.UI.Test;

public class FriendCodeSettingsTests
{
    private static FriendCodeSettings Page(params LicenseIdentity[] licenses)
    {
        var service = Substitute.For<IGameLicenseSingletonService>();
        service.GetLicenseIdentities().Returns(licenses);
        return new FriendCodeSettings(
            Substitute.For<IApiCaller<IRwfcApi>>(),
            service,
            Substitute.For<IPopupFactory>(),
            Substitute.For<ISettingsManager>(),
            Substitute.For<ISaveRegionService>(),
            Substitute.For<ICustomDistributionPaths>(),
            null,
            Substitute.For<IPlayerDatabase>()
        );
    }

    [AvaloniaFact]
    public void TheIdLine_SaysWhetherThePickedLicenseIsInOrder_AndOffersToFixOnlyWhatNewIdsCanFix()
    {
        // License 1 and 2 share ids; 3 is fine; 4 has the same friend code as 3 (new ids would not help).
        var page = Page(
            new LicenseIdentity(0, "a", 10, "0000-0000-0010", 100, 100),
            new LicenseIdentity(1, "b", 11, "0000-0000-0011", 100, 100),
            new LicenseIdentity(2, "c", 12, "0000-0000-0012", 300, 300),
            new LicenseIdentity(3, "d", 12, "0000-0000-0012", 400, 400)
        );
        var window = new Window { Content = page };
        window.Show();
        var text = page.FindControl<TextBlock>("IdStatusText")!;
        var fix = page.FindControl<WheelWizard.Views.Components.Button>("FixIdsButton")!;
        var tiles = page.FindControl<UniformGrid>("LicenseList")!.Children.OfType<Border>().ToList();

        // License 1 is picked first: it shares ids with license 2, which a fix can sort out.
        Assert.Contains("Shares IDs with b", text.Text);
        Assert.True(fix.IsVisible);

        Select(tiles[2]);
        Assert.Contains("same friend code as d", text.Text);
        Assert.False(fix.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void ALicenseWithGoodIds_IsConfirmed_WithNoFixButton()
    {
        var page = Page(
            new LicenseIdentity(0, "a", 10, "0000-0000-0010", 100, 100),
            new LicenseIdentity(1, "b", 11, "0000-0000-0011", 200, 200)
        );
        var window = new Window { Content = page };
        window.Show();

        Assert.Equal("IDs are unique and match.", page.FindControl<TextBlock>("IdStatusText")!.Text);
        Assert.False(page.FindControl<WheelWizard.Views.Components.Button>("FixIdsButton")!.IsVisible);
        window.Close();
    }

    private static void Select(Border tile)
    {
        // A tile is picked with a press, like the user does.
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        tile.RaiseEvent(
            new PointerPressedEventArgs(
                tile,
                pointer,
                (Visual)tile,
                new(1, 1),
                0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None
            )
            {
                RoutedEvent = InputElement.PointerPressedEvent,
            }
        );
    }
}
