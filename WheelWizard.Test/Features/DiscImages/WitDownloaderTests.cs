using WheelWizard.DiscImages;

namespace WheelWizard.Test.Features.DiscImages;

public class WitDownloaderTests
{
    [Fact]
    public void LatestZipName_PicksTheWindowsPackageWithTheHighestRevision()
    {
        const string html = """
            <a href="/download/wit-v3.04a-r8427-cygwin64.zip">old</a>
            <a href="/download/wit-v3.05a-r8638-cygwin64.zip">new</a>
            <a href="/download/wit-v3.05a-r8638-mac.tar.gz">mac</a>
            <a href="/download/wit-v3.05a-r8638-x86_64.tar.gz">linux</a>
            """;

        Assert.Equal("wit-v3.05a-r8638-cygwin64.zip", WitDownloader.LatestZipName(html));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<a href=\"/download/wit-v3.05a-r8638-mac.tar.gz\">mac</a>")]
    [InlineData("<a href=\"/download/../evil-r1-cygwin64.zip\">x</a>")]
    public void LatestZipName_GivesNothing_WhenThePageHasNoWindowsPackage(string? html) => Assert.Null(WitDownloader.LatestZipName(html));
}
