<p align="center">
  <img src="docs/brand/hero.png" alt="ValheimOne" width="100%">
</p>

<p align="center">
  <b>Everything your Valheim dedicated server is missing.</b><br>
  A live world map you can hand to your players, a searchable item codex, a browser admin console,<br>
  Discord alerts and server-enforced rules &mdash; server-side only, on a completely vanilla client.
</p>

<p align="center">
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/License-MIT-c9a959?style=for-the-badge"></a>
  <a href="https://github.com/BepInEx/BepInEx"><img alt="BepInEx 5.4.x" src="https://img.shields.io/badge/BepInEx-5.4.x-2f80ed?style=for-the-badge"></a>
  <a href="https://store.steampowered.com/app/892970/"><img alt="Valheim Dedicated Server" src="https://img.shields.io/badge/Valheim-Dedicated_Server-1b2838?style=for-the-badge&logo=steam&logoColor=white"></a>
  <a href="#features"><img alt="Client Mods: None" src="https://img.shields.io/badge/Client_Mods-None_Required-2ea043?style=for-the-badge"></a>
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

Your whole world in a browser, drawn from the server seed and updating in real time. Players, ships, carts, portals, tombstones, wards and beds move as they happen. Fog-of-war can mirror exactly what your vikings have actually charted.

![ValheimOne Live Map, admin view](docs/screenshots/livemap-admin.png)

Turn on the heatmap to see where everyone has been over the last day or week, or open the Sagas leaderboard for playtime, deaths and distance travelled.

<a id="sharing"></a>

### 🔗 Share It With Your Players

Three views, and you decide who gets which. **Admin** sees everything. **Shared** shows live players and every layer but never grants a single admin action. **Public** is read-only and fog-covered, safe to post in your Discord.

![ValheimOne public map with explored fog](docs/screenshots/livemap-public-fog.png)

<a id="dungeons"></a>

### 🏛️ Look Inside Dungeons

Click any dungeon entrance and get a top-down room schematic read straight from the game's own generated layout, with live players drawn inside it. No more wondering where someone vanished to.

![ValheimOne dungeon interior viewer](docs/screenshots/dungeon-interior.png)

<a id="codex"></a>

### 📖 Codex of Items

Every one of roughly 1,084 items, searchable and filterable: weight, stack size, tiers, damage, armour, full recipes with station requirements, what drops it, and what it is used to make. Jump straight from an ingredient to everything that needs it.

![ValheimOne Codex of Items](docs/screenshots/codex-of-items.png)

<a id="console"></a>

### 💻 Admin Console In The Browser

A live server log and a command box with history and autocomplete, running whitelisted commands through the game's own console. Kick, ban, save the world, or schedule a graceful shutdown that warns players on the way down.

![ValheimOne web admin console](docs/screenshots/console-tab.png)

### ⚙️ Server Rules That Actually Stick

Twenty-six opt-in modules covering carry weight, stamina, food, drops, gathering, build rules, portals, taming, raids, production speeds and more. Everything is off until you turn it on, and most values hot-reload without a restart.

See [docs/gameplay-modules.md](docs/gameplay-modules.md) for the full list.

### 🔔 Discord Alerts

Joins, leaves, deaths with the biome they died in, raids starting and ending, world saves and new days. Point it at a webhook and pick which events you care about.

### 📡 Server Browser Support

A standalone A2S query responder so server browsers, monitoring tools and hosting panels can see your server, including crossplay servers that do not answer A2S on their own. Richer JSON lives at `/api/status` and `/api/players`.

See [docs/query.md](docs/query.md).

---

<a id="install"></a>

## Installation

Two ways in. Pick one.

### 🚀 Option 1 &mdash; Rent a server, skip all of it

[**Get a Valheim server from SurvivalServers**](https://www.survivalservers.com/services/game_servers/valheim/?utm_source=github&utm_medium=readme&utm_campaign=valheim_one) and ValheimOne is **already installed, already configured and updated for you automatically**. No BepInEx, no file uploads, no config editing. Your map, console and share links are live the moment the server boots, wired straight into the control panel.

### 🔧 Option 2 &mdash; Install it yourself

You need a Valheim Dedicated Server with [BepInEx 5.4.x](https://github.com/BepInEx/BepInEx).

1. Download the latest zip from [Releases](https://github.com/HumanGenome/ValheimOne/releases). Take the plugin-only package if you already run BepInEx, or the bundled package for a fresh install.
2. Drop `ValheimOne.dll` into `BepInEx/plugins/`.
3. Start the server. ValheimOne writes `BepInEx/config/valheimone.cfg` on first boot.

```text
Valheim Dedicated Server/
└── BepInEx/
    └── plugins/
        └── ValheimOne.dll
```

Building from source is `./build.sh`; see [RELEASING.md](RELEASING.md) for packaging.

---

## Configuration

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

---

## License

MIT. See [LICENSE](LICENSE).

---

_ValheimOne is a community project and is not affiliated with or endorsed by Iron Gate Studio._
