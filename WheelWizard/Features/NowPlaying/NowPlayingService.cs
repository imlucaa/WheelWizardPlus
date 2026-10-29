namespace WheelWizard.NowPlaying;

/// <summary>
/// The song that is playing now. The media reader only runs while something looks at it: an open Now playing window or
/// settings tab (<see cref="Use"/>), or an OBS page that asked recently (<see cref="Touch"/>); otherwise nothing runs.
/// </summary>
public sealed class NowPlayingService : IDisposable
{
    private static readonly TimeSpan TouchLasts = TimeSpan.FromSeconds(30);

    private readonly IMediaSessionReader _reader;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly ITimer _idle;
    private int _users;
    private DateTimeOffset _touchedUntil = DateTimeOffset.MinValue;

    private readonly NowPlayingConfig? _config;

    public NowPlayingService(IMediaSessionReader reader, TimeProvider time, NowPlayingConfig? config = null)
    {
        _reader = reader;
        _time = time;
        _config = config;
        if (config is not null)
        {
            ApplyConfig();
            config.Changed += ApplyConfig;
        }

        _reader.Changed += OnChanged;
        _reader.ProblemChanged += OnProblemChanged;
        _idle = time.CreateTimer(_ => StopWhenUnused(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public bool IsSupported => _reader.IsSupported;

    /// <summary>Why the song cannot be read right now (PowerShell blocked or stopped), or null.</summary>
    public string? Problem => _reader.Problem;

    /// <summary>The song, or null when nothing is playing or nothing has been read yet.</summary>
    public NowPlayingInfo? Current { get; private set; }

    /// <summary>Raised on a background thread when <see cref="Current"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Keeps the reader running until the returned lease is disposed.</summary>
    public IDisposable Use()
    {
        lock (_gate)
            _users++;
        _reader.Start();
        return new Lease(this);
    }

    /// <summary>A request from outside the app (the OBS page): keeps the reader running for a little while longer.</summary>
    public void Touch()
    {
        lock (_gate)
            _touchedUntil = _time.GetUtcNow() + TouchLasts;
        _reader.Start();
    }

    private void OnProblemChanged() => Changed?.Invoke();

    // What is taken from web browsers (nothing, only music, or everything) follows the option.
    private void ApplyConfig() => _reader.BrowserMode = _config?.GetString("behavior.browsers") ?? "music only";

    private void OnChanged(NowPlayingInfo? info)
    {
        Current = info;
        Changed?.Invoke();
    }

    private void Release()
    {
        lock (_gate)
            _users--;
        StopWhenUnused();
    }

    private void StopWhenUnused()
    {
        lock (_gate)
        {
            if (_users > 0 || _time.GetUtcNow() < _touchedUntil)
                return;
        }

        _reader.Stop();
        if (Current is not null)
        {
            Current = null;
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _idle.Dispose();
        if (_config is not null)
            _config.Changed -= ApplyConfig;
        _reader.Changed -= OnChanged;
        _reader.ProblemChanged -= OnProblemChanged;
        _reader.Dispose();
    }

    private sealed class Lease(NowPlayingService owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release();
        }
    }
}
