# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

BetterWheelWizard: an unofficial GPL-3 fork of [TeamWheelWizard/WheelWizard](https://github.com/TeamWheelWizard/WheelWizard), a cross-platform Avalonia (C#/.NET 10) launcher for the Mario Kart Wii **Retro Rewind** mod pack. It installs/updates Retro Rewind, launches Dolphin or WiiCompiled, manages GameBanana mods, edits Miis, and shows live RWFC rooms and leaderboard (`https://rwfc.net`). The fork adds launcher theming, leaderboard search/paging, room filters, an online Mii downloader and save backups, and ships its own updater feed (`imlucaa/WheelWizardPlus` releases).

This branch (`better-v261-migration`) is BWW's features ported onto **upstream v2.6.x's rewritten architecture** (constructor injection, typed settings, `Features/*/Views`). The older `custom` branch is built on upstream 2.5.7 (static `PathManager`, untyped settings) and does not share this structure or history with upstream. Assembly name is `BetterWheelWizard` (the project folder and namespace stay `WheelWizard`).

## Commands

Requires the .NET 10 SDK (`global.json`: 10.0.100, roll forward latestFeature). All commands run from the repo root.

```sh
dotnet build WheelWizard.sln                      # also runs CSharpier (see below)
dotnet test WheelWizard.sln                       # both test projects
dotnet test WheelWizard.Test                      # unit tests (xUnit v2, VSTest mode)
dotnet test WheelWizard.UI.Test                   # headless Avalonia UI tests (xUnit v3)
dotnet test WheelWizard.Test --filter "FullyQualifiedName~LauncherThemeTests"            # one class
dotnet test WheelWizard.Test --filter "FullyQualifiedName~AccentText_IsCrispBlackOrWhite" # one test
dotnet run --project WheelWizard                  # run the app
```

Single-file Windows release (same flags CI uses):

```sh
dotnet publish WheelWizard/WheelWizard.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -o artifacts/<name>
```

Notes that are easy to trip over:

- **Builds reformat source.** `CSharpier.MsBuild` (0.30.6, `Directory.Build.props`) rewrites `.cs` files during build (`CSharpier_Check` is false), so a build can leave diffs.
- **A running exe locks its output.** Stop `BetterWheelWizard.exe` before publishing to the same folder.
- `WheelWizard.UI.Test` uses Avalonia headless **without Skia**: it checks layout/composition, not pixels, and there is no OpenGL, so the realtime Mii view falls back to images. The sidebar deliberately has no hover glow (clean sidebar), so `HoverGlow_MovesWithoutChangingButtonLayout` only asserts glow movement for controls that show it.
- Many `.axaml`/`.cs` files have **mixed CRLF/LF line endings**. Scripted multi-line exact-match edits fail unless they tolerate both (`\r?\n`).
- PR titles start with `fix:`, `feat:` or `task:` (`CONTRIBUTING.md`).

## Architecture

**Startup / composition.** `Program.Main` resolves the app-data location (`Features/ApplicationData`), builds Serilog, then `SetupExtensions.AddWheelWizardServices(applicationData)` registers every feature through one `AddX()` extension each, and the container is built with `ValidateOnBuild`/`ValidateScopes` (a missing registration fails at startup, and in `ApplicationCompositionTests`). It returns an `IDesktopStartup` that shows the splash and then the shell. There is no static service locator and no static `PathManager` here: take what you need by constructor injection.

**Features own their code.** `WheelWizard/Features/<Feature>/` holds services, domain types and a `Views/` folder with that feature's pages and controls (e.g. `Features/WiiManagement/MiiManagement/Views/MiiListPage`, `Features/RrRooms/Views/RoomsPage`). `Views/README.md` defines the shared-UI split: `Views/Components` (reusable, no feature services), `Views/Shell` (layout, navigation, startup), `Views/Dialogs` (popup workflows + window base), `Views/Styles` (shared resources). `Shared/` is cross-feature infrastructure (`OperationResult`, polling, downloads, IO, platform, calendar).

**Pages and navigation.** Pages are `UserControl`s created by `PageFactory` via `ActivatorUtilities.CreateInstance`: constructor parameters are DI services plus any navigation arguments, so adding a service to a page is just a new constructor parameter. `INavigationService` swaps the page in `Views/Shell/Layout`. Popups derive from `PopupContent`/`PopupWindow` (`Views/Dialogs/Base`) and are opened through `IPopupFactory`.

**Settings** (`Features/Settings`). Typed `Setting<T>` (`.Get()`, `.Set(v)`) registered in `SettingsManager` as WheelWizard (JSON `config.json`), Dolphin (INI), Recomp (TOML) or virtual (computed, with declared dependencies). Declare in `ISettingsProperties`, implement in `SettingsManager`, register with `RegisterWhWz<T>(name, default, validate)`. `ADDING_SETTINGS.md` describes the flow but still shows the older untyped `Get<bool>(...)` call style. Changes are broadcast on `ISettingsSignalBus`.

**Theming is runtime resource replacement.** `Features/Themes/LauncherThemeService` overwrites application resources when the theme settings change: the `Neutral*` (surface/text scale), `Primary*`/`Brand*` (accent scale), `BodyTextColor`, `FormFieldLabelColor`, `DefaultFont` and the `Launcher*Brush`/`Popup*Brush` brushes. Consequences:
- XAML that should follow the theme must use **`DynamicResource`** for those keys. `StaticResource Neutral…/Primary…/Brand…` freezes the default palette at load and the control stays blue-gray on every theme.
- Text on accent-filled controls uses `LauncherAccentTextBrush` (`CreateAccentText`: crisp white or black). Don't use `Brand50` text on a `Brand500` fill; on a white accent it is invisible.
- Theme colors can be translucent when a custom background image is set (`LauncherBackgroundService`, `LAUNCHER_SURFACE_OPACITY`); `Layout.axaml.cs` `ApplyLauncherAppearance` applies them to the shell surfaces.

**Localization.** One YAML file per language in `Resources/Languages` (embedded; `en.yml` is the default and the fallback). Use `t("section.key")` in C# and `{loc:T section.key}` in XAML; plurals via `t(key, count: n)`. Details in `Documentation/Translations.md`. New UI strings need at least an `en.yml` key.

**Realtime Mii.** `MiiEditorScene` (`Features/WiiManagement/MiiManagement/Views/Editor`) is a reusable 3D stage: construct it with `IMiiNativeRenderer`, `IMiiAnimationLibrary`, `ISeasonalCalendar`, `IRandomSystem` and an animate flag, add it to a `Grid`, `ShowEditor(mii)` once, then `Present(mii)` to swap. It drags/rotates, looks at the cursor and reacts to clicks; it falls back to still images without OpenGL. The Mii Downloader preview in `MiiListPage` uses it.

**RWFC data.** `IApiCaller<IRwfcApi>` (Refit) against `rwfc.net`: `roomstatus`, `leaderboard` (paged, `?search=`), player profile/history. Live rooms are polled by `Features/RrRooms` (`LiveRoomsService`) and mapped to `RrRoom`.

**Self-update.** `Features/AutoUpdating` + `Features/GitHub` read releases from `imlucaa/WheelWizardPlus`, take the newest published release that has a platform asset (`BetterWheelWizard.exe`, `BetterWheelWizard_Linux`, `BetterWheelWizard_ARM64_Linux`), and swap the exe via a PowerShell script on Windows. There is no checksum verification. Release CI (`.github/workflows/release.yml`) builds Windows/Linux only; there are no macOS builds.

## Skills

- Start C#/.NET work (features, fixes, build errors) with the `csharp-expert` skill; it routes to the right specialist.
- Use the `run-tests` skill before running or recommending `dotnet test` commands (this repo is VSTest mode with xUnit; no MTP runner).

## Working here

- The branch's working copy lives under `%TEMP%\opencode\betterwheelwizard-v261-migration` as a git worktree, so it can be deleted by temp cleanup; commit and keep a copy elsewhere.
- Upstream remote is `TeamWheelWizard/WheelWizard`; this branch is based on upstream `v2.6.1`, and upstream `main` has moved on (v2.6.2+).
