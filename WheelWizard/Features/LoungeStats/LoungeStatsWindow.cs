using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using WheelWizard.RrLounge;

namespace WheelWizard.LoungeStats;

/// <summary>
/// The RR Lounge stats in a small window of their own, for a second monitor or a stream scene that captures a window,
/// next to the OBS browser source. No frame, always on top unless switched off; drag it to move it, right-click for its
/// options. With the blurred background it blurs what is behind it, where the system allows.
/// </summary>
public sealed class LoungeStatsWindow : Window
{
    private static LoungeStatsWindow? _open;

    /// <summary>The window that is open, if any.</summary>
    public static LoungeStatsWindow? Current => _open;

    /// <summary>Raised when the window opens or closes, so a button can say "Open" or "Close".</summary>
    public static event Action? OpenChanged;

    public static void CloseIfOpen() => _open?.Close();

    public static void ShowOrActivate(LoungeStatsService service, LoungeStatsConfig config, RrLoungeImages? images)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }

        _open = new LoungeStatsWindow(service, config, images);
        _open.Show();
        OpenChanged?.Invoke();
    }

    private readonly LoungeStatsService _service;
    private readonly LoungeStatsConfig _config;
    private readonly LoungeStatsCard _card;
    private IDisposable? _lease;

    private LoungeStatsWindow(LoungeStatsService service, LoungeStatsConfig config, RrLoungeImages? images)
    {
        _service = service;
        _config = config;
        _card = new LoungeStatsCard(images);

        Title = "RR Lounge stats";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _card.Cursor = new Cursor(StandardCursorType.SizeAll);
        Content = _card;
        ContextMenu = BuildMenu();
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };

        Opened += (_, _) =>
        {
            _lease = _service.Use();
            _service.Changed += OnChanged;
            _config.Changed += OnChanged;
            Refresh();
        };
        Closed += (_, _) =>
        {
            _service.Changed -= OnChanged;
            _config.Changed -= OnChanged;
            _lease?.Dispose();
            _open = null;
            OpenChanged?.Invoke();
        };
    }

    private ContextMenu BuildMenu()
    {
        var onTop = new MenuItem { Header = "Always on top  ✓" };
        onTop.Click += (_, _) =>
        {
            Topmost = !Topmost;
            onTop.Header = Topmost ? "Always on top  ✓" : "Always on top";
        };
        var refresh = new MenuItem { Header = "Refresh now" };
        refresh.Click += (_, _) => _ = _service.RefreshNowAsync();
        var close = new MenuItem { Header = "Close" };
        close.Click += (_, _) => Close();
        return new ContextMenu { Items = { onTop, refresh, new Separator(), close } };
    }

    private void OnChanged() => Dispatcher.UIThread.Post(Refresh);

    /// <summary>Draws the current stats in the current look.</summary>
    public void Refresh()
    {
        // A blurred background blurs the desktop behind the window; the others only need a see-through window.
        TransparencyLevelHint =
            _config.GetString("background.mode") == "blurred"
                ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent]
                : [WindowTransparencyLevel.Transparent];
        _card.Show(_service.Current, _config, _service.LastChange, _service.ChangeNumber);
        // Nothing to show (hidden, or no stats yet) still leaves a small handle to drag or right-click.
        MinWidth = _card.Child is null ? 40 : 0;
        MinHeight = _card.Child is null ? 20 : 0;
    }
}
