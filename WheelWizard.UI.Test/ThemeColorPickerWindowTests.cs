using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using WheelWizard.Views.Dialogs;

namespace WheelWizard.UI.Test;

public class ThemeColorPickerWindowTests
{
    [AvaloniaFact]
    public void Picker_ShowsCurrentAndNewColorAndQuickSwatches()
    {
        var owner = new ModalOwnerWindow();
        owner.Show();
        var content = new ThemeColorPickerWindow("Main color", Color.Parse("#112233"));
        content.Show();
        var window = Assert.IsAssignableFrom<Window>(TopLevel.GetTopLevel(content));
        Assert.Same(owner, window);
        try
        {
            window.UpdateLayout();

            var texts = content.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Current · #112233", texts);
            Assert.Contains("New · #112233", texts);

            var swatches = content.GetVisualDescendants().OfType<UniformGrid>().Single(g => g.Name == "SwatchRow");
            Assert.Equal(12, swatches.Children.Count);

            var swatch = (Control)swatches.Children[3];
            var center = swatch.TranslatePoint(new Avalonia.Point(swatch.Bounds.Width / 2, swatch.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, Avalonia.Input.MouseButton.Left);
            window.MouseUp(center, Avalonia.Input.MouseButton.Left);
            window.UpdateLayout();

            var after = content.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("New · #EF4444", after);
            Assert.Contains("Current · #112233", after);
        }
        finally
        {
            owner.Close();
        }
    }
}
