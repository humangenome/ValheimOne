# Gameplay Modules

Every gameplay module is off until you enable it. Each has its own `Enabled = false` gate in `BepInEx/config/valheimone.cfg`, so installing ValheimOne changes nothing on its own.

Each module has a mode, explained at the bottom of this page. **Server-authoritative** modules work for every player. **Synced** modules work only for players who also have ValheimOne on their PC; the server sends them its settings when they join.

- `Player` (`[Player]`): sets carry weight, the Megingjord bonus, auto-pickup range, encumbered pickup, and rested seconds per comfort level. Each player's own game applies these values. **Mode:** synced.
- `PlayerStamina` (`[Stamina]`): scales stamina regeneration, delay, movement drains, and action costs. **Mode:** synced.
- `BuildingQoL` (`[Building]`): removes structural-support requirements, suppresses ordinary placement blocking, and overrides build reach and rotation step. **Mode:** synced.
- `FoodDuration` (`[Food]`): scales food duration and can hold benefits at full strength until expiry. `Enabled = true` and `NoDegradation = true` preserve the full health, stamina and eitr benefits while the food timer still counts down. Each participating player needs ValheimOne; use 0.13.18 or later for the no-degradation fix. **Mode:** synced.
- `ItemTweaks` (`[Items]`): scales item stack sizes, weights, and maximum durability. **Mode:** synced.
- `ItemDropMultiplier` (`[Drops]`): scales destructible, creature, and pickable yields. The game rolls this loot in the game of the player standing near the tree, rock, creature or plant, so a player without ValheimOne gets normal drops. Loot chests are never changed. **Mode:** synced.
- `Gathering` (`[Gathering]`): applies per-material yield modifiers and adjusts supported non-guaranteed drop chances. Like Drops, it is rolled in the game of the player standing nearby, so it needs ValheimOne on that PC. **Mode:** synced.
- `CraftFromChest` (`[CraftFromChest]`): consumes crafting and optional build costs from nearby accessible containers. **Mode:** synced.
- `StationAutomation` (`[StationAutomation]`): pulls fuel and processable items from nearby containers for smelter-based stations and fireplaces. **Mode:** synced.
- `DayNightLength` (`[Time]`): scales or absolutely overrides the full day/night cycle length. **Mode:** synced.
- `Beehive` (`[Beehive]`): overrides honey production time and storage capacity. **Mode:** synced.
- `Fermenter` (`[Fermenter]`): overrides fermentation time. **Mode:** synced.
- `SapCollector` (`[SapCollector]`): overrides sap production time and storage capacity. **Mode:** synced.
- `Wards` (`[Wards]`): overrides ward protection radius. **Mode:** synced.
- `Portals` (`[Portals]`): disables portal travel or permits normally restricted inventory. **Mode:** synced.
- `ExperienceRates` (`[Experience]`): applies global and per-skill experience multipliers. **Mode:** synced.
- `DeathPenalty` (`[DeathPenalty]`): scales death skill loss or preserves inventory without a tombstone. **Mode:** synced.
- `MapSharing` (`[MapSharing]`): forces compatible clients to share positions and synchronizes their combined explored-map area. **Mode:** synced.
- `ProductionSpeeds` (`[ProductionSpeeds]`): overrides production time, queue size, and fuel capacity across smelters, blast furnaces, kilns, windmills, spinning wheels, eitr refineries, and frigid kilns. **Mode:** synced.
- `CookingStation` (`[CookingStation]`): scales cook speed, optionally bypasses the fire requirement, and auto-feeds fuel and raw food from nearby containers. **Mode:** synced.
- `FireSource` (`[FireSource]`): makes torches and fires infinite. **Mode:** synced.
- `StructuralIntegrity` (`[StructuralIntegrity]`): disables weather damage and reduces support loss by material. **Mode:** synced.
- `ContainerSizes` (`[ContainerSizes]`): overrides chest (wood, personal, reinforced, black metal, Grausten), wardrobe, cart, karve, and longship grid sizes with an item-safe shrink guard. **Mode:** synced.
- `Tames` (`[Tames]`): applies taming, growth, and procreation rate modifiers. **Mode:** synced.
- `WorldEvents` (`[Events]`): controls raid chance and interval, disables raids, and overrides guardian-power duration and cooldown. **Mode:** server-authoritative for the raid settings, which work for every player; synced for the guardian-power duration and cooldown, which each player's own game applies, so they only change for players with ValheimOne on their PC.
- `Trader` (`[Trader]`): multiplies trader buy prices. **Mode:** synced.

`[Network]`, the higher send limit, is not a gameplay module: it is on by default, runs on the server only and needs nothing on a player's PC. See [Less lag when you play together](../README.md#less-lag).

**Client-only:** n/a; no current gameplay module uses this mode.

For **Shared Exploration on Valheim 1.0**, update the server and all participating PCs to **0.13.6 or newer**. Enable both `MapSharing.Enabled` and `MapSharing.SharedExploration`; the server combines and saves the players' explored minimap areas.

For **Craft From Chest on 0.13.6+**, nearby chests can be used even when another player last opened them. Crafting may briefly wait for the chest's current owner to hand over its inventory; hammer placement may ask you to click again once the chest is ready. As in the base game, a recipe takes all of one ingredient at the same quality, so fish of different star levels do not add up toward one recipe.

For **station and cooking automation on Valheim 1.0**, update the server and participating PCs to **0.13.8 or newer**. Enable the relevant module's `Enabled` switch as well as its individual automation options. Automation waits for access to a chest owned by another player before taking fuel or ingredients. Keep every participating PC current; older clients can still run the old station behavior.

Open or otherwise busy chests are excluded, and private-chest and ward restrictions still apply. Update the server and all participating PCs; station automation retains its separate existing ownership rules.

## Modes explained

- **server-authoritative**: runs on the server and works for vanilla and console players.
- **synced**: runs on each player's game, so it needs ValheimOne on that PC; the server's settings replace the player's for that session.
- **client-only**: stays on the player's PC and is never sent by the server.
- **server-only**: runs on the server and is never sent to players.
