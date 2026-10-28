using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using WheelWizard.Views.Dialogs.Base;

namespace WheelWizard.Views.Dialogs;

public partial class ThemeColorPickerWindow : PopupContent
{
    private TaskCompletionSource<Color?>? _completion;
    private Color _selectedColor;
    private bool _isUpdatingHex;

    public ThemeColorPickerWindow(string title, Color initialColor)
        : base(true, false, true, "Theme color")
    {
        InitializeComponent();
        IsDismissible = true;
        PickerTitle.Text = title;
        _selectedColor = initialColor;
        Picker.Color = initialColor;
        HexTextBox.Text = ToHex(initialColor);
        OldSwatch.Background = new SolidColorBrush(initialColor);
        OldHexText.Text = $"Current · {ToHex(initialColor)}";
        BuildSwatches();
        UpdatePreview();
    }

    private static readonly string[] QuickColors =
    [
        "#FFFFFF",
        "#A3A3A3",
        "#000000",
        "#EF4444",
        "#F97316",
        "#EAB308",
        "#22C55E",
        "#14B8A6",
        "#3B82F6",
        "#6366F1",
        "#A855F7",
        "#EC4899",
    ];

    private void BuildSwatches()
    {
        foreach (var hex in QuickColors)
        {
            var color = Color.Parse(hex);
            var swatch = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new Avalonia.CornerRadius(12),
                BorderThickness = new Avalonia.Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                Background = new SolidColorBrush(color),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            ToolTip.SetTip(swatch, hex);
            swatch.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left)
                    Picker.Color = color;
            };
            SwatchRow.Children.Add(swatch);
        }
    }

    private void UpdatePreview()
    {
        NewSwatch.Background = new SolidColorBrush(_selectedColor);
        NewHexText.Text = $"New · {ToHex(_selectedColor)}";
    }

    public async Task<Color?> AwaitColor()
    {
        _completion = new();
        Show();
        return await _completion.Task;
    }

    private void Picker_OnColorChanged(object? sender, ColorChangedEventArgs e)
    {
        _selectedColor = e.NewColor;
        _isUpdatingHex = true;
        HexTextBox.Text = ToHex(_selectedColor);
        _isUpdatingHex = false;
        UpdatePreview();
    }

    private void HexTextBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isUpdatingHex)
            return;

        var text = HexTextBox.Text?.Trim() ?? string.Empty;
        if (text.Length == 6 && !text.StartsWith('#'))
            text = $"#{text}";
        if (!WheelWizard.Themes.LauncherThemeService.IsValidHexColor(text))
            return;

        _selectedColor = Color.Parse(text);
        Picker.Color = _selectedColor;
    }

    private void UseColor_OnClick(object? sender, RoutedEventArgs e)
    {
        _completion?.TrySetResult(_selectedColor);
        Close();
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close();

    protected override void BeforeClose() => _completion?.TrySetResult(null);

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
