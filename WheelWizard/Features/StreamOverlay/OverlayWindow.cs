using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WheelWizard.RrRooms;
using WheelWizard.Shared.Services;
using WheelWizard.WiiManagement.MiiManagement;
using WheelWizard.WiiManagement.MiiManagement.Domain.Mii;

namespace WheelWizard.StreamOverlay;

/// <summary>
/// The VR overlay in a small window of its own, for a second monitor or a stream scene that captures a window, next to
/// the OBS browser source. It is the same card as the preview, keeps your VR and rank up to date, plays the VR change
/// when it moves, and stays on top of other windows unless that is switched off. Drag it to move it; right-click for
/// its options.
/// </summary>
public sealed class OverlayWindow : Window
{
    private static OverlayWindow? _open;

    /// <summary>The overlay window that is open, if any.</summary>
    public static OverlayWindow? Current => _open;

    /// <summary>Raised when the window opens or closes, so a button can say "Open" or "Close".</summary>
    public static event Action? OpenChanged;

    /// <summary>Closes the window when it is open.</summary>
    public static void CloseIfOpen() => _open?.Close();

    private readonly OverlayConfig _config;
    private readonly IApiCaller<IRwfcApi> _api;
    private readonly OverlayLive _live;
    private readonly OverlayBackground _background;
    private readonly OverlayPreview _preview;
    private readonly IRoomPresence? _presence;
    private readonly DispatcherTimer _poll = new();
    private string _loadedImage = string.Empty;
    private int? _lastVr;
    private string _shownCode = string.Empty;
    private bool _loading;

    /// <summary>Opens the overlay window, or brings it forward when it is already open.</summary>
    public static void ShowOrActivate(
        OverlayConfig config,
        IApiCaller<IRwfcApi> api,
        OverlayLive live,
        OverlayBackground background,
        IRoomPresence? presence = null
    )
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }

        _open = new OverlayWindow(config, api, live, background, presence);
        _open.Show();
        OpenChanged?.Invoke();
    }

    private OverlayWindow(
        OverlayConfig config,
        IApiCaller<IRwfcApi> api,
        OverlayLive live,
        OverlayBackground background,
        IRoomPresence? presence
    )
    {
        _presence = presence;
        _config = config;
        _api = api;
        _live = live;
        _background = background;

        Title = "VR overlay";
        // No title bar or frame: only the card shows, on a see-through window, like the overlay in OBS.
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _preview = new OverlayPreview(config, "Player") { Width = 440, Cursor = new Cursor(StandardCursorType.SizeAll) };
        Content = _preview;
        ContextMenu = BuildMenu();
        ToolTip.SetTip(_preview, "Drag to move. Right-click for options.");
        ToolTip.SetShowDelay(_preview, 800);

        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };

        _config.Changed += OnConfigChanged;
        _poll.Tick += async (_, _) => await RefreshPlayerAsync();
        Opened += async (_, _) =>
        {
            ApplyPollInterval();
            _poll.Start();
            await LoadImageAsync();
            await RefreshPlayerAsync();
        };
        Closed += (_, _) =>
        {
            _poll.Stop();
            _config.Changed -= OnConfigChanged;
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
        var replay = new MenuItem { Header = "Play the animation" };
        replay.Click += (_, _) => _preview.Replay();
        var close = new MenuItem { Header = "Close" };
        close.Click += (_, _) => Close();
        return new ContextMenu { Items = { onTop, replay, new Separator(), close } };
    }

    private async void OnConfigChanged()
    {
        ApplyPollInterval();
        await LoadImageAsync();
    }

    // The same refresh rate as the OBS overlay (its "poll seconds"), never faster than every 5 seconds.
    private void ApplyPollInterval() => _poll.Interval = TimeSpan.FromSeconds(Math.Clamp(_config.GetNumber("data.pollSeconds"), 5, 600));

    private async Task LoadImageAsync()
    {
        var url = _config.GetString("background.imageUrl");
        if (url == _loadedImage)
            return;
        _loadedImage = url;
        var bitmap = await _background.LoadPreviewImageAsync(url);
        if (url == _loadedImage)
            _preview.SetBackgroundImage(bitmap);
    }

    /// <summary>Reads the player's current name, VR and rank; a VR that moved plays the change like the OBS overlay.</summary>
    private async Task RefreshPlayerAsync()
    {
        if (_loading)
            return;
        var digits = _live.FriendCodeDigits();
        if (digits.Length != 12)
            return;

        _loading = true;
        try
        {
            var code = $"{digits[..4]}-{digits.Substring(4, 4)}-{digits.Substring(8, 4)}";
            var result = await _api.CallApiAsync(api => api.GetPlayerProfileAsync(code));
            if (result.IsFailure || result.Value is null)
                return;

            Mii? mii = null;
            if (
                !string.IsNullOrWhiteSpace(result.Value.MiiData)
                && MiiSerializer.Deserialize(result.Value.MiiData) is { IsSuccess: true } parsed
            )
                mii = parsed.Value;

            // Another license picked in Settings is another player: start the change from zero.
            var samePlayer = code == _shownCode;
            if (!samePlayer)
            {
                _shownCode = code;
                _lastVr = null;
            }

            var vr = OverlayLive.CurrentVr(_presence, code, result.Value.Vr, await OverlayLive.RoomVrAsync(_api, code));
            var moved = _lastVr is { } before && vr != before;
            _lastVr = vr;
            _preview.SetPlayer(result.Value.Name, vr, result.Value.Rank, mii, samePlayer);
            if (moved)
                _preview.Replay();
        }
        finally
        {
            _loading = false;
        }
    }
}
