# ValheimOne

A BepInEx plugin for Valheim dedicated servers. It adds a live world map in the browser, a dungeon interior viewer, an item codex, a browser admin console, Discord alerts, an A2S query responder and 26 opt-in gameplay modules.

The map, console and alerts run on the server and work with unmodded players, including console players. Open source under the MIT licence: [source, issues and documentation on GitHub](https://github.com/HumanGenome/ValheimOne).

## Who installs it

| Where | Needed |
|---|---|
| Dedicated server | Always. Everything runs here. |
| A player's PC | Only for gameplay modules marked **Synced**. The server sends its settings to that player on join. |
| Console players | Nothing. They can join and use the shared map link. Synced modules do not apply to them. |

Keep the server and every PC on the same minor version, 0.13.x with 0.13.x.

## Features

### Live world map

The whole world in a browser, drawn from the server seed and updated in real time. Players, ships, carts, portals, tombstones, wards and beds move as they happen. Fog of war follows recorded player trails and exploration shared through cartography tables. A heatmap, a world timelapse and a playtime, deaths and distance leaderboard are included.

![Live map, admin view](https://github.com/HumanGenome/ValheimOne/raw/main/docs/screenshots/valheimone-live-map-20260909-r3.png)

### Three views

**Admin** sees everything. **Shared** shows live players and every layer but grants no admin action. **Public** is read-only with its own fog and layer settings. With `FogMode = trails` or `explored`, `FogHideUnexplored = true` gives an opaque cover and withholds unexplored region names and markers. Fog controls the displayed map, not access to the underlying terrain tiles.

![Public map with explored fog](https://github.com/HumanGenome/ValheimOne/raw/main/docs/screenshots/livemap-public-fog.png)

### Dungeon interiors

Click a dungeon entrance for a top-down room schematic read from the game's own generated layout, with live players drawn inside it.

![Dungeon interior viewer](https://github.com/HumanGenome/ValheimOne/raw/main/docs/screenshots/dungeon-interior.png)

### Item codex

Searchable items and recipes: weight, stack size, tiers, damage, armour, full recipes with station requirements, what drops an item and what it is used to make.

![Item codex](https://github.com/HumanGenome/ValheimOne/raw/main/docs/screenshots/valheimone-codex-20260909-r3.png)

### Browser admin console

A live server log and a command box with history and autocomplete. It runs whitelisted commands through the game's own console: kick, ban, save, or a scheduled shutdown that warns players first. Off by default.

![Browser admin console](https://github.com/HumanGenome/ValheimOne/raw/main/docs/screenshots/valheimone-console-20260909-r3.png)

### Gameplay modules

Twenty-six opt-in modules covering carry weight, stamina, food, drops, gathering, build rules, portals, taming, raids, production speeds and more. Every module is off until you enable it, and most values reload without a restart. Each module's scope, including which ones are Synced, is listed in [gameplay-modules.md](https://github.com/HumanGenome/ValheimOne/blob/main/docs/gameplay-modules.md).

### Discord alerts and server query

Joins, leaves, deaths with their biome, raids, world saves and new days can be posted to a Discord webhook. A standalone A2S responder lets server browsers and monitoring tools see the server, including crossplay servers that do not answer A2S on their own. JSON status lives at `/api/status` and `/api/players`; see [query.md](https://github.com/HumanGenome/ValheimOne/blob/main/docs/query.md).

## Install

### On a player's PC with a mod manager

1. Install ValheimOne into your profile in r2modman or Thunderstore Mod Manager. BepInExPack_Valheim is installed with it.
2. Start the game from the manager with **Start modded** and join the server.
3. `BepInEx/LogOutput.log` in the profile shows `Sent VO_Hello to server` and `Server config applied` after you join.

Your local config file is not changed by the server's settings.

### On a dedicated server

The server needs [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/).

1. Stop the server. Copy `plugins/ValheimOne.dll` from this package to `BepInEx/plugins/ValheimOne.dll` in the server folder.
2. Start the server once so it writes `BepInEx/config/valheimone.cfg`, then stop it. This package ships no config file, so an update never overwrites yours.
3. In the `[LiveMap]` section set `Enabled = true`, choose a long unique `AccessToken`, and keep `PublicView = false` while you set up access. The live map is off by default.
4. Start the server and wait for the world to load. The default map port is TCP `8790`. On a trusted network open `http://your-server-ip:8790/?token=YOUR_ADMIN_TOKEN`. Use an HTTPS reverse proxy for authenticated access over the Internet.
5. Optional: `ConsoleEnabled = true` for browser commands, `EntityLayer = true` for ships, carts and portals, and a separate `ShareToken` or `PublicView = true` for sharing. Keep the admin token private.

`EnforceMod = true` in `[Server]` kicks players without ValheimOne, including console players. Leave it `false` on a mixed or crossplay server.

## Compatibility

Tested on the Valheim 1.0.15 dedicated server with BepInExPack_Valheim 5.4.2333. After a Valheim patch, check the changelog before you update.

## Remove it

Delete `ValheimOne.dll` and `BepInEx/config/valheimone.cfg`, or uninstall it from the mod manager profile.

## Bugs and source

Report bugs on [GitHub Issues](https://github.com/HumanGenome/ValheimOne/issues). The DLL is a deterministic build; the repository documents how to reproduce it byte for byte from the release tag.
