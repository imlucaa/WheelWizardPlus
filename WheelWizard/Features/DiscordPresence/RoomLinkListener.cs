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
    WheelWizard.LoungeStats.LoungeStatsService loungeStats,
    WheelWizard.LoungeStats.LoungeStatsConfig loungeStatsConfig,
    WheelWizard.RrLounge.RrLoungeImages loungeImages,
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

            if (
                path.StartsWith(WheelWizard.StreamOverlay.HudDigits.GlyphPath, StringComparison.Ordinal)
                || path.StartsWith(WheelWizard.StreamOverlay.HudDigits.RankPath, StringComparison.Ordinal)
                || path == WheelWizard.StreamOverlay.HudDigits.ScriptPath
            )
            {
                await ServeHudDigitsAsync(context, path);
                return;
            }

            if (path.StartsWith("/rewind-overlay", StringComparison.Ordinal) || path == "/overlay/bg")
            {
                await ServeOverlayFileAsync(context, path);
                return;
            }

            if (path.StartsWith("/overlay-mmr/", StringComparison.Ordinal))
            {
                await ServeOverlayMmrAsync(context, path);
                return;
            }

            if (path.StartsWith("/now-playing", StringComparison.Ordinal))
            {
                await ServeNowPlayingAsync(context, path);
                return;
            }

            if (path.StartsWith("/lounge-stats", StringComparison.Ordinal))
            {
                await ServeLoungeStatsAsync(context, path);
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
            // "mmr-" names the page script with its "VR" labels written as "MMR", for the MMR badge.
            var mmrScript = name.StartsWith("mmr-", StringComparison.Ordinal);
            var real = mmrScript ? name[4..] : name;
            file = real.Contains('/') || real.Contains("..") ? null : WheelWizard.StreamOverlay.OverlayAssets.Get(real);
            if (mmrScript && file is { } script)
            {
                var text = Encoding.UTF8.GetString(script.Bytes).Replace("children:`VR`", "children:`MMR`").Replace("` VR`", "` MMR`");
                file = (Encoding.UTF8.GetBytes(text), script.ContentType);
            }
        }

        if (file is not { } found)
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        var bytes = found.Bytes;
        var isPage = found.ContentType.StartsWith("text/html");
        if (isPage)
        {
            // The colours chosen in Wheel Wizard that the page has fixed (VR number, gains, losses, rank).
            var settings = services.GetRequiredService<WheelWizard.Settings.ISettingsManager>();
            var css = new WheelWizard.StreamOverlay.OverlayConfig(settings).ColorCss();
            var html = Encoding.UTF8.GetString(bytes);
            var end = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            html = end >= 0 ? html.Insert(end, $"<style>{css}</style>") : html + $"<style>{css}</style>";
            if (context.Request.QueryString["card"] == "off")
                html = html.Replace("</head>", $"<style>{WheelWizard.StreamOverlay.HudDigits.NoCardCss}</style></head>");
            // MMR with the flag on: the RR Lounge country's flag before the name, as its background so the name keeps its
            // place (the race HUD script starts the name after it).
            if (context.Request.QueryString["cc"] is { Length: 2 } cc && cc.All(char.IsAsciiLetterLower))
                html = html.Replace(
                    "</head>",
                    $"<style>.player-name{{padding-left:1.55em;background:url(/overlay-mmr/flag?cc={cc}) left center/1.3em .95em no-repeat}}</style></head>"
                );
            var hudDigits = context.Request.QueryString["digits"] == "hud";
            var rankBadge = context.Request.QueryString["rank"] == "on";
            if (hudDigits || rankBadge)
            {
                var look = new WheelWizard.StreamOverlay.OverlayConfig(settings);
                var vr = look.GetString("typography.vrColor").TrimStart('#');
                var mmrSource = context.Request.QueryString["src"] == "mmr";
                var division = Uri.EscapeDataString(context.Request.QueryString["div"] ?? string.Empty);
                var script =
                    $"{WheelWizard.StreamOverlay.HudDigits.ScriptPath}?c={Uri.EscapeDataString(vr)}&d={(hudDigits ? 1 : 0)}&r={(rankBadge ? 1 : 0)}"
                    + $"&m={(mmrSource ? 1 : 0)}&div={division}"
                    + $"&ro={Uri.EscapeDataString(context.Request.QueryString["ro"] ?? string.Empty)}"
                    + $"&rw={Uri.EscapeDataString(context.Request.QueryString["rw"] ?? "3")}"
                    + $"&bs={Uri.EscapeDataString(context.Request.QueryString["bs"] ?? "100")}"
                    + $"&bg={Uri.EscapeDataString(context.Request.QueryString["bg"] ?? "15")}"
                    + $"&t={(context.Request.QueryString["text"] == "hud" ? 1 : 0)}"
                    + $"&hc={(context.Request.QueryString["vrhud"] == "1" ? 1 : 0)}"
                    + $"&tc={Uri.EscapeDataString(look.GetString("typography.textColor").TrimStart('#'))}"
                    + $"&rc={Uri.EscapeDataString(look.GetString("typography.rankColor").TrimStart('#'))}"
                    + $"&mc={Uri.EscapeDataString(look.GetString("typography.mutedColor").TrimStart('#'))}"
                    + $"&gc={Uri.EscapeDataString(look.GetString("typography.gainColor").TrimStart('#'))}"
                    + $"&lc={Uri.EscapeDataString(look.GetString("typography.lossColor").TrimStart('#'))}";
                var style = hudDigits ? $"<style>{WheelWizard.StreamOverlay.HudDigits.Css}</style>" : string.Empty;
                html = html.Replace("</head>", $"{style}<script defer src=\"{script}\"></script></head>");
            }
            if (context.Request.QueryString["src"] == "mmr")
                html = System.Text.RegularExpressions.Regex.Replace(html, @"(assets/)(index-[^""']+\.js)", "$1mmr-$2");
            bytes = Encoding.UTF8.GetBytes(html);
        }

        context.Response.ContentType = found.ContentType;
        context.Response.Headers["Cache-Control"] = path == "/overlay/bg" || isPage ? "no-cache" : "max-age=3600";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    /// <summary>
    /// The sound bars' levels for the OBS page, about 30 times a second, on a connection the page keeps open
    /// (server-sent events). "-" means there are none (nothing playing, or no audio capture), and the page uses its own
    /// animation. The levels are the ones the in-app card shows, so both move together.
    /// </summary>
    private async Task StreamLevelsAsync(HttpListenerContext context)
    {
        var count = int.TryParse(context.Request.QueryString["n"], out var n) ? Math.Clamp(n, 1, 64) : 22;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.SendChunked = true;
        var output = context.Response.OutputStream;
        var touched = DateTime.MinValue;
        try
        {
            while (_listener is { IsListening: true })
            {
                // Keeps the media reader running while OBS shows the page.
                if (DateTime.UtcNow - touched > TimeSpan.FromSeconds(5))
                {
                    nowPlaying.Touch();
                    touched = DateTime.UtcNow;
                }

                var levels = nowPlaying.Current is { IsPlaying: true } ? WheelWizard.NowPlaying.AudioSpectrum.Shared.Levels(count) : null;
                var line = levels is null
                    ? "data: -\n\n"
                    : "data: "
                        + string.Join(
                            ",",
                            levels.Select(level => level.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                        )
                        + "\n\n";
                await output.WriteAsync(Encoding.UTF8.GetBytes(line));
                await output.FlushAsync();
                await Task.Delay(33);
            }
        }
        catch (Exception exception) when (exception is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The page was closed.
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception) { }
        }
    }

    // The page for OBS asks every second, which keeps the media reader running for as long as the page is open.
    private async Task ServeNowPlayingAsync(HttpListenerContext context, string path)
    {
        nowPlaying.Touch();
        if (path.TrimEnd('/') == "/now-playing/levels")
        {
            await StreamLevelsAsync(context);
            return;
        }

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

    // The RR Lounge stats page for OBS. Its two-second asks keep the stats reads running while OBS shows it; the reads
    // themselves run on the app's own timer, never per request.
    private async Task ServeLoungeStatsAsync(HttpListenerContext context, string path)
    {
        loungeStats.Touch();
        byte[]? bytes = null;
        var type = "text/html; charset=utf-8";
        if (path.StartsWith(WheelWizard.LoungeStats.LoungeStatsPage.GlyphPath, StringComparison.Ordinal))
        {
            var name = path[WheelWizard.LoungeStats.LoungeStatsPage.GlyphPath.Length..];
            var glyph =
                name == "dash" ? '-'
                : name.Length == 1 ? name[0]
                : '\0';
            bytes =
                glyph == '\0'
                    ? null
                    : WheelWizard.LoungeStats.HudArt.GlyphPng(
                        glyph,
                        WheelWizard.LoungeStats.HudLook.From(loungeStatsConfig, loungeStats.Current)
                    );
            type = "image/png";
        }
        else
            switch (path.TrimEnd('/'))
            {
                case "/lounge-stats":
                    bytes = Encoding.UTF8.GetBytes(WheelWizard.LoungeStats.LoungeStatsPage.Html);
                    break;
                case "/lounge-stats/state":
                    bytes = Encoding.UTF8.GetBytes(WheelWizard.LoungeStats.LoungeStatsPage.State(loungeStats, loungeStatsConfig));
                    type = "application/json";
                    break;
                case WheelWizard.LoungeStats.LoungeStatsPage.BadgePath when loungeStats.Current?.BadgeUrl is { } badge:
                    bytes = await BadgeAsync(badge);
                    type = "image/png";
                    break;
            }

        if (bytes is null)
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        context.Response.ContentType = type;
        context.Response.Headers["Cache-Control"] = type == "image/png" ? "max-age=3600" : "no-store";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _badges = new();

    // The badge from the launcher's saved small copy (the site's is 1.4 MB), as PNG, made once per badge.
    private async Task<byte[]?> BadgeAsync(string url)
    {
        if (_badges.TryGetValue(url, out var known))
            return known;
        if (await loungeImages.GetAsync(url, 640) is not { } bitmap)
            return null;
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return _badges[url] = stream.ToArray();
    }

    // The Race HUD digits of the VR overlay: the script that draws them, and each digit in the asked colour.
    private async Task ServeHudDigitsAsync(HttpListenerContext context, string path)
    {
        byte[]? bytes;
        string type;
        if (path == WheelWizard.StreamOverlay.HudDigits.RankPath + "lounge")
        {
            // The RR Lounge division badge, for the MMR badge.
            bytes = loungeStats.Current?.BadgeUrl is { } badge ? await BadgeAsync(badge) : null;
            type = "image/png";
        }
        else if (path.StartsWith(WheelWizard.StreamOverlay.HudDigits.RankPath, StringComparison.Ordinal))
        {
            var file = WheelWizard.StreamOverlay.HudDigits.RankFile(path[WheelWizard.StreamOverlay.HudDigits.RankPath.Length..]);
            bytes = file is null ? null : await File.ReadAllBytesAsync(file);
            type = "image/webp";
        }
        else if (path == WheelWizard.StreamOverlay.HudDigits.ScriptPath)
        {
            bytes = Encoding.UTF8.GetBytes(WheelWizard.StreamOverlay.HudDigits.Script);
            type = "text/javascript; charset=utf-8";
        }
        else
        {
            var name = path[WheelWizard.StreamOverlay.HudDigits.GlyphPath.Length..];
            var glyph =
                name == "dash" ? '-'
                : name == "plus" ? '+'
                : name.Length == 1 && (char.IsAsciiDigit(name[0]) || char.IsAsciiLetterUpper(name[0])) ? name[0]
                : ' ';
            var color = "#" + (context.Request.QueryString["c"] ?? string.Empty);
            bytes = glyph == ' ' ? null : WheelWizard.LoungeStats.HudArt.GlyphPng(glyph, WheelWizard.StreamOverlay.HudDigits.Look(color));
            type = "image/png";
        }

        if (bytes is null)
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        context.Response.ContentType = type;
        context.Response.Headers["Cache-Control"] = "max-age=3600";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    // The MMR badge's rank badge: the division travels in the link, so the page reloads when it changes.
    private string? WithDivision(string? url)
    {
        if (url is null || !url.Contains("src=mmr", StringComparison.Ordinal))
            return url;
        if (loungeStats.Current?.Division is { Length: > 0 } division)
            url += "&div=" + Uri.EscapeDataString(division.ToLowerInvariant());
        // The flag travels as the country, so the page reloads when it changes.
        if (url.Contains("flag=on", StringComparison.Ordinal) && loungeStats.Current?.CountryCode is { Length: 2 } country)
            url += "&cc=" + country.ToLowerInvariant();
        return url;
    }

    /// <summary>
    /// The RR Lounge MMR in the shape of an rwfc.net player, for the VR overlay page when it shows MMR: the MMR as "vr"
    /// and the ladder place as "rank". It is the stats the app already reads; asking here only keeps those reads going.
    /// No rooms, so a room's VR never replaces the MMR.
    /// </summary>
    private async Task ServeOverlayMmrAsync(HttpListenerContext context, string path)
    {
        loungeStats.Touch();
        string? json = null;
        var rest = path["/overlay-mmr/".Length..].TrimEnd('/');
        if (rest == "rooms")
            json = "{\"rooms\":[]}";
        else if (rest == "flag" && WheelWizard.RrLounge.RrLoungeData.FlagUrl(loungeStats.Current?.CountryCode) is { } flagUrl)
        {
            // flagcdn.com's 80-wide picture (not the 16-wide one the table uses), so it stays sharp at overlay size.
            if (await loungeImages.GetAsync(flagUrl.Replace("/16x12/", "/w80/"), 0) is { } flag)
            {
                using var stream = new MemoryStream();
                flag.Save(stream);
                var png = stream.ToArray();
                context.Response.ContentType = "image/png";
                context.Response.Headers["Cache-Control"] = "max-age=600";
                context.Response.ContentLength64 = png.Length;
                await context.Response.OutputStream.WriteAsync(png);
                context.Response.Close();
                return;
            }
        }
        else if (rest.StartsWith("player/", StringComparison.Ordinal))
        {
            var parts = rest.Split('/');
            if (parts.Length == 4 && parts[2] == "mii" && parts[3] == "image")
            {
                // The Mii is the one of the friend code picked for VR, from rwfc.net.
                var digits = overlay.FriendCodeDigits();
                if (digits.Length == 12)
                {
                    context.Response.Redirect(
                        $"https://rwfc.net/api/leaderboard/player/{digits[..4]}-{digits.Substring(4, 4)}-{digits.Substring(8, 4)}/mii/image"
                    );
                    context.Response.Close();
                    return;
                }
            }
            else if (parts.Length == 2 && loungeStats.Current is { } stats)
                json = System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        pid = "rr-lounge",
                        name = stats.Name,
                        friendCode = Uri.UnescapeDataString(parts[1]),
                        vr = stats.Mmr,
                        rank = stats.Place,
                        isSuspicious = false,
                    }
                );
        }

        if (json is null)
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentType = "application/json";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task ServeOverlayAsync(HttpListenerContext context, string path)
    {
        var state = path == "/overlay/state";
        var body = state
            ? System.Text.Json.JsonSerializer.Serialize(new { url = WithDivision(overlay.CurrentUrl()) })
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
