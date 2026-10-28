using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using WheelWizard.Views.Components;
using WheelWizard.Views.Dialogs;
using WheelWizard.WiiManagement.GameLicense;

namespace WheelWizard.UI.Test;

public class FriendCodeHistoryWindowTests
{
    [AvaloniaFact]
    public async Task OnlyCodesThatCanComeBack_HaveARestoreButton_AndItResolvesToThatChange()
    {
        var time = new DateTime(2026, 10, 19, 13, 40, 0);
        FriendCodeChange[] history =
        [
            new(time.AddHours(1), "undo", 0, "luca", "4939-2147-5073", "0310-0111-0111", "b3"),
            new(time.AddMinutes(30), "before-license-import", 3, "two", "", "0000-0041-0070", "b2"),
            new(time, "change", 0, "luca", "0310-0111-0111", "4939-2147-5073", "b1"),
        ];
        LicenseIdentity[] now = [new(0, "luca", 1, "0310-0111-0111", 1, 1), new(3, "two", 2, "0000-0041-0070", 2, 2)];
        var window = new FriendCodeHistoryWindow(history, now);

        var answer = window.AwaitAnswer();
        // All four licenses get a section, empty slots and ones without changes included.
        var sections = window.FindControl<StackPanel>("HistoryList")!.Children.Cast<StackPanel>().ToList();
        Assert.Equal(4, sections.Count);
        Assert.Contains(sections[1].GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Empty");
        Assert.Contains(sections[1].GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "No changes");
        var rows = sections
            .SelectMany(section => section.Children.OfType<Border>().Where(row => row.Classes.Contains("HistoryRow")))
            .ToList();
        Assert.Equal(3, rows.Count);
        var restores = rows.Select(row => row.GetVisualDescendants().OfType<ActionButton>().SingleOrDefault()).ToList();

        // License 1's undo: 4939... is not on it now, so it can come back. Its first change: 0310... is the code it has now.
        // License 4's import: nothing came before it.
        Assert.NotNull(restores[0]);
        Assert.Null(restores[1]);
        Assert.Contains(rows[1].GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Current");
        Assert.Null(restores[2]);

        restores[0]!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Same(history[0], await answer);
    }
}
