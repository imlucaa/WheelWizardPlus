using WheelWizard.ApplicationData;
using WheelWizard.ApplicationIntegration;
using WheelWizard.ApplicationLifecycle;
using WheelWizard.AutoUpdating;
using WheelWizard.Branding;
using WheelWizard.CustomCharacters;
using WheelWizard.CustomDistributions;
using WheelWizard.DiscImages;
using WheelWizard.DolphinInstaller;
using WheelWizard.Features.Archives;
using WheelWizard.Features.Backups;
using WheelWizard.Features.Patches;
using WheelWizard.GameBanana;
using WheelWizard.GitHub;
using WheelWizard.Launching;
using WheelWizard.Localization;
using WheelWizard.MiiAnimations;
using WheelWizard.MiiImages;
using WheelWizard.Mods;
using WheelWizard.Recomp;
using WheelWizard.RrLounge;
using WheelWizard.RrRooms;
using WheelWizard.Settings;
using WheelWizard.Themes;
using WheelWizard.Views.Shell;
using WheelWizard.WheelWizardData;
using WheelWizard.WiiManagement;

namespace WheelWizard;

public static class SetupExtensions
{
    /// <summary>Composes feature services and their desktop presentation adapters.</summary>
    public static void AddWheelWizardServices(this IServiceCollection services, IApplicationDataLocation? applicationData = null)
    {
        services.AddSharedServices();
        services.AddApplicationData(applicationData);
        services.AddApplicationIntegration();
        services.AddApplicationLifecycle();
        services.AddLaunching();
        services.AddDolphinInstaller();
        services.AddLocalization();
        services.AddSettings();
        services.AddLauncherThemes();
        services.AddCustomCharacters();
        services.AddAutoUpdating();
        services.AddBranding();
        services.AddGitHub();
        services.AddRrRooms();
        services.AddRrLounge();
        services.AddDiscImages();
        services.AddWhWzData();
        services.AddWiiManagement();
        services.AddGameBanana();
        services.AddMiiImages();
        services.AddMiiAnimations();
        services.AddCustomDistributionService();
        services.AddArchives();
        services.AddSingleton<GameDataBackupService>();
        services.AddSingleton<WheelWizard.DiscordPresence.DiscordPresenceService>();
        services.AddSingleton<WheelWizard.StreamOverlay.OverlayLive>();
        services.AddSingleton<WheelWizard.StreamOverlay.OverlayBackground>();
        services.AddSingleton<WheelWizard.DiscordPresence.RoomLinkListener>();
        services.AddSingleton<WheelWizard.RrRooms.FriendRoomNotifier>();
        services.AddSingleton<WheelWizard.WiiManagement.GameLicense.SessionStatsTracker>();
        services.AddSingleton<WheelWizard.WiiManagement.GameLicense.ISessionStats>(provider =>
            provider.GetRequiredService<WheelWizard.WiiManagement.GameLicense.SessionStatsTracker>()
        );
        services.AddSingleton<WheelWizard.AutoUpdating.UpdateAutoChecker>();
        services.AddSingleton<WheelWizard.Ghosts.GhostImporter>();
        services.AddPatches();
        services.AddMods();
        services.AddRecomp();
        services.AddPresentation();
    }
}
