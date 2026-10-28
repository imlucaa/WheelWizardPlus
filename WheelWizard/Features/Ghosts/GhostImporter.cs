using System.IO.Abstractions;
using System.Text.RegularExpressions;
using WheelWizard.CustomDistributions;

namespace WheelWizard.Ghosts;

public enum GhostKind
{
    Retro,
    Custom,
}

/// <summary>One track that has a ghost slot: its place in the list (also its file number) and its name.</summary>
public sealed record GhostTrack(GhostKind Kind, int Index, string Name, string Hash)
{
    /// <summary>The folder inside <c>Ghosts</c> that holds this track's ghosts.</summary>
    public string FolderName => Kind == GhostKind.Retro ? "ExpertsRT" : "ExpertsCT";

    public string FileName(int speed) => $"{Index}_{speed}.rkg";
}

public static partial class GhostTrackTable
{
    public const string RetroFile = "FolderToTrackNameRT.txt";
    public const string CustomFile = "FolderToTrackNameCT.txt";

    [GeneratedRegex(@"^(?<name>.*?)\s*=\s*(?<hash>[0-9A-Fa-f]{8})\s*$")]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"\\c\{[^}]*\}")]
    private static partial Regex ColorCodeRegex();

    /// <summary>
    /// Reads a "Track name = HASH" list. A track's file number is its position in the list, counting from zero. Blank
    /// lines are skipped.
    /// </summary>
    public static IReadOnlyList<GhostTrack> Parse(GhostKind kind, string text)
    {
        var tracks = new List<GhostTrack>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            var match = LineRegex().Match(trimmed);
            if (!match.Success)
                continue;

            tracks.Add(
                new GhostTrack(kind, tracks.Count, CleanName(match.Groups["name"].Value), match.Groups["hash"].Value.ToUpperInvariant())
            );
        }

        return tracks;
    }

    /// <summary>The list names carry colour codes for the game's own menu (<c>\c{yellow}SNES \c{off}</c>); the launcher shows plain text.</summary>
    public static string CleanName(string raw) => Regex.Replace(ColorCodeRegex().Replace(raw, string.Empty), @"\s+", " ").Trim();
}

/// <summary>What the launcher reads out of a ghost file's header: the finish time, the track slot and the lap count.</summary>
public sealed record GhostHeader(TimeSpan FinishTime, int CourseId, int Laps);

public static class GhostFile
{
    private const int HeaderSize = 0x88;
    private const int MaxSize = 128 * 1024;

    /// <summary>True for a Mario Kart Wii ghost (RKGD) of a believable size, with its finish time.</summary>
    public static bool TryRead(byte[] data, out TimeSpan finishTime)
    {
        var ok = TryReadHeader(data, out var header);
        finishTime = header?.FinishTime ?? TimeSpan.Zero;
        return ok;
    }

    public static bool TryReadHeader(byte[] data, out GhostHeader? header)
    {
        header = null;
        if (data.Length is < HeaderSize + 8 or > MaxSize || data[0] != 'R' || data[1] != 'K' || data[2] != 'G' || data[3] != 'D')
            return false;

        // 7 bits of minutes, 7 of seconds and 10 of milliseconds.
        var packed = (data[4] << 16) | (data[5] << 8) | data[6];
        var minutes = packed >> 17;
        var seconds = (packed >> 10) & 0x7F;
        var milliseconds = packed & 0x3FF;
        if (seconds > 59 || milliseconds > 999)
            return false;

        // The top six bits of the next byte are the slot the track runs in. Many tracks share a slot, so it narrows the
        // track down without naming it.
        header = new GhostHeader(new TimeSpan(0, 0, minutes, seconds, milliseconds), data[7] >> 2, data[0x10]);
        return true;
    }

    /// <summary>The ghost type of the ghosts Retro Rewind ships in its ghost folders. A ghost you record yourself is type 1.</summary>
    public const int ExpertGhostType = 38;

    /// <summary>
    /// A copy of the ghost marked as an expert ghost, with its checksum updated. The game only offers the ghosts in its
    /// folders when they are expert ghosts, so a ghost you recorded (type "player best time") would be skipped, and the
    /// file ends with a CRC-32 of everything before it that has to match.
    /// </summary>
    public static byte[] ToExpertGhost(byte[] data)
    {
        var result = (byte[])data.Clone();
        // The type is 7 bits: the lowest bit of byte 0x0C on top of the upper six bits of byte 0x0D.
        result[0x0C] = (byte)(result[0x0C] & 0xFE | ExpertGhostType >> 6 & 1);
        result[0x0D] = (byte)(result[0x0D] & 0x03 | (ExpertGhostType & 0x3F) << 2);
        var crc = Crc32(result.AsSpan(0, result.Length - 4));
        result[^4] = (byte)(crc >> 24);
        result[^3] = (byte)(crc >> 16);
        result[^2] = (byte)(crc >> 8);
        result[^1] = (byte)crc;
        return result;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }

    public static string Format(TimeSpan time) => $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}";
}

/// <summary>A track whose current ghost has the same track slot and lap count as a ghost being imported.</summary>
public sealed record GhostMatch(GhostTrack Track, int Speed, TimeSpan CurrentTime)
{
    public string Label => $"{Track.Name} ({Speed}cc, now {GhostFile.Format(CurrentTime)})";
}

public sealed record GhostImportResult(bool Success, string Message, TimeSpan? Replaced = null, TimeSpan? Imported = null);

/// <summary>
/// Puts a ghost you recorded into Retro Rewind's ghost folder, over the one that is there. A ghost file does not say
/// which track it is for (the game picks by file name), so the track is chosen from the game's own track lists.
/// </summary>
public sealed partial class GhostImporter(ICustomDistributionPaths paths, IFileSystem fileSystem)
{
    private const int KeepReplaced = 5;

    public string GhostsFolder => fileSystem.Path.Combine(paths.RetroRewindFolderPath, "Ghosts");

    /// <summary>False until Retro Rewind is installed, since the ghost folders come with it.</summary>
    public bool IsAvailable => fileSystem.Directory.Exists(GhostsFolder);

    public IReadOnlyList<GhostTrack> LoadTracks(GhostKind kind)
    {
        var file = fileSystem.Path.Combine(GhostsFolder, kind == GhostKind.Retro ? GhostTrackTable.RetroFile : GhostTrackTable.CustomFile);
        try
        {
            return fileSystem.File.Exists(file) ? GhostTrackTable.Parse(kind, fileSystem.File.ReadAllText(file)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public string TargetPath(GhostTrack track, int speed) => fileSystem.Path.Combine(GhostsFolder, track.FolderName, track.FileName(speed));

    /// <summary>The speeds this track already has a ghost for. A track with none yet is offered at 150cc.</summary>
    public IReadOnlyList<int> Speeds(GhostTrack track)
    {
        var speeds = new[] { 150, 200 }.Where(speed => fileSystem.File.Exists(TargetPath(track, speed))).ToList();
        return speeds.Count == 0 ? [150] : speeds;
    }

    /// <summary>The finish time of the ghost that is there now, or null when there is none.</summary>
    public TimeSpan? CurrentTime(GhostTrack track, int speed)
    {
        try
        {
            var path = TargetPath(track, speed);
            return fileSystem.File.Exists(path) && GhostFile.TryRead(fileSystem.File.ReadAllBytes(path), out var time) ? time : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The tracks a ghost could be for. A ghost file does not name its track, but its header has the track slot and lap
    /// count, so every track whose current ghost has the same two is a candidate. The ones whose current time is closest
    /// to the new ghost's time come first, since both are usually near the same pace.
    /// </summary>
    public IReadOnlyList<GhostMatch> FindMatches(byte[] ghost)
    {
        if (!GhostFile.TryReadHeader(ghost, out var wanted) || wanted is null)
            return [];

        var matches = new List<GhostMatch>();
        foreach (var kind in new[] { GhostKind.Retro, GhostKind.Custom })
        {
            foreach (var track in LoadTracks(kind))
            {
                foreach (var speed in new[] { 150, 200 })
                {
                    var header = ReadHeader(TargetPath(track, speed));
                    if (header is not null && header.CourseId == wanted.CourseId && header.Laps == wanted.Laps)
                        matches.Add(new GhostMatch(track, speed, header.FinishTime));
                }
            }
        }

        return matches
            .OrderBy(match => Math.Abs(Math.Log(Math.Max(wanted.FinishTime.TotalSeconds, 1) / Math.Max(match.CurrentTime.TotalSeconds, 1))))
            .ToList();
    }

    private GhostHeader? ReadHeader(string path)
    {
        try
        {
            return fileSystem.File.Exists(path) && GhostFile.TryReadHeader(fileSystem.File.ReadAllBytes(path), out var header)
                ? header
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex("(?<![0-9A-Fa-f])[0-9A-Fa-f]{8}(?![0-9A-Fa-f])")]
    private static partial Regex HashRegex();

    /// <summary>Picks the track a file name points at: a track's 8-letter code in the name, or its exact name.</summary>
    public static GhostTrack? GuessTrack(string fileName, IReadOnlyList<GhostTrack> tracks)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        foreach (Match match in HashRegex().Matches(name))
        {
            var byHash = tracks.FirstOrDefault(track => track.Hash.Equals(match.Value, StringComparison.OrdinalIgnoreCase));
            if (byHash is not null)
                return byHash;
        }

        var wanted = Normalize(name);
        return wanted.Length == 0 ? null : tracks.FirstOrDefault(track => Normalize(track.Name) == wanted);
    }

    private static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// Copies the ghost over the track's file. The ghost that was there is kept in a <c>Replaced</c> folder (the last few
    /// per slot) so a wrong pick can be undone by hand.
    /// </summary>
    public GhostImportResult Import(string sourcePath, GhostTrack track, int speed)
    {
        if (!IsAvailable)
            return new(false, "Retro Rewind is not installed yet, so there is no ghost folder.");

        byte[] data;
        try
        {
            data = fileSystem.File.ReadAllBytes(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, "Could not read that file.");
        }

        return ImportData(data, track, speed);
    }

    /// <summary>Puts ghost data (a file's contents, or a download) into the track's slot, the same way as <see cref="Import"/>.</summary>
    public GhostImportResult ImportData(byte[] data, GhostTrack track, int speed)
    {
        if (!IsAvailable)
            return new(false, "Retro Rewind is not installed yet, so there is no ghost folder.");
        if (!GhostFile.TryRead(data, out var importedTime))
            return new(false, "That is not a Mario Kart Wii ghost file.");

        var folder = fileSystem.Path.Combine(GhostsFolder, track.FolderName);
        var target = TargetPath(track, speed);
        try
        {
            fileSystem.Directory.CreateDirectory(folder);
            TimeSpan? replacedTime = null;
            if (fileSystem.File.Exists(target))
            {
                replacedTime = CurrentTime(track, speed);
                KeepCopy(target, track, speed);
            }

            // Written beside the target first, so a failure half way never leaves a broken ghost in the game's folder.
            var temp = target + ".importing";
            fileSystem.File.WriteAllBytes(temp, GhostFile.ToExpertGhost(data));
            fileSystem.File.Move(temp, target, overwrite: true);
            return new(true, $"{track.Name} ({speed}cc) now has your ghost, {GhostFile.Format(importedTime)}.", replacedTime, importedTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, $"Could not write the ghost: {ex.Message}");
        }
    }

    /// <summary>The ghost this slot had before it was last replaced (kept in the <c>Replaced</c> folder), or null when there is none.</summary>
    public (string Path, TimeSpan Time)? Previous(GhostTrack track, int speed)
    {
        var folder = fileSystem.Path.Combine(GhostsFolder, "Replaced", track.FolderName);
        try
        {
            if (!fileSystem.Directory.Exists(folder))
                return null;
            foreach (var file in fileSystem.Directory.EnumerateFiles(folder, $"{track.Index}_{speed}-*.rkg").OrderDescending())
            {
                if (GhostFile.TryRead(fileSystem.File.ReadAllBytes(file), out var time))
                    return (file, time);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Puts the previous ghost of the slot back. The ghost that is there now goes to the <c>Replaced</c> folder in its
    /// place, so doing it again swaps them back.
    /// </summary>
    public GhostImportResult RestorePrevious(GhostTrack track, int speed)
    {
        if (Previous(track, speed) is not { } previous)
            return new(false, "There is no earlier ghost for that track.");

        byte[] data;
        try
        {
            data = fileSystem.File.ReadAllBytes(previous.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, "Could not read the earlier ghost.");
        }

        var result = ImportData(data, track, speed);
        return result.Success
            ? result with
            {
                Message = $"{track.Name} ({speed}cc) is back to {GhostFile.Format(previous.Time)}.",
            }
            : result;
    }

    /// <summary>The file name an exported ghost gets: just the finish time, like "1m34s086.rkg".</summary>
    public string ExportFileName(GhostTrack track, int speed) =>
        CurrentTime(track, speed) is { } finish
            ? $"{(int)finish.TotalMinutes}m{finish.Seconds:00}s{finish.Milliseconds:000}.rkg"
            : $"{track.Index}_{speed}.rkg";

    /// <summary>Saves a copy of the track's ghost, as an ordinary ghost file that can be shared or imported again.</summary>
    public GhostImportResult Export(GhostTrack track, int speed, string destinationPath)
    {
        var source = TargetPath(track, speed);
        try
        {
            if (!fileSystem.File.Exists(source))
                return new(false, "There is no ghost on that track yet.");

            var data = fileSystem.File.ReadAllBytes(source);
            fileSystem.File.WriteAllBytes(destinationPath, data);
            GhostFile.TryRead(data, out var time);
            return new(true, "Saved.", Imported: time);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, $"Could not save the ghost: {ex.Message}");
        }
    }

    private void KeepCopy(string target, GhostTrack track, int speed)
    {
        var replaced = fileSystem.Path.Combine(GhostsFolder, "Replaced", track.FolderName);
        fileSystem.Directory.CreateDirectory(replaced);
        var stem = $"{track.Index}_{speed}";
        var copy = fileSystem.Path.Combine(replaced, $"{stem}-{DateTime.Now:yyyyMMdd-HHmmss}.rkg");
        fileSystem.File.Copy(target, copy, overwrite: true);

        foreach (var old in fileSystem.Directory.EnumerateFiles(replaced, $"{stem}-*.rkg").OrderDescending().Skip(KeepReplaced))
            fileSystem.File.Delete(old);
    }
}
