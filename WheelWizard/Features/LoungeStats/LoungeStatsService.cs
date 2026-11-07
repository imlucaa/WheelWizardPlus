using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WheelWizard.RrLounge;
using WheelWizard.Settings;
using WheelWizard.Shared.Services;

namespace WheelWizard.LoungeStats;

/// <summary>
/// Reads one player's RR Lounge stats in the background while something shows them: the settings tab or the window
/// (<see cref="Use"/>), or an OBS page that asked recently (<see cref="Touch"/>). Otherwise it never asks the site.
/// <para>
/// Each read takes the leaderboard (a small JSON answer the site caches for 15 seconds) for the place and MMR. The
/// player's page, about 300 KB, is only read when the MMR moved (a mogi finished, so wins or losses and maybe the
/// division changed), for a new player, or every 15 minutes for a changed country or name. A failed read keeps the last
/// numbers on screen and waits longer before the next try, up to 10 minutes.
/// </para>
/// </summary>
public sealed class LoungeStatsService : IDisposable
{
    private static readonly TimeSpan TouchLasts = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProfileLasts = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);

    private readonly IApiCaller<IRrLoungeApi> _api;
    private readonly LoungeStatsConfig _config;
    private readonly ISettingsManager _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly ITimer _timer;
    private int _users;
    private int _fetching;
    private int _failures;
    private DateTimeOffset _touchedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _nextRead = DateTimeOffset.MinValue;

    // The player page failed last time, so the next read asks for it again even if the MMR did not move.
    private bool _pageOwed;
    private string _playerKey;

    public LoungeStatsService(
        IApiCaller<IRrLoungeApi> api,
        LoungeStatsConfig config,
        ISettingsManager settings,
        TimeProvider time,
        ILogger<LoungeStatsService>? logger = null,
        WheelWizard.ApplicationData.IApplicationDataLocation? data = null
    )
    {
        if (data is not null)
            HudArt.FontFolder = System.IO.Path.Combine(data.DirectoryPath, "Appearance", "hud-font");
        if (data is not null)
            WheelWizard.StreamOverlay.HudDigits.RankFolder = System.IO.Path.Combine(data.DirectoryPath, "Appearance", "vr-ranks");
        _api = api;
        _config = config;
        _settings = settings;
        _time = time;
        _logger = logger ?? NullLogger<LoungeStatsService>.Instance;
        _playerKey = PlayerKey();

        // The numbers from last time, so the overlay has something to show before the first read.
        var saved = LoungeStatsSnapshot.FromJson(settings.Get<string>(settings.LOUNGE_STATS_CACHE));
        if (saved is not null && Matches(saved))
        {
            Current = saved;
            IsStale = true;
        }

        _config.Changed += OnConfigChanged;
        _timer = time.CreateTimer(_ => _ = PollAsync(), null, Tick, Tick);
    }

    /// <summary>The last numbers read (or saved from last time), or null.</summary>
    public LoungeStatsSnapshot? Current { get; private set; }

    /// <summary>What moved in the last read that changed something, for the change animations.</summary>
    public LoungeStatsChange? LastChange { get; private set; }

    /// <summary>Goes up by one with every new <see cref="LastChange"/>, so a view can tell a new change from an old one.</summary>
    public int ChangeNumber { get; private set; }

    /// <summary>True while the numbers shown are from an earlier read, because the latest one failed or none ran yet.</summary>
    public bool IsStale { get; private set; }

    /// <summary>Why the numbers could not be read or are old, in words for the settings tab, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>True while something shows the stats, so reads run.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
                return _users > 0 || _time.GetUtcNow() < _touchedUntil;
        }
    }

    /// <summary>Raised on a background thread after the numbers, the change or the problem changed.</summary>
    public event Action? Changed;

    /// <summary>Keeps reads running until the returned lease is disposed.</summary>
    public IDisposable Use()
    {
        lock (_gate)
            _users++;
        _ = PollAsync();
        return new Lease(this);
    }

    /// <summary>A request from outside the app (the OBS page): keeps reads running for a little while longer.</summary>
    public void Touch()
    {
        var wasActive = IsActive;
        lock (_gate)
            _touchedUntil = _time.GetUtcNow() + TouchLasts;
        if (!wasActive)
            _ = PollAsync();
    }

    /// <summary>Reads now instead of waiting for the next turn, for example after a new player was picked.</summary>
    public Task RefreshNowAsync()
    {
        lock (_gate)
            _nextRead = DateTimeOffset.MinValue;
        return PollAsync();
    }

    /// <summary>
    /// The players on a ladder whose name is this one (first) or contains it, for picking whose stats to show. Null when
    /// the site cannot be reached.
    /// </summary>
    public async Task<List<RrLoungePlayer>?> FindAsync(string name, string ladder)
    {
        var category = ladder;
        var board = await _api.CallApiAsync(lounge => lounge.GetLeaderboardAsync(category, null));
        if (board.IsFailure)
            return null;
        var text = name.Trim();
        return RrLoungeData
            .Search(board.Value?.Players ?? [], text)
            .OrderByDescending(player => player.Name.Equals(text, StringComparison.OrdinalIgnoreCase))
            .ThenBy(player => player.Rank)
            .Take(12)
            .ToList();
    }

    private string PlayerKey() => $"{_config.Ladder}|{_config.PlayerId}|{_config.PlayerName}";

    private bool Matches(LoungeStatsSnapshot stats) =>
        stats.Ladder == _config.Ladder
        && (
            _config.PlayerId.Length > 0
                ? stats.Id == _config.PlayerId
                : stats.Name.Equals(_config.PlayerName, StringComparison.OrdinalIgnoreCase)
        );

    // Another player or ladder: the old numbers go at once (they are someone else's), and a read runs right away. The
    // refresh time takes effect from the next read.
    private void OnConfigChanged()
    {
        var key = PlayerKey();
        if (key == _playerKey)
            return;

        _playerKey = key;
        lock (_gate)
        {
            _nextRead = DateTimeOffset.MinValue;
            _failures = 0;
            _pageOwed = false;
        }

        if (Current is not null && !Matches(Current))
        {
            Current = null;
            LastChange = null;
            Problem = null;
            Changed?.Invoke();
        }

        _ = PollAsync();
    }

    /// <summary>Runs a read when one is due and something shows the stats. Never more than one at a time.</summary>
    public async Task PollAsync()
    {
        if (!IsActive)
            return;
        lock (_gate)
        {
            if (_time.GetUtcNow() < _nextRead)
                return;
        }

        if (Interlocked.Exchange(ref _fetching, 1) == 1)
            return;
        try
        {
            var ok = await ReadAsync();
            lock (_gate)
            {
                _failures = ok ? 0 : _failures + 1;
                var wait = ok ? _config.RefreshInterval : Backoff(_config.RefreshInterval, _failures);
                _nextRead = _time.GetUtcNow() + wait;
            }
        }
        catch (Exception exception)
        {
            // A read must never take the overlay down; the last numbers stay and the next try comes later.
            _logger.LogWarning(exception, "Could not read the RR Lounge stats");
            lock (_gate)
            {
                _failures++;
                _nextRead = _time.GetUtcNow() + Backoff(_config.RefreshInterval, _failures);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _fetching, 0);
        }
    }

    /// <summary>The wait after <paramref name="failures"/> failed reads in a row: doubled each time, at most 10 minutes.</summary>
    public static TimeSpan Backoff(TimeSpan interval, int failures)
    {
        var seconds = interval.TotalSeconds * Math.Pow(2, Math.Clamp(failures, 0, 8));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    // One read. True when the leaderboard answered, even when the player is not on it, since asking again sooner won't help.
    private async Task<bool> ReadAsync()
    {
        var name = _config.PlayerName;
        var id = _config.PlayerId;
        var ladder = _config.Ladder;
        if (name.Length == 0 && id.Length == 0)
        {
            Report(null, "Pick your RR Lounge name to show your stats.", stale: false);
            return true;
        }

        var board = await _api.CallApiAsync(lounge => lounge.GetLeaderboardAsync(ladder, null));
        if (board.IsFailure)
        {
            Report(Current, "RR Lounge could not be reached. The last stats stay on screen.", stale: true);
            return false;
        }

        var players = board.Value?.Players ?? [];
        var player =
            (id.Length > 0 ? players.FirstOrDefault(p => p.Id == id) : null)
            ?? players.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (player is null)
        {
            Report(Current, $"{name} is not on the {ladder.ToUpperInvariant()} leaderboard this season.", stale: Current is not null);
            return true;
        }

        var previous = Current is not null && Current.Id == player.Id && Current.Ladder == ladder ? Current : null;
        var now = _time.GetUtcNow();
        var needsPage =
            previous is null
            || !previous.HasProfile
            || previous.Mmr != player.Mmr
            || previous.Name != player.Name
            || now - previous.ProfileAt > ProfileLasts
            || _pageOwed;

        RrLoungeProfile? profile = null;
        string? pageProblem = null;
        if (needsPage)
        {
            profile = await RrLoungeProfiles.LoadAsync(_api, player.Name, ToLadder(ladder), null);
            if (profile is null)
                pageProblem = "The player page could not be read, so wins, losses and the rank may be behind.";
            _pageOwed = profile is null;
        }

        var next = LoungeStatsSnapshot.From(player, board.Value?.PlayerCount, ladder, profile, previous, now);
        Report(next, pageProblem, stale: false, LoungeStatsChange.Between(previous, next));

        // The site's id is kept, so the player is found again after a name change on the site.
        if (id != player.Id || name != player.Name)
            _config.SetPlayer(player.Name, player.Id);
        return true;
    }

    private static RrLoungeLadder ToLadder(string ladder) =>
        ladder switch
        {
            "ct" => RrLoungeLadder.Ct,
            "tt" => RrLoungeLadder.Tt,
            _ => RrLoungeLadder.Rr,
        };

    private void Report(LoungeStatsSnapshot? stats, string? problem, bool stale, LoungeStatsChange? change = null)
    {
        var numbersChanged = stats is null
            ? Current is not null
            : Current is null || stats with { FetchedAt = default } != Current with { FetchedAt = default };
        var changed = numbersChanged || problem != Problem || stale != IsStale || change is not null;

        if (stats is not null && numbersChanged)
            SaveCache(stats);
        Current = stats;
        Problem = problem;
        IsStale = stale;
        if (change is not null)
        {
            LastChange = change;
            ChangeNumber++;
            _logger.LogInformation(
                "RR Lounge stats changed: MMR {Delta:+0;-0;0}, wins up {Wins}, losses up {Losses}, rank {Rank}",
                change.MmrDelta,
                change.WinsUp,
                change.LossesUp,
                change.RankDirection
            );
        }

        if (changed)
            Changed?.Invoke();
    }

    // Written only when a number changed, not on every read.
    private void SaveCache(LoungeStatsSnapshot stats)
    {
        try
        {
            _settings.Set(_settings.LOUNGE_STATS_CACHE, stats.ToJson());
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not keep the RR Lounge stats for next time");
        }
    }

    private void Release()
    {
        lock (_gate)
            _users--;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _config.Changed -= OnConfigChanged;
    }

    private sealed class Lease(LoungeStatsService owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release();
        }
    }
}
