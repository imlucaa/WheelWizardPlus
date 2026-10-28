using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WheelWizard.Views.Components;
using WheelWizard.Views.Dialogs.Base;
using WheelWizard.WiiManagement.GameLicense;

namespace WheelWizard.Views.Dialogs;

/// <summary>
/// Every friend code change on the save, newest first. Resolves to the change whose old code should be put back, or null
/// when closed.
/// </summary>
public partial class FriendCodeHistoryWindow : PopupContent
{
    private const int Slots = 4;
    private TaskCompletionSource<FriendCodeChange?>? _tcs;

    public FriendCodeHistoryWindow(IReadOnlyList<FriendCodeChange> history, IReadOnlyList<LicenseIdentity> licenses)
        : base(true, false, true, "Friend code history")
    {
        InitializeComponent();
        var labelColor = Application.Current!.FindResource("FormFieldLabelColor") is Color color ? color : Colors.White;

        // One section per license slot, all four, each with its own changes newest first.
        for (var slot = 0; slot < Slots; slot++)
        {
            var now = licenses.FirstOrDefault(l => l.Slot == slot);
            var changes = history.Where(change => change.Slot == slot).ToList();
            var section = new StackPanel { Spacing = 0 };
            section.Children.Add(Header(slot, now, changes, labelColor));
            if (changes.Count == 0)
            {
                section.Children.Add(
                    new TextBlock
                    {
                        Text = "No changes",
                        FontSize = 12,
                        Margin = new Thickness(2, 2, 2, 0),
                        Foreground = new SolidColorBrush(labelColor, 0.4),
                    }
                );
            }

            for (var i = 0; i < changes.Count; i++)
                section.Children.Add(Row(changes[i], now, labelColor, last: i == changes.Count - 1));
            HistoryList.Children.Add(section);
        }
    }

    private static Control Header(int slot, LicenseIdentity? now, IReadOnlyList<FriendCodeChange> changes, Color labelColor)
    {
        // A license without a Mii name goes by its slot; one that is gone keeps the name it had in the history.
        var name =
            now is not null && !string.IsNullOrWhiteSpace(now.Name) && !now.Name.Equals("no name", StringComparison.OrdinalIgnoreCase)
                ? now.Name
                : changes.FirstOrDefault(change => change.Name.Length > 0)?.Name ?? $"License {slot + 1}";
        var code = new TextBlock
        {
            Text = now?.FriendCode ?? "Empty",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(labelColor, 0.5),
        };
        Grid.SetColumn(code, 1);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
            Margin = new Thickness(2, 0, 2, 2),
            Children =
            {
                new TextBlock
                {
                    Text = name,
                    FontWeight = FontWeight.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(labelColor),
                },
                code,
            },
        };
    }

    public static string KindText(string kind) =>
        kind switch
        {
            "change" => "New friend code",
            "restore-change" => "Restored",
            "undo" => "Undo",
            "before-restore" => "Backup restored",
            "before-import" => "Save imported",
            "before-license-import" => "License imported",
            _ => "Changed",
        };

    private Control Row(FriendCodeChange change, LicenseIdentity? now, Color labelColor, bool last)
    {
        var codes =
            change.OldCode.Length == 0 ? $"Added as {change.NewCode}"
            : change.NewCode.Length == 0 ? $"{change.OldCode} removed"
            : $"{change.OldCode}  →  {change.NewCode}";

        var text = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = codes,
                    FontWeight = FontWeight.Medium,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(labelColor),
                },
                new TextBlock
                {
                    Text = $"{change.Time:d MMM, HH:mm} · {KindText(change.Kind)}",
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(labelColor, 0.5),
                },
            },
        };

        // The old code can come back only onto a license that is still there, and only when it is not already on it.
        Control? side = null;
        if (now is not null && change.OldCode.Length > 0 && now.FriendCode == change.OldCode)
        {
            side = new TextBlock
            {
                Text = "Current",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(labelColor, 0.55),
            };
        }
        else if (now is not null && change.OldCode.Length > 0)
        {
            var restore = new ActionButton
            {
                Text = "Restore",
                Size = ButtonSize.Compact,
                Tone = ActionButtonTone.Secondary,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(restore, $"Put back {change.OldCode}");
            restore.Click += (_, _) => Submit(change);
            side = restore;
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(text);
        if (side is not null)
        {
            Grid.SetColumn(side, 1);
            grid.Children.Add(side);
        }

        // Plain rows split by a faint line; no cards.
        var row = new Border
        {
            BorderBrush = new SolidColorBrush(labelColor, 0.08),
            BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1),
            Padding = new Thickness(2, 8),
            Child = grid,
        };
        row.Classes.Add("HistoryRow");
        return row;
    }

    public async Task<FriendCodeChange?> AwaitAnswer()
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(AwaitAnswer);

        _tcs = new();
        Show();
        return await _tcs.Task;
    }

    protected override void BeforeClose() => _tcs?.TrySetResult(null);

    private void Submit(FriendCodeChange change)
    {
        _tcs?.TrySetResult(change);
        Close();
    }
}
