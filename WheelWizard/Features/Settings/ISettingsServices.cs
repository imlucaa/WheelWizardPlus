using WheelWizard.Models.Enums;
using WheelWizard.Settings.Types;

namespace WheelWizard.Settings;

public interface IWhWzSettingManager
{
    void RegisterSetting(IWhWzSetting setting);
    void SaveSettings(string configPath, IWhWzSetting invokingSetting);
    void LoadSettings(string configPath);
}

public interface IDolphinSettingManager
{
    void RegisterSetting(IDolphinSetting setting);
    void SaveSettings(string configDirectory, IDolphinSetting invokingSetting);
    void ReloadSettings(string configDirectory);
    void LoadSettings(string configDirectory);
}

public interface IRecompSettingManager
{
    void RegisterSetting(IRecompSetting setting);
    void SaveSettings(string configPath, IRecompSetting invokingSetting);
    void ReloadSettings(string configPath);
    void LoadSettings(string configPath);

    /// <summary>
    /// Deletes one key from one section of the recomp's <c>Config.toml</c>, leaving every other key,
    /// comment, and ordering untouched. A missing file or key is a no-op.
    /// </summary>
    void RemoveTomlSetting(string configPath, string section, string settingToRemove);
}

public interface ISettingsProperties
{
    Setting<string> USER_FOLDER_PATH { get; }
    Setting<string> DOLPHIN_LOCATION { get; }
    Setting<string> GAME_LOCATION { get; }
    Setting<bool> FORCE_WIIMOTE { get; }
    Setting<bool> LAUNCH_WITH_DOLPHIN { get; }
    Setting<bool> LAUNCH_RR_ON_STARTUP { get; }
    Setting<bool> DISCORD_PRESENCE_ENABLED { get; }
    Setting<bool> DISCORD_LIVE_INFO { get; }
    Setting<bool> DISCORD_PLAYER_BUTTON { get; }
    Setting<bool> DISCORD_ROOM_BUTTON { get; }
    Setting<bool> DOLPHIN_DISCORD_PRESENCE { get; }
    Setting<string> DISCORD_APP_ID { get; }
    Setting<string> MII_TAGS { get; }
    Setting<string> PLAYER_FLAGS { get; }
    Setting<string> OVERLAY_CONFIG { get; }
    Setting<string> NOW_PLAYING_CONFIG { get; }
    Setting<string> NOW_PLAYING_LOOKS { get; }
    Setting<string> OVERLAY_PRESETS { get; }
    Setting<string> OVERLAY_FRIEND_CODE { get; }
    Setting<string> GAME_DISCS { get; }
    Setting<string> WIT_LOCATION { get; }
    Setting<string> LINE_COLOR_ORIGINALS { get; }
    Setting<string> ROOMS_VIEW { get; }
    Setting<string> DISCORD_LARGE_IMAGE { get; }
    Setting<string> DISCORD_LARGE_TEXT { get; }
    Setting<string> DISCORD_DETAILS { get; }
    Setting<string> DISCORD_STATE { get; }
    Setting<bool> ENABLE_RECOMP { get; }
    Setting<bool> RECOMP_USE_DOLPHIN_DATA { get; }
    Setting<bool> RECOMP_COPY_DOLPHIN_NAND { get; }
    Setting<bool> PREFERS_MODS_ROW_VIEW { get; }
    Setting<bool> USE_PATCHES_SYSTEM { get; }
    Setting<int> FOCUSED_USER { get; }
    Setting<bool> ENABLE_ANIMATIONS { get; }
    Setting<bool> FRIEND_ROOM_ALERTS { get; }
    Setting<bool> SHOW_SESSION_STATS { get; }
    Setting<bool> AUTO_CHECK_UPDATES { get; }
    Setting<bool> SIDEBAR_COLLAPSED { get; }
    Setting<string> SIDEBAR_PINNED_TABS { get; }
    Setting<string> SIDEBAR_LAYOUT { get; }
    Setting<bool> TESTING_MODE_ENABLED { get; }
    Setting<double> SAVED_WINDOW_SCALE { get; }
    Setting<MarioKartWiiEnums.Regions> RR_REGION { get; }
    Setting<string> WW_LANGUAGE { get; }
    Setting<string> LAUNCHER_THEME_COLOR { get; }
    Setting<string> LAUNCHER_TEXT_COLOR { get; }
    Setting<string> LAUNCHER_BODY_TEXT_COLOR { get; }
    Setting<string> LAUNCHER_BACKGROUND_COLOR { get; }
    Setting<string> LAUNCHER_BACKGROUND_FILE { get; }
    Setting<string> LAUNCHER_BACKGROUND_STRETCH { get; }
    Setting<double> LAUNCHER_BACKGROUND_OPACITY { get; }
    Setting<double> LAUNCHER_SURFACE_OPACITY { get; }
    Setting<double> LAUNCHER_BACKGROUND_BLUR { get; }
    Setting<double> LAUNCHER_BACKGROUND_ZOOM { get; }
    Setting<double> LAUNCHER_BACKGROUND_FOCUS_X { get; }
    Setting<double> LAUNCHER_BACKGROUND_FOCUS_Y { get; }
    Setting<bool> LAUNCHER_MINIMAL_HOME_ARTWORK { get; }
    Setting<bool> LAUNCHER_HIDE_HOME_TITLE { get; }
    Setting<bool> LAUNCHER_CLEAN_SIDEBAR { get; }
    Setting<bool> LAUNCHER_PROFILE_OUTLINE { get; }
    Setting<double> LAUNCHER_PROFILE_OUTLINE_OPACITY { get; }
    Setting<string> LAUNCHER_FONT { get; }
    Setting<string> NAND_ROOT_PATH { get; }
    Setting<string> LOAD_PATH { get; }
    Setting<bool> VSYNC { get; }
    Setting<int> INTERNAL_RESOLUTION { get; }
    Setting<bool> SHOW_FPS { get; }
    Setting<string> GFX_BACKEND { get; }
    Setting<string> MACADDRESS { get; }
    Setting<double> WINDOW_SCALE { get; }
    Setting<bool> RECOMMENDED_SETTINGS { get; }
    Setting<double> RECOMP_RESOLUTION_MULTIPLIER { get; }
    Setting<string> RECOMP_GRAPHICS_API { get; }
    Setting<bool> RECOMP_SHOW_FPS { get; }
    Setting<bool> RECOMP_DISCORD_PRESENCE { get; }
    Setting<bool> RECOMP_PREVENT_STUTTERS { get; }
    Setting<string> RECOMP_NAND_ROOT { get; }
}

public interface ISettingsManager : ISettingsProperties
{
    OperationResult<SettingsValidationReport> ValidateCorePathSettings();

    T Get<T>(Setting<T> setting);
    bool Set<T>(Setting<T> setting, T value, bool skipSave = false);
    ExtensionConfigurationInfo CheckExtensionConfiguration();
    bool PathsSetupCorrectly();
    bool DolphinPathsSetupCorrectly();

    /// <summary>
    /// Whether WiiCompiled is the active frontend instead of Dolphin/Retro Rewind. This is the single
    /// definition of that mode: it carries the platform guard, so a stale <c>EnableRecomp</c>
    /// flag can never activate recomp behavior on a platform the recomp does not run on.
    /// </summary>
    bool IsRecompModeActive();

    void LoadSettings();
}

public interface ISettingsStartupInitializer
{
    void Initialize();
}

public interface ISettingsLocalizationService
{
    void Initialize();
    void ApplyCurrentLanguage();
}
