using System.Diagnostics;
using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using WheelWizard.Dolphin.Paths;
using WheelWizard.Models.RRInfo;
using WheelWizard.RrRooms;
using WheelWizard.Settings;
using WheelWizard.WiiManagement.GameLicense;
using WheelWizard.WiiManagement.GameLicense.Domain;

namespace WheelWizard.DiscordPresence;

/// <summary>
/// Shows a user-configured Discord presence while Dolphin is running. The pack's own presence is
/// untouched, so the online server's pack check keeps passing.
/// </summary>
public sealed class DiscordPresenceService(
    ISettingsManager settings,
    IDolphinPaths dolphinPaths,
    IFileSystem fileSystem,
    LiveRoomsService rooms,
    IRrRoomsSingletonService roomsApi,
    IGameLicenseSingletonService licenses,
    ILogger<DiscordPresenceService> logger
) : IDisposable
{
    // Checking is cheap (a memory read), but Discord only takes a few updates every 20 seconds, so an update
    // is sent only when something changed.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RoomRefresh = TimeSpan.FromSeconds(8);
    private readonly CancellationTokenSource _stop = new();
    private readonly DolphinMemoryReader _memory = new();
    private DiscordIpcClient? _client;
    private string? _connectedClientId;
    private long _startedAt;
    private int? _lastSection;
    private string _lastMoment = "";
    private bool _started;
    private DiscordActivity? _lastSent;
    private int? _lastGoodSection;
    private DateTime _lastSentAt = DateTime.MinValue;

    // Resent now and then even when nothing changed: it notices a connection Discord dropped (for example while
    // you were idle) and puts the status back.
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(60);
    private List<RwfcRoomStatusRoom> _freshRooms = [];
    private DateTime _freshRoomsAt = DateTime.MinValue;
    private bool _wasRacing;
    private int? _raceBeforeThisOne;
    private int? _lastShownRace;

    /// <summary>The name the Discord app must have, because it is the title everyone sees ("Playing Retro Rewind").</summary>
    public const string RequiredName = "Retro Rewind";

    /// <summary>The leaderboard page for a friend code (like 1234-5678-9012), or null for anything else.</summary>
    public static string? BuildPlayerUrl(string? friendCode)
    {
        var code = (friendCode ?? "").Trim();
        var valid =
            code.Length == 14
            && code[4] == '-'
            && code[9] == '-'
            && code.Where((c, i) => i is not (4 or 9)).All(char.IsAsciiDigit)
            && code != "0000-0000-0000";
        return valid ? $"https://rwfc.net/player/{code}" : null;
    }

    public static bool IsValidApplicationId(string value) => value.Length is >= 15 and <= 20 && value.All(char.IsAsciiDigit);

    // Dolphin's own Discord application: its status is posted under this id and Dolphin's process id.
    private const string DolphinApplicationId = "455712169795780630";

    // Retro Rewind's own Discord application, the one the pack posts its status under (the id is in the pack itself).
    private const string PackApplicationId = "1471316950004006963";

    /// <summary>
    /// Dolphin only reads its "show status" setting when it starts, so a running Dolphin keeps its own status. This asks
    /// Discord to clear that status the same way Dolphin would, so switching it off applies at once. It is best effort:
    /// false when Dolphin is not running or Discord did not answer.
    /// </summary>
    public async Task<bool> ClearDolphinOwnStatusAsync()
    {
        var processId = FindDolphinProcessId();
        if (processId is null)
            return false;
        try
        {
            // A status can sit under Dolphin's own application or under the pack's, depending on what is installed.
            foreach (var applicationId in new[] { DolphinApplicationId, PackApplicationId })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var client = await DiscordIpcClient.ConnectAsync(applicationId, timeout.Token);
                await client.ClearActivityAsync(timeout.Token, processId);
            }

            return true;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not clear Dolphin's own Discord status");
            return false;
        }
    }

    /// <summary>What the launcher can currently see of Dolphin and the game, for the settings page.</summary>
    public string Diagnose()
    {
        var processId = FindDolphinProcessId();
        if (processId is not null)
            return _memory.Diagnose(processId.Value);
        return FindRecompProcessId() is { } recompId
            ? _memory.Diagnose(recompId, recomp: true)
            : "Neither Dolphin nor WiiCompiled is running.";
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>The names of the images uploaded to a Discord application (empty when none or it could not be read).</summary>
    public static async Task<IReadOnlyList<string>> GetImageNamesAsync(string applicationId)
    {
        try
        {
            var json = await Http.GetStringAsync(
                $"https://discord.com/api/v9/oauth2/applications/{applicationId}/assets?cb={DateTime.UtcNow.Ticks}"
            );
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document
                .RootElement.EnumerateArray()
                .Select(asset => asset.GetProperty("name").GetString() ?? "")
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The name of a Discord application, or null when it could not be looked up.</summary>
    public static async Task<string?> GetApplicationNameAsync(string applicationId)
    {
        try
        {
            var json = await Http.GetStringAsync(
                $"https://discord.com/api/v9/oauth2/applications/{applicationId}/rpc?cb={DateTime.UtcNow.Ticks}"
            );
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.GetProperty("name").GetString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a Discord application has an App Icon (null when it could not be looked up). Without one, Discord shows a
    /// question mark next to the player's name in the member list.
    /// </summary>
    public static async Task<bool?> HasApplicationIconAsync(string applicationId)
    {
        try
        {
            var json = await Http.GetStringAsync(
                $"https://discord.com/api/v9/oauth2/applications/{applicationId}/rpc?cb={DateTime.UtcNow.Ticks}"
            );
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("icon", out var icon) && !string.IsNullOrEmpty(icon.GetString());
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks Discord whether the saved image key exists in the saved application, so a missing or unsaved upload
    /// is reported instead of showing a blank image on Discord.
    /// </summary>
    public async Task<string> CheckImageAsync()
    {
        var clientId = settings.Get<string>(settings.DISCORD_APP_ID).Trim();
        var key = settings.Get<string>(settings.DISCORD_LARGE_IMAGE).Trim();
        if (!IsValidApplicationId(clientId))
            return "No Application ID yet.";
        if (key.Length == 0)
            return "No image set.";

        try
        {
            var json = await Http.GetStringAsync(
                $"https://discord.com/api/v9/oauth2/applications/{clientId}/assets?cb={DateTime.UtcNow.Ticks}"
            );
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var names = document.RootElement.EnumerateArray().Select(asset => asset.GetProperty("name").GetString() ?? "").ToList();
            var result =
                names.Contains(key, StringComparer.Ordinal) ? $"Image '{key}' found."
                : names.Count == 0 ? $"Discord can't see '{key}' yet. If you just added it, wait up to 15 minutes."
                : $"'{key}' not found. Your app has: {string.Join(", ", names.Take(8))}.";
            if (await HasApplicationIconAsync(clientId) == false)
                result += "\nThe app has no App Icon, so Discord shows a ? next to your name. Add one under General Information.";
            return result;
        }
        catch (Exception)
        {
            return "Can't reach Discord.";
        }
    }

    /// <summary>The images uploaded to the saved Discord application, by the name used as the image key.</summary>
    public async Task<IReadOnlyList<DiscordAsset>> ListAssetsAsync()
    {
        var clientId = settings.Get<string>(settings.DISCORD_APP_ID).Trim();
        if (!IsValidApplicationId(clientId))
            return [];

        try
        {
            var json = await Http.GetStringAsync(
                $"https://discord.com/api/v9/oauth2/applications/{clientId}/assets?cb={DateTime.UtcNow.Ticks}"
            );
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document
                .RootElement.EnumerateArray()
                .Select(asset => new DiscordAsset(asset.GetProperty("name").GetString() ?? "", asset.GetProperty("id").GetString() ?? ""))
                .Where(asset => asset.Name.Length > 0 && asset.Id.Length > 0)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Downloads one uploaded image for the preview. Null when it cannot be fetched or is too big.</summary>
    public async Task<byte[]?> DownloadAssetAsync(DiscordAsset asset)
    {
        var clientId = settings.Get<string>(settings.DISCORD_APP_ID).Trim();
        if (!IsValidApplicationId(clientId))
            return null;

        try
        {
            var bytes = await Http.GetByteArrayAsync($"https://cdn.discordapp.com/app-assets/{clientId}/{asset.Id}.png?size=256");
            return bytes.Length is > 0 and <= 4 * 1024 * 1024 ? bytes : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Start()
    {
        if (_started)
            return;
        _started = true;
        _ = Task.Run(() => LoopAsync(_stop.Token));
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await TickAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        } while (await WaitAsync(timer, cancellationToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private int? _liveDefaultProcessId;

    /// <summary>
    /// Called when "Default" is picked. If Dolphin is already open it will not show its own status until it restarts,
    /// so the launcher shows the same status for it now. False when Dolphin is not open (nothing to do then).
    /// </summary>
    public bool EnableLiveDefault()
    {
        _liveDefaultProcessId = FindDolphinProcessId();
        RefreshNow();
        return _liveDefaultProcessId is not null;
    }

    public void DisableLiveDefault() => _liveDefaultProcessId = null;

    private readonly SemaphoreSlim _tickGate = new(1, 1);

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        await _tickGate.WaitAsync(cancellationToken);
        try
        {
            await TickCoreAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Dropped inside the gate, so a refresh and the timer never tear down a connection the other is using.
            logger.LogDebug(exception, "Discord presence update failed");
            DropClient();
        }
        finally
        {
            _tickGate.Release();
        }
    }

    /// <summary>Runs an update right away instead of waiting for the next one, for when a setting just changed.</summary>
    public void RefreshNow()
    {
        if (_started)
            _ = Task.Run(async () =>
            {
                try
                {
                    await TickAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    // The launcher is closing.
                }
                catch (ObjectDisposedException)
                {
                    // The launcher is closing.
                }
            });
    }

    private async Task TickCoreAsync(CancellationToken cancellationToken)
    {
        var clientId = settings.Get<string>(settings.DISCORD_APP_ID).Trim();
        // "Default" picked while Dolphin was already open: Dolphin will not start its own status until it restarts, so
        // the launcher posts the same one (under the pack's application) for that Dolphin.
        var liveDefault =
            _liveDefaultProcessId is { } pinned
            && !settings.Get<bool>(settings.DISCORD_PRESENCE_ENABLED)
            && FindDolphinProcessId() == pinned;
        if (_liveDefaultProcessId is not null && !liveDefault)
            _liveDefaultProcessId = null;
        if (liveDefault)
            clientId = PackApplicationId;
        var dolphinId =
            (liveDefault || settings.Get<bool>(settings.DISCORD_PRESENCE_ENABLED)) && IsValidApplicationId(clientId)
                ? FindDolphinProcessId()
                : null;
        var useLive = liveDefault || settings.Get<bool>(settings.DISCORD_LIVE_INFO);
        // WiiCompiled is a native build, but it keeps the game's memory laid out like the Wii's at a fixed place in its
        // process, so the same screen, license and track reads work there too.
        var recompId =
            dolphinId is null && settings.Get<bool>(settings.DISCORD_PRESENCE_ENABLED) && IsValidApplicationId(clientId)
                ? FindRecompProcessId()
                : null;
        var recompRunning = recompId is not null;
        var gameId = dolphinId ?? recompId;
        int? section = null;
        LiveInfo? live = null;
        if (gameId is { } processId && useLive)
        {
            section = _memory.ReadSectionId(processId, recompRunning);
            // The screen cannot be read for a moment while the game switches menus or sits idle. The game is still
            // there, so keep the last screen (or a plain menu) instead of dropping the status back to Dolphin's.
            if (section is { } readId)
                _lastGoodSection = readId;
            else if (_memory.GameFound)
                section = _lastGoodSection ?? -1;
            else
                _lastGoodSection = null;
            if (section is { } id && RetroRewindSections.IsOnline(id))
                await RefreshRoomsAsync(cancellationToken);
            else if (section is null && recompRunning)
                await RefreshRoomsAsync(cancellationToken);
            live = BuildLiveInfo(
                section,
                _memory.ReadLicenseIndex(processId, recompRunning),
                _memory.ReadTrackName(processId, recompRunning)
            );
            // Dolphin open with no game loaded: show nothing rather than a stale static status. (WiiCompiled always is
            // the game, so it keeps the static lines.)
            if (live == null && DolphinMemoryReader.IsSupported && !recompRunning)
                dolphinId = null;
        }

        if (dolphinId is null && !recompRunning)
        {
            if (_client != null)
            {
                await _client.ClearActivityAsync(cancellationToken);
                DropClient();
            }
            _startedAt = 0;
            _lastSection = null;
            _lastMoment = "";
            _lastSent = null;
            return;
        }

        if (_client == null || _connectedClientId != clientId)
        {
            // The old status stays up until the new one is posted, so Discord never falls back to showing "Dolphin".
            _retiringClient?.Dispose();
            _retiringClient = _client;
            _client = null;
            _connectedClientId = null;
            _lastSent = null;
            _client = await DiscordIpcClient.ConnectAsync(clientId, cancellationToken);
            _connectedClientId = clientId;
        }

        // Like the pack's own presence, the timer restarts whenever the screen changes. Without a readable screen it follows
        // the room and its race instead, so it counts the time on this track and not the whole time in the room.
        var moment = live?.Moment ?? "";
        if (_startedAt == 0 || section != _lastSection || moment != _lastMoment)
        {
            _startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // The pack posts its own status again whenever the screen changes, so it is cleared again each time.
            if (!liveDefault)
            {
                _lastDolphinClear = DateTime.UtcNow;
                _ = ClearDolphinOwnStatusAsync();
            }
        }
        // Dolphin can post its own status again while the game sits idle in a menu, which would replace this one, so it is
        // cleared again now and then and not only when the screen changes.
        else if (!liveDefault && DateTime.UtcNow - _lastDolphinClear > DolphinClearEvery)
        {
            _lastDolphinClear = DateTime.UtcNow;
            _ = ClearDolphinOwnStatusAsync();
        }
        _lastSection = section;
        _lastMoment = moment;

        // The first line is always the game's name, so every status reads the same.
        var details = RequiredName;
        var state = settings.Get<string>(settings.DISCORD_STATE);
        var partySize = 0;
        var partyMax = 0;
        if (live != null)
        {
            details = live.Details;
            state = live.State;
            partySize = live.PlayerCount;
            partyMax = live.PlayerCount > 0 ? Math.Max(live.PlayerCount, 12) : 0;
        }

        // Left alone on a menu or vote screen for a long time, or in a room you stopped playing in: the track and the room
        // are no longer true, so the status says idle, with the time counted from when it went idle. Screens with a track
        // (offline modes, online races) are being played, so a long Grand Prix or Time Trial session is never idle.
        var idle =
            live != null
            && !liveDefault
            && (section is not { } screen || !RetroRewindSections.ShowsGameTrack(screen))
            && IsIdle(_startedAt, DateTimeOffset.UtcNow);
        if (idle)
        {
            details = "Idle";
            state = "";
            partySize = 0;
            partyMax = 0;
        }

        // View Player opens your page on the leaderboard; View Room opens the room list, and only while you are in one.
        // Offline play has no leaderboard page yet, so View Player waits until you are online on a license.
        var online = section is { } onlineId && RetroRewindSections.IsOnline(onlineId) || live is { InRoom: true };
        var playerUrl =
            online && !idle && !liveDefault && settings.Get<bool>(settings.DISCORD_PLAYER_BUTTON)
                ? live?.PlayerUrl ?? BuildPlayerUrl(ActiveProfile?.FriendCode)
                : null;
        var roomUrl =
            !liveDefault
            && !idle
            && settings.Get<bool>(settings.DISCORD_ROOM_BUTTON)
            && live is { InRoom: true, RoomId: { } roomId }
            && RoomLinkListener.IsValidRoomId(roomId)
                ? RoomLinkListener.BuildUrl(roomId, live.PlayerUrl?.Split('/').Last())
                : null;

        var activity = new DiscordActivity(
            details,
            state,
            liveDefault ? "" : settings.Get<string>(settings.DISCORD_LARGE_IMAGE),
            settings.Get<string>(settings.DISCORD_LARGE_TEXT),
            idle ? _startedAt + (long)IdleAfter.TotalSeconds : _startedAt,
            partySize,
            partyMax,
            playerUrl,
            roomUrl
        );
        if (activity == _lastSent && DateTime.UtcNow - _lastSentAt < Heartbeat)
            return;

        await _client.SetActivityAsync(activity, cancellationToken);
        _retiringClient?.Dispose();
        _retiringClient = null;
        _lastSent = activity;
        _lastSentAt = DateTime.UtcNow;
    }

    /// <summary>
    /// The room list the rest of the launcher uses refreshes every 40 seconds, which is too slow to follow a vote.
    /// While you are online this asks the server directly every few seconds.
    /// </summary>
    private async Task RefreshRoomsAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _freshRoomsAt < RoomRefresh)
            return;
        _freshRoomsAt = DateTime.UtcNow;

        try
        {
            var result = await roomsApi.GetRoomsAsync().WaitAsync(cancellationToken);
            if (result.IsSuccess)
                _freshRooms = result.Value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not refresh rooms for Discord presence");
        }
    }

    private sealed record LiveInfo(
        string Details,
        string State,
        int PlayerCount,
        string? PlayerUrl = null,
        bool InRoom = false,
        string? RoomId = null,
        string Moment = ""
    );

    /// <summary>Tells one race of one room from the next, so the status timer can start over for each.</summary>
    public static string MomentOf(string? roomId, int? raceNumber) => roomId is null ? "" : $"{roomId}:{raceNumber ?? 0}";

    private sealed record MyRoom(string Id, string Track, int PlayerCount, int RaceNumber, int? Vr, int? Br, string? Rk);

    /// <summary>
    /// The screen comes from the game itself (same wording as the pack's presence); the online room list adds the
    /// track, player count and ratings. Without the game screen, the room list alone still shows an online room.
    /// </summary>
    private int? _loggedMissingTrack;
    private DateTime _lastDolphinClear = DateTime.MinValue;
    private static readonly TimeSpan DolphinClearEvery = TimeSpan.FromSeconds(20);

    /// <summary>How long the same screen (or the same race of a room) can stay up before the status says you are idle.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(15);

    /// <summary>True when the status has shown the same thing for <see cref="IdleAfter"/> or longer.</summary>
    public static bool IsIdle(long startedAtUnixSeconds, DateTimeOffset now) =>
        startedAtUnixSeconds > 0 && now - DateTimeOffset.FromUnixTimeSeconds(startedAtUnixSeconds) >= IdleAfter;

    private LiveInfo? BuildLiveInfo(int? sectionId, int? licenseIndex, string? gameTrack = null)
    {
        // The license picked in the game, not the one selected in the launcher, is the one playing.
        var profile = ProfileAt(licenseIndex) ?? ActiveProfile;

        if (sectionId is { } id)
        {
            var room = FindFreshRoom(profile);
            var details = RetroRewindSections.Describe(id);
            if (RetroRewindSections.IsOnline(id))
                details += $" (VR: {Rating(room?.Vr, profile?.Vr)} BR: {Rating(room?.Br, profile?.Br)})";

            var raceTrack = TrackFor(id, room);
            // Offline play (Time Trials, Grand Prix, VS...) has no room to name the track, so the name the pack has
            // loaded in the game is used, the same one the pack's own status shows.
            if (raceTrack.Length == 0 && gameTrack is not null && RetroRewindSections.ShowsGameTrack(id))
                raceTrack = gameTrack;
            if (raceTrack.Length == 0 && gameTrack is null && RetroRewindSections.ShowsGameTrack(id) && _loggedMissingTrack != id)
            {
                // Once per screen, so a missing track name in Time Trials shows up in the log without filling it.
                _loggedMissingTrack = id;
                logger.LogInformation(
                    "Discord status: screen {Section} should show a track, but no track name could be read from the game",
                    id
                );
            }
            return new(
                details,
                raceTrack,
                room?.PlayerCount ?? 0,
                BuildPlayerUrl(profile?.FriendCode),
                room != null,
                room?.Id,
                MomentOf(room?.Id, room?.RaceNumber)
            );
        }

        var fresh = FindFreshRoom(profile);
        if (fresh != null)
        {
            var freshMode = ModeLabel(fresh.Rk);
            var freshRating = fresh.Vr is { } freshVr ? $" (VR: {freshVr} BR: {fresh.Br?.ToString() ?? "--"})" : "";
            return new(
                $"{freshMode}{freshRating}",
                fresh.Track.Length > 0 ? fresh.Track : "In a room",
                fresh.PlayerCount,
                BuildPlayerUrl(profile?.FriendCode),
                true,
                fresh.Id,
                MomentOf(fresh.Id, fresh.RaceNumber)
            );
        }

        var (cachedRoom, me) = FindCachedRoom(profile);
        if (cachedRoom == null)
            return null;

        var mode = string.IsNullOrWhiteSpace(cachedRoom.GameModeAbbrev) ? "Online" : $"Online {cachedRoom.GameModeAbbrev}";
        var rating = me?.Vr is { } vr ? $" (VR: {vr} BR: {me.Br?.ToString() ?? "--"})" : "";
        var track = string.IsNullOrWhiteSpace(cachedRoom.TrackName) ? "In a room" : cachedRoom.TrackName;
        return new(
            $"{mode}{rating}",
            track,
            cachedRoom.PlayerCount,
            BuildPlayerUrl(profile?.FriendCode),
            true,
            cachedRoom.Id,
            MomentOf(cachedRoom.Id, cachedRoom.RaceNumber)
        );
    }

    /// <summary>
    /// The server only learns about a new race a few seconds after it starts, so right after a vote it still reports
    /// the previous track. A track is shown only once the server's race number has moved past the previous race.
    /// </summary>
    private string TrackFor(int sectionId, MyRoom? room)
    {
        if (!RetroRewindSections.IsOnlineRace(sectionId))
        {
            _wasRacing = false;
            return "";
        }

        if (!_wasRacing)
        {
            _wasRacing = true;
            _raceBeforeThisOne = _lastShownRace;
        }

        if (room is not { Track.Length: > 0 })
            return "";
        if (_raceBeforeThisOne is { } before && room.RaceNumber == before)
            return "";

        _lastShownRace = room.RaceNumber;
        return room.Track;
    }

    private static string Rating(int? live, uint? saved) => (live ?? (int?)saved)?.ToString() ?? "--";

    private LicenseProfile? ProfileAt(int? index)
    {
        try
        {
            var users = licenses.LicenseCollection.Users;
            return index is { } i && i >= 0 && i < users.Count ? users[i] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private LicenseProfile? ActiveProfile
    {
        get
        {
            try
            {
                return licenses.ActiveUser;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Finds the room the given license is in, in the room list refreshed every few seconds.</summary>
    private MyRoom? FindFreshRoom(LicenseProfile? profile)
    {
        var myCode = Digits(profile?.FriendCode);
        if (myCode.Length == 0)
            return null;

        foreach (var room in _freshRooms)
        {
            var me = room.Players.FirstOrDefault(player => Digits(player.FriendCode) == myCode);
            if (me != null)
                return new(room.Id, room.Race?.TrackName ?? "", room.Players.Count, room.Race?.Num ?? 0, me.Vr, me.Br, room.Rk);
        }

        return null;
    }

    /// <summary>Finds the room the given license is in, using the launcher's slower shared room list.</summary>
    private (RrRoom? Room, RrPlayer? Me) FindCachedRoom(LicenseProfile? profile)
    {
        try
        {
            var myCode = Digits(profile?.FriendCode);
            if (myCode.Length == 0)
                return (null, null);

            foreach (var room in rooms.CurrentRooms)
            {
                var me = room.Players.FirstOrDefault(player => Digits(player.FriendCode) == myCode);
                if (me != null)
                    return (room, me);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not look up the active player's room");
        }

        return (null, null);
    }

    /// <summary>"Online RR", "Online TT" and so on, using the same mode names as the Rooms page.</summary>
    private static string ModeLabel(string? rk)
    {
        var abbreviation = new RrRoom
        {
            Id = "",
            Created = DateTime.MinValue,
            Type = "",
            Suspend = false,
            Players = [],
            Rk = rk,
        }.GameModeAbbrev;
        return string.IsNullOrWhiteSpace(abbreviation) ? "Online" : $"Online {abbreviation}";
    }

    private int? FindRecompProcessId()
    {
        // The Retro Rewind product and the base game each ship their own executable.
        foreach (var name in new[] { "RetroRewind", "WiiCompiled" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                    return processes[0].Id;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        return null;
    }

    private static string Digits(string? value) => new((value ?? "").Where(char.IsAsciiDigit).ToArray());

    private int? FindDolphinProcessId()
    {
        var name = fileSystem.Path.GetFileNameWithoutExtension(dolphinPaths.ExecutablePath);
        if (string.IsNullOrWhiteSpace(name))
            name = "Dolphin";
        var processes = Process.GetProcessesByName(name);
        try
        {
            return processes.Length > 0 ? processes[0].Id : null;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private DiscordIpcClient? _retiringClient;

    private void DropClient()
    {
        _retiringClient?.Dispose();
        _retiringClient = null;
        _client?.Dispose();
        _client = null;
        _connectedClientId = null;
        _lastSent = null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        DropClient();
        _memory.Dispose();
        _stop.Dispose();
    }
}

/// <summary>An image uploaded to a Discord application: its key name and its asset id.</summary>
public sealed record DiscordAsset(string Name, string Id);
