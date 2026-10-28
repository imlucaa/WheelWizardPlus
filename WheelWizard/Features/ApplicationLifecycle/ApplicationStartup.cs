using Microsoft.Extensions.Logging;
using WheelWizard.AutoUpdating;
using WheelWizard.GameBanana.InstallRequests;
using WheelWizard.Launching;
using WheelWizard.Models.Enums;
using WheelWizard.Mods;
using WheelWizard.Settings;
using WheelWizard.WheelWizardData;

namespace WheelWizard.ApplicationLifecycle;

public interface IApplicationStartup
{
    Task RunAsync(StartupOptions options, CancellationToken cancellationToken = default);
}

public sealed class ApplicationStartup(
    IModManager mods,
    IModInstallRequestHandler installRequests,
    IAutoUpdaterSingletonService updates,
    IWhWzDataSingletonService badges,
    IApplicationLiveUpdates liveUpdates,
    ISettingsManager settings,
    ILauncherProvider launchers,
    ILogger<ApplicationStartup> logger
) : IApplicationStartup
{
    public async Task RunAsync(StartupOptions options, CancellationToken cancellationToken = default)
    {
        // #todo: pass shutdown cancellation into the startup operations too; cancelling these waits leaves their work running.
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var openedProtocol = !string.IsNullOrWhiteSpace(options.ProtocolArgument);
            if (openedProtocol)
            {
                var reload = await mods.ReloadAsync().WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (reload.IsFailure)
                    logger.LogError(
                        reload.Error.Exception,
                        "Failed to reload mods before opening protocol window: {Message}",
                        reload.Error.Message
                    );
                await installRequests.HandleAsync(options.ProtocolArgument!).WaitAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await updates.CheckForUpdatesAsync().WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await badges.LoadBadgesAsync().WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            liveUpdates.Start();
            cancellationToken.ThrowIfCancellationRequested();

            if (options.LaunchRetroRewind || (!openedProtocol && settings.Get<bool>(settings.LAUNCH_RR_ON_STARTUP)))
            {
                var launcher = launchers.GetActiveLauncher();
                // With an update out, launching now would start the old version every time. The Update button on Home
                // takes care of it, so the automatic launch waits for that. Anything else (offline, unknown) still launches.
                if (await launcher.GetCurrentStatus().WaitAsync(cancellationToken) == WheelWizardStatus.OutOfDate)
                {
                    logger.LogInformation("Retro Rewind has an update, so it was not launched on startup.");
                    return;
                }

                var result = await launcher.Launch().WaitAsync(cancellationToken);
                if (result.IsFailure)
                    logger.LogError(result.Error.Exception, "Failed to launch Retro Rewind on startup: {Message}", result.Error.Message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to initialize application: {Message}", exception.Message);
        }
    }
}
