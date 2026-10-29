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

    public NowPlayingService(IMediaSessionReader reader, TimeProvider time)
    {
        _reader = reader;
        _time = time;
        _reader.Changed += OnChanged;
        _idle = time.CreateTimer(_ => StopWhenUnused(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public bool IsSupported => _reader.IsSupported;

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
        _reader.Changed -= OnChanged;
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
