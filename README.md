<p align="center">
  <img src="docs/brand/hero.png" alt="ValheimOne" width="100%">
</p>

<p align="center">
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/License-MIT-c9a959?style=for-the-badge"></a>
  <a href="https://github.com/BepInEx/BepInEx"><img alt="BepInEx 5.4.x" src="https://img.shields.io/badge/BepInEx-5.4.x-2f80ed?style=for-the-badge"></a>
  <a href="https://store.steampowered.com/app/892970/"><img alt="Valheim Dedicated Server" src="https://img.shields.io/badge/Valheim-Dedicated_Server-1b2838?style=for-the-badge&logo=steam&logoColor=white"></a>
  <a href="#features"><img alt="Map and console: vanilla clients" src="https://img.shields.io/badge/Map_and_Console-Vanilla_Clients-2ea043?style=for-the-badge"></a>
</p>

# ValheimOne

A Valheim dedicated server has no map, no browser console and no alerts of its own, and it stops at 10 players. ValheimOne adds them: a live web map you can share with your players, an admin console in your browser, an item and recipe search, Discord alerts, a higher player limit and optional gameplay settings. It is one BepInEx mod on the server. Your players can join with an unmodded game, on PC or console.

_ValheimOne is a community project and is not affiliated with or endorsed by Iron Gate Studio._

## Contents

- [Features](#features)
- [Install](#install)
- [Settings](#settings)
- [Commands](#commands)
- [Verifying the binary](#verifying-the-binary)
- [Getting help](#getting-help)
- [Contributing](#contributing)
- [License](#license)
- [Credits](#credits)

---

## Features

### 🗺️ Live map

The map draws the whole world from the server seed and updates in real time. Players, ships, carts, portals, tombstones, wards and beds all show on it. Fog of war follows where players have walked and what they shared through cartography tables.

![ValheimOne Live Map, admin view](docs/screenshots/live-map-20261016.png)

Turn on the heatmap to see where everyone has been over the last day or week, open the timelapse to scrub through explored fog and base growth, or open the Sagas leaderboard for playtime, deaths and distance travelled.

### 🔗 Share the map with your players

There are three views. **Admin** sees everything. **Shared** shows live players and the layers you allow, with no admin actions. **Public** is a read-only link with its own fog and layer settings. Fog can be a light tint or a solid cover that hides everything nobody has explored yet, including region names and markers, and Public viewers cannot switch the cover off. The settings are under [Fog and Shared map layers](#fog-and-shared-map-layers).

![ValheimOne public map with explored fog](docs/screenshots/livemap-public-fog.png)

### 🏛️ Dungeon maps

Click any dungeon entrance for a top-down map of its rooms, read from the game's own generated layout, with live players drawn inside it.

![ValheimOne dungeon interior viewer](docs/screenshots/dungeon-interior.png)

### 📖 Item and recipe search

Search and filter every item and recipe: weight, stack size, tiers, damage, armour, full recipes with station requirements, what drops it and what it is used to make. Jump from an ingredient to everything that needs it.

![ValheimOne Codex of Items](docs/screenshots/valheimone-codex-20260909-r3.png)

### 💻 Server console in your browser

A live server log and a command box with history and autocomplete, running whitelisted commands through the game's own console. Kick, ban, save the world, or schedule a shutdown that warns players first.

![ValheimOne web admin console](docs/screenshots/valheimone-console-20260909-r3.png)

<a id="less-lag"></a>

### 📶 Less lag when you play together

Valheim lets the server have at most 10 KB of world updates on the way to each player, and serves one player per frame. When several players play close together, each one also needs everything the others change, the 10 KB runs out, and updates arrive in bursts: players and creatures rubber-band until the group splits up. ValheimOne raises that limit to 32 KB on Steam and crossplay alike, and lets Steam send up to 1 MB/s per player instead of a fixed 150 KB/s. It runs on the server only, so players install nothing and vanilla and console players get it too.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/send-limit-lab-dark.png">
  <img src="docs/screenshots/send-limit-lab-light.png" alt="Bar chart from a lab server: four players together with 41 moving things each got 2.1 updates a second for every moving thing nearby with Valheim's own limit and 5.6 with ValheimOne's higher limit; at 200 ms ping, 1.2 and 3.9" width="800">
</picture>

Measured on a lab server with four test players, 150 seconds per run. Valheim sends each moving thing at most about 6 times a second, so a quiet group gains little; a busy group gets back to near that ceiling.

It is on by default. Set `Enabled = false` in `[Network]` to go back to Valheim's own limit. It turns itself off, with one line in the log, when Better Networking or another mod that changes the same limit is installed.

### Also built in

- **More than 10 players.** Set `MaxPlayers` in `[Server]`, up to 127. The real join limit moves with it, on Steam and crossplay, and players install nothing. On crossplay the new limit applies at the next start.
- **Gameplay settings.** 26 optional modules for carry weight, stamina, food, drops, gathering, build rules, portals, taming, raids, production speeds and more. All are off until you turn them on, and most values apply without a restart. Modules marked Synced also need ValheimOne on each player's PC. [Every module and what it needs](docs/gameplay-modules.md).
- **Discord alerts.** Joins, leaves, deaths with the biome they died in, raids starting and ending, world saves and new days. Point it at a webhook and pick the events you want.
- **Shows up in server lists.** A query responder (A2S) lets server browsers, monitoring tools and hosting panels see the server, including crossplay servers that do not answer on their own. Richer JSON lives at `/api/status` and `/api/players` ([reference](docs/query.md)).

---

## Install

The [Steam setup guide](https://steamcommunity.com/sharedfiles/filedetails/?id=3802119003) walks through installation, private map access, sharing and browser commands.

### Rent a server

[Valheim server hosting from Survival Servers](https://www.survivalservers.com/services/game_servers/valheim/?utm_source=github&utm_medium=readme&utm_campaign=valheim_one) comes with ValheimOne, with the map and console in the control panel.

### Install on your server

You need a Valheim Dedicated Server with the Valheim-compatible [BepInEx pack](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/).

1. Download the current [GitHub release](https://github.com/HumanGenome/ValheimOne/releases/latest). Use the plugin-only ZIP if BepInEx is installed, or the Full ZIP for a fresh setup.
2. Stop the server and extract the ZIP into its root folder. The plugin belongs at `BepInEx/plugins/ValheimOne.dll`. Preserve your existing `BepInEx/config/valheimone.cfg` when updating; do not replace it with the packaged defaults. On Linux, use the Full pack's `start_server_bepinex.sh` with your usual server arguments.
3. Start once to generate any missing config, then stop before the initial configuration. In the existing `[LiveMap]` section of `BepInEx/config/valheimone.cfg`, set `Enabled = true`, choose a long unique `AccessToken`, and set `PublicView = false` while configuring access. The live map is disabled by default.
4. Start the server and wait for the world to load. The default map port is TCP `8790`. On a trusted network, open `http://your-server-ip:8790/?token=YOUR_ADMIN_TOKEN` with your own values. Use an HTTPS reverse proxy for authenticated access over the Internet. Behind a proxy, set `RelayKey` in `[LiveMap]` to a long random value and have the proxy send it in an `X-Relay-Key` header: the map then refuses every request without it, except requests from the server machine itself.
5. Set `ConsoleEnabled = true` in `[LiveMap]` if you want browser commands. Set `EntityLayer = true` to collect the optional ships, carts and portals layer. For sharing, configure a separate `ShareToken` or enable the tokenless `PublicView`, then choose the fog and public layers. Keep your admin token private.

```text
Valheim Dedicated Server/
└── BepInEx/
    ├── config/valheimone.cfg
    └── plugins/ValheimOne.dll
```

<a id="install-on-your-pc"></a>

### Install on players' PCs

You can join a ValheimOne server with an unmodded game, on PC or console. The live map, the browser console, the alerts and the raid settings in World Events work with nothing installed on your side.

The other gameplay modules are applied by the player's own game, so they only work on a PC that also has ValheimOne. That includes Drops, Gathering and the guardian power settings in World Events. The game rolls the loot of a tree, rock or creature in the game of the player standing near it, so a player without ValheimOne gets normal drops. Consoles cannot install mods.

1. Close Valheim. Open the [latest release](https://github.com/HumanGenome/ValheimOne/releases/latest) and download `ValheimOne-full-<version>.zip` under **Assets**. The green Code button gives you the source, not the mod.
2. In Steam, right click Valheim, then Manage, then Browse local files. Extract the ZIP into that folder so `winhttp.dll` and `BepInEx/` sit next to `valheim.exe`.
3. Start Valheim from Steam. A BepInEx console window opens next to the game. Join the server. It sends its settings to your game at join, and your local config file is not changed.

```text
Valheim/
├── BepInEx/
│   ├── config/valheimone.cfg
│   └── plugins/ValheimOne.dll
├── doorstop_config.ini
├── winhttp.dll
└── valheim.exe
```

- **Already running BepInEx:** use the plugin-only ZIP instead. BepInEx alone changes nothing; the mod is `BepInEx/plugins/ValheimOne.dll`.
- **Mod manager profile:** install [ValheimOne from Thunderstore](https://thunderstore.io/c/valheim/p/HumanGenome/ValheimOne/) in r2modman or Thunderstore Mod Manager and start the game from the manager. If its search does not show it yet, use Import local mod with the plugin-only ZIP. A profile and the game folder are separate installs.
- **Linux and Steam Deck:** set the Steam launch option `./start_game_bepinex.sh %command%`.
- **Check it:** `BepInEx/LogOutput.log` shows `Sent VO_Hello to server` and `Server config applied` after you join. The server log shows `is modded: handshake ok` or `is vanilla: no VO_Hello received within 15s` for each player.
- **Versions:** keep the server and every PC on the same minor version, 0.13.x with 0.13.x. After a Valheim patch, check the releases page before you play.
- **Enforce Mod:** `EnforceMod = true` in `[Server]` kicks vanilla and console players. Leave it `false` on a mixed or crossplay server.
- **Remove it:** delete `BepInEx/plugins/ValheimOne.dll` and `BepInEx/config/valheimone.cfg`. To turn every mod off, rename `winhttp.dll`.

Building from source is `./build.sh`; see [RELEASING.md](RELEASING.md) for packaging.

---

## Settings

Everything lives in one file: `BepInEx/config/valheimone.cfg`. Start the server once to generate it, then turn on the sections you want. Saving the file applies most changes live, without a restart. Keep your file when you update.

| Section | What it does | Out of the box |
|---|---|---|
| `[LiveMap]` | The map, console, sharing and fog | Off |
| `[Network]` | The higher [send limit](#less-lag) | On |
| `[Server]` | Player limit, Enforce Mod, settings sent to players | On, with Valheim's limit of 10 |
| `[ActivityLog]` | The activity feed behind the map's Sagas panel, 30 days | On |
| `[Discord]` | Discord alerts | Off |
| `[Query]` | Server list query ([reference](docs/query.md)) | Off |
| 26 gameplay sections | [Each module and what it needs](docs/gameplay-modules.md) | Off |

```ini
[Player]

Enabled = true
BaseMaximumWeight = 450
MegingjordBuff = 200
```

<a id="fog-and-shared-map-layers"></a>

### Fog and Shared map layers

All of these live in `[LiveMap]`. Set `FogMode = trails` or `explored` to record the fog you want to display. The default fog is a translucent tint over the full terrain.

`FogHideUnexplored = true` gives the Public view a solid cover and withholds unexplored region names and spawn and trader markers. Public viewers cannot turn it off. Check the tokenless link before you share it.

`SharedFog = true` gives Shared viewers the same locked cover while keeping chat and stats. It defaults to false.

`SharedPoiGroups = all` keeps every point-of-interest layer in the Shared view. Use `none` to hide them, or a space-separated list such as `spawn trader` to show only those groups. The [group keys](docs/query.md#shared-point-of-interest-groups) are in the API reference.

Admins can switch on **Fog preview** under Layers to keep unexplored areas covered on their own map. It starts off, also hides unexplored markers and region names, and does not change admin permissions. It needs `FogMode` set.

Fog changes what the map draws. It does not restrict access to the underlying terrain images.

---

## Commands

Type these in the browser console. `vo help` lists them all and `vo help <command>` explains one.

| Command | What it does |
|---|---|
| `vo players` | Connected players, session times and IDs |
| `vo save` | Save the world and connected player profiles |
| `vo shutdown 60 Restarting` | Shut down after a save and a warning, `vo shutdown cancel` to stop it |
| `vo kick <player>`, `vo ban <player>`, `vo unban <id>` | Remove or ban a player by name or ID |
| `vo item <name>` | Recipes, sources and drops for an item |
| `vo doctor` | Plugin, module, settings and map health |

---

## Verifying the binary

Every release can be rebuilt byte for byte from its tag. The steps are in [docs/verifying.md](docs/verifying.md).

---

## Getting help

Bugs and feature requests go to [GitHub Issues](https://github.com/HumanGenome/ValheimOne/issues). If you rent your server, hosting and control-panel questions belong with your provider.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

---

## License

MIT. See [LICENSE](LICENSE).

---

## Credits

[BepInEx](https://github.com/BepInEx/BepInEx) and [Harmony](https://github.com/pardeike/Harmony) load and patch the mod. The map draws with [Leaflet](https://leafletjs.com). The higher send limit takes its idea from [Better Networking](https://github.com/CW-Jesse/valheim-betternetworking) by CW_Jesse (MIT); ValheimOne's code is its own. Fonts: Metamorphous and Averia Serif Libre (SIL Open Font License).
