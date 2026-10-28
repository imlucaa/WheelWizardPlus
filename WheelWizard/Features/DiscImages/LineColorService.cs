using System.IO.Abstractions;
using WheelWizard.Settings;
using WheelWizard.Shared.Platform;

namespace WheelWizard.DiscImages;

public interface ILineColorService
{
    /// <summary>The disc formats Wiimm's ISO Tool can take apart and put back together.</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>The saved Wiimm's ISO Tool, or the first one found in the usual places; null when there is none.</summary>
    string? FindWit();

    /// <summary>Checks that a file is Wiimm's ISO Tool and remembers it.</summary>
    Task<OperationResult> SetWitAsync(string path);

    /// <summary>Reads the colour a disc gives now, for the popup to show; null when it cannot be told.</summary>
    Task<LineColor?> CurrentAsync(string discPath, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the disc apart in a temporary folder, changes the region ID in <c>main.dol</c>, and builds the disc again,
    /// either over the original or as a copy next to it. Returns where the new disc is.
    /// </summary>
    Task<OperationResult<string>> ChangeAsync(
        string discPath,
        LineColor color,
        bool replaceOriginal,
        IProgress<string>? status,
        CancellationToken cancellationToken
    );
}

public sealed class LineColorService(
    IFileSystem fileSystem,
    IDiscImageService discs,
    IWitRunner wit,
    ISettingsManager settings,
    IRuntimeEnvironment environment
) : ILineColorService
{
    // A Mario Kart Wii disc is about 4.4 GB; the folder holds all of it, and so does the new disc.
    private const long MinimumFreeBytes = 4_800_000_000;

    public IReadOnlyList<string> SupportedExtensions { get; } = [".iso", ".wbfs", ".ciso", ".wia"];

    public string? FindWit()
    {
        var saved = settings.Get<string>(settings.WIT_LOCATION);
        if (!string.IsNullOrWhiteSpace(saved) && fileSystem.File.Exists(saved))
            return saved;

        return Candidates().FirstOrDefault(fileSystem.File.Exists);
    }

    private IEnumerable<string> Candidates()
    {
        var name = environment.IsWindows ? "wit.exe" : "wit";
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in path.Split(fileSystem.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return fileSystem.Path.Combine(folder.Trim('"'), name);

        if (environment.IsWindows)
        {
            foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                yield return fileSystem.Path.Combine(Environment.GetFolderPath(root), "Wiimm", "WIT", name);
        }
        else
        {
            yield return "/usr/local/bin/wit";
            yield return "/usr/bin/wit";
            yield return "/opt/homebrew/bin/wit";
            yield return fileSystem.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "bin", "wit");
        }
    }

    public async Task<OperationResult> SetWitAsync(string path)
    {
        if (!fileSystem.File.Exists(path))
            return Fail("That file does not exist.");

        try
        {
            var result = await wit.RunAsync(path, ["version"], null, CancellationToken.None);
            if (result.ExitCode != 0 || !result.Output.Contains("ISO Tool", StringComparison.OrdinalIgnoreCase))
                return Fail("That is not Wiimm's ISO Tool (wit).");
        }
        catch (Exception ex)
        {
            return Fail($"Could not start that file: {ex.Message}");
        }

        settings.Set(settings.WIT_LOCATION, path);
        return Ok();
    }

    public async Task<LineColor?> CurrentAsync(string discPath, CancellationToken cancellationToken)
    {
        var tool = FindWit();
        var info = discs.ReadInfo(discPath);
        if (tool is null || info.IsFailure || !RegionLine.Supports(info.Value.GameId))
            return null;

        var folder = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), $"wheelwizard-line-{Guid.NewGuid():N}");
        try
        {
            // Only main.dol comes out, so this takes a moment, not minutes.
            var run = await wit.RunAsync(
                tool,
                ["extract", discPath, fileSystem.Path.GetFileName(folder), "--psel", "data", "--files", "+sys/main.dol"],
                fileSystem.Path.GetDirectoryName(folder),
                cancellationToken
            );
            var dol = run.ExitCode == 0 ? FindDol(folder) : null;
            return dol is null ? null : RegionLine.Read(fileSystem.File.ReadAllBytes(dol), info.Value.GameId);
        }
        finally
        {
            Delete(folder);
        }
    }

    public async Task<OperationResult<string>> ChangeAsync(
        string discPath,
        LineColor color,
        bool replaceOriginal,
        IProgress<string>? status,
        CancellationToken cancellationToken
    )
    {
        var tool = FindWit();
        if (tool is null)
            return Fail("Wiimm's ISO Tool was not found.");

        var info = discs.ReadInfo(discPath);
        if (info.IsFailure)
            return info.Error;
        if (!RegionLine.Supports(info.Value.GameId))
            return Fail("That does not look like a Mario Kart Wii disc.");

        var extension = fileSystem.Path.GetExtension(discPath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
            return Fail("Wiimm's ISO Tool cannot read this kind of file. Turn the disc into an ISO or WBFS first.");

        var folder = fileSystem.Path.GetDirectoryName(fileSystem.Path.GetFullPath(discPath))!;
        var stem = fileSystem.Path.GetFileNameWithoutExtension(discPath);
        var work = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), $"wheelwizard-line-{Guid.NewGuid():N}");
        var built = fileSystem.Path.Combine(folder, $".wheelwizard-line-{Guid.NewGuid():N}{extension}");
        try
        {
            var space = EnoughSpace(fileSystem.Path.GetTempPath(), folder);
            if (space is not null)
                return space;

            status?.Report("Taking the disc apart...");
            var extract = await wit.RunAsync(
                tool,
                ["extract", discPath, fileSystem.Path.GetFileName(work)],
                fileSystem.Path.GetDirectoryName(work),
                cancellationToken
            );
            if (extract.ExitCode != 0)
                return Fail($"Wiimm's ISO Tool could not take the disc apart.\n{LastLines(extract.Output)}");

            var dol = FindDol(work);
            if (dol is null)
                return Fail("There is no main.dol on this disc.");

            status?.Report("Changing the line colour...");
            var bytes = fileSystem.File.ReadAllBytes(dol);
            var patched = RegionLine.Apply(bytes, info.Value.GameId, color);
            if (patched.IsFailure)
                return patched.Error;
            fileSystem.File.WriteAllBytes(dol, bytes);

            status?.Report("Putting the disc back together...");
            var build = await wit.RunAsync(tool, ["copy", work, fileSystem.Path.GetFileName(built)], folder, cancellationToken);
            if (build.ExitCode != 0 || !fileSystem.File.Exists(built))
                return Fail($"Wiimm's ISO Tool could not build the disc.\n{LastLines(build.Output)}");

            return replaceOriginal ? Replace(discPath, built) : Keep(built, folder, stem, extension, color);
        }
        catch (OperationCanceledException)
        {
            return Fail("Cancelled. The disc was not changed.");
        }
        catch (Exception ex)
        {
            return new OperationError { Message = $"Could not change the line colour: {ex.Message}", Exception = ex };
        }
        finally
        {
            Delete(work);
            if (fileSystem.File.Exists(built))
                fileSystem.File.Delete(built);
        }
    }

    private OperationResult<string> Replace(string discPath, string built)
    {
        // The original only goes once the new disc is in place.
        var aside = discPath + "." + Guid.NewGuid().ToString("N") + ".old";
        fileSystem.File.Move(discPath, aside);
        try
        {
            fileSystem.File.Move(built, discPath);
        }
        catch
        {
            fileSystem.File.Move(aside, discPath);
            throw;
        }

        fileSystem.File.Delete(aside);
        return discPath;
    }

    private OperationResult<string> Keep(string built, string folder, string stem, string extension, LineColor color)
    {
        var name = $"{stem} ({color.ToString().ToLowerInvariant()} line)";
        var target = fileSystem.Path.Combine(folder, name + extension);
        for (var number = 2; fileSystem.File.Exists(target); number++)
            target = fileSystem.Path.Combine(folder, $"{name} {number}{extension}");

        fileSystem.File.Move(built, target);
        return target;
    }

    private OperationError? EnoughSpace(string temp, string folder)
    {
        foreach (var place in new[] { temp, folder })
        {
            var free = FreeBytes(place);
            if (free is { } bytes && bytes < MinimumFreeBytes)
                return Fail(
                    $"There is not enough room on the drive of {place}. About {MinimumFreeBytes / 1_000_000_000d:0.#} GB is needed."
                );
        }

        return null;
    }

    private long? FreeBytes(string path)
    {
        try
        {
            return fileSystem.DriveInfo.New(fileSystem.Path.GetPathRoot(fileSystem.Path.GetFullPath(path))!).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    // Taking a whole disc apart gives a DATA folder (and a few more); taking only the data partition gives sys directly.
    private string? FindDol(string folder)
    {
        foreach (var candidate in new[] { "DATA/sys/main.dol", "sys/main.dol" })
        {
            var path = fileSystem.Path.Combine(folder, candidate);
            if (fileSystem.File.Exists(path))
                return path;
        }

        return null;
    }

    private void Delete(string folder)
    {
        try
        {
            if (fileSystem.Directory.Exists(folder))
                fileSystem.Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // A temporary folder that cannot be removed now is cleaned up with the system's temporary files.
        }
    }

    private static string LastLines(string output) =>
        string.Join("\n", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
}
