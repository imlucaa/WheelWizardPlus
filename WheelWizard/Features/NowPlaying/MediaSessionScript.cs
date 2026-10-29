namespace WheelWizard.NowPlaying;

/// <summary>
/// The PowerShell the media reader runs. Every second it looks at the Windows media sessions (one per player), takes the
/// one that is playing (or the one Windows shows), and prints a JSON line when the song, the state or the picture changed,
/// or when the playback position jumped (a skip inside the song). It stops by itself when Wheel Wizard+ is gone.
/// </summary>
internal static class MediaSessionScript
{
    public const string Text = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
        function Await($op, $type) { $t = $asTask.MakeGenericMethod($type).Invoke($null, @($op)); [void]$t.Wait(-1); $t.Result }
        $manager = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]
        $properties = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media.Control, ContentType = WindowsRuntime]
        $streamType = [Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType = WindowsRuntime]
        [void][Windows.Storage.Streams.IInputStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
        $asStream = [System.IO.WindowsRuntimeStreamExtensions].GetMethod('AsStreamForRead', [Type[]]@([Windows.Storage.Streams.IInputStream]))
        $mgr = Await ($manager::RequestAsync()) $manager
        $parent = __PARENT__
        $last = $null
        $cover = $null
        $coverTries = 0
        $emitPosition = 0.0
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $emitClock = 0.0
        while ($true) {
          if (-not (Get-Process -Id $parent -ErrorAction SilentlyContinue)) { exit }
          try {
            $session = $null
            foreach ($s in $mgr.GetSessions()) {
              if ($s.GetPlaybackInfo().PlaybackStatus -eq 'Playing') { $session = $s; break }
            }
            if ($null -eq $session) { $session = $mgr.GetCurrentSession() }
            if ($null -eq $session) {
              if ($last -ne '') { $last = ''; Write-Output '{}' }
            } else {
              $p = Await ($session.TryGetMediaPropertiesAsync()) $properties
              $playing = $session.GetPlaybackInfo().PlaybackStatus -eq 'Playing'
              $key = "$($session.SourceAppUserModelId)|$($p.Title)|$($p.Artist)|$playing"
              if (-not $last -or -not $last.StartsWith($key)) { $cover = $null; $coverTries = 0 }
              if ($null -eq $cover -and $coverTries -lt 4 -and $null -ne $p.Thumbnail) {
                $coverTries++
                try {
                  $raw = Await ($p.Thumbnail.OpenReadAsync()) $streamType
                  $reader = $asStream.Invoke($null, @($raw))
                  $memory = New-Object System.IO.MemoryStream
                  $reader.CopyTo($memory)
                  if ($memory.Length -gt 0 -and $memory.Length -lt 4000000) { $cover = [Convert]::ToBase64String($memory.ToArray()) }
                } catch { }
              }
              $duration = 0.0
              $position = 0.0
              try {
                $timeline = $session.GetTimelineProperties()
                $duration = [math]::Max(0.0, ($timeline.EndTime - $timeline.StartTime).TotalSeconds)
                $position = ($timeline.Position - $timeline.StartTime).TotalSeconds
                if ($playing) { $position += ([DateTimeOffset]::Now - $timeline.LastUpdatedTime).TotalSeconds }
                $position = [math]::Max(0.0, $position)
                if ($duration -gt 0) { $position = [math]::Min($position, $duration) }
              } catch { }
              $now = $clock.Elapsed.TotalSeconds
              $expected = $emitPosition
              if ($playing) { $expected += ($now - $emitClock) }
              $jumped = [math]::Abs($position - $expected) -gt 2.5
              $state = "$key|$($null -ne $cover)"
              if ($state -ne $last -or $jumped) {
                $last = $state
                $emitPosition = $position
                $emitClock = $now
                @{ app = [string]$session.SourceAppUserModelId; playing = $playing; title = [string]$p.Title; artist = [string]$p.Artist; album = [string]$p.AlbumTitle; cover = $cover; position = [math]::Round($position, 1); duration = [math]::Round($duration, 1) } | ConvertTo-Json -Compress | Write-Output
              }
            }
          } catch { }
          Start-Sleep -Milliseconds 1000
        }
        """;
}
