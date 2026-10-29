using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace WheelWizard.NowPlaying;

/// <summary>
/// The song in a small window of its own, for a second monitor or a stream scene that captures a window, next to the OBS
/// browser source. No frame, always on top unless switched off; drag it to move it, right-click for its options. It fades
/// out when nothing plays, if that option is on.
/// </summary>
public sealed class NowPlayingWindow : Window
{
    private static NowPlayingWindow? _open;

    /// <summary>The window that is open, if any.</summary>
    public static NowPlayingWindow? Current => _open;

    /// <summary>Raised when the window opens or closes, so a button can say "Open" or "Close".</summary>
    public static event Action? OpenChanged;

    public static void CloseIfOpen() => _open?.Close();

    public static void ShowOrActivate(NowPlayingService service, NowPlayingConfig config)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }

        _open = new NowPlayingWindow(service, config);
        _open.Show();
        OpenChanged?.Invoke();
    }

    private readonly NowPlayingService _service;
    private readonly NowPlayingConfig _config;
    private readonly NowPlayingCard _card = new();
    private IDisposable? _lease;

    private NowPlayingWindow(NowPlayingService service, NowPlayingConfig config)
    {
        _service = service;
        _config = config;

        Title = "Now playing";
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
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
        var close = new MenuItem { Header = "Close" };
        close.Click += (_, _) => Close();
        return new ContextMenu { Items = { onTop, new Separator(), close } };
    }

    private void OnChanged() => Dispatcher.UIThread.Post(Refresh);

    /// <summary>Draws the current song in the current look.</summary>
    public void Refresh() => _card.Show(_service.Current, _config);
}
