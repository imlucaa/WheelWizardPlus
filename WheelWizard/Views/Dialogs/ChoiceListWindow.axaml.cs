using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WheelWizard.Views.Dialogs.Base;

namespace WheelWizard.Views.Dialogs;

/// <param name="Label">Main text of the row.</param>
/// <param name="Tag">Small muted text on the right of the row.</param>
/// <param name="Highlight">Draws the row bold and slightly lighter, e.g. for a detected default.</param>
/// <param name="Swatches">Optional colors drawn as a small strip before the label, to show what a look is like.</param>
/// <param name="Detail">Optional small muted text under the label, for example what is inside the item.</param>
public sealed record ChoiceOption(
    string Label,
    string Tag,
    bool Highlight = false,
    IReadOnlyList<string>? Swatches = null,
    string? Detail = null
);

/// <summary>A flat list popup that resolves to the index of the chosen option, or null when dismissed.</summary>
public partial class ChoiceListWindow : PopupContent
{
    private TaskCompletionSource<int?>? _tcs;

    public ChoiceListWindow(string title, string heading, string description, IReadOnlyList<ChoiceOption> options)
        : base(true, false, true, title)
    {
        InitializeComponent();
        IsDismissible = true;
        HeadingText.Text = heading;
        DescriptionText.Text = description;

        var labelColor = Application.Current!.FindResource("FormFieldLabelColor") is Color color ? color : Colors.White;
        for (var index = 0; index < options.Count; index++)
        {
            var selectedIndex = index;
            var option = options[index];
            var label = new TextBlock
            {
                Text = option.Label,
                FontWeight = option.Highlight ? FontWeight.Bold : FontWeight.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(labelColor),
            };
            var tag = new TextBlock
            {
                Text = option.Tag,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(labelColor, option.Highlight ? 1.0 : 0.55),
            };
            Grid.SetColumn(tag, 1);

            Control left = label;
            if (option.Swatches is { Count: > 0 } colors)
            {
                var strip = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                };
                for (var i = 0; i < colors.Count; i++)
                {
                    if (Color.TryParse(colors[i], out var stop))
                        strip.GradientStops.Add(new GradientStop(stop, colors.Count == 1 ? 0 : i / (double)(colors.Count - 1)));
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

            if (!string.IsNullOrWhiteSpace(option.Detail))
            {
                // One item per line, laid out two to a row so they line up.
                var items = new UniformGrid { Columns = 2, HorizontalAlignment = HorizontalAlignment.Left };
                foreach (var line in option.Detail.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    items.Children.Add(
                        new TextBlock
                        {
                            Text = line,
                            FontSize = 12,
                            Margin = new Thickness(0, 1, 18, 1),
                            Opacity = 0.65,
                            Foreground = new SolidColorBrush(labelColor),
                        }
                    );
                }

                left = new StackPanel
                {
                    Spacing = 5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 8),
                    Children = { left, items },
                };
            }

            var button = new Avalonia.Controls.Button
            {
                Content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { left, tag } },
            };
            button.Classes.Add("ChoiceRow");
            if (option.Highlight)
                button.Classes.Add("Active");
            button.Click += (_, _) => SubmitSelection(selectedIndex);
            ChoiceList.Children.Add(button);
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
