using WheelWizard.RrRooms;

namespace WheelWizard.Test.Features.RrRooms;

public class RoomsViewStateTests
{
    [Fact]
    public void Serialize_ThenParse_KeepsTheSortTheVrRangeAndFriendsOnly()
    {
        var view = new RoomsViewState(2, 7000, null, true);

        Assert.Equal(view, RoomsViewState.Parse(view.Serialize()));
        Assert.Equal(new RoomsViewState(4, 5000, 9000, false), RoomsViewState.Parse(new RoomsViewState(4, 5000, 9000, false).Serialize()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("9|1|2|0")]
    [InlineData("-1|1|2|0")]
    [InlineData("1|2|3")]
    public void Parse_GivesTheDefaultView_ForAnythingMissingOrDamaged(string? text) =>
        Assert.Equal(new RoomsViewState(), RoomsViewState.Parse(text));

    [Fact]
    public void Parse_DropsAVrBoundThatIsNotANumber()
    {
        Assert.Equal(new RoomsViewState(1, null, 8000, false), RoomsViewState.Parse("1|x|8000|0"));
    }

    [Fact]
    public void Parse_IgnoresAFifthPart_FromABuildThatHadAnAutoRefreshSwitch() =>
        Assert.Equal(new RoomsViewState(2, 9000, null, true), RoomsViewState.Parse("2|9000||1|0"));
}
