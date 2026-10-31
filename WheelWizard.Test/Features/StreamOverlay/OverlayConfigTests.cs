using NSubstitute;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using WheelWizard.StreamOverlay;

namespace WheelWizard.Test.Features.StreamOverlay;

public class OverlayConfigTests
{
    private static OverlayConfig Create(string saved = "")
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<string>(Arg.Any<Setting<string>>()).Returns(saved);
        settings.Set(Arg.Any<Setting<string>>(), Arg.Any<string>()).Returns(true);
        return new OverlayConfig(settings);
    }

    [Fact]
    public void Defaults_GiveAPlainLinkWithOnlyTheFriendCode()
    {
        var link = Create().BuildLink("123456789012");

        Assert.Equal("http://127.0.0.1:47831/rewind-overlay/?view=overlay&fc=123456789012", link);
    }

    [Fact]
    public void ChangedValues_TravelInTheLink_AndComeBackOutOfIt()
    {
        var config = Create();
        config.Set("layout.scale", System.Text.Json.Nodes.JsonValue.Create(1.5));

        var link = config.BuildLink("123456789012");
        var cfg = link[(link.IndexOf("&cfg=", StringComparison.Ordinal) + 5)..].Replace('-', '+').Replace('_', '/');
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cfg + new string('=', (4 - cfg.Length % 4) % 4)));

        Assert.Equal("""{"layout":{"scale":1.5}}""", json);
    }

    [Fact]
    public void AChangedVrColor_GoesIntoThePageCss_NotIntoTheLinkConfig()
    {
        var config = Create();
        config.Set("typography.vrColor", System.Text.Json.Nodes.JsonValue.Create("#ffffff"));

        var link = config.BuildLink("123456789012");

        Assert.Contains("&colors=ffffff-5ef0a5-ff6d7e-ffe88f", link);
        Assert.DoesNotContain("&cfg=", link);
        Assert.Contains(".vr-value{color:#ffffff;", config.ColorCss());
    }

    [Fact]
    public void ASavedValueOfTheWrongType_IsIgnored_InsteadOfBreakingTheEditor()
    {
        var config = Create("""{"visibility":{"avatar":5},"layout":{"width":"wide"},"border":null}""");

        Assert.True(config.GetBool("visibility.avatar"));
        Assert.Equal(560, config.GetNumber("layout.width"));
        Assert.Equal("rainbow", config.GetString("border.effect"));
    }

    [Fact]
    public void Import_IgnoresUnknownParts_AndWrongTypes()
    {
        var config = Create();

        var ok = config.TryImport("""{"look":{"layout":{"compact":true,"scale":"big"},"mystery":{"a":1}}}""");

        Assert.True(ok);
        Assert.True(config.GetBool("layout.compact"));
        Assert.Equal(1, config.GetNumber("layout.scale"));
    }

    [Fact]
    public void Import_RefusesTextThatIsNotAnObject()
    {
        var config = Create();

        Assert.False(config.TryImport("not json"));
        Assert.False(config.TryImport("[1,2]"));
    }

    [Fact]
    public void Import_DropsAPictureThatIsNotAWebLink()
    {
        var config = Create();

        config.TryImport("""{"look":{"background":{"imageUrl":"C:\\secret.png"}}}""");

        Assert.Equal("", config.GetString("background.imageUrl"));
    }

    [Fact]
    public void AnAbsurdPollTime_IsLeftOutOfTheLink()
    {
        var config = Create();
        config.Set("data.pollSeconds", System.Text.Json.Nodes.JsonValue.Create(-3));

        Assert.DoesNotContain("poll=", config.BuildLink("123456789012"));
    }

    [Fact]
    public void BuiltInPresets_CannotBeOverwritten()
    {
        var config = Create();

        config.SavePreset("Default");

        Assert.True(OverlayConfig.IsBuiltIn("Default"));
        Assert.Contains("Blue", config.PresetNames);
    }
}
