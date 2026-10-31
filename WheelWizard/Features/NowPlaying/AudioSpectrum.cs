using NAudio.Wave;

namespace WheelWizard.NowPlaying;

/// <summary>
/// The sound bars' levels, from what the PC is playing (Windows loopback capture). The audio is split into bands spaced
/// by octave from 30 Hz to 12 kHz, so the left bars are the kick and bass and the right ones the hi-hats. A band jumps
/// up at once on a hit and falls back smoothly. The in-app card and the OBS page read the same levels, so they move
/// together. It runs only while something asks for levels and stops by itself a few seconds after the last ask.
/// </summary>
public sealed class AudioSpectrum : IDisposable
{
    private const int FftSize = 4096;
    private const double LowHz = 30;
    private const double HighHz = 12000;

    // How far a band falls per second when the sound drops (the full height is 1), and the loudness span shown.
    private const double FallPerSecond = 2.2;
    private const double RangeDb = 42;

    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly float[] _ring = new float[FftSize];
    private readonly double[] _window = new double[FftSize];
    private int _ringAt;
    private int _sinceLast;
    private int _sampleRate = 48000;
    private int _hop = 800;

    private WasapiLoopbackCapture? _capture;
    private DateTime _lastAsked;
    private DateTime _lastFrame;
    private double[] _levels = [];
    private double _topDb = -30;
    private readonly Timer _idle;

    /// <summary>The one both the card and the OBS page read, so they show the same levels.</summary>
    public static AudioSpectrum Shared { get; } = new();

    private AudioSpectrum()
    {
        for (var i = 0; i < FftSize; i++)
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
        _idle = new Timer(_ => StopWhenUnused(), null, IdleAfter, IdleAfter);
    }

    /// <summary>An analyser fed by hand (<see cref="Feed"/>) instead of the PC's sound, for tests.</summary>
    public AudioSpectrum(int sampleRate)
        : this()
    {
        _sampleRate = sampleRate;
        _hop = Math.Max(256, sampleRate / 60);
    }

    /// <summary>Analyses mono samples as if they had just played, for <paramref name="count"/> bars, and returns the levels.</summary>
    public double[] Feed(ReadOnlySpan<float> mono, int count)
    {
        lock (_lock)
        {
            if (_levels.Length != count)
                _levels = new double[count];
            foreach (var sample in mono)
                Push(sample);
            return (double[])_levels.Clone();
        }
    }

    /// <summary>False where loopback capture isn't there (not Windows); the bars then use their own animation.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// The current level of each of <paramref name="count"/> bars, 0 to 1, or null while there is no sound data yet.
    /// Starts listening when needed.
    /// </summary>
    public double[]? Levels(int count)
    {
        if (!IsSupported || count <= 0)
            return null;

        lock (_lock)
        {
            _lastAsked = DateTime.UtcNow;
            if (_capture is null)
                Start();
            if (_levels.Length != count)
                _levels = new double[count];
            // Nothing new for a while (the PC is silent and Windows sends no audio): let the bars fall.
            if (DateTime.UtcNow - _lastFrame > TimeSpan.FromMilliseconds(100))
                Fall((DateTime.UtcNow - _lastFrame).TotalSeconds);
            return _capture is null ? null : (double[])_levels.Clone();
        }
    }

    private void Start()
    {
        try
        {
            var capture = new WasapiLoopbackCapture();
            _sampleRate = capture.WaveFormat.SampleRate;
            // About 60 analyses a second.
            _hop = Math.Max(256, _sampleRate / 60);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, _) =>
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_capture, capture))
                        _capture = null;
                }
                capture.Dispose();
            };
            capture.StartRecording();
            _capture = capture;
            _lastFrame = DateTime.UtcNow;
        }
        catch (Exception)
        {
            // No playback device, or Windows refused: the bars keep their own animation.
            _capture = null;
        }
    }

    private void StopWhenUnused()
    {
        WasapiLoopbackCapture? capture;
        lock (_lock)
        {
            if (_capture is null || DateTime.UtcNow - _lastAsked < IdleAfter)
                return;
            capture = _capture;
            _capture = null;
        }
        try
        {
            capture.StopRecording();
        }
        catch (Exception)
        {
            capture.Dispose();
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var format = ((WasapiLoopbackCapture)sender!).WaveFormat;
        var channels = Math.Max(1, format.Channels);
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || format.BitsPerSample == 32;
        var frameBytes = format.BlockAlign;
        if (frameBytes <= 0)
            return;

        lock (_lock)
        {
            for (var at = 0; at + frameBytes <= e.BytesRecorded; at += frameBytes)
            {
                // Mono: the channels averaged.
                double sum = 0;
                for (var c = 0; c < channels; c++)
                {
                    sum += isFloat ? BitConverter.ToSingle(e.Buffer, at + c * 4) : BitConverter.ToInt16(e.Buffer, at + c * 2) / 32768.0;
                }
                Push((float)(sum / channels));
            }
        }
    }

    private void Push(float sample)
    {
        _ring[_ringAt] = sample;
        _ringAt = (_ringAt + 1) % FftSize;
        if (++_sinceLast >= _hop)
        {
            _sinceLast = 0;
            Analyse();
        }
    }

    private void Analyse()
    {
        var now = DateTime.UtcNow;
        var elapsed = Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.25);
        _lastFrame = now;
        if (_levels.Length == 0)
            return;

        var re = new double[FftSize];
        var im = new double[FftSize];
        for (var i = 0; i < FftSize; i++)
            re[i] = _ring[(_ringAt + i) % FftSize] * _window[i];
        Fft(re, im);

        // Each bar's loudness: the strongest frequency in its band. A full-scale tone reads 0 dB.
        var count = _levels.Length;
        var binHz = (double)_sampleRate / FftSize;
        var db = new double[count];
        var loudest = double.MinValue;
        for (var b = 0; b < count; b++)
        {
            var from = LowHz * Math.Pow(HighHz / LowHz, (double)b / count);
            var to = LowHz * Math.Pow(HighHz / LowHz, (double)(b + 1) / count);
            var first = Math.Max(1, (int)Math.Floor(from / binHz));
            var last = Math.Max(first, Math.Min(FftSize / 2 - 1, (int)Math.Ceiling(to / binHz) - 1));
            double peak = 0;
            for (var k = first; k <= last; k++)
                peak = Math.Max(peak, Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
            db[b] = 20 * Math.Log10(peak / (FftSize / 4.0) + 1e-9);
            loudest = Math.Max(loudest, db[b]);
        }

        // The top follows the loudest band at once and sinks slowly, so quiet and loud songs both fill the bars.
        _topDb = loudest > _topDb ? loudest : Math.Max(loudest, _topDb - 6 * elapsed);
        var top = Math.Max(_topDb, -45);

        for (var b = 0; b < count; b++)
        {
            var level = Math.Clamp((db[b] - (top - RangeDb)) / RangeDb, 0, 1);
            // Squared, so a hit stands out from the sound around it.
            level *= level;
            _levels[b] = level >= _levels[b] ? level : Math.Max(level, _levels[b] - FallPerSecond * elapsed);
        }
    }

    private void Fall(double seconds)
    {
        for (var b = 0; b < _levels.Length; b++)
            _levels[b] = Math.Max(0, _levels[b] - FallPerSecond * Math.Min(seconds, 0.25));
        _lastFrame = DateTime.UtcNow;
    }

    // In-place radix-2 FFT.
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var wRe = Math.Cos(angle);
            var wIm = Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                double curRe = 1,
                    curIm = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var aRe = re[i + k];
                    var aIm = im[i + k];
                    var bRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm;
                    var bIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe;
                    re[i + k] = aRe + bRe;
                    im[i + k] = aIm + bIm;
                    re[i + k + len / 2] = aRe - bRe;
                    im[i + k + len / 2] = aIm - bIm;
                    var next = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = next;
                }
            }
        }
    }

    public void Dispose()
    {
        _idle.Dispose();
        lock (_lock)
        {
            _capture?.StopRecording();
            _capture = null;
        }
    }
}
