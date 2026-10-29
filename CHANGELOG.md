# Changelog

User-facing changes to Wheel Wizard+ (formerly BetterWheelWizard) are recorded here. Versions follow
the Wheel Wizard+ release sequence rather than the upstream Wheel Wizard
version sequence.

## [Unreleased]

## [2.6.8] - 2026-10-29

### Changed

- The mod browser is now a page (open it from the Patches page with Browse) with category and Patches filters, search, and more mods loading as you scroll. A mod opens in a redesigned details view with a screenshot gallery.
- Pop-ups (messages, questions, progress, text input, friend and player profiles, option lists) now open inside the app window instead of as separate windows. Esc or a click outside closes the ones that can be dismissed.
- Settings: tabs hidden from the sidebar are listed at the end of the settings tabs, not under a separate heading.
- Themes: an option to show or hide the outline around your profile in the sidebar (it shows on any picture and at any panel opacity, and has an opacity slider); the Install Beta button on the Testing Mode page uses the theme colour.
- Testing Mode page follows the theme colours.
- Mii names can be 1 to 10 characters (the minimum was 3).
- Favourite Miis (the outline and the star) follow the theme colour instead of gold.
- Click a friend code in Discover Miis to copy it.
- Renamed the app to Wheel Wizard+. The download files keep their names, so updates keep working.
- WiiCompiled v0.9.0 is picked up automatically; it also accepts NTSC-U and NTSC-J discs.

### Added

- Licenses can be exported as .rkp files (Settings > Friend codes).
- Import a license from a .rkp file, an earlier save or any rksys.dat: pick the save, then the license in it, then the slot it goes into.
- A friend code history lists every change for each license, and an old code can be restored.
- Friend codes: a line under the licenses says whether the picked license's IDs are in order (unique, and the pseudo and authentic IDs equal), with a Fix IDs button when new IDs can sort it out. Importing a license warns about clashing IDs too.
- Friend codes: after a code passes the checks, Search database looks it up in the community player list (about 29 MB, downloaded once and kept for a week) and marks a code that is in it as taken.
- Discs in CISO and GCZ format are now accepted, and a file that is not a disc says so.
- Friend codes: Export all saves every license as a .rkp file in a folder you pick, and importing from a save or an earlier save can bring back all licenses at once, each into its own slot.
- Backup: Manage names an earlier save, deletes one, or clears out the old ones. Saves are also cleared out on their own, keeping the newest 30; named saves and friend code changes are never removed.
- Friend codes: double-click a license to copy its friend code; a license without a name says No name.
- Rooms: the sort, the VR filter and Friends are remembered when you close the app, and their buttons stay lit while they are on. Friends redraws as soon as your save is read, so it no longer needs toggling off and on. The room list is read again every 10 seconds while the page is open, and your place in the list is kept.
- Testing Mode can be unlocked again: press the version text under Settings > Wheel Wizard+ ten times.
- Friend codes: pick several .rkp files at once and they are added to empty slots together (nothing that is there is replaced).
- Backup: Manage > Back up now saves a copy of the save as it is.
- Ghosts: click Gap to list the tracks furthest from the world record first, and Undo puts back the ghost a track had before an import.
- Friends: a friend who is in a room shows their current VR and BR instead of the ones stored in the save; a friend who is offline shows their current VR too (looked up a few at a time, at most every 10 minutes each).
- Line colour is a tab in Settings (and can be pinned to the sidebar): it changes the colour of the line under your name online, inside a disc. Pick a disc and a colour, and Wheel Wizard uses Wiimm's ISO Tool to take it apart, change the region ID in main.dol and rebuild it, as a copy or over the original. The tool is asked for once, or the Download button on that tab fetches it from wit.wiimm.de (Windows) into Wheel Wizard+'s folder. A disc that was replaced remembers the colour it had, and the colour list offers Back to the original.
- Friend codes: Rename changes the name of the picked license.
- Ghosts: Undo keeps going back. With more than one earlier ghost, Undo lists the last few (with the time and when each was replaced) and you pick which one to put back.
- Now playing is a tab in Settings (and can be pinned): it shows the song playing on the PC, from Spotify, Apple Music, iTunes, SoundCloud or YouTube Music in a browser and other players Windows can read, as a card with the cover, title, artist, time, a moving sound bar and a progress line. The cards take the color of the cover. Show it in its own window or as an OBS Browser source (a link to copy). Options: five styles (Classic, Vertical, Compact, Strip, Text), cover side, shape and size, width, size, roundness, spacing, text size and alignment, what shows (cover, artist, app, time, sound bar, progress), card color (cover or your own), card strength, text and progress colors, glow, a spinning round cover, a border, and hiding the card while nothing plays.
- Testing Mode is a normal item in Arrange sidebar, so it can be moved or hidden like the others.

### Fixed

- Now playing has more options: font, title weight and capitals, the album name, time left instead of length, whether it says Paused, the number and color of the sound bar's bars, the thickness and position (above or below) of the progress line, a text shadow, card shadows and gradient cards.
- Now playing has presets (Classic, Vinyl, Strip, Text only, Big cover, Compact, Neon, Glass, Terminal): one click sets the whole look, and the one that matches is outlined. Save the look you made under a name (up to 24, delete with the x), and export a look to a file or import one from a file.
- Now playing and web browsers: by default a browser only counts when it is playing music: media that names an album (Spotify and Apple Music on the web), or a tab of a music website such as YouTube Music, SoundCloud, Apple Music or Tidal (found from the browser's tab names). A YouTube video is not shown as a song. Songs from browsers (under When it shows) can also be set to ignore browsers, or to take everything.
- Discord status: after 15 minutes on a menu or vote screen (or in a room you stopped playing in) the status says Idle, without the track, room or buttons, and counts the idle time. Screens that show a track are never idle.
- Now playing: when Windows' media controls cannot be read (PowerShell blocked by security software, or stopped), the tab says so instead of showing a card that says nothing is playing, and it only tries again every 30 seconds.
- Discord status: an online race shows the one track the game loaded, not both names the room lists ("GCN Dry Dry Desert / Wii U Dry Dry Desert").
- Discord status: the timer no longer counts the whole time in a room. When the game screen cannot be read, the status shows the room's current race, and its timer now starts over with each new race instead of running for as long as the room lasts.

### Removed

- The Dev Tool button in Settings is hidden in debug builds unless `WHEELWIZARD_DEV=1` is set.
- The globe marker on Miis in the Mii picker.

### Fixed

- VR overlay: the card showed a made-up +41 whatever your VR did; it now shows the real change since the card opened (and counts from the previous VR).
- Discord RPC: Time Trials (and other offline modes) now show the track on PAL games; the pack keeps the track name at a different place there, and it is looked for when it is not where it should be.
- Discord RPC: the check says when your Discord app has no App Icon, which is why Discord shows a ? next to your name; the setup popup has a step for it.
- VR overlay: the preview in Settings keeps up with your VR instead of showing it from when the page opened, and both it and the overlay window read your VR from the live room list (the rwfc.net profile runs about a minute behind), so a change shows sooner.
- Changing a friend code only changes the same license on your other regions' saves, not a different license that happens to sit in the same slot.
- Reinstalling Retro Rewind no longer deletes your install and mod patches when the download fails or the server is down; the old install is put back. The Beta build is only replaced once the new one has downloaded.
- Launch on startup no longer starts an out-of-date Retro Rewind; use the Update button on Home first.
- The Mii rendering download error now names the site it downloads from, so a blocking firewall is easy to spot.
- Player profile window no longer logs a failed binding for every field.
- Starting the game automatically or with `--launch rr` now uses WiiCompiled when it is turned on (upstream PR #535).
- Fixes from upstream Wheel Wizard: Retro WFC check uses the current HTTPS address, smoother Windows icon, clearer Linux update notices, Flatpak window grouping, German translation fix, and safer file paths.

## [2.6.7] - 2026-10-24

### Added

- RR Lounge tab: the rr-lounge.com leaderboard for Retro Rewind, Custom Tracks and Time Trial, with seasons, search, rank colours and badges. It updates every minute.
- Player profiles in RR Lounge: division, rank, stats, an MMR chart and every mogi.
- Ghosts tab in the sidebar: your times next to the world records, with the same filters as rwfc.net/tt.
- Get top ghosts: download the fastest ghosts from rwfc.net/tt, with player rankings and player pages.
- Flag players in rooms with a note and a colour. Left click a player for their stats, right click to flag them.
- VR overlay can open as its own window.
- Discover Miis shows nine Miis from anywhere on the leaderboard.
- Hidden sidebar pages are listed in Settings.

### Changed

- RR Lounge and Ghosts have their own Competitive category in the sidebar.
- Language moved into Appearance, and save backups moved into Backup.
- Simpler update prompt and shorter setting descriptions.
- Rank badges and flags are kept on disk, so they only download once.

### Fixed

- Switching theme left the old background behind.
- The two bottom sidebar icons overlapping.
- Odd characters in some Mii search messages.

## [2.6.6] - 2026-10-20

### Added

- Ghosts tab: import a ghost and the track is found for you, search tracks, and export any ghost. Exported files are named by the finish time.
- Friend codes tab (RKSYS editor): check if a friend code is free, claim it in game, back up your save, and undo the last change.
- Sidebar editor (pen icon): add settings pages to the sidebar, drag items and categories around, rename or add categories, hide what you don't use, and reset.
- VR overlay: a Preview button that plays the animation, and the preview replays when you change it.
- Discord RPC: Default, Custom and Off switch live while you play. Track names show in Time Trials, and it works with WiiCompiled too.
- Theme options for hiding the home menu and wheel now apply right away on WiiCompiled.
- Patch presets, game discs per region, and a dot on the sidebar when a new version is out.

### Changed

- Cleaner Ghosts, Backup, Discord RPC and VR overlay tabs.
- Removed the version label from the sidebar.

### Fixed

- The red outline on the search box.

## [2.6.5] - 2026-10-18

### Added

- Discord RPC tab: show Retro Rewind on your Discord profile with your own image, the current screen, track, players, VR and BR. A short setup guide walks you through it.
- "View Player" and "View Room" buttons on your Discord profile, each with its own switch. View Room opens the room in Wheel Wizard for people who have it open.
- Color picker now works, and any hex color is used exactly as typed.

### Changed

- The "Mods found" question no longer appears when you start the game.
- Panels at partial opacity no longer show a black line.

## [2.6.4] - 2026-10-17

### Changed

- Backing up rksys.dat now asks which region to save, or uses the region you played last.
- Simplified the Backup tab and the Theme library popup.
- Centered the Rooms and Leaderboard titles.

### Fixed

- Rooms labels and many settings, leaderboard and friend messages are now translated into Japanese.

## [2.6.3] - 2026-10-17

### Changed

- Restyled the launcher with flat, theme-aware controls: no outlines, dark panel buttons, and accent buttons that stay readable on every theme.
- Redesigned the My Miis and Mii Downloader pages, with an interactive 3D Mii preview in the downloader and in Leaderboard search results.
- Refreshed Rooms, Leaderboard, Patches, Themes and Settings layouts, and restyled all popups.
- Custom background images no longer show a seam at the window edge.

### Fixed

- Accent-coloured button labels are now always crisp black or white for contrast.
- Disabled accent buttons are visibly dimmed instead of turning grey.

## [2.6.0] - 2026-09-28

### Changed

- Rebuilt BetterWheelWizard on the rewritten Wheel Wizard application engine for improved reliability, maintainability, startup handling, and path management.
- Preserved the existing `CT-MKWII` application-data location so current settings and files continue to be found after upgrading.
- Updated the app identity, update checker, and standalone release downloads for BetterWheelWizard.

### Added back

- Custom launcher themes, built-in presets, share codes, and theme importing.
- Direct backups for regional Mario Kart Wii saves and Retro Rewind ratings.
- RWFC leaderboard search, pagination, player profiles, and Mii copying.
- Room search, average-VR filters, sorting, track details, and race timers.
- Online Mii search, suggestions, downloading/importing, and saved-Mii filtering.

### Downloads

- Standalone Windows x64, Linux x64, and Linux ARM64 single-file builds remain available without a separate .NET installation.

## [2.5.14] - 2026-09-27

### Added

- Added a popup Theme Library for choosing built-in and saved themes.
- Added compact palette cards with larger color swatches and aligned HEX inputs.
- Added animated gold highlighting for searched leaderboard players.

### Changed

- Simplified theme choosing, saving, sharing, and deletion into compact actions.
- Improved leaderboard page spacing, ranked-player rows, pagination, and later-page placement labels.
- Made popup confirmation buttons use the same neutral style as popup cancel buttons.
- Simplified the README and release automation.

### Fixed

- Fixed RWFC search ranks above 50,000 being replaced with the search-result position.
- Fixed imported theme codes retaining the previously selected theme name and delete state.
- Fixed later leaderboard pages showing page-slot labels instead of actual placements.
- Fixed the searched-player profile button not matching the adjacent Back button.

## [2.5.13] - 2026-09-25

### Added

- Added RWFC leaderboard search by friend code or player/Mii name.
- Added duplicate-result browsing for leaderboard and Mii downloader searches.
- Added popup-based search entry for the leaderboard, My Miis, and Mii Downloader.
- Added a dedicated Rooms sort popup for cleaner room-browser controls.

### Changed

- Refined the leaderboard header and player result card to better match the app's native styling.
- Reworked Rooms controls into centered popup actions for search, VR filtering, and sorting.
- Cleaned up page chrome, spacing, separators, and window-control padding across the app.
- Updated the README with current screenshots and clearer setup and feature documentation.

### Fixed

- Fixed RWFC Mii downloader lookups for both friend code and player-name searches.
- Fixed leaderboard search reset behavior so clearing a search returns to the default ranked view.
- Fixed font/resource URI usage for BetterWheelWizard branding assets.
- Removed the unused Rooms page warning and deleted an unused converter.

## [2.5.12] - 2026-09-24

### Added

- Added a visual color picker with copyable and editable HEX values for all four theme colors.
- Added more built-in presets covering basic rainbow colors and popular colors such as coral, mint, lavender, and rose.

### Changed

- Renamed the accent control to Main color and added plain-language descriptions of what each color changes.
- Applied selected main, wheel/title, and background colors exactly across the full brightness range, including pure black and white.
- Adapted surfaces and text for bright backgrounds while retaining automatic readable-text contrast.
- Made the Home wheel and game title follow the wheel/title color, while the BetterWheelWizard name follows the text color.
- Simplified and compacted the Themes page and color-picker layout.

### Fixed

- Fixed built-in presets loading intermediate values into the wrong color fields.
- Fixed positive VR History changes using accent/green text instead of the normal theme text color.

## [2.5.11] - 2026-09-24

### Changed

- Renamed future release downloads to use the BetterWheelWizard name.
- Improved room-browser responsiveness at narrow widths and high display scaling.
- Updated release automation to publish structured notes from this changelog.

## [2.5.10] - 2026-09-23

### Fixed

- Fixed clipped room game-mode labels such as `RR 150CC`.
- Tightened spacing between game modes and room IDs.
- Expanded player rows to use the full width in Rooms and Room Details.

## [2.5.9] - 2026-09-23

### Added

- Added built-in and saved launcher themes with replace, delete, and restore controls.
- Added compact `BWW1` theme codes for copying and importing shared colors.
- Added direct backups for regional `rksys.dat` saves and `RRRating.pul`.

### Changed

- Applied theme colors across launcher navigation, actions, profiles, rooms, and status indicators.
- Refined page headers, cards, tabs, buttons, spacing, and narrow layouts.

[Unreleased]: https://github.com/imlucaa/WheelWizardPlus/compare/v2.6.0...HEAD
[2.6.0]: https://github.com/imlucaa/WheelWizardPlus/compare/v2.5.14...v2.6.0
[2.5.14]: https://github.com/imlucaa/WheelWizardPlus/compare/v2.5.13...v2.5.14
[2.5.13]: https://github.com/imlucaa/WheelWizardPlus/compare/v2.5.12...v2.5.13
[2.5.12]: https://github.com/imlucaa/WheelWizardPlus/compare/v2.5.11...v2.5.12
[2.5.11]: https://github.com/imlucaa/WheelWizardPlus/compare/v2.5.10...v2.5.11
[2.5.10]: https://github.com/imlucaa/WheelWizardPlus/compare/v2.5.9...v2.5.10
[2.5.9]: https://github.com/imlucaa/WheelWizardPlus/releases/tag/v2.5.9
