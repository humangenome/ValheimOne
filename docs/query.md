# Server query endpoint

`GET /api/status` on the LiveMap HTTP port (default `8790`) is the machine query surface for hosting panels and monitoring tools. It is a cheap, cache-free JSON snapshot served directly from memory.

```
curl http://<server-ip>:8790/api/status
```

```json
{
  "serverName": "MyWorld",
  "worldName": "MyWorld",
  "day": 12,
  "uptimeSeconds": 13320,
  "timeOfDay": 0.42,
  "players": 3,
  "view": "public",
  "console": false,
  "map": {
    "state": "ready",
    "progress": 1,
    "textureSize": 2048,
    "pixelSize": 12,
    "worldRadius": 10500,
    "fog": { "mode": "off", "revision": 0, "size": 512 }
  }
}
```

| Field | Meaning |
|---|---|
| `serverName` / `worldName` | World identity. |
| `joinCode` | Current PlayFab crossplay join code. Admin view only; absent from shared/public responses and when no code is available. Treat this value as a server-access credential. |
| `day` | Current in-game day number. |
| `uptimeSeconds` | Seconds since the dedicated server process started. Available at every authorized view tier, including public status responses when `StatusPublic` permits them. |
| `timeOfDay` | 0..1 fraction of the current day. |
| `players` | Players visible at the caller's view level (public callers only see players sharing their position unless names/positions are public). |
| `view` | `admin`, `shared`, or `public` — which view level answered the request. |
| `console` | `true` only for admin-token requests when the web console is enabled. |
| `map.state` / `map.progress` | World-render lifecycle for the map front-end. |

## Access rules

- `[LiveMap] StatusPublic = true` (default): `/api/status` answers **without a token** even when the map itself is token-locked (`AccessToken` set, `PublicView = false`). Tokenless callers get the public view. This is intended for hosting-panel status polls — set it to `false` to require map access for status too.
- `/api/catalog` is always public because it contains static game data only. All remaining endpoints follow the normal map rules: `AccessToken` grants the admin view; `PublicView` controls whether tokenless visitors get the read-only public map.

## GET /api/catalog

`GET /api/catalog` returns the startup-built Codex of Items payload to every view tier, including tokenless callers when `PublicView = false`. It contains no world, player, seed, position, or ZDO data. The server reuses `catalog.json` from the LiveMap sidecar directory when its game, mod, and schema versions match.

```json
{
  "version": { "game": "0.221.12", "mod": "0.11.0", "schema": 1 },
  "generatedUtc": "2026-07-22T12:00:00.000Z",
  "items": [
    {
      "token": "SwordIron",
      "name": "Iron sword",
      "description": "A survivor's friend.",
      "type": "OneHandedWeapon",
      "maxQuality": 4,
      "toolTier": 3,
      "weight": 0.8,
      "maxStackSize": 1,
      "teleportable": true,
      "damage": { "base": { "slash": 55 }, "perLevel": { "slash": 6 } },
      "recipes": [
        {
          "enabled": true,
          "amount": 1,
          "station": { "prefab": "forge", "name": "Forge" },
          "minStationLevel": 1,
          "ingredients": [
            { "prefab": "Iron", "name": "Iron", "amount": 20, "amountPerLevel": 10 }
          ]
        }
      ],
      "sources": [],
      "uses": [],
      "droppedBy": []
    }
  ]
}
```

Armor items add an `armor` base/per-level summary. Conversion outputs list `sources` and their inputs list matching `uses`; methods are `smelter`, `cooking`, or `fermenter`. Creature drops include the creature prefab, localized name, and 0..1 base `chance`. Responses send a strong content-hash `ETag` and `Cache-Control: public, max-age=86400`; a matching `If-None-Match` receives `304 Not Modified`.

## Admin API (token required)

With a non-empty `AccessToken`, the same port serves admin actions; console-specific routes additionally require `[LiveMap] ConsoleEnabled = true`. Every endpoint below returns `401` without the token (query `?token=...` or header `X-LiveMap-Token`), including when `PublicView = true`. With an empty `AccessToken` the admin endpoints stay locked out entirely.

| Endpoint | Method | Purpose |
|---|---|---|
| `/api/console/exec` | POST `{"command":"..."}` | Run a console command via the game's own console path. Commands must be in `ConsoleWhitelist` unless `AllowAllCommands = true` (non-whitelisted → `403`). Returns `{"ok":true,"output":[...]}`. |
| `/api/console/log` | GET `?cursor=N&max=M` | Cursor-based server-log polling from the in-memory ring buffer (`ConsoleLogLines`, default 500). Returns `{"cursor":N,"lines":[...]}` — pass the returned cursor back to get only new lines. |
| `/api/console/meta` | GET | Whitelist, `allowAll` flag, and known command metadata for autocomplete. |
| `/api/admin/kick` | POST `{"player":"name/ip/userID"}` | Kick a player. |
| `/api/admin/ban` / `/api/admin/unban` | POST `{"player":"..."}` | Ban / unban. |
| `/api/admin/banlist` | GET | Current ban list. |
| `/api/admin/chat` | POST `{"text":"..."}` | Broadcast up to 256 characters as a server shout. Limited to 5 sends per 10 seconds and recorded in the admin activity audit. |
| `/api/admin/save` | POST | Trigger a world + profile save. Returns `alreadySaving` when a save was in flight. |
| `/api/admin/shutdown` | POST `{"seconds":60,"message":"Maintenance"}` or `{"action":"cancel"}` | Schedule a save-first server shutdown (5–3600 seconds) or cancel one. Returns the pending deadline and message. `{"cancel":true}` is also accepted. |
| `/api/stats` | GET | Uptime, player/peer counts, ZDO count, Mono heap, frame avg/max ms, world day/time, and pending-shutdown state. |

## Streaming and map-data endpoints

| Endpoint | Method | Purpose |
|---|---|---|
| `/api/events` | GET (SSE) | Server-Sent-Events stream. Map-view auth rules apply. Named events include `players`, change-detected `status`, new `chat` messages for admin/shared views, and — for console-authorized admin tokens only — incremental `log` batches. Sends `retry: 5000`; capped at 8 concurrent streams (`409` beyond). |
| `/api/chat` | GET | Admin/shared only; public returns `404`. Returns the current 32-message ring buffer oldest-first as `{"chats":[{"sequence":1,"x":0,"z":0,"playerName":"...","text":"...","shout":false,"unixMs":1720000000000}]}`. Player chat follows `MirrorChat`; server-originated shouts remain available when mirroring is off. |
| `/api/heatmap` | GET `?window=24h|7d` | Admin/shared only; public returns `404`. Returns the aggregate activity grid used by the default-off Activity Heatmap layer. |
| `/api/leaderboard` | GET | Admin/shared only; public returns `404`. Returns per-wipe playtime, deaths, and distance traveled for up to 50 display names; no platform identifiers are exposed. |
| `/api/dungeons` | GET | Admin/shared only; public returns `404`. Returns discovered dungeon entrances with generation and interior state, room counts, and live-player counts. |
| `/api/dungeons/{id}` | GET | Admin/shared only; public returns `404`. Returns one dungeon's generated room layout and live players inside for the View Interior schematic; unknown IDs return `404`. |
| `/api/entities` | GET | Admin view + `EntityLayer = true` only. Ship/cart/portal positions from ZDO scans (5 s refresh, 500-entity cap) plus the active raid `event` object. |

Admins also get an `"event"` raid object (`{name,x,z,radius,elapsed,duration}` or `null`) on `/api/status` regardless of `EntityLayer`.

Notes:

- POST bodies are JSON, max 8 KB. Send a `Content-Length` header (an empty body with `Content-Length: 0` is fine for `/api/admin/save`; `curl` needs `-d ''`).
- Cheat-gated commands (e.g. `sleep`) execute through the same rules as the in-game console: they report `not valid in the current context` until `devcommands` is enabled. Add `devcommands` to `ConsoleWhitelist` or set `AllowAllCommands = true` if you want that from the web console.
- `say` is not in the default whitelist: the vanilla command is a silent no-op on dedicated servers (it requires a local player).

## A2S query responder

The standalone `[Query]` feature provides an A2S-compatible (Source Engine Query) UDP responder for server browsers, monitoring tools, and hosting-panel query infrastructure. It works when `[LiveMap]` is disabled and gives crossplay servers an A2S surface they do not have natively.

Enable it in `valheimone.cfg`:

```ini
[Query]
Enabled = true
QueryPort = 0
PublicPlayerNames = false
```

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Starts the standalone UDP responder. Changes hot-reload and start or stop the listener. |
| `QueryPort` | `0` | UDP listen port. `0` selects the game port plus 4; port changes hot-reload and restart the listener. |
| `PublicPlayerNames` | `false` | Returns real player names from A2S_PLAYER when enabled; otherwise returns generic `Player N` slots. |

A2S `max_players` reports the effective gameplay cap: `[Server] MaxPlayers` when set, otherwise Valheim's default of 10. (`[Query] MaxPlayers` is deprecated; an existing value in the config file is still honored as a reporting fallback for this release when `[Server] MaxPlayers` is unset.)

The automatic port is the Valheim game port plus 4. Vanilla non-crossplay Valheim already answers Steam queries on the game port plus 1, so the separate default avoids colliding with that listener. Open the selected port for inbound UDP traffic.

### Responses

- **A2S_INFO** reports the live server name; world name as the map; folder `valheim`; game `Valheim`; live peer count; configured maximum players; dedicated server type `d`; environment byte reporting the host OS (`l` Linux, `w` Windows); password flag; game version (for example, `0.221.12`); game port; keywords `valheimone,vo=<version>`; and 64-bit game ID `892970`.
- **A2S_PLAYER** reports connected player slots. With `PublicPlayerNames = false`, names are returned as generic `Player N` labels, matching the Live Map's privacy stance; set it to `true` to return live player names.

Both A2S_INFO and A2S_PLAYER use the standard S2C_CHALLENGE flow: the responder issues a challenge for the requesting client, the client repeats the request with that challenge, and the challenge expires after 30 seconds. Wrong challenges are re-challenged; malformed and oversized (over 1400 bytes) packets are dropped without throwing.

The responder uses one background thread and serves an immutable status snapshot refreshed on the main thread every 2 seconds. If the UDP port cannot be bound, it logs one warning and retries every 30 seconds.

With `python-a2s`:

```bash
python -m pip install python-a2s
```

```python
import a2s; a2s.info(("ip", port))
```
