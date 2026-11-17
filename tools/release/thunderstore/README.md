# ValheimOne

A Valheim dedicated server has no map, no browser console and no alerts of its own, and it stops at 10 players. ValheimOne adds them: a live web map you can share, an admin console in your browser, item and recipe search, Discord alerts, a higher player limit and 26 optional gameplay modules.

The map, console and alerts run on the server and work with unmodded players, including console players. Open source under the MIT licence: [source, issues and documentation on GitHub](https://github.com/HumanGenome/ValheimOne).

_ValheimOne is a community project and is not affiliated with or endorsed by Iron Gate Studio._

## Who installs it

| Where | Needed |
|---|---|
| Dedicated server | Always. Everything runs here. |
| A player's PC | Only for gameplay modules marked **Synced**. The server sends its settings to that player on join. |
| Console players | Nothing. They can join and use the shared map link. Synced modules do not apply to them. |

Keep the server and every PC on the same minor version, 0.13.x with 0.13.x.

## Features

### Live map

The map draws the whole world from the server seed and updates in real time. Players, ships, carts, portals, tombstones, wards and beds all show on it. Fog of war follows where players have walked and what they shared through cartography tables. It also has a heatmap, a world timelapse and the Sagas leaderboard for playtime, deaths and distance.

![Live map, admin view](https://github.com/HumanGenome/ValheimOne/raw/main/docs/screenshots/thunderstore/live-map-20261016.png)

### Share the map with your players

**Admin** sees everything. **Shared** shows live players and the layers the owner allows, with no admin actions. **Public** is read-only with its own fog and layer settings. With `FogMode = trails` or `explored`, `FogHideUnexplored = true` covers everything nobody has explored yet, region names and markers included. Fog only changes what the map draws: someone with the link can still load the terrain images under it.

![Public map with explored fog](https://github.com/HumanGenome/ValheimOne/raw/main/docs/screenshots/thunderstore/public-fog-20261013.png)

### Dungeon maps

Click any dungeon entrance for a top-down map of its rooms, with live players drawn inside it.

![Dungeon interior viewer](https://github.com/HumanGenome/ValheimOne/raw/main/docs/screenshots/thunderstore/dungeon-interior-20261013.png)

### Item and recipe search

Search and filter every item and recipe: weight, stack size, tiers, damage, armour, full recipes with station requirements, what drops it and what it's used to make.

![Item codex](https://github.com/HumanGenome/ValheimOne/raw/main/docs/screenshots/thunderstore/codex-20261013.png)

### Server console in your browser

Watch the server log live and type commands with history and autocomplete. Kick, ban, save the world, or schedule a shutdown that warns players first. It's off until you turn it on.

![Browser admin console](https://github.com/HumanGenome/ValheimOne/raw/main/docs/screenshots/thunderstore/console-20261013.png)

### Gameplay modules

26 optional modules for carry weight, stamina, food, drops, gathering, build rules, portals, taming, raids, production speeds and more. All are off until you turn them on, and most values apply without a restart. Each module, and which ones are Synced, is listed in [gameplay-modules.md](https://github.com/HumanGenome/ValheimOne/blob/main/docs/gameplay-modules.md).

### Discord alerts and server lists

Post joins, leaves, deaths with their biome, raids, world saves and new days to a Discord webhook. Server browsers and monitoring tools can see the server, crossplay servers included. Player and server details are also at `/api/status` and `/api/players`. See [query.md](https://github.com/HumanGenome/ValheimOne/blob/main/docs/query.md).

### New world seed and save summary

`NewWorldSeed` in `[Server]` gives a dedicated server's new world the seed you choose. It is used once, only when that world does not exist yet. After every save that works, the server writes `<world>.valheimone.json` beside the world with the day, the bosses defeated, who played and the versions.

### Less lag when you play together

When several players play close together, the server runs out of room for world updates and players and creatures rubber-band. ValheimOne raises that limit from 10 KB to 32 KB per player, on Steam and crossplay, and lets Steam send up to 1 MB/s per player instead of 150 KB/s. It's on by default and runs on the server only. `Enabled = false` in `[Network]` turns it off, and it turns itself off when Better Networking or another mod that changes the same limit is installed.

## Install

### On a player's PC with a mod manager

1. Install ValheimOne into your profile in r2modman or Thunderstore Mod Manager. BepInExPack_Valheim is installed with it.
2. Start the game from the manager with **Start modded** and join the server.
3. `BepInEx/LogOutput.log` in the profile shows `Sent VO_Hello to server` and `Server config applied` after you join.

The server's settings don't change your own config file.

### On a dedicated server

The server needs [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/).

1. Stop the server. Copy `plugins/ValheimOne.dll` from this package to `BepInEx/plugins/ValheimOne.dll` in the server folder.
2. Start the server once so it writes `BepInEx/config/valheimone.cfg`, then stop it. This package ships no config file, so an update never overwrites yours.
3. In the `[LiveMap]` section, set `Enabled = true`, pick a long `AccessToken` of your own, and keep `PublicView = false` for now. The live map is off until you do this.
4. Start the server and wait for the world to load. On your own network, open `http://your-server-ip:8790/?token=YOUR_ADMIN_TOKEN` (the map uses TCP port `8790`). Over the internet, put it behind an HTTPS reverse proxy. Behind a proxy, set `RelayKey` to a long random value and have the proxy send it in an `X-Relay-Key` header. The map then answers only requests that carry it, or that come from the server itself.
5. Optional: `ConsoleEnabled = true` to type commands in the browser, `EntityLayer = true` to show ships, carts and portals, and a separate `ShareToken` or `PublicView = true` to share the map. Keep the admin token to yourself.

`EnforceMod = true` in `[Server]` kicks players without ValheimOne, including console players. Leave it `false` on a mixed or crossplay server.

## Compatibility

Tested on the Valheim 1.0.21 dedicated server with BepInExPack_Valheim 5.4.2351. After a Valheim patch, check the changelog before you update.

## Remove it

Delete `ValheimOne.dll` and `BepInEx/config/valheimone.cfg`, or uninstall it from the mod manager profile.

## Bugs and source

Report bugs on [GitHub Issues](https://github.com/HumanGenome/ValheimOne/issues). The DLL is a deterministic build: `tools/package-thunderstore.sh` at the release tag rebuilds this package byte for byte.
