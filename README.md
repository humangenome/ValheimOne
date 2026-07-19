<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/brand/valheimone-landscape-dark.png">
    <img src="docs/brand/valheimone-landscape.png" alt="ValheimOne" width="550">
  </picture>
</p>

# ValheimOne

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![BepInEx 5.4.x](https://img.shields.io/badge/BepInEx-5.4.x-2f80ed.svg)](https://github.com/BepInEx/BepInEx)
[![Valheim — Dedicated Server](https://img.shields.io/badge/Valheim-Dedicated_Server-1b2838.svg?logo=steam&logoColor=white)](https://store.steampowered.com/app/892970/)
[![Client Mods: Optional](https://img.shields.io/badge/Client_Mods-Optional-brightgreen.svg)](#features)

Everything your Valheim dedicated server is missing: a live map, a web console, server-enforced settings, and a server query endpoint — with vanilla-compatible server features and synchronized client features where game ownership requires them.

_ValheimOne is a community project and is not affiliated with or endorsed by Iron Gate Studio._

> **Official Hosting:** [SurvivalServers.com](https://www.survivalservers.com/services/game_servers/valheim/?utm_source=github&utm_medium=readme&utm_campaign=valheim_one) offers managed Valheim dedicated servers with BepInEx support for ValheimOne.

**Status — unreleased / in development.** The server enforcement chassis and eighteen default-off gameplay modules are implemented; the web console and status endpoint remain in development. There is no packaged release yet. Gameplay features are disabled by default; the `[Server]` transport infrastructure is enabled by default but does not alter gameplay on its own.

---

## Table of Contents

- [Features](#features)
- [Installation](#installation)
- [Using ValheimOne](#using-valheimone)
- [Contributing](#contributing)
- [License](#license)

---

## Features

### Live Map

🚧 **In development.**

Builds a Sea-Chart-grade procedural world render directly from the server seed, then cuts it into a zoomable tile pyramid for fast browser navigation. Live player positions, points of interest, map pins, and explored fog-of-war update over the generated terrain.

Map generation and runtime data stay on the server. Players join with fully vanilla clients; the browser map does not require a Valheim client mod.

![ValheimOne Live Map](docs/screenshots/live-map.png)

### Web Admin Console

🚧 **In development.**

Provides an authenticated dashboard for remote server administration. Operators can inspect server and player status, run commands, follow command output, and review or update the live ValheimOne configuration from one browser session.

![ValheimOne Web Admin Console](docs/screenshots/web-admin-console.png)

### Server-Enforced Settings

Uses one `BepInEx/config/valheimone.cfg` file as the server ruleset. Typed sections keep Boolean, integer, float, and percentage settings explicit; every feature has its own `Enabled = false` gate, so installing ValheimOne changes nothing until an operator opts in.

The enforcement chassis exchanges `VO_Hello`, `VO_Config`, and `VO_Ack` over routed RPC. In `[Server]`, `EnforceMod = false` permits vanilla clients; setting it to `true` kicks vanilla or mismatched clients after `HandshakeGraceSeconds`. `SyncConfig = true` sends compatible clients a chunked, acknowledgement-gated ruleset. Clients apply it as a data-only in-memory overlay, clear it on disconnect, and never receive `ClientOnly` sections. Compatible clients can therefore be hot-enabled by a server config push without installing new patches.

Features use three modes: **server-authoritative** logic runs under server ownership and can support vanilla clients; **synced** logic requires a compatible client and receives the server overlay; **client-only** settings stay local and are never pushed. The current gameplay modules are:

- `Player` (`[Player]`) — sets carry weight, the Megingjord bonus, auto-pickup range, encumbered pickup, and rested seconds per comfort level. **Mode:** server-authoritative.
- `PlayerStamina` (`[Stamina]`) — scales stamina regeneration, delay, movement drains, and action costs. **Mode:** synced.
- `BuildingQoL` (`[Building]`) — removes structural-support requirements, suppresses ordinary placement blocking, and overrides build reach and rotation step. **Mode:** synced.
- `FoodDuration` (`[Food]`) — scales food duration and can hold benefits at full strength until expiry. **Mode:** synced.
- `ItemTweaks` (`[Items]`) — scales item stack sizes, weights, and maximum durability. **Mode:** synced.
- `ItemDropMultiplier` (`[Drops]`) — scales destructible, creature, and pickable yields. **Mode:** server-authoritative.
- `Gathering` (`[Gathering]`) — applies per-material yield modifiers and adjusts supported non-guaranteed drop chances. **Mode:** server-authoritative.
- `CraftFromChest` (`[CraftFromChest]`) — consumes crafting and optional build costs from nearby accessible containers. **Mode:** synced.
- `StationAutomation` (`[StationAutomation]`) — pulls fuel and processable items from nearby containers for smelter-based stations and fireplaces. **Mode:** synced.
- `DayNightLength` (`[Time]`) — scales or absolutely overrides the full day/night cycle length. **Mode:** synced.
- `Beehive` (`[Beehive]`) — overrides honey production time and storage capacity. **Mode:** synced.
- `Fermenter` (`[Fermenter]`) — overrides fermentation time. **Mode:** synced.
- `SapCollector` (`[SapCollector]`) — overrides sap production time and storage capacity. **Mode:** synced.
- `Wards` (`[Wards]`) — overrides ward protection radius. **Mode:** synced.
- `Portals` (`[Portals]`) — disables portal travel or permits normally restricted inventory. **Mode:** synced.
- `ExperienceRates` (`[Experience]`) — applies global and per-skill experience multipliers. **Mode:** synced.
- `DeathPenalty` (`[DeathPenalty]`) — scales death skill loss or preserves inventory without a tombstone. **Mode:** synced.
- `MapSharing` (`[MapSharing]`) — forces compatible clients to share positions and synchronizes their combined explored-map area. **Mode:** synced.

**Client-only:** n/a; no current gameplay module uses this mode.

![ValheimOne Server-Enforced Settings](docs/screenshots/server-enforced-settings.png)

### Server Query / Status

🚧 **In development.**

Adds a compact status endpoint for server browsers, uptime monitors, and operator tooling. The response reports server identity, game and ValheimOne versions, availability, player counts, uptime, and the active feature set without requiring a game connection.

![ValheimOne Server Query Status](docs/screenshots/server-query-status.png)

---

## Installation

ValheimOne requires a Valheim Dedicated Server with [BepInEx 5.4.x](https://github.com/BepInEx/BepInEx) installed.

ValheimOne is unreleased, so build it from source for now:

```bash
./build.sh
```

The build produces `src/ValheimOne/bin/Release/net472/ValheimOne.dll`. Copy that file into the dedicated server's `BepInEx/plugins/` directory:

```text
Valheim Dedicated Server/
└── BepInEx/
    └── plugins/
        └── ValheimOne.dll
```

Start the dedicated server normally. ValheimOne loads through BepInEx and creates `BepInEx/config/valheimone.cfg` on first boot.

---

## Using ValheimOne

### Configure the Server Ruleset

1. Start the dedicated server once so ValheimOne writes `BepInEx/config/valheimone.cfg` with documented defaults.
2. Stop the server and open the configuration file.
3. Enable only the feature sections you want and set their typed values.
4. Start the server again to apply the ruleset.

Every gameplay section is opt-in. The `[Server]` infrastructure section is the sole default-on exception. Sections include `[Player]` for carry weight, `[Stamina]` for stamina regeneration and costs, and `[Time]` for day/night length. This `[Player]` example raises base carry weight to 450 and changes the Megingjord bonus to 200:

```ini
[Player]

Enabled = true
BaseMaximumWeight = 450
MegingjordBuff = 200
```

`BaseMaximumWeight` is the absolute unmodified carry limit; `MegingjordBuff` is the absolute bonus applied when the belt is active. Their Valheim defaults are 300 and 150 respectively.

The current development build detects configuration-file changes, but feature patches are applied at startup. Restart the dedicated server after an edit.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

---

## License

MIT. See [LICENSE](LICENSE).
