using Avalonia.Threading;
using WheelWizard.Views.Dialogs.Base;

namespace WheelWizard.Views.Dialogs;

/// <summary>Pick how the room list is ordered with one tap. Resolves to the sort number, or null when dismissed.</summary>
public partial class RoomSortWindow : PopupContent
{
    private TaskCompletionSource<int?>? _tcs;

    public RoomSortWindow(int current)
        : base(true, false, true, "Sort rooms")
    {
        InitializeComponent();
        IsDismissible = true;
        AddChips(OrderChips, current, (0, "Default"), (1, "Lowest VR first"), (2, "Highest VR first"));
        AddChips(SingleChips, current, (3, "Lowest only"), (4, "Highest only"));
    }

    private void AddChips(Avalonia.Controls.WrapPanel panel, int current, params (int Value, string Label)[] items)
    {
        foreach (var (value, label) in items)
        {
            var chip = new Avalonia.Controls.Button { Content = label };
            chip.Classes.Add("Chip");
            chip.Classes.Set("Active", value == current);
            chip.Click += (_, _) => Submit(value);
            panel.Children.Add(chip);
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

    private void Submit(int value)
    {
        _tcs?.TrySetResult(value);
        Close();
    }
}
