using WheelWizard.DiscImages;

namespace WheelWizard.Test.Features.DiscImages;

public class LineColorHistoryTests
{
    [Fact]
    public void Set_ThenGet_ReturnsTheColour_ForTheSameDisc_IgnoringCase()
    {
        var saved = LineColorHistory.Set(null, @"C:\Games\MKW.iso", LineColor.Blue);

        Assert.Equal(LineColor.Blue, LineColorHistory.Get(saved, @"c:\games\mkw.ISO"));
        Assert.Null(LineColorHistory.Get(saved, @"C:\Games\other.iso"));
    }

    [Fact]
    public void Set_WithNull_ForgetsTheDisc_AndKeepsTheOthers()
    {
        var saved = LineColorHistory.Set(LineColorHistory.Set(null, "a.iso", LineColor.Red), "b.iso", LineColor.Green);

        saved = LineColorHistory.Set(saved, "a.iso", null);

        Assert.Null(LineColorHistory.Get(saved, "a.iso"));
        Assert.Equal(LineColor.Green, LineColorHistory.Get(saved, "b.iso"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a.iso\":99}")]
    public void Get_RemembersNothing_ForMissingOrDamagedValues(string? saved) => Assert.Null(LineColorHistory.Get(saved, "a.iso"));
}
