using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using WheelWizard.Views.Components;
using WheelWizard.Views.Dialogs.Base;
using Button = WheelWizard.Views.Components.Button;

namespace WheelWizard.Views.Dialogs;

public partial class ThemeLibraryWindow : PopupContent
{
    private TaskCompletionSource<int?>? _tcs;

    public ThemeLibraryWindow(IReadOnlyList<string> themeNames, IReadOnlyList<string[]>? swatches = null)
        : base(true, false, true, "Theme library")
    {
        InitializeComponent();
        IsDismissible = true;

        var labelColor = Application.Current!.FindResource("FormFieldLabelColor") is Color color ? color : Colors.White;
        for (var index = 0; index < themeNames.Count; index++)
        {
            var selectedIndex = index;
            var raw = themeNames[index];
            var active = raw.Contains("Active", StringComparison.OrdinalIgnoreCase) && raw.Contains('✓');
            var builtIn = raw.Contains("(built-in)", StringComparison.OrdinalIgnoreCase);
            var name = raw.Replace("✓", "").Replace("— Active", "").Replace("(built-in)", "", StringComparison.OrdinalIgnoreCase).Trim();

            var label = new TextBlock
            {
                Text = name,
                FontWeight = active ? FontWeight.Bold : FontWeight.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.Foreground = new SolidColorBrush(labelColor);

            var tag = new TextBlock
            {
                Text =
                    active ? "Active"
                    : builtIn ? "Built-in"
                    : "Saved",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
            };
            tag.Foreground = new SolidColorBrush(labelColor, active ? 1.0 : 0.55);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(tag, 1);
            Control left = label;
            if (swatches is not null && index < swatches.Count && swatches[index].Length > 0)
            {
                var colors = swatches[index];
                var strip = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                };
                for (var i = 0; i < colors.Length; i++)
                {
                    if (Color.TryParse(colors[i], out var stop))
                        strip.GradientStops.Add(new GradientStop(stop, colors.Length == 1 ? 0 : i / (double)(colors.Length - 1)));
                }

                left = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new Border
                        {
                            Width = 36,
                            Height = 16,
                            CornerRadius = new CornerRadius(8),
                            Background = strip,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        label,
                    },
                };
            }

            row.Children.Add(left);
            row.Children.Add(tag);

            var button = new Avalonia.Controls.Button { Content = row };
            button.Classes.Add("ThemeRow");
            if (active)
                button.Classes.Add("Active");
            button.Click += (_, _) => SubmitSelection(selectedIndex);
            ThemeList.Children.Add(button);
        }
    }

    public async Task<int?> AwaitAnswer()
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(AwaitAnswer);

        _tcs = new();
        Show();
        return await _tcs.Task;
    }

    protected override void BeforeClose() => _tcs?.TrySetResult(null);

    private void SubmitSelection(int index)
    {
        _tcs?.TrySetResult(index);
        Close();
    }
}
