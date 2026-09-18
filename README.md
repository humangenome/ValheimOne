<p align="center">
  <img src="docs/brand/hero.png" alt="ValheimOne" width="100%">
</p>

<p align="center">
  <b>Everything your Valheim dedicated server is missing.</b><br>
  A live world map you can hand to your players, a searchable item codex, a browser admin console,<br>
  Discord alerts and optional gameplay rules. The map and admin tools work with vanilla clients.
</p>

<p align="center">
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/License-MIT-c9a959?style=for-the-badge"></a>
  <a href="https://github.com/BepInEx/BepInEx"><img alt="BepInEx 5.4.x" src="https://img.shields.io/badge/BepInEx-5.4.x-2f80ed?style=for-the-badge"></a>
  <a href="https://store.steampowered.com/app/892970/"><img alt="Valheim Dedicated Server" src="https://img.shields.io/badge/Valheim-Dedicated_Server-1b2838?style=for-the-badge&logo=steam&logoColor=white"></a>
  <a href="#features"><img alt="Map and console: vanilla clients" src="https://img.shields.io/badge/Map_and_Console-Vanilla_Clients-2ea043?style=for-the-badge"></a>
</p>

<p align="center">
  <a href="#live-map"><b>Live Map</b></a> &nbsp;&bull;&nbsp;
  <a href="#sharing"><b>Sharing</b></a> &nbsp;&bull;&nbsp;
  <a href="#dungeons"><b>Dungeons</b></a> &nbsp;&bull;&nbsp;
  <a href="#codex"><b>Codex</b></a> &nbsp;&bull;&nbsp;
  <a href="#console"><b>Console</b></a> &nbsp;&bull;&nbsp;
  <a href="#install"><b>Install</b></a>
</p>

---

<a id="features"></a>

## Features

<a id="live-map"></a>

### 🗺️ Live World Map

Your whole world in a browser, drawn from the server seed and updating in real time. Players, ships, carts, portals, tombstones, wards and beds move as they happen. Fog-of-war follows recorded player trails and exploration shared through cartography tables.

![ValheimOne Live Map, admin view](docs/screenshots/valheimone-live-map-20260909-r3.png)

Turn on the heatmap to see where everyone has been over the last day or week, open World Timelapse to scrub through explored fog, base growth and aggregate movement history, or view the Sagas leaderboard for playtime, deaths and distance travelled.

<a id="sharing"></a>

### 🔗 Share It With Your Players

Three views, and you decide who gets which. **Admin** sees everything. **Shared** shows live players and every layer but never grants a single admin action. **Public** is read-only, with its own fog and public-layer settings. Check the tokenless link before sharing it. With `FogMode = trails` or `explored`, enable `FogHideUnexplored = true` for an opaque cover and to withhold unexplored region names and every allowed public marker (spawn, trader, bosses, dungeons, ores, structures, last seen, ships, portals, and the rest). Public viewers cannot turn that cover off through the map controls. The default fog is a translucent tint; Admin and Shared views still show the full terrain. Fog controls the displayed map, not access to the underlying terrain tiles.

![ValheimOne public map with explored fog](docs/screenshots/livemap-public-fog.png)

<a id="dungeons"></a>

### 🏛️ Look Inside Dungeons

Click any dungeon entrance and get a top-down room schematic read straight from the game's own generated layout, with live players drawn inside it. No more wondering where someone vanished to.

![ValheimOne dungeon interior viewer](docs/screenshots/dungeon-interior.png)

<a id="codex"></a>

### 📖 Codex of Items

Searchable, filterable items and recipes: weight, stack size, tiers, damage, armour, full recipes with station requirements, what drops it, and what it is used to make. Jump straight from an ingredient to everything that needs it.

![ValheimOne Codex of Items](docs/screenshots/valheimone-codex-20260909-r3.png)

<a id="console"></a>

### 💻 Admin Console In The Browser

A live server log and a command box with history and autocomplete, running whitelisted commands through the game's own console. Kick, ban, save the world, or schedule a graceful shutdown that warns players on the way down.

![ValheimOne web admin console](docs/screenshots/valheimone-console-20260909-r3.png)

### ⚙️ Server Rules That Actually Stick

Twenty-six opt-in modules covering carry weight, stamina, food, drops, gathering, build rules, portals, taming, raids, production speeds and more. Everything is off until you turn it on, and most values hot-reload without a restart.

Modules marked **Synced** need ValheimOne on each participating PC; the server then sends those players its settings. Console players cannot install those client mods. The live map, browser console and alerts do not require a client plugin. See [docs/gameplay-modules.md](docs/gameplay-modules.md) for each module's scope.

### 🔔 Discord Alerts

Joins, leaves, deaths with the biome they died in, raids starting and ending, world saves and new days. Point it at a webhook and pick which events you care about.

### 📡 Server Browser Support

A standalone A2S query responder so server browsers, monitoring tools and hosting panels can see your server, including crossplay servers that do not answer A2S on their own. Richer JSON lives at `/api/status` and `/api/players`.

See [docs/query.md](docs/query.md).

---

<a id="install"></a>

## Installation

The [Steam setup guide](https://steamcommunity.com/sharedfiles/filedetails/?id=3802119003) walks through installation, private map access, sharing and browser commands.

### Install on your server

You need a Valheim Dedicated Server with the Valheim-compatible [BepInEx pack](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/).

1. Download the current [GitHub release](https://github.com/HumanGenome/ValheimOne/releases/latest). Use the plugin-only ZIP if BepInEx is installed, or the Full ZIP for a fresh setup.
2. Stop the server and extract the ZIP into its root folder. The plugin belongs at `BepInEx/plugins/ValheimOne.dll`. Preserve your existing `BepInEx/config/valheimone.cfg` when updating; do not replace it with the packaged defaults. On Linux, use the Full pack's `start_server_bepinex.sh` with your usual server arguments.
3. Start once to generate any missing config, then stop before the initial configuration. In the existing `[LiveMap]` section of `BepInEx/config/valheimone.cfg`, set `Enabled = true`, choose a long unique `AccessToken`, and set `PublicView = false` while configuring access. The live map is disabled by default.
4. Start the server and wait for the world to load. The default map port is TCP `8790`. On a trusted network, open `http://your-server-ip:8790/?token=YOUR_ADMIN_TOKEN` with your own values. Use an HTTPS reverse proxy for authenticated access over the Internet.
5. Set `ConsoleEnabled = true` in `[LiveMap]` if you want browser commands. Set `EntityLayer = true` to collect the optional ships, carts and portals layer. For a public link, `PublicPoiGroups` and `PublicEntityGroups` stay narrow until you name extra groups, and `PublicChat`, `PublicLeaderboard`, and `PublicEvents` stay off. For sharing, configure a separate `ShareToken` or enable the tokenless `PublicView`, then choose the fog and public layers. Keep your admin token private.

```text
Valheim Dedicated Server/
└── BepInEx/
    ├── config/valheimone.cfg
    └── plugins/ValheimOne.dll
```

> **Official Hosting:** ValheimOne is included with [Valheim server hosting from SurvivalServers](https://www.survivalservers.com/services/game_servers/valheim/?utm_source=github&utm_medium=readme&utm_campaign=valheim_one), with map and console controls in the control panel. You can also install the free mod on your own dedicated server using the steps above.

Building from source is `./build.sh`; see [RELEASING.md](RELEASING.md) for packaging.

---

## Verifying the binary

A byte-for-byte reproduction requires a Git clone checked out at the exact release tag, the .NET SDK pinned in `global.json`, and a clean working tree. The compile is deterministic and embeds no commit SHA or build-host paths, so identical sources and toolchain produce an identical DLL.

```bash
git clone https://github.com/HumanGenome/ValheimOne.git
cd ValheimOne
git checkout --detach v<version>
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version 10.0.302 --install-dir "$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
tools/verify-reproducible.sh --release v<version>
```

The verifier clean-builds the DLL and checks it against both the checked-in release provenance and the DLL inside the GitHub release asset.

```text
building HEAD commit: <40-hex commit>
verified: clean build ValheimOne.dll <sha256>
verified: published v<version> plugin zip ValheimOne.dll <sha256>
REPRODUCIBLE <version>
```

---

## Configuration

### Fog and Shared map layers

In `[LiveMap]`, set `FogMode = trails` or `explored` to record the fog you want to display.
`SharedFog = true` gives Shared viewers a locked opaque cover and hides unexplored
points of interest and region names while preserving chat and stats. It defaults to false.
Public opaque fog remains controlled by `FogHideUnexplored`.

`SharedPoiGroups = all` preserves all point-of-interest layers. Use `none` to hide them,
or a space-separated selection such as `spawn trader` to show only those groups.
The setting applies to both the layer list and direct point-of-interest requests.
[Available group keys](docs/query.md#shared-point-of-interest-groups) are documented in the API reference.

The tokenless public view keeps a narrower default: `PublicPoiGroups = spawn trader`.
Add category keys such as `bosses`, `dungeons`, `spawners`, `ores`, and `structures`,
or individual group keys, to show those markers on a public link. `live` adds Last seen.
`PublicEntityGroups = none` hides live ships, portals, carts, wards, beds, and tombstones;
set `EntityLayer = true` and name groups such as `ship portal cart ward bed tombstone`
to publish them. `PublicChat`, `PublicLeaderboard`, and `PublicEvents` add the read-only
chat panel, wipe leaderboard, and raid overlay; all three default off. `all` remains
available for the group lists. With `FogMode = trails` or `explored` and
`FogHideUnexplored = true`, unexplored terrain stays covered and only explored markers
are served, including the extra public layers. Player-made pins, chat, and the
leaderboard are not fog-gated. These settings are server-authoritative and apply live
after the configuration reloads.

Admins can enable **Fog preview** in Layers when `FogMode` is enabled. It starts off and
remembers the choice separately from Public fog. Turning it on also hides unexplored
point-of-interest markers and region names; it does not change admin permissions.
Fog is a map-display feature and does not restrict access to underlying terrain images.
These settings apply live after the configuration reloads; preserve existing configuration when updating.


Everything lives in one file: `BepInEx/config/valheimone.cfg`. Every gameplay section is off by default, so a fresh install changes nothing until you opt in.

Start the server once to generate the file, then enable the sections you want:

```ini
[Player]

Enabled = true
BaseMaximumWeight = 450
MegingjordBuff = 200
```

Saving the file applies changed values live. Only changes that alter patch topology need a restart.

- Full module list and what each one does: [docs/gameplay-modules.md](docs/gameplay-modules.md)
- Query and status endpoints: [docs/query.md](docs/query.md)

---

## Getting Help

Bugs and feature requests go to [GitHub Issues](https://github.com/HumanGenome/ValheimOne/issues). If you rent your server, hosting and control-panel questions belong with your provider.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

If ValheimOne helps your group, a GitHub star is appreciated.

---

## License

MIT. See [LICENSE](LICENSE).

---

_ValheimOne is a community project and is not affiliated with or endorsed by Iron Gate Studio._
