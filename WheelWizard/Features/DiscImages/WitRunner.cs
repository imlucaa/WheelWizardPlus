using System.Diagnostics;

namespace WheelWizard.DiscImages;

public sealed record WitRunResult(int ExitCode, string Output);

/// <summary>Runs Wiimm's ISO Tool and waits for it. A seam so the disc steps can be tested without the tool.</summary>
public interface IWitRunner
{
    Task<WitRunResult> RunAsync(string wit, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken);
}

public sealed class WitRunner : IWitRunner
{
    public async Task<WitRunResult> RunAsync(
        string wit,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken
    )
    {
        var info = new ProcessStartInfo(wit)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
            info.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Failed to start {wit}.");
        // Both streams are read as they come in, or a full pipe would stall the tool.
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }

        return new WitRunResult(process.ExitCode, await output + await errors);
    }
}
