# Changelog

All notable changes to ValheimOne will be documented in this file. This project follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and intends to use [Semantic Versioning](https://semver.org/spec/v2.0.0.html) after its first release.

## [Unreleased]

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
- Runtime-visibility hardening for Unity 6 Mono: verified public game APIs where available and cached reflection delegates for vanilla-private members.
- Build script, project documentation, contribution guidance, and MIT license.
- Opt-in `LiveMap` module: background seed-to-tiles world render (north-up Leaflet tile pyramid cached on disk), embedded HTTP server with `/api/status` and `/api/players` (honoring in-game position privacy), and a dark self-contained live map page with player markers and day/time readout.
