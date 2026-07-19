# Changelog

All notable changes to ValheimOne will be documented in this file. This project follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and intends to use [Semantic Versioning](https://semver.org/spec/v2.0.0.html) after its first release.

## [Unreleased]

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
