using System.Text.Json;

namespace WheelWizard.NowPlaying;

/// <summary>The song the PC is playing: what Windows shows in its media controls, whichever app is playing it.</summary>
public sealed record NowPlayingInfo(
    string Title,
    string Artist,
    string Album,
    string App,
    bool IsPlaying,
    byte[]? Cover,
    double Position = 0,
    double Duration = 0,
    DateTimeOffset? At = null
)
{
    /// <summary>How far into the song it is at <paramref name="now"/>: the position when it was read, moving on while it plays.</summary>
    public double ElapsedAt(DateTimeOffset now)
    {
        var elapsed = Position + (IsPlaying && At is { } read ? Math.Max(0, (now - read).TotalSeconds) : 0);
        return Duration > 0 ? Math.Clamp(elapsed, 0, Duration) : Math.Max(0, elapsed);
    }

    public bool HasLength => Duration > 0;

    /// <summary>"3:07", or "1:02:09" for a long one.</summary>
    public static string FormatTime(double seconds)
    {
        var total = (int)Math.Max(0, seconds);
        return total >= 3600 ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}" : $"{total / 60}:{total % 60:00}";
    }

    /// <summary>One line of the media reader's output, or null for an empty one (nothing is playing) or one that is not valid.</summary>
    public static NowPlayingInfo? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var title = Text(root, "title");
            if (title.Length == 0)
                return null;

            byte[]? cover = null;
            if (Text(root, "cover") is { Length: > 0 } encoded)
            {
                try
                {
                    cover = Convert.FromBase64String(encoded);
                }
                catch (FormatException) { }
            }

            return new NowPlayingInfo(
                title,
                Text(root, "artist"),
                Text(root, "album"),
                FriendlyApp(Text(root, "app")),
                root.TryGetProperty("playing", out var playing) && playing.ValueKind == JsonValueKind.True,
                cover,
                Number(root, "position"),
                Number(root, "duration"),
                DateTimeOffset.UtcNow
            );
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
        && double.IsFinite(number)
            ? Math.Max(0, number)
            : 0;

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : string.Empty;

    /// <summary>Parts of a media session's app name that mean a web browser (YouTube, SoundCloud and the like play in one).</summary>
    public static readonly string[] BrowserWords = ["chrome", "msedge", "edge", "opera", "firefox", "brave", "vivaldi"];

    /// <summary>True when the app a media session belongs to is a web browser.</summary>
    public static bool IsBrowser(string appId) => BrowserWords.Any(word => appId.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] KnownApps =
    [
        "spotify",
        "soundcloud",
        "youtubemusic",
        "applemusic",
        "itunes",
        "tidal",
        "deezer",
        "amazonmusic",
        "vlc",
        "foobar2000",
        "musicbee",
        "winamp",
        "chrome",
        "msedge",
        "opera",
        "firefox",
        "brave",
        "vivaldi",
    ];

    /// <summary>The name of the app a media session belongs to ("Spotify.exe" becomes "Spotify"), or empty when it is not known.</summary>
    public static string FriendlyApp(string appId)
    {
        // Spaces, dots and dashes differ between apps ("AppleInc.AppleMusicWin", "Apple Music"); only the letters count.
        var id = new string(appId.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return KnownApps.FirstOrDefault(id.Contains) switch
        {
            null => string.Empty,
            "msedge" => "Edge",
            "itunes" => "iTunes",
            "vlc" => "VLC",
            "applemusic" => "Apple Music",
            "youtubemusic" => "YouTube Music",
            "amazonmusic" => "Amazon Music",
            "foobar2000" => "foobar2000",
            { } name => string.Join(' ', name.Split(' ').Select(word => char.ToUpperInvariant(word[0]) + word[1..])),
        };
    }
}
