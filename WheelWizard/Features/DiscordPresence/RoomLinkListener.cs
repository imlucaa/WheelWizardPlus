using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WheelWizard.Models.RRInfo;
using WheelWizard.RrRooms;
using WheelWizard.Views.Shell.Navigation;

namespace WheelWizard.DiscordPresence;

/// <summary>
/// Lets the "View Room" button on a Discord profile open the room inside Wheel Wizard. Discord buttons only open web
/// links, so the button points at this small listener on the viewer's own PC, which brings the launcher to the front.
/// </summary>
public sealed class RoomLinkListener(
    IServiceProvider services,
    LiveRoomsService rooms,
    WheelWizard.StreamOverlay.OverlayLive overlay,
    WheelWizard.StreamOverlay.OverlayBackground background,
    WheelWizard.NowPlaying.NowPlayingService nowPlaying,
    WheelWizard.NowPlaying.NowPlayingConfig nowPlayingConfig,
    ILogger<RoomLinkListener> logger
)
{
    public const int Port = 47831;
    private static readonly string[] Page =
    [
        "<!doctype html><meta charset=utf-8><title>Wheel Wizard</title>",
        "<body style=\"font-family:sans-serif;background:#1b1b1f;color:#eee;text-align:center;padding-top:80px\">",
        "<h2>Opening the room in Wheel Wizard</h2><p>You can close this tab.</p>",
    ];

    private HttpListener? _listener;

    /// <summary>The link on the Discord button; the friend code lets the room be found if its id has changed.</summary>
    public static string BuildUrl(string roomId, string? friendCode = null)
    {
        var url = $"http://127.0.0.1:{Port}/room/{Uri.EscapeDataString(roomId)}";
        return string.IsNullOrEmpty(friendCode) ? url : $"{url}?fc={Uri.EscapeDataString(friendCode)}";
    }

    public static bool IsValidRoomId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public void Start()
    {
        if (_listener != null)
            return;

        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            _listener = listener;
            _ = Task.Run(() => ListenAsync(listener));
        }
        catch (Exception exception)
        {
            // Another copy of the launcher already owns the port; the button then opens in that one.
            logger.LogDebug(exception, "Room links are not available");
        }
    }

    private async Task ListenAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception exception) when (!listener.IsListening)
            {
                logger.LogDebug(exception, "Room link listener stopped");
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not accept a room link request");
                continue;
            }

            // Each request runs on its own, so a room link that waits for a room refresh never holds up the OBS
            // overlay, which asks for its state every few seconds.
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "";

            if (path.StartsWith("/rewind-overlay", StringComparison.Ordinal) || path == "/overlay/bg")
            {
                await ServeOverlayFileAsync(context, path);
                return;
            }

            if (path.StartsWith("/now-playing", StringComparison.Ordinal))
            {
                await ServeNowPlayingAsync(context, path);
                return;
            }

            if (path is "/overlay" or "/overlay/state")
            {
                await ServeOverlayAsync(context, path);
                return;
            }

            // Browsers also ask for /favicon.ico and the like; those must not open anything.
            if (!path.StartsWith("/room/", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            var roomId = Uri.UnescapeDataString(path[6..]);

            var friendCode = new string((context.Request.QueryString["fc"] ?? "").Where(char.IsAsciiDigit).ToArray());

            var bytes = Encoding.UTF8.GetBytes(string.Concat(Page));
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();

            var id = IsValidRoomId(roomId) ? roomId : null;
            // A room made since the last refresh is not in the launcher's list yet, so fetch it fresh first.
            if (id != null && FindRoom(id, friendCode) == null)
            {
                try
                {
                    await rooms.RefreshNowAsync();
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Could not refresh rooms for a room link");
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() => OpenRoom(id, friendCode));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not open a room link");
        }
    }

    private async Task ServeOverlayFileAsync(HttpListenerContext context, string path)
    {
        (byte[] Bytes, string ContentType)? file;
        if (path == "/overlay/bg")
        {
            file = background.Read();
        }
        else
        {
            var name = path.TrimEnd('/') == "/rewind-overlay" ? "index.html" : path["/rewind-overlay/".Length..].Replace("assets/", "");
            file = name.Contains('/') || name.Contains("..") ? null : WheelWizard.StreamOverlay.OverlayAssets.Get(name);
        }

        if (file is not { } found)
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        context.Response.ContentType = found.ContentType;
        context.Response.Headers["Cache-Control"] = path == "/overlay/bg" ? "no-cache" : "max-age=3600";
        context.Response.ContentLength64 = found.Bytes.Length;
        await context.Response.OutputStream.WriteAsync(found.Bytes);
        context.Response.Close();
    }

    // The page for OBS asks every second, which keeps the media reader running for as long as the page is open.
    private async Task ServeNowPlayingAsync(HttpListenerContext context, string path)
    {
        nowPlaying.Touch();
        byte[] bytes;
        string type;
        switch (path.TrimEnd('/'))
        {
            case "/now-playing":
                bytes = Encoding.UTF8.GetBytes(WheelWizard.NowPlaying.NowPlayingPage.Html);
                type = "text/html; charset=utf-8";
                break;
            case "/now-playing/state":
                bytes = Encoding.UTF8.GetBytes(WheelWizard.NowPlaying.NowPlayingPage.StateFor(nowPlaying, nowPlayingConfig));
                type = "application/json";
                break;
            case "/now-playing/cover" when nowPlaying.Current?.Cover is { Length: > 0 } cover:
                bytes = cover;
                type = "image/jpeg";
                break;
            default:
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
        }

        context.Response.ContentType = type;
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task ServeOverlayAsync(HttpListenerContext context, string path)
    {
        var state = path == "/overlay/state";
        var body = state
            ? System.Text.Json.JsonSerializer.Serialize(new { url = overlay.CurrentUrl() })
            : WheelWizard.StreamOverlay.OverlayLive.Html;
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = state ? "application/json" : "text/html; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    // The room by its id, or failing that the room the player with this friend code is in.
    private RrRoom? FindRoom(string? roomId, string friendCode) =>
        rooms.CurrentRooms.FirstOrDefault(r => r.Id == roomId)
        ?? (
            friendCode.Length == 0
                ? null
                : rooms.CurrentRooms.FirstOrDefault(r =>
                    r.Players.Any(p => new string((p.FriendCode ?? "").Where(char.IsAsciiDigit).ToArray()) == friendCode)
                )
        );

    private void OpenRoom(string? roomId, string friendCode)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
        }

        var navigation = services.GetRequiredService<INavigationService>();
        var room = FindRoom(roomId, friendCode);
        logger.LogInformation("Room link {RoomId}: {Found} of {Count} rooms known", roomId, room != null, rooms.CurrentRooms.Count);
        if (room != null)
            navigation.NavigateTo<WheelWizard.RrRooms.Views.RoomDetailsPage>(room);
        else
            navigation.NavigateTo<WheelWizard.RrRooms.Views.RoomsPage>();
    }
}
