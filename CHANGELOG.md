# Changelog

All notable changes to ValheimOne will be documented in this file. This project follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and intends to use [Semantic Versioning](https://semver.org/spec/v2.0.0.html) after its first release.

## [Unreleased]

## [0.13.1] - 2026-08-11

### Added

- Release provenance now records authoritative DLL and package hashes with the pinned .NET, BepInEx, and Valheim toolchain, while CI and a public verifier enforce cross-machine byte-for-byte reproducibility. Pinning the SDK fixes the root cause: previously, CI and developer clones could select different SDKs and compile with different Roslyn versions.
- The Codex list now has a sticky column header (Item, Category, Weight, Stack, Portal) with properly aligned columns, and the category filter is a themed dropdown with a per-category icon menu instead of a native select.
- The sidebar's World, Server, and Join Code rows each carry a small copy icon; the old Copy button is gone. When the world and server names are identical the two rows collapse into a single World row.
- Embedding hosts that render their own navigation can pass `externalNav: true` in the embed config and drive the view through `ValheimOneEmbed.setView("map"|"console"|"codex")`; the sidebar's internal view switcher stays hidden in that mode.

### Changed

- The default map view now centres the world circle in the visible map area, compensating for the floating Layers panel; explicit hash views and the initial players fit are unaffected.
- Overview clustering for cartography pins and point-of-interest layers now engages below the map's 1:1 overview zoom (previously only below zoom 2), so dense long-lived worlds no longer render thousands of individual markers on first open. Grid cells holding a single record still render the plain marker, keeping sparse worlds visually unchanged.
- The sidebar below the server details is now a single scrolling column: Players, Chat, Leaderboard, and Server Events keep their natural height, expanding a section pushes the content below it, and nothing overlaps. Cinema Mode moved from a full-width pill to a compact icon beside the brand mark.
- A readability pass across the dashboard: Codex rows, item details, recipes, and chips moved from 7–10 px type to 10–14 px; popups, console side panels, dungeon dialogs, and search results lifted similarly; the shared secondary text colour is brighter against the dark background.
- Codex item details render with compact, consistent section spacing. Element-level style guards (section padding, heading line-height, code chips, label margins) keep host-page styles from inflating the embedded layout.

### Fixed

- Release builds no longer carry any debug-info derivative of the build environment, which made the shipped DLL's hash depend on the commit and on the build machine even though the compile itself was deterministic. Two carriers, both reaching the DLL through the PDB checksum embedded in its debug directory: the SDK's implicit SourceLink document map (repository URL plus HEAD commit SHA — so the ledger could never describe a tag containing itself), and the portable PDB's metadata-references record (each reference's MVID — and the publicized game assemblies are regenerated per machine with fresh MVIDs). SourceLink is now off and Release emits no PDB at all; only the DLL ever shipped, and the deterministic build is now byte-identical across commits and machines, so the cross-machine reproducibility gate holds by construction.
- Release packaging now pins the fixed zip timestamp to UTC end to end: `touch -t` interprets its stamp in the machine's local timezone, so builders in different zones stored different DOS timestamps for the same stamp and the otherwise-identical zips hashed differently across machines.
- `PublicShowPlayerNames` now defaults to `false`, so a public map link never shows player names unless the server owner explicitly opts in. Explicitly saved values are unaffected.
- Scrolling the Codex no longer rebuilds the whole visible window every few pixels; rows are recycled and renders are coalesced per animation frame, removing the main-thread stalls on large catalogs.
- Cartography pins no longer tear down and rebuild every marker on each poll when nothing changed.

## [0.13.0] - 2026-08-04

### Added

- LiveMap admin and shared views now include World Timelapse, an aggregate history overlay with a bottom-of-map timeline scrubber, play/pause, 1x/4x/12x speeds, day and date readouts, arrow-key frame stepping, and Home, End, Space, and Escape controls.
- The server now records periodic aggregate world-state snapshots of the explored-fog mask, clustered base footprints, world day and boss progression, movement harvested from the activity heatmap's hourly slices before its seven-day window expires, and portal, bed, and ward positions when the entity layer is enabled.
- `GET /api/timelapse` and `GET /api/timelapse/frame` now serve the frame index and individual frames to admin and shared views.
- The `[LiveMap]` section now includes `Timelapse` (default `true`), `TimelapseIntervalMinutes` (default `60`, clamped to `5`–`1440`), and `PublicTimelapse` (default `false`) configuration keys.

### Changed

- Live fog, activity heatmap, base, portal, bed, ward, portal-network, and ward-radius layers now hide while World Timelapse is open and return to their prior visibility when it closes, keeping historical layers from overlapping their live counterparts.

### Notes

- Timelapse snapshots store state rather than images. Retention keeps eligible frames for 14 days, then thins older history to the newest frame per UTC day for up to one year, subject to hard caps of 1,024 frames and 24 MB; the oldest frames are evicted first and the newest frame is always retained. Snapshots contain no individual player positions or names, and movement is aggregate only. Public requests receive a 404 unless `PublicTimelapse` is explicitly enabled.

## [0.12.2] - 2026-07-24

### Fixed

- The LiveMap sidebar's brand block no longer shrinks below its own content when vertical space is tight, which previously let the Cinema Mode button overlap the view tabs in a short browser window or an embedded host.

## [0.12.1] - 2026-07-24

### Added

- LiveMap can now be mounted directly inside a host page: `GET /api/embed` returns a body fragment and `assets/app.embed.js` / `assets/app.embed.css` provide a root-scoped bundle, so a host page can render the map natively instead of in an iframe.
- LiveMap static assets are now served with strong ETags, `If-None-Match` 304 handling, and pre-computed gzip payloads, cutting repeat asset transfer to a revalidation round trip.

### Changed

- The LiveMap app now isolates itself when embedded: it mounts into a caller-supplied root, routes every request through a configurable API base, keeps its keyboard shortcuts and scroll handling inside that root, and leaves the host page's body, document title and URL hash untouched.

### Notes

- Standalone LiveMap behaviour is unchanged, and the network handshake schema is untouched, so 0.12.0 clients stay compatible with 0.12.1 servers.

## [0.12.0] - 2026-07-22

### Added

- LiveMap dungeon entrances now offer a View Interior action with a canvas-rendered top-down room schematic, live player positions, and matching `In: <dungeon>` tags on map markers and the player roster.
- LiveMap admin and shared views now expose `/api/dungeons` and `/api/dungeons/{id}` with interior room layouts, live players inside, and dungeon tags on player markers.
- LiveMap now maintains a server-side dungeon registry with world-wide entrance discovery, generated-interior room layouts decoded from each dungeon's `roomData` ZDO, and bounds-based resolution of players inside.
- The native and web consoles now support `vo item <name>` catalog summaries with recipe-use lookup and lazy item-name autocomplete.
- LiveMap now includes a public Codex of Items tab with searchable, categorized item stats, recipes, conversion sources, creature drops, and jump-link recipe navigation.
- LiveMap now serves a public, versioned item and recipe catalog from `GET /api/catalog`, including crafting, conversion, and creature-drop sources with day-long ETag caching.
- LiveMap admin and shared views now include a live Chat panel, while admins can send audited server shouts back into the game without enabling the web console.
- LiveMap admin and shared views now include a wipe-scoped Leaderboard for character playtime, deaths, and distance traveled, with public views kept private.
- LiveMap admin and shared views now include a default-off Activity Heatmap overlay with private aggregate 24-hour and 7-day activity windows, while public views receive no heatmap access.
- LiveMap admins can tow unattended ships from their map popup, with moves blocked when players are within 12 meters or the destination is more than 5 kilometers away.
- LiveMap admin and shared views now include a default-off Bases layer that surveys player-built structures, groups nearby pieces into base areas, and shows approximate structure counts without exposing settlements to the public view.
- LiveMap admin and shared views now include a default-off Creatures layer for active bosses, serpents, and raid mobs inside the event area, with live counts, creature-specific map art, smooth movement, star levels, and position details.

### Fixed

- Server-originated shouts no longer emit harmless player-lookup errors in the dedicated-server log.
- Server-originated shutdown shouts and web map pings no longer emit a platform-ID parse error in each connected player's log.

## [0.11.0] - 2026-07-22

### Added

- LiveMap search now accepts Valheim `x, z` world-coordinate pairs, flies to valid points with a temporary parchment-gold pulse, and gives a themed notice for coordinates beyond the world edge.
- LiveMap now keeps a slim parchment-themed metrics strip above the map and console: every view sees server status, day, and uptime, while console-authorized admins also see frame-time and abbreviated ZDO metrics on a 30-second map cadence without duplicate stats requests.
- Dedicated-server operators can schedule a graceful shutdown with `vo shutdown <seconds> [message]`, cancel it with `vo shutdown cancel`, or use the admin shutdown API; connected players receive shout countdowns, the pending deadline is exposed to the dashboard, and expiry forces a synchronous world save before Valheim's clean application-exit path runs.

### Changed

- LiveMap player, ship, and cart markers now move smoothly at their live-feed cadence through one shared animation loop; portal hops and respawns snap without drawing a trail across the world, while follow and Cinema Mode stay attached to the moving marker.
- LiveMap now keeps dense Dungeons, Spawners, Ores & Deposits, Forage, and Structures layers off the map until useful zoom levels, while preserving their checked and collapsed state and keeping layer counts visible with a clear zoom-in hint.
- The LiveMap web console now completes `vo` commands from bare subcommand prefixes, gives kick, ban, and shutdown confirmations terse recovery guidance, and replays the latest 30 command-journal entries behind a clear live-feed divider on first open.

### Fixed

- LiveMap recurring requests now pause in hidden tabs, refresh immediately when visible, and stop after three consecutive failures with reload guidance; the initial map shade also times out instead of spinning forever.

## [0.10.3] - 2026-07-21

### Fixed

- Resource-node surveys introduced in 0.10.2 no longer fail immediately and report empty layers when scan-progress reporting encounters an inaccessible game internal; progress now uses the mod's safe reflection path and falls back to an indeterminate state instead of aborting the scan.

## [0.10.2] - 2026-07-21

### Security

- LiveMap cartography-table pins now respect map view tiers: shared views replace raw player platform IDs with “a viking,” while public views receive no pins unless the new default-off `PublicPins` setting is enabled, and even then omit author identity entirely.

### Added

- First-time resource-layer surveys now report live progress and an estimated time remaining in the Layers panel, with a one-time notice explaining that initial results can take a few minutes instead of leaving an indefinite loading state.

### Changed

- LiveMap entity scans now use independent per-group budgets for ships, carts, portals, tombstones, wards, and beds instead of one shared 800-entity ceiling, so an abundant group cannot crowd the others out; the API reports each group’s cap and truncation state and capped layer counts display with a `+` suffix.
- Cartography-table pins now cluster into compact count markers at overview zoom, preserving checked-state styling and zooming toward the cluster when selected, then expand back into individual pins at closer views.

### Fixed

- Resource POI groups that reach the 5,000-entry safety cap no longer present that partial result as a complete world count: API responses expose the cap and truncation state, while the Layers panel shows `5,000+` with an explicit “world has more” note.

## [0.10.1] - 2026-07-21

### Changed

- LiveMap layers panel and legend raised to a genuinely readable scale — 13px row labels, proportional section headers, legible counts, taller rows, a slightly wider 300px panel — with brighter parchment contrast on formerly dim text and one consistent vertical-centering idiom for every header, chevron, count, and all/none control.
- The long POI sections (Dungeons, Spawners, Ores & Deposits, Forage, Structures) are now collapsible and start collapsed with per-category state remembered in layer settings, so the bigger text keeps the panel to a single screen; Live, Places, and overlay rows stay open.
- The Bosses jump menu now lists each Forsaken exactly once in progression order with its map icon and altar count; selecting a boss flies to the altar nearest the current view and selecting it again cycles through the rest.
- Watch mode is now Cinema Mode across the header button, player popups, context menu, and tooltips (existing `cinema` permalinks unchanged); entering settles within about three seconds, and on an empty server the mode announces "No vikings ashore — touring the world until someone joins" and slow-tours spawn, traders, and boss altars until a player appears, then follows them as before.
- The sidebar received a composition pass: every icon keeps its aspect ratio (the day/night glyph no longer stretches), wind and explored share one tidy stats row that never truncates, section headers/gaps/separators follow one rhythm matching the layers panel, the raid banner renders only while a raid is active, and the Saga panel is now titled Server Events.
- Boss map icons replaced with dedicated silhouette art of the seven Forsaken, used consistently on map markers, the boss-progression chips, popups, hover cards, and the Bosses jump menu.

### Fixed

- Opening a `cinema` permalink on an idle server no longer stalls forever before entering Cinema Mode.

## [0.10.0] - 2026-07-21

### Added

- LiveMap gains collaborative web pins: admin and shared viewers can drop pins from the map's right-click menu or a new toolbar tool, pick from a whitelisted icon set, label them, drag their own pins, and check them off — checked pins grey out with a strikethrough instead of disappearing, and one click restores them. Pins live-sync to every open viewer through a revision-bumping `webpins` SSE event, persist in an atomically written per-server `webpins.json` (100 pins per author, 500 per server, oldest evicted), join map search, and are served through a sanitized, rate-limited `/api/webpins` CRUD surface where shared viewers may edit only their own pins under a default-on `SharedPinEditing` setting and the public view stays pin-free unless a default-off `PublicWebPins` setting is enabled.
- LiveMap admin `/api/status` responses and the admin sidebar now expose the current crossplay join code for convenient copying; the access credential is never included in shared or public views and stays absent on non-crossplay servers.
- LiveMap admin and shared views now include a default-off Last seen layer with privacy-aware offline player ghost markers, relative disconnect times, per-session and accumulated playtime, and restart-safe retained positions.
- LiveMap can now mirror player Say and Shout chat as transient authenticated-view map bubbles and Saga lines through a privacy-sensitive `MirrorChat` opt-in that is disabled by default and never persists player speech.
- LiveMap admin and shared views now include a default-collapsed Saga panel backed by a restart-seeded activity ring, cursor-based `GET /api/activity` history, and live `activity` SSE batches for joins, leaves, deaths, raids, world saves, and new days.
- LiveMap now shows compact live boss-progression chips for all seven Forsaken in every view tier.
- LiveMap now warns once when browser storage cannot save settings and shows a themed one-time update toast after plugin-version changes.
- LiveMap layer settings now include per-category POI color palettes and a persistent POI-only marker opacity slider, with matching icon, cluster, density-dot, layer-row, and legend colors.
- LiveMap dungeon entrance popups and hover cards now show when vikings are inside.
- LiveMap moving ships now cast a dashed gold 30-meter bow-line along their current heading in both icon and density-dot views.
- LiveMap now offers a themed desktop right-click map menu for copying coordinates, starting a measurement, centering the view, and sending direct in-game pings for admins.
- LiveMap raid popups now show a live gold progress bar with the event's remaining time.
- LiveMap POI, entity, and pin markers now show compact dark-oak mini-cards with their matching Viking map art on desktop hover, while touch devices keep the existing plain tooltips and tap behavior.

## [0.9.0] - 2026-07-21

### Added

- LiveMap gains switchable Topographic and Old Chart basemaps alongside the default map, with a compact persistent style selector in Overlays, `st=topo` and `st=chart` permalink state that also survives Cinema mode, on-demand first-use rendering with live progress and failure feedback while the current basemap stays visible, style-specific revision-busted browser URLs backed by reusable server render caches, and an Old Chart fog treatment for public maps.
- Opt-in, server-only `[Discord]` webhook notifications for player joins and leaves, deaths with a last-known biome when available, raid starts and ends, world saves, and in-game day changes. Delivery uses a bounded background queue, two-second batches of up to ten embeds, TLS 1.2, five-second request timeouts, one retry, overflow dropping with a single warning, and a bounded shutdown flush; all settings hot-reload, while `WebhookUrl` is never logged or synchronized to clients.
- Default-on, server-only `[ActivityLog]` JSONL activity logging for server lifecycle, player joins, leaves and deaths, raids, world saves, day changes, and operator activity, with UTC daily files, configurable retention, bounded background writes, and live config hot-reload.
- LiveMap console commands and admin actions now produce operator audit events with sanitized `X-Operator` attribution, success or failure details, and automatic secret redaction; token-authenticated requests without panel attribution are recorded as `unknown` without ever storing token values.
- The LiveMap web console now keeps a shared, persistent 200-command journal with bounded 300-character output summaries, monotonic cursors through `GET /api/console/history`, restart-safe recall, and operator-prefixed command history on load.
- `vo doctor` and LiveMap `GET /api/stats` now report activity-log health, including the current UTC file, events written today, and time since the last successful write.

## [0.8.1] - 2026-07-20

### Security

- An empty `LiveMap.AccessToken` no longer grants the admin map view to tokenless requests; admin and console tiers are disabled until a token is set, and startup plus `vo doctor` now warn when the token is missing.
- `LiveMap.AccessToken` and `LiveMap.ShareToken` changes now apply live on config hot-reload, with no server restart needed for token provisioning or rotation.

## [0.8.0] - 2026-07-20

### Added

- The live map now paints instantly: a single small whole-world base image loads under the tile layer on first open, so the world appears at once and sharpens as tiles stream in, with the underlay refreshing on render-revision changes.
- The live map gains default-off Wards and Beds layers for admin and shared views: wards show their owner, active state, and a translucent gold protected-radius circle (radius read from the actual game prefab), inactive wards render dimmed without a circle, and beds mark spawn points with owner and claimed state; both ride the existing chunked entity scan with matching new icon runes.
- The live map now labels the world's regions: biome blobs are detected once on the render worker from the world generator (flood-filled on a coarse grid, largest ~60 kept) and served through a new `GET /api/regions` endpoint to every view tier, with a default-on "Region names" overlay drawing parchment-gold small-caps labels (Meadows, Black Forest, Swamp, and so on) that appear at overview zooms and step aside when you zoom in.
- Greydwarf nests, skeleton bone piles, and draugr piles are now found by the resource ZDO scanner (they are placed objects, not worldgen locations), Ashlands charred stone spawners get their own location-backed group, and any location group with more than 400 entries is now served on demand instead of inline, keeping the base `/api/pois` payload lean even with the full structures long tail indexed.
- The live map now wears an original Viking icon set: 49 hand-designed inline-SVG marker runes in the gold line-work style — one icon per boss altar (Eikthyr, the Elder, Bonemass, Moder, Yagluth, the Queen, Fader), per dungeon type, per ore, spawner, forage, and structure group, plus trader, spawn temple, ships, carts, portals, tombstones, and map-table pins — served as a new embedded `icons.js` manifest with the old text glyphs kept as fallback and density-dot mode unchanged; layer panel rows and cluster markers show the icons too.
- The live map POI system now covers the full world taxonomy in six categories: Bosses & Trader, Dungeons split by type (Burial Chambers, Sunken Crypts, Troll Caves, Frost Caves, Infested Mines, Ashlands Ruins), Spawners split by kind (Greydwarf nests, skeleton and draugr spawners, surtling geysers), Ores & Deposits (copper, tin, muddy scrap piles, silver, obsidian, meteorite, leviathans), Forage (berries, thistle, mushrooms, wild seeds, barley and flax, dragon eggs, black cores), and Structures (enemy camps, tar pits, shipwrecks, ruins and villages, Mistlands remains, runestones and lore), with the layer panel reorganized into collapsible category sections showing live per-group counts and per-category show/hide-all.
- `GET /api/pois` now returns a `groups` metadata array (key, label, category, count, inline, scan time) alongside inline location POIs; ore and forage groups are served on demand via `GET /api/pois?group=<key>` so the base payload stays small, and the public view tier still exposes only spawn and trader locations.
- Resource nodes now report live state on the map: copper and silver deposits read MineRock5 per-area health for intact/partial with a mined percentage, tin, scrap piles, obsidian, and meteorite read single-node health, leviathans show submerged after diving, and forage clusters count how many members are picked, serving "12 of 14 available" and a respawning state; depleted or picked nodes render dimmed, and resource popups carry an honest "as of last survey Xm ago" staleness footer.
- Ore and forage positions come from a new amortized resource scanner that mirrors the entity tracker's discipline: one incremental ZDO query per frame, scans only while a resource layer is being viewed, rescans at most every 3 minutes, caps ore groups at 5000 entries, and clusters forage into 64 m grid cells with member counts; a new `ResourceLayers` config toggle gates it.

- LiveMap map imagery is now revision-busted: tile, base, and fog URLs carry a render/fog revision so browsers can cache them for a day yet refresh instantly after a re-render, and the fog image now allows long-lived caching instead of no-store.
- LiveMap now shows a "Saved Xm ago" badge fed by the world-save hook (amber when a save is overdue) and a subtle dawn toast when a new in-game day begins.
- LiveMap gains a full-screen Cinema/Watch mode for a second monitor: a Watch button and popup action hide all chrome and show a parchment HUD with server, day, in-game clock, mini wind rose, and a live followed-viking card; the camera follows with an always-on 30-minute trail, auto-cycles players every 20 seconds when nothing is locked, drifts slowly over the world when nobody is ashore, jumps to raids with a pulsing alert (with a stay-on-target opt-out), survives reconnects and respawns, and is bookmarkable via #cinema URLs.
- LiveMap admins can now send a map ping into the game: arm Ping, click the map, and every connected player sees a vanilla ping in-game with the web label, with no client mod required; in-game map pings are mirrored back to the web map for all view tiers as transient animated markers that fade after about 30 seconds.
- LiveMap players now report health, max health, dead, PvP, and in-bed state to admin and shared views, shown as HP bars in the sidebar roster, health/PvP/sleeping rows in the player popup, and a moon glyph for sleeping vikings.
- LiveMap portals now form a network graph on the web map: popups show pair status with distance and a jump-to-pair action, a dashed gold link is drawn while a paired portal's popup is open, and an optional default-off "Portal network" overlay draws every same-tag link at once, with unpaired and tag-conflict states called out.
- LiveMap now tracks player tombstones as a default-off skull layer for admin and shared views, with owner and time-of-death read from the tombstone itself, the newest tombstone per player emphasized, and a "Last death" row with relative time, distance, and jump in the player popup.
- LiveMap now keeps a rolling 30-minute server-side position history for players, ships, and carts, serving it through `GET /api/trail` with per-view authorization; `GET /api/entities?focus=<id>` adds a 2-second single-entity fast path for followed ships and carts, and the web client back-fills trails from history on click, follow, and the all-players mini-trails toggle.
- LiveMap now supports a `ShareToken` spectator tier with names, full map layers, POIs, and follow without console or admin access, plus `RespectInGameVisibility` to control whether shared and public views honor players' in-game position-sharing preference.
- LiveMap's web console now offers a categorized command helper dropdown with click and keyboard completion, including live player-name completion for commands that target a player.
- The web console now renders `help`, `vo help`, and `/help` as a rich in-terminal command reference and includes a collapsible Commands panel for browsing all available commands.
- The LiveMap sidebar footer now includes a subtle SurvivalServers.com hosting attribution link.
- Server-side `vo` administration commands now provide categorized self-help, privacy-aware player and session details, moderation and save actions, server diagnostics, weather and boss status, and LiveMap entity summaries from both the native console and web console; `/api/console/meta` now publishes usage, category, examples, and player-argument metadata for these commands and the curated vanilla command set.
- LiveMap `/api/status` and `/api/players` now report snapshot timestamps and ages through `unixMs` and `snapshotAgeMs`, with a `stale` flag when player data has missed three update intervals.
- LiveMap admin player and entity data now includes stable IDs, with player biome, speed, heading, session start, daily distance, and entity rotation and portal tags.
- LiveMap status now reports wind direction, wind intensity, and explored-world percentage, while POIs report whether they have been explored.
- LiveMap's web client now uses stable player and ship tracking IDs, gives shared spectators all non-console live-map features, and surfaces wind, exploration, player session details, daily travel, and ship wind alignment.
- LiveMap now provides `/api/height` for invariant, validated world-height sampling.

### Changed

- Live map forest stipple no longer shows a repeating grid or checkerboard at detail zooms: zoom-aware noise stippling preserves the overview texture and becomes soft tree dots at deep zoom, while a renderer cache version bump forces a one-time map re-render.
- With no players online, the server now idles its periodic work: player/status snapshots, A2S query info, and stats refresh every ~30 seconds instead of every couple of seconds, and fog processing goes dormant; full cadence returns the moment someone joins, while API staleness reporting accounts for the slower idle cadence.
- LiveMap entity-layer ZDO scanning is now spread across frames with one incremental query per frame, refreshes at most every ~30 seconds, and only runs while the entity layer is being viewed (an entities request in the last 2 minutes); the active raid event still updates every few seconds.
- Periodic fog-of-war cache writes now run off the main thread to avoid simulation hitches from disk I/O, and fog snapshot revisions are batched to no more than one update every ~10 seconds during normal operation, reducing viewer refresh work on busy servers.
- LiveMap's web dashboard now wears a Valheim-lore visual reskin: a warm dark-oak, parchment, and gold palette replaces the old grey theme, with candlelit texture and vignette, a Vegvisir rune-compass brand mark beside the VALHEIMONE wordmark, lore-minded labels such as World Chart, Longhouse Console, and “No vikings ashore,” an ember-toned raid banner, and gold-glow map markers.
- This is a pure reskin: layout, sizes, and behavior are unchanged. The OFL-licensed Metamorphous and Averia Serif Libre fonts are bundled, embedded, and served by the plugin, so the dashboard makes no external font or CDN requests.

## [0.7.2] - 2026-07-20

### Fixed

- The world render no longer shows red garbage outside the playable circle (square texture corners and a band past the southern edge, from out-of-range WorldGenerator biome samples): both the base render and on-demand detail tiles now clamp everything beyond the world edge to deep ocean, with a soft fade from normal shading to the deep-ocean page color over the last ~300 m inside the edge so the boundary reads like the game's edge mist. Renderer cache version bumped — servers re-render the world map (~2 min) on next boot.

## [0.7.1] - 2026-07-19

### Fixed

- Public-view fog no longer blacks out unexplored worlds: the fog overlay is now a ghosted treatment (~57% cover toward a cool slate instead of 92% near-black), so the world's shape, biomes, coastlines, and POI markers stay readable at every zoom while clearly fogged. Reveal edges got a wider feather, and on fogged public views the map holds an ocean-colored cover until the fog image has loaded so unfogged terrain never flashes on first paint.

## [0.7.0] - 2026-07-19

### Added

- `[Server] MaxPlayers`: gameplay-effective player cap override for the dedicated server (0 keeps Valheim's cap of 10; higher values raise the real join limit, clamped 1..127). Patches the join gate in `ZNet.RPC_PeerInfo` and, for crossplay, the PlayFab lobby capacity, network configuration, and advertised session capacity at boot. The join gate hot-reloads; lobby capacity applies at startup. A2S `max_players` and `/api/status` follow the effective cap.
- `[Server] NoPasswordRequired`: allow starting a public dedicated server without a join password by skipping the vanilla minimum-password startup validation (`FejdStartup.IsPublicPasswordValid`). It does not remove or bypass a password that is set — join-side password checks are untouched.
- `/api/status` now reports `maxPlayers` (the effective gameplay cap) on both view levels.

### Changed

- **Breaking:** `[Query] MaxPlayers` is replaced by `[Server] MaxPlayers`. A2S reporting now always follows the effective gameplay cap. An existing `[Query] MaxPlayers` value in the config file is still honored as a reporting fallback for this release (with a deprecation warning) when `[Server] MaxPlayers` is unset; the fallback will be removed in the next release.

## [0.6.1] - 2026-07-19

### Added

- LiveMap deep zoom now reaches zoom 8 (about 0.375 m/px on the default 2048 texture), with the on-demand detail tile cache bounded by a 512 MB LRU disk cap (least-recently-served tiles are evicted; evictions are logged).
- Official ValheimOne icon: dashboard favicon and header brand mark now use the painted shield icon; source assets under docs/brand/.
- Contract test now boots with a pinned config (pristine reference config plus the `tools/fixtures/contract.cfg` overlay, `[Query]` enabled) and restores the harness config afterward, making the enabled-module fingerprint deterministic; golden re-blessed accordingly.

### Fixed

- LiveMap dashboard no longer shows a hard black margin/seam beside or around the world at any zoom or pan position: the map surface background now matches the renderer's deep-ocean edge color, so letterboxed areas and still-rendering deep-zoom tiles read as open ocean.

## [0.6.0] - 2026-07-19

### Added

- Opt-in standalone `[Query]` A2S UDP responder for server browsers, monitoring tools, and hosting panels, including crossplay servers, with per-client challenge flow, A2S_INFO and A2S_PLAYER responses, player-name privacy via `PublicPlayerNames`, and an automatic game-port-plus-4 default.
- LiveMap palette fidelity: biome tints now match the in-game map look (light-green Meadows, gray-green Black Forest, murky Swamp, tan Plains, gray Mistlands with dark forest specks, red Ashlands with a contained southern lava sea, pale Deep North, crisp ocean depth ramp), with forest stipple following the game's own per-biome forest-factor rules; renderer cache version bumped.
- LiveMap true zoom depth: tiles beyond the base overview render lazily at each tile's own world resolution (down to about 1.5 m/px at zoom 6) on a single background worker with request coalescing, disk caching, a flat-ocean fast path outside the world edge, and background pre-rendering of the first detail zoom.

### Fixed

- LiveMap dashboard zoom bounds now reconcile from live status, so a page opened during the boot-time world render gains the full zoom depth without a manual refresh; the admin dashboard also no longer sends a guaranteed-404 `/api/entities` probe when the entity layer is disabled because availability is now advertised in `/api/status`.

## [0.5.0] - 2026-07-19

### Added

- LiveMap `/api/events` Server-Sent-Events stream (players, status deltas, and console log lines for admin tokens) with automatic front-end fallback to polling and exponential SSE retry.
- Opt-in LiveMap entity layer (`EntityLayer`): ships, carts, and portals served from ZDO scans as toggleable admin map layers, plus an active raid event exposed on the admin status feed and rendered as a pulsing map ring with a sidebar badge.
- LiveMap front-end polish: low-zoom POI grid clustering and a collapsible layers legend.
- Live config hot-reload: editing `valheimone.cfg` on disk now applies changed values without a restart (debounced, main-thread reload with a per-key diff logged; server-pushed overlay values keep precedence; new patch topology still requires a restart).
- Golden-seed contract test (`tools/contract-test.sh`): pinned world pair, deterministic worldgen fingerprint, Harmony patch inventory, and module registration contract with drift reporting and a `--bless` refresh path; wired into RELEASING.md as a mandatory release gate.
- Contract diagnostics in the plugin: worldgen fingerprint, patch inventory, and per-module apply hardening.
- GitHub Actions CI: steamcmd dedicated-server download with login priming and retry, assembly publicizing, Release build, and DLL artifact upload.

## [0.4.0] - 2026-07-19

### Added

- Initial `net472` BepInEx plugin scaffold for Valheim 0.221.12.
- Original typed, section-based configuration framework with default-off feature gates and modifier-percent support.
- Central feature registry with server-authoritative, synced (client-required), and client-only classifications.
- Working opt-in `PlayerCarryWeight` Harmony module as the first end-to-end gameplay example.
- Safe game-version detection, startup diagnostics, and a config-watcher stub.
- `VO_Hello` routed-RPC version enforcement with an optional vanilla-client gate and grace period.
- Chunked `VO_Config`/`VO_Ack` server config transfer with acknowledgement gating and non-persistent, non-ClientOnly client overlays.
- Startup-installed, runtime-gated feature patches so synced config can enable client modules safely.
- Opt-in `PlayerStamina` module with percentage controls for regeneration, delay, movement drains, and action costs.
- Opt-in `FoodDuration` module with duration scaling and optional no-degradation food benefits.
- Opt-in `ItemDropMultiplier` module for destructible, creature, and pickable yields.
- Opt-in `CraftFromChest` module for crafting and optional building from nearby accessible containers.
- Opt-in `StationAutomation` module for feeding smelter-based stations and fireplaces from nearby containers.
- Opt-in `DayNightLength` module with percentage and absolute full-day length controls.
- Opt-in `Portals` module for disabling portal travel or allowing restricted inventory through portals.
- Opt-in `ExperienceRates` module with global and per-skill experience multipliers.
- Opt-in `DeathPenalty` module for scaling skill loss or preserving inventory on death.
- Extended the opt-in `[Player]` module with auto-pickup range, pickup while encumbered, and rested seconds per comfort level.
- Opt-in `BuildingQoL` module for bypassing structural support, suppressing ordinary placement blocking, and overriding build reach and rotation step.
- Opt-in `ItemTweaks` module with stack-size, weight, and durability multipliers.
- Opt-in `Gathering` module with per-material yield and supported drop-chance modifiers.
- Opt-in `Beehive`, `Fermenter`, and `SapCollector` production modules for timing and capacity overrides.
- Opt-in `Wards` module for overriding ward protection radius.
- Opt-in `MapSharing` module for forced position sharing and server-unioned client exploration sync.
- New `VO_Map` wire protocol with validated run-length map ranges, bounded chunks, and acknowledgement-gated bidirectional transfers.
- Generalized acknowledgement-gated chunk queue shared by config sync and map exploration transfers.
- Runtime-visibility hardening for Unity 6 Mono: verified public game APIs where available and cached reflection delegates for vanilla-private members.
- Build script, project documentation, contribution guidance, and MIT license.
- Opt-in `LiveMap` module: background seed-to-tiles world render (north-up Leaflet tile pyramid cached on disk), embedded HTTP server with `/api/status` and `/api/players` (honoring in-game position privacy), and a dark self-contained live map page with player markers and day/time readout.
- LiveMap P2: POI layer from world locations with a toggleable Layers panel, fog-of-war (`off`/`trails`/`explored` incl. vanilla cartography-table decode), shared map pins, and a token-gated admin view with a read-only fogged public view.
- LiveMap P3 web admin console (opt-in via `ConsoleEnabled`, admin token required): whitelisted console command execution through the game's own console path (`ConsoleWhitelist` / `AllowAllCommands`), cursor-polled server-log ring buffer (`ConsoleLogLines`), kick/ban/unban/banlist and world-save endpoints, and a `/api/stats` health snapshot (uptime, players, ZDOs, Mono heap, frame timings).
- Dashboard Console tab: live server log with severity colors and resume-scroll, command input with history and whitelist autocomplete, player kick/ban and banned-list unban with confirm dialogs, stats readout, and a save-world button — same dark self-contained page, admin view only.
- `StatusPublic` config key (default on): `/api/status` stays available without a token for hosting-panel queries even when the map is token-locked; see `docs/query.md`.
- Opt-in `[ProductionSpeeds]` module for production time and queue or fuel capacity overrides across smelters, blast furnaces, kilns, windmills, spinning wheels, and eitr refineries.
- Opt-in `[CookingStation]` module for cook speed, optional fire bypass, and automatic fuel and nearby-container raw-food feeding.
- Opt-in `[FireSource]` module for infinite torches and fires.
- Opt-in `[StructuralIntegrity]` module for disabling weather damage and reducing support loss by material.
- Opt-in `[ContainerSizes]` module for chest, cart, karve, and longship grid overrides with an item-safe shrink guard.
- Opt-in `[Tames]` module for taming, growth, and procreation modifiers.
- Opt-in `[Events]` module for raid chance and interval controls, raid disabling, and guardian-power duration and cooldown overrides.
- Opt-in `[Trader]` module for buy-price multiplication.

### Deferred

- Client-only quality-of-life features from the reference set—first-person and camera options, HUD tweaks, hotkey tools, and advanced building or editing modes—remain intentionally out of scope; mob-AI aggression tuning was skipped because no stable non-transpiler hook was available.
