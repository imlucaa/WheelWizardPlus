# Wheel Wizard+

Quality-of-life upgrades for [WheelWizard](https://github.com/TeamWheelWizard/WheelWizard).

## Download

| Platform | Download |
| --- | --- |
| Windows x64 | [BetterWheelWizard.exe](https://github.com/imlucaa/WheelWizardPlus/releases/latest/download/BetterWheelWizard.exe) |
| Linux x64 | [BetterWheelWizard_Linux](https://github.com/imlucaa/WheelWizardPlus/releases/latest/download/BetterWheelWizard_Linux) |
| Linux ARM64 | [BetterWheelWizard_ARM64_Linux](https://github.com/imlucaa/WheelWizardPlus/releases/latest/download/BetterWheelWizard_ARM64_Linux) |

All builds are standalone single files. No installer or separate .NET installation is required.

## Features

- **Leaderboard**: search for any player.
- **Rooms**: filter the room list and sort it by highest or lowest VR. Flag players (Wii off, VR farmer, your own note) so you can spot them in rooms.
- **RR Lounge**: the rr-lounge.com leaderboard in the launcher, with every player's profile, MMR chart and mogi history. It updates live.
- **Miis**: download a Mii with the player's friend code or Mii name, or discover random ones from the leaderboard.
- **Themes**: make your own themes and use your own images as backgrounds.
- **RKSYS editor**: see if a friend code is free, then claim it in game.
- **VR overlay**: show your VR on stream with an OBS overlay, or open it as its own window.
- **Custom Discord status**: put your own image on your Discord status instead of the default one.
- **Ghosts**: see your times next to the world records, browse the time trial leaderboards and player rankings, download the fastest ghosts, and import or export ghost files.
- **Backup**: back up your save and rating files and import them later.
- **Sidebar**: press the pen icon to add pages, reorder them, make categories, or hide what you don't use. Hidden pages are still in Settings.
- **WiiCompiled**: the Discord status and themes work there too.

## FAQ

### How do I show Retro Rewind on my Discord profile with my own image?

1. Open [discord.com/developers/applications](https://discord.com/developers/applications), click **New Application** and name it **Retro Rewind**.
2. Under **Rich Presence > Art Assets**, upload a square image and give it a name, for example `my_image`. Save, then wait a few minutes.
3. In Wheel Wizard+ open **Settings > Discord RPC** and press **Run setup**.
4. Paste your **Application ID** (from General Information), pick your image, and press **Finish**.
5. Restart Dolphin and start the game.

Use the **Check setup** button if the image does not show. A new image can take up to 20 minutes to appear.

### Why does it only show for some people?

The View Player and View Room buttons are only shown to other people looking at your profile, not to you. View Room opens the room in Wheel Wizard, so it only works for people who have it open.

### Does it change the Retro Rewind pack?

No. The game files stay untouched, so online play works as normal. Wheel Wizard+ reads the game screen from Dolphin and sends the status to Discord itself.

## Screenshots

<table>
  <tr>
    <td align="center" width="33%"><img src="docs/screenshots/home.png" width="250" alt="Home" /><br /><sub><b>Home</b></sub></td>
    <td align="center" width="33%"><img src="docs/screenshots/leaderboard.png" width="250" alt="Leaderboard" /><br /><sub><b>Leaderboard</b></sub></td>
    <td align="center" width="33%"><img src="docs/screenshots/better-rooms.png" width="250" alt="Rooms" /><br /><sub><b>Rooms</b></sub></td>
  </tr>
  <tr>
    <td align="center" width="33%"><img src="docs/screenshots/mii-downloader.png" width="250" alt="Mii downloader" /><br /><sub><b>Mii downloader</b></sub></td>
    <td align="center" width="33%"><img src="docs/screenshots/custom-background.png" width="250" alt="Themes" /><br /><sub><b>Themes</b></sub></td>
    <td align="center" width="33%"><img src="docs/screenshots/fc-checker.png" width="250" alt="RKSYS editor" /><br /><sub><b>RKSYS editor</b></sub></td>
  </tr>
  <tr>
    <td align="center" width="33%"><img src="docs/screenshots/vr-overlay.png" width="250" alt="VR overlay" /><br /><sub><b>VR overlay</b></sub></td>
    <td align="center" width="33%"><img src="docs/screenshots/ghosts.png" width="250" alt="Ghosts" /><br /><sub><b>Ghosts</b></sub></td>
    <td align="center" width="33%"><img src="docs/screenshots/backup.png" width="250" alt="Backup" /><br /><sub><b>Backup</b></sub></td>
  </tr>
  <tr>
    <td align="center" width="33%"><img src="docs/screenshots/sidebar.png" width="250" alt="Sidebar editor" /><br /><sub><b>Sidebar editor</b></sub></td>
    <td align="center" width="33%"><img src="docs/screenshots/discord-rpc.png" width="250" alt="Discord settings" /><br /><sub><b>Discord settings</b></sub></td>
    <td align="center" width="33%"><img src="docs/screenshots/rr-lounge.png" width="250" alt="RR Lounge" /><br /><sub><b>RR Lounge</b></sub></td>
  </tr>
</table>

<p align="center">
  <img src="docs/screenshots/time-trial-leaderboards.png" width="760" alt="Time trial leaderboards" /><br />
  <sub><b>Time trial leaderboards</b></sub>
</p>

<p align="center">
  <img src="docs/screenshots/discord-rpc2.png" width="420" alt="Discord status" />
</p>

## Build from source

Wheel Wizard+ requires the .NET 10 SDK.

```sh
dotnet test WheelWizard.sln --configuration Release
dotnet build WheelWizard.sln --configuration Release
```

## License

Wheel Wizard+ (formerly BetterWheelWizard) is an unofficial fork of WheelWizard and is licensed under the [GNU GPL v3](LICENSE).
