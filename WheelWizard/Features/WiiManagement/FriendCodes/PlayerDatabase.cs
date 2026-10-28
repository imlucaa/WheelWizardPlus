using System.IO.Abstractions;
using System.Text.Json;
using WheelWizard.ApplicationData;
using WheelWizard.Shared.Downloads;

namespace WheelWizard.WiiManagement.FriendCodes;

/// <summary>One player in the community player database.</summary>
public sealed record PlayerDatabaseEntry(string FriendCode, string Name, string ProfileId, bool Banned);

/// <summary>
/// The community player database (github.com/ImpactCoding/rr-player-database): every friend code seen on Retro Rewind,
/// including players who no longer show up online. One of the checks before taking a friend code.
/// </summary>
public interface IPlayerDatabase
{
    /// <summary>True when a recent copy is already on disk, so a search does not have to download anything.</summary>
    bool IsReady { get; }

    /// <summary>The entry for a friend code, or null when the database does not have it.</summary>
    Task<OperationResult<PlayerDatabaseEntry?>> FindAsync(
        string friendCode,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// The list is about 29 MB, so it is downloaded once and kept in the data folder for a week; a copy that cannot be renewed
/// (offline) is still used. It is searched as it is read, so it is never held as objects.
/// </summary>
public sealed class PlayerDatabase(IDownloadService downloads, IApplicationDataLocation location, IFileSystem files, TimeProvider time)
    : IPlayerDatabase
{
    public const string Url = "https://raw.githubusercontent.com/ImpactCoding/rr-player-database/main/rr-players.json";
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);

    private string FilePath => files.Path.Combine(location.DirectoryPath, "player-database", "rr-players.json");

    public bool IsReady => files.File.Exists(FilePath) && Age(FilePath) < RefreshAfter;

    private TimeSpan Age(string path) => time.GetUtcNow().UtcDateTime - files.File.GetLastWriteTimeUtc(path);

    public async Task<OperationResult<PlayerDatabaseEntry?>> FindAsync(
        string friendCode,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        var path = FilePath;
        var haveCopy = files.File.Exists(path);
        if (!haveCopy || Age(path) >= RefreshAfter)
        {
            var download = await downloads.DownloadAsync(Url, path, useExactPath: true, progress, cancellationToken);
            if (download.IsFailure && !haveCopy)
                return Fail("The player database could not be downloaded. Check your connection and try again.");
        }

        try
        {
            var bytes = await files.File.ReadAllBytesAsync(path, cancellationToken);
            return await Task.Run(() => Search(bytes, friendCode), cancellationToken);
        }
        catch (JsonException)
        {
            // A damaged copy would fail every time: remove it so the next search downloads it again.
            TryDelete(path);
            return Fail("The player database was damaged. Try again to download it anew.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("The player database could not be read.");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            files.File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Reads the top-level object, which has one property per friend code, and stops at the one asked for.</summary>
    private static OperationResult<PlayerDatabaseEntry?> Search(byte[] json, string friendCode)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("The player database is not a list of players.");

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!reader.ValueTextEquals(friendCode))
            {
                reader.Read();
                reader.Skip();
                continue;
            }

            reader.Read();
            using var entry = JsonDocument.ParseValue(ref reader);
            var root = entry.RootElement;
            return new PlayerDatabaseEntry(
                friendCode,
                Text(root, "name"),
                Text(root, "pid"),
                root.TryGetProperty("banned", out var banned) && banned.ValueKind == JsonValueKind.True
            );
        }

        return Ok<PlayerDatabaseEntry?>(null);
    }

    private static string Text(JsonElement entry, string property) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
