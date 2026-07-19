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
| `day` | Current in-game day number. |
| `timeOfDay` | 0..1 fraction of the current day. |
| `players` | Players visible at the caller's view level (public callers only see players sharing their position unless names/positions are public). |
| `view` | `admin` or `public` — which view level answered the request. |
| `console` | `true` only for admin-token requests when the web console is enabled. |
| `map.state` / `map.progress` | World-render lifecycle for the map front-end. |

## Access rules

- `[LiveMap] StatusPublic = true` (default): `/api/status` answers **without a token** even when the map itself is token-locked (`AccessToken` set, `PublicView = false`). Tokenless callers get the public view. This is intended for hosting-panel status polls — set it to `false` to require map access for status too.
- All other endpoints follow the normal map rules: `AccessToken` grants the admin view; `PublicView` controls whether tokenless visitors get the read-only public map.

## Admin API (token required)

With `[LiveMap] ConsoleEnabled = true` **and** a non-empty `AccessToken`, the same port serves an admin API. Every endpoint below returns `401` without the token (query `?token=...` or header `X-LiveMap-Token`), including when `PublicView = true`. With an empty `AccessToken` the console endpoints stay locked out entirely.

| Endpoint | Method | Purpose |
|---|---|---|
| `/api/console/exec` | POST `{"command":"..."}` | Run a console command via the game's own console path. Commands must be in `ConsoleWhitelist` unless `AllowAllCommands = true` (non-whitelisted → `403`). Returns `{"ok":true,"output":[...]}`. |
| `/api/console/log` | GET `?cursor=N&max=M` | Cursor-based server-log polling from the in-memory ring buffer (`ConsoleLogLines`, default 500). Returns `{"cursor":N,"lines":[...]}` — pass the returned cursor back to get only new lines. |
| `/api/console/meta` | GET | Whitelist, `allowAll` flag, and known command metadata for autocomplete. |
| `/api/admin/kick` | POST `{"player":"name/ip/userID"}` | Kick a player. |
| `/api/admin/ban` / `/api/admin/unban` | POST `{"player":"..."}` | Ban / unban. |
| `/api/admin/banlist` | GET | Current ban list. |
| `/api/admin/save` | POST | Trigger a world + profile save. Returns `alreadySaving` when a save was in flight. |
| `/api/stats` | GET | Uptime, player/peer counts, ZDO count, Mono heap, frame avg/max ms, world day/time. |

Notes:

- POST bodies are JSON, max 8 KB. Send a `Content-Length` header (an empty body with `Content-Length: 0` is fine for `/api/admin/save`; `curl` needs `-d ''`).
- Cheat-gated commands (e.g. `sleep`) execute through the same rules as the in-game console: they report `not valid in the current context` until `devcommands` is enabled. Add `devcommands` to `ConsoleWhitelist` or set `AllowAllCommands = true` if you want that from the web console.
- `say` is not in the default whitelist: the vanilla command is a silent no-op on dedicated servers (it requires a local player).
