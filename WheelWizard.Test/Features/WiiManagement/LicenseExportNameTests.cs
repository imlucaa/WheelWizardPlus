using WheelWizard.Settings.Views;

namespace WheelWizard.Test.Features.WiiManagement;

public class LicenseExportNameTests
{
    [Theory]
    [InlineData("luca", "0000-1511-1151", "luca 0000-1511-1151.rkp")]
    // A name made only of the game's own symbols would show as dots, so the friend code names the file.
    [InlineData(" ", "0310-0111-0111", "0310-0111-0111.rkp")]
    [InlineData("l >:3", "0000-1511-1151", "l 3 0000-1511-1151.rkp")]
    [InlineData("no name", "0990-9333-3330", "0990-9333-3330.rkp")]
    [InlineData("a/b?", "0000-0041-0070", "ab 0000-0041-0070.rkp")]
    public void TheSuggestedFileName_IsReadable_AndAlwaysHasTheFriendCode(string name, string code, string expected) =>
        Assert.Equal(expected, FriendCodeSettings.ExportFileName(name, code));
}
