using System.IO.Abstractions;
using System.IO.Compression;
using System.Text.RegularExpressions;
using WheelWizard.ApplicationData;
using WheelWizard.Shared.Platform;

namespace WheelWizard.DiscImages;

public interface IWitDownloader
{
    /// <summary>True where Wiimm's site has a ready-made package (Windows); elsewhere the download page is opened instead.</summary>
    bool CanDownload { get; }

    /// <summary>The Wiimm's ISO Tool downloaded earlier, or null.</summary>
    string? Installed();

    /// <summary>Downloads the newest Windows package from wit.wiimm.de into Wheel Wizard+'s own folder and returns the path of wit.exe.</summary>
    Task<OperationResult<string>> DownloadAsync(IProgress<double>? progress, CancellationToken cancellationToken);
}

public sealed partial class WitDownloader(
    IFileSystem fileSystem,
    IApplicationDataLocation location,
    IRuntimeEnvironment environment,
    HttpClient? http = null
) : IWitDownloader
{
    public const string Site = "https://wit.wiimm.de";
    public const string DownloadPage = Site + "/download.html";

    // Used when the download page cannot be read or lists no package.
    private const string FallbackZip = "wit-v3.05a-r8638-cygwin64.zip";

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromMinutes(5) };

    [GeneratedRegex(@"/download/(wit-v[0-9a-z.]+-r(?<revision>\d+)-cygwin64\.zip)")]
    private static partial Regex ZipLink();

    public bool CanDownload => environment.IsWindows;

    private string Folder => fileSystem.Path.Combine(location.DirectoryPath, "Tools", "wit");

    public string? Installed() => FindExe(Folder) is { } exe && fileSystem.File.Exists(exe) ? exe : null;

    /// <summary>The newest Windows package named on the download page (highest revision), or null when there is none.</summary>
    public static string? LatestZipName(string? html) =>
        html is null
            ? null
            : ZipLink()
                .Matches(html)
                .OrderByDescending(match => int.Parse(match.Groups["revision"].Value))
                .Select(match => match.Groups[1].Value)
                .FirstOrDefault();

    public async Task<OperationResult<string>> DownloadAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!CanDownload)
            return Fail("There is no ready-made package for this system. Use the download page.");

        var client = http ?? Shared;
        var temp = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "wheelwizard-wit-" + Guid.NewGuid().ToString("N"));
        var part = Folder + ".part";
        try
        {
            string? page = null;
            try
            {
                page = await client.GetStringAsync(DownloadPage, cancellationToken);
            }
            catch (HttpRequestException) { }

            var name = LatestZipName(page) ?? FallbackZip;
            fileSystem.Directory.CreateDirectory(temp);
            var zip = fileSystem.Path.Combine(temp, name);
            using (
                var response = await client.GetAsync($"{Site}/download/{name}", HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            )
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = fileSystem.File.Create(zip);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    done += read;
                    if (total is > 0)
                        progress?.Report(done * 100.0 / total.Value);
                }
            }

            if (fileSystem.Directory.Exists(part))
                fileSystem.Directory.Delete(part, recursive: true);
            fileSystem.Directory.CreateDirectory(part);
            // Extraction refuses any entry that would land outside the folder.
            await using (var stream = fileSystem.File.OpenRead(zip))
                ZipFile.ExtractToDirectory(stream, part, overwriteFiles: true);

            if (FindExe(part) is null)
                return Fail("The download did not contain wit.exe.");

            if (fileSystem.Directory.Exists(Folder))
                fileSystem.Directory.Delete(Folder, recursive: true);
            fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(Folder)!);
            fileSystem.Directory.Move(part, Folder);
            // Not one conditional: string converts to an error here, so a path would come back as a failure.
            if (Installed() is { } installed)
                return installed;
            return Fail("The download could not be installed.");
        }
        catch (OperationCanceledException)
        {
            return Fail("Cancelled.");
        }
        catch (Exception ex)
        {
            return new OperationError { Message = $"Could not download Wiimm's ISO Tool: {ex.Message}", Exception = ex };
        }
        finally
        {
            foreach (var folder in new[] { temp, part })
            {
                try
                {
                    if (fileSystem.Directory.Exists(folder))
                        fileSystem.Directory.Delete(folder, recursive: true);
                }
                catch (IOException) { }
            }
        }
    }

    private string? FindExe(string folder) =>
        fileSystem.Directory.Exists(folder)
            ? fileSystem.Directory.EnumerateFiles(folder, "wit.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
}
