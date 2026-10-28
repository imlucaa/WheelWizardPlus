using Semver;
using WheelWizard.AutoUpdating.Platforms;
using WheelWizard.Branding;
using WheelWizard.GitHub;
using WheelWizard.GitHub.Domain;
using WheelWizard.Shared.Platform;

namespace WheelWizard.AutoUpdating;

public interface IAutoUpdaterSingletonService
{
    bool IsUpdateAvailable { get; }

    /// <summary>When the launcher last asked GitHub for releases, or null when it has not yet.</summary>
    DateTimeOffset? LastCheckedAt { get; }

    event EventHandler? UpdateAvailable;
    Task CheckForUpdatesAsync();
    Task ShowAvailableUpdateAsync();

    /// <summary>Looks for a newer release right now and reports what it found, without opening any pop-up.</summary>
    Task<UpdateCheckResult> CheckNowAsync();
}

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed,
}

/// <summary>The outcome of a manual update check: the newer version when there is one, or why the check failed.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, string? Version = null, string? Message = null);

public class AutoUpdaterSingletonService(
    IUpdatePlatform updatePlatform,
    IBrandingSingletonService brandingService,
    IGitHubSingletonService gitHubService,
    IUpdatePresentation presentation,
    IRuntimeEnvironment environment,
    TimeProvider time
) : IAutoUpdaterSingletonService
{
    private bool _manualUpdateShown;
    private bool _updatePromptOpen;
    private GithubRelease? _availableRelease;
    public bool IsUpdateAvailable => _availableRelease is not null;
    public DateTimeOffset? LastCheckedAt { get; private set; }
    public event EventHandler? UpdateAvailable;
    private string CurrentVersion => brandingService.Branding.Version;

    public async Task CheckForUpdatesAsync()
    {
        LastCheckedAt = DateTimeOffset.Now;
        var latestRelease = await GetLatestReleaseAsync();
        if (latestRelease?.TagName is null)
            return;

        _availableRelease = latestRelease;
        UpdateAvailable?.Invoke(this, EventArgs.Empty);
        if (!updatePlatform.SupportsAutomaticUpdate && _manualUpdateShown)
            return;

        await ShowAvailableUpdateAsync();
    }

    public async Task<UpdateCheckResult> CheckNowAsync()
    {
        LastCheckedAt = DateTimeOffset.Now;
        var releases = await gitHubService.GetReleasesAsync();
        if (releases.IsFailure)
            return new(UpdateCheckStatus.Failed, Message: releases.Error.Message);

        GithubRelease? latest;
        try
        {
            latest = FindNewestUsable(releases.Value);
        }
        catch (FormatException)
        {
            return new(UpdateCheckStatus.Failed, Message: "A release has a version number this launcher can not read.");
        }

        if (latest?.TagName is null)
            return new(UpdateCheckStatus.UpToDate);

        _availableRelease = latest;
        UpdateAvailable?.Invoke(this, EventArgs.Empty);
        return new(UpdateCheckStatus.UpdateAvailable, Version: latest.TagName.TrimStart('v'));
    }

    public async Task ShowAvailableUpdateAsync()
    {
        if (_availableRelease is not { TagName: not null } latestRelease || _updatePromptOpen)
            return;

        _updatePromptOpen = true;
        try
        {
            var latestVersion = latestRelease.TagName.TrimStart('v');
            if (!updatePlatform.SupportsAutomaticUpdate)
            {
                _manualUpdateShown = true;
                await presentation.ShowManualUpdateAsync(latestVersion, CurrentVersion);
                return;
            }

            var asset = updatePlatform.GetAssetForCurrentPlatform(latestRelease);
            if (asset is null)
                return;

            var notes = ReleaseNotesText.Summarize(latestRelease.Body);
            var confirmed =
                notes.Length == 0
                    ? await presentation.ConfirmUpdateAsync(latestVersion, CurrentVersion)
                    : await presentation.ConfirmUpdateAsync(latestVersion, CurrentVersion, notes);
            if (!confirmed)
                return;

            var updateResult = await presentation.RunUpdateAsync(
                (progress, cancellation) => updatePlatform.ExecuteUpdateAsync(asset.BrowserDownloadUrl, progress, cancellation)
            );
            if (updateResult.IsFailure)
                await presentation.ShowUpdateFailureAsync(updateResult.Error.Message);
        }
        finally
        {
            _updatePromptOpen = false;
        }
    }

    private async Task<GithubRelease?> GetLatestReleaseAsync()
    {
        var releasesResult = await gitHubService.GetReleasesAsync();
        if (releasesResult.IsFailure)
        {
            await presentation.ShowCheckFailureAsync(releasesResult.Error.Message);

            return null;
        }

        return FindNewestUsable(releasesResult.Value);
    }

    /// <summary>The newest stable release that is newer than this version and, where it matters, has a file for this platform.</summary>
    private GithubRelease? FindNewestUsable(List<GithubRelease> releases)
    {
        if (releases.Count == 0)
            return null;

        // Get the current version
        var currentVersion = SemVersion.Parse(CurrentVersion, SemVersionStyles.Any);

        // Iterate over the latest 3 releases and find the newest one that has an asset for this platform
        GithubRelease? bestMatch = null;
        SemVersion? bestVersion = null;

        foreach (var release in releases)
        {
            if (release.TagName == null!)
                continue;

            if (release.Prerelease)
                continue;

            if (!SemVersion.TryParse(release.TagName.TrimStart('v'), SemVersionStyles.Any, out var releaseVersion))
                continue;
            if (releaseVersion.ComparePrecedenceTo(currentVersion) <= 0)
                continue;

            // Package managers (Flatpak) lag behind GitHub, so wait a day before pointing them there. The standalone Linux
            // builds update themselves from the release, so they get it straight away.
            if (
                environment.IsLinux
                && !updatePlatform.SupportsAutomaticUpdate
                && (release.PublishedAt is not { } publishedAt || time.GetUtcNow() - publishedAt < TimeSpan.FromDays(1))
            )
                continue;

            var asset = updatePlatform.GetAssetForCurrentPlatform(release);
            if (updatePlatform.SupportsAutomaticUpdate && asset is null)
                continue;

            if (bestVersion is null || releaseVersion.ComparePrecedenceTo(bestVersion) > 0)
            {
                bestMatch = release;
                bestVersion = releaseVersion;
            }
        }

        return bestMatch;
    }
}
