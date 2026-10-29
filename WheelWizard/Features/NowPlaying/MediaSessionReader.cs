using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WheelWizard.NowPlaying;

/// <summary>Follows the media Windows is playing and says when it changes.</summary>
public interface IMediaSessionReader : IDisposable
{
    bool IsSupported { get; }

    /// <summary>Raised for every change; null means nothing is playing any more.</summary>
    event Action<NowPlayingInfo?>? Changed;

    void Start();
    void Stop();
}

/// <summary>
/// Reads the Windows media controls (the ones that show Spotify, SoundCloud in a browser, and most other players) through
/// a small PowerShell script that keeps running and prints a line when the song changes. No extra package or permission is
/// needed, and nothing is read from the apps themselves.
/// </summary>
public sealed class WindowsMediaSessionReader(ILogger<WindowsMediaSessionReader> logger) : IMediaSessionReader
{
    private readonly object _gate = new();
    private Process? _worker;

    public bool IsSupported => OperatingSystem.IsWindows();

    public event Action<NowPlayingInfo?>? Changed;

    public void Start()
    {
        if (!IsSupported)
            return;

        lock (_gate)
        {
            if (_worker is { HasExited: false })
                return;

            try
            {
                var script = MediaSessionScript.Text.Replace("__PARENT__", Environment.ProcessId.ToString());
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

                var worker = new Process { StartInfo = info, EnableRaisingEvents = true };
                worker.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                        Changed?.Invoke(NowPlayingInfo.Parse(e.Data));
                };
                worker.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        logger.LogDebug("Media reader: {Message}", e.Data);
                };
                worker.Start();
                worker.BeginOutputReadLine();
                worker.BeginErrorReadLine();
                _worker = worker;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogDebug(ex, "Could not start the media reader");
            }
        }
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
