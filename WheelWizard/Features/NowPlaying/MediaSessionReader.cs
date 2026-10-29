using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WheelWizard.NowPlaying;

/// <summary>Follows the media Windows is playing and says when it changes.</summary>
public interface IMediaSessionReader : IDisposable
{
    bool IsSupported { get; }

    /// <summary>
    /// What to take from web browsers: "ignore", "music only" (media that names an album) or "everything". Changing it restarts the reader.
    /// </summary>
    string BrowserMode { get; set; }

    /// <summary>Why the media controls cannot be read right now (PowerShell blocked or stopped), or null when all is well.</summary>
    string? Problem { get; }

    /// <summary>Raised for every change; null means nothing is playing any more.</summary>
    event Action<NowPlayingInfo?>? Changed;

    /// <summary>Raised when <see cref="Problem"/> appears or goes away.</summary>
    event Action? ProblemChanged;

    void Start();
    void Stop();
}

/// <summary>
/// Reads the Windows media controls (the ones that show Spotify, SoundCloud in a browser, and most other players) through
/// a small PowerShell script that keeps running and prints a line when the song changes. No extra package or permission is
/// needed, and nothing is read from the apps themselves. When PowerShell cannot run (blocked by security software or a
/// policy, or the script stops), <see cref="Problem"/> says so, and a new try waits <see cref="RetryAfter"/>.
/// </summary>
public sealed class WindowsMediaSessionReader(ILogger<WindowsMediaSessionReader> logger) : IMediaSessionReader
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private Process? _worker;
    private bool _answered;
    private string _lastError = string.Empty;
    private DateTime _failedAt = DateTime.MinValue;

    public bool IsSupported => OperatingSystem.IsWindows();

    public string? Problem { get; private set; }

    private string _browserMode = "music only";

    public string BrowserMode
    {
        get => _browserMode;
        set
        {
            bool running;
            lock (_gate)
            {
                if (_browserMode == value)
                    return;
                _browserMode = value;
                running = _worker is { HasExited: false };
            }

            // The script was started with the old choice, so it starts again with the new one.
            if (running)
            {
                Stop();
                Start();
            }
        }
    }

    public event Action<NowPlayingInfo?>? Changed;

    public event Action? ProblemChanged;

    /// <summary>The words for a script that stopped by itself, with the last thing it printed to its error output.</summary>
    public static string DescribeExit(int exitCode, string? error)
    {
        var detail = (error ?? string.Empty).Trim();
        if (detail.Length > 140)
            detail = detail[..140] + "…";
        return $"Wheel Wizard+ could not read the Windows media controls: PowerShell stopped (code {exitCode}). Security software or a policy may be blocking it."
            + (detail.Length > 0 ? $" ({detail})" : string.Empty);
    }

    /// <summary>The word the script understands for a browser mode: ignore, music or all.</summary>
    public static string ScriptWord(string mode) =>
        mode switch
        {
            "ignore" => "ignore",
            "everything" => "all",
            _ => "music",
        };

    public void Start()
    {
        if (!IsSupported)
            return;

        lock (_gate)
        {
            if (_worker is { HasExited: false })
                return;
            // Without this a blocked script would be started again by every look at the page.
            if (DateTime.UtcNow - _failedAt < RetryAfter)
                return;

            try
            {
                var script = MediaSessionScript
                    .Text.Replace("__PARENT__", Environment.ProcessId.ToString())
                    .Replace("__BROWSER_MODE__", ScriptWord(_browserMode))
                    .Replace("__BROWSERS__", string.Join(",", NowPlayingInfo.BrowserWords.Select(word => $"'{word}'")));
                var info = new ProcessStartInfo("powershell.exe")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-NonInteractive");
                info.ArgumentList.Add("-ExecutionPolicy");
                info.ArgumentList.Add("Bypass");
                info.ArgumentList.Add("-EncodedCommand");
                info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

                _answered = false;
                _lastError = string.Empty;
                var worker = new Process { StartInfo = info, EnableRaisingEvents = true };
                worker.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is null)
                        return;
                    _answered = true;
                    SetProblem(null);
                    Changed?.Invoke(NowPlayingInfo.Parse(e.Data));
                };
                worker.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data))
                        return;
                    _lastError = e.Data;
                    logger.LogDebug("Media reader: {Message}", e.Data);
                };
                worker.Exited += (sender, _) => OnExited((Process)sender!);
                worker.Start();
                worker.BeginOutputReadLine();
                worker.BeginErrorReadLine();
                _worker = worker;
                _ = WatchAsync(worker);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogDebug(ex, "Could not start the media reader");
                Fail("PowerShell could not be started, so the song cannot be read. Security software or a policy may be blocking it.");
            }
        }
    }

    // The script prints a line within a second or two of starting, even with nothing playing; silence means it is stuck or blocked.
    private async Task WatchAsync(Process worker)
    {
        await Task.Delay(AnswerWithin);
        lock (_gate)
        {
            if (!ReferenceEquals(_worker, worker) || _answered)
                return;
        }

        Fail("The Windows media controls did not answer. Security software may be blocking PowerShell.");
    }

    private void OnExited(Process worker)
    {
        lock (_gate)
        {
            // A stop on purpose clears the worker first; anything else is the script dying on its own.
            if (!ReferenceEquals(_worker, worker))
                return;
            _worker = null;
        }

        var code = -1;
        try
        {
            code = worker.ExitCode;
        }
        catch (InvalidOperationException) { }

        logger.LogInformation("The media reader stopped by itself with code {Code}", code);
        Fail(DescribeExit(code, _lastError));
    }

    private void Fail(string problem)
    {
        _failedAt = DateTime.UtcNow;
        SetProblem(problem);
    }

    private void SetProblem(string? problem)
    {
        if (Problem == problem)
            return;
        Problem = problem;
        ProblemChanged?.Invoke();
    }

    public void Stop()
    {
        lock (_gate)
        {
            var worker = _worker;
            _worker = null;
            if (worker is null)
                return;

            try
            {
                if (!worker.HasExited)
                    worker.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogDebug(ex, "Could not stop the media reader");
            }
            finally
            {
                worker.Dispose();
            }
        }
    }

    public void Dispose() => Stop();
}
