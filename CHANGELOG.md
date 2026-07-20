# Changelog

All notable changes to ValheimOne will be documented in this file. This project follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and intends to use [Semantic Versioning](https://semver.org/spec/v2.0.0.html) after its first release.

## [Unreleased]

### Added

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
