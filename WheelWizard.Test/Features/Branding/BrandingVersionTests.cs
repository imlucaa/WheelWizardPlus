using Semver;
using WheelWizard.Branding;

namespace WheelWizard.Test.Features.Versioning;

public class BrandingVersionTests
{
    [Fact]
    public void Version_IsAPlainSemanticVersion_WithoutTheBuildCommit()
    {
        var version = new BrandingSingletonService().Branding.Version;

        Assert.DoesNotContain('+', version);
        Assert.True(SemVersion.TryParse(version, SemVersionStyles.Any, out _));
    }

    [Fact]
    public void APrereleaseVersion_IsOlderThanItsFinalRelease_SoTheUpdaterOffersTheFinal()
    {
        var prerelease = SemVersion.Parse("2.6.8-pre.1", SemVersionStyles.Any);
        var final = SemVersion.Parse("2.6.8", SemVersionStyles.Any);
        var previous = SemVersion.Parse("2.6.7", SemVersionStyles.Any);

        Assert.True(final.ComparePrecedenceTo(prerelease) > 0);
        Assert.True(prerelease.ComparePrecedenceTo(previous) > 0);
    }
}
