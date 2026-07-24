# Gameplay Modules

Every gameplay module is off until you enable it. Each has its own `Enabled = false` gate in
`BepInEx/config/valheimone.cfg`, so installing ValheimOne changes nothing on its own.

Features use four modes: **server-authoritative** logic runs under server ownership and can support vanilla clients; **synced** logic requires a compatible client and receives the server overlay; **client-only** settings stay local and are never pushed; and **server-only** integrations run only on the host and are never synchronized. The current gameplay modules are:

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
- `ProductionSpeeds` (`[ProductionSpeeds]`) — overrides production time, queue size, and fuel capacity across smelters, blast furnaces, kilns, windmills, spinning wheels, and eitr refineries. **Mode:** synced.
- `CookingStation` (`[CookingStation]`) — scales cook speed, optionally bypasses the fire requirement, and auto-feeds fuel and raw food from nearby containers. **Mode:** synced.
- `FireSource` (`[FireSource]`) — makes torches and fires infinite. **Mode:** synced.
- `StructuralIntegrity` (`[StructuralIntegrity]`) — disables weather damage and reduces support loss by material. **Mode:** synced.
- `ContainerSizes` (`[ContainerSizes]`) — overrides chest, cart, karve, and longship grid sizes with an item-safe shrink guard. **Mode:** synced.
- `Tames` (`[Tames]`) — applies taming, growth, and procreation rate modifiers. **Mode:** synced.
- `WorldEvents` (`[Events]`) — controls raid chance and interval, disables raids, and overrides guardian-power duration and cooldown. **Mode:** server-authoritative.
- `Trader` (`[Trader]`) — multiplies trader buy prices. **Mode:** synced.

**Client-only:** n/a; no current gameplay module uses this mode.

## Modes explained

- **server-authoritative** - runs under server ownership and works with vanilla clients.
- **synced** - needs a compatible client, which receives the server's overlay.
- **client-only** - stays local, never pushed to clients.
- **server-only** - runs on the host, never synchronized.
