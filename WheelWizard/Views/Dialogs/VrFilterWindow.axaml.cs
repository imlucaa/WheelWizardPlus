using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WheelWizard.Views.Dialogs.Base;

namespace WheelWizard.Views.Dialogs;

public sealed record VrRange(int? Min, int? Max);

/// <summary>Pick an average VR range with one tap on a chip, or type your own. Resolves to the range, or null when dismissed.</summary>
public partial class VrFilterWindow : PopupContent
{
    private static readonly (string Label, int? Min, int? Max)[] Presets =
    [
        ("Any", null, null),
        ("Under 10k", null, 9_999),
        ("10k to 25k", 10_000, 25_000),
        ("25k to 50k", 25_000, 50_000),
        ("Over 50k", 50_001, null),
    ];

    private TaskCompletionSource<VrRange?>? _tcs;
    private bool _syncing;

    public VrFilterWindow(int? min, int? max)
        : base(true, false, true, "Filter rooms")
    {
        InitializeComponent();
        IsDismissible = true;
        foreach (var (label, presetMin, presetMax) in Presets)
        {
            var chip = new Avalonia.Controls.Button { Content = label, Tag = new VrRange(presetMin, presetMax) };
            chip.Classes.Add("Chip");
            chip.Click += (_, _) => SetRange(presetMin, presetMax);
            Chips.Children.Add(chip);
        }

        SetRange(min, max);
    }

    private void SetRange(int? min, int? max)
    {
        _syncing = true;
        MinInput.Text = min?.ToString() ?? string.Empty;
        MaxInput.Text = max?.ToString() ?? string.Empty;
        _syncing = false;
        HighlightChip();
    }

    private void HighlightChip()
    {
        var current = ReadRange();
        foreach (var chip in Chips.Children.OfType<Avalonia.Controls.Button>())
            chip.Classes.Set("Active", chip.Tag is VrRange range && range == current);
    }

    private VrRange ReadRange() => new(Parse(MinInput.Text), Parse(MaxInput.Text));

    private static int? Parse(string? text) => int.TryParse(text?.Replace(",", string.Empty), out var value) && value >= 0 ? value : null;

    private void Range_OnChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_syncing)
            HighlightChip();
    }

    private void Reset_OnClick(object? sender, RoutedEventArgs e) => Submit(new VrRange(null, null));

    private void Apply_OnClick(object? sender, RoutedEventArgs e) => Submit(ReadRange());

    private void Submit(VrRange range)
    {
        _tcs?.TrySetResult(range);
        Close();
    }

    public async Task<VrRange?> AwaitAnswer()
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(AwaitAnswer);

        _tcs = new();
        Show();
        return await _tcs.Task;
    }

    protected override void BeforeClose() => _tcs?.TrySetResult(null);
}
