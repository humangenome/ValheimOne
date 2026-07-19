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
[![Client Mods: Not Required](https://img.shields.io/badge/Client_Mods-Not_Required-brightgreen.svg)](#features)

Everything your Valheim dedicated server is missing: a live map, a web console, server-enforced settings, and a server query endpoint — delivered server-side with no client mods required.

_ValheimOne is a community project and is not affiliated with or endorsed by Iron Gate Studio._

> **Official Hosting:** [SurvivalServers.com](https://www.survivalservers.com/services/game_servers/valheim/?utm_source=github&utm_medium=readme&utm_campaign=valheim_one) offers managed Valheim dedicated servers with BepInEx support for ValheimOne.

**Status — unreleased / in development.** The configuration foundation and first gameplay module are implemented; the live map, web console, and status endpoint are in development. There is no packaged release yet, and every feature is disabled by default.

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

The first module exists today: `[Player]` controls base carry weight and the Megingjord bonus. The server owns the configured values, and additional modules follow the same isolated, default-off contract.

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

Every section is opt-in. This `[Player]` example raises base carry weight to 450 and changes the Megingjord bonus to 200:

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
