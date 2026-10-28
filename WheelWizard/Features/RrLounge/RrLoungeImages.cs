using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using WheelWizard.ApplicationData;

namespace WheelWizard.RrLounge;

/// <summary>
/// The pictures RR Lounge shows (rank badges from rr-lounge.com, flags from flagcdn.com), kept on disk so they download
/// once rather than on every launch. A badge is saved as a small copy: the site's are 2600 pixels wide, about 1.4 MB
/// each, and the launcher never shows them bigger than 72. Saved pictures are checked again after a month, and a
/// picture that cannot be downloaded falls back to the saved copy.
/// </summary>
public sealed partial class RrLoungeImages(IApplicationDataLocation? location)
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    // One download per picture per run, shared by every place that shows it.
    private readonly Dictionary<string, Task<Bitmap?>> _loaded = [];

    /// <summary>Where the pictures are saved, or null to keep them in memory only.</summary>
    public string? Folder => location is null ? null : Path.Combine(location.DirectoryPath, "rr-lounge");

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex Unsafe();

    /// <summary>"rr-lounge.com_icons_new_diamond.png" for the badge's address: a plain file name, one per picture.</summary>
    public static string FileName(string url)
    {
        var uri = new Uri(url);
        return Unsafe().Replace((uri.Host + uri.AbsolutePath).ToLowerInvariant(), "_").Trim('_');
    }

    /// <summary>The picture, from disk when it is there and recent, else downloaded (and shrunk to <paramref name="width"/> when given).</summary>
    public Task<Bitmap?> GetAsync(string url, int width)
    {
        if (!_loaded.TryGetValue(url, out var task))
            _loaded[url] = task = LoadAsync(url, width);
        return task;
    }

    private async Task<Bitmap?> LoadAsync(string url, int width)
    {
        var file = Folder is { } folder ? Path.Combine(folder, FileName(url)) : null;
        var saved = file is not null && File.Exists(file) ? file : null;
        if (saved is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(saved) < RefreshAfter && Read(saved) is { } fresh)
            return fresh;

        try
        {
            var bytes = await Client.GetByteArrayAsync(url);
            using var stream = new MemoryStream(bytes);
            var bitmap = width > 0 ? Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality) : new Bitmap(stream);
            if (file is not null)
                Save(bitmap, file);
            return bitmap;
        }
        catch (Exception)
        {
            // Offline or gone: the copy from last time, if any; else the text next to it still shows.
            return saved is not null ? Read(saved) : null;
        }
    }

    private static Bitmap? Read(string file)
    {
        try
        {
            return new Bitmap(file);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Save(Bitmap bitmap, string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var staging = file + ".part";
            bitmap.Save(staging, new PngBitmapEncoderOptions());
            File.Move(staging, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not saved this time; it is downloaded again next launch.
        }
    }
}
