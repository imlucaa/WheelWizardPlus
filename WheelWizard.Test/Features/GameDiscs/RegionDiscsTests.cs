using WheelWizard.GameDiscs;
using WheelWizard.Models.Enums;
using WheelWizard.WiiManagement.GameLicense;

namespace WheelWizard.Test.Features.GameDiscs;

public class RegionDiscsTests
{
    [Theory]
    [InlineData("RMCE01", MarioKartWiiEnums.Regions.America)]
    [InlineData("RMCP01", MarioKartWiiEnums.Regions.Europe)]
    [InlineData("RMCJ01", MarioKartWiiEnums.Regions.Japan)]
    [InlineData("RMCK01", MarioKartWiiEnums.Regions.Korea)]
    [InlineData("RMCX01", MarioKartWiiEnums.Regions.None)]
    [InlineData("GALE01", MarioKartWiiEnums.Regions.None)]
    [InlineData("RMC", MarioKartWiiEnums.Regions.None)]
    [InlineData("", MarioKartWiiEnums.Regions.None)]
    [InlineData(null, MarioKartWiiEnums.Regions.None)]
    public void TheRegion_ComesFromTheGameId_OnlyForMarioKartWii(string? id, MarioKartWiiEnums.Regions expected) =>
        Assert.Equal(expected, GameRegion.FromGameId(id));

    [Fact]
    public void StoredDiscs_RoundTrip_PerRegion()
    {
        var json = RegionDiscs.WithPath("", MarioKartWiiEnums.Regions.America, "C:/roms/us.iso");
        json = RegionDiscs.WithPath(json, MarioKartWiiEnums.Regions.Europe, "C:/roms/eu.iso");

        Assert.Equal("C:/roms/us.iso", RegionDiscs.PathFor(json, MarioKartWiiEnums.Regions.America));
        Assert.Equal("C:/roms/eu.iso", RegionDiscs.PathFor(json, MarioKartWiiEnums.Regions.Europe));
        Assert.Null(RegionDiscs.PathFor(json, MarioKartWiiEnums.Regions.Japan));
        Assert.Null(
            RegionDiscs.PathFor(RegionDiscs.WithPath(json, MarioKartWiiEnums.Regions.Europe, null), MarioKartWiiEnums.Regions.Europe)
        );
        Assert.Null(RegionDiscs.PathFor("not json", MarioKartWiiEnums.Regions.America));
    }
}
