namespace WheelWizard.NowPlaying;

/// <summary>
/// The PowerShell the media reader runs. Every second it looks at the Windows media sessions (one per player), takes the
/// one that is playing (or the one Windows shows), and prints a JSON line when the song, the state or the picture changed,
/// or when the playback position jumped (a skip inside the song). In a browser it can take only music: media that names an
/// album, or media from a tab of a music website (the tab names are read through Windows' accessibility, which Chromium
/// browsers answer), so a YouTube video is not taken for a song. It stops by itself when Wheel Wizard+ is gone.
/// </summary>
public static class MediaSessionScript
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
        $browserMode = '__BROWSER_MODE__'
        $musicSites = __MUSIC_SITES__
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $tabCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
        $browserWords = __BROWSERS__
        function IsBrowser($id) { $lower = ([string]$id).ToLower(); foreach ($word in $browserWords) { if ($lower.Contains($word)) { return $true } } return $false }
        # The names of the open browser tabs, as the browser tells Windows (they end with ', Audio playing' while one makes sound).
        function BrowserTabs() {
          $names = @()
          foreach ($proc in (Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and (IsBrowser $_.ProcessName) })) {
            try {
              $window = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
              foreach ($tab in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCondition)) { $names += $tab.Current.Name.ToLower() }
            } catch { }
          }
          return $names
        }
        # A tab is a music site's when a part of its title starts or ends with the site's name ("... | YouTube Music"), so a
        # video that only mentions Spotify in its title does not count.
        function IsMusicTab($name) {
          $title = $name
          $cut = $title.IndexOf(' - memory usage')
          if ($cut -ge 0) { $title = $title.Substring(0, $cut) }
          $parts = @(' | ', ' - ', (' ' + [char]0x2013 + ' '), (' ' + [char]0x2014 + ' '), (' ' + [char]0x2022 + ' '), ': ')
          foreach ($segment in $title.Split($parts, [StringSplitOptions]::RemoveEmptyEntries)) {
            $segment = $segment.Trim()
            foreach ($site in $musicSites) { if ($segment.StartsWith($site) -or $segment.EndsWith($site)) { return $true } }
          }
          return $false
        }
        # A music website is open, and when the session is playing, one of them is the tab making sound.
        function HasMusicTab($playing) {
          $tabs = BrowserTabs
          $music = @($tabs | Where-Object { IsMusicTab $_ })
          if ($music.Count -eq 0) { return $false }
          if (-not $playing) { return $true }
          if (@($tabs | Where-Object { $_.Contains('audio playing') }).Count -eq 0) { return $true }
          return @($music | Where-Object { $_.Contains('audio playing') }).Count -gt 0
        }
        # A session in a browser is used when the mode says everything, or in music mode when it names an album (Spotify
        # and Apple Music on the web do) or comes from a music website's tab (YouTube Music); a YouTube video is neither.
        function Allowed($s) {
          if (-not (IsBrowser $s.SourceAppUserModelId)) { return $true }
          if ($browserMode -eq 'all') { return $true }
          if ($browserMode -eq 'ignore') { return $false }
          try {
            $named = Await ($s.TryGetMediaPropertiesAsync()) $properties
            if (-not [string]::IsNullOrWhiteSpace($named.AlbumTitle)) { return $true }
            return HasMusicTab ($s.GetPlaybackInfo().PlaybackStatus -eq 'Playing')
          } catch { return $false }
        }
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
              if (-not (Allowed $s)) { continue }
              if ($s.GetPlaybackInfo().PlaybackStatus -eq 'Playing') { $session = $s; break }
            }
            if ($null -eq $session) {
              $current = $mgr.GetCurrentSession()
              if ($null -ne $current -and (Allowed $current)) { $session = $current }
              else {
                foreach ($s in $mgr.GetSessions()) { if (Allowed $s) { $session = $s; break } }
              }
            }
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
