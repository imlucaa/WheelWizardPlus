using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NSubstitute;
using WheelWizard.ApplicationData;
using WheelWizard.RrLounge;

namespace WheelWizard.UI.Test;

public class RrLoungeImagesTests
{
    // Nothing answers here, so these tests never touch the network.
    private const string Offline = "https://rr-lounge.invalid/icons/new_diamond.png";

    private static RrLoungeImages Store(string folder)
    {
        var location = Substitute.For<IApplicationDataLocation>();
        location.DirectoryPath.Returns(folder);
        return new RrLoungeImages(location);
    }

    private static void SavePicture(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var bitmap = new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        bitmap.Save(file, new PngBitmapEncoderOptions());
    }

    [Fact]
    public void FileNames_AreOnePlainNamePerPicture()
    {
        Assert.Equal("rr-lounge.com_icons_new_diamond.png", RrLoungeImages.FileName("https://rr-lounge.com/icons/new_diamond.png"));
        Assert.Equal("flagcdn.com_16x12_us.png", RrLoungeImages.FileName("https://flagcdn.com/16x12/us.png"));
    }

    [AvaloniaFact]
    public async Task ASavedPicture_IsUsed_WithoutDownloadingItAgain()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lounge-images-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = Store(folder);
            SavePicture(Path.Combine(store.Folder!, RrLoungeImages.FileName(Offline)));

            var bitmap = await store.GetAsync(Offline, 160);

            Assert.NotNull(bitmap);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaFact]
    public async Task AnOldPicture_IsStillShown_WhenTheSiteCannotBeReached()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lounge-images-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = Store(folder);
            var file = Path.Combine(store.Folder!, RrLoungeImages.FileName(Offline));
            SavePicture(file);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow - RrLoungeImages.RefreshAfter - TimeSpan.FromDays(1));

            Assert.NotNull(await store.GetAsync(Offline, 160));
            // Nothing saved and nothing reachable: no picture, and no crash.
            Assert.Null(await Store(folder).GetAsync("https://rr-lounge.invalid/icons/none.png", 160));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
