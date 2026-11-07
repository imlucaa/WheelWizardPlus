using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using WheelWizard.LoungeStats;

namespace WheelWizard.UI.Test;

public class HudOutlineTests
{
    [AvaloniaFact]
    public void Outlined_GrowsThePictureByTheOutline()
    {
        var file = @"C:\Users\Administrator\Desktop\wiicomplied\wheel wizard\rr-lounge\rr-lounge.com_icons_new_legend.png";
        if (!File.Exists(file))
            return;
        using var source = new Bitmap(file);
        var outlined = HudArt.Outlined(source, "#000000", 3);
        Assert.True(outlined.PixelSize.Width > source.PixelSize.Width);
    }
}
