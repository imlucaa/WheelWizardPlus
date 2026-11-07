using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using WheelWizard.Settings;
using WheelWizard.StreamOverlay;

namespace WheelWizard.UI.Test;

public class OverlayPreviewTests
{
    private static List<string> Texts(Control root) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(text => text.Text ?? string.Empty)
            // Numbers drawn in the race HUD digits are pictures that carry their text.
            .Concat(root.GetVisualDescendants().OfType<WheelWizard.LoungeStats.HudSvgImage>().Select(image => image.Text))
            .ToList();

    [AvaloniaFact]
    public async Task Play_ShowsAChange_EvenWhenTheRealVrHasNotMoved()
    {
        var preview = new OverlayPreview(new OverlayConfig(Substitute.For<ISettingsManager>()), "Player");
        var window = new Window
        {
            Content = preview,
            Width = 700,
            Height = 300,
        };
        window.Show();
        // A real player whose VR has not changed yet: the change is +0.
        preview.SetPlayer("Player", 1000, 5);
        Assert.Contains("+0", Texts(window));

        preview.Replay();
        Assert.Contains("+41", Texts(window));
        var counted = new HashSet<string>();
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();
            foreach (var text in Texts(window).Where(text => text.Contains(',') || text.All(char.IsDigit)))
                counted.Add(text);
        }

        // The number counted up to the VR, and the real change came back afterwards.
        Assert.Contains(counted, text => text != "1,000" && text.Length >= 3 && text.StartsWith('9'));
        await Task.Delay(1300);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("1,000", Texts(window));
        Assert.Contains("+0", Texts(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task RaceHudChange_CountsTheChangeDownWhileTheNumberCountsUp_ThenHidesIt()
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<string>(Arg.Any<WheelWizard.Settings.Types.Setting<string>>()).Returns("{\"animations\":{\"vr\":\"race hud\"}}");
        var config = new OverlayConfig(settings);
        var preview = new OverlayPreview(config, "Player");
        var window = new Window
        {
            Content = preview,
            Width = 700,
            Height = 300,
        };
        window.Show();
        preview.SetPlayer("Player", 1000, 5);
        // At rest there is no VR change to show (the rank chip's own "+2" stays).
        var atRest = Texts(window).Where(text => text.StartsWith('+')).ToHashSet();
        Assert.DoesNotContain("+0", atRest);

        preview.Replay();
        Assert.Contains("+41", Texts(window));
        var changes = new HashSet<string>();
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();
            foreach (var text in Texts(window).Where(text => text.StartsWith('+') && !atRest.Contains(text)))
                changes.Add(text);
        }

        // It counted down below +41 on the way.
        Assert.Contains(changes, text => int.Parse(text[1..]) < 41);
        await Task.Delay(1300);
        Dispatcher.UIThread.RunJobs();
        // The race HUD VR change comes with the race HUD look, so the number is in the game digits.
        Assert.Contains("1000", Texts(window));
        Assert.DoesNotContain(Texts(window).Where(text => IsShown(window, text)), text => text.StartsWith('+') && !atRest.Contains(text));
        window.Close();
    }

    private static bool IsShown(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == text && block.IsEffectivelyVisible);
}
