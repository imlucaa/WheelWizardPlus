using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NSubstitute;
using WheelWizard.LoungeStats;
using WheelWizard.RrLounge;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using WheelWizard.Settings.Views;
using WheelWizard.Shared.Services;
using WheelWizard.Views.Dialogs;

namespace WheelWizard.UI.Test;

public class LoungeStatsCardTests
{
    private static readonly LoungeStatsSnapshot Stats = new()
    {
        Id = "b2",
        Name = "Lumis",
        CountryCode = "US",
        Division = "Amethyst",
        DivisionColor = "#b145e3",
        Place = 12,
        Players = 551,
        Mmr = 4275,
        Wins = 121,
        Losses = 45,
    };

    private static LoungeStatsConfig Config()
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Set(Arg.Any<Setting<string>>(), Arg.Any<string>()).Returns(true);
        return new LoungeStatsConfig(settings);
    }

    private static (LoungeStatsCard Card, Window Window) Shown(
        LoungeStatsConfig config,
        LoungeStatsSnapshot? stats = null,
        bool preview = false
    )
    {
        var card = new LoungeStatsCard();
        var window = new Window { Content = card };
        window.Show();
        card.Show(stats ?? Stats, config, preview: preview);
        return (card, window);
    }

    private static List<string> Texts(Control card) =>
        card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text ?? "").ToList();

    [AvaloniaFact]
    public void TheCard_ShowsEveryStat_WithLabels()
    {
        var (card, window) = Shown(Config());

        var texts = Texts(card);
        Assert.Contains("Lumis", texts);
        Assert.Contains("AMETHYST", texts);
        Assert.Contains("4,275", texts);
        Assert.Contains("121", texts);
        Assert.Contains("45", texts);
        Assert.Contains("MMR", texts);
        Assert.Contains("WINS", texts);
        Assert.Contains("LOSSES", texts);
        Assert.DoesNotContain("#12", texts);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("show.tag", "Lumis")]
    [InlineData("show.rank", "AMETHYST")]
    [InlineData("show.mmr", "4,275")]
    [InlineData("show.wins", "121")]
    [InlineData("show.losses", "45")]
    [InlineData("show.labels", "WINS")]
    public void EachToggle_HidesItsPart(string path, string text)
    {
        var config = Config();
        config.Set(path, JsonValue.Create(false));

        var (card, window) = Shown(config);

        Assert.DoesNotContain(text, Texts(card));
        window.Close();
    }

    [AvaloniaFact]
    public void ThePlaceToggle_ShowsTheLeaderboardPlace()
    {
        var config = Config();
        config.Set("show.place", JsonValue.Create(true));

        var (card, window) = Shown(config);

        Assert.Contains("#12", Texts(card));
        window.Close();
    }

    [AvaloniaFact]
    public void TheHudStyle_DrawsTheNumbersAsHudDigits_InTheChosenColours()
    {
        var config = Config();
        config.Set("layout.style", JsonValue.Create("hud"));
        config.Set("show.badge", JsonValue.Create(false));
        config.Set("hud.emblem", JsonValue.Create(false));
        config.Set("hud.fill", JsonValue.Create("#00FF00"));
        config.Set("hud.depthSize", JsonValue.Create(7));

        var (card, window) = Shown(config);

        var digits = card.GetVisualDescendants().OfType<HudSvgImage>().Where(d => d.Text != "emblem").ToList();
        Assert.Equal(["4275", "W121", "L45"], digits.Select(d => d.Text));
        Assert.All(digits, d => Assert.Contains("#00FF00", d.Svg!.Markup, StringComparison.OrdinalIgnoreCase));
        // Without the badge no rank picture shows.
        Assert.DoesNotContain("AMETHYST", Texts(card));

        config.Set("show.labels", JsonValue.Create(false));
        card.Show(Stats, config);
        Assert.Equal(
            ["4275", "121", "45"],
            card.GetVisualDescendants().OfType<HudSvgImage>().Where(d => d.Text != "emblem").Select(d => d.Text)
        );
        window.Close();
    }

    [AvaloniaFact]
    public void TheLook_SizeOpacityAndBackground_ReachTheCard()
    {
        var config = Config();
        config.Set("layout.scale", JsonValue.Create(2));
        config.Set("layout.opacity", JsonValue.Create(0.5));
        config.Set("background.mode", JsonValue.Create("transparent"));

        var (card, window) = Shown(config);

        Assert.Equal(0.5, card.Opacity, 3);
        Assert.Null(card.Background);
        var scale = (Avalonia.Media.ScaleTransform)((LayoutTransformControl)card.Child!).LayoutTransform!;
        Assert.Equal(2, scale.ScaleX);

        config.Set("background.mode", JsonValue.Create("solid"));
        config.Set("background.color", JsonValue.Create("#102030"));
        config.Set("background.opacity", JsonValue.Create(1));
        card.Show(Stats, config);
        Assert.Equal(Avalonia.Media.Color.Parse("#FF102030"), ((Avalonia.Media.ISolidColorBrush)card.Background!).Color);
        window.Close();
    }

    [AvaloniaFact]
    public void Hidden_HidesTheCard_ButThePreviewStillShowsItFaintly()
    {
        var config = Config();
        config.Set("show.component", JsonValue.Create(false));

        var (card, window) = Shown(config);
        Assert.False(card.IsVisible);

        card.Show(Stats, config, preview: true);
        Assert.True(card.IsVisible);
        Assert.True(card.Opacity < 0.5);
        window.Close();
    }

    [AvaloniaFact]
    public void MissingNumbers_ShowADash()
    {
        var (card, window) = Shown(Config(), Stats with { Wins = null, Losses = null, Division = null });

        var texts = Texts(card);
        Assert.Equal(2, texts.Count(t => t == "-"));
        Assert.DoesNotContain("AMETHYST", texts);
        window.Close();
    }

    [AvaloniaFact]
    public void NoStatsYet_ThePreviewSaysWhatToDo()
    {
        var card = new LoungeStatsCard();
        var window = new Window { Content = card };
        window.Show();
        card.Show(null, Config(), preview: true);

        Assert.Contains(Texts(card), t => t.Contains("Pick your RR Lounge name"));
        window.Close();
    }

    [AvaloniaFact]
    public void TheSettingsTab_Builds_WithThePlayerAndShowSectionsOpen()
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Set(Arg.Any<Setting<string>>(), Arg.Any<string>()).Returns(true);
        var config = new LoungeStatsConfig(settings);
        using var service = new LoungeStatsService(Substitute.For<IApiCaller<IRrLoungeApi>>(), config, settings, TimeProvider.System);
        var page = new LoungeStatsSettings(service, config, Substitute.For<IPopupFactory>());
        var window = new Window { Content = page };
        window.Show();

        var host = page.FindControl<StackPanel>("OptionsHost")!;
        var panels = host.Children.OfType<StackPanel>().ToList();
        Assert.Equal(8, panels.Count);
        Assert.Equal([true, true, false, false, false, false, true, false], panels.Select(panel => panel.IsVisible));
        window.Close();
    }
}
