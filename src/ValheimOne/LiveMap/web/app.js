(function (hostWindow) {
    "use strict";

    var embedConfig = hostWindow.VALHEIMONE_EMBED;
    var embedRoot = null;
    if (embedConfig && typeof embedConfig.rootId === "string") {
        embedRoot = document.getElementById(embedConfig.rootId);
    }
    var embedMode = Boolean(embedRoot);
    var embedApiBase = embedMode && typeof embedConfig.apiBase === "string"
        ? embedConfig.apiBase
        : "";
    var appRoot = embedRoot || document.body;
    var styleRoot = embedRoot || document.documentElement;
    var destroyed = false;
    var embedPointerInside = false;
    var embedTimeouts = new Set();
    var embedIntervals = new Set();
    var embedAnimationFrames = new Set();
    var embedWindowListeners = [];
    var appListeners = [];
    var window = embedMode ? createEmbedWindow(hostWindow) : hostWindow;

    function createEmbedWindow(source) {
        var scopedWindow = {
            EventSource: source.EventSource,
            Image: source.Image,
            ResizeObserver: source.ResizeObserver,
            VO_ICONS: source.VO_ICONS,
            VO_ICON_FOR_POI: source.VO_ICON_FOR_POI,
            devicePixelRatio: source.devicePixelRatio,
            history: source.history,
            location: source.location,
            addEventListener: function (type, listener, options) {
                if (destroyed) {
                    return;
                }
                source.addEventListener(type, listener, options);
                embedWindowListeners.push({
                    listener: listener,
                    options: options,
                    type: type
                });
            },
            cancelAnimationFrame: function (frame) {
                embedAnimationFrames.delete(frame);
                source.cancelAnimationFrame(frame);
            },
            clearInterval: function (timer) {
                embedIntervals.delete(timer);
                source.clearInterval(timer);
            },
            clearTimeout: function (timer) {
                embedTimeouts.delete(timer);
                source.clearTimeout(timer);
            },
            getComputedStyle: function () {
                return source.getComputedStyle.apply(source, arguments);
            },
            matchMedia: function () {
                return source.matchMedia.apply(source, arguments);
            },
            requestAnimationFrame: function (callback) {
                if (destroyed) {
                    return 0;
                }
                var frame = source.requestAnimationFrame(function (timestamp) {
                    embedAnimationFrames.delete(frame);
                    callback(timestamp);
                });
                embedAnimationFrames.add(frame);
                return frame;
            },
            setInterval: function (callback, delay) {
                if (destroyed) {
                    return 0;
                }
                var args = Array.prototype.slice.call(arguments, 2);
                var timer = source.setInterval(function () {
                    callback.apply(source, args);
                }, delay);
                embedIntervals.add(timer);
                return timer;
            },
            setTimeout: function (callback, delay) {
                if (destroyed) {
                    return 0;
                }
                var args = Array.prototype.slice.call(arguments, 2);
                var timer = source.setTimeout(function () {
                    embedTimeouts.delete(timer);
                    callback.apply(source, args);
                }, delay);
                embedTimeouts.add(timer);
                return timer;
            }
        };
        Object.defineProperty(scopedWindow, "localStorage", {
            get: function () {
                return source.localStorage;
            }
        });
        Object.defineProperty(scopedWindow, "sessionStorage", {
            get: function () {
                return source.sessionStorage;
            }
        });
        return scopedWindow;
    }

    function embedElementById(id) {
        return embedMode ? embedRoot.querySelector("#" + id) : document.getElementById(id);
    }

    function addAppListener(target, type, listener, options) {
        if (destroyed) {
            return;
        }
        target.addEventListener(type, listener, options);
        if (embedMode) {
            appListeners = appListeners.filter(function (record) {
                return !record.target.nodeType ||
                    record.target === document ||
                    record.target === embedRoot ||
                    embedRoot.contains(record.target);
            });
            appListeners.push({
                listener: listener,
                options: options,
                target: target,
                type: type
            });
        }
    }

    function removeAppListener(target, type, listener, options) {
        target.removeEventListener(type, listener, options);
        if (!embedMode) {
            return;
        }
        appListeners = appListeners.filter(function (record) {
            return record.target !== target ||
                record.type !== type ||
                record.listener !== listener;
        });
    }

    function keyboardEventAllowed(event) {
        if (!embedMode) {
            return true;
        }
        return embedPointerInside ||
            eventInsideApp(event);
    }

    function eventInsideApp(event) {
        return !embedMode ||
            Boolean(event.target && embedRoot.contains(event.target));
    }

    function addKeyboardListener(listener, options) {
        addAppListener(document, "keydown", function (event) {
            if (keyboardEventAllowed(event)) {
                listener(event);
            }
        }, options);
    }

    function appHash() {
        return embedMode ? "" : window.location.hash;
    }

    function clearEmbedRuntime() {
        embedTimeouts.forEach(function (timer) {
            hostWindow.clearTimeout(timer);
        });
        embedTimeouts.clear();
        embedIntervals.forEach(function (timer) {
            hostWindow.clearInterval(timer);
        });
        embedIntervals.clear();
        embedAnimationFrames.forEach(function (frame) {
            hostWindow.cancelAnimationFrame(frame);
        });
        embedAnimationFrames.clear();
        appListeners.forEach(function (record) {
            record.target.removeEventListener(
                record.type,
                record.listener,
                record.options
            );
        });
        appListeners = [];
        embedWindowListeners.forEach(function (record) {
            hostWindow.removeEventListener(
                record.type,
                record.listener,
                record.options
            );
        });
        embedWindowListeners = [];
    }

    if (embedMode) {
        addAppListener(embedRoot, "mouseenter", function () {
            embedPointerInside = true;
        });
        addAppListener(embedRoot, "mouseleave", function () {
            embedPointerInside = false;
        });
    }

    var POLL_INTERVAL_MS = 2000;
    var POLL_FAILURE_LIMIT = 3;
    var PINS_POLL_INTERVAL_MS = 60000;
    var HEATMAP_POLL_INTERVAL_MS = 60000;
    var LEADERBOARD_POLL_INTERVAL_MS = 60000;
    var ENTITIES_POLL_INTERVAL_MS = 10000;
    var CONSOLE_LOG_POLL_INTERVAL_MS = 2000;
    var CONSOLE_STATS_POLL_INTERVAL_MS = 5000;
    var MAP_STATS_POLL_INTERVAL_MS = 30000;
    var ACTIVITY_POLL_INTERVAL_MS = 5000;
    var CONSOLE_LOG_LIMIT = 1000;
    var CONSOLE_HISTORY_REPLAY_LIMIT = 30;
    var SAGA_EVENT_LIMIT = 100;
    var COMMAND_HISTORY_LIMIT = 50;
    var MARKER_TELEPORT_DISTANCE_M = 40;
    var MARKER_TWEEN_MIN_DURATION_MS = 250;
    var MARKER_TWEEN_MAX_DURATION_MS = 30000;
    var TILE_SIZE = 256;
    var WORLD_UNITS = 256;
    var OVERVIEW_CLUSTER_ZOOM = 2;
    var OVERVIEW_CLUSTER_GRID_PX = 64;
    var DUNGEON_MATCH_DISTANCE_M = 8;
    var DUNGEON_MIN_VISIBLE_ROOM_DIMENSION_M = 0.65;
    var DUNGEON_REGISTRY_POLL_INTERVAL_MS = 5000;
    var RESOURCE_POI_POLL_INTERVAL_MS = 5000;
    var RESOURCE_POI_REFRESH_INTERVAL_MS = 3 * 60 * 1000;
    var BASE_POI_REFRESH_INTERVAL_MS = 10 * 60 * 1000;
    var MAP_LOADING_TIMEOUT_MS = 15000;
    var SSE_RETRY_INITIAL_MS = 5000;
    var SSE_RETRY_MAX_MS = 60000;
    var TRAIL_MAX_AGE_MS = 30 * 60 * 1000;
    var TRAIL_TARGET_AGE_MS = 15 * 60 * 1000;
    var TRAIL_ALL_PLAYERS_AGE_MS = 5 * 60 * 1000;
    var TRAIL_EVICT_AGE_MS = 10 * 60 * 1000;
    var TRAIL_MAX_POINTS = 900;
    var TRAIL_BUCKET_COUNT = 10;
    var SHIP_MATCH_DISTANCE = 40;
    var SHIP_MOVING_SPEED_MPS = 0.3;
    var SHIP_HEADING_LENGTH_M = 30;
    var MAP_PING_LIFETIME_MS = 30000;
    var COORDINATE_SEARCH_PULSE_MS = 4000;
    var CHAT_BUBBLE_LIFETIME_MS = 8000;
    var CHAT_BUBBLE_LIMIT = 8;
    var CHAT_HISTORY_LIMIT = 32;
    var SAVED_BADGE_REFRESH_MS = 30000;
    var SAVED_STALE_MS = 30 * 60 * 1000;
    var DAY_TOAST_DURATION_MS = 4000;
    var NOTICE_TOAST_DURATION_MS = 6000;
    var CINEMA_AUTO_CYCLE_MS = 20000;
    var CINEMA_REFOLLOW_MS = 10 * 60 * 1000;
    var CINEMA_AMBIENT_STEP_MS = 18000;
    var CINEMA_AMBIENT_DURATION_SEC = 6;
    var CINEMA_ENTRY_DURATION_SEC = 2.75;
    var CINEMA_TOUR_BOSS_COUNT = 5;
    var TIMELAPSE_FRAME_CACHE_LIMIT = 24;
    var TIMELAPSE_WORLD_RADIUS = 12288;
    var TIMELAPSE_SPEEDS = {
        "1x": 2,
        "4x": 8,
        "12x": 24
    };
    var LAYER_STORAGE_KEY = "vo-livemap-layers-v2";
    var LEGACY_LAYER_STORAGE_KEY = "vo-livemap-layers";
    var LEGACY_MINIMAP_STORAGE_KEY = "vo-livemap-minimap";
    var MOTD_VERSION_STORAGE_KEY = "vo-livemap-motd-version";
    var WEB_PIN_AUTHOR_STORAGE_KEY = "webPinAuthor";
    var TAB_SESSION_KEY = "vo-livemap-active-tab";
    var CODEX_ROW_HEIGHT = 42;
    var CODEX_WINDOW_OVERSCAN = 8;
    var CODEX_SEARCH_DEBOUNCE_MS = 180;
    var CODEX_REVERSE_USE_LIMIT = 8;
    var CONSOLE_CATEGORY_ORDER = ["server", "players", "moderation", "world", "diagnostics"];
    var CONSOLE_CATEGORY_LABELS = {
        server: "Server",
        players: "Players",
        moderation: "Moderation",
        world: "World",
        diagnostics: "Diagnostics"
    };
    var CODEX_CATEGORY_TYPES = {
        weapons: [
            "Ammo", "AmmoNonEquipable", "Bow", "OneHandedWeapon", "TwoHandedWeapon",
            "TwoHandedWeaponLeft"
        ],
        armor: ["Chest", "Helmet", "Legs", "Shield", "Shoulder", "Utility"],
        tools: ["Tool", "Torch"],
        materials: ["Fish", "Material"],
        consumables: ["Consumable"],
        trophies: ["Trophy"],
        misc: ["Customization", "Misc", "Trinket"]
    };
    var CODEX_CATEGORY_LABELS = {
        weapons: "Weapons",
        armor: "Armor",
        tools: "Tools",
        materials: "Materials",
        consumables: "Consumables",
        trophies: "Trophies",
        misc: "Misc"
    };
    var POI_COLOR_PALETTE = [
        { key: "gold", label: "Gold", value: "var(--accent)" },
        { key: "parchment", label: "Parchment", value: "var(--text)" },
        { key: "moss", label: "Moss green", value: "var(--moss)" },
        { key: "frost", label: "Frost blue", value: "var(--frost)" },
        { key: "raid", label: "Raid red", value: "var(--raid)" },
        { key: "dungeon", label: "Dungeon violet", value: "var(--dungeon)" },
        { key: "cart", label: "Cart amber", value: "var(--cart)" },
        { key: "spawner", label: "Spawner pink", value: "var(--spawner)" }
    ];
    var POI_CATEGORY_DEFAULT_SWATCHES = {
        bosses: "conic-gradient(var(--frost) 0 33%, var(--raid) 33% 66%, var(--accent) 66%)",
        dungeons: "var(--dungeon)",
        spawners: "var(--spawner)",
        ores: "var(--sun)",
        forage: "var(--moss)",
        structures: "var(--marker-muted)"
    };
    var WEB_PIN_ICONS = [
        "pin",
        "boss",
        "bed",
        "portal",
        "ship",
        "cart",
        "tombstone",
        "trader",
        "spawn",
        "ward",
        "dungeon_crypt",
        "dungeon_mine",
        "ore_copper",
        "ore_iron",
        "ore_silver",
        "forage_berries",
        "forage_mushroom",
        "structure_camp",
        "structure_ruins",
        "spawner_greydwarf"
    ];

    var BOSS_PROGRESSION = [
        { name: "Eikthyr", key: "defeated_eikthyr", iconKey: "boss_eikthyr" },
        { name: "The Elder", key: "defeated_gdking", iconKey: "boss_elder" },
        { name: "Bonemass", key: "defeated_bonemass", iconKey: "boss_bonemass" },
        { name: "Moder", key: "defeated_dragon", iconKey: "boss_moder" },
        { name: "Yagluth", key: "defeated_goblinking", iconKey: "boss_yagluth" },
        { name: "The Queen", key: "defeated_queen", iconKey: "boss_queen" },
        { name: "Fader", key: "defeated_fader", iconKey: "boss_fader" }
    ];

    var POI_CATEGORIES = [
        {
            key: "bosses",
            label: "Bosses & Trader",
            groups: ["spawn", "boss", "trader"]
        },
        {
            key: "dungeons",
            label: "Dungeons",
            minimumZoom: 2,
            groups: [
                "dungeon_crypt",
                "dungeon_sunkencrypt",
                "dungeon_trollcave",
                "dungeon_frostcave",
                "dungeon_mine",
                "dungeon_ashlands"
            ]
        },
        {
            key: "spawners",
            label: "Spawners",
            minimumZoom: 3,
            groups: [
                "spawner_greydwarf",
                "spawner_bonepile",
                "spawner_draugrpile",
                "spawner_firehole",
                "spawner_charred",
                "spawner_other"
            ]
        },
        {
            key: "ores",
            label: "Ores & Deposits",
            minimumZoom: 4,
            groups: [
                "ore_copper",
                "ore_tin",
                "ore_iron",
                "ore_silver",
                "ore_obsidian",
                "ore_meteorite",
                "ore_leviathan"
            ]
        },
        {
            key: "forage",
            label: "Forage",
            minimumZoom: 5,
            groups: [
                "forage_berries",
                "forage_thistle",
                "forage_mushroom",
                "forage_seeds",
                "forage_crops",
                "forage_dragonegg",
                "forage_blackcore"
            ]
        },
        {
            key: "structures",
            label: "Structures",
            minimumZoom: 3,
            groups: [
                "bases",
                "structure_camp",
                "structure_tarpit",
                "structure_shipwreck",
                "structure_ruins",
                "structure_mistlands",
                "structure_runestone",
                "misc"
            ]
        }
    ];

    var POI_GROUPS = {
        spawn: { label: "Spawn", glyph: "⌂", category: "bosses" },
        boss: { label: "Boss altars", glyph: "☠", category: "bosses" },
        trader: { label: "Traders", glyph: "◉", category: "bosses" },
        dungeon_crypt: {
            label: "Burial Chambers", glyph: "∩", category: "dungeons", dungeonEntrance: true
        },
        dungeon_sunkencrypt: {
            label: "Sunken Crypts", glyph: "≋", category: "dungeons", dungeonEntrance: true
        },
        dungeon_trollcave: {
            label: "Troll Caves", glyph: "△", category: "dungeons", dungeonEntrance: true
        },
        dungeon_frostcave: {
            label: "Frost Caves", glyph: "❄", category: "dungeons", dungeonEntrance: true
        },
        dungeon_mine: {
            label: "Infested Mines", glyph: "⛏", category: "dungeons", dungeonEntrance: true
        },
        dungeon_ashlands: { label: "Ashlands Ruins", glyph: "♨", category: "dungeons" },
        spawner_greydwarf: {
            label: "Greydwarf Nests", glyph: "♣", category: "spawners", resource: true
        },
        spawner_bonepile: {
            label: "Skeleton Spawners", glyph: "☠", category: "spawners", resource: true
        },
        spawner_draugrpile: {
            label: "Draugr Spawners", glyph: "⚔", category: "spawners", resource: true
        },
        spawner_firehole: { label: "Surtling Geysers", glyph: "♨", category: "spawners" },
        spawner_charred: { label: "Charred Spawners", glyph: "✦", category: "spawners" },
        spawner_other: { label: "Other Spawners", glyph: "•", category: "spawners" },
        ore_copper: { label: "Copper", glyph: "Cu", category: "ores", resource: true },
        ore_tin: { label: "Tin", glyph: "Sn", category: "ores", resource: true },
        ore_iron: { label: "Muddy Scrap Piles", glyph: "Fe", category: "ores", resource: true },
        ore_silver: { label: "Silver Veins", glyph: "Ag", category: "ores", resource: true },
        ore_obsidian: { label: "Obsidian", glyph: "◆", category: "ores", resource: true },
        ore_meteorite: { label: "Meteorite", glyph: "✦", category: "ores", resource: true },
        ore_leviathan: { label: "Leviathans", glyph: "◉", category: "ores", resource: true },
        forage_berries: {
            label: "Berry Bushes", glyph: "●", category: "forage", resource: true,
            searchGroupOnly: true
        },
        forage_thistle: {
            label: "Thistle", glyph: "✣", category: "forage", resource: true,
            searchGroupOnly: true
        },
        forage_mushroom: {
            label: "Mushrooms", glyph: "♠", category: "forage", resource: true,
            searchGroupOnly: true
        },
        forage_seeds: {
            label: "Wild Seeds", glyph: "⁙", category: "forage", resource: true,
            searchGroupOnly: true
        },
        forage_crops: {
            label: "Barley & Flax", glyph: "≋", category: "forage", resource: true,
            searchGroupOnly: true
        },
        forage_dragonegg: {
            label: "Dragon Eggs", glyph: "◍", category: "forage", resource: true,
            searchGroupOnly: true
        },
        forage_blackcore: {
            label: "Black Cores", glyph: "◆", category: "forage", resource: true,
            searchGroupOnly: true
        },
        structure_camp: { label: "Enemy Camps", glyph: "⚔", category: "structures" },
        bases: {
            label: "Bases", glyph: "⌂", category: "structures", resource: true, base: true
        },
        structure_tarpit: { label: "Tar Pits", glyph: "≋", category: "structures" },
        structure_shipwreck: { label: "Shipwrecks", glyph: "⚓", category: "structures" },
        structure_ruins: { label: "Ruins & Villages", glyph: "▥", category: "structures" },
        structure_mistlands: { label: "Mistlands Remains", glyph: "†", category: "structures" },
        structure_runestone: { label: "Runestones & Lore", glyph: "ᚱ", category: "structures" },
        misc: { label: "Misc", glyph: "◇", category: "structures" },
        ghosts: { label: "Last seen", glyph: "♙", category: "live", dynamic: true }
    };

    var POI_GROUP_ORDER = [];
    POI_CATEGORIES.forEach(function (category) {
        category.groups.forEach(function (group) {
            POI_GROUP_ORDER.push(group);
        });
    });
    POI_GROUP_ORDER.push("ghosts");

    var ENTITY_GROUP_ORDER = [
        "creatures", "ship", "cart", "portal", "ward", "bed", "tombstone"
    ];
    var ENTITY_GROUPS = {
        creatures: { label: "Creatures", glyph: "☠" },
        ship: { label: "Ships", glyph: "⛵" },
        cart: { label: "Carts", glyph: "▣" },
        portal: { label: "Portals", glyph: "◊" },
        ward: { label: "Wards", glyph: "ᛉ" },
        bed: { label: "Beds", glyph: "▰" },
        tombstone: { label: "Tombstones", glyph: "☠" }
    };

    function iconMarkup(iconKey, fallbackGlyph) {
        if (window.VO_ICONS && typeof window.VO_ICONS[iconKey] === "string") {
            return window.VO_ICONS[iconKey];
        }
        return fallbackGlyph;
    }

    function poiIconKey(record) {
        if (typeof window.VO_ICON_FOR_POI === "function") {
            var resolved = window.VO_ICON_FOR_POI(record);
            if (typeof resolved === "string" && resolved) {
                return resolved;
            }
        }
        return record.group;
    }

    function bossIconKey(record) {
        var iconKey = poiIconKey(record);
        return iconKey.indexOf("boss_") === 0 ? iconKey : "";
    }

    function creatureIconKey(entity) {
        var prefab = entity && typeof entity.prefab === "string"
            ? entity.prefab.replace(/[^a-z0-9]/gi, "").toLowerCase()
            : "";
        if (prefab === "eikthyr") {
            return "boss_eikthyr";
        }
        if (prefab === "gdking") {
            return "boss_elder";
        }
        if (prefab === "bonemass") {
            return "boss_bonemass";
        }
        if (prefab === "dragon") {
            return "boss_moder";
        }
        if (prefab === "goblinking") {
            return "boss_yagluth";
        }
        if (prefab === "seekerqueen") {
            return "boss_queen";
        }
        if (prefab === "fader") {
            return "boss_fader";
        }
        if (prefab === "serpent") {
            return "creature_serpent";
        }
        return "creature_hostile";
    }

    function movingEntityGroup(group) {
        return group === "ship" || group === "cart" || group === "creatures";
    }

    function layerIconKey(key) {
        if (key === "pins") {
            return "pin";
        }
        if (Object.prototype.hasOwnProperty.call(POI_GROUPS, key) ||
            Object.prototype.hasOwnProperty.call(ENTITY_GROUPS, key)) {
            return key;
        }
        return "";
    }

    var LAYER_DEFAULTS = {
        players: true,
        pins: true,
        webpins: true,
        trails: false,
        ghosts: false,
        spawn: true,
        trader: true,
        boss: true,
        dungeon_crypt: false,
        dungeon_sunkencrypt: false,
        dungeon_trollcave: false,
        dungeon_frostcave: false,
        dungeon_mine: false,
        dungeon_ashlands: false,
        spawner_greydwarf: false,
        spawner_bonepile: false,
        spawner_draugrpile: false,
        spawner_firehole: false,
        spawner_charred: false,
        spawner_other: false,
        ore_copper: false,
        ore_tin: false,
        ore_iron: false,
        ore_silver: false,
        ore_obsidian: false,
        ore_meteorite: false,
        ore_leviathan: false,
        forage_berries: false,
        forage_thistle: false,
        forage_mushroom: false,
        forage_seeds: false,
        forage_crops: false,
        forage_dragonegg: false,
        forage_blackcore: false,
        structure_camp: false,
        bases: false,
        structure_tarpit: false,
        structure_shipwreck: false,
        structure_ruins: false,
        structure_mistlands: false,
        structure_runestone: false,
        misc: false,
        fog: true,
        heatmap: false,
        heatmapWindow: "24h",
        timelapse: false,
        timelapseSpeed: "4x",
        regions: true,
        tint: true,
        minimap: false,
        creatures: false,
        ship: true,
        cart: true,
        portal: true,
        portalNetwork: false,
        ward: false,
        bed: false,
        tombstone: false,
        densityDots: false,
        iconSize: "m",
        poiColors: {},
        poiCollapsed: {
            dungeons: true,
            spawners: true,
            ores: true,
            forage: true,
            structures: true
        },
        poiOpacity: 100,
        legendCollapsed: false,
        mapStyle: "default"
    };

    var query = new URLSearchParams(window.location.search);
    var token = embedMode ? "" : query.get("token") || "";
    var failedFeeds = new Set();
    var consecutiveStatusFailures = 0;
    var pollFailureCounts = Object.create(null);
    var pollCircuitOpen = false;
    var recurringPollTimers = new Set();
    var markerRecords = new Map();
    var markerTweens = new Map();
    var markerTweenFrame = 0;
    var playerTweenDurationMs = POLL_INTERVAL_MS;
    var lastPlayerSnapshotUnixMs = 0;
    var latestPlayers = [];
    var latestEntities = [];
    var entityGroupMeta = new Map();
    var latestPlayerCount = 0;
    var map = null;
    var tileLayer = null;
    var baseOverlay = null;
    var baseOverlayDisplayedRevision = null;
    var baseOverlayRequestedRevision = null;
    var baseOverlayLoadSequence = 0;
    var mapMetrics = null;
    var worldBounds = null;
    var hashViewApplied = false;
    var firstPlayersViewApplied = false;
    var hashUpdateTimer = 0;
    var pendingHashFollowName = "";
    var pendingCinemaFromHash = false;
    var firstPlayersPayloadReceived = false;
    var followTarget = null;
    var followPill = null;
    var cinemaState = null;
    var cinemaRaidOptOutIds = new Set();
    var nextCinemaRaidId = 1;
    var compassButton = null;
    var compassWindNeedle = null;
    var cinemaWindNeedle = null;
    var scaleBarElement = null;
    var coordinateChip = null;
    var coordinateUsesMapCenter = window.matchMedia("(hover: none), (pointer: coarse)").matches;
    var hoverMiniCardsEnabled = window.matchMedia(
        "(hover: hover) and (pointer: fine)"
    ).matches;
    var minimapElement = null;
    var minimapImage = null;
    var minimapViewRect = null;
    var minimapFrame = 0;
    var minimapSetOpen = null;
    var measureButton = null;
    var webPinButton = null;
    var webPinPlacementArmed = false;
    var webPinDialog = null;
    var webPinDialogState = null;
    var measureHud = null;
    var measureLine = null;
    var measureLayer = null;
    var measureModeEnabled = false;
    var measureActive = false;
    var measurePoints = [];
    var measureVertexMarkers = [];
    var measureDoubleClickZoomWasEnabled = false;
    var mapContextMenu = null;
    var mapContextMenuTimer = 0;
    var mapContextMenuGeneration = 0;
    var pingButton = null;
    var pingControlElement = null;
    var pingArmed = false;
    var pingRequestPending = false;
    var towState = null;
    var towRequestPending = false;
    var towBanner = null;
    var pendingShipTowTweenIds = new Set();
    var pingLayer = null;
    var pendingMapPings = [];
    var activePingMarkers = new Set();
    var chatLayer = null;
    var pendingChatBubbles = [];
    var activeChatBubbles = [];
    var playerLayer = null;
    var pinLayer = null;
    var latestPins = [];
    var webPinLayer = null;
    var latestWebPins = [];
    var webPinsRevision = null;
    var webPinsAvailable = false;
    var webPinsSharedEditing = false;
    var webPinsProbed = false;
    var webPinsFetchPending = false;
    var webPinsFetchQueued = false;
    var webPinsPollingStarted = false;
    var trailLayer = null;
    var shipHeadingLayer = null;
    var shipHeadingLines = new Map();
    var trailBuffers = new Map();
    var trailBackfillWindows = new Map();
    var selectedTrailTargets = new Map();
    var openPopupTrailTarget = null;
    var popupRefreshTimer = 0;
    var raidProgressTimer = 0;
    var nextShipTrackId = 1;
    var poiLayers = new Map();
    var poiRecords = new Map();
    var poiGroupMeta = new Map();
    var lazyPoiStates = new Map();
    var resourceSurveyToastGroups = new Set();
    var availablePoiGroups = new Set();
    var entityLayers = new Map();
    var entityAvailability = "unknown";
    var entityRequestPending = false;
    var entityPollTimer = 0;
    var entityFocusPollTimer = 0;
    var entityFocusRequestPending = false;
    var entityRevision = null;
    var entityTweenDurationMs = ENTITIES_POLL_INTERVAL_MS;
    var lastEntityRevisionUnixMs = 0;
    var entityFocusTweenDurationMs = POLL_INTERVAL_MS;
    var lastEntityFocusUnixMs = 0;
    var entityMarkerRecords = new Map();
    var portalMarkerRecords = new Map();
    var portalPairs = [];
    var portalNetworkLayer = null;
    var portalPopupLinkLayer = null;
    var wardRadiusLayer = null;
    var openPopupPortalId = "";
    var raidCircle = null;
    var currentRaidEvent = null;
    var currentTimeOfDay = null;
    var currentStatusDay = null;
    var renderRevision = "0";
    var overviewClusterRenderZoom = null;
    var latestMapStatus = null;
    var displayedMapStyle = "default";
    var mapStyleProbeRequested = "";
    var renderStatusFailureTimer = 0;
    var mapLoadingTimeoutTimer = 0;
    var initialMapLoadingComplete = false;
    var initialMapLoadingTimedOut = false;
    var lastSavedUnixMs = 0;
    var savedBadgeTimer = 0;
    var dayToastTimer = 0;
    var noticeToastTimer = 0;
    var bossProgressionState = "";
    var bossJumpServedIndices = new Map();
    var storageWriteWarningShown = false;
    var noticeToastElement = embedElementById("notice-toast");
    var latestWind = null;
    var tintOverlay = null;
    var regionLayer = null;
    var regionLabelRecords = [];
    var regionsRequested = false;
    var layerSettings = loadLayerSettings();
    var layersRows = null;
    var layersSetCollapsed = null;
    var layersStalenessTimer = 0;
    var legendContent = null;
    var zoomGatedPoiCategories = new Set();
    var searchControlElement = null;
    var searchInput = null;
    var searchResultsElement = null;
    var searchResultItems = [];
    var searchResultIndex = -1;
    var coordinateSearchMarker = null;
    var coordinateSearchTimer = 0;
    var currentView = null;
    var sagaEvents = [];
    var sagaChatEvents = [];
    var chatHistory = [];
    var chatHistoryRequested = false;
    var chatHistoryRequestSequence = 0;
    var chatSequences = new Set();
    var liveChatSequences = new Set();
    var chatSendPending = false;
    var sagaCursor = 0;
    var sagaEnabled = null;
    var sagaLoaded = false;
    var sagaLoadFailed = false;
    var sagaRequestPending = false;
    var sagaRequestSequence = 0;
    var pendingSagaPayloads = [];
    var sagaRelativeTimer = 0;
    var leaderboardPlayers = [];
    var leaderboardLoaded = false;
    var leaderboardLoadFailed = false;
    var leaderboardRequestPending = false;
    var leaderboardRequestSequence = 0;
    var leaderboardPollTimer = 0;
    var dungeonRegistryState = {
        dungeons: [],
        loaded: false,
        pending: false,
        ready: false,
        scanning: false,
        timer: 0
    };
    var dungeonDetailCache = new Map();
    var dungeonDetailPollTimer = 0;
    var dungeonDetailRequestPending = false;
    var dungeonDetailRequestSequence = 0;
    var activeDungeonId = "";
    var dungeonReturnFocus = null;
    var dungeonResizeObserver = null;
    var lastPoiRequestedView = null;
    var poiRequestSequence = 0;
    var poiLoadPending = false;
    var pinsPollingStarted = false;
    var fogStatus = { mode: "off", revision: "0", size: 0 };
    var fogAvailable = false;
    var fogOverlay = null;
    var fogDisplayedRevision = null;
    var fogRequestedRevision = null;
    var fogLoadSequence = 0;
    var fogCoverElement = null;
    var fogCoverTimer = 0;
    var heatmapLayer = null;
    var heatmapLegendElement = null;
    var heatmapWindowControlElement = null;
    var heatmapPollTimer = 0;
    var heatmapRequestPending = false;
    var heatmapRequestSequence = 0;
    var latestHeatmap = null;
    var timelapseAvailability = "unknown";
    var timelapseIndex = null;
    var timelapseIndexPromise = null;
    var timelapseFrameCache = new Map();
    var timelapseFrameRequests = new Map();
    var timelapseRequestSequence = 0;
    var timelapseCurrentIndex = -1;
    var timelapseRequestedIndex = -1;
    var timelapseRenderedFrame = null;
    var timelapseFogLayer = null;
    var timelapseMovementLayer = null;
    var timelapseMarkerLayer = null;
    var timelapseScrubber = null;
    var timelapseTrack = null;
    var timelapsePlayButton = null;
    var timelapseReadoutDay = null;
    var timelapseReadoutDate = null;
    var timelapseSpeedControl = null;
    var timelapsePlaying = false;
    var timelapseAnimationFrame = 0;
    var timelapseAnimationTimestamp = 0;
    var timelapseAnimationAccumulator = 0;
    var timelapseTrackSyncing = false;
    var timelapseRestoreVisibility = null;
    var timelapseBasePulses = new Map();
    var initialCodexToken = codexTokenFromHash(appHash());
    var requestedTab = initialCodexToken !== null ? "codex" : loadRequestedTab();
    var activeTab = "map";
    var consoleAvailable = false;
    var catalogPayload = null;
    var catalogPromise = null;
    var catalogItems = [];
    var catalogItemsByToken = new Map();
    var catalogReverseRecipes = new Map();
    var codexFilteredItems = [];
    var codexExpandedToken = "";
    var codexExpandedHeight = 0;
    var codexWindowKey = "";
    var codexSearchTimer = 0;
    var codexShowAllReverseUses = false;
    var pendingCodexToken = initialCodexToken || "";
    var consolePollingStarted = false;
    var consoleLogPollTimer = 0;
    var statsPollingStarted = false;
    var statsPollTimer = 0;
    var consoleLogRequestPending = false;
    var statsRequestPending = false;
    var consoleBanRequestPending = false;
    var consoleBanRefreshQueued = false;
    var consoleMetaRequestPending = false;
    var consoleMetaLoaded = false;
    var consoleMetaPromise = null;
    var consoleHistoryLoaded = false;
    var consoleHistoryPromise = null;
    var pendingConsoleLogPayloads = [];
    var consoleCursor = 0;
    var consoleFollowLog = true;
    var consoleCommands = [];
    var consoleSuggestions = [];
    var consoleSuggestionIndex = -1;
    var consoleSuggestionClosed = false;
    var commandHistory = [];
    var commandHistoryIndex = 0;
    var commandHistoryDraft = "";
    var consoleFailures = Object.create(null);
    var confirmAction = null;
    var saveButtonTimer = 0;
    var eventSource = null;
    var eventSourceOpen = false;
    var eventSourceLogFlowing = false;
    var eventSourceRetryTimer = 0;
    var eventSourceRetryDelay = SSE_RETRY_INITIAL_MS;
    var latestStatusSnapshotStale = null;
    var feedLastUpdated = {
        entities: 0,
        pins: 0,
        webpins: 0,
        players: 0,
        pois: 0,
        fog: 0,
        heatmap: 0,
        status: 0
    };

    var elements = {
        bossProgression: embedElementById("boss-progression"),
        bannedCount: embedElementById("console-banned-count"),
        bannedList: embedElementById("console-banned-list"),
        chatContent: embedElementById("chat-content"),
        chatForm: embedElementById("chat-form"),
        chatInput: embedElementById("chat-input"),
        chatList: embedElementById("chat-list"),
        chatNote: embedElementById("chat-note"),
        chatPanel: embedElementById("chat-panel"),
        chatSend: embedElementById("chat-send"),
        chatSendNotice: embedElementById("chat-send-notice"),
        chatToggle: embedElementById("chat-toggle"),
        commandForm: embedElementById("console-command-form"),
        commandInput: embedElementById("console-command"),
        commandReference: embedElementById("console-command-reference"),
        commandReferenceBody: embedElementById("console-command-reference-body"),
        commandReferenceClose: embedElementById("console-command-reference-close"),
        commandsToggle: embedElementById("console-commands-toggle"),
        cinemaClock: embedElementById("cinema-clock"),
        cinemaDay: embedElementById("cinema-day"),
        cinemaExit: embedElementById("cinema-exit"),
        cinemaHud: embedElementById("cinema-hud"),
        cinemaModeChip: embedElementById("cinema-mode-chip"),
        cinemaPlayerBiome: embedElementById("cinema-player-biome"),
        cinemaPlayerCard: embedElementById("cinema-player-card"),
        cinemaPlayerHeading: embedElementById("cinema-player-heading"),
        cinemaPlayerName: embedElementById("cinema-player-name"),
        cinemaPlayerSession: embedElementById("cinema-player-session"),
        cinemaPlayerSpeed: embedElementById("cinema-player-speed"),
        cinemaSecondaryChip: embedElementById("cinema-secondary-chip"),
        cinemaServerName: embedElementById("cinema-server-name"),
        cinemaStaleness: embedElementById("cinema-staleness"),
        cinemaStayTarget: embedElementById("cinema-stay-target"),
        cinemaWind: embedElementById("cinema-wind"),
        cinemaWindLabel: embedElementById("cinema-wind-label"),
        confirmBackdrop: embedElementById("console-confirm-backdrop"),
        confirmCancel: embedElementById("console-confirm-cancel"),
        confirmMessage: embedElementById("console-confirm-message"),
        confirmSubmit: embedElementById("console-confirm-submit"),
        codexCategory: embedElementById("codex-category"),
        codexCount: embedElementById("codex-count"),
        codexList: embedElementById("codex-list"),
        codexPane: embedElementById("codex-pane"),
        codexScroll: embedElementById("codex-scroll"),
        codexSearch: embedElementById("codex-search"),
        codexState: embedElementById("codex-state"),
        codexTab: embedElementById("codex-tab"),
        codexVersion: embedElementById("codex-version"),
        consoleLog: embedElementById("console-log"),
        consolePane: embedElementById("console-pane"),
        consoleResume: embedElementById("console-resume"),
        consoleTab: embedElementById("console-tab"),
        dayToast: embedElementById("day-toast"),
        dayNumber: embedElementById("day-number"),
        dungeonBackdrop: embedElementById("dungeon-backdrop"),
        dungeonCanvas: embedElementById("dungeon-canvas"),
        dungeonCanvasShell: embedElementById("dungeon-canvas-shell"),
        dungeonClose: embedElementById("dungeon-close"),
        dungeonElevation: embedElementById("dungeon-elevation"),
        dungeonEmpty: embedElementById("dungeon-empty"),
        dungeonEmptyCopy: embedElementById("dungeon-empty-copy"),
        dungeonEntranceInfo: embedElementById("dungeon-entrance-info"),
        dungeonError: embedElementById("dungeon-error"),
        dungeonGenerated: embedElementById("dungeon-generated"),
        dungeonLiveStatus: embedElementById("dungeon-live-status"),
        dungeonLoading: embedElementById("dungeon-loading"),
        dungeonRooms: embedElementById("dungeon-rooms"),
        dungeonScale: embedElementById("dungeon-scale"),
        dungeonTitle: embedElementById("dungeon-title"),
        dungeonType: embedElementById("dungeon-type"),
        exploredChip: embedElementById("explored-chip"),
        exploredLabel: embedElementById("sidebar-explored-label"),
        joinCode: embedElementById("join-code"),
        joinCodeCopy: embedElementById("join-code-copy"),
        joinCodeLine: embedElementById("join-code-line"),
        mapPane: embedElementById("map"),
        metricDay: embedElementById("map-metric-day"),
        metricDayItem: embedElementById("map-metric-day-item"),
        metricFrame: embedElementById("map-metric-frame"),
        metricFrameItem: embedElementById("map-metric-frame-item"),
        metricStatus: embedElementById("map-metric-status"),
        metricUptime: embedElementById("map-metric-uptime"),
        metricUptimeItem: embedElementById("map-metric-uptime-item"),
        metricZdo: embedElementById("map-metric-zdo"),
        metricZdoItem: embedElementById("map-metric-zdo-item"),
        mapTab: embedElementById("map-tab"),
        mapStatus: embedElementById("render-status"),
        mapStatusText: embedElementById("render-status-text"),
        leaderboardContent: embedElementById("leaderboard-content"),
        leaderboardList: embedElementById("leaderboard-list"),
        leaderboardNote: embedElementById("leaderboard-note"),
        leaderboardPanel: embedElementById("leaderboard-panel"),
        leaderboardTable: embedElementById("leaderboard-table"),
        leaderboardToggle: embedElementById("leaderboard-toggle"),
        offlineBadge: embedElementById("offline-badge"),
        playerCount: embedElementById("player-count"),
        playerList: embedElementById("player-list"),
        publicViewBadge: embedElementById("public-view-badge"),
        raidBadge: embedElementById("raid-badge"),
        saveButton: embedElementById("console-save"),
        savedChip: embedElementById("saved-chip"),
        savedLabel: embedElementById("sidebar-saved-label"),
        saveStatus: embedElementById("console-save-status"),
        sagaChevron: embedElementById("saga-chevron"),
        sagaContent: embedElementById("saga-content"),
        sagaList: embedElementById("saga-list"),
        sagaNote: embedElementById("saga-note"),
        sagaPanel: embedElementById("saga-panel"),
        sagaToggle: embedElementById("saga-toggle"),
        serverName: embedElementById("server-name"),
        sidebarState: embedElementById("sidebar-state"),
        sidebarWindNeedle: embedElementById("sidebar-wind-needle"),
        sidebarWindLabel: embedElementById("sidebar-wind-label"),
        skyIndicator: embedElementById("sky-indicator"),
        statFrameAvg: embedElementById("console-stat-frame-avg"),
        statFrameMax: embedElementById("console-stat-frame-max"),
        statHeap: embedElementById("console-stat-heap"),
        statPlayers: embedElementById("console-stat-players"),
        statUptime: embedElementById("console-stat-uptime"),
        statZdo: embedElementById("console-stat-zdo"),
        statusChips: embedElementById("status-chips"),
        suggestionList: embedElementById("console-suggestions"),
        tabList: embedElementById("view-tabs"),
        consolePlayerCount: embedElementById("console-player-count"),
        consolePlayerList: embedElementById("console-player-list"),
        worldClock: embedElementById("world-clock"),
        worldName: embedElementById("world-name"),
        windChip: embedElementById("wind-chip"),
        watchButton: embedElementById("watch-button")
    };

    function hasLiveAccess() {
        return currentView !== "public";
    }

    function authorizedUrl(path, includeToken) {
        var url = embedMode
            ? embedApiBase + path.replace(/^\/+/, "")
            : path;
        if (!token || includeToken === false) {
            return url;
        }

        return url + (url.indexOf("?") === -1 ? "?" : "&") +
            "token=" + encodeURIComponent(token);
    }

    function sanitizeMapStyle(style) {
        return style === "topo" || style === "chart" ? style : "default";
    }

    function mapStyleStatus(statusMap, style) {
        if (style === "default") {
            return statusMap || null;
        }
        if (!statusMap || !statusMap.styles ||
            !Object.prototype.hasOwnProperty.call(statusMap.styles, style)) {
            return null;
        }
        return statusMap.styles[style];
    }

    function mapStyleRevision(style) {
        if (style === "default") {
            return renderRevision;
        }
        var status = mapStyleStatus(latestMapStatus, style);
        return status && typeof status.revision === "string" && status.revision
            ? status.revision
            : "0";
    }

    function mapStyleCacheKey(style) {
        return style + "|" + mapStyleRevision(style);
    }

    function versionedMapUrl(path, style) {
        style = sanitizeMapStyle(style || displayedMapStyle);
        if (style === "default") {
            return authorizedUrl(path + (path.indexOf("?") === -1 ? "?" : "&") +
                "v=" + encodeURIComponent(renderRevision));
        }

        return authorizedUrl(path + (path.indexOf("?") === -1 ? "?" : "&") +
            "style=" + encodeURIComponent(style) +
            "&v=" + encodeURIComponent(mapStyleRevision(style)));
    }

    function updateRenderRevision(statusMap) {
        var previousCacheKey = mapStyleCacheKey(displayedMapStyle);
        var nextRevision = statusMap && typeof statusMap.renderRevision === "string" &&
            statusMap.renderRevision ? statusMap.renderRevision : "0";
        latestMapStatus = statusMap || null;
        renderRevision = nextRevision;
        var nextCacheKey = mapStyleCacheKey(displayedMapStyle);
        if (tileLayer && previousCacheKey !== nextCacheKey) {
            tileLayer.setUrl(versionedMapUrl("/tiles/{z}/{x}-{y}.png"));
        }
        refreshBaseOverlay();
        if (minimapImage && previousCacheKey !== nextCacheKey) {
            minimapImage.src = versionedMapUrl("/base.png");
        }
    }

    function refreshBaseOverlay() {
        var cacheKey = mapStyleCacheKey(displayedMapStyle);
        if (!map || !worldBounds ||
            cacheKey === baseOverlayDisplayedRevision ||
            cacheKey === baseOverlayRequestedRevision) {
            return;
        }

        var style = displayedMapStyle;
        var url = versionedMapUrl("/base.png", style);
        var loadSequence = ++baseOverlayLoadSequence;
        var image = new window.Image();
        baseOverlayRequestedRevision = cacheKey;
        image.onload = function () {
            if (loadSequence !== baseOverlayLoadSequence ||
                cacheKey !== mapStyleCacheKey(displayedMapStyle) || !map || !worldBounds) {
                return;
            }

            if (baseOverlay) {
                baseOverlay.setUrl(url);
            } else {
                baseOverlay = L.imageOverlay(image, worldBounds, {
                    className: "world-base-layer",
                    interactive: false,
                    opacity: 1,
                    pane: "basePane"
                }).addTo(map);
            }
            baseOverlayDisplayedRevision = cacheKey;
            baseOverlayRequestedRevision = cacheKey;
        };
        image.onerror = function () {
            if (loadSequence === baseOverlayLoadSequence &&
                baseOverlayRequestedRevision === cacheKey) {
                baseOverlayRequestedRevision = null;
            }
        };
        image.src = url;
    }

    function mapStyleName(style) {
        return style === "topo" ? "Topographic" : style === "chart" ? "Old Chart" : "Default";
    }

    function mapStyleRenderLabel(style) {
        return style === "topo" ? "topographic map" : "old chart map";
    }

    function syncMapStyleControl() {
        if (!layersRows) {
            return;
        }
        layersRows.querySelectorAll("[data-map-style]").forEach(function (button) {
            var isSelected = button.dataset.mapStyle === layerSettings.mapStyle;
            button.classList.toggle("is-selected", isSelected);
            button.setAttribute("aria-pressed", String(isSelected));
        });
    }

    function clearMapStyleFailureMessage() {
        window.clearTimeout(renderStatusFailureTimer);
        renderStatusFailureTimer = 0;
    }

    function showMapStyleFailure(style) {
        clearMapStyleFailureMessage();
        elements.mapStatus.hidden = false;
        elements.mapStatus.querySelector(".spinner").hidden = true;
        elements.mapStatusText.textContent = mapStyleName(style) +
            " map rendering failed — reverted to Default";
        renderStatusFailureTimer = window.setTimeout(function () {
            renderStatusFailureTimer = 0;
            updateRenderStatus(latestMapStatus);
        }, 4000);
    }

    function triggerMapStyleRender(style) {
        if (style === "default" || mapStyleProbeRequested === style) {
            return;
        }

        mapStyleProbeRequested = style;
        fetch(versionedMapUrl("/base.png", style), {
            cache: "no-store",
            credentials: "same-origin"
        }).catch(function () {
            return;
        });
    }

    function setDisplayedMapStyle(style) {
        style = sanitizeMapStyle(style);
        var previousStyle = displayedMapStyle;
        var previousCacheKey = mapStyleCacheKey(previousStyle);
        displayedMapStyle = style;
        var nextCacheKey = mapStyleCacheKey(displayedMapStyle);
        if (tileLayer && (previousStyle !== displayedMapStyle ||
            previousCacheKey !== nextCacheKey)) {
            tileLayer.setUrl(versionedMapUrl("/tiles/{z}/{x}-{y}.png"));
        }
        refreshBaseOverlay();
        if (minimapImage && (previousStyle !== displayedMapStyle ||
            previousCacheKey !== nextCacheKey)) {
            minimapImage.src = versionedMapUrl("/base.png");
        }
        applyFogStatus();
    }

    function reconcileMapStyle(statusMap) {
        var requestedStyle = sanitizeMapStyle(layerSettings.mapStyle);
        var requestedStatus = mapStyleStatus(statusMap, requestedStyle);
        if (requestedStyle === "default") {
            mapStyleProbeRequested = "";
            setDisplayedMapStyle("default");
            syncMapStyleControl();
            return;
        }
        if (!statusMap || statusMap.state !== "ready") {
            syncMapStyleControl();
            return;
        }

        if (requestedStatus && requestedStatus.state === "failed") {
            layerSettings.mapStyle = "default";
            mapStyleProbeRequested = "";
            saveLayerSettings();
            setDisplayedMapStyle("default");
            syncMapStyleControl();
            scheduleHashUpdate();
            showMapStyleFailure(requestedStyle);
            return;
        }

        if (requestedStatus && requestedStatus.state === "ready") {
            mapStyleProbeRequested = "";
            setDisplayedMapStyle(requestedStyle);
        } else {
            triggerMapStyleRender(requestedStyle);
        }
        syncMapStyleControl();
    }

    function selectMapStyle(style) {
        style = sanitizeMapStyle(style);
        clearMapStyleFailureMessage();
        layerSettings.mapStyle = style;
        mapStyleProbeRequested = "";
        saveLayerSettings();
        syncMapStyleControl();
        scheduleHashUpdate();
        reconcileMapStyle(latestMapStatus);
        updateRenderStatus(latestMapStatus);
    }

    async function fetchJson(path, options) {
        var requestOptions = {};
        Object.keys(options || {}).forEach(function (key) {
            requestOptions[key] = options[key];
        });
        requestOptions.cache = "no-store";
        requestOptions.credentials = "same-origin";
        var response = await fetch(authorizedUrl(path), requestOptions);
        var payload = null;
        try {
            payload = await response.json();
        } catch (error) {
            if (response.ok) {
                throw new Error("Invalid server response");
            }
        }
        if (!response.ok) {
            var message = payload && typeof payload.error === "string"
                ? payload.error
                : "HTTP " + response.status;
            var requestError = new Error(message);
            requestError.status = response.status;
            throw requestError;
        }

        return payload;
    }

    function loadRequestedTab() {
        try {
            var storedTab = window.sessionStorage.getItem(TAB_SESSION_KEY);
            return storedTab === "console" || storedTab === "codex" ? storedTab : "map";
        } catch (error) {
            return "map";
        }
    }

    function saveRequestedTab(tab) {
        try {
            window.sessionStorage.setItem(TAB_SESSION_KEY, tab);
        } catch (error) {
            return;
        }
    }

    function consoleIsActive() {
        return consoleAvailable && activeTab === "console";
    }

    function setTabButtonState(button, isActive) {
        button.classList.toggle("is-active", isActive);
        button.setAttribute("aria-selected", String(isActive));
        button.tabIndex = isActive ? 0 : -1;
    }

    function setActiveTab(tab, persist) {
        var nextTab = tab === "codex"
            ? "codex"
            : (tab === "console" && consoleAvailable ? "console" : "map");
        if (persist) {
            requestedTab = nextTab;
            saveRequestedTab(nextTab);
            if (window.matchMedia("(max-width: 759px)").matches) {
                elements.sidebarState.checked = false;
            }
        }

        if (nextTab === activeTab &&
            elements.consolePane.hidden === (nextTab !== "console") &&
            elements.codexPane.hidden === (nextTab !== "codex")) {
            return;
        }

        var priorTab = activeTab;
        activeTab = nextTab;
        if (activeTab !== "map" && timelapsePlaying) {
            stopTimelapsePlayback();
        }
        var showConsole = activeTab === "console";
        var showCodex = activeTab === "codex";
        setTabButtonState(elements.mapTab, !showConsole && !showCodex);
        setTabButtonState(elements.consoleTab, showConsole);
        setTabButtonState(elements.codexTab, showCodex);
        elements.consolePane.hidden = !showConsole;
        elements.codexPane.hidden = !showCodex;
        elements.mapPane.setAttribute("aria-hidden", String(showConsole || showCodex));
        appRoot.classList.toggle("is-console-active", showConsole);
        appRoot.classList.toggle("is-codex-active", showCodex);
        if (!showCodex && (priorTab === "codex" ||
            codexTokenFromHash(appHash()) !== null)) {
            restoreMapHash();
        }

        if (showConsole || showCodex) {
            disarmShipTow();
        }

        if (!showConsole) {
            closeSuggestions();
            closeConfirmDialog();
            scheduleStatsPolling(0);
            if (showCodex) {
                if (persist) {
                    writeCodexHash(codexExpandedToken);
                }
                loadCatalog();
                return;
            }
            return;
        }

        if (!persist && window.matchMedia("(max-width: 759px)").matches) {
            elements.sidebarState.checked = false;
        }

        scheduleStatsPolling(0);
        loadConsoleMeta();
        loadConsoleHistory().then(function () {
            startConsolePolling();
            pollConsoleLog();
        });
        loadBanList();
        renderConsolePlayers();
        if (persist) {
            window.setTimeout(function () {
                elements.commandInput.focus();
            }, 0);
        }
    }

    function updateConsoleAvailability(status) {
        var isAvailable = currentView === "admin" && status && status.console === true;
        consoleAvailable = isAvailable;
        elements.consoleTab.hidden = !isAvailable;
        elements.tabList.hidden = [elements.mapTab, elements.consoleTab, elements.codexTab]
            .filter(function (button) { return !button.hidden; }).length <= 1;
        elements.metricFrameItem.hidden = !isAvailable;
        elements.metricZdoItem.hidden = !isAvailable;
        if (!isAvailable) {
            setActiveTab(requestedTab, false);
            return;
        }

        startStatsPolling();
        setActiveTab(requestedTab, false);
    }

    function codexTokenFromHash(hash) {
        var match = String(hash || "").match(/^#codex(?:\/([^/?#]*))?$/i);
        if (!match) {
            return null;
        }
        if (!match[1]) {
            return "";
        }
        try {
            return decodeURIComponent(match[1]);
        } catch (error) {
            return match[1];
        }
    }

    function writeCodexHash(itemToken) {
        if (embedMode) {
            return;
        }
        var hash = "#codex";
        if (itemToken) {
            hash += "/" + encodeURIComponent(itemToken);
        }
        if (window.location.hash === hash) {
            return;
        }
        window.history.replaceState(
            window.history.state,
            "",
            window.location.pathname + window.location.search + hash
        );
    }

    function restoreMapHash() {
        if (embedMode || codexTokenFromHash(appHash()) === null) {
            return;
        }
        window.history.replaceState(
            window.history.state,
            "",
            window.location.pathname + window.location.search
        );
        scheduleHashUpdate();
    }

    function codexCategoryForType(type) {
        var rawType = typeof type === "string" ? type : "";
        var category = Object.keys(CODEX_CATEGORY_TYPES).find(function (key) {
            return CODEX_CATEGORY_TYPES[key].indexOf(rawType) !== -1;
        });
        return category || "misc";
    }

    function cleanCatalogText(value) {
        return typeof value === "string"
            ? value.replace(/<[^>]*>/g, "").replace(/\s+/g, " ").trim()
            : "";
    }

    function humanizeCatalogName(value) {
        var text = typeof value === "string" ? value : "";
        return text.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/_/g, " ").trim();
    }

    function formatCatalogNumber(value) {
        var number = Number(value);
        if (!Number.isFinite(number)) {
            return "—";
        }
        return number.toLocaleString("en-US", { maximumFractionDigits: 2 });
    }

    function codexElement(tagName, className, textValue) {
        var element = document.createElement(tagName);
        if (className) {
            element.className = className;
        }
        if (textValue !== undefined && textValue !== null) {
            element.textContent = String(textValue);
        }
        return element;
    }

    function makeCodexJumpLink(reference, className) {
        var tokenValue = reference && typeof reference.prefab === "string"
            ? reference.prefab
            : "";
        var name = reference && typeof reference.name === "string" && reference.name.trim()
            ? reference.name.trim()
            : tokenValue;
        var link = codexElement("a", className || "codex-jump-link", name || "Unknown item");
        link.href = "#codex/" + encodeURIComponent(tokenValue);
        link.dataset.codexToken = tokenValue;
        return link;
    }

    function setCodexLoadState(kind, detail) {
        elements.codexState.textContent = "";
        elements.codexState.classList.toggle("is-error", kind === "error");
        elements.codexScroll.hidden = kind !== "ready";
        if (kind === "ready") {
            elements.codexState.hidden = true;
            return;
        }

        elements.codexState.hidden = false;
        if (kind === "loading") {
            elements.codexState.appendChild(codexElement("span", "spinner"));
            elements.codexState.appendChild(codexElement("span", "", "Unrolling the item catalog…"));
            return;
        }

        var copy = codexElement("div", "codex-state-copy");
        copy.appendChild(codexElement("strong", "", "The Codex could not be opened"));
        copy.appendChild(codexElement(
            "span",
            "",
            detail || "The catalog request failed. Check the connection and try again."
        ));
        var retry = codexElement("button", "console-primary-button", "Retry");
        retry.type = "button";
        retry.dataset.codexRetry = "true";
        elements.codexState.appendChild(copy);
        elements.codexState.appendChild(retry);
    }

    function buildCatalogIndexes(payload) {
        catalogPayload = payload;
        catalogItems = payload.items.slice().sort(function (left, right) {
            var byName = String(left.name || left.token || "").localeCompare(
                String(right.name || right.token || ""),
                "en",
                { sensitivity: "base" }
            );
            return byName || String(left.token || "").localeCompare(String(right.token || ""));
        });
        catalogItemsByToken = new Map();
        catalogReverseRecipes = new Map();

        catalogItems.forEach(function (item) {
            if (item && typeof item.token === "string") {
                catalogItemsByToken.set(item.token.toLocaleLowerCase(), item);
            }
        });

        catalogItems.forEach(function (outputItem) {
            (Array.isArray(outputItem.recipes) ? outputItem.recipes : []).forEach(function (recipe) {
                if (!recipe || recipe.enabled === false) {
                    return;
                }
                (Array.isArray(recipe.ingredients) ? recipe.ingredients : []).forEach(function (ingredient) {
                    if (!ingredient || typeof ingredient.prefab !== "string") {
                        return;
                    }
                    var key = ingredient.prefab.toLocaleLowerCase();
                    var consumers = catalogReverseRecipes.get(key) || [];
                    if (!consumers.some(function (item) { return item.token === outputItem.token; })) {
                        consumers.push(outputItem);
                    }
                    catalogReverseRecipes.set(key, consumers);
                });
            });
        });

        var version = payload.version || {};
        var versionParts = [];
        if (version.game) {
            versionParts.push("Game " + version.game);
        }
        if (version.mod) {
            versionParts.push("ValheimOne " + version.mod);
        }
        if (version.schema !== undefined) {
            versionParts.push("schema " + version.schema);
        }
        elements.codexVersion.textContent = versionParts.length > 0
            ? versionParts.join(" · ")
            : "Valheim item & recipe catalog";
        elements.codexSearch.disabled = false;
        elements.codexCategory.disabled = false;
        setCodexLoadState("ready");
        applyCodexFilters(true);

        if (pendingCodexToken) {
            var jumpToken = pendingCodexToken;
            pendingCodexToken = "";
            jumpToCodexItem(jumpToken, false);
        }
    }

    async function loadCatalog() {
        if (catalogPayload) {
            setCodexLoadState("ready");
            return catalogPayload;
        }
        if (catalogPromise) {
            return catalogPromise;
        }

        elements.codexSearch.disabled = true;
        elements.codexCategory.disabled = true;
        setCodexLoadState("loading");
        catalogPromise = (async function () {
            try {
                var response = await fetch(authorizedUrl("/api/catalog", false), {
                    credentials: "same-origin"
                });
                if (!response.ok) {
                    throw new Error("HTTP " + response.status);
                }
                var payload = await response.json();
                if (!payload || !Array.isArray(payload.items)) {
                    throw new Error("Invalid catalog response");
                }
                buildCatalogIndexes(payload);
                return payload;
            } catch (error) {
                setCodexLoadState("error");
                throw error;
            } finally {
                catalogPromise = null;
            }
        }());
        catalogPromise.catch(function () {
            return null;
        });
        return catalogPromise;
    }

    function applyCodexFilters(resetScroll) {
        if (!catalogPayload) {
            return;
        }
        var queryText = elements.codexSearch.value.trim().toLocaleLowerCase();
        var category = elements.codexCategory.value || "all";
        codexFilteredItems = catalogItems.filter(function (item) {
            if (category !== "all" && codexCategoryForType(item.type) !== category) {
                return false;
            }
            if (!queryText) {
                return true;
            }
            return [item.name, item.token, cleanCatalogText(item.description)]
                .join("\n")
                .toLocaleLowerCase()
                .indexOf(queryText) !== -1;
        });

        if (!codexFilteredItems.some(function (item) { return item.token === codexExpandedToken; })) {
            codexExpandedToken = "";
            codexExpandedHeight = 0;
            codexShowAllReverseUses = false;
        }
        elements.codexCount.textContent = codexFilteredItems.length.toLocaleString("en-US") +
            " of " + catalogItems.length.toLocaleString("en-US") + " items";
        if (resetScroll !== false) {
            elements.codexScroll.scrollTop = 0;
        }
        codexWindowKey = "";
        renderCodexWindow(true);
    }

    function codexExpandedIndex() {
        if (!codexExpandedToken) {
            return -1;
        }
        return codexFilteredItems.findIndex(function (item) {
            return item.token === codexExpandedToken;
        });
    }

    function codexIndexAtOffset(offset, expandedIndex) {
        if (expandedIndex < 0 || codexExpandedHeight <= 0) {
            return Math.floor(offset / CODEX_ROW_HEIGHT);
        }
        var expandedRowBottom = (expandedIndex + 1) * CODEX_ROW_HEIGHT;
        if (offset <= expandedRowBottom) {
            return Math.floor(offset / CODEX_ROW_HEIGHT);
        }
        if (offset < expandedRowBottom + codexExpandedHeight) {
            return expandedIndex;
        }
        return Math.floor((offset - codexExpandedHeight) / CODEX_ROW_HEIGHT);
    }

    function createCodexQuickStat(label, value, className) {
        var stat = codexElement("span", "codex-quick-stat" + (className ? " " + className : ""));
        stat.appendChild(codexElement("b", "", label));
        stat.appendChild(document.createTextNode(" " + value));
        return stat;
    }

    function createCodexItemRow(item, index, top, expanded) {
        var entry = codexElement("div", "codex-item-entry");
        entry.setAttribute("role", "listitem");
        entry.style.top = top + "px";

        var row = codexElement("button", "codex-item-row" + (expanded ? " is-expanded" : ""));
        row.type = "button";
        row.dataset.codexOpen = item.token;
        row.setAttribute("aria-expanded", String(expanded));
        if (expanded) {
            row.setAttribute("aria-controls", "codex-detail-" + index);
        }

        var category = codexCategoryForType(item.type);
        var glyph = codexElement("span", "codex-category-glyph");
        glyph.title = CODEX_CATEGORY_LABELS[category];
        glyph.innerHTML = iconMarkup("codex_" + category, "◇");
        row.appendChild(glyph);

        var identity = codexElement("span", "codex-item-identity");
        identity.appendChild(codexElement("strong", "codex-item-name", item.name || item.token));
        identity.appendChild(codexElement("small", "codex-item-token", item.token));
        row.appendChild(identity);
        row.appendChild(codexElement("span", "codex-item-type", humanizeCatalogName(item.type) || "Misc"));

        var quickStats = codexElement("span", "codex-quick-stats");
        quickStats.appendChild(createCodexQuickStat("Wt", formatCatalogNumber(item.weight)));
        quickStats.appendChild(createCodexQuickStat("Stack", formatCatalogNumber(item.maxStackSize)));
        quickStats.appendChild(createCodexQuickStat(
            "",
            item.teleportable === false ? "No portal" : "Portal",
            item.teleportable === false ? "is-no-portal" : "is-portal"
        ));
        row.appendChild(quickStats);
        row.appendChild(codexElement("span", "codex-row-chevron", "›"));
        entry.appendChild(row);
        return entry;
    }

    function catalogDamageSummary(damage) {
        if (!damage || typeof damage !== "object") {
            return "";
        }
        var base = damage.base && typeof damage.base === "object" ? damage.base : {};
        var perLevel = damage.perLevel && typeof damage.perLevel === "object" ? damage.perLevel : {};
        return Array.from(new Set(Object.keys(base).concat(Object.keys(perLevel)))).filter(function (key) {
            return Number(base[key]) !== 0 || Number(perLevel[key]) !== 0;
        }).map(function (key) {
            var value = humanizeCatalogName(key);
            value = value.charAt(0).toUpperCase() + value.slice(1);
            value += " " + formatCatalogNumber(base[key] || 0);
            if (Number(perLevel[key])) {
                value += " (+" + formatCatalogNumber(perLevel[key]) + "/quality)";
            }
            return value;
        }).join(" · ");
    }

    function appendCodexStat(stats, label, value) {
        var record = codexElement("div", "codex-stat");
        record.appendChild(codexElement("dt", "", label));
        record.appendChild(codexElement("dd", "", value));
        stats.appendChild(record);
    }

    function createCodexSection(title) {
        var section = codexElement("section", "codex-detail-section");
        section.appendChild(codexElement("h2", "", title));
        var body = codexElement("div", "codex-detail-section-body");
        section.appendChild(body);
        return { section: section, body: body };
    }

    function appendCodexRecipe(sectionBody, recipe) {
        var recipeCard = codexElement("article", "codex-recipe");
        var heading = codexElement("div", "codex-recipe-heading");
        if (recipe.station && recipe.station.name) {
            heading.appendChild(document.createTextNode("Crafted at "));
            heading.appendChild(codexElement("strong", "", recipe.station.name));
            if (Number.isFinite(Number(recipe.minStationLevel))) {
                heading.appendChild(document.createTextNode(" (lvl " + recipe.minStationLevel + ")"));
            }
        } else {
            heading.textContent = "Crafted by hand";
        }
        if (Number(recipe.amount) > 1) {
            heading.appendChild(document.createTextNode(" · makes " + formatCatalogNumber(recipe.amount)));
        }
        recipeCard.appendChild(heading);

        var ingredients = codexElement("div", "codex-ingredients");
        (Array.isArray(recipe.ingredients) ? recipe.ingredients : []).forEach(function (ingredient, index) {
            if (index > 0) {
                ingredients.appendChild(document.createTextNode(", "));
            }
            ingredients.appendChild(makeCodexJumpLink(ingredient));
            ingredients.appendChild(document.createTextNode(" ×" + formatCatalogNumber(ingredient.amount)));
            if (Number(ingredient.amountPerLevel) > 0) {
                ingredients.appendChild(codexElement(
                    "small",
                    "codex-per-level",
                    " +" + formatCatalogNumber(ingredient.amountPerLevel) + "/quality"
                ));
            }
        });
        recipeCard.appendChild(ingredients);
        sectionBody.appendChild(recipeCard);
    }

    function appendCodexConversion(sectionBody, record, outputKey) {
        var line = codexElement("div", "codex-conversion");
        var stationName = record.station && record.station.name
            ? record.station.name
            : humanizeCatalogName(record.method || "Source");
        line.appendChild(codexElement("strong", "", stationName + ":"));
        line.appendChild(document.createTextNode(" " + formatCatalogNumber(record.amount || 1) + "× "));
        line.appendChild(makeCodexJumpLink(record[outputKey] || {}));
        sectionBody.appendChild(line);
    }

    function createCodexDetail(item, index, top) {
        var card = codexElement("article", "codex-item-detail");
        card.id = "codex-detail-" + index;
        card.setAttribute("role", "region");
        card.setAttribute("aria-label", (item.name || item.token) + " details");
        card.style.top = top + "px";

        var description = cleanCatalogText(item.description);
        if (description) {
            card.appendChild(codexElement("p", "codex-description", description));
        }

        var stats = codexElement("dl", "codex-stat-block");
        appendCodexStat(stats, "Weight", formatCatalogNumber(item.weight));
        appendCodexStat(stats, "Stack", formatCatalogNumber(item.maxStackSize));
        appendCodexStat(stats, "Max quality", formatCatalogNumber(item.maxQuality));
        appendCodexStat(stats, "Tool tier", formatCatalogNumber(item.toolTier));
        appendCodexStat(stats, "Portal", item.teleportable === false ? "No" : "Yes");
        var damageSummary = catalogDamageSummary(item.damage);
        if (damageSummary) {
            appendCodexStat(stats, "Damage", damageSummary);
        }
        if (item.armor && Number.isFinite(Number(item.armor.base))) {
            var armorSummary = formatCatalogNumber(item.armor.base);
            if (Number(item.armor.perLevel)) {
                armorSummary += " (+" + formatCatalogNumber(item.armor.perLevel) + "/quality)";
            }
            appendCodexStat(stats, "Armor", armorSummary);
        }
        card.appendChild(stats);

        var recipes = (Array.isArray(item.recipes) ? item.recipes : []).filter(function (recipe) {
            return recipe && recipe.enabled !== false;
        });
        if (recipes.length > 0) {
            var recipesSection = createCodexSection("Recipes");
            recipes.forEach(function (recipe) {
                appendCodexRecipe(recipesSection.body, recipe);
            });
            card.appendChild(recipesSection.section);
        }

        var sources = Array.isArray(item.sources) ? item.sources : [];
        if (sources.length > 0) {
            var sourceSection = createCodexSection("Obtained from");
            sources.forEach(function (source) {
                appendCodexConversion(sourceSection.body, source, "input");
            });
            card.appendChild(sourceSection.section);
        }

        var uses = Array.isArray(item.uses) ? item.uses : [];
        var reverseUses = catalogReverseRecipes.get(String(item.token || "").toLocaleLowerCase()) || [];
        if (uses.length > 0 || reverseUses.length > 0) {
            var usesSection = createCodexSection("Used in");
            uses.forEach(function (use) {
                appendCodexConversion(usesSection.body, use, "output");
            });
            if (reverseUses.length > 0) {
                var reverse = codexElement("div", "codex-reverse-uses");
                reverse.appendChild(codexElement("strong", "codex-reverse-label", "Used to craft:"));
                var visibleUses = codexShowAllReverseUses
                    ? reverseUses
                    : reverseUses.slice(0, CODEX_REVERSE_USE_LIMIT);
                visibleUses.forEach(function (consumer) {
                    reverse.appendChild(makeCodexJumpLink(
                        { prefab: consumer.token, name: consumer.name },
                        "codex-use-chip"
                    ));
                });
                if (visibleUses.length < reverseUses.length) {
                    var more = codexElement(
                        "button",
                        "codex-use-chip is-more",
                        "+" + (reverseUses.length - visibleUses.length) + " more"
                    );
                    more.type = "button";
                    more.dataset.codexMoreUses = "true";
                    reverse.appendChild(more);
                }
                usesSection.body.appendChild(reverse);
            }
            card.appendChild(usesSection.section);
        }

        var droppedBy = Array.isArray(item.droppedBy) ? item.droppedBy : [];
        if (droppedBy.length > 0) {
            var dropsSection = createCodexSection("Dropped by");
            var dropList = codexElement("div", "codex-drop-list");
            droppedBy.forEach(function (drop) {
                var label = drop && drop.name ? drop.name : (drop.creature || drop.prefab || "Unknown creature");
                var hasChance = drop && drop.chance !== undefined && drop.chance !== null;
                var chance = Number(hasChance ? drop.chance : NaN);
                if (hasChance && Number.isFinite(chance)) {
                    var percentage = chance <= 1 ? chance * 100 : chance;
                    label += " (" + formatCatalogNumber(percentage) + "%)";
                }
                dropList.appendChild(codexElement("span", "codex-drop", label));
            });
            dropsSection.body.appendChild(dropList);
            card.appendChild(dropsSection.section);
        }

        return card;
    }

    function measureCodexDetail() {
        var detail = elements.codexList.querySelector(".codex-item-detail");
        if (!detail) {
            return;
        }
        var measuredHeight = Math.ceil(detail.getBoundingClientRect().height);
        if (measuredHeight > 0 && measuredHeight !== codexExpandedHeight) {
            codexExpandedHeight = measuredHeight;
            codexWindowKey = "";
            renderCodexWindow(true);
        }
    }

    function renderCodexWindow(force) {
        if (!catalogPayload || elements.codexScroll.hidden) {
            return;
        }

        var expandedIndex = codexExpandedIndex();
        var totalHeight = codexFilteredItems.length * CODEX_ROW_HEIGHT +
            (expandedIndex >= 0 ? codexExpandedHeight : 0);
        elements.codexList.style.height = Math.max(totalHeight, 1) + "px";

        if (codexFilteredItems.length === 0) {
            var emptyKey = "empty";
            if (!force && codexWindowKey === emptyKey) {
                return;
            }
            codexWindowKey = emptyKey;
            elements.codexList.textContent = "";
            elements.codexList.appendChild(codexElement(
                "p",
                "codex-empty",
                "No items match those runes."
            ));
            return;
        }

        var viewTop = elements.codexScroll.scrollTop;
        var viewBottom = viewTop + elements.codexScroll.clientHeight;
        var start = Math.max(
            0,
            codexIndexAtOffset(viewTop, expandedIndex) - CODEX_WINDOW_OVERSCAN
        );
        var end = Math.min(
            codexFilteredItems.length,
            codexIndexAtOffset(viewBottom, expandedIndex) + CODEX_WINDOW_OVERSCAN + 1
        );
        var windowKey = [
            start,
            end,
            codexExpandedToken,
            codexExpandedHeight,
            codexShowAllReverseUses,
            codexFilteredItems.length
        ].join("|");
        if (!force && codexWindowKey === windowKey) {
            return;
        }
        codexWindowKey = windowKey;
        elements.codexList.textContent = "";

        for (var index = start; index < end; index++) {
            var top = index * CODEX_ROW_HEIGHT +
                (expandedIndex >= 0 && index > expandedIndex ? codexExpandedHeight : 0);
            var isExpanded = index === expandedIndex;
            elements.codexList.appendChild(createCodexItemRow(
                codexFilteredItems[index],
                index,
                top,
                isExpanded
            ));
            if (isExpanded) {
                elements.codexList.appendChild(createCodexDetail(
                    codexFilteredItems[index],
                    index,
                    top + CODEX_ROW_HEIGHT
                ));
            }
        }

        if (expandedIndex >= start && expandedIndex < end) {
            window.requestAnimationFrame(measureCodexDetail);
        }
    }

    function openCodexItem(item, scrollToItem, updateHash) {
        if (!item) {
            return;
        }
        var closing = codexExpandedToken === item.token && !scrollToItem;
        codexExpandedToken = closing ? "" : item.token;
        codexExpandedHeight = 0;
        codexShowAllReverseUses = false;
        codexWindowKey = "";

        if (!closing && scrollToItem) {
            var index = codexFilteredItems.findIndex(function (candidate) {
                return candidate.token === item.token;
            });
            if (index >= 0) {
                elements.codexScroll.scrollTop = index * CODEX_ROW_HEIGHT;
            }
        }
        renderCodexWindow(true);
        if (updateHash !== false) {
            writeCodexHash(codexExpandedToken);
        }
    }

    function jumpToCodexItem(tokenValue, updateHash) {
        if (!catalogPayload) {
            pendingCodexToken = tokenValue || "";
            setActiveTab("codex", false);
            loadCatalog();
            return;
        }
        var item = catalogItemsByToken.get(String(tokenValue || "").toLocaleLowerCase());
        if (!item) {
            showNoticeToast("That item is not present in this Codex");
            return;
        }

        elements.codexSearch.value = "";
        elements.codexCategory.value = "all";
        applyCodexFilters(false);
        openCodexItem(item, true, updateHash);
    }

    function handleCodexHashChange() {
        var hashToken = codexTokenFromHash(appHash());
        if (hashToken === null) {
            if (activeTab === "codex") {
                requestedTab = "map";
                saveRequestedTab("map");
                setActiveTab("map", false);
            }
            return;
        }

        requestedTab = "codex";
        saveRequestedTab("codex");
        setActiveTab("codex", false);
        if (hashToken) {
            jumpToCodexItem(hashToken, false);
        }
    }

    function bindCodexEvents() {
        addAppListener(elements.codexSearch, "input", function () {
            window.clearTimeout(codexSearchTimer);
            codexSearchTimer = window.setTimeout(function () {
                codexSearchTimer = 0;
                applyCodexFilters(true);
            }, CODEX_SEARCH_DEBOUNCE_MS);
        });
        addAppListener(elements.codexCategory, "change", function () {
            window.clearTimeout(codexSearchTimer);
            codexSearchTimer = 0;
            applyCodexFilters(true);
        });
        addAppListener(elements.codexScroll, "scroll", function () {
            renderCodexWindow(false);
        });
        addAppListener(elements.codexList, "click", function (event) {
            var jumpLink = event.target.closest("[data-codex-token]");
            if (jumpLink) {
                event.preventDefault();
                jumpToCodexItem(jumpLink.dataset.codexToken, true);
                return;
            }
            var moreUses = event.target.closest("[data-codex-more-uses]");
            if (moreUses) {
                codexShowAllReverseUses = true;
                codexWindowKey = "";
                renderCodexWindow(true);
                return;
            }
            var row = event.target.closest("[data-codex-open]");
            if (row) {
                openCodexItem(
                    catalogItemsByToken.get(row.dataset.codexOpen.toLocaleLowerCase()),
                    false,
                    true
                );
            }
        });
        addAppListener(elements.codexState, "click", function (event) {
            if (event.target.closest("[data-codex-retry]")) {
                loadCatalog();
            }
        });
        window.addEventListener("resize", function () {
            if (activeTab === "codex" && catalogPayload) {
                codexWindowKey = "";
                renderCodexWindow(true);
            }
        });
        if (!embedMode) {
            window.addEventListener("hashchange", handleCodexHashChange);
        }
    }

    async function fetchConsoleJson(path, options) {
        var requestOptions = options || {};
        requestOptions.cache = "no-store";
        requestOptions.credentials = "same-origin";
        var response = await fetch(authorizedUrl(path), requestOptions);
        var payload = null;
        try {
            payload = await response.json();
        } catch (error) {
            if (response.ok) {
                throw new Error("Invalid server response");
            }
        }

        if (!response.ok) {
            var message = payload && typeof payload.error === "string" ? payload.error : "HTTP " + response.status;
            var requestError = new Error(message);
            requestError.reason = payload && typeof payload.reason === "string" ? payload.reason : "";
            requestError.status = response.status;
            throw requestError;
        }

        return payload;
    }

    function postConsoleJson(path, body) {
        return fetchConsoleJson(path, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body || {})
        });
    }

    function formatConsoleTime(value) {
        if (typeof value === "string") {
            var timeMatch = value.match(/(?:^|T|\s)(\d{2}):(\d{2}):(\d{2})/);
            if (timeMatch) {
                return timeMatch[1] + ":" + timeMatch[2] + ":" + timeMatch[3];
            }
        }

        var numericValue = Number(value);
        var date;
        if (Number.isFinite(numericValue) && numericValue > 0) {
            date = new Date(numericValue < 100000000000 ? numericValue * 1000 : numericValue);
        } else if (typeof value === "string" && value) {
            date = new Date(value);
        } else {
            date = new Date();
        }

        if (!Number.isFinite(date.getTime())) {
            date = new Date();
        }
        return padTwo(date.getHours()) + ":" + padTwo(date.getMinutes()) + ":" + padTwo(date.getSeconds());
    }

    function formatServerConsoleText(value) {
        var text = value == null ? "" : String(value);
        text = text.replace(/^\d{2}\/\d{2}\/\d{4} \d{2}:\d{2}:\d{2}: /, "");
        text = text.replace(/^Console: /, "");
        return text.replace(/\s+$/, "");
    }

    function createConsoleLine(entry) {
        var line = document.createElement("div");
        line.className = "console-log-line";

        if (entry.kind === "server") {
            var level = typeof entry.level === "string" && entry.level.trim() ? entry.level.trim() : "Info";
            var normalizedLevel = level.toLowerCase();
            if (normalizedLevel === "warning" || normalizedLevel === "warn") {
                line.classList.add("is-warning");
            } else if (normalizedLevel === "error") {
                line.classList.add("is-error");
            }

            var timestamp = document.createElement("span");
            var levelLabel = document.createElement("span");
            var text = document.createElement("span");
            timestamp.className = "console-log-timestamp";
            timestamp.textContent = "[" + formatConsoleTime(entry.time) + "]";
            levelLabel.className = "console-log-level";
            levelLabel.textContent = "[" + level + "]";
            text.className = "console-log-text";
            text.textContent = formatServerConsoleText(entry.text);
            line.appendChild(timestamp);
            line.appendChild(document.createTextNode(" "));
            line.appendChild(levelLabel);
            line.appendChild(document.createTextNode(" "));
            line.appendChild(text);
            return line;
        }

        if (entry.kind === "live-divider") {
            line.classList.add("is-live-divider");
            line.setAttribute("role", "separator");
            line.textContent = entry.text == null ? "--- live ---" : String(entry.text);
            return line;
        }

        if (entry.kind === "help-separator") {
            line.classList.add("is-help-separator");
            line.setAttribute("role", "separator");
            return line;
        }

        if (entry.kind === "help-category") {
            line.classList.add("is-help-category");
            line.textContent = entry.text == null ? "" : String(entry.text);
            return line;
        }

        if (entry.kind === "help-command") {
            var helpUsage = document.createElement("span");
            var helpDescription = document.createElement("span");
            helpUsage.className = "console-help-usage";
            helpUsage.textContent = entry.usage || entry.name || "";
            helpDescription.className = "console-help-description";
            helpDescription.textContent = entry.description || "No description available.";
            line.classList.add("is-help-command");
            line.appendChild(helpUsage);
            line.appendChild(helpDescription);

            if (Array.isArray(entry.examples) && entry.examples.length > 0) {
                var helpExamples = document.createElement("span");
                helpExamples.className = "console-help-examples";
                helpExamples.textContent = "e.g. " + entry.examples.join(" · ");
                line.appendChild(helpExamples);
            }
            return line;
        }

        if (entry.kind === "help-hint") {
            line.classList.add("is-help-hint");
            line.textContent = entry.text == null ? "" : String(entry.text);
            return line;
        }

        var content = document.createElement("span");
        content.className = "console-log-text";
        content.textContent = entry.text == null ? "" : String(entry.text);
        if (entry.history === true) {
            line.classList.add("is-history");
            if (entry.time != null) {
                var historyTimestamp = document.createElement("span");
                historyTimestamp.className = "console-log-timestamp";
                historyTimestamp.textContent = "[" + formatConsoleTime(entry.time) + "]";
                line.appendChild(historyTimestamp);
                line.appendChild(document.createTextNode(" "));
            }
            if (entry.historyDetail === true) {
                line.classList.add("is-history-detail");
            }
        }
        if (entry.kind === "command") {
            line.classList.add("is-command");
        } else if (entry.kind === "error") {
            line.classList.add("is-error", "is-inline");
        } else {
            line.classList.add("is-output");
        }
        line.appendChild(content);
        return line;
    }

    function appendConsoleEntries(entries) {
        if (!entries || entries.length === 0) {
            return;
        }

        var shouldFollow = consoleFollowLog;
        var oldScrollTop = elements.consoleLog.scrollTop;
        var fragment = document.createDocumentFragment();
        entries.forEach(function (entry) {
            fragment.appendChild(createConsoleLine(entry));
        });
        elements.consoleLog.appendChild(fragment);

        var removedHeight = 0;
        while (elements.consoleLog.childNodes.length > CONSOLE_LOG_LIMIT) {
            removedHeight += elements.consoleLog.firstChild.offsetHeight || 0;
            elements.consoleLog.removeChild(elements.consoleLog.firstChild);
        }

        if (shouldFollow) {
            elements.consoleLog.scrollTop = elements.consoleLog.scrollHeight;
            elements.consoleResume.hidden = true;
        } else {
            elements.consoleLog.scrollTop = Math.max(0, oldScrollTop - removedHeight);
            elements.consoleResume.hidden = false;
        }
    }

    function appendConsoleError(message) {
        appendConsoleEntries([{ kind: "error", text: "! " + message }]);
    }

    function reportConsoleFailure(feed, label, error) {
        var detail = error && error.message ? error.message : "Request failed";
        var message = label + " failed (" + detail + ")";
        if (consoleFailures[feed] === message) {
            return;
        }

        consoleFailures[feed] = message;
        appendConsoleError(message);
    }

    function clearConsoleFailure(feed) {
        delete consoleFailures[feed];
    }

    function closeSuggestions() {
        consoleSuggestions = [];
        consoleSuggestionIndex = -1;
        elements.suggestionList.textContent = "";
        elements.suggestionList.hidden = true;
        elements.commandInput.setAttribute("aria-expanded", "false");
        elements.commandInput.removeAttribute("aria-activedescendant");
    }

    function normalizeConsoleCategory(value) {
        var category = typeof value === "string" ? value.trim().toLowerCase() : "";
        return category || "server";
    }

    function consoleCategoryLabel(category) {
        if (CONSOLE_CATEGORY_LABELS[category]) {
            return CONSOLE_CATEGORY_LABELS[category];
        }
        return category ? category.charAt(0).toUpperCase() + category.slice(1) : "Server";
    }

    function orderedConsoleCategoryKeys(categories) {
        var keys = [];
        CONSOLE_CATEGORY_ORDER.forEach(function (category) {
            if (categories[category]) {
                keys.push(category);
            }
        });
        Object.keys(categories).filter(function (category) {
            return CONSOLE_CATEGORY_ORDER.indexOf(category) === -1;
        }).sort(function (left, right) {
            return consoleCategoryLabel(left).localeCompare(consoleCategoryLabel(right));
        }).forEach(function (category) {
            keys.push(category);
        });
        return keys;
    }

    function groupConsoleCommands(commands) {
        var categories = Object.create(null);
        commands.forEach(function (command) {
            var category = normalizeConsoleCategory(command.category);
            if (!categories[category]) {
                categories[category] = [];
            }
            categories[category].push(command);
        });
        Object.keys(categories).forEach(function (category) {
            categories[category].sort(function (left, right) {
                return left.name.toLowerCase().localeCompare(right.name.toLowerCase());
            });
        });
        return categories;
    }

    function showSuggestionList() {
        elements.suggestionList.hidden = false;
        elements.commandInput.setAttribute("aria-expanded", "true");
    }

    function setConsoleSuggestionIndex(index) {
        if (consoleSuggestions.length === 0) {
            consoleSuggestionIndex = -1;
            elements.commandInput.removeAttribute("aria-activedescendant");
            return;
        }

        consoleSuggestionIndex = index;
        var options = elements.suggestionList.querySelectorAll("[data-suggestion-index]");
        Array.prototype.forEach.call(options, function (option) {
            var isSelected = Number(option.getAttribute("data-suggestion-index")) === index;
            option.classList.toggle("is-selected", isSelected);
            option.setAttribute("aria-selected", String(isSelected));
            if (isSelected) {
                elements.commandInput.setAttribute("aria-activedescendant", option.id);
                option.scrollIntoView({ block: "nearest" });
            }
        });
    }

    function moveConsoleSuggestionSelection(direction) {
        if (consoleSuggestions.length === 0) {
            return false;
        }

        var nextIndex;
        if (consoleSuggestionIndex < 0) {
            nextIndex = direction > 0 ? 0 : consoleSuggestions.length - 1;
        } else {
            nextIndex = (consoleSuggestionIndex + direction + consoleSuggestions.length) %
                consoleSuggestions.length;
        }
        setConsoleSuggestionIndex(nextIndex);
        return true;
    }

    function appendSuggestionOption(group, suggestion, index) {
        var option = document.createElement("button");
        var heading = document.createElement("span");
        var identity = document.createElement("span");
        var name = document.createElement("span");
        var tags = document.createElement("span");
        option.type = "button";
        option.tabIndex = -1;
        option.id = "console-suggestion-" + index;
        option.className = "console-suggestion";
        option.setAttribute("role", "option");
        option.setAttribute("aria-selected", "false");
        option.setAttribute("data-suggestion-index", String(index));
        heading.className = "console-suggestion-heading";
        identity.className = "console-suggestion-identity";
        name.className = "console-suggestion-name";
        tags.className = "console-suggestion-tags";

        if (suggestion.kind === "player") {
            name.textContent = suggestion.playerName;
            identity.appendChild(name);
            var onlineTag = document.createElement("span");
            onlineTag.className = "console-category-tag";
            onlineTag.textContent = "Online";
            tags.appendChild(onlineTag);
            heading.appendChild(identity);
            heading.appendChild(tags);
            option.appendChild(heading);
        } else if (suggestion.kind === "item") {
            name.textContent = suggestion.itemName;
            identity.appendChild(name);
            var token = document.createElement("span");
            token.className = "console-suggestion-usage";
            token.textContent = suggestion.itemToken;
            identity.appendChild(token);
            var itemTag = document.createElement("span");
            itemTag.className = "console-category-tag";
            itemTag.textContent = "Item";
            tags.appendChild(itemTag);
            heading.appendChild(identity);
            heading.appendChild(tags);
            option.appendChild(heading);
        } else {
            var command = suggestion.command;
            name.textContent = command.name;
            identity.appendChild(name);
            if (command.usage && command.usage.toLowerCase() !== command.name.toLowerCase()) {
                var usage = document.createElement("span");
                usage.className = "console-suggestion-usage";
                usage.textContent = command.usage;
                identity.appendChild(usage);
            }
            if (command.cheat) {
                var badge = document.createElement("span");
                badge.className = "console-cheat-badge";
                badge.textContent = "Cheat";
                tags.appendChild(badge);
            }
            var categoryTag = document.createElement("span");
            categoryTag.className = "console-category-tag";
            categoryTag.textContent = consoleCategoryLabel(normalizeConsoleCategory(command.category));
            tags.appendChild(categoryTag);
            heading.appendChild(identity);
            heading.appendChild(tags);
            option.appendChild(heading);

            if (command.description) {
                var description = document.createElement("span");
                description.className = "console-suggestion-description";
                description.textContent = command.description;
                option.appendChild(description);
            }
        }

        addAppListener(option, "mouseenter", function () {
            setConsoleSuggestionIndex(index);
        });
        addAppListener(option, "mousedown", function (event) {
            event.preventDefault();
            completeSuggestion(index);
        });
        group.appendChild(option);
    }

    function appendSuggestionGroup(category, suggestions) {
        var group = document.createElement("div");
        var header = document.createElement("div");
        var groupId = "console-suggestion-group-" + category.replace(/[^a-z0-9_-]/g, "-");
        group.className = "console-suggestion-group";
        group.setAttribute("role", "group");
        group.setAttribute("aria-labelledby", groupId);
        header.id = groupId;
        header.className = "console-suggestion-category";
        header.textContent = consoleCategoryLabel(category);
        group.appendChild(header);
        suggestions.forEach(function (suggestion) {
            var index = consoleSuggestions.indexOf(suggestion);
            appendSuggestionOption(group, suggestion, index);
        });
        elements.suggestionList.appendChild(group);
    }

    function renderPlayerSuggestions(context) {
        var query = context.query.toLowerCase();
        var seen = Object.create(null);
        var names = latestPlayers.map(function (player) {
            return typeof player.name === "string" ? player.name.trim() : "";
        }).filter(function (name) {
            var key = name.toLowerCase();
            if (!name || seen[key]) {
                return false;
            }
            seen[key] = true;
            return true;
        }).map(function (name) {
            var lowerName = name.toLowerCase();
            return {
                kind: "player",
                command: context.command,
                playerName: name,
                rank: lowerName.indexOf(query) === 0 ? 0 : 1
            };
        }).filter(function (suggestion) {
            return !query || suggestion.playerName.toLowerCase().indexOf(query) !== -1;
        }).sort(function (left, right) {
            return left.rank - right.rank || left.playerName.localeCompare(right.playerName);
        }).slice(0, 10);

        if (names.length === 0) {
            var hintGroup = document.createElement("div");
            var hintHeader = document.createElement("div");
            var hint = document.createElement("div");
            hintGroup.className = "console-suggestion-group";
            hintGroup.setAttribute("role", "group");
            hintGroup.setAttribute("aria-labelledby", "console-suggestion-player-group");
            hintHeader.id = "console-suggestion-player-group";
            hintHeader.className = "console-suggestion-category";
            hintHeader.textContent = "Players";
            hint.className = "console-suggestion console-suggestion-hint";
            hint.setAttribute("role", "option");
            hint.setAttribute("aria-selected", "false");
            hint.setAttribute("aria-disabled", "true");
            hint.textContent = latestPlayers.length === 0 ? "No players online" : "No matching players";
            hintGroup.appendChild(hintHeader);
            hintGroup.appendChild(hint);
            elements.suggestionList.appendChild(hintGroup);
            showSuggestionList();
            return;
        }

        consoleSuggestions = names;
        appendSuggestionGroup("players", names);
        showSuggestionList();
    }

    function findPlayerSuggestionContext(input) {
        var lowerInput = input.toLowerCase();
        var matches = consoleCommands.filter(function (command) {
            var commandName = command.name.toLowerCase();
            return command.playerArg && lowerInput.indexOf(commandName) === 0 &&
                input.length > command.name.length && /\s/.test(input.charAt(command.name.length));
        }).sort(function (left, right) {
            return right.name.length - left.name.length;
        });
        if (matches.length === 0) {
            return null;
        }

        var command = matches[0];
        var query = input.slice(command.name.length).replace(/^\s+/, "");
        var lowerQuery = query.toLowerCase();
        var completedPlayer = latestPlayers.some(function (player) {
            var playerName = typeof player.name === "string" ? player.name.toLowerCase() : "";
            return playerName && lowerQuery.indexOf(playerName) === 0 &&
                query.length > playerName.length && /\s/.test(query.charAt(playerName.length));
        });
        if (completedPlayer) {
            return null;
        }
        return { command: command, query: query };
    }

    function findItemSuggestionContext(input) {
        var lowerInput = input.toLowerCase();
        var matches = consoleCommands.filter(function (command) {
            var commandName = command.name.toLowerCase();
            return command.itemArg && lowerInput.indexOf(commandName) === 0 &&
                input.length > command.name.length && /\s/.test(input.charAt(command.name.length));
        }).sort(function (left, right) {
            return right.name.length - left.name.length;
        });
        if (matches.length === 0) {
            return null;
        }

        var command = matches[0];
        return {
            command: command,
            query: input.slice(command.name.length).replace(/^\s+/, "")
        };
    }

    function renderItemSuggestionHint(text) {
        var hintGroup = document.createElement("div");
        var hintHeader = document.createElement("div");
        var hint = document.createElement("div");
        hintGroup.className = "console-suggestion-group";
        hintGroup.setAttribute("role", "group");
        hintGroup.setAttribute("aria-labelledby", "console-suggestion-item-group");
        hintHeader.id = "console-suggestion-item-group";
        hintHeader.className = "console-suggestion-category";
        hintHeader.textContent = "Items";
        hint.className = "console-suggestion console-suggestion-hint";
        hint.setAttribute("role", "option");
        hint.setAttribute("aria-selected", "false");
        hint.setAttribute("aria-disabled", "true");
        hint.textContent = text;
        hintGroup.appendChild(hintHeader);
        hintGroup.appendChild(hint);
        elements.suggestionList.appendChild(hintGroup);
        showSuggestionList();
    }

    function renderItemSuggestions(context) {
        if (!catalogPayload) {
            renderItemSuggestionHint("Loading item catalog…");
            var requestedInput = elements.commandInput.value;
            loadCatalog().then(function () {
                if (elements.commandInput.value === requestedInput) {
                    renderCommandSuggestions();
                }
            }).catch(function () {
                if (elements.commandInput.value === requestedInput) {
                    closeSuggestions();
                    renderItemSuggestionHint("Item catalog unavailable");
                }
            });
            return;
        }

        var query = context.query.toLocaleLowerCase();
        var matches = catalogItems.map(function (item) {
            var name = String(item.name || item.token || "").trim();
            var token = String(item.token || "").trim();
            var lowerName = name.toLocaleLowerCase();
            var lowerToken = token.toLocaleLowerCase();
            var nameIndex = lowerName.indexOf(query);
            var tokenIndex = lowerToken.indexOf(query);
            return {
                kind: "item",
                command: context.command,
                itemName: name,
                itemToken: token,
                rank: nameIndex === 0 || tokenIndex === 0 ? 0 : 1,
                matches: !query || nameIndex !== -1 || tokenIndex !== -1
            };
        }).filter(function (suggestion) {
            return suggestion.itemName && suggestion.itemToken && suggestion.matches;
        }).sort(function (left, right) {
            return left.rank - right.rank || left.itemName.localeCompare(right.itemName, "en", {
                sensitivity: "base"
            }) || left.itemToken.localeCompare(right.itemToken);
        }).slice(0, 10);

        if (matches.length === 0) {
            renderItemSuggestionHint("No matching items");
            return;
        }

        consoleSuggestions = matches;
        appendSuggestionGroup("items", matches);
        showSuggestionList();
        setConsoleSuggestionIndex(0);
    }

    function commandSuggestionQuery(input) {
        if (!input) {
            return "";
        }
        if (!/\s/.test(input) || /^vo(?:\s+[^\s]*)?$/i.test(input)) {
            return input;
        }
        return "";
    }

    function findCommandSuggestions(query) {
        var lowerQuery = query.toLowerCase();
        var bareQuery = lowerQuery.replace(/^vo\s+/, "");
        return consoleCommands.map(function (command) {
            var lowerName = command.name.toLowerCase();
            var bareName = lowerName.replace(/^vo\s+/, "");
            var lowerDescription = command.description.toLowerCase();
            var rank = lowerName.indexOf(lowerQuery) === 0 ||
                bareName.indexOf(bareQuery) === 0 ? 0 :
                (lowerName.indexOf(lowerQuery) !== -1 ||
                    bareName.indexOf(bareQuery) !== -1 ? 1 :
                    (lowerDescription.indexOf(lowerQuery) !== -1 ? 2 : -1));
            return { kind: "command", command: command, rank: rank };
        }).filter(function (suggestion) {
            return suggestion.rank !== -1;
        }).sort(function (left, right) {
            return left.rank - right.rank ||
                left.command.name.toLowerCase().localeCompare(right.command.name.toLowerCase());
        }).slice(0, 10);
    }

    function renderCommandSuggestions() {
        closeSuggestions();
        if (!consoleMetaLoaded || consoleSuggestionClosed) {
            return;
        }

        var input = elements.commandInput.value.replace(/^\s+/, "");
        var playerContext = findPlayerSuggestionContext(input);
        if (playerContext) {
            renderPlayerSuggestions(playerContext);
            return;
        }

        var itemContext = findItemSuggestionContext(input);
        if (itemContext) {
            renderItemSuggestions(itemContext);
            return;
        }

        var query = commandSuggestionQuery(input);
        if (!query) {
            return;
        }

        var matches = findCommandSuggestions(query);
        if (matches.length === 0) {
            return;
        }

        consoleSuggestions = matches;
        var categories = Object.create(null);
        matches.forEach(function (suggestion) {
            var category = normalizeConsoleCategory(suggestion.command.category);
            if (!categories[category]) {
                categories[category] = [];
            }
            categories[category].push(suggestion);
        });
        orderedConsoleCategoryKeys(categories).forEach(function (category) {
            appendSuggestionGroup(category, categories[category]);
        });
        showSuggestionList();
    }

    function completeSuggestion(index) {
        var suggestion = consoleSuggestions[index];
        if (!suggestion) {
            return;
        }

        elements.commandInput.value = suggestion.kind === "player"
            ? suggestion.command.name + " " + suggestion.playerName + " "
            : (suggestion.kind === "item"
                ? suggestion.command.name + " " + suggestion.itemName + " "
                : suggestion.command.name + " ");
        closeSuggestions();
        elements.commandInput.focus();
    }

    function normalizeConsoleCommands(payload) {
        var byName = Object.create(null);
        var commands = [];
        var whitelist = payload && Array.isArray(payload.whitelist) ? payload.whitelist : [];
        var metadata = payload && Array.isArray(payload.commands) ? payload.commands : [];

        whitelist.forEach(function (entry) {
            var rawName = typeof entry === "string" ? entry : entry && entry.name;
            var name = typeof rawName === "string" ? rawName.trim() : "";
            if (!name || byName[name.toLowerCase()]) {
                return;
            }

            var command = {
                name: name,
                description: entry && typeof entry.description === "string" ? entry.description.trim() : "",
                cheat: false,
                usage: name,
                category: "server",
                examples: [],
                playerArg: false,
                itemArg: false,
                whitelisted: true
            };
            byName[name.toLowerCase()] = command;
            commands.push(command);
        });

        metadata.forEach(function (entry) {
            var rawName = typeof entry === "string" ? entry : entry && entry.name;
            var name = typeof rawName === "string" ? rawName.trim() : "";
            if (!name) {
                return;
            }

            var key = name.toLowerCase();
            var command = byName[key];
            if (!command) {
                command = {
                    name: name,
                    description: "",
                    cheat: false,
                    usage: name,
                    category: "server",
                    examples: [],
                    playerArg: false,
                    itemArg: false,
                    whitelisted: false
                };
                byName[key] = command;
                commands.push(command);
            }
            command.description = entry && typeof entry.description === "string" ? entry.description.trim() : "";
            command.cheat = entry && entry.cheat === true;
            command.usage = entry && typeof entry.usage === "string" && entry.usage.trim()
                ? entry.usage.trim()
                : name;
            command.category = normalizeConsoleCategory(entry && entry.category);
            command.examples = entry && Array.isArray(entry.examples) ? entry.examples.map(function (example) {
                return typeof example === "string" ? example.trim() : "";
            }).filter(function (example) {
                return Boolean(example);
            }) : [];
            command.playerArg = entry && entry.playerArg === true;
            command.itemArg = entry && entry.itemArg === true;
        });

        commands.sort(function (left, right) {
            if (left.whitelisted !== right.whitelisted) {
                return left.whitelisted ? -1 : 1;
            }
            return left.name.toLowerCase().localeCompare(right.name.toLowerCase());
        });
        return commands;
    }

    async function loadConsoleMeta() {
        if (!consoleIsActive()) {
            return consoleCommands;
        }
        if (consoleMetaLoaded) {
            return consoleCommands;
        }
        if (consoleMetaPromise) {
            return consoleMetaPromise;
        }

        consoleMetaRequestPending = true;
        consoleMetaPromise = (async function () {
            try {
                var payload = await fetchConsoleJson("/api/console/meta");
                consoleCommands = normalizeConsoleCommands(payload);
                consoleMetaLoaded = true;
                clearConsoleFailure("meta");
                renderCommandSuggestions();
                if (!elements.commandReference.hidden) {
                    buildCommandReference();
                }
            } catch (error) {
                reportConsoleFailure("meta", "Command metadata", error);
            } finally {
                consoleMetaRequestPending = false;
                consoleMetaPromise = null;
            }
            return consoleCommands;
        }());
        return consoleMetaPromise;
    }

    function buildCommandReference() {
        elements.commandReferenceBody.textContent = "";
        if (consoleCommands.length === 0) {
            var empty = document.createElement("p");
            empty.className = "console-reference-empty";
            empty.textContent = consoleMetaRequestPending
                ? "Loading command metadata…"
                : "No command metadata available.";
            elements.commandReferenceBody.appendChild(empty);
            return;
        }

        var categories = groupConsoleCommands(consoleCommands);
        orderedConsoleCategoryKeys(categories).forEach(function (category) {
            var section = document.createElement("section");
            var heading = document.createElement("h3");
            section.className = "console-reference-category";
            heading.textContent = consoleCategoryLabel(category);
            section.appendChild(heading);
            categories[category].forEach(function (command) {
                var row = document.createElement("div");
                var usage = document.createElement("span");
                var description = document.createElement("span");
                row.className = "console-reference-row";
                usage.className = "console-reference-usage";
                usage.textContent = command.usage || command.name;
                description.className = "console-reference-description";
                description.textContent = command.description || "No description available.";
                row.appendChild(usage);
                row.appendChild(description);
                section.appendChild(row);
            });
            elements.commandReferenceBody.appendChild(section);
        });
    }

    function setCommandReferenceOpen(isOpen) {
        elements.commandReference.hidden = !isOpen;
        elements.commandsToggle.setAttribute("aria-expanded", String(isOpen));
        elements.commandsToggle.classList.toggle("is-active", isOpen);
    }

    async function toggleCommandReference() {
        var isOpening = elements.commandReference.hidden;
        setCommandReferenceOpen(isOpening);
        if (!isOpening) {
            return;
        }

        buildCommandReference();
        await loadConsoleMeta();
        if (!elements.commandReference.hidden) {
            buildCommandReference();
        }
    }

    function renderConsoleHelp() {
        if (consoleCommands.length === 0) {
            appendConsoleEntries([{
                kind: "output",
                text: "No command metadata available. Try again in a moment."
            }]);
            return;
        }

        var entries = [{ kind: "help-separator" }];
        var categories = groupConsoleCommands(consoleCommands);
        orderedConsoleCategoryKeys(categories).forEach(function (category) {
            entries.push({
                kind: "help-category",
                text: consoleCategoryLabel(category)
            });
            categories[category].forEach(function (command) {
                entries.push({
                    kind: "help-command",
                    name: command.name,
                    usage: command.usage,
                    description: command.description,
                    examples: command.examples
                });
            });
        });
        entries.push({ kind: "help-separator" });
        entries.push({
            kind: "help-hint",
            text: "click a command above the input while typing to autocomplete"
        });
        appendConsoleEntries(entries);
    }

    function handleConsoleLogPayload(payload, preserveNewerCursor) {
        if (!consoleHistoryLoaded) {
            pendingConsoleLogPayloads.push({
                payload: payload,
                preserveNewerCursor: preserveNewerCursor
            });
            if (pendingConsoleLogPayloads.length > 20) {
                pendingConsoleLogPayloads.shift();
            }
            return;
        }

        var previousCursor = consoleCursor;
        var nextCursor = payload ? Number(payload.cursor) : NaN;
        var cursorReset = Number.isFinite(nextCursor) && nextCursor < previousCursor &&
            !preserveNewerCursor;
        var minimumSequence = cursorReset ? 0 : previousCursor;
        var lines = payload && Array.isArray(payload.lines) ? payload.lines : [];
        appendConsoleEntries(lines.filter(function (line) {
            var sequence = line ? Number(line.seq) : NaN;
            return !Number.isFinite(sequence) || sequence > minimumSequence;
        }).map(function (line) {
            return {
                kind: "server",
                time: line && line.time,
                level: line && line.level,
                text: line && line.text
            };
        }));

        if (Number.isFinite(nextCursor) &&
            !(preserveNewerCursor && nextCursor < previousCursor)) {
            consoleCursor = Math.max(0, Math.floor(nextCursor));
        }
        clearConsoleFailure("log");
    }

    function renderConsoleHistory(payload) {
        var allEntries = payload && Array.isArray(payload.entries) ? payload.entries : [];
        var entries = allEntries.slice(-CONSOLE_HISTORY_REPLAY_LIMIT);
        var recalled = [];
        entries.forEach(function (entry) {
            var operatorName = entry && typeof entry.operator === "string" && entry.operator.trim()
                ? entry.operator.trim()
                : "unknown";
            var command = entry && typeof entry.command === "string" ? entry.command : "";
            if (!command) {
                return;
            }

            recalled.push({
                kind: "command",
                history: true,
                time: entry && entry.t,
                text: "[" + operatorName + "] " + command
            });
            var output = entry && typeof entry.output === "string" ? entry.output : "";
            if (output) {
                recalled.push({
                    kind: entry.status === "error" ? "error" : "output",
                    history: true,
                    historyDetail: true,
                    text: entry.status === "error" ? "! " + output : output
                });
            }
        });
        appendConsoleEntries(recalled);

        commandHistory = allEntries.map(function (entry) {
            return entry && typeof entry.command === "string" ? entry.command : "";
        }).filter(function (command) {
            return Boolean(command);
        }).slice(-COMMAND_HISTORY_LIMIT);
        commandHistoryIndex = commandHistory.length;
    }

    async function loadConsoleHistory() {
        if (consoleHistoryLoaded) {
            return;
        }
        if (consoleHistoryPromise) {
            return consoleHistoryPromise;
        }

        consoleHistoryPromise = (async function () {
            try {
                var payload = await fetchConsoleJson("/api/console/history?cursor=0&max=200");
                renderConsoleHistory(payload);
                clearConsoleFailure("history");
            } catch (error) {
                reportConsoleFailure("history", "Console history", error);
            } finally {
                appendConsoleEntries([{ kind: "live-divider", text: "--- live ---" }]);
                consoleFollowLog = true;
                elements.consoleLog.scrollTop = elements.consoleLog.scrollHeight;
                elements.consoleResume.hidden = true;
                consoleHistoryLoaded = true;
                consoleHistoryPromise = null;
                var pending = pendingConsoleLogPayloads;
                pendingConsoleLogPayloads = [];
                pending.forEach(function (record) {
                    handleConsoleLogPayload(record.payload, record.preserveNewerCursor);
                });
            }
        }());
        return consoleHistoryPromise;
    }

    async function pollConsoleLog() {
        if (!consoleIsActive() || !consoleHistoryLoaded || consoleLogRequestPending ||
            document.hidden || pollCircuitOpen ||
            (eventSourceOpen && eventSourceLogFlowing)) {
            return;
        }

        consoleLogRequestPending = true;
        var logStreamWasFlowing = eventSourceLogFlowing;
        try {
            var payload = await fetchConsoleJson(
                "/api/console/log?cursor=" + encodeURIComponent(consoleCursor) + "&max=250"
            );
            if (!logStreamWasFlowing && eventSourceLogFlowing) {
                recordPollSuccess("console-log");
                return;
            }
            handleConsoleLogPayload(payload);
            recordPollSuccess("console-log");
        } catch (error) {
            recordPollFailure("console-log");
            reportConsoleFailure("log", "Console log", error);
        } finally {
            consoleLogRequestPending = false;
        }
    }

    function formatUptime(value) {
        var totalMinutes = Math.max(0, Math.floor(Number(value) / 60));
        if (!Number.isFinite(totalMinutes)) {
            return "—";
        }

        var days = Math.floor(totalMinutes / 1440);
        var hours = Math.floor((totalMinutes % 1440) / 60);
        var minutes = totalMinutes % 60;
        var parts = [];
        if (days) {
            parts.push(days + "d");
        }
        if (hours || days) {
            parts.push(hours + "h");
        }
        parts.push(minutes + "m");
        return parts.join(" ");
    }

    function formatInteger(value) {
        var number = Number(value);
        return Number.isFinite(number) ? Math.max(0, Math.floor(number)).toLocaleString("en-US") : "—";
    }

    function formatDecimal(value, suffix) {
        var number = Number(value);
        return Number.isFinite(number) ? number.toFixed(1) + suffix : "—";
    }

    function formatAbbreviatedInteger(value) {
        var number = Math.max(0, Math.floor(Number(value)));
        if (!Number.isFinite(number)) {
            return "—";
        }
        if (number >= 1000000) {
            return (number / 1000000).toFixed(number < 10000000 ? 1 : 0)
                .replace(/\.0$/, "") + "M";
        }
        if (number >= 1000) {
            return (number / 1000).toFixed(number < 100000 ? 1 : 0)
                .replace(/\.0$/, "") + "k";
        }
        return String(number);
    }

    function updateMapMetricsFromStatus(status) {
        var day = Number(status && status.day);
        elements.metricDayItem.hidden = !Number.isFinite(day);
        elements.metricDay.textContent = Number.isFinite(day)
            ? formatInteger(day)
            : "—";

        var uptime = Number(status && status.uptimeSeconds);
        elements.metricUptimeItem.hidden = !Number.isFinite(uptime);
        elements.metricUptime.textContent = Number.isFinite(uptime)
            ? formatUptime(uptime)
            : "—";
    }

    function updateMapMetricsFromStats(payload) {
        elements.metricFrame.textContent = formatDecimal(payload && payload.frameAvgMs, " ms");
        elements.metricZdo.textContent = formatAbbreviatedInteger(payload && payload.zdoCount);
        updateMapMetricsFromStatus(payload);
    }

    function updateMapMetricStatus() {
        var state = feedStaleness("status").state;
        var label = state === "green"
            ? "Server online"
            : state === "red" ? "Server offline" : "Server status loading";
        elements.metricStatus.classList.toggle("is-online", state === "green");
        elements.metricStatus.classList.toggle("is-offline", state === "red");
        elements.metricStatus.classList.toggle("is-waiting", state !== "green" && state !== "red");
        elements.metricStatus.setAttribute("aria-label", label);
        elements.metricStatus.title = label;
    }

    async function pollStats(reportFailure) {
        if (!consoleAvailable || statsRequestPending || document.hidden || pollCircuitOpen) {
            return;
        }

        statsRequestPending = true;
        try {
            var payload = await fetchConsoleJson("/api/stats");
            recordPollSuccess("stats");
            updateMapMetricsFromStats(payload);
            elements.statUptime.textContent = formatUptime(payload && payload.uptimeSeconds);
            elements.statPlayers.textContent = formatInteger(payload && payload.players);
            elements.statZdo.textContent = formatInteger(payload && payload.zdoCount);
            elements.statHeap.textContent = formatDecimal(
                Number(payload && payload.monoHeapBytes) / (1024 * 1024),
                " MB"
            );
            elements.statFrameAvg.textContent = formatDecimal(payload && payload.frameAvgMs, " ms");
            elements.statFrameMax.textContent = formatDecimal(payload && payload.frameMaxMs, " ms");
            clearConsoleFailure("stats");
        } catch (error) {
            recordPollFailure("stats");
            if (reportFailure) {
                reportConsoleFailure("stats", "Server stats", error);
            }
        } finally {
            statsRequestPending = false;
        }
    }

    async function pollConsoleStats() {
        if (!consoleIsActive()) {
            return;
        }
        await pollStats(true);
    }

    async function pollMapStats() {
        if (activeTab !== "map") {
            return;
        }
        await pollStats(false);
    }

    function scheduleStatsPolling(delay) {
        if (!statsPollingStarted || pollCircuitOpen) {
            return;
        }
        window.clearTimeout(statsPollTimer);
        statsPollTimer = window.setTimeout(runStatsPolling, delay);
    }

    async function runStatsPolling() {
        statsPollTimer = 0;
        if (consoleIsActive()) {
            await pollConsoleStats();
        } else {
            await pollMapStats();
        }
        scheduleStatsPolling(consoleIsActive()
            ? CONSOLE_STATS_POLL_INTERVAL_MS
            : MAP_STATS_POLL_INTERVAL_MS);
    }

    function startStatsPolling() {
        if (statsPollingStarted) {
            return;
        }
        statsPollingStarted = true;
        scheduleStatsPolling(0);
    }

    function normalizeBannedPlayers(payload) {
        var banned = payload && Array.isArray(payload.banned) ? payload.banned : [];
        return banned.map(function (entry) {
            if (typeof entry === "string") {
                return entry.trim();
            }
            if (entry && typeof entry.player === "string") {
                return entry.player.trim();
            }
            if (entry && typeof entry.name === "string") {
                return entry.name.trim();
            }
            return "";
        }).filter(function (name) {
            return Boolean(name);
        });
    }

    function createActionButton(label, className, callback, disabled) {
        var button = document.createElement("button");
        button.type = "button";
        button.className = "console-list-action" + (className ? " " + className : "");
        button.textContent = label;
        button.disabled = disabled === true;
        addAppListener(button, "click", callback);
        return button;
    }

    function renderBannedPlayers(players) {
        elements.bannedList.textContent = "";
        elements.bannedCount.textContent = players.length + " banned";
        if (players.length === 0) {
            var empty = document.createElement("li");
            empty.className = "console-empty-list";
            empty.textContent = "No banned players";
            elements.bannedList.appendChild(empty);
            return;
        }

        players.forEach(function (player) {
            var item = document.createElement("li");
            var name = document.createElement("span");
            var actions = document.createElement("span");
            item.className = "console-admin-list-row";
            name.className = "console-admin-name";
            name.textContent = player;
            actions.className = "console-admin-actions";
            actions.appendChild(createActionButton("Unban", "", function () {
                openConfirmDialog("unban", player);
            }));
            item.appendChild(name);
            item.appendChild(actions);
            elements.bannedList.appendChild(item);
        });
    }

    async function loadBanList(forceRefresh) {
        if (!consoleIsActive()) {
            return;
        }
        if (consoleBanRequestPending) {
            consoleBanRefreshQueued = consoleBanRefreshQueued || forceRefresh === true;
            return;
        }

        consoleBanRequestPending = true;
        try {
            var payload = await fetchConsoleJson("/api/admin/banlist");
            if (!payload || !Array.isArray(payload.banned)) {
                throw new Error(payload && payload.error ? payload.error : "Invalid server response");
            }
            renderBannedPlayers(normalizeBannedPlayers(payload));
            clearConsoleFailure("banlist");
        } catch (error) {
            reportConsoleFailure("banlist", "Ban list", error);
        } finally {
            consoleBanRequestPending = false;
            if (consoleBanRefreshQueued) {
                consoleBanRefreshQueued = false;
                loadBanList(false);
            }
        }
    }

    function renderConsolePlayers() {
        elements.consolePlayerList.textContent = "";
        elements.consolePlayerCount.textContent = latestPlayers.length + " online";
        if (latestPlayers.length === 0) {
            var empty = document.createElement("li");
            empty.className = "console-empty-list";
            empty.textContent = "No vikings ashore";
            elements.consolePlayerList.appendChild(empty);
            return;
        }

        latestPlayers.forEach(function (player) {
            var item = document.createElement("li");
            var name = document.createElement("span");
            var actions = document.createElement("span");
            var cannotManage = !player.name;
            item.className = "console-admin-list-row";
            name.className = "console-admin-name";
            name.textContent = player.displayName;
            actions.className = "console-admin-actions";
            actions.appendChild(createActionButton("Kick", "", function () {
                openConfirmDialog("kick", player.name, player.id);
            }, cannotManage));
            actions.appendChild(createActionButton("Ban", "is-danger", function () {
                openConfirmDialog("ban", player.name, player.id);
            }, cannotManage));
            item.appendChild(name);
            item.appendChild(actions);
            elements.consolePlayerList.appendChild(item);
        });
    }

    function openConfirmDialog(action, player, playerId) {
        if (!player) {
            return;
        }

        confirmAction = { action: action, player: player };
        if (action === "kick") {
            elements.confirmMessage.textContent = "Kick " + player + "? The player can rejoin.";
        } else if (action === "ban") {
            var undoTarget = playerId == null || String(playerId).trim() === ""
                ? "<id>"
                : String(playerId).trim();
            elements.confirmMessage.textContent = "Ban " + player +
                "? Banned players can be restored with vo unban " + undoTarget + ".";
        } else {
            elements.confirmMessage.textContent = action.charAt(0).toUpperCase() +
                action.slice(1) + " " + player + "?";
        }
        elements.confirmSubmit.textContent = "Confirm";
        elements.confirmSubmit.classList.add("is-danger");
        elements.confirmBackdrop.hidden = false;
        elements.confirmCancel.focus();
    }

    function shutdownCommandDetails(command) {
        var match = /^vo\s+shutdown\s+([+-]?\d+)(?:\s+.*)?$/i.exec(command);
        if (!match) {
            return null;
        }

        var requestedSeconds = Number(match[1]);
        if (!Number.isSafeInteger(requestedSeconds) ||
            requestedSeconds < -2147483648 || requestedSeconds > 2147483647) {
            return null;
        }

        return {
            seconds: Math.max(5, Math.min(3600, requestedSeconds))
        };
    }

    function openShutdownConfirmDialog(command, details) {
        confirmAction = { action: "console-command", command: command };
        elements.confirmMessage.textContent = "Shut down the server in " + details.seconds +
            "s? Everyone will disconnect after the world saves. Cancel with " +
            "vo shutdown cancel.";
        elements.confirmSubmit.textContent = "Confirm";
        elements.confirmSubmit.classList.add("is-danger");
        elements.confirmBackdrop.hidden = false;
        elements.confirmCancel.focus();
    }

    function closeConfirmDialog() {
        confirmAction = null;
        elements.confirmBackdrop.hidden = true;
    }

    async function runConfirmedAction() {
        if (!confirmAction) {
            return;
        }

        var pendingAction = confirmAction;
        var action = pendingAction.action;
        var player = pendingAction.player;
        closeConfirmDialog();
        if (action === "console-command") {
            await submitConsoleCommand(pendingAction.command);
            return;
        }
        if (action === "tow") {
            await submitShipTow(pendingAction);
            return;
        }

        try {
            var payload = await postConsoleJson("/api/admin/" + action, { player: player });
            if (!payload || payload.ok !== true) {
                throw new Error(payload && payload.error ? payload.error : "Request rejected");
            }
            appendConsoleEntries([{
                kind: "output",
                text: action.charAt(0).toUpperCase() + action.slice(1) + " completed for " + player + "."
            }]);
            if (action === "ban" || action === "unban") {
                await loadBanList(true);
            }
        } catch (error) {
            appendConsoleError(
                action.charAt(0).toUpperCase() + action.slice(1) + " failed (" +
                (error && error.message ? error.message : "Request failed") + ")"
            );
        }
    }

    async function saveWorld() {
        elements.saveButton.disabled = true;
        elements.saveStatus.textContent = "Saving…";
        window.clearTimeout(saveButtonTimer);
        saveButtonTimer = window.setTimeout(function () {
            elements.saveButton.disabled = false;
        }, 5000);

        try {
            var payload = await postConsoleJson("/api/admin/save", {});
            if (!payload || payload.ok !== true) {
                throw new Error(payload && payload.error ? payload.error : "Request rejected");
            }
            clearConsoleFailure("save");
            elements.saveStatus.textContent = payload.alreadySaving ? "Already saving" : "Save requested";
        } catch (error) {
            elements.saveStatus.textContent = "Save failed";
            reportConsoleFailure("save", "World save", error);
        }
    }

    async function submitConsoleCommand(confirmedCommand) {
        var isConfirmed = typeof confirmedCommand === "string";
        var command = isConfirmed ? confirmedCommand : elements.commandInput.value.trim();
        if (!command) {
            return;
        }

        if (!isConfirmed) {
            var shutdownDetails = shutdownCommandDetails(command);
            if (shutdownDetails) {
                openShutdownConfirmDialog(command, shutdownDetails);
                return;
            }
        }

        appendConsoleEntries([{ kind: "command", text: "> " + command }]);
        commandHistory.push(command);
        if (commandHistory.length > COMMAND_HISTORY_LIMIT) {
            commandHistory.shift();
        }
        commandHistoryIndex = commandHistory.length;
        commandHistoryDraft = "";
        elements.commandInput.value = "";
        closeSuggestions();

        var lowerCommand = command.toLowerCase();
        if (lowerCommand === "help" || lowerCommand === "vo help" || lowerCommand === "/help") {
            await loadConsoleMeta();
            renderConsoleHelp();
            return;
        }

        try {
            var payload = await postConsoleJson("/api/console/exec", { command: command });
            if (!payload || payload.ok !== true) {
                throw new Error(payload && payload.error ? payload.error : "Command rejected");
            }

            var output = Array.isArray(payload.output) ? payload.output : [];
            appendConsoleEntries(output.map(function (line) {
                return { kind: "output", text: line == null ? "" : String(line) };
            }));
        } catch (error) {
            appendConsoleError("Command failed (" +
                (error && error.message ? error.message : "Request failed") + ")");
        }
    }

    function walkCommandHistory(direction) {
        if (commandHistory.length === 0) {
            return;
        }

        if (commandHistoryIndex === commandHistory.length) {
            commandHistoryDraft = elements.commandInput.value;
        }
        commandHistoryIndex = Math.max(0, Math.min(commandHistory.length, commandHistoryIndex + direction));
        elements.commandInput.value = commandHistoryIndex === commandHistory.length
            ? commandHistoryDraft
            : commandHistory[commandHistoryIndex];
        consoleSuggestionClosed = false;
        renderCommandSuggestions();
    }

    function startConsolePolling() {
        if (consolePollingStarted) {
            return;
        }

        consolePollingStarted = true;
        scheduleConsoleLogPolling(CONSOLE_LOG_POLL_INTERVAL_MS);
    }

    function scheduleConsoleLogPolling(delay) {
        window.clearTimeout(consoleLogPollTimer);
        consoleLogPollTimer = 0;
        if (!consolePollingStarted || pollCircuitOpen) {
            return;
        }
        consoleLogPollTimer = window.setTimeout(async function () {
            consoleLogPollTimer = 0;
            await pollConsoleLog();
            scheduleConsoleLogPolling(CONSOLE_LOG_POLL_INTERVAL_MS);
        }, delay);
    }

    function bindConsoleEvents() {
        addAppListener(elements.mapTab, "click", function () {
            setActiveTab("map", true);
        });
        addAppListener(elements.consoleTab, "click", function () {
            setActiveTab("console", true);
        });
        addAppListener(elements.codexTab, "click", function () {
            setActiveTab("codex", true);
        });
        addAppListener(elements.tabList, "keydown", function (event) {
            if (["ArrowLeft", "ArrowRight", "Home", "End"].indexOf(event.key) === -1) {
                return;
            }
            var tabs = [elements.mapTab, elements.consoleTab, elements.codexTab].filter(
                function (button) { return !button.hidden; }
            );
            var currentIndex = Math.max(0, tabs.indexOf(document.activeElement));
            var nextIndex = currentIndex;
            if (event.key === "Home") {
                nextIndex = 0;
            } else if (event.key === "End") {
                nextIndex = tabs.length - 1;
            } else if (event.key === "ArrowLeft") {
                nextIndex = (currentIndex - 1 + tabs.length) % tabs.length;
            } else {
                nextIndex = (currentIndex + 1) % tabs.length;
            }
            event.preventDefault();
            tabs[nextIndex].focus();
            setActiveTab(
                tabs[nextIndex] === elements.consoleTab
                    ? "console"
                    : (tabs[nextIndex] === elements.codexTab ? "codex" : "map"),
                true
            );
        });
        addAppListener(elements.consoleLog, "scroll", function () {
            var distanceFromBottom = elements.consoleLog.scrollHeight -
                elements.consoleLog.scrollTop - elements.consoleLog.clientHeight;
            consoleFollowLog = distanceFromBottom <= 36;
            if (consoleFollowLog) {
                elements.consoleResume.hidden = true;
            }
        });
        addAppListener(elements.consoleResume, "click", function () {
            consoleFollowLog = true;
            elements.consoleLog.scrollTop = elements.consoleLog.scrollHeight;
            elements.consoleResume.hidden = true;
        });
        addAppListener(elements.commandsToggle, "click", toggleCommandReference);
        addAppListener(elements.commandReferenceClose, "click", function () {
            setCommandReferenceOpen(false);
            elements.commandsToggle.focus();
        });
        addAppListener(elements.commandForm, "submit", function (event) {
            event.preventDefault();
            submitConsoleCommand();
        });
        addAppListener(elements.commandInput, "input", function () {
            consoleSuggestionClosed = false;
            commandHistoryIndex = commandHistory.length;
            renderCommandSuggestions();
        });
        addAppListener(elements.commandInput, "keydown", function (event) {
            if (event.key === "ArrowUp") {
                event.preventDefault();
                if (!elements.suggestionList.hidden && consoleSuggestions.length > 0) {
                    moveConsoleSuggestionSelection(-1);
                } else {
                    walkCommandHistory(-1);
                }
            } else if (event.key === "ArrowDown") {
                event.preventDefault();
                if (!elements.suggestionList.hidden && consoleSuggestions.length > 0) {
                    moveConsoleSuggestionSelection(1);
                } else {
                    walkCommandHistory(1);
                }
            } else if ((event.key === "Tab" || event.key === "Enter") &&
                consoleSuggestionIndex >= 0) {
                event.preventDefault();
                completeSuggestion(consoleSuggestionIndex);
            } else if (event.key === "Escape") {
                consoleSuggestionClosed = true;
                closeSuggestions();
            }
        });
        addAppListener(elements.saveButton, "click", saveWorld);
        addAppListener(elements.confirmCancel, "click", closeConfirmDialog);
        addAppListener(elements.confirmSubmit, "click", runConfirmedAction);
        addAppListener(elements.confirmBackdrop, "click", function (event) {
            if (event.target === elements.confirmBackdrop) {
                closeConfirmDialog();
            }
        });
        addKeyboardListener(function (event) {
            if (event.key === "Escape" && !elements.confirmBackdrop.hidden) {
                closeConfirmDialog();
            } else if (event.key === "Escape" && !elements.commandReference.hidden) {
                setCommandReferenceOpen(false);
                elements.commandsToggle.focus();
            }
        });
    }

    function showConnectionLostState() {
        elements.mapStatus.hidden = false;
        elements.mapStatus.querySelector(".spinner").hidden = true;
        elements.mapStatusText.textContent = "Connection lost — reload to reconnect";
        elements.offlineBadge.textContent = "Connection lost — reload to reconnect";
        elements.offlineBadge.hidden = false;
    }

    function clearRecurringPollTimers() {
        recurringPollTimers.forEach(function (timer) {
            window.clearTimeout(timer);
        });
        recurringPollTimers.clear();
        window.clearTimeout(consoleLogPollTimer);
        consoleLogPollTimer = 0;
        window.clearTimeout(statsPollTimer);
        statsPollTimer = 0;
        window.clearTimeout(entityPollTimer);
        entityPollTimer = 0;
        window.clearTimeout(entityFocusPollTimer);
        entityFocusPollTimer = 0;
        window.clearTimeout(heatmapPollTimer);
        heatmapPollTimer = 0;
        window.clearTimeout(dungeonRegistryState.timer);
        dungeonRegistryState.timer = 0;
        window.clearTimeout(dungeonDetailPollTimer);
        dungeonDetailPollTimer = 0;
        clearLeaderboardPoll();
        stopAllLazyPoiPolling();
    }

    function recordPollSuccess(pollKey) {
        if (pollCircuitOpen) {
            return;
        }
        if (pollKey === "status") {
            consecutiveStatusFailures = 0;
        } else {
            pollFailureCounts[pollKey] = 0;
        }
    }

    function recordPollFailure(pollKey) {
        if (document.hidden || pollCircuitOpen) {
            return;
        }

        var failures;
        if (pollKey === "status") {
            consecutiveStatusFailures++;
            failures = consecutiveStatusFailures;
        } else {
            failures = (pollFailureCounts[pollKey] || 0) + 1;
            pollFailureCounts[pollKey] = failures;
        }
        if (failures < POLL_FAILURE_LIMIT) {
            return;
        }

        pollCircuitOpen = true;
        window.clearTimeout(mapLoadingTimeoutTimer);
        mapLoadingTimeoutTimer = 0;
        clearRecurringPollTimers();
        failedFeeds.add("status");
        showConnectionLostState();
        updateMapMetricStatus();
        updateFeedStalenessDots();
        appendConsoleError("Connection lost — reload to reconnect");
    }

    function setFeedState(feed, isOnline) {
        if (isOnline) {
            failedFeeds.delete(feed);
        } else {
            failedFeeds.add(feed);
        }

        if (pollCircuitOpen) {
            failedFeeds.add("status");
            showConnectionLostState();
        } else {
            elements.offlineBadge.textContent = "Offline";
            elements.offlineBadge.hidden = failedFeeds.size === 0;
        }

        updateMapMetricStatus();
        updateFeedStalenessDots();
    }

    function textOrDash(value) {
        return typeof value === "string" && value.trim() ? value : "—";
    }

    function finiteNumberOrNull(value) {
        var number = Number(value);
        return Number.isFinite(number) ? number : null;
    }

    function poiPaletteChoice(key) {
        return POI_COLOR_PALETTE.find(function (choice) {
            return choice.key === key;
        }) || null;
    }

    function sanitizePoiOpacity(value) {
        var opacity = Number(value);
        if (!Number.isFinite(opacity)) {
            return LAYER_DEFAULTS.poiOpacity;
        }
        return Math.max(20, Math.min(100, Math.round(opacity / 5) * 5));
    }

    function storageWrite(key, value) {
        try {
            window.localStorage.setItem(key, value);
            return true;
        } catch (error) {
            if (!storageWriteWarningShown) {
                storageWriteWarningShown = true;
                showNoticeToast("Settings can't be saved — browser storage is unavailable");
            }
            return false;
        }
    }

    function sanitizeWebPinAuthor(value) {
        return String(value || "")
            .trim()
            .replace(/[\u0000-\u001f\u007f-\u009f<>]/g, "")
            .trim()
            .slice(0, 32);
    }

    function storedWebPinAuthor() {
        try {
            return sanitizeWebPinAuthor(
                window.localStorage.getItem(WEB_PIN_AUTHOR_STORAGE_KEY) || ""
            );
        } catch (error) {
            return "";
        }
    }

    function webPinOperatorAuthor() {
        var author = storedWebPinAuthor();
        return author || (currentView === "admin" ? "Admin" : "");
    }

    function saveWebPinAuthor(value) {
        var author = sanitizeWebPinAuthor(value);
        if (author) {
            storageWrite(WEB_PIN_AUTHOR_STORAGE_KEY, author);
        }
        return author;
    }

    function canCreateWebPin() {
        return webPinsAvailable &&
            (currentView === "admin" || webPinsSharedEditing);
    }

    function canEditWebPin(pin) {
        if (currentView === "admin") {
            return true;
        }
        var author = storedWebPinAuthor();
        return currentView !== "public" && webPinsSharedEditing && Boolean(author) &&
            typeof pin.author === "string" &&
            pin.author.toLocaleLowerCase() === author.toLocaleLowerCase();
    }

    function loadLayerSettings() {
        var settings = {};
        Object.keys(LAYER_DEFAULTS).forEach(function (key) {
            var defaultValue = LAYER_DEFAULTS[key];
            settings[key] = defaultValue && typeof defaultValue === "object"
                ? Object.assign({}, defaultValue)
                : defaultValue;
        });

        try {
            var savedText = window.localStorage.getItem(LAYER_STORAGE_KEY);
            var isMigration = savedText === null;
            var migratedPoiKeys = false;
            if (isMigration) {
                savedText = window.localStorage.getItem(LEGACY_LAYER_STORAGE_KEY);
            }
            var saved = savedText ? JSON.parse(savedText) : null;
            if (saved && typeof saved === "object") {
                Object.keys(LAYER_DEFAULTS).forEach(function (key) {
                    if (typeof LAYER_DEFAULTS[key] === "boolean" &&
                        typeof saved[key] === "boolean") {
                        settings[key] = saved[key];
                    }
                });
                if (["s", "m", "l"].indexOf(saved.iconSize) !== -1) {
                    settings.iconSize = saved.iconSize;
                }
                if (["default", "topo", "chart"].indexOf(saved.mapStyle) !== -1) {
                    settings.mapStyle = saved.mapStyle;
                }
                if (["24h", "7d"].indexOf(saved.heatmapWindow) !== -1) {
                    settings.heatmapWindow = saved.heatmapWindow;
                }
                if (Object.prototype.hasOwnProperty.call(
                    TIMELAPSE_SPEEDS,
                    saved.timelapseSpeed
                )) {
                    settings.timelapseSpeed = saved.timelapseSpeed;
                }
                if (typeof saved.poiOpacity === "number" &&
                    Number.isFinite(saved.poiOpacity)) {
                    settings.poiOpacity = sanitizePoiOpacity(saved.poiOpacity);
                }
                if (saved.poiColors && typeof saved.poiColors === "object" &&
                    !Array.isArray(saved.poiColors)) {
                    POI_CATEGORIES.forEach(function (category) {
                        var colorKey = saved.poiColors[category.key];
                        if (poiPaletteChoice(colorKey)) {
                            settings.poiColors[category.key] = colorKey;
                        }
                    });
                }
                if (saved.poiCollapsed && typeof saved.poiCollapsed === "object" &&
                    !Array.isArray(saved.poiCollapsed)) {
                    POI_CATEGORIES.forEach(function (category) {
                        if (typeof saved.poiCollapsed[category.key] === "boolean") {
                            settings.poiCollapsed[category.key] =
                                saved.poiCollapsed[category.key];
                        }
                    });
                }
                if (saved.dungeon === true) {
                    POI_CATEGORIES.find(function (category) {
                        return category.key === "dungeons";
                    }).groups.forEach(function (key) {
                        settings[key] = true;
                    });
                    migratedPoiKeys = true;
                }
            }
            if (isMigration) {
                var legacyMinimap = window.localStorage.getItem(LEGACY_MINIMAP_STORAGE_KEY);
                if (legacyMinimap !== null) {
                    settings.minimap = legacyMinimap === "1" || legacyMinimap === "true";
                }
            }
            if (isMigration || migratedPoiKeys) {
                storageWrite(LAYER_STORAGE_KEY, JSON.stringify(settings));
            }
        } catch (error) {
            return settings;
        }

        return settings;
    }

    function saveLayerSettings() {
        storageWrite(LAYER_STORAGE_KEY, JSON.stringify(layerSettings));
    }

    function popupAgeText(updatedAt) {
        var seconds = Math.max(0, Math.floor((Date.now() - updatedAt) / 1000));
        if (seconds < 60) {
            return seconds + "s ago";
        }

        return Math.floor(seconds / 60) + "m ago";
    }

    function popupStalenessText(feed) {
        return "as of " + popupAgeText(feedLastUpdated[feed] || Date.now());
    }

    function popupSurveyStalenessText(scanUnixMs) {
        return "as of last survey " + popupAgeText(scanUnixMs);
    }

    function miniCardStateText(content) {
        var text = content && typeof content.textContent === "string"
            ? content.textContent
            : String(content || "");
        return text.replace(/\s+/g, " ").trim();
    }

    function normalizeMiniCardText(titleContent, stateContent) {
        var title = miniCardStateText(titleContent) || "Point of interest";
        var state = miniCardStateText(stateContent);
        var titleLower = title.toLowerCase();
        var stateLower = state.toLowerCase();

        if (stateLower === titleLower) {
            state = "";
        } else if (stateLower.indexOf(titleLower) === 0) {
            state = state.slice(title.length).replace(/^[\s\-—·:]+/, "").trim();
        }

        return {
            state: state,
            title: title
        };
    }

    function miniCard(options) {
        var card = document.createElement("div");
        var icon = document.createElement("span");
        var copy = document.createElement("span");
        var title = document.createElement("span");
        var state = document.createElement("span");
        var normalized = normalizeMiniCardText(options.title, options.state);
        var iconSvg = window.VO_ICONS &&
            typeof window.VO_ICONS[options.iconKey] === "string"
            ? window.VO_ICONS[options.iconKey]
            : "";

        card.className = "vo-minicard" + (normalized.state ? "" : " is-single-line");
        icon.className = "vo-minicard-icon";
        icon.setAttribute("aria-hidden", "true");
        if (iconSvg) {
            icon.innerHTML = iconSvg;
        } else {
            icon.textContent = options.fallbackGlyph || "•";
        }
        copy.className = "vo-minicard-copy";
        title.className = "vo-minicard-title";
        title.textContent = normalized.title;
        state.className = "vo-minicard-state";
        state.textContent = normalized.state;
        copy.appendChild(title);
        if (normalized.state) {
            copy.appendChild(state);
        }
        card.appendChild(icon);
        card.appendChild(copy);
        return card;
    }

    function miniCardTooltipContent(plainContent, cardOptions) {
        if (!hoverMiniCardsEnabled) {
            return plainContent;
        }

        return miniCard({
            fallbackGlyph: cardOptions.fallbackGlyph,
            iconKey: cardOptions.iconKey,
            state: plainContent,
            title: cardOptions.title
        });
    }

    function miniCardTooltipOptions(options) {
        if (!hoverMiniCardsEnabled) {
            return options;
        }

        var cardOptions = {};
        Object.keys(options).forEach(function (key) {
            cardOptions[key] = options[key];
        });
        cardOptions.className = (options.className ? options.className + " " : "") +
            "vo-minicard-tooltip";
        return cardOptions;
    }

    function bindMarkerTooltip(marker, plainContent, cardOptions, tooltipOptions) {
        marker.bindTooltip(
            miniCardTooltipContent(plainContent, cardOptions),
            miniCardTooltipOptions(tooltipOptions)
        );
    }

    function updateMarkerTooltip(marker, plainContent, cardOptions) {
        if (!hoverMiniCardsEnabled) {
            marker.setTooltipContent(plainContent);
            return;
        }

        var tooltip = marker.getTooltip();
        var card = tooltip ? tooltip.getContent() : null;
        if (!card || !card.classList || !card.classList.contains("vo-minicard")) {
            marker.setTooltipContent(miniCardTooltipContent(plainContent, cardOptions));
            return;
        }

        var title = card.querySelector(".vo-minicard-title");
        var copy = card.querySelector(".vo-minicard-copy");
        var state = card.querySelector(".vo-minicard-state");
        var normalized = normalizeMiniCardText(cardOptions.title, plainContent);
        if (title) {
            title.textContent = normalized.title;
        }
        if (normalized.state) {
            if (!state && copy) {
                state = document.createElement("span");
                state.className = "vo-minicard-state";
                copy.appendChild(state);
            }
            if (state) {
                state.textContent = normalized.state;
            }
            card.classList.remove("is-single-line");
        } else {
            if (state && state.parentNode) {
                state.parentNode.removeChild(state);
            }
            card.classList.add("is-single-line");
        }
    }

    function popupShell(options) {
        var shell = document.createElement("div");
        var header = document.createElement("div");
        var glyph = document.createElement("span");
        var heading = document.createElement("div");
        var kicker = document.createElement("div");
        var title = document.createElement("div");
        var rows = document.createElement("div");

        shell.className = "vo-popup";
        header.className = "vo-popup-header";
        glyph.className = "vo-popup-glyph";
        if (options.iconKey) {
            glyph.innerHTML = iconMarkup(options.iconKey, options.glyph || "•");
        } else {
            glyph.textContent = options.glyph || "•";
        }
        glyph.setAttribute("aria-hidden", "true");
        heading.className = "vo-popup-heading";
        kicker.className = "vo-popup-kicker";
        kicker.textContent = options.kicker || "MAP";
        title.className = "vo-popup-title";
        title.textContent = options.title || "Point of interest";
        rows.className = "vo-popup-rows";

        heading.appendChild(kicker);
        heading.appendChild(title);
        header.appendChild(glyph);
        header.appendChild(heading);
        shell.appendChild(header);

        (options.rows || []).forEach(function (row) {
            var rowElement = document.createElement("div");
            var label = document.createElement("span");
            var value = document.createElement("span");
            var valueText = document.createElement("span");
            rowElement.className = "vo-popup-row";
            label.className = "vo-popup-label";
            label.textContent = row.label;
            value.className = "vo-popup-value";
            if (row.valueNode) {
                value.appendChild(row.valueNode);
            } else {
                valueText.textContent = row.value;
                value.appendChild(valueText);
            }
            if (typeof row.copy === "string") {
                var copy = document.createElement("button");
                copy.type = "button";
                copy.className = "vo-copy";
                copy.textContent = "Copy";
                copy.setAttribute("data-copy", row.copy);
                copy.setAttribute("aria-label", "Copy " + row.label.toLowerCase());
                value.appendChild(copy);
            }
            if (row.action) {
                var rowAction = document.createElement("button");
                rowAction.type = "button";
                rowAction.className = "vo-popup-row-action";
                rowAction.textContent = row.action.label;
                rowAction.setAttribute("data-popup-action", row.action.action);
                if (row.action.kind) {
                    rowAction.setAttribute("data-trail-kind", row.action.kind);
                }
                if (row.action.key) {
                    rowAction.setAttribute("data-target-key", row.action.key);
                }
                value.appendChild(rowAction);
            }
            rowElement.appendChild(label);
            rowElement.appendChild(value);
            rows.appendChild(rowElement);
        });
        shell.appendChild(rows);

        var actions = null;
        if (options.actions && options.actions.length > 0) {
            actions = document.createElement("div");
            actions.className = "vo-popup-actions";
            options.actions.forEach(function (action) {
                var button = document.createElement("button");
                button.type = "button";
                button.className = "vo-popup-action" +
                    (action.active ? " is-active" : "") +
                    (action.pending ? " is-pending" : "") +
                    (action.danger ? " is-danger" : "");
                button.textContent = action.label;
                button.setAttribute("data-popup-action", action.action);
                button.disabled = action.disabled === true;
                if (action.pending) {
                    button.setAttribute("aria-busy", "true");
                }
                if (action.kind) {
                    button.setAttribute("data-trail-kind", action.kind);
                }
                if (action.key) {
                    button.setAttribute("data-target-key", action.key);
                }
                if (action.action === "trail") {
                    button.setAttribute("aria-pressed", String(action.active === true));
                }
                actions.appendChild(button);
            });
            if (!options.actionsInFooter) {
                shell.appendChild(actions);
            }
        }

        var footer = document.createElement("div");
        footer.className = "vo-popup-footer";
        var footerStatus = options.actionsInFooter
            ? document.createElement("span")
            : footer;
        if (options.actionsInFooter) {
            footer.classList.add("has-actions");
            footerStatus.className = "vo-popup-footer-status";
            if (actions) {
                footer.appendChild(actions);
            }
        }
        if (Number.isFinite(options.surveyUnixMs) && options.surveyUnixMs > 0) {
            footerStatus.setAttribute("data-survey-unix-ms", String(options.surveyUnixMs));
            footerStatus.textContent = popupSurveyStalenessText(options.surveyUnixMs);
        } else {
            footerStatus.setAttribute("data-feed", options.feed);
            footerStatus.textContent = popupStalenessText(options.feed);
        }
        if (options.actionsInFooter) {
            footer.appendChild(footerStatus);
        }
        shell.appendChild(footer);
        return shell;
    }

    function bindMapPopup(marker, builder, metadata) {
        marker._voPopupKind = metadata && metadata.kind ? metadata.kind : "";
        marker._voTrailKind = metadata && metadata.trailKind ? metadata.trailKind : "";
        marker._voTrailKey = metadata && metadata.trailKey ? metadata.trailKey : "";
        marker._voEntityId = metadata && metadata.entityId ? metadata.entityId : "";
        marker.bindPopup(function () {
            return builder();
        }, {
            autoPan: true,
            className: "vo-popup-wrap",
            closeButton: true,
            maxWidth: 280
        });
    }

    function refreshOpenPopupFooter() {
        if (!map || !map._popup || !map._popup.getElement()) {
            return;
        }

        var footers = map._popup.getElement().querySelectorAll(".vo-popup-footer");
        Array.prototype.forEach.call(footers, function (footer) {
            var status = footer.querySelector(".vo-popup-footer-status") || footer;
            var scanUnixMs = Number(status.getAttribute("data-survey-unix-ms"));
            status.textContent = Number.isFinite(scanUnixMs) && scanUnixMs > 0
                ? popupSurveyStalenessText(scanUnixMs)
                : popupStalenessText(status.getAttribute("data-feed"));
        });
    }

    function refreshOpenPopupContent() {
        if (!map || !map._popup) {
            return;
        }

        var content = map._popup.getContent();
        if (typeof content === "function") {
            map._popup.setContent(content);
        }
        refreshOpenPopupFooter();
    }

    function fallbackCopyText(text) {
        return new Promise(function (resolve, reject) {
            var textarea = document.createElement("textarea");
            var activeElement = document.activeElement;
            textarea.value = text;
            textarea.setAttribute("readonly", "");
            textarea.style.position = "fixed";
            textarea.style.opacity = "0";
            appRoot.appendChild(textarea);
            textarea.select();
            var copied = false;
            try {
                copied = document.execCommand("copy");
            } catch (error) {
                copied = false;
            }
            appRoot.removeChild(textarea);
            if (activeElement && typeof activeElement.focus === "function") {
                activeElement.focus();
            }
            if (copied) {
                resolve();
            } else {
                reject(new Error("Copy failed"));
            }
        });
    }

    function copyText(text) {
        if (navigator.clipboard && typeof navigator.clipboard.writeText === "function") {
            return navigator.clipboard.writeText(text).catch(function () {
                return fallbackCopyText(text);
            });
        }
        return fallbackCopyText(text);
    }

    function flashCopyButton(button) {
        window.clearTimeout(button._voCopyTimer);
        if (!button._voCopyLabel) {
            button._voCopyLabel = button.textContent;
        }
        button.textContent = "✓";
        button.classList.add("is-copied");
        button._voCopyTimer = window.setTimeout(function () {
            button.textContent = button._voCopyLabel;
            button.classList.remove("is-copied");
            button._voCopyTimer = 0;
        }, 1200);
    }

    function trailTargetId(kind, key) {
        return kind + ":" + key;
    }

    function isFollowing(kind, key) {
        return Boolean(followTarget && followTarget.kind === kind && followTarget.id === key);
    }

    function toggleSelectedTrail(kind, key) {
        var id = trailTargetId(kind, key);
        if (selectedTrailTargets.has(id)) {
            selectedTrailTargets.delete(id);
        } else {
            selectedTrailTargets.set(id, { kind: kind, key: key });
            requestTrailBackfill(kind, key, 1800);
        }
        renderTrails();
        refreshOpenPopupContent();
    }

    function bindPopupDocumentEvents() {
        addAppListener(document, "click", function (event) {
            if (!eventInsideApp(event)) {
                return;
            }
            var target = event.target;
            if (!target || typeof target.closest !== "function") {
                return;
            }

            var copyButton = target.closest(".vo-copy[data-copy]");
            if (copyButton) {
                event.preventDefault();
                copyFromButton(copyButton);
                return;
            }

            var actionButton = target.closest(
                ".vo-popup-action[data-popup-action], .vo-popup-row-action[data-popup-action]"
            );
            if (!actionButton) {
                return;
            }

            event.preventDefault();
            var action = actionButton.getAttribute("data-popup-action");
            var key = actionButton.getAttribute("data-target-key") || "";
            var kind = actionButton.getAttribute("data-trail-kind") || "player";
            if (action === "follow") {
                if (isFollowing(kind, key)) {
                    clearFollow();
                } else if (kind === "player") {
                    followPlayer(key);
                } else {
                    followEntity(kind, key);
                }
            } else if (action === "trail") {
                toggleSelectedTrail(kind, key);
            } else if (action === "tow" && kind === "ship" && currentView === "admin") {
                var ship = shipTowEntityById(key);
                if (ship) {
                    armShipTow(ship);
                }
            } else if (action === "dungeon-open" && hasLiveAccess()) {
                openDungeonInterior(key);
            } else if (action === "watch" && kind === "player") {
                enterCinema(key);
            } else if (action === "jump-tombstone") {
                jumpToTombstone(key);
            } else if (action === "jump-portal") {
                jumpToPortal(key);
            } else if (action === "webpin-toggle") {
                updateWebPinChecked(key, actionButton);
            } else if (action === "webpin-edit") {
                var pin = webPinById(key);
                if (pin && canEditWebPin(pin)) {
                    openWebPinDialog({ pin: pin });
                }
            } else if (action === "webpin-delete") {
                if (actionButton.dataset.confirming !== "true") {
                    actionButton.dataset.confirming = "true";
                    actionButton.textContent = "Confirm?";
                    actionButton.classList.add("is-confirming");
                    window.clearTimeout(actionButton._voConfirmTimer);
                    actionButton._voConfirmTimer = window.setTimeout(function () {
                        actionButton.dataset.confirming = "false";
                        actionButton.textContent = "Delete";
                        actionButton.classList.remove("is-confirming");
                    }, 3000);
                } else {
                    window.clearTimeout(actionButton._voConfirmTimer);
                    deleteWebPin(key, actionButton);
                }
            }
        });
    }

    function worldDistance(leftX, leftZ, rightX, rightZ) {
        var deltaX = rightX - leftX;
        var deltaZ = rightZ - leftZ;
        return Math.sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    function measuredTweenDuration(previousUnixMs, nextUnixMs, fallbackMs) {
        var elapsed = nextUnixMs - previousUnixMs;
        if (!Number.isFinite(elapsed) || elapsed <= 0) {
            return fallbackMs;
        }
        return Math.max(
            MARKER_TWEEN_MIN_DURATION_MS,
            Math.min(MARKER_TWEEN_MAX_DURATION_MS, elapsed)
        );
    }

    function playerPayloadTweenDuration(payload) {
        var snapshotUnixMs = Number(payload && payload.unixMs);
        if (!Number.isFinite(snapshotUnixMs) || snapshotUnixMs <= 0) {
            return playerTweenDurationMs;
        }
        if (lastPlayerSnapshotUnixMs > 0 && snapshotUnixMs > lastPlayerSnapshotUnixMs) {
            playerTweenDurationMs = measuredTweenDuration(
                lastPlayerSnapshotUnixMs,
                snapshotUnixMs,
                POLL_INTERVAL_MS
            );
        }
        lastPlayerSnapshotUnixMs = Math.max(lastPlayerSnapshotUnixMs, snapshotUnixMs);
        return playerTweenDurationMs;
    }

    function entityPayloadTweenDuration(payload, nextRevision) {
        var snapshotUnixMs = Number(payload && payload.time);
        if (nextRevision === entityRevision ||
            !Number.isFinite(snapshotUnixMs) || snapshotUnixMs <= 0) {
            return entityTweenDurationMs;
        }
        if (lastEntityRevisionUnixMs > 0 && snapshotUnixMs > lastEntityRevisionUnixMs) {
            entityTweenDurationMs = measuredTweenDuration(
                lastEntityRevisionUnixMs,
                snapshotUnixMs,
                ENTITIES_POLL_INTERVAL_MS
            );
        }
        lastEntityRevisionUnixMs = Math.max(lastEntityRevisionUnixMs, snapshotUnixMs);
        return entityTweenDurationMs;
    }

    function entityFocusPayloadTweenDuration(payload) {
        var snapshotUnixMs = Number(payload && payload.focus && payload.focus.unixMs);
        if (!Number.isFinite(snapshotUnixMs) || snapshotUnixMs <= 0) {
            return entityFocusTweenDurationMs;
        }
        if (lastEntityFocusUnixMs > 0 && snapshotUnixMs > lastEntityFocusUnixMs) {
            entityFocusTweenDurationMs = measuredTweenDuration(
                lastEntityFocusUnixMs,
                snapshotUnixMs,
                POLL_INTERVAL_MS
            );
        }
        lastEntityFocusUnixMs = Math.max(lastEntityFocusUnixMs, snapshotUnixMs);
        return entityFocusTweenDurationMs;
    }

    function stopMarkerTweenFrameWhenIdle() {
        if (markerTweens.size !== 0 || !markerTweenFrame) {
            return;
        }
        window.cancelAnimationFrame(markerTweenFrame);
        markerTweenFrame = 0;
    }

    function cancelMarkerTween(key) {
        markerTweens.delete(key);
        stopMarkerTweenFrameWhenIdle();
    }

    function scheduleMarkerTweenFrame() {
        if (markerTweenFrame || markerTweens.size === 0 || document.hidden) {
            return;
        }
        markerTweenFrame = window.requestAnimationFrame(markerTweenTick);
    }

    function markerTweenTick(now) {
        markerTweenFrame = 0;
        if (document.hidden) {
            return;
        }

        markerTweens.forEach(function (tween, key) {
            var amount = Math.min(1, Math.max(0, (now - tween.startedAt) / tween.duration));
            var current = L.latLng(
                tween.from.lat + ((tween.to.lat - tween.from.lat) * amount),
                tween.from.lng + ((tween.to.lng - tween.from.lng) * amount)
            );
            tween.marker.setLatLng(current);
            tween.onMove(current);
            if (amount >= 1) {
                markerTweens.delete(key);
            }
        });
        scheduleMarkerTweenFrame();
    }

    function tweenMarker(key, marker, target, duration, options) {
        var tweenDuration = Number.isFinite(duration) ? duration : POLL_INTERVAL_MS;
        var existing = markerTweens.get(key);
        if (existing && existing.to.lat === target.lat && existing.to.lng === target.lng) {
            return;
        }
        var current = marker.getLatLng();
        var currentWorld = latLngToWorld(current);
        var targetWorld = latLngToWorld(target);
        var distance = currentWorld && targetWorld
            ? worldDistance(currentWorld.x, currentWorld.z, targetWorld.x, targetWorld.z)
            : 0;
        if (distance > MARKER_TELEPORT_DISTANCE_M && !options.allowTeleportTween) {
            cancelMarkerTween(key);
            marker.setLatLng(target);
            resetTrailBuffer(
                options.trailKey,
                options.trailKind,
                targetWorld.x,
                targetWorld.z,
                Date.now()
            );
            options.onMove(target);
            return;
        }
        if (distance <= 0.01 || document.hidden) {
            cancelMarkerTween(key);
            marker.setLatLng(target);
            options.onMove(target);
            return;
        }

        markerTweens.set(key, {
            duration: Math.max(
                MARKER_TWEEN_MIN_DURATION_MS,
                Math.min(MARKER_TWEEN_MAX_DURATION_MS, tweenDuration)
            ),
            from: L.latLng(current.lat, current.lng),
            marker: marker,
            onMove: options.onMove,
            startedAt: performance.now(),
            to: L.latLng(target.lat, target.lng)
        });
        scheduleMarkerTweenFrame();
    }

    function handleMarkerTweenVisibility() {
        if (document.hidden) {
            if (markerTweenFrame) {
                window.cancelAnimationFrame(markerTweenFrame);
                markerTweenFrame = 0;
            }
            return;
        }
        refreshPollingAfterVisibility();
        scheduleMarkerTweenFrame();
    }

    function calculateDerivedMotion(samples) {
        if (!samples || samples.length < 2) {
            return null;
        }

        var recent = samples.slice(Math.max(0, samples.length - 3));
        var speed = null;
        for (var index = 1; index < recent.length; index++) {
            var elapsed = (recent[index].t - recent[index - 1].t) / 1000;
            if (elapsed <= 0) {
                continue;
            }
            var segmentSpeed = worldDistance(
                recent[index - 1].x,
                recent[index - 1].z,
                recent[index].x,
                recent[index].z
            ) / elapsed;
            speed = speed == null ? segmentSpeed : (segmentSpeed * 0.55) + (speed * 0.45);
        }
        if (speed == null) {
            return null;
        }

        var first = recent[0];
        var last = recent[recent.length - 1];
        var heading = Math.atan2(last.x - first.x, last.z - first.z) * 180 / Math.PI;
        heading = (heading + 360) % 360;
        return {
            headingDeg: heading,
            speedMps: speed
        };
    }

    function ensureTrailBuffer(key, kind, timestamp) {
        var buffer = trailBuffers.get(key);
        if (!buffer) {
            buffer = {
                historyFloor: 0,
                kind: kind,
                lastMovedAt: 0,
                lastSeen: timestamp,
                motion: null,
                samples: []
            };
            trailBuffers.set(key, buffer);
        }

        buffer.kind = kind;
        buffer.lastSeen = timestamp;
        return buffer;
    }

    function resetTrailBuffer(key, kind, x, z, timestamp) {
        if (!key) {
            return;
        }
        var buffer = ensureTrailBuffer(key, kind, timestamp);
        buffer.historyFloor = Math.max(buffer.historyFloor || 0, timestamp);
        buffer.lastMovedAt = 0;
        buffer.motion = null;
        buffer.samples = [{ t: timestamp, x: x, z: z }];
    }

    function appendTrailSample(key, kind, x, z, timestamp) {
        var buffer = ensureTrailBuffer(key, kind, timestamp);
        var last = buffer.samples.length > 0 ? buffer.samples[buffer.samples.length - 1] : null;
        var distance = last ? worldDistance(last.x, last.z, x, z) : 0;
        if (last && distance > MARKER_TELEPORT_DISTANCE_M) {
            resetTrailBuffer(key, kind, x, z, timestamp);
            return buffer;
        }
        if (!last || distance > 0.5 || timestamp - last.t > 5000) {
            buffer.samples.push({ t: timestamp, x: x, z: z });
            if (last && distance > 0.5) {
                buffer.lastMovedAt = timestamp;
            }
        }

        var oldestAllowed = timestamp - TRAIL_MAX_AGE_MS;
        while (buffer.samples.length > 0 && buffer.samples[0].t < oldestAllowed) {
            buffer.samples.shift();
        }
        if (buffer.samples.length > TRAIL_MAX_POINTS) {
            buffer.samples.splice(0, buffer.samples.length - TRAIL_MAX_POINTS);
        }
        buffer.motion = calculateDerivedMotion(buffer.samples);
        return buffer;
    }

    async function requestTrailBackfill(kind, key, windowSeconds) {
        if ((!key.startsWith("player:") && !key.startsWith("entity:")) ||
            trailBackfillWindows.get(key) >= windowSeconds) {
            return;
        }

        trailBackfillWindows.set(key, windowSeconds);
        try {
            var payload = await fetchJson(
                "/api/trail?id=" + encodeURIComponent(key) +
                "&window=" + encodeURIComponent(String(windowSeconds))
            );
            var points = payload && Array.isArray(payload.points) ? payload.points : [];
            var timestamp = Date.now();
            var buffer = ensureTrailBuffer(key, kind, timestamp);
            var oldestBufferedTimestamp = buffer.samples.length > 0
                ? buffer.samples[0].t
                : Number.POSITIVE_INFINITY;
            var historyFloor = buffer.historyFloor || 0;
            var seenTimestamps = new Set();
            var prepend = points.filter(function (point) {
                var pointTimestamp = Number(point && point.t);
                if (!Number.isFinite(pointTimestamp) ||
                    !Number.isFinite(Number(point.x)) ||
                    !Number.isFinite(Number(point.z)) ||
                    pointTimestamp >= oldestBufferedTimestamp ||
                    pointTimestamp < historyFloor ||
                    timestamp - pointTimestamp > TRAIL_MAX_AGE_MS ||
                    seenTimestamps.has(pointTimestamp)) {
                    return false;
                }
                seenTimestamps.add(pointTimestamp);
                return true;
            }).map(function (point) {
                return { t: Number(point.t), x: Number(point.x), z: Number(point.z) };
            }).sort(function (left, right) {
                return left.t - right.t;
            });

            if (prepend.length > 0) {
                buffer.samples = prepend.concat(buffer.samples);
                for (var sampleIndex = buffer.samples.length - 1; sampleIndex > 0; sampleIndex--) {
                    if (worldDistance(
                        buffer.samples[sampleIndex - 1].x,
                        buffer.samples[sampleIndex - 1].z,
                        buffer.samples[sampleIndex].x,
                        buffer.samples[sampleIndex].z
                    ) > MARKER_TELEPORT_DISTANCE_M) {
                        buffer.historyFloor = Math.max(
                            buffer.historyFloor || 0,
                            buffer.samples[sampleIndex].t
                        );
                        buffer.samples.splice(0, sampleIndex);
                        break;
                    }
                }
                if (buffer.samples.length > TRAIL_MAX_POINTS) {
                    buffer.samples.splice(0, buffer.samples.length - TRAIL_MAX_POINTS);
                }
            }

            buffer.lastMovedAt = 0;
            for (var index = 1; index < buffer.samples.length; index++) {
                if (worldDistance(
                    buffer.samples[index - 1].x,
                    buffer.samples[index - 1].z,
                    buffer.samples[index].x,
                    buffer.samples[index].z
                ) > 0.5) {
                    buffer.lastMovedAt = buffer.samples[index].t;
                }
            }
            buffer.motion = calculateDerivedMotion(buffer.samples);
            markerRecords.forEach(updatePlayerMarkerMotion);
            updateShipHeadingLines(latestEntities);
            renderTrails();
            refreshOpenPopupContent();
        } catch (error) {
            if (trailBackfillWindows.get(key) === windowSeconds) {
                trailBackfillWindows.delete(key);
            }
        }
    }

    function backfillVisiblePlayerTrails() {
        latestPlayers.forEach(function (player) {
            requestTrailBackfill("player", player.trailKey, 300);
        });
    }

    function evictTrailBuffers(timestamp) {
        trailBuffers.forEach(function (buffer, key) {
            if (timestamp - buffer.lastSeen <= TRAIL_EVICT_AGE_MS) {
                return;
            }

            trailBuffers.delete(key);
            trailBackfillWindows.delete(key);
            selectedTrailTargets.forEach(function (target, id) {
                if (target.key === key) {
                    selectedTrailTargets.delete(id);
                }
            });
        });
    }

    function derivedMotion(key) {
        var buffer = trailBuffers.get(key);
        return buffer ? buffer.motion : null;
    }

    function shipHeadingColor() {
        var color = window.getComputedStyle(styleRoot)
            .getPropertyValue("--accent").trim();
        return color || "#d9b168";
    }

    function removeShipHeadingLine(key) {
        var line = shipHeadingLines.get(key);
        if (!line) {
            return;
        }

        if (shipHeadingLayer) {
            shipHeadingLayer.removeLayer(line);
        }
        shipHeadingLines.delete(key);
    }

    function clearShipHeadingLines() {
        if (shipHeadingLayer) {
            shipHeadingLayer.clearLayers();
        }
        shipHeadingLines.clear();
    }

    function updateShipHeadingLine(entity, originLatLng) {
        if (!shipHeadingLayer || !entity || entity.group !== "ship" ||
            !entity.trailKey) {
            return;
        }

        var motion = derivedMotion(entity.trailKey);
        if (!layerSettings.ship || !entityLayersAreAvailable() || !motion ||
            !Number.isFinite(motion.speedMps) ||
            motion.speedMps < SHIP_MOVING_SPEED_MPS ||
            !Number.isFinite(motion.headingDeg)) {
            removeShipHeadingLine(entity.trailKey);
            return;
        }

        var headingRadians = motion.headingDeg * Math.PI / 180;
        var originWorld = originLatLng ? latLngToWorld(originLatLng) : null;
        var originX = originWorld ? originWorld.x : entity.x;
        var originZ = originWorld ? originWorld.z : entity.z;
        var points = [
            originLatLng || worldToLatLng(originX, originZ),
            worldToLatLng(
                originX + Math.sin(headingRadians) * SHIP_HEADING_LENGTH_M,
                originZ + Math.cos(headingRadians) * SHIP_HEADING_LENGTH_M
            )
        ];
        var line = shipHeadingLines.get(entity.trailKey);
        if (line) {
            line.setLatLngs(points);
            return;
        }

        line = L.polyline(points, {
            color: shipHeadingColor(),
            dashArray: "8 6",
            interactive: false,
            opacity: 0.75,
            pane: "trailPane",
            weight: 2
        }).addTo(shipHeadingLayer);
        shipHeadingLines.set(entity.trailKey, line);
    }

    function updateShipHeadingLines(entities) {
        if (!shipHeadingLayer || !layerSettings.ship || !entityLayersAreAvailable()) {
            clearShipHeadingLines();
            return;
        }

        var seenKeys = new Set();
        entities.forEach(function (entity) {
            if (entity.group !== "ship" || !entity.trailKey) {
                return;
            }
            seenKeys.add(entity.trailKey);
            var record = entityMarkerRecords.get(entity.trailKey);
            updateShipHeadingLine(entity, record ? record.marker.getLatLng() : null);
        });
        shipHeadingLines.forEach(function (line, key) {
            if (!seenKeys.has(key)) {
                removeShipHeadingLine(key);
            }
        });
    }

    function recordPlayerTrails(players) {
        var timestamp = Date.now();
        players.forEach(function (player) {
            appendTrailSample(player.trailKey, "player", player.x, player.z, timestamp);
        });
        evictTrailBuffers(timestamp);
    }

    function recordEntityTrails(entities) {
        var timestamp = Date.now();
        entities.forEach(function (entity) {
            if (movingEntityGroup(entity.group) && entity.trailKey) {
                appendTrailSample(entity.trailKey, entity.group, entity.x, entity.z, timestamp);
            }
        });
        evictTrailBuffers(timestamp);
    }

    function addVisibleTrailTarget(targets, kind, key, windowMs) {
        if (!key) {
            return;
        }

        var id = trailTargetId(kind, key);
        var target = targets.get(id);
        if (!target) {
            targets.set(id, { kind: kind, key: key, windowMs: windowMs });
            return;
        }
        target.windowMs = Math.max(target.windowMs, windowMs);
    }

    function trailStrokeColor(kind) {
        var token = kind === "player" ? "--accent" : "--frost";
        var color = window.getComputedStyle(styleRoot).getPropertyValue(token).trim();
        return color || (kind === "player" ? "#d9b168" : "#7eb1d6");
    }

    function renderTrailBuffer(buffer, windowMs, timestamp) {
        var samples = buffer.samples.filter(function (sample) {
            return timestamp - sample.t <= windowMs;
        });
        if (samples.length < 2) {
            return;
        }

        var segmentsByBucket = [];
        for (var bucketIndex = 0; bucketIndex < TRAIL_BUCKET_COUNT; bucketIndex++) {
            segmentsByBucket.push([]);
        }
        for (var sampleIndex = 1; sampleIndex < samples.length; sampleIndex++) {
            var age = Math.max(0, timestamp - samples[sampleIndex].t);
            var bucket = Math.min(
                TRAIL_BUCKET_COUNT - 1,
                Math.floor(age / windowMs * TRAIL_BUCKET_COUNT)
            );
            segmentsByBucket[bucket].push([
                worldToLatLng(samples[sampleIndex - 1].x, samples[sampleIndex - 1].z),
                worldToLatLng(samples[sampleIndex].x, samples[sampleIndex].z)
            ]);
        }

        var color = trailStrokeColor(buffer.kind);
        segmentsByBucket.forEach(function (segments, bucket) {
            if (segments.length === 0) {
                return;
            }
            var ageAmount = bucket / (TRAIL_BUCKET_COUNT - 1);
            L.polyline(segments, {
                color: color,
                interactive: false,
                opacity: 0.85 - (0.77 * ageAmount),
                pane: "trailPane",
                weight: 2.5 - (1.25 * ageAmount)
            }).addTo(trailLayer);
        });

        var drawnMinute = -1;
        for (var dotIndex = samples.length - 1; dotIndex >= 0; dotIndex--) {
            var dotAge = Math.max(0, timestamp - samples[dotIndex].t);
            var minute = Math.floor(dotAge / 60000);
            if (minute < 1 || minute === drawnMinute) {
                continue;
            }
            drawnMinute = minute;
            var dotAgeAmount = Math.min(1, dotAge / windowMs);
            L.circleMarker(worldToLatLng(samples[dotIndex].x, samples[dotIndex].z), {
                color: color,
                fillColor: color,
                fillOpacity: 0.78 - (0.62 * dotAgeAmount),
                interactive: false,
                opacity: 0.82 - (0.66 * dotAgeAmount),
                pane: "trailPane",
                radius: 1.5,
                stroke: false
            }).addTo(trailLayer);
        }
    }

    function renderTrails() {
        if (!map || !trailLayer) {
            return;
        }

        trailLayer.clearLayers();
        var targets = new Map();
        selectedTrailTargets.forEach(function (target) {
            addVisibleTrailTarget(targets, target.kind, target.key, TRAIL_TARGET_AGE_MS);
        });
        if (followTarget) {
            addVisibleTrailTarget(
                targets,
                followTarget.kind,
                followTarget.trailKey,
                cinemaState && followTarget.kind === "player"
                    ? TRAIL_MAX_AGE_MS
                    : TRAIL_TARGET_AGE_MS
            );
        }
        if (cinemaState && cinemaState.locked) {
            addVisibleTrailTarget(
                targets,
                "player",
                cinemaState.locked.trailKey,
                TRAIL_MAX_AGE_MS
            );
        }
        if (openPopupTrailTarget) {
            addVisibleTrailTarget(
                targets,
                openPopupTrailTarget.kind,
                openPopupTrailTarget.key,
                TRAIL_TARGET_AGE_MS
            );
        }
        if (layerSettings.trails) {
            latestPlayers.forEach(function (player) {
                addVisibleTrailTarget(
                    targets,
                    "player",
                    player.trailKey,
                    TRAIL_ALL_PLAYERS_AGE_MS
                );
            });
        }

        var timestamp = Date.now();
        targets.forEach(function (target) {
            var buffer = trailBuffers.get(target.key);
            if (buffer && buffer.kind === target.kind) {
                renderTrailBuffer(buffer, target.windowMs, timestamp);
            }
        });
    }

    function bindMapPopupEvents() {
        map.on("popupopen", function (event) {
            var source = event.popup && event.popup._source;
            openPopupPortalId = source && source._voPopupKind === "portal"
                ? source._voEntityId
                : "";
            openPopupTrailTarget = source && source._voTrailKind && source._voTrailKey
                ? { kind: source._voTrailKind, key: source._voTrailKey }
                : null;
            if (openPopupTrailTarget) {
                requestTrailBackfill(
                    openPopupTrailTarget.kind,
                    openPopupTrailTarget.key,
                    1800
                );
            }
            window.clearInterval(popupRefreshTimer);
            window.clearInterval(raidProgressTimer);
            raidProgressTimer = 0;
            refreshOpenPopupFooter();
            popupRefreshTimer = window.setInterval(refreshOpenPopupContent, 5000);
            if (source && source._voPopupKind === "raid") {
                refreshOpenRaidProgress();
                raidProgressTimer = window.setInterval(refreshOpenRaidProgress, 1000);
            }
            renderTrails();
            renderPortalLinks();
        });
        map.on("popupclose", function () {
            openPopupPortalId = "";
            openPopupTrailTarget = null;
            window.clearInterval(popupRefreshTimer);
            popupRefreshTimer = 0;
            window.clearInterval(raidProgressTimer);
            raidProgressTimer = 0;
            renderTrails();
            renderPortalLinks();
        });
    }

    function ensureFollowPill() {
        if (followPill) {
            return;
        }

        followPill = document.createElement("button");
        followPill.type = "button";
        followPill.className = "follow-pill";
        followPill.hidden = true;
        addAppListener(followPill, "click", clearFollow);
        elements.mapPane.appendChild(followPill);
        L.DomEvent.disableClickPropagation(followPill);
    }

    function updateFollowPill() {
        ensureFollowPill();
        var record = followTarget
            ? followTarget.kind === "player"
                ? markerRecords.get(followTarget.id)
                : entityMarkerRecords.get(followTarget.id)
            : null;
        followPill.hidden = !record;
        if (!record) {
            followPill.textContent = "";
        } else if (followTarget.kind === "player") {
            followPill.textContent =
                "Following " + record.player.displayName + " — click to release";
        } else {
            followPill.textContent = "Following " +
                (record.entity.group === "ship"
                    ? shipDisplayName(record.entity.prefab)
                    : "Cart") +
                " — click to release";
        }
    }

    function renderWorldTime(day, timeOfDay) {
        var dayNumber = Number(day);
        var normalizedDay = Number.isFinite(dayNumber)
            ? Math.max(0, Math.floor(dayNumber))
            : null;
        if (normalizedDay !== null) {
            if (currentStatusDay !== null && normalizedDay > currentStatusDay) {
                showDayToast(normalizedDay);
            }
            currentStatusDay = normalizedDay;
        }
        elements.dayNumber.textContent = "Day " +
            (normalizedDay === null ? "—" : normalizedDay);

        var fraction = Number(timeOfDay);
        if (!Number.isFinite(fraction)) {
            currentTimeOfDay = null;
            elements.worldClock.textContent = "--:--";
            updateDayNightTint();
            return;
        }

        fraction = ((fraction % 1) + 1) % 1;
        currentTimeOfDay = fraction;
        var totalMinutes = Math.floor(fraction * 24 * 60);
        var hours = Math.floor(totalMinutes / 60);
        var minutes = totalMinutes % 60;
        elements.worldClock.textContent = padTwo(hours) + ":" + padTwo(minutes);

        var isDaytime = fraction >= 0.15 && fraction < 0.85;
        elements.skyIndicator.textContent = isDaytime ? "☀" : "☾";
        elements.skyIndicator.classList.toggle("is-sun", isDaytime);
        elements.skyIndicator.classList.toggle("is-moon", !isDaytime);
        elements.skyIndicator.setAttribute("aria-label", isDaytime ? "Daytime" : "Nighttime");
        updateDayNightTint();
    }

    function renderBossProgression(globalKeys) {
        var activeKeys = Object.create(null);
        if (Array.isArray(globalKeys)) {
            globalKeys.forEach(function (value) {
                if (typeof value !== "string") {
                    return;
                }

                var key = value.trim().split(/\s+/, 1)[0].toLowerCase();
                if (key) {
                    activeKeys[key] = true;
                }
            });
        }

        var state = BOSS_PROGRESSION.map(function (boss) {
            return activeKeys[boss.key] ? "1" : "0";
        }).join("");
        if (state === bossProgressionState) {
            return;
        }

        bossProgressionState = state;
        elements.bossProgression.textContent = "";
        var defeatedCount = 0;
        BOSS_PROGRESSION.forEach(function (boss) {
            var defeated = activeKeys[boss.key] === true;
            if (defeated) {
                defeatedCount++;
            }

            var chip = document.createElement("span");
            chip.className = "boss-progress-chip" + (defeated ? " is-defeated" : "");
            chip.innerHTML = iconMarkup(boss.iconKey, "◆");
            chip.title = boss.name + " — " + (defeated ? "Defeated" : "Not yet defeated");
            chip.setAttribute("role", "img");
            chip.setAttribute("aria-label", chip.title);
            elements.bossProgression.appendChild(chip);
        });
        elements.bossProgression.setAttribute(
            "aria-label",
            "Boss progression: " + defeatedCount + " of " + BOSS_PROGRESSION.length + " defeated"
        );
    }

    function showDayToast(day) {
        window.clearTimeout(dayToastTimer);
        elements.dayToast.textContent = "Day " + day + " dawns";
        elements.dayToast.hidden = false;
        elements.dayToast.classList.remove("is-visible");
        void elements.dayToast.offsetWidth;
        elements.dayToast.classList.add("is-visible");
        dayToastTimer = window.setTimeout(function () {
            elements.dayToast.classList.remove("is-visible");
            elements.dayToast.hidden = true;
            dayToastTimer = 0;
        }, DAY_TOAST_DURATION_MS);
    }

    function showNoticeToast(message) {
        window.clearTimeout(noticeToastTimer);
        noticeToastElement.textContent = message;
        noticeToastElement.hidden = false;
        noticeToastElement.classList.remove("is-visible");
        void noticeToastElement.offsetWidth;
        noticeToastElement.classList.add("is-visible");
        noticeToastTimer = window.setTimeout(function () {
            noticeToastElement.classList.remove("is-visible");
            noticeToastElement.hidden = true;
            noticeToastTimer = 0;
        }, NOTICE_TOAST_DURATION_MS);
    }

    function formatSavedAge(ageMs) {
        var minutes = Math.floor(Math.max(0, ageMs) / 60000);
        if (minutes < 1) {
            return "just now";
        }
        if (minutes < 60) {
            return minutes + "m ago";
        }

        var hours = Math.floor(minutes / 60);
        if (hours < 24) {
            return hours + "h ago";
        }

        return Math.floor(hours / 24) + "d ago";
    }

    function renderSavedBadge() {
        if (!(lastSavedUnixMs > 0)) {
            elements.savedChip.hidden = true;
            syncStatusChipsVisibility();
            return;
        }

        var ageMs = Math.max(0, Date.now() - lastSavedUnixMs);
        var label = "Saved " + formatSavedAge(ageMs);
        elements.savedLabel.textContent = label;
        elements.savedChip.setAttribute("aria-label", label);
        elements.savedChip.classList.toggle("is-stale", ageMs > SAVED_STALE_MS);
        elements.savedChip.hidden = false;
        syncStatusChipsVisibility();
    }

    function updateLastSaved(value) {
        var timestamp = finiteNumberOrNull(value);
        lastSavedUnixMs = timestamp !== null && timestamp > 0 ? timestamp : 0;
        renderSavedBadge();
    }

    function syncStatusChipsVisibility() {
        elements.statusChips.hidden = elements.windChip.hidden &&
            elements.exploredChip.hidden && elements.savedChip.hidden;
    }

    function renderWindStatus() {
        if (!latestWind) {
            elements.windChip.hidden = true;
            syncStatusChipsVisibility();
            return;
        }

        var direction = compassLabel(latestWind.fromDeg);
        var intensityPct = Math.round(latestWind.intensity * 100);
        var sidebarWindLabel = "Wind " + direction + " " + intensityPct + "%";
        elements.sidebarWindLabel.textContent = sidebarWindLabel;
        elements.windChip.setAttribute("aria-label", sidebarWindLabel);
        elements.windChip.hidden = false;

        // The server reports where wind comes from; the needle points where it blows toward.
        var towardDeg = (latestWind.fromDeg + 180) % 360;
        var scale = 0.3 + (0.7 * latestWind.intensity);
        var windTitle = "Wind from " + direction + " · " + intensityPct + "%";
        elements.sidebarWindNeedle.style.opacity = String(0.45 + (0.55 * latestWind.intensity));
        elements.sidebarWindNeedle.style.transform = "rotate(" + towardDeg.toFixed(1) + "deg)";
        syncStatusChipsVisibility();
        if (compassButton && compassWindNeedle) {
            compassWindNeedle.style.opacity = String(0.3 + (0.7 * latestWind.intensity));
            compassWindNeedle.style.transform = "rotate(" + towardDeg.toFixed(1) +
                "deg) scaleY(" + scale.toFixed(3) + ")";
            compassWindNeedle.classList.add("is-visible");
            compassButton.title = windTitle;
            compassButton.setAttribute("aria-label", windTitle);
        }
        if (cinemaWindNeedle && elements.cinemaWind) {
            cinemaWindNeedle.style.opacity = String(0.3 + (0.7 * latestWind.intensity));
            cinemaWindNeedle.style.transform = "rotate(" + towardDeg.toFixed(1) +
                "deg) scaleY(" + scale.toFixed(3) + ")";
            cinemaWindNeedle.classList.add("is-visible");
            elements.cinemaWind.title = windTitle;
            elements.cinemaWindLabel.textContent = direction + " " + intensityPct + "%";
        }
    }

    function updateWorldMetrics(status) {
        var exploredPct = finiteNumberOrNull(status.exploredPct);
        if (exploredPct !== null) {
            exploredPct = Math.max(0, Math.min(100, exploredPct));
            var exploredLabel = "Explored " + exploredPct.toFixed(1) + "%";
            elements.exploredLabel.textContent = exploredLabel;
            elements.exploredChip.setAttribute("aria-label", exploredLabel);
            elements.exploredChip.hidden = false;
        }

        var windDirDeg = finiteNumberOrNull(status.windDirDeg);
        var windIntensity = finiteNumberOrNull(status.windIntensity);
        if (windDirDeg === null || windIntensity === null) {
            syncStatusChipsVisibility();
            return;
        }

        latestWind = {
            fromDeg: ((windDirDeg % 360) + 360) % 360,
            intensity: Math.max(0, Math.min(1, windIntensity))
        };
        renderWindStatus();
    }

    function tintOpacityForTime(fraction) {
        if (!Number.isFinite(fraction)) {
            return 0;
        }
        if (Math.abs(fraction - 0.15) <= 0.04 || Math.abs(fraction - 0.85) <= 0.04) {
            return 0.06;
        }
        return fraction < 0.15 || fraction >= 0.85 ? 0.12 : 0;
    }

    function updateDayNightTint() {
        if (!tintOverlay) {
            return;
        }
        tintOverlay.setStyle({
            fillColor: "#0d1626",
            fillOpacity: tintOpacityForTime(currentTimeOfDay)
        });
    }

    function padTwo(value) {
        return value < 10 ? "0" + value : String(value);
    }

    function renderPlayerCount(count) {
        var numericCount = Number(count);
        latestPlayerCount = Number.isFinite(numericCount) ? Math.max(0, Math.floor(numericCount)) : 0;
        elements.playerCount.textContent = latestPlayerCount + " online";
    }

    function updateRenderStatus(mapStatus) {
        if (pollCircuitOpen) {
            showConnectionLostState();
            return;
        }
        if (renderStatusFailureTimer) {
            return;
        }
        if (!initialMapLoadingComplete && mapStatus &&
            (mapStatus.state === "ready" || mapStatus.state === "failed")) {
            initialMapLoadingComplete = true;
            initialMapLoadingTimedOut = false;
            window.clearTimeout(mapLoadingTimeoutTimer);
            mapLoadingTimeoutTimer = 0;
        }
        if (initialMapLoadingTimedOut && !initialMapLoadingComplete) {
            elements.mapStatus.hidden = false;
            elements.mapStatus.querySelector(".spinner").hidden = true;
            elements.mapStatusText.textContent = "World map loading timed out — reload to retry";
            return;
        }
        if (mapStatus && mapStatus.state === "ready") {
            var requestedStyle = sanitizeMapStyle(layerSettings.mapStyle);
            var requestedStatus = mapStyleStatus(mapStatus, requestedStyle);
            if (requestedStyle === "default" ||
                (requestedStatus && requestedStatus.state === "ready")) {
                elements.mapStatus.hidden = true;
                return;
            }

            elements.mapStatus.hidden = false;
            var styleSpinner = elements.mapStatus.querySelector(".spinner");
            styleSpinner.hidden = false;
            var styleProgress = requestedStatus ? Number(requestedStatus.progress) : 0;
            var stylePercentage = Number.isFinite(styleProgress)
                ? Math.round(Math.max(0, Math.min(1, styleProgress)) * 100)
                : 0;
            elements.mapStatusText.textContent = "Rendering " +
                mapStyleRenderLabel(requestedStyle) + " — " + stylePercentage + "%";
            return;
        }

        elements.mapStatus.hidden = false;
        var spinner = elements.mapStatus.querySelector(".spinner");
        var failed = mapStatus && mapStatus.state === "failed";
        spinner.hidden = failed;
        if (failed) {
            elements.mapStatusText.textContent = "World map rendering failed";
            return;
        }

        var progress = mapStatus ? Number(mapStatus.progress) : 0;
        var percentage = Number.isFinite(progress)
            ? Math.round(Math.max(0, Math.min(1, progress)) * 100)
            : 0;
        elements.mapStatusText.textContent = "Rendering world map — " + percentage + "%";
    }

    function startMapLoadingTimeout() {
        window.clearTimeout(mapLoadingTimeoutTimer);
        mapLoadingTimeoutTimer = window.setTimeout(function () {
            mapLoadingTimeoutTimer = 0;
            if (initialMapLoadingComplete || pollCircuitOpen) {
                return;
            }
            initialMapLoadingTimedOut = true;
            updateRenderStatus(latestMapStatus);
        }, MAP_LOADING_TIMEOUT_MS);
    }

    function calculateMaximumZoom(textureSize) {
        return Math.max(0, Math.round(Math.log(textureSize / TILE_SIZE) / Math.LN2));
    }

    function readMapMetrics(statusMap) {
        var textureSize = Number(statusMap.textureSize);
        var pixelSize = Number(statusMap.pixelSize);
        if (!Number.isFinite(textureSize) || textureSize < TILE_SIZE ||
            !Number.isFinite(pixelSize) || pixelSize <= 0) {
            return null;
        }

        var overviewZoom = Number(statusMap.baseZoom);
        if (!Number.isFinite(overviewZoom) || overviewZoom < 0) {
            overviewZoom = calculateMaximumZoom(textureSize);
        }
        var maximumZoom = Number(statusMap.maxZoom);
        if (!Number.isFinite(maximumZoom) || maximumZoom < overviewZoom) {
            maximumZoom = overviewZoom;
        }
        var worldRadius = Number(statusMap.worldRadius);
        if (!Number.isFinite(worldRadius) || worldRadius <= 0) {
            worldRadius = textureSize * pixelSize / 2;
        }
        return {
            textureSize: textureSize,
            pixelSize: pixelSize,
            worldRadius: worldRadius,
            overviewZoom: overviewZoom,
            maximumZoom: maximumZoom,
            unitsPerPixel: WORLD_UNITS / textureSize
        };
    }

    function reconcileMapMetrics(nextMetrics) {
        if (nextMetrics.maximumZoom === mapMetrics.maximumZoom &&
            nextMetrics.overviewZoom === mapMetrics.baseZoom &&
            nextMetrics.textureSize === mapMetrics.textureSize &&
            nextMetrics.pixelSize === mapMetrics.pixelSize &&
            nextMetrics.worldRadius === mapMetrics.worldRadius) {
            return;
        }

        var center = map.getCenter();
        var zoom = Math.max(map.getMinZoom(), Math.min(nextMetrics.maximumZoom, map.getZoom()));
        mapMetrics.textureSize = nextMetrics.textureSize;
        mapMetrics.pixelSize = nextMetrics.pixelSize;
        mapMetrics.worldRadius = nextMetrics.worldRadius;
        mapMetrics.baseZoom = nextMetrics.overviewZoom;
        mapMetrics.maximumZoom = nextMetrics.maximumZoom;
        mapMetrics.unitsPerPixel = nextMetrics.unitsPerPixel;
        map.setMaxZoom(nextMetrics.maximumZoom);
        tileLayer.options.maxZoom = nextMetrics.maximumZoom;
        tileLayer.options.maxNativeZoom = nextMetrics.maximumZoom;
        tileLayer.removeFrom(map);
        tileLayer.addTo(map);
        map.setView(center, zoom, { animate: false });
        updateScaleBar();
        scheduleMinimapUpdate();
        updateRegionLayerVisibility();
    }

    function ensureMap(statusMap) {
        if (!statusMap || statusMap.state !== "ready") {
            return;
        }

        var nextMetrics = readMapMetrics(statusMap);
        if (!nextMetrics) {
            return;
        }
        if (tileLayer) {
            reconcileMapMetrics(nextMetrics);
            return;
        }

        var overviewZoom = nextMetrics.overviewZoom;
        var maximumZoom = nextMetrics.maximumZoom;
        mapMetrics = {
            baseZoom: overviewZoom,
            textureSize: nextMetrics.textureSize,
            pixelSize: nextMetrics.pixelSize,
            worldRadius: nextMetrics.worldRadius,
            maximumZoom: maximumZoom,
            unitsPerPixel: nextMetrics.unitsPerPixel
        };

        worldBounds = L.latLngBounds([[-WORLD_UNITS, 0], [0, WORLD_UNITS]]);
        map = L.map(embedMode ? elements.mapPane : "map", {
            attributionControl: false,
            crs: L.CRS.Simple,
            maxBounds: worldBounds.pad(0.08),
            maxBoundsViscosity: 0.72,
            maxZoom: maximumZoom,
            minZoom: 0,
            zoomControl: true,
            zoomDelta: 0.5,
            zoomSnap: 0.25
        });

        map.createPane("basePane");
        map.getPane("basePane").style.zIndex = "190";
        map.getPane("basePane").style.pointerEvents = "none";
        map.createPane("heatmapPane");
        map.getPane("heatmapPane").style.zIndex = "340";
        map.getPane("heatmapPane").style.pointerEvents = "none";
        map.createPane("fogPane");
        map.getPane("fogPane").style.zIndex = "350";
        map.getPane("fogPane").style.pointerEvents = "none";
        map.createPane("timelapsePane");
        map.getPane("timelapsePane").style.zIndex = "355";
        map.getPane("timelapsePane").style.pointerEvents = "none";
        map.createPane("tintPane");
        map.getPane("tintPane").style.zIndex = "360";
        map.getPane("tintPane").style.pointerEvents = "none";
        map.createPane("regionPane");
        map.getPane("regionPane").style.zIndex = "370";
        map.getPane("regionPane").style.pointerEvents = "none";
        map.createPane("trailPane");
        map.getPane("trailPane").style.zIndex = "380";
        map.getPane("trailPane").style.pointerEvents = "none";
        map.createPane("baseAreaPane");
        map.getPane("baseAreaPane").style.zIndex = "385";
        map.getPane("baseAreaPane").style.pointerEvents = "none";
        map.createPane("wardRadiusPane");
        map.getPane("wardRadiusPane").style.zIndex = "390";
        map.getPane("wardRadiusPane").style.pointerEvents = "none";
        map.createPane("poiPane");
        map.getPane("poiPane").style.zIndex = "595";

        applyInitialHashState(Math.max(0, overviewZoom - 1));
        var initialStyle = sanitizeMapStyle(layerSettings.mapStyle);
        var initialStyleStatus = mapStyleStatus(statusMap, initialStyle);
        displayedMapStyle = initialStyle !== "default" && initialStyleStatus &&
            initialStyleStatus.state === "ready" ? initialStyle : "default";

        // With fog active, keep an ocean-colored cover over the pane until the
        // fog image has loaded so the unfogged world never flashes on first paint.
        if (fogAvailable) {
            showFogCover();
        }

        refreshBaseOverlay();

        var tileTemplate = versionedMapUrl("/tiles/{z}/{x}-{y}.png");
        tileLayer = L.tileLayer(tileTemplate, {
            bounds: worldBounds,
            className: "world-map-layer",
            maxNativeZoom: maximumZoom,
            maxZoom: maximumZoom,
            minZoom: 0,
            noWrap: true,
            tileSize: TILE_SIZE
        });

        tileLayer.addTo(map);
        tintOverlay = L.rectangle(worldBounds, {
            className: "tint-overlay",
            fill: true,
            fillColor: "#0d1626",
            fillOpacity: tintOpacityForTime(currentTimeOfDay),
            interactive: false,
            pane: "tintPane",
            stroke: false
        });
        heatmapLayer = createActivityHeatmapLayer();
        initialiseDataLayers();
        bindMapPopupEvents();
        ensureFollowPill();
        createLayersControl();
        probeTimelapseAvailability();
        createHeatmapLegendControl();
        createCompassControl();
        createScaleBarControl();
        createMeasureControl();
        createPingControl();
        bindShipTowInteraction();
        createFullscreenControl();
        createSearchControl();
        createCoordinateControl();
        bindMapContextMenu();
        createMinimapControl();
        applyDensityPreferences();
        applyPoiPreferences();
        applyPoiZoomGates();
        map.on("dragstart", function () {
            if (cinemaState) {
                cinemaPauseAmbientForUser();
                return;
            }
            clearFollow();
        });
        var mapContainer = map.getContainer();
        ["pointerdown", "touchstart", "wheel"].forEach(function (eventName) {
            addAppListener(mapContainer, eventName, cinemaPauseAmbientForUser, {
                passive: true
            });
        });
        addAppListener(mapContainer, "keydown", function (event) {
            if (["ArrowDown", "ArrowLeft", "ArrowRight", "ArrowUp", "+", "-"].indexOf(
                event.key
            ) !== -1) {
                cinemaPauseAmbientForUser();
            }
        });
        overviewClusterRenderZoom = map.getZoom();
        map.on("moveend", renderOverviewClustersAfterMove);
        map.on("zoomend", applyPoiZoomGates);
        map.on("zoomend", updateRegionLayerVisibility);
        map.on("moveend zoomend", scheduleHashUpdate);
        syncLayerVisibility();
        updatePlayerMarkers(latestPlayers);
        applyInitialPlayersView();
        applyPendingHashFollow();
        loadPoisForCurrentView();
        loadRegions();
        applyFogStatus();
        applyRaidEvent(currentRaidEvent);
        ensureEntityFeed();
        startPinsPolling();
        startWebPinsPolling();
    }

    function initialiseDataLayers() {
        playerLayer = L.layerGroup();
        pinLayer = L.layerGroup();
        webPinLayer = L.layerGroup();
        regionLayer = L.layerGroup();
        trailLayer = L.layerGroup().addTo(map);
        shipHeadingLayer = L.layerGroup();
        portalNetworkLayer = L.layerGroup();
        portalPopupLinkLayer = L.layerGroup().addTo(map);
        wardRadiusLayer = L.layerGroup();
        pingLayer = L.layerGroup().addTo(map);
        chatLayer = L.layerGroup().addTo(map);
        timelapseFogLayer = createTimelapseFogLayer();
        timelapseMovementLayer = createActivityHeatmapLayer({
            canvasClass: "timelapse-movement-canvas",
            maximumOpacity: 0.64,
            minimumOpacity: 0.04,
            pane: "timelapsePane",
            reuseSource: true
        });
        timelapseMarkerLayer = L.layerGroup();
        POI_GROUP_ORDER.forEach(function (group) {
            poiLayers.set(group, L.layerGroup());
            poiRecords.set(group, []);
        });
        ENTITY_GROUP_ORDER.forEach(function (group) {
            entityLayers.set(group, L.layerGroup());
        });
        renderWebPins();
        pendingMapPings.splice(0).forEach(renderMapPing);
        pendingChatBubbles.splice(0).forEach(renderChatBubble);
    }

    function worldToLatLng(worldX, worldZ) {
        if (!mapMetrics) {
            return L.latLng(0, 0);
        }

        var pixelX = worldX / mapMetrics.pixelSize + mapMetrics.textureSize / 2;
        var pixelYFromNorth = mapMetrics.textureSize / 2 - worldZ / mapMetrics.pixelSize;
        return L.latLng(
            -pixelYFromNorth * mapMetrics.unitsPerPixel,
            pixelX * mapMetrics.unitsPerPixel
        );
    }

    function latLngToWorld(latLng) {
        if (!mapMetrics || !latLng) {
            return null;
        }

        // Exact inverse of worldToLatLng: recover source pixels, then world meters.
        return {
            x: (latLng.lng / mapMetrics.unitsPerPixel - mapMetrics.textureSize / 2) *
                mapMetrics.pixelSize,
            z: (mapMetrics.textureSize / 2 - (-latLng.lat / mapMetrics.unitsPerPixel)) *
                mapMetrics.pixelSize
        };
    }

    function createActivityHeatmapLayer(options) {
        options = options || {};
        var canvasClass = options.canvasClass || "activity-heatmap-canvas";
        var paneName = options.pane || "heatmapPane";
        var reuseSource = options.reuseSource === true;
        var minimumOpacity = Number.isFinite(options.minimumOpacity)
            ? options.minimumOpacity
            : 0.10;
        var maximumOpacity = Number.isFinite(options.maximumOpacity)
            ? options.maximumOpacity
            : 0.90;
        var HeatmapLayer = L.Layer.extend({
            initialize: function () {
                this._canvas = null;
                this._source = null;
                this._reusableSource = null;
                this._payload = null;
            },
            onAdd: function (activeMap) {
                this._canvas = L.DomUtil.create(
                    "canvas",
                    canvasClass,
                    activeMap.getPane(paneName)
                );
                activeMap.on("moveend zoomend resize", this._redraw, this);
                this._redraw();
            },
            onRemove: function (activeMap) {
                activeMap.off("moveend zoomend resize", this._redraw, this);
                if (this._canvas && this._canvas.parentNode) {
                    this._canvas.parentNode.removeChild(this._canvas);
                }
                this._canvas = null;
            },
            setData: function (payload) {
                this._payload = payload;
                if (payload) {
                    this._source = buildHeatmapSource(
                        payload,
                        reuseSource ? this._reusableSource : null,
                        minimumOpacity,
                        maximumOpacity
                    );
                    if (reuseSource) {
                        this._reusableSource = this._source;
                    }
                } else {
                    this._source = null;
                }
                this._redraw();
            },
            _redraw: function () {
                if (!this._map || !this._canvas) {
                    return;
                }

                var size = this._map.getSize();
                var ratio = Math.min(2, window.devicePixelRatio || 1);
                var width = Math.max(1, Math.round(size.x * ratio));
                var height = Math.max(1, Math.round(size.y * ratio));
                if (this._canvas.width !== width || this._canvas.height !== height) {
                    this._canvas.width = width;
                    this._canvas.height = height;
                    this._canvas.style.width = size.x + "px";
                    this._canvas.style.height = size.y + "px";
                }

                L.DomUtil.setPosition(
                    this._canvas,
                    this._map.containerPointToLayerPoint([0, 0])
                );
                var context = this._canvas.getContext("2d");
                context.setTransform(1, 0, 0, 1, 0, 0);
                context.clearRect(0, 0, width, height);
                if (!this._source || !this._payload) {
                    return;
                }

                var radius = this._payload.worldRadius;
                var northWest = this._map.latLngToContainerPoint(
                    worldToLatLng(-radius, radius)
                );
                var southEast = this._map.latLngToContainerPoint(
                    worldToLatLng(radius, -radius)
                );
                context.imageSmoothingEnabled = true;
                context.imageSmoothingQuality = "high";
                context.drawImage(
                    this._source,
                    northWest.x * ratio,
                    northWest.y * ratio,
                    (southEast.x - northWest.x) * ratio,
                    (southEast.y - northWest.y) * ratio
                );
            }
        });
        return new HeatmapLayer();
    }

    function createTimelapseFogLayer() {
        var fogSize = 512;
        var fogCellCount = fogSize * fogSize;
        var FogLayer = L.Layer.extend({
            initialize: function () {
                this._canvas = null;
                this._source = document.createElement("canvas");
                this._source.width = fogSize;
                this._source.height = fogSize;
                this._sourceContext = this._source.getContext("2d");
                this._imageData = this._sourceContext.createImageData(fogSize, fogSize);
                this._hasData = false;
                for (var cell = 0; cell < fogCellCount; cell++) {
                    var offset = cell * 4;
                    this._imageData.data[offset] = 18;
                    this._imageData.data[offset + 1] = 14;
                    this._imageData.data[offset + 2] = 10;
                }
            },
            onAdd: function (activeMap) {
                this._canvas = L.DomUtil.create(
                    "canvas",
                    "timelapse-fog-canvas",
                    activeMap.getPane("timelapsePane")
                );
                activeMap.on("moveend zoomend resize", this._redraw, this);
                this._redraw();
            },
            onRemove: function (activeMap) {
                activeMap.off("moveend zoomend resize", this._redraw, this);
                if (this._canvas && this._canvas.parentNode) {
                    this._canvas.parentNode.removeChild(this._canvas);
                }
                this._canvas = null;
            },
            setFrame: function (frame) {
                this.setData(frame && Array.isArray(frame.fog) ? frame.fog : null);
            },
            setData: function (runs) {
                if (!runs) {
                    this._hasData = false;
                    this._redraw();
                    return;
                }

                var total = 0;
                for (var index = 0; index < runs.length; index++) {
                    var runLength = Number(runs[index]);
                    if (!Number.isInteger(runLength) || runLength < 0 ||
                        total + runLength > fogCellCount) {
                        this._hasData = false;
                        this._redraw();
                        return;
                    }
                    total += runLength;
                }
                if (total !== fogCellCount) {
                    this._hasData = false;
                    this._redraw();
                    return;
                }

                var explored = false;
                var cellIndex = 0;
                for (var runIndex = 0; runIndex < runs.length; runIndex++) {
                    var end = cellIndex + Number(runs[runIndex]);
                    var alpha = explored ? 0 : 209;
                    while (cellIndex < end) {
                        this._imageData.data[(cellIndex * 4) + 3] = alpha;
                        cellIndex++;
                    }
                    explored = !explored;
                }
                this._sourceContext.putImageData(this._imageData, 0, 0);
                this._hasData = true;
                this._redraw();
            },
            _redraw: function () {
                if (!this._map || !this._canvas) {
                    return;
                }

                var size = this._map.getSize();
                var ratio = Math.min(2, window.devicePixelRatio || 1);
                var width = Math.max(1, Math.round(size.x * ratio));
                var height = Math.max(1, Math.round(size.y * ratio));
                if (this._canvas.width !== width || this._canvas.height !== height) {
                    this._canvas.width = width;
                    this._canvas.height = height;
                    this._canvas.style.width = size.x + "px";
                    this._canvas.style.height = size.y + "px";
                }

                L.DomUtil.setPosition(
                    this._canvas,
                    this._map.containerPointToLayerPoint([0, 0])
                );
                var context = this._canvas.getContext("2d");
                context.setTransform(1, 0, 0, 1, 0, 0);
                context.clearRect(0, 0, width, height);
                if (!this._hasData || !worldBounds) {
                    return;
                }

                var northWest = this._map.latLngToContainerPoint(
                    worldBounds.getNorthWest()
                );
                var southEast = this._map.latLngToContainerPoint(
                    worldBounds.getSouthEast()
                );
                context.imageSmoothingEnabled = false;
                context.drawImage(
                    this._source,
                    northWest.x * ratio,
                    northWest.y * ratio,
                    (southEast.x - northWest.x) * ratio,
                    (southEast.y - northWest.y) * ratio
                );
            }
        });
        return new FogLayer();
    }

    function themeColor(variableName, fallback) {
        var value = window.getComputedStyle(styleRoot)
            .getPropertyValue(variableName).trim();
        return value || fallback;
    }

    function themeRgb(variableName) {
        var value = window.getComputedStyle(styleRoot)
            .getPropertyValue(variableName).trim();
        var hex = value.match(/^#([0-9a-f]{6})$/i);
        if (hex) {
            return [
                parseInt(hex[1].slice(0, 2), 16),
                parseInt(hex[1].slice(2, 4), 16),
                parseInt(hex[1].slice(4, 6), 16)
            ];
        }

        var rgb = value.match(/^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/i);
        return rgb
            ? [Number(rgb[1]), Number(rgb[2]), Number(rgb[3])]
            : null;
    }

    function mixRgb(from, to, amount) {
        return [
            Math.round(from[0] + (to[0] - from[0]) * amount),
            Math.round(from[1] + (to[1] - from[1]) * amount),
            Math.round(from[2] + (to[2] - from[2]) * amount)
        ];
    }

    function buildHeatmapSource(
        payload,
        reusableCanvas,
        minimumOpacity,
        maximumOpacity
    ) {
        var canvas = reusableCanvas || document.createElement("canvas");
        if (canvas.width !== payload.size || canvas.height !== payload.size) {
            canvas.width = payload.size;
            canvas.height = payload.size;
            canvas._voHeatmapImageData = null;
        }
        var context = canvas.getContext("2d");
        var image = canvas._voHeatmapImageData;
        if (!image) {
            image = context.createImageData(payload.size, payload.size);
            canvas._voHeatmapImageData = image;
        } else {
            image.data.fill(0);
        }
        var lowColor = themeRgb("--accent");
        var middleColor = themeRgb("--sun");
        var highColor = themeRgb("--raid");
        minimumOpacity = Number.isFinite(minimumOpacity) ? minimumOpacity : 0.10;
        maximumOpacity = Number.isFinite(maximumOpacity) ? maximumOpacity : 0.90;
        if (!lowColor || !middleColor || !highColor || payload.maxCount <= 0) {
            return canvas;
        }

        var maximumLog = Math.log1p(payload.maxCount);
        payload.cells.forEach(function (cell) {
            var intensity = maximumLog > 0 ? Math.log1p(cell[2]) / maximumLog : 0;
            intensity = Math.max(0, Math.min(1, intensity));
            var color = intensity < 0.5
                ? mixRgb(lowColor, middleColor, intensity * 2)
                : mixRgb(middleColor, highColor, (intensity - 0.5) * 2);
            var imageY = payload.size - cell[1] - 1;
            var offset = ((imageY * payload.size) + cell[0]) * 4;
            image.data[offset] = color[0];
            image.data[offset + 1] = color[1];
            image.data[offset + 2] = color[2];
            image.data[offset + 3] = Math.round(
                (minimumOpacity + ((maximumOpacity - minimumOpacity) *
                    Math.pow(intensity, 1.15))) * 255
            );
        });
        context.putImageData(image, 0, 0);
        return canvas;
    }

    function createHeatmapLegendControl() {
        var HeatmapLegend = L.Control.extend({
            options: { position: "bottomright" },
            onAdd: function () {
                var container = L.DomUtil.create(
                    "div",
                    "leaflet-control activity-heatmap-legend"
                );
                var low = document.createElement("span");
                var ramp = document.createElement("span");
                var high = document.createElement("span");
                low.textContent = "Low";
                ramp.className = "activity-heatmap-ramp";
                ramp.setAttribute("aria-hidden", "true");
                high.textContent = "High";
                container.setAttribute("aria-label", "Activity heatmap intensity, low to high");
                container.appendChild(low);
                container.appendChild(ramp);
                container.appendChild(high);
                container.hidden = true;
                heatmapLegendElement = container;
                L.DomEvent.disableClickPropagation(container);
                L.DomEvent.disableScrollPropagation(container);
                return container;
            }
        });
        new HeatmapLegend().addTo(map);
        syncHeatmapControls();
    }

    function normalizeHeatmap(payload, requestedWindow) {
        if (!payload || payload.window !== requestedWindow ||
            Number(payload.size) !== 128 ||
            !Number.isFinite(Number(payload.worldRadius)) ||
            Number(payload.worldRadius) <= 0 ||
            !Number.isFinite(Number(payload.maxCount)) ||
            Number(payload.maxCount) < 0 ||
            !Number.isFinite(Number(payload.generatedUnixMs)) ||
            !Array.isArray(payload.cells) || payload.cells.length > 128 * 128) {
            return null;
        }

        var cells = [];
        var seen = new Set();
        var maximumCount = 0;
        for (var index = 0; index < payload.cells.length; index++) {
            var cell = payload.cells[index];
            if (!Array.isArray(cell) || cell.length !== 3) {
                return null;
            }
            var ix = Number(cell[0]);
            var iz = Number(cell[1]);
            var count = Number(cell[2]);
            var key = ix + ":" + iz;
            if (!Number.isInteger(ix) || ix < 0 || ix >= 128 ||
                !Number.isInteger(iz) || iz < 0 || iz >= 128 ||
                !Number.isInteger(count) || count <= 0 || seen.has(key)) {
                return null;
            }
            seen.add(key);
            maximumCount = Math.max(maximumCount, count);
            cells.push([ix, iz, count]);
        }

        return {
            window: requestedWindow,
            size: 128,
            worldRadius: Number(payload.worldRadius),
            maxCount: Math.max(maximumCount, Math.floor(Number(payload.maxCount))),
            generatedUnixMs: Math.floor(Number(payload.generatedUnixMs)),
            cells: cells
        };
    }

    function heatmapIsEnabled() {
        return hasLiveAccess() && !timelapseIsActive() &&
            layerSettings.heatmap === true &&
            Boolean(map && heatmapLayer);
    }

    function scheduleHeatmapPoll(delay) {
        window.clearTimeout(heatmapPollTimer);
        heatmapPollTimer = 0;
        if (!heatmapIsEnabled() || pollCircuitOpen) {
            return;
        }
        heatmapPollTimer = window.setTimeout(function () {
            heatmapPollTimer = 0;
            pollHeatmap();
        }, delay);
    }

    function startHeatmapPolling() {
        if (!heatmapIsEnabled() || pollCircuitOpen || heatmapPollTimer ||
            heatmapRequestPending) {
            return;
        }
        scheduleHeatmapPoll(0);
    }

    function stopHeatmapPolling() {
        if (heatmapPollTimer || heatmapRequestPending) {
            heatmapRequestSequence++;
        }
        window.clearTimeout(heatmapPollTimer);
        heatmapPollTimer = 0;
        recordPollSuccess("heatmap");
        setFeedState("heatmap", true);
    }

    async function pollHeatmap() {
        if (!heatmapIsEnabled() || heatmapRequestPending || document.hidden ||
            pollCircuitOpen) {
            return;
        }

        heatmapRequestPending = true;
        var requestedWindow = layerSettings.heatmapWindow;
        var sequence = ++heatmapRequestSequence;
        try {
            var payload = await fetchJson(
                "/api/heatmap?window=" + encodeURIComponent(requestedWindow)
            );
            if (sequence !== heatmapRequestSequence || !heatmapIsEnabled() ||
                requestedWindow !== layerSettings.heatmapWindow) {
                return;
            }
            var normalized = normalizeHeatmap(payload, requestedWindow);
            if (!normalized) {
                throw new Error("Invalid heatmap response");
            }
            latestHeatmap = normalized;
            heatmapLayer.setData(normalized);
            feedLastUpdated.heatmap = Date.now();
            recordPollSuccess("heatmap");
            setFeedState("heatmap", true);
        } catch (error) {
            if (sequence === heatmapRequestSequence && heatmapIsEnabled()) {
                recordPollFailure("heatmap");
                setFeedState("heatmap", false);
            }
        } finally {
            heatmapRequestPending = false;
            if (heatmapIsEnabled()) {
                var staleRequest = sequence !== heatmapRequestSequence ||
                    requestedWindow !== layerSettings.heatmapWindow;
                scheduleHeatmapPoll(staleRequest ? 0 : HEATMAP_POLL_INTERVAL_MS);
            }
        }
    }

    function timelapseIsActive() {
        return appRoot.classList.contains("is-timelapse");
    }

    function timelapseHasAccess() {
        return currentView === "admin" || currentView === "shared";
    }

    function normalizeTimelapseIndex(payload) {
        if (!payload || !Array.isArray(payload.frames)) {
            return null;
        }

        var frames = [];
        var seen = new Set();
        for (var index = 0; index < payload.frames.length; index++) {
            var source = payload.frames[index];
            var timestamp = source && Number(source.t);
            if (!Number.isSafeInteger(timestamp) || timestamp <= 0 || seen.has(timestamp)) {
                return null;
            }
            seen.add(timestamp);
            frames.push({
                bases: Math.max(0, Math.floor(Number(source.bases) || 0)),
                beds: Math.max(0, Math.floor(Number(source.beds) || 0)),
                bossMask: Math.max(0, Math.floor(Number(source.bossMask) || 0)),
                day: Math.max(0, Math.floor(Number(source.day) || 0)),
                exploredCells: Math.max(
                    0,
                    Math.floor(Number(source.exploredCells) || 0)
                ),
                exploredPct: Math.max(0, Number(source.exploredPct) || 0),
                portals: Math.max(0, Math.floor(Number(source.portals) || 0)),
                t: timestamp,
                wards: Math.max(0, Math.floor(Number(source.wards) || 0))
            });
        }
        frames.sort(function (left, right) {
            return left.t - right.t;
        });
        return {
            frames: frames,
            intervalMinutes: Math.max(0, Math.floor(Number(payload.intervalMinutes) || 0))
        };
    }

    function syncTimelapseLayerRow() {
        if (!layersRows) {
            return;
        }
        layersRows.querySelectorAll(".timelapse-layer-row").forEach(function (row) {
            if (!timelapseHasAccess() || timelapseAvailability !== "available") {
                row.remove();
                return;
            }
            var checkbox = row.querySelector('input[data-layer-key="timelapse"]');
            if (checkbox) {
                checkbox.checked = layerSettings.timelapse === true;
            }
        });
        updateLayerCounts();
    }

    function markTimelapseUnavailable() {
        timelapseAvailability = "unavailable";
        timelapseIndex = null;
        var settingsChanged = layerSettings.timelapse === true;
        layerSettings.timelapse = false;
        if (settingsChanged) {
            saveLayerSettings();
        }
        deactivateTimelapse();
        syncTimelapseLayerRow();
    }

    function ensureTimelapseIndex() {
        if (destroyed || !timelapseHasAccess() ||
            timelapseAvailability === "unavailable") {
            return Promise.resolve(null);
        }
        if (timelapseIndex) {
            return Promise.resolve(timelapseIndex);
        }
        if (timelapseIndexPromise) {
            return timelapseIndexPromise;
        }

        timelapseIndexPromise = fetchJson("/api/timelapse").then(function (payload) {
            if (destroyed || !timelapseHasAccess()) {
                timelapseIndexPromise = null;
                return null;
            }
            var normalized = normalizeTimelapseIndex(payload);
            if (normalized && normalized.frames.length === 0) {
                markTimelapseUnavailable();
                return null;
            }
            if (!normalized) {
                throw new Error("Invalid timelapse index response");
            }
            timelapseIndex = normalized;
            timelapseAvailability = "available";
            syncTimelapseLayerRow();
            return normalized;
        }).catch(function (error) {
            if (destroyed || !timelapseHasAccess()) {
                timelapseIndexPromise = null;
                return null;
            }
            if (error && (error.status === 403 || error.status === 404)) {
                markTimelapseUnavailable();
            } else {
                timelapseIndexPromise = null;
            }
            return null;
        });
        return timelapseIndexPromise;
    }

    function probeTimelapseAvailability() {
        if (!map || !timelapseHasAccess() ||
            timelapseAvailability === "unavailable") {
            return;
        }
        ensureTimelapseIndex().then(function (index) {
            if (destroyed || !map || !timelapseHasAccess()) {
                return;
            }
            renderLayerRows();
            if (index && layerSettings.timelapse) {
                syncLayerVisibility();
            }
        });
    }

    function normalizeTimelapsePoint(value) {
        if (!Array.isArray(value) || value.length !== 2) {
            return null;
        }
        var x = Number(value[0]);
        var z = Number(value[1]);
        if (!Number.isFinite(x) || !Number.isFinite(z) ||
            Math.abs(x) > TIMELAPSE_WORLD_RADIUS ||
            Math.abs(z) > TIMELAPSE_WORLD_RADIUS) {
            return null;
        }
        return [x, z];
    }

    function normalizeTimelapsePoints(values) {
        if (!Array.isArray(values)) {
            return null;
        }
        var points = [];
        for (var index = 0; index < values.length; index++) {
            var point = normalizeTimelapsePoint(values[index]);
            if (!point) {
                return null;
            }
            points.push(point);
        }
        return points;
    }

    function normalizeTimelapseFrame(payload, expectedTimestamp) {
        if (!payload || Number(payload.t) !== expectedTimestamp ||
            Number(payload.size) !== 512 || !Array.isArray(payload.fog) ||
            payload.fog.length > (512 * 512) + 1 ||
            Number(payload.movementSize) !== 128 ||
            !Array.isArray(payload.bases) || !Array.isArray(payload.movement)) {
            return null;
        }

        var fogCells = 0;
        for (var runIndex = 0; runIndex < payload.fog.length; runIndex++) {
            var runLength = Number(payload.fog[runIndex]);
            if (!Number.isInteger(runLength) || runLength < 0 ||
                fogCells + runLength > 512 * 512) {
                return null;
            }
            fogCells += runLength;
        }
        if (fogCells !== 512 * 512) {
            return null;
        }

        var bases = [];
        for (var baseIndex = 0; baseIndex < payload.bases.length; baseIndex++) {
            var sourceBase = payload.bases[baseIndex];
            if (!Array.isArray(sourceBase) || sourceBase.length !== 4) {
                return null;
            }
            var baseX = Number(sourceBase[0]);
            var baseZ = Number(sourceBase[1]);
            var baseRadius = Number(sourceBase[2]);
            var basePieces = Number(sourceBase[3]);
            if (!Number.isFinite(baseX) || !Number.isFinite(baseZ) ||
                Math.abs(baseX) > TIMELAPSE_WORLD_RADIUS ||
                Math.abs(baseZ) > TIMELAPSE_WORLD_RADIUS ||
                !Number.isFinite(baseRadius) || baseRadius < 0 ||
                !Number.isInteger(basePieces) || basePieces < 0) {
                return null;
            }
            bases.push([baseX, baseZ, baseRadius, basePieces]);
        }

        var portals = normalizeTimelapsePoints(payload.portals);
        var beds = normalizeTimelapsePoints(payload.beds);
        var wards = normalizeTimelapsePoints(payload.wards);
        if (!portals || !beds || !wards || payload.movement.length > 128 * 128) {
            return null;
        }

        var movement = [];
        var seenMovement = new Set();
        var movementMaximum = 0;
        for (var movementIndex = 0;
            movementIndex < payload.movement.length;
            movementIndex++) {
            var sourceCell = payload.movement[movementIndex];
            if (!Array.isArray(sourceCell) || sourceCell.length !== 2) {
                return null;
            }
            var cellIndex = Number(sourceCell[0]);
            var count = Number(sourceCell[1]);
            if (!Number.isInteger(cellIndex) || cellIndex < 0 ||
                cellIndex >= 128 * 128 || !Number.isInteger(count) || count <= 0 ||
                seenMovement.has(cellIndex)) {
                return null;
            }
            seenMovement.add(cellIndex);
            movementMaximum = Math.max(movementMaximum, count);
            movement.push([cellIndex, count]);
        }

        var reportedMovementMaximum = Number(payload.movementMax);
        if (!Number.isFinite(reportedMovementMaximum) || reportedMovementMaximum < 0) {
            return null;
        }
        return {
            bases: bases,
            beds: beds,
            day: Math.max(0, Math.floor(Number(payload.day) || 0)),
            fog: payload.fog,
            movement: movement,
            movementMax: Math.max(
                movementMaximum,
                Math.floor(reportedMovementMaximum)
            ),
            portals: portals,
            t: expectedTimestamp,
            wards: wards
        };
    }

    function cachedTimelapseFrame(timestamp) {
        if (!timelapseFrameCache.has(timestamp)) {
            return null;
        }
        var frame = timelapseFrameCache.get(timestamp);
        timelapseFrameCache.delete(timestamp);
        timelapseFrameCache.set(timestamp, frame);
        return frame;
    }

    function cacheTimelapseFrame(timestamp, frame) {
        timelapseFrameCache.delete(timestamp);
        timelapseFrameCache.set(timestamp, frame);
        while (timelapseFrameCache.size > TIMELAPSE_FRAME_CACHE_LIMIT) {
            var oldest = timelapseFrameCache.keys().next();
            if (oldest.done) {
                break;
            }
            timelapseFrameCache.delete(oldest.value);
        }
    }

    function fetchTimelapseFrame(index) {
        if (destroyed || !timelapseHasAccess() || !timelapseIndex || index < 0 ||
            index >= timelapseIndex.frames.length || document.hidden) {
            return Promise.resolve(null);
        }
        var timestamp = timelapseIndex.frames[index].t;
        var cached = cachedTimelapseFrame(timestamp);
        if (cached) {
            return Promise.resolve(cached);
        }
        if (timelapseFrameRequests.has(timestamp)) {
            return timelapseFrameRequests.get(timestamp);
        }

        var request = fetchJson(
            "/api/timelapse/frame?t=" + encodeURIComponent(timestamp)
        ).then(function (payload) {
            if (destroyed || !timelapseHasAccess()) {
                return null;
            }
            var frame = normalizeTimelapseFrame(payload, timestamp);
            if (!frame) {
                throw new Error("Invalid timelapse frame response");
            }
            cacheTimelapseFrame(timestamp, frame);
            return frame;
        }).catch(function () {
            return null;
        }).then(function (frame) {
            timelapseFrameRequests.delete(timestamp);
            return frame;
        });
        timelapseFrameRequests.set(timestamp, request);
        return request;
    }

    function loadTimelapseFrame(index, options) {
        options = options || {};
        if (destroyed || !timelapseHasAccess() || !timelapseIndex ||
            !Number.isInteger(index) || index < 0 ||
            index >= timelapseIndex.frames.length || document.hidden) {
            return Promise.resolve(null);
        }
        if (options.prefetch === true) {
            return fetchTimelapseFrame(index);
        }

        timelapseRequestedIndex = index;
        var sequence = ++timelapseRequestSequence;
        return fetchTimelapseFrame(index).then(function (frame) {
            if (!frame || sequence !== timelapseRequestSequence ||
                index !== timelapseRequestedIndex || document.hidden ||
                destroyed || !timelapseHasAccess() ||
                !layerSettings.timelapse || !timelapseIsActive()) {
                return null;
            }
            timelapseCurrentIndex = index;
            renderTimelapseFrame(frame);
            if (timelapsePlaying) {
                prefetchTimelapseFrames(index);
            }
            return frame;
        });
    }

    function prefetchTimelapseFrames(index) {
        if (destroyed || !timelapseHasAccess() || !timelapsePlaying ||
            document.hidden || !timelapseIndex) {
            return;
        }
        for (var offset = 1; offset <= 2; offset++) {
            var nextIndex = index + offset;
            if (nextIndex < timelapseIndex.frames.length) {
                loadTimelapseFrame(nextIndex, { prefetch: true });
            }
        }
    }

    function appendTimelapseSpeedControl(parent) {
        var segments = document.createElement("div");
        segments.className = "timelapse-speed";
        segments.setAttribute("role", "group");
        segments.setAttribute("aria-label", "Timelapse speed");
        ["1x", "4x", "12x"].forEach(function (speed) {
            var button = document.createElement("button");
            var isSelected = layerSettings.timelapseSpeed === speed;
            button.type = "button";
            button.className = "timelapse-speed-option" +
                (isSelected ? " is-selected" : "");
            button.dataset.timelapseSpeed = speed;
            button.textContent = speed;
            button.setAttribute("aria-pressed", String(isSelected));
            addAppListener(button, "click", function () {
                selectTimelapseSpeed(speed);
            });
            segments.appendChild(button);
        });
        parent.appendChild(segments);
        timelapseSpeedControl = segments;
    }

    function selectTimelapseSpeed(speed) {
        if (!Object.prototype.hasOwnProperty.call(TIMELAPSE_SPEEDS, speed) ||
            layerSettings.timelapseSpeed === speed) {
            return;
        }
        layerSettings.timelapseSpeed = speed;
        saveLayerSettings();
        timelapseAnimationTimestamp = 0;
        timelapseAnimationAccumulator = 0;
        syncTimelapseControls();
    }

    function syncTimelapseControls() {
        if (timelapseSpeedControl) {
            timelapseSpeedControl.querySelectorAll("[data-timelapse-speed]")
                .forEach(function (button) {
                    var isSelected = button.dataset.timelapseSpeed ===
                        layerSettings.timelapseSpeed;
                    button.classList.toggle("is-selected", isSelected);
                    button.setAttribute("aria-pressed", String(isSelected));
                });
        }
        if (timelapsePlayButton) {
            var label = timelapsePlaying ? "Pause timelapse" : "Play timelapse";
            timelapsePlayButton.setAttribute("aria-label", label);
            timelapsePlayButton.title = label;
            timelapsePlayButton.innerHTML = iconMarkup(
                timelapsePlaying ? "pause" : "play",
                timelapsePlaying ? "Ⅱ" : "▶"
            );
        }
    }

    function showTimelapseScrubber() {
        if (destroyed || !timelapseHasAccess() ||
            timelapseAvailability !== "available") {
            return;
        }
        if (!timelapseScrubber) {
            var playButton = document.createElement("button");
            var track = document.createElement("input");
            var readout = document.createElement("span");
            var day = document.createElement("span");
            var date = document.createElement("span");
            var closeButton = document.createElement("button");

            timelapseScrubber = document.createElement("div");
            timelapseScrubber.className = "timelapse-scrubber";
            playButton.type = "button";
            playButton.className = "map-tool-button timelapse-play";
            track.type = "range";
            track.className = "timelapse-track";
            track.min = "0";
            track.step = "1";
            track.setAttribute("aria-label", "Timelapse frame");
            readout.className = "timelapse-readout";
            day.className = "timelapse-readout-day";
            day.textContent = "Day —";
            date.className = "timelapse-readout-date";
            date.textContent = "—";
            closeButton.type = "button";
            closeButton.className = "map-tool-button timelapse-close";
            closeButton.setAttribute("aria-label", "Close timelapse");
            closeButton.title = "Close timelapse";
            closeButton.innerHTML = iconMarkup("close", "×");

            readout.appendChild(day);
            readout.appendChild(date);
            timelapseScrubber.appendChild(playButton);
            timelapseScrubber.appendChild(track);
            timelapseScrubber.appendChild(readout);
            appendTimelapseSpeedControl(timelapseScrubber);
            timelapseScrubber.appendChild(closeButton);
            elements.mapPane.appendChild(timelapseScrubber);

            timelapseTrack = track;
            timelapsePlayButton = playButton;
            timelapseReadoutDay = day;
            timelapseReadoutDate = date;
            addAppListener(playButton, "click", toggleTimelapsePlayback);
            addAppListener(closeButton, "click", function () {
                setTimelapseEnabled(false);
            });
            addAppListener(track, "pointerdown", stopTimelapsePlayback);
            addAppListener(track, "touchstart", stopTimelapsePlayback, {
                passive: true
            });
            addAppListener(track, "input", function () {
                if (timelapseTrackSyncing) {
                    return;
                }
                stopTimelapsePlayback();
                loadTimelapseFrame(Number(track.value));
            });
            addAppListener(document, "visibilitychange", timelapseVisibilityChanged);
            addKeyboardListener(handleTimelapseKeyboard);
            L.DomEvent.disableClickPropagation(timelapseScrubber);
            L.DomEvent.disableScrollPropagation(timelapseScrubber);
        }

        timelapseTrack.max = String(
            Math.max(0, timelapseIndex ? timelapseIndex.frames.length - 1 : 0)
        );
        timelapseScrubber.hidden = false;
        appRoot.classList.add("is-timelapse");
        syncTimelapseControls();
    }

    function hideTimelapseScrubber() {
        if (timelapseScrubber) {
            timelapseScrubber.hidden = true;
        }
        appRoot.classList.remove("is-timelapse");
    }

    function setTimelapseEnabled(enabled) {
        enabled = enabled === true && timelapseHasAccess() &&
            timelapseAvailability === "available";
        layerSettings.timelapse = enabled;
        saveLayerSettings();
        syncTimelapseLayerRow();
        syncLayerVisibility();
    }

    function captureTimelapseLiveVisibility() {
        return {
            bases: Boolean(map && poiLayers.get("bases") &&
                map.hasLayer(poiLayers.get("bases"))),
            bed: Boolean(map && entityLayers.get("bed") &&
                map.hasLayer(entityLayers.get("bed"))),
            fog: Boolean(map && fogOverlay && map.hasLayer(fogOverlay)),
            heatmap: Boolean(map && heatmapLayer && map.hasLayer(heatmapLayer)),
            keyboard: Boolean(map && map.keyboard && map.keyboard.enabled()),
            portal: Boolean(map && entityLayers.get("portal") &&
                map.hasLayer(entityLayers.get("portal"))),
            portalNetwork: Boolean(map && portalNetworkLayer &&
                map.hasLayer(portalNetworkLayer)),
            ward: Boolean(map && entityLayers.get("ward") &&
                map.hasLayer(entityLayers.get("ward"))),
            wardRadius: Boolean(map && wardRadiusLayer && map.hasLayer(wardRadiusLayer))
        };
    }

    function hideTimelapseLiveLayers() {
        setLayerVisible(fogOverlay, false);
        setLayerVisible(heatmapLayer, false);
        setLayerVisible(poiLayers.get("bases"), false);
        setLayerVisible(entityLayers.get("portal"), false);
        setLayerVisible(entityLayers.get("bed"), false);
        setLayerVisible(entityLayers.get("ward"), false);
        setLayerVisible(portalNetworkLayer, false);
        setLayerVisible(wardRadiusLayer, false);
    }

    function updateTimelapseRestoreVisibility() {
        if (!timelapseRestoreVisibility || !timelapseIsActive()) {
            return;
        }
        timelapseRestoreVisibility.fog = Boolean(fogAvailable &&
            layerSettings.fog);
        timelapseRestoreVisibility.heatmap = Boolean(timelapseHasAccess() &&
            layerSettings.heatmap && heatmapLayer);
        timelapseRestoreVisibility.bases = Boolean(
            availablePoiGroups.has("bases") && layerSettings.bases &&
            !isPoiGroupZoomGated("bases")
        );
        timelapseRestoreVisibility.portal = Boolean(
            entityLayersAreAvailable() && layerSettings.portal
        );
        timelapseRestoreVisibility.bed = Boolean(
            entityLayersAreAvailable() && layerSettings.bed
        );
        timelapseRestoreVisibility.ward = Boolean(
            entityLayersAreAvailable() && layerSettings.ward
        );
        timelapseRestoreVisibility.portalNetwork = Boolean(
            entityLayersAreAvailable() && layerSettings.portalNetwork
        );
        timelapseRestoreVisibility.wardRadius = Boolean(
            entityLayersAreAvailable() && layerSettings.ward
        );
    }

    function restoreTimelapseLiveLayers() {
        if (!timelapseRestoreVisibility) {
            return;
        }
        setLayerVisible(fogOverlay, timelapseRestoreVisibility.fog);
        setLayerVisible(heatmapLayer, timelapseRestoreVisibility.heatmap);
        setLayerVisible(poiLayers.get("bases"), timelapseRestoreVisibility.bases);
        setLayerVisible(entityLayers.get("portal"), timelapseRestoreVisibility.portal);
        setLayerVisible(entityLayers.get("bed"), timelapseRestoreVisibility.bed);
        setLayerVisible(entityLayers.get("ward"), timelapseRestoreVisibility.ward);
        setLayerVisible(
            portalNetworkLayer,
            timelapseRestoreVisibility.portalNetwork
        );
        setLayerVisible(wardRadiusLayer, timelapseRestoreVisibility.wardRadius);
        if (timelapseRestoreVisibility.keyboard && map && map.keyboard) {
            map.keyboard.enable();
        }
        syncHeatmapControls();
        if (timelapseRestoreVisibility.heatmap) {
            startHeatmapPolling();
        }
        timelapseRestoreVisibility = null;
    }

    function activateTimelapse() {
        if (destroyed || !timelapseHasAccess() || timelapseIsActive() ||
            timelapseAvailability !== "available" || !map) {
            return;
        }
        ensureTimelapseIndex().then(function (index) {
            if (destroyed || !timelapseHasAccess() || !index ||
                !layerSettings.timelapse || timelapseIsActive() || !map) {
                return;
            }
            timelapseRestoreVisibility = captureTimelapseLiveVisibility();
            hideTimelapseLiveLayers();
            if (timelapseRestoreVisibility.keyboard && map.keyboard) {
                map.keyboard.disable();
            }
            showTimelapseScrubber();
            stopHeatmapPolling();
            syncHeatmapControls();
            setLayerVisible(timelapseFogLayer, true);
            setLayerVisible(timelapseMovementLayer, true);
            setLayerVisible(timelapseMarkerLayer, true);
            loadTimelapseFrame(index.frames.length - 1);
        });
    }

    function deactivateTimelapse() {
        stopTimelapsePlayback();
        timelapseRequestSequence++;
        timelapseRequestedIndex = -1;
        timelapseCurrentIndex = -1;
        timelapseRenderedFrame = null;
        timelapseBasePulses.clear();
        if (timelapseFogLayer) {
            timelapseFogLayer.setFrame(null);
        }
        if (timelapseMovementLayer) {
            timelapseMovementLayer.setData(null);
        }
        if (timelapseMarkerLayer) {
            timelapseMarkerLayer.clearLayers();
        }
        setLayerVisible(timelapseFogLayer, false);
        setLayerVisible(timelapseMovementLayer, false);
        setLayerVisible(timelapseMarkerLayer, false);
        hideTimelapseScrubber();
        restoreTimelapseLiveLayers();
    }

    function previousTimelapseBasePieces(base, previousBases) {
        var closestPieces = null;
        var closestDistance = Infinity;
        for (var index = 0; index < previousBases.length; index++) {
            var previous = previousBases[index];
            var deltaX = base[0] - previous[0];
            var deltaZ = base[1] - previous[1];
            var distanceSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
            var matchRadius = Math.max(64, base[2], previous[2]);
            if (distanceSquared <= matchRadius * matchRadius &&
                distanceSquared < closestDistance) {
                closestDistance = distanceSquared;
                closestPieces = previous[3];
            }
        }
        return closestPieces;
    }

    function timelapseBasePulseKey(base) {
        return Math.round(base[0] / 64) + ":" + Math.round(base[1] / 64);
    }

    function addTimelapsePointMarkers(points, color, radius, className) {
        points.forEach(function (point) {
            L.circleMarker(worldToLatLng(point[0], point[1]), {
                bubblingMouseEvents: false,
                className: className,
                color: "#120e0a",
                fill: true,
                fillColor: color,
                fillOpacity: 0.95,
                interactive: false,
                opacity: 0.94,
                pane: "timelapsePane",
                radius: radius,
                stroke: true,
                weight: 1
            }).addTo(timelapseMarkerLayer);
        });
    }

    function renderTimelapseFrame(frame) {
        if (destroyed || !timelapseHasAccess() || !timelapseIsActive() ||
            !frame || !timelapseFogLayer || !timelapseMovementLayer ||
            !timelapseMarkerLayer) {
            return;
        }
        var accentColor = themeColor("--accent", "#d9b168");
        var frostColor = themeColor("--frost", "#7eb1d6");
        var mossColor = themeColor("--moss", "#a3c26a");
        var dungeonColor = themeColor("--dungeon", "#b49acb");
        var previousBases = timelapseRenderedFrame
            ? timelapseRenderedFrame.bases
            : [];
        var reducedMotion = window.matchMedia(
            "(prefers-reduced-motion: reduce)"
        ).matches;
        var now = Date.now();
        timelapseBasePulses.forEach(function (expiresAt, key) {
            if (expiresAt <= now) {
                timelapseBasePulses.delete(key);
            }
        });

        timelapseFogLayer.setFrame(frame);
        timelapseMarkerLayer.clearLayers();
        frame.bases.forEach(function (base) {
            if (!Number.isFinite(base[0]) || !Number.isFinite(base[1])) {
                return;
            }
            var previousPieces = previousTimelapseBasePieces(base, previousBases);
            var pulseKey = timelapseBasePulseKey(base);
            if (!reducedMotion && previousPieces !== null && base[3] > previousPieces) {
                timelapseBasePulses.set(pulseKey, now + 900);
            }
            var pieces = Number(base[3]) || 0;
            var pixelRadius = 5 + (3.2 * Math.sqrt(Math.max(0, pieces) / 25));
            pixelRadius = Math.max(6, Math.min(20, pixelRadius));
            var pulsing = !reducedMotion &&
                (timelapseBasePulses.get(pulseKey) || 0) > now;
            L.circleMarker(worldToLatLng(base[0], base[1]), {
                bubblingMouseEvents: false,
                className: "timelapse-base" +
                    (pulsing ? " timelapse-base-pulse" : ""),
                color: accentColor,
                fillColor: accentColor,
                fillOpacity: 0.25,
                interactive: false,
                opacity: 0.95,
                pane: "timelapsePane",
                radius: pixelRadius,
                weight: 2
            }).addTo(timelapseMarkerLayer);
        });
        addTimelapsePointMarkers(
            frame.portals,
            frostColor,
            5,
            "timelapse-portal"
        );
        addTimelapsePointMarkers(frame.beds, mossColor, 4, "timelapse-bed");
        addTimelapsePointMarkers(frame.wards, dungeonColor, 4, "timelapse-ward");

        var movementCells = frame.movement.map(function (cell) {
            return [cell[0] % 128, Math.floor(cell[0] / 128), cell[1]];
        });
        timelapseMovementLayer.setData({
            cells: movementCells,
            maxCount: frame.movementMax,
            size: 128,
            worldRadius: mapMetrics && Number.isFinite(mapMetrics.worldRadius)
                ? mapMetrics.worldRadius
                : 10500
        });

        if (timelapseTrack) {
            timelapseTrackSyncing = true;
            timelapseTrack.value = String(timelapseCurrentIndex);
            timelapseTrackSyncing = false;
        }
        if (timelapseReadoutDay) {
            timelapseReadoutDay.textContent = "Day " + frame.day;
        }
        if (timelapseReadoutDate) {
            timelapseReadoutDate.textContent = new Date(frame.t).toLocaleString(
                undefined,
                {
                    day: "numeric",
                    hour: "2-digit",
                    hour12: false,
                    minute: "2-digit",
                    month: "short"
                }
            );
        }
        timelapseRenderedFrame = frame;
    }

    function stopTimelapsePlayback() {
        timelapsePlaying = false;
        if (timelapseAnimationFrame) {
            window.cancelAnimationFrame(timelapseAnimationFrame);
        }
        timelapseAnimationFrame = 0;
        timelapseAnimationTimestamp = 0;
        timelapseAnimationAccumulator = 0;
        syncTimelapseControls();
    }

    function beginTimelapsePlayback() {
        if (destroyed || !timelapseHasAccess() || !timelapseIsActive() ||
            document.hidden || !timelapseIndex ||
            timelapseIndex.frames.length < 2) {
            stopTimelapsePlayback();
            return;
        }
        timelapsePlaying = true;
        timelapseAnimationTimestamp = 0;
        timelapseAnimationAccumulator = 0;
        syncTimelapseControls();
        prefetchTimelapseFrames(timelapseCurrentIndex);
        timelapseAnimationFrame = window.requestAnimationFrame(
            timelapseAnimationStep
        );
    }

    function playTimelapse() {
        if (destroyed || !timelapseHasAccess() || !timelapseIsActive() ||
            document.hidden || !timelapseIndex) {
            return;
        }
        var lastIndex = timelapseIndex.frames.length - 1;
        var position = Math.max(timelapseCurrentIndex, timelapseRequestedIndex);
        if (position >= lastIndex) {
            stopTimelapsePlayback();
            loadTimelapseFrame(0).then(function (frame) {
                if (frame && timelapseIsActive()) {
                    beginTimelapsePlayback();
                }
            });
            return;
        }
        beginTimelapsePlayback();
    }

    function toggleTimelapsePlayback() {
        if (timelapsePlaying) {
            stopTimelapsePlayback();
        } else {
            playTimelapse();
        }
    }

    function timelapseAnimationStep(timestamp) {
        timelapseAnimationFrame = 0;
        if (destroyed || !timelapseHasAccess() || !timelapsePlaying ||
            !timelapseIsActive() || document.hidden || !timelapseIndex) {
            stopTimelapsePlayback();
            return;
        }
        if (!timelapseAnimationTimestamp) {
            timelapseAnimationTimestamp = timestamp;
        } else {
            timelapseAnimationAccumulator += Math.min(
                1000,
                timestamp - timelapseAnimationTimestamp
            );
            timelapseAnimationTimestamp = timestamp;
        }

        var speed = TIMELAPSE_SPEEDS[layerSettings.timelapseSpeed] ||
            TIMELAPSE_SPEEDS["4x"];
        var frameDuration = 1000 / speed;
        var lastIndex = timelapseIndex.frames.length - 1;
        while (timelapsePlaying &&
            timelapseAnimationAccumulator >= frameDuration) {
            timelapseAnimationAccumulator -= frameDuration;
            var position = Math.max(
                timelapseCurrentIndex,
                timelapseRequestedIndex
            );
            if (position >= lastIndex) {
                stopTimelapsePlayback();
                return;
            }
            var nextIndex = position + 1;
            loadTimelapseFrame(nextIndex);
            if (nextIndex >= lastIndex) {
                stopTimelapsePlayback();
                return;
            }
        }
        if (timelapsePlaying) {
            timelapseAnimationFrame = window.requestAnimationFrame(
                timelapseAnimationStep
            );
        }
    }

    function timelapseVisibilityChanged() {
        if (document.hidden) {
            stopTimelapsePlayback();
        }
    }

    function handleTimelapseKeyboard(event) {
        if (!timelapseIsActive() || activeTab !== "map" ||
            event.altKey || event.ctrlKey || event.metaKey) {
            return;
        }
        var target = event.target;
        var tagName = target && target.tagName
            ? target.tagName.toLowerCase()
            : "";
        var inputType = tagName === "input"
            ? String(target.type || "text").toLowerCase()
            : "";
        if (tagName === "textarea" || tagName === "select" ||
            (tagName === "input" && inputType !== "range") ||
            (target && target.isContentEditable)) {
            return;
        }
        if (inputType === "range" && event.key !== "Escape" &&
            event.key !== " " && event.code !== "Space") {
            return;
        }
        if (tagName === "button" &&
            (event.key === " " || event.code === "Space")) {
            return;
        }
        var handled = ["ArrowLeft", "ArrowRight", "Home", "End", "Escape", " "]
            .indexOf(event.key) !== -1 || event.code === "Space";
        if (!handled) {
            return;
        }
        event.preventDefault();
        event.stopImmediatePropagation();
        if (event.key === "Escape") {
            setTimelapseEnabled(false);
            return;
        }
        if (!timelapseIndex) {
            return;
        }
        if (event.key === " " || event.code === "Space") {
            if (!event.repeat) {
                toggleTimelapsePlayback();
            }
            return;
        }

        stopTimelapsePlayback();
        var lastIndex = timelapseIndex.frames.length - 1;
        var position = timelapseRequestedIndex >= 0
            ? timelapseRequestedIndex
            : timelapseCurrentIndex;
        if (event.key === "Home") {
            position = 0;
        } else if (event.key === "End") {
            position = lastIndex;
        } else if (event.key === "ArrowLeft") {
            position = Math.max(0, position - 1);
        } else if (event.key === "ArrowRight") {
            position = Math.min(lastIndex, position + 1);
        }
        loadTimelapseFrame(position);
    }

    function formatMapCoordinates(world) {
        return Math.round(world.x) + ", " + Math.round(world.z);
    }

    function parseMapCoordinates(value) {
        var match = String(value || "").trim().match(
            /^\(?\s*(-?\d+)[,\s]+(-?\d+)\s*\)?$/
        );
        if (!match) {
            return null;
        }
        return {
            x: parseInt(match[1], 10),
            z: parseInt(match[2], 10)
        };
    }

    function mapCoordinatesInsideWorld(world) {
        if (!world || !mapMetrics || !Number.isFinite(world.x) ||
            !Number.isFinite(world.z)) {
            return false;
        }
        return (world.x * world.x) + (world.z * world.z) <=
            mapMetrics.worldRadius * mapMetrics.worldRadius;
    }

    function worldDistanceToMap(distance) {
        if (!mapMetrics) {
            return 0;
        }

        return Math.abs(distance / mapMetrics.pixelSize * mapMetrics.unitsPerPixel);
    }

    function updateRegionLayerVisibility() {
        var visible = Boolean(map && mapMetrics && layerSettings.regions &&
            map.getZoom() <= mapMetrics.baseZoom + 1);
        setLayerVisible(regionLayer, visible);
        if (visible) {
            renderRegionLabels();
        }
    }

    function regionLabelRectsIntersect(first, second) {
        var padding = 14;
        return first.left < second.right + padding &&
            first.right + padding > second.left &&
            first.top < second.bottom + padding &&
            first.bottom + padding > second.top;
    }

    function renderRegionLabels() {
        if (!map || !regionLayer) {
            return;
        }

        var keptRects = [];
        var visibleBiomeCounts = Object.create(null);
        regionLayer.clearLayers();
        regionLabelRecords.forEach(function (record) {
            var biomeKey = record.name.toLowerCase();
            var biomeCount = visibleBiomeCounts[biomeKey] || 0;
            if (biomeCount >= 4) {
                return;
            }

            var anchor = map.latLngToContainerPoint(record.latLng);
            var fontSize = record.isMajor ? 11 : 9;
            var width = record.name.length * 7 * fontSize / 9;
            var height = 16;
            var rect = {
                left: anchor.x - width / 2,
                right: anchor.x + width / 2,
                top: anchor.y - height / 2,
                bottom: anchor.y + height / 2
            };
            var collides = keptRects.some(function (keptRect) {
                return regionLabelRectsIntersect(rect, keptRect);
            });
            if (collides) {
                return;
            }

            keptRects.push(rect);
            visibleBiomeCounts[biomeKey] = biomeCount + 1;
            record.marker.addTo(regionLayer);
        });
    }

    async function loadRegions() {
        if (!map || !regionLayer || regionsRequested) {
            return;
        }

        regionsRequested = true;
        try {
            var payload = await fetchJson("/api/regions");
            var regions = payload && Array.isArray(payload.regions) ? payload.regions : [];
            regionLayer.clearLayers();
            regionLabelRecords = [];
            regions.forEach(function (region, index) {
                var name = region && typeof region.name === "string"
                    ? region.name.trim()
                    : "";
                var x = Number(region && region.x);
                var z = Number(region && region.z);
                var area = Number(region && region.area);
                if (!name || !Number.isFinite(x) || !Number.isFinite(z)) {
                    return;
                }

                var label = document.createElement("span");
                label.textContent = name;
                var marker = L.marker(worldToLatLng(x, z), {
                    icon: L.divIcon({
                        className: "region-label-anchor",
                        html: "",
                        iconAnchor: [0, 0],
                        iconSize: [0, 0]
                    }),
                    interactive: false,
                    keyboard: false,
                    pane: "regionPane"
                });
                marker.bindTooltip(label, {
                    className: "region-label" +
                        (Number.isFinite(area) && area >= 1500000 ? " is-major" : ""),
                    direction: "center",
                    opacity: 1,
                    pane: "regionPane",
                    permanent: true
                });
                regionLabelRecords.push({
                    area: Number.isFinite(area) ? area : 0,
                    index: index,
                    isMajor: Number.isFinite(area) && area >= 1500000,
                    latLng: marker.getLatLng(),
                    marker: marker,
                    name: name
                });
            });
            regionLabelRecords.sort(function (first, second) {
                return second.area - first.area || first.index - second.index;
            });
            updateRegionLayerVisibility();
        } catch (error) {
            return;
        }
    }

    function createCompassControl() {
        var CompassControl = L.Control.extend({
            options: { position: "bottomleft" },
            onAdd: function () {
                var container = L.DomUtil.create("div", "leaflet-control compass-control");
                var button = L.DomUtil.create("button", "compass-button", container);
                var clickTimer = 0;
                button.type = "button";
                button.title = "Click: home · Double-click: fit world";
                button.setAttribute("aria-label", button.title);
                // wind needle stubbed until Phase 2 wind fields
                button.innerHTML = '<svg viewBox="0 0 80 80" aria-hidden="true" focusable="false">' +
                    '<circle class="compass-disc" cx="40" cy="40" r="29" />' +
                    '<circle class="compass-ring" cx="40" cy="40" r="25" />' +
                    '<g class="compass-staves">' +
                    '<path d="M40 40V15M40 15l-5 6m5-6 5 6M36 24h8" />' +
                    '<path d="M40 40l18-18m0 0 5-5m-5 5-7-1m7 1 1 7" />' +
                    '<path d="M40 40h25m0 0-6-5m6 5-6 5M56 36v8" />' +
                    '<path d="M40 40l18 18m0 0 5 5m-5-5 1-7m-1 7-7-1" />' +
                    '<path d="M40 40v25m0 0-5-6m5 6 5-6M36 56h8" />' +
                    '<path d="M40 40 22 58m0 0-5 5m5-5 7-1m-7 1-1-7" />' +
                    '<path d="M40 40H15m0 0 6-5m-6 5 6 5M24 36v8" />' +
                    '<path d="M40 40 22 22m0 0-5-5m5 5-1 7m1-7 7 1" />' +
                    '</g><g class="compass-wind-needle"><path d="M40 40V19" /></g>' +
                    '<circle class="compass-hub" cx="40" cy="40" r="2.8" />' +
                    '<g class="compass-cardinals"><text class="compass-north" x="40" y="8">N</text>' +
                    '<text x="74" y="43">E</text><text x="40" y="78">S</text>' +
                    '<text x="6" y="43">W</text></g></svg>';
                compassButton = button;
                compassWindNeedle = button.querySelector(".compass-wind-needle");
                renderWindStatus();

                addAppListener(button, "click", function () {
                    window.clearTimeout(clickTimer);
                    clickTimer = window.setTimeout(function () {
                        map.flyTo(worldToLatLng(0, 0), map.getZoom(), { duration: 0.45 });
                    }, 250);
                });
                addAppListener(button, "dblclick", function (event) {
                    event.preventDefault();
                    window.clearTimeout(clickTimer);
                    clickTimer = 0;
                    map.fitBounds(worldBounds, { animate: true, duration: 0.45 });
                });
                L.DomEvent.disableClickPropagation(container);
                L.DomEvent.disableScrollPropagation(container);
                return container;
            }
        });

        new CompassControl().addTo(map);
    }

    function formatScaleDistance(distance) {
        if (distance >= 1000) {
            return (distance / 1000).toLocaleString("en-US", {
                maximumFractionDigits: distance < 10000 ? 1 : 0
            }) + " km";
        }
        return distance.toLocaleString("en-US", {
            maximumFractionDigits: distance < 10 ? 1 : 0
        }) + " m";
    }

    function updateScaleBar() {
        if (!scaleBarElement || !map || !mapMetrics) {
            return;
        }

        var zoom = map.getZoom();
        if (!Number.isFinite(zoom)) {
            return;
        }
        var metersPerCssPixel = mapMetrics.pixelSize * mapMetrics.textureSize / TILE_SIZE /
            Math.pow(2, zoom);
        var maximumDistance = metersPerCssPixel * 120;
        if (!Number.isFinite(maximumDistance) || maximumDistance <= 0) {
            return;
        }

        var magnitude = Math.pow(10, Math.floor(Math.log(maximumDistance) / Math.LN10));
        var distance = magnitude;
        [5, 2, 1].some(function (multiple) {
            var candidate = multiple * magnitude;
            if (candidate <= maximumDistance) {
                distance = candidate;
                return true;
            }
            return false;
        });
        scaleBarElement.style.width = (distance / metersPerCssPixel).toFixed(1) + "px";
        scaleBarElement.querySelector(".map-scale-label").textContent = formatScaleDistance(distance);
    }

    function createScaleBarControl() {
        var ScaleBarControl = L.Control.extend({
            options: { position: "bottomleft" },
            onAdd: function () {
                var container = L.DomUtil.create("div", "leaflet-control map-scale-control");
                var label = L.DomUtil.create("span", "map-scale-label", container);
                label.textContent = "—";
                scaleBarElement = container;
                L.DomEvent.disableClickPropagation(container);
                return container;
            }
        });

        new ScaleBarControl().addTo(map);
        map.on("zoomend resize", updateScaleBar);
        updateScaleBar();
    }

    function formatMeasurementDistance(distance) {
        if (distance >= 1000) {
            return "~" + (distance / 1000).toLocaleString("en-US", {
                maximumFractionDigits: 2,
                minimumFractionDigits: distance < 10000 ? 2 : 1
            }) + " km straight line";
        }
        return "~" + Math.round(distance).toLocaleString("en-US") + " m straight line";
    }

    function formatTravelTime(distance, speed) {
        var totalSeconds = Math.max(0, Math.round(distance / speed));
        return Math.floor(totalSeconds / 60) + "m " + padTwo(totalSeconds % 60) + "s";
    }

    function measurementDistance() {
        var total = 0;
        for (var index = 1; index < measurePoints.length; index++) {
            var previous = latLngToWorld(measurePoints[index - 1]);
            var current = latLngToWorld(measurePoints[index]);
            if (previous && current) {
                total += worldDistance(previous.x, previous.z, current.x, current.z);
            }
        }
        return total;
    }

    function webPinIconLabel(icon) {
        return icon.split("_").map(function (part) {
            return part.charAt(0).toUpperCase() + part.slice(1);
        }).join(" ");
    }

    function setWebPinDialogIcon(icon) {
        if (!webPinDialogState || WEB_PIN_ICONS.indexOf(icon) === -1) {
            return;
        }
        webPinDialogState.icon = icon;
        webPinDialog.querySelectorAll("[data-webpin-icon]").forEach(function (button) {
            var isSelected = button.dataset.webpinIcon === icon;
            button.classList.toggle("is-selected", isSelected);
            button.setAttribute("aria-pressed", String(isSelected));
        });
    }

    function ensureWebPinDialog() {
        if (webPinDialog) {
            return webPinDialog;
        }

        var backdrop = document.createElement("div");
        var dialog = document.createElement("section");
        var form = document.createElement("form");
        var header = document.createElement("header");
        var kicker = document.createElement("span");
        var title = document.createElement("h2");
        var position = document.createElement("p");
        var iconField = document.createElement("fieldset");
        var iconLegend = document.createElement("legend");
        var iconGrid = document.createElement("div");
        var labelField = document.createElement("label");
        var labelText = document.createElement("span");
        var labelInput = document.createElement("input");
        var authorField = document.createElement("label");
        var authorText = document.createElement("span");
        var authorInput = document.createElement("input");
        var error = document.createElement("p");
        var actions = document.createElement("div");
        var cancel = document.createElement("button");
        var submit = document.createElement("button");

        backdrop.className = "webpin-dialog-backdrop";
        backdrop.hidden = true;
        dialog.className = "webpin-dialog";
        dialog.setAttribute("role", "dialog");
        dialog.setAttribute("aria-modal", "true");
        dialog.setAttribute("aria-labelledby", "webpin-dialog-title");
        form.className = "webpin-dialog-form";
        header.className = "webpin-dialog-header";
        kicker.className = "webpin-dialog-kicker";
        kicker.textContent = "WEB PIN";
        title.id = "webpin-dialog-title";
        title.textContent = "Drop a pin";
        position.className = "webpin-dialog-position";
        iconField.className = "webpin-icon-field";
        iconLegend.textContent = "Icon";
        iconGrid.className = "webpin-icon-grid";
        WEB_PIN_ICONS.forEach(function (icon) {
            var button = document.createElement("button");
            var mark = document.createElement("span");
            var name = document.createElement("span");
            button.type = "button";
            button.className = "webpin-icon-choice";
            button.dataset.webpinIcon = icon;
            button.setAttribute("aria-pressed", "false");
            button.title = webPinIconLabel(icon);
            mark.className = "webpin-icon-choice-mark";
            mark.innerHTML = iconMarkup(icon, "✦");
            mark.setAttribute("aria-hidden", "true");
            name.className = "webpin-icon-choice-name";
            name.textContent = webPinIconLabel(icon);
            button.appendChild(mark);
            button.appendChild(name);
            addAppListener(button, "click", function () {
                setWebPinDialogIcon(icon);
            });
            iconGrid.appendChild(button);
        });
        labelField.className = "webpin-dialog-field";
        labelText.textContent = "Label";
        labelInput.name = "label";
        labelInput.type = "text";
        labelInput.maxLength = 60;
        labelInput.placeholder = "What is here?";
        labelInput.autocomplete = "off";
        authorField.className = "webpin-dialog-field webpin-author-field";
        authorText.textContent = "Your name";
        authorInput.name = "author";
        authorInput.type = "text";
        authorInput.maxLength = 32;
        authorInput.placeholder = "Viking name";
        authorInput.autocomplete = "nickname";
        error.className = "webpin-dialog-error";
        error.setAttribute("role", "alert");
        error.hidden = true;
        actions.className = "webpin-dialog-actions";
        cancel.type = "button";
        cancel.className = "webpin-dialog-cancel";
        cancel.textContent = "Cancel";
        submit.type = "submit";
        submit.className = "webpin-dialog-submit";
        submit.textContent = "Save";

        header.appendChild(kicker);
        header.appendChild(title);
        iconField.appendChild(iconLegend);
        iconField.appendChild(iconGrid);
        labelField.appendChild(labelText);
        labelField.appendChild(labelInput);
        authorField.appendChild(authorText);
        authorField.appendChild(authorInput);
        actions.appendChild(cancel);
        actions.appendChild(submit);
        form.appendChild(header);
        form.appendChild(position);
        form.appendChild(iconField);
        form.appendChild(labelField);
        form.appendChild(authorField);
        form.appendChild(error);
        form.appendChild(actions);
        dialog.appendChild(form);
        backdrop.appendChild(dialog);
        appRoot.appendChild(backdrop);

        addAppListener(cancel, "click", closeWebPinDialog);
        addAppListener(backdrop, "click", function (event) {
            if (event.target === backdrop) {
                closeWebPinDialog();
            }
        });
        addAppListener(form, "submit", submitWebPinDialog);
        addKeyboardListener(function (event) {
            if (event.key === "Escape" && webPinDialog && !webPinDialog.hidden) {
                event.preventDefault();
                closeWebPinDialog();
            }
        });
        webPinDialog = backdrop;
        return backdrop;
    }

    function closeWebPinDialog() {
        if (!webPinDialog) {
            return;
        }
        webPinDialog.hidden = true;
        webPinDialogState = null;
    }

    function showWebPinDialogError(message) {
        if (!webPinDialog) {
            return;
        }
        var error = webPinDialog.querySelector(".webpin-dialog-error");
        error.textContent = message;
        error.hidden = false;
    }

    function openWebPinDialog(options) {
        var pin = options && options.pin ? options.pin : null;
        var world = options && options.world ? options.world : null;
        if ((!pin && (!world || !canCreateWebPin())) || (pin && !canEditWebPin(pin))) {
            return;
        }

        disarmWebPinPlacement();
        disarmMapPing();
        disarmShipTow();
        if (measureModeEnabled) {
            clearMeasurement();
        }
        dismissMapContextMenu();
        if (map && map._popup) {
            map.closePopup();
        }

        var dialog = ensureWebPinDialog();
        var x = pin ? pin.x : world.x;
        var z = pin ? pin.z : world.z;
        webPinDialogState = {
            icon: pin && WEB_PIN_ICONS.indexOf(pin.icon) !== -1 ? pin.icon : "pin",
            pin: pin,
            world: { x: x, z: z }
        };
        dialog.classList.toggle("is-editing", Boolean(pin));
        dialog.classList.toggle("is-admin", currentView === "admin");
        dialog.querySelector("#webpin-dialog-title").textContent = pin
            ? "Edit web pin"
            : "Drop a web pin";
        dialog.querySelector(".webpin-dialog-position").textContent =
            "X " + Math.round(x) + " · Z " + Math.round(z);
        dialog.querySelector('input[name="label"]').value = pin ? pin.label : "";
        dialog.querySelector('input[name="author"]').value = webPinOperatorAuthor();
        dialog.querySelector(".webpin-dialog-submit").disabled = false;
        dialog.querySelector(".webpin-dialog-submit").textContent = "Save";
        var error = dialog.querySelector(".webpin-dialog-error");
        error.textContent = "";
        error.hidden = true;
        setWebPinDialogIcon(webPinDialogState.icon);
        dialog.hidden = false;
        window.setTimeout(function () {
            var author = dialog.querySelector('input[name="author"]');
            var label = dialog.querySelector('input[name="label"]');
            (storedWebPinAuthor() ? label : author).focus();
        }, 0);
    }

    function webPinWriteOptions(method, body, author) {
        var options = {
            headers: {
                "Content-Type": "application/json",
                "X-Operator": author
            },
            method: method
        };
        if (body !== null) {
            options.body = JSON.stringify(body);
        }
        return options;
    }

    async function submitWebPinDialog(event) {
        event.preventDefault();
        if (!webPinDialogState || !webPinDialog) {
            return;
        }
        var state = webPinDialogState;
        var label = webPinDialog.querySelector('input[name="label"]').value.slice(0, 60);
        var author = saveWebPinAuthor(
            webPinDialog.querySelector('input[name="author"]').value
        );
        if (!author) {
            showWebPinDialogError("Your name is required.");
            webPinDialog.querySelector('input[name="author"]').focus();
            return;
        }

        var submit = webPinDialog.querySelector(".webpin-dialog-submit");
        submit.disabled = true;
        submit.textContent = "Saving…";
        webPinDialog.hidden = true;
        try {
            if (state.pin) {
                await fetchJson(
                    "/api/webpins/" + encodeURIComponent(state.pin.id),
                    webPinWriteOptions("PATCH", {
                        icon: state.icon,
                        label: label
                    }, author)
                );
            } else {
                await fetchJson("/api/webpins", webPinWriteOptions("POST", {
                    author: author,
                    icon: state.icon,
                    label: label,
                    x: state.world.x,
                    z: state.world.z
                }, author));
            }
            webPinDialogState = null;
            requestWebPinsFetch();
        } catch (error) {
            webPinDialogState = state;
            webPinDialog.hidden = false;
            showWebPinDialogError(
                error && error.message ? error.message : "The pin could not be saved."
            );
        } finally {
            submit.disabled = false;
            submit.textContent = "Save";
        }
    }

    function syncWebPinControl() {
        if (!webPinButton) {
            return;
        }
        webPinButton.hidden = !canCreateWebPin();
        if (!canCreateWebPin()) {
            disarmWebPinPlacement();
        }
    }

    function disarmWebPinPlacement() {
        webPinPlacementArmed = false;
        appRoot.classList.remove("is-dropping-webpin");
        if (!webPinButton) {
            return;
        }
        webPinButton.classList.remove("is-active");
        webPinButton.title = "Drop a web pin";
        webPinButton.setAttribute("aria-label", "Drop a web pin");
        webPinButton.setAttribute("aria-pressed", "false");
    }

    function armWebPinPlacement() {
        if (!map || !canCreateWebPin()) {
            return;
        }
        disarmShipTow();
        disarmMapPing();
        if (measureModeEnabled) {
            clearMeasurement();
        }
        if (map._popup) {
            map.closePopup();
        }
        dismissMapContextMenu();
        webPinPlacementArmed = true;
        appRoot.classList.add("is-dropping-webpin");
        webPinButton.classList.add("is-active");
        webPinButton.title = "Click the map to drop a pin · Esc cancels";
        webPinButton.setAttribute("aria-label", "Click the map to drop a pin; Escape cancels");
        webPinButton.setAttribute("aria-pressed", "true");
    }

    function updateMeasureHud() {
        if (!measureHud) {
            return;
        }

        var distance = measurementDistance();
        measureHud.querySelector(".measure-instruction").textContent = measureActive
            ? "Click to add points · double-click or Esc to finish · Backspace undoes"
            : "Measurement finished · Esc or ruler clears";
        measureHud.querySelector(".measure-total").textContent =
            formatMeasurementDistance(distance) + " · run ~" + formatTravelTime(distance, 7) +
            " · longship ~" + formatTravelTime(distance, 6.5);
        measureHud.hidden = !measureModeEnabled;
    }

    function redrawMeasurement() {
        if (!measureLayer) {
            return;
        }

        measureLayer.clearLayers();
        measureLine = null;
        measureVertexMarkers = [];
        if (measurePoints.length > 1) {
            measureLine = L.polyline(measurePoints, {
                color: "#d9b168",
                dashArray: "6 6",
                interactive: false,
                opacity: 0.95,
                weight: 2
            }).addTo(measureLayer);
        }
        measurePoints.forEach(function (point) {
            var marker = L.marker(point, {
                icon: L.divIcon({
                    className: "measure-vertex-div-icon",
                    html: '<span class="measure-vertex" aria-hidden="true"></span>',
                    iconAnchor: [5, 5],
                    iconSize: [10, 10]
                }),
                interactive: false,
                keyboard: false
            }).addTo(measureLayer);
            measureVertexMarkers.push(marker);
        });
        updateMeasureHud();
    }

    function restoreMeasureDoubleClickZoom() {
        if (measureDoubleClickZoomWasEnabled && map && !map.doubleClickZoom.enabled()) {
            map.doubleClickZoom.enable();
        }
        measureDoubleClickZoomWasEnabled = false;
    }

    function finishMeasurement() {
        if (!measureActive) {
            return;
        }
        measureActive = false;
        appRoot.classList.remove("is-measuring");
        restoreMeasureDoubleClickZoom();
        updateMeasureHud();
    }

    function clearMeasurement() {
        measureActive = false;
        measureModeEnabled = false;
        measurePoints = [];
        measureLine = null;
        measureVertexMarkers = [];
        appRoot.classList.remove("is-measuring");
        restoreMeasureDoubleClickZoom();
        if (measureLayer) {
            measureLayer.clearLayers();
        }
        if (measureButton) {
            measureButton.classList.remove("is-active");
            measureButton.title = "Measure distance";
            measureButton.setAttribute("aria-label", "Measure distance");
            measureButton.setAttribute("aria-pressed", "false");
        }
        if (measureHud) {
            measureHud.hidden = true;
        }
    }

    function startMeasurement(initialPoint) {
        disarmShipTow();
        disarmMapPing();
        disarmWebPinPlacement();
        measureModeEnabled = true;
        measureActive = true;
        measurePoints = initialPoint ? [L.latLng(initialPoint)] : [];
        if (map._popup) {
            map.closePopup();
        }
        measureLayer.clearLayers();
        measureDoubleClickZoomWasEnabled = map.doubleClickZoom.enabled();
        if (measureDoubleClickZoomWasEnabled) {
            map.doubleClickZoom.disable();
        }
        appRoot.classList.add("is-measuring");
        measureButton.classList.add("is-active");
        measureButton.title = "Clear measurement";
        measureButton.setAttribute("aria-label", "Clear measurement");
        measureButton.setAttribute("aria-pressed", "true");
        redrawMeasurement();
    }

    function createMeasureControl() {
        var MeasureControl = L.Control.extend({
            options: { position: "topleft" },
            onAdd: function () {
                var container = L.DomUtil.create("div", "leaflet-control leaflet-bar map-tool-control");
                measureButton = L.DomUtil.create("button", "map-tool-button measure-button", container);
                measureButton.type = "button";
                measureButton.title = "Measure distance";
                measureButton.setAttribute("aria-label", "Measure distance");
                measureButton.setAttribute("aria-pressed", "false");
                measureButton.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true">' +
                    '<path d="M5 18 18 5l2 2L7 20zM9 15l2 2m1-5 2 2m1-5 2 2" /></svg>';
                addAppListener(measureButton, "click", function () {
                    if (measureModeEnabled) {
                        clearMeasurement();
                    } else {
                        startMeasurement();
                    }
                });
                webPinButton = L.DomUtil.create(
                    "button",
                    "map-tool-button webpin-drop-button",
                    container
                );
                webPinButton.type = "button";
                webPinButton.textContent = "✦";
                webPinButton.title = "Drop a web pin";
                webPinButton.setAttribute("aria-label", "Drop a web pin");
                webPinButton.setAttribute("aria-pressed", "false");
                addAppListener(webPinButton, "click", function () {
                    if (webPinPlacementArmed) {
                        disarmWebPinPlacement();
                    } else {
                        armWebPinPlacement();
                    }
                });
                L.DomEvent.disableClickPropagation(container);
                L.DomEvent.disableScrollPropagation(container);
                syncWebPinControl();
                return container;
            }
        });

        measureLayer = L.layerGroup().addTo(map);
        measureHud = document.createElement("div");
        measureHud.className = "measure-hud";
        measureHud.hidden = true;
        measureHud.innerHTML = '<div class="measure-instruction"></div>' +
            '<div class="measure-total"></div>' +
            '<div class="measure-footnote">sail time varies with wind (longship 3.6-9.4 m/s)</div>';
        elements.mapPane.appendChild(measureHud);
        new MeasureControl().addTo(map);

        map.on("click", function (event) {
            if (!measureActive || (event.originalEvent && event.originalEvent.detail > 1) ||
                (event.originalEvent && typeof event.originalEvent.button === "number" &&
                    event.originalEvent.button !== 0)) {
                return;
            }
            measurePoints.push(event.latlng);
            redrawMeasurement();
        });
        map.on("click", function (event) {
            if (!webPinPlacementArmed ||
                (event.originalEvent && typeof event.originalEvent.button === "number" &&
                    event.originalEvent.button !== 0)) {
                return;
            }
            var world = latLngToWorld(event.latlng);
            disarmWebPinPlacement();
            if (world) {
                openWebPinDialog({ world: world });
            }
        });
        map.on("dblclick", function (event) {
            if (!measureActive) {
                return;
            }
            if (event.originalEvent) {
                event.originalEvent.preventDefault();
            }
            finishMeasurement();
        });
        addKeyboardListener(function (event) {
            if (event.key === "Escape") {
                if (webPinPlacementArmed) {
                    event.preventDefault();
                    disarmWebPinPlacement();
                } else if (measureActive) {
                    event.preventDefault();
                    finishMeasurement();
                } else if (measureModeEnabled) {
                    event.preventDefault();
                    clearMeasurement();
                }
            } else if (event.key === "Backspace" && measureActive && measurePoints.length > 0) {
                event.preventDefault();
                measurePoints.pop();
                redrawMeasurement();
            }
        });
    }

    function syncMapPingControl() {
        var admin = currentView === "admin";
        if (pingControlElement) {
            pingControlElement.hidden = !admin;
        }
        if (!admin) {
            disarmMapPing();
        }
    }

    function shipTowEntityById(shipId) {
        return latestEntities.find(function (entity) {
            return entity.group === "ship" && entity.id === shipId;
        }) || null;
    }

    function ensureTowBanner() {
        if (towBanner) {
            return towBanner;
        }

        towBanner = document.createElement("div");
        towBanner.className = "tow-armed-banner";
        towBanner.setAttribute("role", "status");
        towBanner.setAttribute("aria-live", "polite");
        towBanner.hidden = true;
        elements.mapPane.appendChild(towBanner);
        return towBanner;
    }

    function disarmShipTow() {
        towState = null;
        appRoot.classList.remove("is-towing");
        if (towBanner) {
            towBanner.hidden = true;
            towBanner.textContent = "";
        }
    }

    function armShipTow(entity) {
        if (!map || currentView !== "admin" || towRequestPending || !entity ||
            entity.group !== "ship" || !entity.id) {
            return;
        }

        disarmMapPing();
        disarmWebPinPlacement();
        if (measureModeEnabled) {
            clearMeasurement();
        }
        dismissMapContextMenu();
        var shipName = shipDisplayName(entity.prefab);
        towState = {
            shipId: entity.id,
            shipName: shipName,
            x: entity.x,
            z: entity.z
        };
        appRoot.classList.add("is-towing");
        var banner = ensureTowBanner();
        banner.textContent = "Click map to tow " + shipName;
        banner.hidden = false;
        if (map._popup) {
            map.closePopup();
        }
    }

    function openShipTowConfirm(state, target) {
        var latestShip = shipTowEntityById(state.shipId);
        var fromX = latestShip ? latestShip.x : state.x;
        var fromZ = latestShip ? latestShip.z : state.z;
        var distance = worldDistance(fromX, fromZ, target.x, target.z);
        confirmAction = {
            action: "tow",
            distance: distance,
            shipId: state.shipId,
            shipName: state.shipName,
            target: { x: target.x, z: target.z }
        };
        elements.confirmMessage.textContent = "Tow " + state.shipName + " ~" +
            Math.round(distance).toLocaleString("en-US") + " m to (" +
            Math.round(target.x).toLocaleString("en-US") + ", " +
            Math.round(target.z).toLocaleString("en-US") + ")?";
        elements.confirmSubmit.textContent = "Tow";
        elements.confirmSubmit.classList.remove("is-danger");
        elements.confirmBackdrop.hidden = false;
        elements.confirmCancel.focus();
    }

    function shipTowRefusalMessage(error) {
        if (error && error.reason === "players_aboard") {
            return "Players are aboard or nearby";
        }
        if (error && error.reason === "too_far") {
            return "Too far — 5 km limit";
        }
        return error && error.message ? error.message : "Tow request failed";
    }

    async function submitShipTow(action) {
        if (towRequestPending || currentView !== "admin") {
            return;
        }

        towRequestPending = true;
        try {
            var payload = await fetchConsoleJson("/api/admin/tow", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "X-Operator": webPinOperatorAuthor()
                },
                body: JSON.stringify({
                    shipId: action.shipId,
                    x: action.target.x,
                    z: action.target.z
                })
            });
            if (!payload || payload.ok !== true) {
                var rejection = new Error(payload && payload.message
                    ? payload.message
                    : "Tow request rejected");
                rejection.reason = payload && payload.reason ? payload.reason : "";
                throw rejection;
            }

            var moved = Number(payload.moved);
            showNoticeToast(
                "Towed " + action.shipName +
                (Number.isFinite(moved)
                    ? " " + Math.round(moved).toLocaleString("en-US") + " m"
                    : "")
            );
            pendingShipTowTweenIds.add(action.shipId);
            updateEntityPolling(true);
        } catch (error) {
            showNoticeToast(shipTowRefusalMessage(error));
        } finally {
            towRequestPending = false;
        }
    }

    function bindShipTowInteraction() {
        ensureTowBanner();
        map.on("click", function (event) {
            if (!towState ||
                (event.originalEvent && typeof event.originalEvent.button === "number" &&
                    event.originalEvent.button !== 0)) {
                return;
            }

            var state = towState;
            var world = latLngToWorld(event.latlng);
            disarmShipTow();
            if (world) {
                openShipTowConfirm(state, world);
            }
        });
        addKeyboardListener(function (event) {
            if (event.key === "Escape" && towState) {
                event.preventDefault();
                disarmShipTow();
            }
        });
    }

    function disarmMapPing() {
        pingArmed = false;
        appRoot.classList.remove("is-pinging");
        if (!pingButton) {
            return;
        }
        pingButton.classList.remove("is-active");
        pingButton.title = "Ping in-game map";
        pingButton.setAttribute("aria-label", "Ping in-game map");
        pingButton.setAttribute("aria-pressed", "false");
    }

    function armMapPing() {
        if (!map || currentView !== "admin" || pingRequestPending) {
            return;
        }
        disarmShipTow();
        disarmWebPinPlacement();
        if (measureModeEnabled) {
            clearMeasurement();
        }
        if (map._popup) {
            map.closePopup();
        }
        pingArmed = true;
        appRoot.classList.add("is-pinging");
        pingButton.classList.add("is-active");
        pingButton.title = "Click the map to send ping · Esc cancels";
        pingButton.setAttribute("aria-label", "Click the map to send ping; Escape cancels");
        pingButton.setAttribute("aria-pressed", "true");
    }

    function flashMapPingResult(ok, message) {
        if (!pingButton) {
            return;
        }
        window.clearTimeout(pingButton._voPingResultTimer);
        pingButton.classList.toggle("is-success", ok);
        pingButton.classList.toggle("is-error", !ok);
        pingButton.title = message;
        pingButton._voPingResultTimer = window.setTimeout(function () {
            pingButton.classList.remove("is-success", "is-error");
            pingButton.title = "Ping in-game map";
            pingButton._voPingResultTimer = 0;
        }, 1800);
    }

    async function sendMapPing(world) {
        pingRequestPending = true;
        pingButton.disabled = true;
        try {
            var payload = await fetchConsoleJson("/api/ping", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ x: world.x, z: world.z })
            });
            if (!payload || payload.ok !== true) {
                throw new Error(payload && payload.error ? payload.error : "Request rejected");
            }
            flashMapPingResult(true, "Ping sent to in-game maps");
        } catch (error) {
            flashMapPingResult(
                false,
                "Ping failed: " + (error && error.message ? error.message : "request failed")
            );
        } finally {
            pingRequestPending = false;
            pingButton.disabled = false;
        }
    }

    function createPingControl() {
        var PingControl = L.Control.extend({
            options: { position: "topleft" },
            onAdd: function () {
                var container = L.DomUtil.create("div", "leaflet-control leaflet-bar map-tool-control ping-control");
                pingControlElement = container;
                pingButton = L.DomUtil.create("button", "map-tool-button ping-button", container);
                pingButton.type = "button";
                pingButton.title = "Ping in-game map";
                pingButton.setAttribute("aria-label", "Ping in-game map");
                pingButton.setAttribute("aria-pressed", "false");
                pingButton.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true">' +
                    '<circle cx="12" cy="12" r="2"></circle>' +
                    '<circle cx="12" cy="12" r="6"></circle>' +
                    '<path d="M12 2v3m0 14v3M2 12h3m14 0h3"></path></svg>';
                addAppListener(pingButton, "click", function () {
                    if (pingArmed) {
                        disarmMapPing();
                    } else {
                        armMapPing();
                    }
                });
                L.DomEvent.disableClickPropagation(container);
                L.DomEvent.disableScrollPropagation(container);
                syncMapPingControl();
                return container;
            }
        });

        new PingControl().addTo(map);
        map.on("click", function (event) {
            if (!pingArmed ||
                (event.originalEvent && typeof event.originalEvent.button === "number" &&
                    event.originalEvent.button !== 0)) {
                return;
            }
            var world = latLngToWorld(event.latlng);
            disarmMapPing();
            if (world) {
                sendMapPing(world);
            }
        });
        addKeyboardListener(function (event) {
            if (event.key === "Escape" && pingArmed) {
                event.preventDefault();
                disarmMapPing();
            }
        });
    }

    function renderMapPing(ping) {
        if (!map || !pingLayer) {
            pendingMapPings.push(ping);
            if (pendingMapPings.length > 16) {
                pendingMapPings.shift();
            }
            return;
        }

        var remainingLifetime = MAP_PING_LIFETIME_MS -
            Math.max(0, Date.now() - ping.receivedAt);
        if (remainingLifetime <= 0) {
            return;
        }

        var shell = document.createElement("div");
        var firstRing = document.createElement("span");
        var secondRing = document.createElement("span");
        var core = document.createElement("span");
        var label = document.createElement("span");
        shell.className = "map-ping-marker";
        firstRing.className = "map-ping-ring map-ping-ring-one";
        secondRing.className = "map-ping-ring map-ping-ring-two";
        core.className = "map-ping-core";
        label.className = "map-ping-label";
        label.textContent = ping.label || "Ping";
        shell.appendChild(firstRing);
        shell.appendChild(secondRing);
        shell.appendChild(core);
        shell.appendChild(label);

        var marker = L.marker(worldToLatLng(ping.x, ping.z), {
            icon: L.divIcon({
                className: "map-ping-div-icon",
                html: shell.outerHTML,
                iconAnchor: [0, 0],
                iconSize: [1, 1]
            }),
            interactive: false,
            keyboard: false,
            zIndexOffset: 1200
        }).addTo(pingLayer);
        var record = { marker: marker, timer: 0 };
        activePingMarkers.add(record);
        record.timer = window.setTimeout(function () {
            activePingMarkers.delete(record);
            if (pingLayer) {
                pingLayer.removeLayer(marker);
            }
        }, remainingLifetime);
    }

    function handlePingPayload(payload) {
        if (!payload || typeof payload !== "object") {
            return;
        }
        var x = Number(payload.x);
        var z = Number(payload.z);
        if (!Number.isFinite(x) || !Number.isFinite(z)) {
            return;
        }
        renderMapPing({
            x: x,
            z: z,
            label: typeof payload.label === "string" ? payload.label : "Ping",
            unixMs: Number(payload.unixMs) || Date.now(),
            receivedAt: Date.now()
        });
    }

    function matchingPlayerMarker(playerName) {
        var normalizedName = playerName.trim().toLowerCase();
        if (!normalizedName) {
            return null;
        }

        var match = null;
        markerRecords.forEach(function (record) {
            if (match || !record.player) {
                return;
            }
            var candidateName = typeof record.player.name === "string"
                ? record.player.name.trim().toLowerCase()
                : "";
            if (candidateName === normalizedName) {
                match = record;
            }
        });
        return match;
    }

    function removeChatBubble(record) {
        if (!record || record.removed) {
            return;
        }

        record.removed = true;
        window.clearTimeout(record.timer);
        if (chatLayer && record.marker) {
            chatLayer.removeLayer(record.marker);
        }
        var index = activeChatBubbles.indexOf(record);
        if (index !== -1) {
            activeChatBubbles.splice(index, 1);
        }
    }

    function renderChatBubble(chat) {
        if (currentView === "public") {
            return;
        }
        if (!map || !chatLayer) {
            pendingChatBubbles.push(chat);
            while (pendingChatBubbles.length > CHAT_BUBBLE_LIMIT) {
                pendingChatBubbles.shift();
            }
            return;
        }

        var remainingLifetime = CHAT_BUBBLE_LIFETIME_MS -
            Math.max(0, Date.now() - chat.receivedAt);
        if (remainingLifetime <= 0) {
            return;
        }

        var playerRecord = matchingPlayerMarker(chat.playerName);
        var anchor = playerRecord
            ? playerRecord.marker.getLatLng()
            : worldToLatLng(chat.x, chat.z);
        var shell = document.createElement("div");
        var name = document.createElement("span");
        var text = document.createElement("span");
        shell.className = "map-chat-bubble" + (chat.shout ? " is-shout" : "");
        name.className = "map-chat-name";
        text.className = "map-chat-text";
        name.textContent = (chat.shout ? "📯 " : "") +
            (chat.shout ? chat.playerName.toUpperCase() : chat.playerName);
        text.textContent = chat.text;
        shell.appendChild(name);
        shell.appendChild(text);

        var marker = L.marker(anchor, {
            icon: L.divIcon({
                className: "map-chat-div-icon",
                html: shell.outerHTML,
                iconAnchor: [0, 0],
                iconSize: [1, 1]
            }),
            interactive: false,
            keyboard: false,
            zIndexOffset: 1400
        }).addTo(chatLayer);
        var record = {
            marker: marker,
            playerKey: playerRecord ? playerRecord.player.key : "",
            playerName: chat.playerName,
            removed: false,
            timer: 0
        };
        while (activeChatBubbles.length >= CHAT_BUBBLE_LIMIT) {
            removeChatBubble(activeChatBubbles[0]);
        }
        activeChatBubbles.push(record);
        record.timer = window.setTimeout(function () {
            removeChatBubble(record);
        }, remainingLifetime);
    }

    function updateChatBubblesForPlayer(playerKey, latLng) {
        activeChatBubbles.forEach(function (record) {
            if (record.playerKey === playerKey) {
                record.marker.setLatLng(latLng);
            }
        });
    }

    function updateChatBubblePositions() {
        activeChatBubbles.forEach(function (record) {
            var playerRecord = record.playerKey
                ? markerRecords.get(record.playerKey)
                : matchingPlayerMarker(record.playerName);
            if (!playerRecord) {
                return;
            }
            record.playerKey = playerRecord.player.key;
            record.marker.setLatLng(playerRecord.marker.getLatLng());
        });
    }

    function clearChatBubbles() {
        pendingChatBubbles = [];
        activeChatBubbles.slice().forEach(removeChatBubble);
    }

    function formatChatTime(unixMs) {
        var date = new Date(unixMs);
        var hours = String(date.getHours()).padStart(2, "0");
        var minutes = String(date.getMinutes()).padStart(2, "0");
        return hours + ":" + minutes;
    }

    function normalizeChatPayload(payload) {
        if (!payload || typeof payload !== "object") {
            return null;
        }

        var sequence = Number(payload.sequence);
        var x = Number(payload.x);
        var z = Number(payload.z);
        var unixMs = Number(payload.unixMs);
        var playerName = typeof payload.playerName === "string"
            ? payload.playerName.trim()
            : "";
        var text = typeof payload.text === "string" ? payload.text.trim() : "";
        if (!Number.isFinite(sequence) || sequence <= 0 ||
            !Number.isFinite(x) || !Number.isFinite(z) ||
            !Number.isFinite(unixMs) || unixMs <= 0 ||
            !text) {
            return null;
        }

        return {
            playerName: playerName || "A viking",
            receivedAt: Date.now(),
            sequence: Math.floor(sequence),
            shout: payload.shout === true,
            text: text.slice(0, 256),
            unixMs: Math.floor(unixMs),
            x: x,
            z: z
        };
    }

    function renderChatHistory() {
        var distanceFromBottom = elements.chatList.scrollHeight -
            elements.chatList.scrollTop - elements.chatList.clientHeight;
        var followLatest = distanceFromBottom <= 24;
        elements.chatList.textContent = "";
        elements.chatNote.hidden = chatHistory.length > 0;
        chatHistory.forEach(function (chat) {
            var item = document.createElement("li");
            var meta = document.createElement("div");
            var time = document.createElement("time");
            var sender = document.createElement("strong");
            var kind = document.createElement("span");
            var message = document.createElement("p");
            item.className = "chat-entry" + (chat.shout ? " is-shout" : "");
            meta.className = "chat-entry-meta";
            time.className = "chat-entry-time";
            time.dateTime = new Date(chat.unixMs).toISOString();
            time.textContent = formatChatTime(chat.unixMs);
            sender.className = "chat-entry-sender";
            sender.textContent = chat.playerName;
            kind.className = "chat-entry-kind";
            kind.textContent = chat.shout ? "Shout" : "Say";
            message.className = "chat-entry-text";
            message.textContent = chat.text;
            meta.appendChild(time);
            meta.appendChild(sender);
            meta.appendChild(kind);
            item.appendChild(meta);
            item.appendChild(message);
            elements.chatList.appendChild(item);
        });
        if (followLatest) {
            elements.chatList.scrollTop = elements.chatList.scrollHeight;
        }
    }

    function appendChatHistory(chat, deferRender) {
        if (chatSequences.has(chat.sequence)) {
            return false;
        }

        chatSequences.add(chat.sequence);
        chatHistory.push(chat);
        chatHistory.sort(function (left, right) {
            return left.sequence - right.sequence;
        });
        chatHistory = chatHistory.slice(-CHAT_HISTORY_LIMIT);
        if (!deferRender) {
            renderChatHistory();
        }
        return true;
    }

    async function ensureChatHistory() {
        if ((currentView !== "admin" && currentView !== "shared") ||
            chatHistoryRequested || (eventSource && !eventSourceOpen)) {
            return;
        }

        chatHistoryRequested = true;
        var requestSequence = ++chatHistoryRequestSequence;
        try {
            var payload = await fetchJson("/api/chat");
            if (requestSequence !== chatHistoryRequestSequence ||
                currentView === "public" || !payload ||
                !Array.isArray(payload.chats)) {
                return;
            }

            payload.chats.forEach(function (entry) {
                var chat = normalizeChatPayload(entry);
                if (chat) {
                    appendChatHistory(chat, true);
                }
            });
            renderChatHistory();
        } catch (error) {
            return;
        }
    }

    function handleChatPayload(payload) {
        if (currentView === "public") {
            return;
        }

        var chat = normalizeChatPayload(payload);
        if (!chat) {
            return;
        }
        appendChatHistory(chat, false);
        if (liveChatSequences.has(chat.sequence)) {
            return;
        }
        liveChatSequences.add(chat.sequence);

        var sagaId = "chat:" + chat.unixMs + ":" + chat.sequence;
        if (!sagaChatEvents.some(function (event) { return event.id === sagaId; })) {
            sagaChatEvents.push({
                data: {
                    name: chat.playerName,
                    shout: chat.shout,
                    text: chat.text
                },
                id: sagaId,
                type: "chat",
                unixMs: chat.unixMs
            });
            sagaChatEvents.sort(function (left, right) {
                return right.unixMs - left.unixMs;
            });
            sagaChatEvents = sagaChatEvents.slice(0, SAGA_EVENT_LIMIT);
            renderSagaFeed();
        }
        renderChatBubble(chat);
    }

    function setChatSendNotice(message) {
        elements.chatSendNotice.textContent = message || "";
        elements.chatSendNotice.hidden = !message;
    }

    async function sendAdminChat() {
        if (chatSendPending || currentView !== "admin") {
            return;
        }

        var text = elements.chatInput.value.trim();
        if (!text) {
            setChatSendNotice("Enter a message first");
            return;
        }
        if (text.length > 256) {
            setChatSendNotice("Messages must be 256 characters or fewer");
            return;
        }

        chatSendPending = true;
        elements.chatInput.disabled = true;
        elements.chatSend.disabled = true;
        setChatSendNotice("");
        try {
            var payload = await fetchConsoleJson("/api/admin/chat", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "X-LiveMap-Token": token,
                    "X-Operator": webPinOperatorAuthor()
                },
                body: JSON.stringify({ text: text })
            });
            if (!payload || payload.ok !== true) {
                throw new Error(payload && payload.error ? payload.error : "Message rejected");
            }
            elements.chatInput.value = "";
        } catch (error) {
            setChatSendNotice(
                error && error.message ? error.message : "Message could not be sent"
            );
        } finally {
            chatSendPending = false;
            elements.chatInput.disabled = false;
            elements.chatSend.disabled = false;
            if (currentView === "admin") {
                elements.chatInput.focus();
            }
        }
    }

    function copyFromButton(button) {
        return copyText(button.getAttribute("data-copy") || "").then(function () {
            flashCopyButton(button);
        }).catch(function () {
            return;
        });
    }

    function updateCoordinateChip(latLng) {
        if (!coordinateChip || !worldBounds || !latLng || !worldBounds.contains(latLng)) {
            if (coordinateChip) {
                coordinateChip.hidden = true;
            }
            return;
        }

        var world = latLngToWorld(latLng);
        if (!world || !Number.isFinite(world.x) || !Number.isFinite(world.z)) {
            coordinateChip.hidden = true;
            return;
        }
        var x = Math.round(world.x);
        var z = Math.round(world.z);
        var label = "X " + x + " · Z " + z;
        coordinateChip.hidden = false;
        coordinateChip.setAttribute("data-copy", x + ", " + z);
        coordinateChip._voCopyLabel = label;
        if (!coordinateChip.classList.contains("is-copied")) {
            coordinateChip.textContent = label;
        }
    }

    function createCoordinateControl() {
        var CoordinateControl = L.Control.extend({
            options: { position: "bottomright" },
            onAdd: function () {
                coordinateChip = L.DomUtil.create("button", "leaflet-control coordinate-chip vo-copy");
                coordinateChip.type = "button";
                coordinateChip.title = "Copy world coordinates";
                coordinateChip.setAttribute("aria-label", "Copy world coordinates");
                coordinateChip.hidden = true;
                addAppListener(coordinateChip, "click", function () {
                    copyFromButton(coordinateChip);
                });
                L.DomEvent.disableClickPropagation(coordinateChip);
                L.DomEvent.disableScrollPropagation(coordinateChip);
                return coordinateChip;
            }
        });

        new CoordinateControl().addTo(map);
        // Touch uses honest map-center coordinates; a long-press crosshair is future polish.
        if (coordinateUsesMapCenter) {
            map.on("moveend", function () {
                updateCoordinateChip(map.getCenter());
            });
            if (map._loaded) {
                updateCoordinateChip(map.getCenter());
            }
        } else {
            map.on("mousemove", function (event) {
                updateCoordinateChip(event.latlng);
            });
            addAppListener(map.getContainer(), "mouseleave", function () {
                coordinateChip.hidden = true;
            });
        }
    }

    function dismissMapContextMenu() {
        window.clearTimeout(mapContextMenuTimer);
        mapContextMenuTimer = 0;
        mapContextMenuGeneration += 1;
        if (mapContextMenu) {
            mapContextMenu.hidden = true;
        }
    }

    function ensureMapContextMenu() {
        if (mapContextMenu) {
            return mapContextMenu;
        }

        mapContextMenu = document.createElement("div");
        mapContextMenu.className = "vo-context-menu";
        mapContextMenu.setAttribute("role", "menu");
        mapContextMenu.setAttribute("aria-label", "Map actions");
        mapContextMenu.hidden = true;
        appRoot.appendChild(mapContextMenu);
        return mapContextMenu;
    }

    function createMapContextItem(label, action) {
        var item = document.createElement("button");
        item.type = "button";
        item.className = "vo-context-item";
        item.setAttribute("role", "menuitem");
        item.textContent = label;
        addAppListener(item, "click", function (event) {
            event.preventDefault();
            event.stopPropagation();
            action(item);
        });
        return item;
    }

    function positionMapContextMenu(menu, clientX, clientY) {
        var margin = 8;
        var viewportWidth = styleRoot.clientWidth;
        var viewportHeight = styleRoot.clientHeight;
        menu.style.left = "0px";
        menu.style.top = "0px";
        menu.style.visibility = "hidden";
        menu.hidden = false;
        var bounds = menu.getBoundingClientRect();
        var maximumLeft = Math.max(margin, viewportWidth - bounds.width - margin);
        var maximumTop = Math.max(margin, viewportHeight - bounds.height - margin);
        menu.style.left = Math.max(margin, Math.min(clientX, maximumLeft)) + "px";
        menu.style.top = Math.max(margin, Math.min(clientY, maximumTop)) + "px";
        menu.style.visibility = "visible";
    }

    function showMapContextMenu(event) {
        var latLng = map.containerPointToLatLng(map.mouseEventToContainerPoint(event));
        var world = latLngToWorld(latLng);
        if (!world || !Number.isFinite(world.x) || !Number.isFinite(world.z)) {
            dismissMapContextMenu();
            return;
        }

        dismissMapContextMenu();
        var menu = ensureMapContextMenu();
        var generation = mapContextMenuGeneration;
        while (menu.firstChild) {
            menu.removeChild(menu.firstChild);
        }

        menu.appendChild(createMapContextItem("Copy coordinates", function (item) {
            var coordinates = Math.round(world.x) + ", " + Math.round(world.z);
            copyText(coordinates).then(function () {
                if (generation !== mapContextMenuGeneration || menu.hidden) {
                    return;
                }
                item.textContent = "Copied";
                item.classList.add("is-copied");
                mapContextMenuTimer = window.setTimeout(dismissMapContextMenu, 650);
            }).catch(dismissMapContextMenu);
        }));
        menu.appendChild(createMapContextItem("Measure from here", function () {
            dismissMapContextMenu();
            if (measureModeEnabled) {
                clearMeasurement();
            }
            startMeasurement(latLng);
        }));
        menu.appendChild(createMapContextItem("Center here", function () {
            dismissMapContextMenu();
            map.panTo(latLng);
        }));
        if (canCreateWebPin()) {
            menu.appendChild(createMapContextItem("Drop pin here", function () {
                dismissMapContextMenu();
                openWebPinDialog({ world: world });
            }));
        }
        if (currentView === "admin") {
            menu.appendChild(createMapContextItem("Ping in-game here", function () {
                dismissMapContextMenu();
                disarmMapPing();
                if (!pingRequestPending && currentView === "admin") {
                    sendMapPing(world);
                }
            }));
        }

        positionMapContextMenu(menu, event.clientX, event.clientY);
    }

    function bindMapContextMenu() {
        if (!window.matchMedia("(hover: hover) and (pointer: fine)").matches) {
            return;
        }

        addAppListener(map.getContainer(), "contextmenu", function (event) {
            if (event._simulated ||
                (event.sourceCapabilities && event.sourceCapabilities.firesTouchEvents)) {
                return;
            }
            event.preventDefault();
            event.stopImmediatePropagation();
            showMapContextMenu(event);
        }, true);
        addAppListener(document, "mousedown", function (event) {
            if (!eventInsideApp(event)) {
                return;
            }
            if (mapContextMenu && !mapContextMenu.hidden &&
                !mapContextMenu.contains(event.target)) {
                dismissMapContextMenu();
            }
        }, true);
        addAppListener(document, "click", function (event) {
            if (!eventInsideApp(event)) {
                return;
            }
            if (mapContextMenu && !mapContextMenu.hidden &&
                !mapContextMenu.contains(event.target)) {
                dismissMapContextMenu();
            }
        }, true);
        addKeyboardListener(function (event) {
            if (event.key === "Escape" && mapContextMenu && !mapContextMenu.hidden) {
                event.preventDefault();
                event.stopImmediatePropagation();
                dismissMapContextMenu();
            }
        }, true);
        map.on("dragstart zoomstart", dismissMapContextMenu);
    }

    function createFullscreenControl() {
        var FullscreenControl = L.Control.extend({
            options: { position: "topleft" },
            onAdd: function () {
                var container = L.DomUtil.create("div", "leaflet-control leaflet-bar map-tool-control");
                var button = L.DomUtil.create("button", "map-tool-button fullscreen-button", container);

                function fullscreenElement() {
                    var activeElement =
                        document.fullscreenElement || document.webkitFullscreenElement;
                    return !embedMode || activeElement === appRoot ? activeElement : null;
                }

                function syncFullscreenButton() {
                    var isFullscreen = Boolean(fullscreenElement());
                    button.textContent = isFullscreen ? "⤡" : "⛶";
                    button.title = isFullscreen ? "Exit fullscreen" : "Enter fullscreen";
                    button.setAttribute("aria-label", button.title);
                    button.setAttribute("aria-pressed", String(isFullscreen));
                }

                button.type = "button";
                addAppListener(button, "click", function () {
                    var action;
                    if (fullscreenElement()) {
                        action = document.exitFullscreen || document.webkitExitFullscreen;
                        if (action) {
                            var exitResult = action.call(document);
                            if (exitResult && typeof exitResult.catch === "function") {
                                exitResult.catch(function () {
                                    return;
                                });
                            }
                        }
                    } else {
                        action = styleRoot.requestFullscreen ||
                            styleRoot.webkitRequestFullscreen;
                        if (action) {
                            var result = action.call(styleRoot);
                            if (result && typeof result.catch === "function") {
                                result.catch(function () {
                                    return;
                                });
                            }
                        }
                    }
                });
                addAppListener(document, "fullscreenchange", syncFullscreenButton);
                addAppListener(document, "webkitfullscreenchange", syncFullscreenButton);
                syncFullscreenButton();
                L.DomEvent.disableClickPropagation(container);
                L.DomEvent.disableScrollPropagation(container);
                return container;
            }
        });

        new FullscreenControl().addTo(map);
    }

    function mapSearchRegistry() {
        var items = [];
        latestPlayers.forEach(function (player) {
            items.push({
                glyph: "●",
                kind: "Player",
                layerKey: "players",
                latLng: worldToLatLng(player.x, player.z),
                name: player.displayName,
                searchText: player.displayName,
                x: player.x,
                z: player.z,
                markerResolver: function () {
                    var record = markerRecords.get(player.key);
                    return record ? record.marker : null;
                }
            });
        });
        latestPins.forEach(function (pin) {
            items.push({
                glyph: "⌖",
                kind: "Pin",
                layerKey: "pins",
                latLng: pin.latLng || worldToLatLng(pin.x, pin.z),
                name: pin.name,
                searchText: pin.author ? pin.name + " " + pin.author : pin.name,
                x: pin.x,
                z: pin.z,
                markerResolver: function () {
                    return findMarkerNearLayer(pinLayer, pin.latLng);
                }
            });
        });
        latestWebPins.forEach(function (pin) {
            items.push({
                glyph: "✦",
                kind: "Web pin",
                layerKey: "webpins",
                latLng: pin.latLng || worldToLatLng(pin.x, pin.z),
                name: pin.label || "Web pin",
                searchText: (pin.label || "Web pin") + " " + pin.author,
                x: pin.x,
                z: pin.z,
                markerResolver: function () {
                    return findMarkerNearLayer(webPinLayer, pin.latLng);
                }
            });
        });
        POI_GROUP_ORDER.forEach(function (group) {
            var definition = POI_GROUPS[group];
            var metadata = poiGroupMeta.get(group);
            if (availablePoiGroups.has(group) && metadata && metadata.inline === false) {
                items.push({
                    glyph: definition.glyph,
                    groupOnly: true,
                    kind: "POI layer",
                    layerKey: group,
                    name: definition.label,
                    searchText: definition.label
                });
            }
            if (definition.searchGroupOnly) {
                return;
            }
            (poiRecords.get(group) || []).forEach(function (record) {
                items.push({
                    glyph: definition.glyph,
                    iconKey: bossIconKey(record),
                    kind: definition.label,
                    layerKey: group,
                    latLng: record.latLng,
                    name: record.title,
                    searchText: record.title + " " + definition.label,
                    x: record.x,
                    z: record.z,
                    markerResolver: function () {
                        return findMarkerNearLayer(poiLayers.get(group), record.latLng);
                    }
                });
            });
        });
        return items;
    }

    function rankMapSearchItem(item, queryText) {
        var haystack = item.searchText.toLocaleLowerCase();
        if (haystack.indexOf(queryText) === 0) {
            return 0;
        }
        var words = haystack.split(/\s+/);
        if (words.some(function (word) { return word.indexOf(queryText) === 0; })) {
            return 1;
        }
        return haystack.indexOf(queryText) !== -1 ? 2 : -1;
    }

    function clearCoordinateSearchMarker() {
        window.clearTimeout(coordinateSearchTimer);
        coordinateSearchTimer = 0;
        if (coordinateSearchMarker && map) {
            map.removeLayer(coordinateSearchMarker);
        }
        coordinateSearchMarker = null;
    }

    function showCoordinateSearchMarker(latLng) {
        clearCoordinateSearchMarker();
        coordinateSearchMarker = L.marker(latLng, {
            icon: L.divIcon({
                className: "map-search-coordinate-pulse",
                html: '<span class="map-search-coordinate-pulse-ring"></span>',
                iconSize: [30, 30],
                iconAnchor: [15, 15]
            }),
            interactive: false,
            keyboard: false,
            zIndexOffset: 1100
        }).addTo(map);
        coordinateSearchTimer = window.setTimeout(function () {
            clearCoordinateSearchMarker();
        }, COORDINATE_SEARCH_PULSE_MS);
    }

    function goToMapCoordinates(world) {
        if (!mapCoordinatesInsideWorld(world)) {
            showNoticeToast("Those coordinates are outside the world");
            return;
        }
        var latLng = worldToLatLng(world.x, world.z);
        setMapSearchOpen(false, false);
        focusMapLocation(latLng);
        showCoordinateSearchMarker(latLng);
    }

    function setMapSearchSelection(nextIndex) {
        var buttons = searchResultsElement
            ? Array.prototype.slice.call(searchResultsElement.querySelectorAll(".map-search-result"))
            : [];
        if (buttons.length === 0) {
            searchResultIndex = -1;
            return;
        }
        searchResultIndex = (nextIndex + buttons.length) % buttons.length;
        buttons.forEach(function (button, index) {
            var selected = index === searchResultIndex;
            button.classList.toggle("is-selected", selected);
            button.setAttribute("aria-selected", String(selected));
        });
        searchInput.setAttribute("aria-activedescendant", buttons[searchResultIndex].id);
        buttons[searchResultIndex].scrollIntoView({ block: "nearest" });
    }

    function moveMapSearchSelection(direction) {
        if (searchResultItems.length === 0) {
            return;
        }
        var nextIndex = searchResultIndex < 0
            ? (direction > 0 ? 0 : searchResultItems.length - 1)
            : searchResultIndex + direction;
        setMapSearchSelection(nextIndex);
    }

    function renderMapSearchResults() {
        if (!searchInput || !searchResultsElement) {
            return;
        }
        var rawQueryText = searchInput.value.trim();
        var queryText = rawQueryText.toLocaleLowerCase();
        var coordinates = parseMapCoordinates(rawQueryText);
        searchResultsElement.textContent = "";
        searchResultItems = [];
        searchResultIndex = -1;
        searchInput.removeAttribute("aria-activedescendant");
        if (!queryText) {
            searchResultsElement.hidden = true;
            searchInput.setAttribute("aria-expanded", "false");
            return;
        }

        if (coordinates) {
            searchResultItems = [{
                coordinateSearch: true,
                glyph: "⌖",
                kind: "World coordinates",
                name: "Go to " + formatMapCoordinates(coordinates),
                x: coordinates.x,
                z: coordinates.z
            }];
        } else {
            searchResultItems = mapSearchRegistry().map(function (item) {
                return { item: item, rank: rankMapSearchItem(item, queryText) };
            }).filter(function (entry) {
                return entry.rank >= 0;
            }).sort(function (left, right) {
                return left.rank - right.rank || left.item.name.localeCompare(right.item.name);
            }).slice(0, 12).map(function (entry) {
                return entry.item;
            });
        }

        if (searchResultItems.length === 0) {
            var empty = document.createElement("div");
            empty.className = "map-search-empty";
            empty.textContent = "No matching players, pins, or places";
            searchResultsElement.appendChild(empty);
        } else {
            searchResultItems.forEach(function (item, index) {
                var button = document.createElement("button");
                var glyph = document.createElement("span");
                var copy = document.createElement("span");
                var name = document.createElement("strong");
                var kind = document.createElement("small");
                var coordinates = document.createElement("span");
                button.type = "button";
                button.className = "map-search-result";
                button.id = "map-search-option-" + index;
                button.setAttribute("role", "option");
                button.setAttribute("aria-selected", "false");
                glyph.className = "map-search-result-glyph";
                if (item.iconKey) {
                    glyph.innerHTML = iconMarkup(item.iconKey, item.glyph);
                } else {
                    glyph.textContent = item.glyph;
                }
                glyph.setAttribute("aria-hidden", "true");
                copy.className = "map-search-result-copy";
                name.textContent = item.name;
                kind.textContent = item.kind;
                coordinates.className = "map-search-result-coordinates";
                coordinates.textContent = item.groupOnly
                    ? "Layer"
                    : "X " + Math.round(item.x) + " · Z " + Math.round(item.z);
                copy.appendChild(name);
                copy.appendChild(kind);
                button.appendChild(glyph);
                button.appendChild(copy);
                button.appendChild(coordinates);
                addAppListener(button, "click", function () {
                    selectMapSearchResult(index);
                });
                searchResultsElement.appendChild(button);
            });
        }
        searchResultsElement.hidden = false;
        searchInput.setAttribute("aria-expanded", "true");
    }

    function setMapSearchOpen(isOpen, focusInput) {
        if (!searchControlElement) {
            return;
        }
        if (isOpen && measureModeEnabled &&
            window.matchMedia("(max-width: 899px)").matches) {
            clearMeasurement();
        }
        if (isOpen && layersSetCollapsed &&
            window.matchMedia("(max-width: 899px)").matches) {
            layersSetCollapsed(true);
        }
        if (isOpen && window.matchMedia("(max-width: 759px)").matches) {
            elements.sidebarState.checked = false;
        }
        searchControlElement.classList.toggle("is-open", isOpen);
        var toggle = searchControlElement.querySelector(".map-search-toggle");
        toggle.setAttribute("aria-expanded", String(isOpen));
        if (!isOpen) {
            searchResultsElement.hidden = true;
            searchInput.setAttribute("aria-expanded", "false");
            searchInput.removeAttribute("aria-activedescendant");
            searchResultIndex = -1;
            return;
        }
        if (focusInput) {
            searchInput.focus();
            searchInput.select();
        }
        renderMapSearchResults();
    }

    function selectMapSearchResult(index) {
        var item = searchResultItems[index];
        if (!item) {
            return;
        }
        if (item.coordinateSearch) {
            goToMapCoordinates({ x: item.x, z: item.z });
            return;
        }
        if (!layerSettings[item.layerKey]) {
            layerSettings[item.layerKey] = true;
            saveLayerSettings();
            renderLayerRows();
            syncLayerVisibility();
            if (Object.prototype.hasOwnProperty.call(ENTITY_GROUPS, item.layerKey)) {
                updateEntityPolling(true);
            }
        }
        setMapSearchOpen(false, false);
        if (item.groupOnly) {
            updateLazyPoiLoading();
            return;
        }
        focusMapLocation(item.latLng, item.markerResolver);
    }

    function activeElementAcceptsTyping() {
        var active = document.activeElement;
        if (!active) {
            return false;
        }
        return /^(INPUT|TEXTAREA|SELECT)$/.test(active.tagName) || active.isContentEditable;
    }

    function createSearchControl() {
        var SearchControl = L.Control.extend({
            options: { position: "topleft" },
            onAdd: function () {
                var container = L.DomUtil.create("section", "leaflet-control map-search-control");
                var shell = L.DomUtil.create("div", "map-search-shell", container);
                var toggle = L.DomUtil.create("button", "map-search-toggle", shell);
                searchInput = L.DomUtil.create("input", "map-search-input", shell);
                searchResultsElement = L.DomUtil.create("div", "map-search-results", container);
                searchControlElement = container;

                toggle.type = "button";
                toggle.title = "Search map (/ or Ctrl+K)";
                toggle.setAttribute("aria-label", toggle.title);
                toggle.setAttribute("aria-expanded", "false");
                toggle.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="11" cy="11" r="6"></circle><path d="M16 16l4 4"></path></svg>';
                searchInput.type = "search";
                searchInput.placeholder = "Search names or X, Z";
                searchInput.autocomplete = "off";
                searchInput.spellcheck = false;
                searchInput.setAttribute(
                    "aria-label",
                    "Search coordinates, players, pins, and places"
                );
                searchInput.setAttribute("role", "combobox");
                searchInput.setAttribute("aria-autocomplete", "list");
                searchInput.setAttribute("aria-controls", "map-search-results");
                searchInput.setAttribute("aria-expanded", "false");
                searchResultsElement.id = "map-search-results";
                searchResultsElement.className = "map-search-results";
                searchResultsElement.setAttribute("role", "listbox");
                searchResultsElement.hidden = true;

                addAppListener(toggle, "click", function () {
                    setMapSearchOpen(!container.classList.contains("is-open"), true);
                });
                addAppListener(searchInput, "input", renderMapSearchResults);
                addAppListener(searchInput, "keydown", function (event) {
                    var coordinates = parseMapCoordinates(searchInput.value);
                    if (event.key === "ArrowDown" && searchResultItems.length > 0) {
                        event.preventDefault();
                        moveMapSearchSelection(1);
                    } else if (event.key === "ArrowUp" && searchResultItems.length > 0) {
                        event.preventDefault();
                        moveMapSearchSelection(-1);
                    } else if (event.key === "Enter" && coordinates) {
                        event.preventDefault();
                        goToMapCoordinates(coordinates);
                    } else if (event.key === "Enter" && searchResultIndex >= 0) {
                        event.preventDefault();
                        selectMapSearchResult(searchResultIndex);
                    } else if (event.key === "Escape") {
                        event.preventDefault();
                        setMapSearchOpen(false, false);
                        toggle.focus();
                    }
                });
                addAppListener(container, "focusout", function () {
                    window.setTimeout(function () {
                        if (!container.contains(document.activeElement)) {
                            setMapSearchOpen(false, false);
                        }
                    }, 0);
                });
                L.DomEvent.disableClickPropagation(container);
                L.DomEvent.disableScrollPropagation(container);
                return container;
            }
        });

        new SearchControl().addTo(map);
        map.on("click", function () {
            setMapSearchOpen(false, false);
        });
        addKeyboardListener(function (event) {
            if (activeTab !== "map" || event.altKey || event.metaKey) {
                return;
            }
            var slashShortcut = event.key === "/" && !event.ctrlKey &&
                !event.shiftKey && !activeElementAcceptsTyping();
            var controlKShortcut = event.key.toLocaleLowerCase() === "k" && event.ctrlKey;
            if (!slashShortcut && !controlKShortcut) {
                return;
            }
            event.preventDefault();
            setMapSearchOpen(true, true);
        });
    }

    function loadMinimapPreference() {
        return layerSettings.minimap === true;
    }

    function saveMinimapPreference(isOpen) {
        layerSettings.minimap = isOpen;
        saveLayerSettings();
        if (layersRows) {
            var checkbox = layersRows.querySelector('input[data-layer-key="minimap"]');
            if (checkbox) {
                checkbox.checked = isOpen;
            }
        }
        updateLayerCounts();
    }

    function clampUnit(value) {
        return Math.max(0, Math.min(1, value));
    }

    function updateMinimapView() {
        minimapFrame = 0;
        if (!map || !mapMetrics || !worldBounds || !minimapElement || !minimapViewRect ||
            minimapElement.classList.contains("is-collapsed")) {
            return;
        }

        var longitudeSpan = worldBounds.getEast() - worldBounds.getWest();
        var latitudeSpan = worldBounds.getNorth() - worldBounds.getSouth();
        if (!Number.isFinite(longitudeSpan) || !Number.isFinite(latitudeSpan) ||
            longitudeSpan <= 0 || latitudeSpan <= 0) {
            return;
        }

        var bounds = map.getBounds();
        var left = clampUnit((bounds.getWest() - worldBounds.getWest()) / longitudeSpan);
        var right = clampUnit((bounds.getEast() - worldBounds.getWest()) / longitudeSpan);
        var top = clampUnit((worldBounds.getNorth() - bounds.getNorth()) / latitudeSpan);
        var bottom = clampUnit((worldBounds.getNorth() - bounds.getSouth()) / latitudeSpan);
        minimapViewRect.style.left = (left * 100).toFixed(2) + "%";
        minimapViewRect.style.top = (top * 100).toFixed(2) + "%";
        minimapViewRect.style.width = (Math.max(0, right - left) * 100).toFixed(2) + "%";
        minimapViewRect.style.height = (Math.max(0, bottom - top) * 100).toFixed(2) + "%";
    }

    function scheduleMinimapUpdate() {
        if (!minimapFrame) {
            minimapFrame = window.requestAnimationFrame(updateMinimapView);
        }
    }

    function createMinimapControl() {
        var MinimapControl = L.Control.extend({
            options: { position: "bottomright" },
            onAdd: function () {
                var container = L.DomUtil.create("section", "leaflet-control minimap-control");
                var frame = L.DomUtil.create("div", "minimap-frame", container);
                minimapImage = L.DomUtil.create("img", "minimap-image", frame);
                minimapViewRect = L.DomUtil.create("div", "minimap-view-rect", frame);
                var toggle = L.DomUtil.create("button", "minimap-toggle", container);
                var isOpen = loadMinimapPreference();

                function setOpen(nextOpen) {
                    isOpen = nextOpen;
                    container.classList.toggle("is-collapsed", !isOpen);
                    elements.mapPane.classList.toggle("has-open-minimap", isOpen);
                    toggle.setAttribute("aria-expanded", String(isOpen));
                    toggle.title = isOpen ? "Hide minimap" : "Show minimap";
                    toggle.setAttribute("aria-label", toggle.title);
                    if (isOpen) {
                        scheduleMinimapUpdate();
                    }
                }

                minimapSetOpen = function (nextOpen, persist) {
                    setOpen(Boolean(nextOpen));
                    if (persist !== false) {
                        saveMinimapPreference(isOpen);
                    }
                };

                minimapImage.src = versionedMapUrl("/base.png");
                minimapImage.alt = "World overview";
                minimapImage.draggable = false;
                toggle.type = "button";
                toggle.textContent = "◱";
                addAppListener(toggle, "click", function () {
                    minimapSetOpen(!isOpen, true);
                });
                addAppListener(frame, "click", function (event) {
                    var rectangle = frame.getBoundingClientRect();
                    if (!isOpen || rectangle.width <= 0 || rectangle.height <= 0 || !worldBounds) {
                        return;
                    }
                    var xAmount = clampUnit((event.clientX - rectangle.left) / rectangle.width);
                    var yAmount = clampUnit((event.clientY - rectangle.top) / rectangle.height);
                    clearFollow();
                    map.setView(L.latLng(
                        worldBounds.getNorth() - yAmount *
                            (worldBounds.getNorth() - worldBounds.getSouth()),
                        worldBounds.getWest() + xAmount *
                            (worldBounds.getEast() - worldBounds.getWest())
                    ), map.getZoom());
                });
                minimapElement = container;
                minimapSetOpen(isOpen, false);
                L.DomEvent.disableClickPropagation(container);
                L.DomEvent.disableScrollPropagation(container);
                return container;
            }
        });

        new MinimapControl().addTo(map);
        map.on("move zoom resize", scheduleMinimapUpdate);
    }

    function applyInitialHashState(defaultZoom) {
        var parameters = new URLSearchParams(appHash().replace(/^#/, ""));
        var settingsChanged = false;
        pendingCinemaFromHash = parameters.has("cinema");
        var hashStyle = parameters.get("st");
        if (["default", "topo", "chart"].indexOf(hashStyle) !== -1) {
            layerSettings.mapStyle = hashStyle;
            settingsChanged = true;
        }
        var layerKeys = parameters.get("ly");
        if (layerKeys) {
            layerKeys.split(",").forEach(function (key) {
                if (key !== "legendCollapsed" && key !== "densityDots" &&
                    key !== "iconSize" && key !== "mapStyle" &&
                    key !== "poiColors" && key !== "poiCollapsed" &&
                    key !== "poiOpacity" && key !== "timelapse" &&
                    key !== "timelapseSpeed" &&
                    typeof LAYER_DEFAULTS[key] === "boolean" &&
                    Object.prototype.hasOwnProperty.call(layerSettings, key)) {
                    layerSettings[key] = true;
                    settingsChanged = true;
                }
            });
        }
        if (settingsChanged) {
            saveLayerSettings();
            renderLayerRows();
        }

        var xText = parameters.get("x");
        var zText = parameters.get("z");
        var zoomText = parameters.get("zm");
        var x = Number(xText);
        var z = Number(zText);
        var zoom = Number(zoomText);
        if (xText !== null && xText !== "" && zText !== null && zText !== "" &&
            zoomText !== null && zoomText !== "" && Number.isFinite(x) &&
            Number.isFinite(z) && Number.isFinite(zoom)) {
            var worldExtent = mapMetrics.pixelSize * mapMetrics.textureSize / 2;
            x = Math.max(-worldExtent, Math.min(worldExtent, x));
            z = Math.max(-worldExtent, Math.min(worldExtent, z));
            zoom = Math.max(map.getMinZoom(), Math.min(map.getMaxZoom(), zoom));
            map.setView(worldToLatLng(x, z), zoom, { animate: false });
            hashViewApplied = true;
        } else {
            map.setView(worldToLatLng(0, 0), defaultZoom);
        }

        pendingHashFollowName = (parameters.get("follow") || "").trim();
    }

    function hashFollowName() {
        if (cinemaState) {
            return cinemaState.locked ? cinemaState.locked.trailKey : "";
        }
        if (pendingCinemaFromHash) {
            return pendingHashFollowName;
        }
        if (!followTarget) {
            return pendingHashFollowName;
        }

        if (followTarget.kind !== "player") {
            return followTarget.trailKey;
        }

        var record = markerRecords.get(followTarget.id);
        return record ? record.player.displayName : pendingHashFollowName;
    }

    function writeMapHash() {
        hashUpdateTimer = 0;
        if (embedMode || activeTab === "codex" || !map || !mapMetrics) {
            return;
        }

        var center = latLngToWorld(map.getCenter());
        if (!center) {
            return;
        }
        var parameters = new URLSearchParams();
        if (cinemaState || pendingCinemaFromHash) {
            parameters.set("cinema", "");
        }
        parameters.set("x", String(Math.round(center.x)));
        parameters.set("z", String(Math.round(center.z)));
        parameters.set("zm", String(Number(map.getZoom().toFixed(2))));
        if (layerSettings.mapStyle !== "default") {
            parameters.set("st", layerSettings.mapStyle);
        }
        var enabledNonDefaultLayers = Object.keys(layerSettings).filter(function (key) {
            return key !== "legendCollapsed" && key !== "densityDots" &&
                key !== "iconSize" && key !== "mapStyle" &&
                key !== "poiColors" && key !== "poiCollapsed" &&
                key !== "poiOpacity" && key !== "timelapse" &&
                key !== "timelapseSpeed" &&
                layerSettings[key] === true &&
                LAYER_DEFAULTS[key] === false;
        });
        if (enabledNonDefaultLayers.length > 0) {
            parameters.set("ly", enabledNonDefaultLayers.join(","));
        }
        var followName = hashFollowName();
        if (followName) {
            parameters.set("follow", followName);
        }

        var serialized = parameters.toString().replace(/(^|&)cinema=(?=&|$)/, "$1cinema");
        var hash = "#" + serialized;
        if (appHash() !== hash) {
            window.history.replaceState(
                window.history.state,
                "",
                window.location.pathname + window.location.search + hash
            );
        }
    }

    function scheduleHashUpdate() {
        if (embedMode) {
            return;
        }
        window.clearTimeout(hashUpdateTimer);
        hashUpdateTimer = window.setTimeout(writeMapHash, 400);
    }

    function applyPendingHashFollow() {
        if (pendingCinemaFromHash) {
            tryBootCinemaFromHash();
            return;
        }
        if (!pendingHashFollowName) {
            return;
        }
        if (pendingHashFollowName.startsWith("entity:")) {
            var entityRecord = entityMarkerRecords.get(pendingHashFollowName);
            if (entityRecord) {
                var entityKey = pendingHashFollowName;
                pendingHashFollowName = "";
                followEntity(entityRecord.entity.group, entityKey);
            }
            return;
        }
        if (latestPlayers.length === 0) {
            return;
        }
        var requestedName = pendingHashFollowName.toLocaleLowerCase();
        var match = latestPlayers.find(function (player) {
            return player.key === pendingHashFollowName ||
                player.trailKey === pendingHashFollowName ||
                player.displayName.toLocaleLowerCase() === requestedName;
        });
        if (match) {
            pendingHashFollowName = "";
            followPlayer(match.key);
        }
    }

    function createLayersControl() {
        var LayersControl = L.Control.extend({
            options: { position: "topright" },
            onAdd: function () {
                var container = L.DomUtil.create("section", "leaflet-control layers-control");
                var toggle = L.DomUtil.create("button", "layers-toggle", container);
                var title = L.DomUtil.create("span", "layers-title", toggle);
                var chevron = L.DomUtil.create("span", "layers-chevron", toggle);
                var initiallyCollapsed = window.matchMedia("(max-width: 759px)").matches;

                toggle.type = "button";
                title.textContent = "Layers";
                chevron.textContent = "›";
                chevron.setAttribute("aria-hidden", "true");
                layersRows = L.DomUtil.create("div", "layers-rows", container);

                function setCollapsed(isCollapsed) {
                    if (!isCollapsed && window.matchMedia("(max-width: 759px)").matches) {
                        elements.sidebarState.checked = false;
                    }
                    if (!isCollapsed && searchControlElement &&
                        window.matchMedia("(max-width: 899px)").matches) {
                        setMapSearchOpen(false, false);
                    }
                    if (!isCollapsed && measureModeEnabled &&
                        window.matchMedia("(max-width: 899px)").matches) {
                        clearMeasurement();
                    }
                    container.classList.toggle("is-collapsed", isCollapsed);
                    layersRows.hidden = isCollapsed;
                    toggle.setAttribute("aria-expanded", String(!isCollapsed));
                    window.clearInterval(layersStalenessTimer);
                    layersStalenessTimer = 0;
                    if (!isCollapsed) {
                        updateFeedStalenessDots();
                        layersStalenessTimer = window.setInterval(
                            updateFeedStalenessDots,
                            5000
                        );
                    }
                }

                layersSetCollapsed = setCollapsed;
                setCollapsed(initiallyCollapsed);
                L.DomEvent.on(toggle, "click", function () {
                    setCollapsed(!container.classList.contains("is-collapsed"));
                });
                L.DomEvent.disableClickPropagation(container);
                L.DomEvent.disableScrollPropagation(container);
                return container;
            }
        });

        new LayersControl().addTo(map);
        renderLayerRows();
    }

    function renderLayerRows() {
        if (!layersRows) {
            return;
        }

        layersRows.textContent = "";
        legendContent = null;
        heatmapWindowControlElement = null;
        renderJumpChips();

        var liveFeeds = hasLiveAccess() ? ["players", "entities"] : ["players"];
        var liveBody = appendLayerSection("live", "Live", liveFeeds);
        appendLayerRow(liveBody, "players", "Players", "●", "players");
        appendLayerRow(liveBody, "trails", "Trails", "〰", "trails");
        if (hasLiveAccess() && availablePoiGroups.has("ghosts")) {
            appendLayerRow(liveBody, "ghosts", "Last seen", "♙", "ghosts");
        }
        if (hasLiveAccess() && entityAvailability !== "unavailable") {
            ENTITY_GROUP_ORDER.forEach(function (group) {
                appendLayerRow(
                    liveBody,
                    group,
                    ENTITY_GROUPS[group].label,
                    ENTITY_GROUPS[group].glyph,
                    group
                );
            });
            if (entityAvailability === "unknown") {
                appendLayerStatus(liveBody, "Entity data: no data yet");
            }
        } else if (hasLiveAccess()) {
            appendLayerStatus(liveBody, "Entity data unavailable");
        }
        if (currentRaidEvent) {
            appendDisplayLayerRow(liveBody, "Raid area", "◯", "raid", "1");
        }

        var placeFeeds = ["pins", "pois"];
        if (webPinsAvailable) {
            placeFeeds.splice(1, 0, "webpins");
        }
        var placesBody = appendLayerSection("places", "Places", placeFeeds);
        appendLayerRow(placesBody, "pins", "Pins", "⌖", "pins");
        if (webPinsAvailable) {
            appendLayerRow(placesBody, "webpins", "Web pins", "✦", "webpins");
        }
        POI_CATEGORIES.forEach(function (category) {
            var groups = category.groups.filter(function (group) {
                return availablePoiGroups.has(group);
            });
            if (groups.length === 0) {
                return;
            }

            var categoryBody = appendPoiCategory(placesBody, category);
            groups.forEach(function (group) {
                appendLayerRow(
                    categoryBody,
                    group,
                    POI_GROUPS[group].label,
                    POI_GROUPS[group].glyph,
                    group
                );
                appendPoiTruncationNote(categoryBody, group);
            });
        });
        if (feedLastUpdated.pins === 0) {
            appendLayerStatus(placesBody, "Pins: no save yet");
        }
        if (feedLastUpdated.pois === 0 || poiLoadPending) {
            appendLayerStatus(placesBody, "POIs: no data yet");
        }

        var overlayFeeds = hasLiveAccess()
            ? ["fog", "entities", "heatmap"]
            : ["fog"];
        var overlaysBody = appendLayerSection("overlays", "Overlays", overlayFeeds);
        appendMapStyleControl(overlaysBody);
        if (fogAvailable) {
            appendLayerRow(overlaysBody, "fog", "Fog", "≈", "fog", { counted: false });
        }
        if (hasLiveAccess()) {
            appendLayerRow(
                overlaysBody,
                "heatmap",
                "Activity Heatmap",
                "▦",
                "heatmap",
                { counted: false }
            );
            appendHeatmapWindowControl(overlaysBody);
        }
        if (timelapseHasAccess() && timelapseAvailability === "available") {
            appendLayerRow(
                overlaysBody,
                "timelapse",
                "World Timelapse",
                "◷",
                "timelapse",
                { counted: false, rowClass: "timelapse-layer-row" }
            );
        }
        appendLayerRow(
            overlaysBody,
            "regions",
            "Region names",
            "Aa",
            "regions",
            { counted: false }
        );
        appendLayerRow(
            overlaysBody,
            "portalNetwork",
            "Portal network",
            "╌",
            "portal-network"
        );
        appendLayerRow(
            overlaysBody,
            "tint",
            "Day/night tint",
            "◐",
            "tint",
            { counted: false }
        );
        appendLayerRow(
            overlaysBody,
            "minimap",
            "Minimap",
            "◱",
            "minimap",
            { counted: false }
        );

        appendLayerPreferences();
        appendLegendBlock();
        updateLayerCounts();
        updatePoiZoomGateRows();
        updateFeedStalenessDots();
    }

    function appendLayerSection(key, labelText, feeds) {
        var section = document.createElement("section");
        var header = document.createElement("header");
        var identity = document.createElement("div");
        var name = document.createElement("span");
        var dots = document.createElement("span");
        var actions = document.createElement("div");
        var count = document.createElement("span");
        var allButton = document.createElement("button");
        var noneButton = document.createElement("button");
        var body = document.createElement("div");

        section.className = "layer-section";
        section.dataset.layerSection = key;
        header.className = "layer-section-header";
        identity.className = "layer-section-identity";
        name.className = "layer-section-name";
        name.textContent = labelText;
        dots.className = "layer-feed-dots";
        feeds.forEach(function (feed) {
            var dot = document.createElement("span");
            dot.className = "feed-staleness-dot is-grey";
            dot.dataset.feed = feed;
            dot.setAttribute("aria-label", feed + " not loaded");
            dots.appendChild(dot);
        });
        actions.className = "layer-section-actions";
        count.className = "layer-section-count";
        count.dataset.sectionCount = key;
        allButton.type = "button";
        allButton.className = "layer-section-mini";
        allButton.textContent = "all";
        allButton.setAttribute("aria-label", "Show all " + labelText.toLowerCase() + " layers");
        addAppListener(allButton, "click", function () {
            setSectionLayers(section, true);
        });
        noneButton.type = "button";
        noneButton.className = "layer-section-mini";
        noneButton.textContent = "none";
        noneButton.setAttribute("aria-label", "Hide all " + labelText.toLowerCase() + " layers");
        addAppListener(noneButton, "click", function () {
            setSectionLayers(section, false);
        });
        body.className = "layer-section-body";

        identity.appendChild(name);
        identity.appendChild(dots);
        actions.appendChild(count);
        actions.appendChild(allButton);
        actions.appendChild(noneButton);
        header.appendChild(identity);
        header.appendChild(actions);
        section.appendChild(header);
        section.appendChild(body);
        layersRows.appendChild(section);
        return body;
    }

    function appendMapStyleControl(parent) {
        var row = document.createElement("div");
        var title = document.createElement("span");
        var segments = document.createElement("div");
        row.className = "layer-map-style";
        title.className = "layer-map-style-title";
        title.textContent = "Map style";
        segments.className = "layer-map-style-segments";
        segments.setAttribute("role", "group");
        segments.setAttribute("aria-label", "Map style");
        [["default", "Default"], ["topo", "Topographic"], ["chart", "Old Chart"]]
            .forEach(function (choice) {
                var button = document.createElement("button");
                var isSelected = layerSettings.mapStyle === choice[0];
                button.type = "button";
                button.className = "layer-map-style-option" +
                    (isSelected ? " is-selected" : "");
                button.dataset.mapStyle = choice[0];
                button.textContent = choice[1];
                button.setAttribute("aria-pressed", String(isSelected));
                addAppListener(button, "click", function () {
                    selectMapStyle(choice[0]);
                });
                segments.appendChild(button);
            });
        row.appendChild(title);
        row.appendChild(segments);
        parent.appendChild(row);
    }

    function appendHeatmapWindowControl(parent) {
        var row = document.createElement("div");
        var title = document.createElement("span");
        var segments = document.createElement("div");
        row.className = "heatmap-window-control";
        row.hidden = !layerSettings.heatmap;
        title.className = "heatmap-window-title";
        title.textContent = "Window";
        segments.className = "heatmap-window-segments";
        segments.setAttribute("role", "group");
        segments.setAttribute("aria-label", "Activity heatmap window");
        [["24h", "24h"], ["7d", "7d"]].forEach(function (choice) {
            var button = document.createElement("button");
            var isSelected = layerSettings.heatmapWindow === choice[0];
            button.type = "button";
            button.className = "heatmap-window-option" +
                (isSelected ? " is-selected" : "");
            button.dataset.heatmapWindow = choice[0];
            button.textContent = choice[1];
            button.setAttribute("aria-pressed", String(isSelected));
            addAppListener(button, "click", function () {
                selectHeatmapWindow(choice[0]);
            });
            segments.appendChild(button);
        });
        row.appendChild(title);
        row.appendChild(segments);
        parent.appendChild(row);
        heatmapWindowControlElement = row;
    }

    function selectHeatmapWindow(windowName) {
        if (["24h", "7d"].indexOf(windowName) === -1 ||
            layerSettings.heatmapWindow === windowName) {
            return;
        }

        layerSettings.heatmapWindow = windowName;
        saveLayerSettings();
        latestHeatmap = null;
        heatmapRequestSequence++;
        window.clearTimeout(heatmapPollTimer);
        heatmapPollTimer = 0;
        if (heatmapLayer) {
            heatmapLayer.setData(null);
        }
        syncHeatmapControls();
        startHeatmapPolling();
    }

    function syncHeatmapControls() {
        var enabled = heatmapIsEnabled();
        if (heatmapWindowControlElement) {
            heatmapWindowControlElement.hidden = !enabled;
            heatmapWindowControlElement.querySelectorAll("[data-heatmap-window]")
                .forEach(function (button) {
                    var isSelected = button.dataset.heatmapWindow ===
                        layerSettings.heatmapWindow;
                    button.classList.toggle("is-selected", isSelected);
                    button.setAttribute("aria-pressed", String(isSelected));
                });
        }
        if (heatmapLegendElement) {
            heatmapLegendElement.hidden = !enabled;
        }
    }

    function setSectionLayers(section, isEnabled) {
        var trailsWereEnabled = layerSettings.trails;
        section.querySelectorAll("input[data-layer-key]").forEach(function (checkbox) {
            checkbox.checked = isEnabled;
            layerSettings[checkbox.dataset.layerKey] = isEnabled;
        });
        saveLayerSettings();
        syncLayerVisibility();
        scheduleHashUpdate();
        if (!trailsWereEnabled && layerSettings.trails) {
            backfillVisiblePlayerTrails();
        }
        updateEntityPolling(true);
        updateLayerCounts();
    }

    function appendPoiCategory(parent, category) {
        var section = document.createElement("section");
        var header = document.createElement("header");
        var toggle = document.createElement("button");
        var chevron = document.createElement("span");
        var name = document.createElement("span");
        var actions = document.createElement("span");
        var count = document.createElement("span");
        var allButton = document.createElement("button");
        var noneButton = document.createElement("button");
        var content = document.createElement("div");
        var body = document.createElement("div");
        var isCollapsed = layerSettings.poiCollapsed[category.key] === true;

        section.className = "poi-category" + (isCollapsed ? " is-collapsed" : "");
        section.dataset.poiCategory = category.key;
        header.className = "poi-category-header";
        toggle.type = "button";
        toggle.className = "poi-category-toggle";
        toggle.setAttribute("aria-controls", "poi-category-content-" + category.key);
        toggle.setAttribute("aria-expanded", String(!isCollapsed));
        chevron.className = "poi-category-chevron";
        chevron.textContent = "›";
        chevron.setAttribute("aria-hidden", "true");
        name.className = "poi-category-name";
        name.textContent = category.label;
        actions.className = "poi-category-actions";
        count.className = "poi-category-count";
        count.dataset.poiCategoryCount = category.key;
        allButton.type = "button";
        allButton.className = "poi-category-mini";
        allButton.textContent = "all";
        allButton.setAttribute("aria-label", "Show all " + category.label.toLowerCase());
        addAppListener(allButton, "click", function (event) {
            event.stopPropagation();
            setSectionLayers(section, true);
        });
        noneButton.type = "button";
        noneButton.className = "poi-category-mini";
        noneButton.textContent = "none";
        noneButton.setAttribute("aria-label", "Hide all " + category.label.toLowerCase());
        addAppListener(noneButton, "click", function (event) {
            event.stopPropagation();
            setSectionLayers(section, false);
        });
        content.id = "poi-category-content-" + category.key;
        content.className = "poi-category-content";
        content.hidden = isCollapsed;
        body.className = "poi-category-body";

        toggle.appendChild(chevron);
        toggle.appendChild(name);
        actions.appendChild(count);
        actions.appendChild(allButton);
        actions.appendChild(noneButton);
        header.appendChild(toggle);
        header.appendChild(actions);
        section.appendChild(header);
        appendPoiColorControls(content, category);
        content.appendChild(body);
        section.appendChild(content);
        parent.appendChild(section);
        addAppListener(toggle, "click", function () {
            var nextCollapsed = !section.classList.contains("is-collapsed");
            section.classList.toggle("is-collapsed", nextCollapsed);
            content.hidden = nextCollapsed;
            toggle.setAttribute("aria-expanded", String(!nextCollapsed));
            layerSettings.poiCollapsed[category.key] = nextCollapsed;
            saveLayerSettings();
        });
        return body;
    }

    function appendPoiColorControls(parent, category) {
        var row = document.createElement("div");
        var label = document.createElement("span");
        var swatches = document.createElement("span");
        var choices = [{
            key: "",
            label: "Default",
            value: POI_CATEGORY_DEFAULT_SWATCHES[category.key]
        }].concat(POI_COLOR_PALETTE);
        var activeKey = layerSettings.poiColors[category.key] || "";

        row.className = "poi-category-colors";
        label.className = "poi-category-color-label";
        label.textContent = "Color";
        swatches.className = "poi-category-swatches";
        swatches.setAttribute("role", "group");
        swatches.setAttribute("aria-label", category.label + " marker color");
        choices.forEach(function (choice) {
            var button = document.createElement("button");
            var isSelected = choice.key === activeKey;
            button.type = "button";
            button.className = "poi-color-swatch" +
                (choice.key ? "" : " is-default") +
                (isSelected ? " is-selected" : "");
            button.dataset.poiColorCategory = category.key;
            button.dataset.poiColorKey = choice.key;
            button.style.setProperty("--poi-swatch-color", choice.value);
            button.title = choice.label;
            button.setAttribute(
                "aria-label",
                (choice.key ? "Use " + choice.label : "Use default colors") +
                    " for " + category.label
            );
            button.setAttribute("aria-pressed", String(isSelected));
            addAppListener(button, "click", function () {
                if (choice.key) {
                    layerSettings.poiColors[category.key] = choice.key;
                } else {
                    delete layerSettings.poiColors[category.key];
                }
                saveLayerSettings();
                applyPoiPreferences();
            });
            swatches.appendChild(button);
        });
        row.appendChild(label);
        row.appendChild(swatches);
        parent.appendChild(row);
    }

    function syncPoiColorControls() {
        if (!layersRows) {
            return;
        }
        layersRows.querySelectorAll("[data-poi-color-category]").forEach(function (button) {
            var activeKey = layerSettings.poiColors[button.dataset.poiColorCategory] || "";
            var isSelected = button.dataset.poiColorKey === activeKey;
            button.classList.toggle("is-selected", isSelected);
            button.setAttribute("aria-pressed", String(isSelected));
        });
    }

    function appendLayerRow(parent, key, labelText, glyph, swatchClass, options) {
        options = options || {};
        var label = document.createElement("label");
        var checkbox = document.createElement("input");
        var swatch = document.createElement("span");
        var text = document.createElement("span");
        var count = document.createElement("span");

        label.className = "layer-row";
        if (options.rowClass) {
            label.className += " " + options.rowClass;
        }
        checkbox.type = "checkbox";
        checkbox.checked = layerSettings[key];
        checkbox.dataset.layerKey = key;
        checkbox.setAttribute("aria-label", "Show " + labelText);
        addAppListener(checkbox, "change", function () {
            layerSettings[key] = checkbox.checked;
            saveLayerSettings();
            syncLayerVisibility();
            scheduleHashUpdate();
            if (key === "trails" && checkbox.checked) {
                backfillVisiblePlayerTrails();
            }
            if (Object.prototype.hasOwnProperty.call(ENTITY_GROUPS, key) ||
                key === "portalNetwork") {
                updateEntityPolling(true);
            }
            updateLayerCounts();
        });

        swatch.className = "layer-swatch layer-swatch-" + swatchClass;
        swatch.innerHTML = iconMarkup(layerIconKey(key), glyph);
        swatch.setAttribute("aria-hidden", "true");
        text.className = "layer-label";
        text.textContent = labelText;
        count.className = "layer-count";
        if (options.counted !== false) {
            count.dataset.layerCount = key;
        }

        label.appendChild(checkbox);
        label.appendChild(swatch);
        label.appendChild(text);
        label.appendChild(count);
        parent.appendChild(label);
    }

    function appendDisplayLayerRow(parent, labelText, glyph, swatchClass, countText) {
        var row = document.createElement("div");
        var spacer = document.createElement("span");
        var swatch = document.createElement("span");
        var text = document.createElement("span");
        var count = document.createElement("span");
        row.className = "layer-row is-display-only";
        spacer.setAttribute("aria-hidden", "true");
        swatch.className = "layer-swatch layer-swatch-" + swatchClass;
        swatch.textContent = glyph;
        swatch.setAttribute("aria-hidden", "true");
        text.className = "layer-label";
        text.textContent = labelText;
        count.className = "layer-count";
        count.textContent = countText;
        row.appendChild(spacer);
        row.appendChild(swatch);
        row.appendChild(text);
        row.appendChild(count);
        parent.appendChild(row);
    }

    function appendLayerStatus(parent, message) {
        var status = document.createElement("div");
        status.className = "layer-section-status";
        status.textContent = message;
        parent.appendChild(status);
    }

    function appendPoiTruncationNote(parent, group) {
        var note = document.createElement("div");
        note.className = "layer-section-status";
        note.dataset.poiTruncationNote = group;
        note.hidden = true;
        parent.appendChild(note);
    }

    function layerCountValue(key) {
        if (key === "players") {
            return latestPlayers.length;
        }
        if (key === "pins") {
            return latestPins.length;
        }
        if (key === "webpins") {
            return latestWebPins.length;
        }
        if (key === "trails") {
            return latestPlayers.length;
        }
        if (key === "portalNetwork") {
            return portalPairs.length;
        }
        if (Object.prototype.hasOwnProperty.call(POI_GROUPS, key)) {
            var metadata = poiGroupMeta.get(key);
            var count = metadata ? metadata.count : (poiRecords.get(key) || []).length;
            return formatTruncatedGroupCount(metadata, count);
        }
        if (Object.prototype.hasOwnProperty.call(ENTITY_GROUPS, key)) {
            var groupMetadata = entityGroupMeta.get(key);
            var count = groupMetadata
                ? groupMetadata.count
                : latestEntities.filter(function (entity) {
                    return entity.group === key;
                }).length;
            return formatTruncatedGroupCount(groupMetadata, count);
        }
        return "";
    }

    function resourceSurveyingText(state) {
        var etaSeconds = state ? Number(state.scanEtaSeconds) : NaN;
        if (!Number.isFinite(etaSeconds) || etaSeconds < 0) {
            return "Surveying…";
        }

        return "Surveying… ~" + Math.max(1, Math.ceil(etaSeconds / 60)) + "m";
    }

    function formatTruncatedGroupCount(metadata, count) {
        if (!metadata || !metadata.truncated) {
            return String(count);
        }

        return String(metadata.cap) + "+";
    }

    function updateLayerCounts() {
        if (!layersRows) {
            return;
        }
        layersRows.querySelectorAll("[data-layer-count]").forEach(function (badge) {
            var key = badge.dataset.layerCount;
            var lazyState = lazyPoiStates.get(key);
            var metadata = poiGroupMeta.get(key);
            var resultCount = metadata
                ? metadata.count
                : (poiRecords.get(key) || []).length;
            var surveying = isResourcePoiGroup(key) && layerSettings[key] &&
                Number(resultCount) === 0 && lazyState &&
                (lazyState.requestPending || lazyState.scanning);
            var loading = !surveying && layerSettings[key] && lazyState &&
                lazyState.requestPending &&
                (!isResourcePoiGroup(key) || Number(resultCount) === 0);
            badge.textContent = surveying
                ? resourceSurveyingText(lazyState)
                : loading ? "Loading…" : String(layerCountValue(key));
            badge.classList.toggle("is-surveying", Boolean(surveying || loading));
        });
        layersRows.querySelectorAll("[data-poi-truncation-note]").forEach(function (note) {
            var metadata = poiGroupMeta.get(note.dataset.poiTruncationNote);
            var truncated = metadata && metadata.truncated === true;
            var piecesTruncated = metadata && metadata.piecesTruncated === true;
            note.hidden = !truncated && !piecesTruncated;
            if (truncated) {
                note.textContent = "Showing first " + formatInteger(metadata.cap) +
                    " — world has more";
            } else if (piecesTruncated) {
                note.textContent = "Survey capped at " + formatInteger(metadata.pieceCap) +
                    " structures — results may be incomplete";
            } else {
                note.textContent = "";
            }
        });
        layersRows.querySelectorAll(".layer-section").forEach(function (section) {
            var inputs = Array.prototype.slice.call(
                section.querySelectorAll("input[data-layer-key]")
            );
            var enabled = inputs.filter(function (input) {
                return input.checked;
            }).length;
            var count = section.querySelector("[data-section-count]");
            if (count) {
                count.textContent = enabled + "/" + inputs.length;
            }
        });
        layersRows.querySelectorAll(".poi-category").forEach(function (section) {
            var inputs = Array.prototype.slice.call(
                section.querySelectorAll("input[data-layer-key]")
            );
            var enabled = inputs.filter(function (input) {
                return input.checked;
            }).length;
            var count = section.querySelector("[data-poi-category-count]");
            if (count) {
                count.textContent = enabled + "/" + inputs.length;
            }
        });
    }

    function feedAgeText(updatedAt) {
        if (!updatedAt) {
            return "not loaded";
        }
        var seconds = Math.max(0, Math.floor((Date.now() - updatedAt) / 1000));
        return "updated " + seconds + "s ago";
    }

    function feedStaleness(feed) {
        if (feed === "fog") {
            if (!fogAvailable || !layerSettings.fog) {
                return { state: "grey", title: "fog off" };
            }
            if (fogOverlay && fogDisplayedRevision !== null && map && map.hasLayer(fogOverlay)) {
                return { state: "green", title: feedAgeText(feedLastUpdated.fog) };
            }
            return { state: "amber", title: "fog loading" };
        }
        if (feed === "heatmap") {
            if (!heatmapIsEnabled()) {
                return { state: "grey", title: "heatmap off" };
            }
            if (!feedLastUpdated.heatmap) {
                return { state: "amber", title: "heatmap loading" };
            }
        }

        var updatedAt = feedLastUpdated[feed] || 0;
        if (!updatedAt) {
            return { state: "grey", title: "not loaded" };
        }
        if (feed === "pois") {
            return { state: "green", title: feedAgeText(updatedAt) };
        }
        if (feed === "players" || feed === "status") {
            if (failedFeeds.has(feed)) {
                return { state: "red", title: feedAgeText(updatedAt) };
            }
            if (latestStatusSnapshotStale === null) {
                return { state: "grey", title: "not loaded" };
            }
            return {
                state: latestStatusSnapshotStale ? "red" : "green",
                title: feedAgeText(updatedAt)
            };
        }
        var expected = {
            entities: ENTITIES_POLL_INTERVAL_MS,
            pins: PINS_POLL_INTERVAL_MS,
            webpins: PINS_POLL_INTERVAL_MS,
            heatmap: HEATMAP_POLL_INTERVAL_MS
        }[feed];
        var age = Date.now() - updatedAt;
        return {
            state: age < expected * 2 ? "green" : age < expected * 5 ? "amber" : "red",
            title: feedAgeText(updatedAt)
        };
    }

    function updateFeedStalenessDots() {
        updateCinemaStalenessBadge();
        if (!layersRows) {
            return;
        }
        layersRows.querySelectorAll(".feed-staleness-dot").forEach(function (dot) {
            var result = feedStaleness(dot.dataset.feed);
            dot.className = "feed-staleness-dot is-" + result.state;
            dot.title = result.title;
            dot.setAttribute("aria-label", dot.dataset.feed + " " + result.title);
        });
    }

    function appendLayerPreferences() {
        var container = document.createElement("section");
        var dotsLabel = document.createElement("label");
        var dotsText = document.createElement("span");
        var dotsToggle = document.createElement("input");
        var sizeLabel = document.createElement("label");
        var sizeText = document.createElement("span");
        var sizeSelect = document.createElement("select");
        var opacityLabel = document.createElement("label");
        var opacityText = document.createElement("span");
        var opacityControl = document.createElement("span");
        var opacityInput = document.createElement("input");
        var opacityOutput = document.createElement("output");

        container.className = "layer-preferences";
        dotsLabel.className = "layer-preference-row";
        dotsText.textContent = "Dots mode";
        dotsToggle.type = "checkbox";
        dotsToggle.checked = layerSettings.densityDots;
        addAppListener(dotsToggle, "change", function () {
            layerSettings.densityDots = dotsToggle.checked;
            saveLayerSettings();
            applyDensityPreferences();
        });
        dotsLabel.appendChild(dotsText);
        dotsLabel.appendChild(dotsToggle);

        sizeLabel.className = "layer-preference-row";
        sizeText.textContent = "Icon size";
        sizeSelect.setAttribute("aria-label", "Map icon size");
        [["s", "S"], ["m", "M"], ["l", "L"]].forEach(function (choice) {
            var option = document.createElement("option");
            option.value = choice[0];
            option.textContent = choice[1];
            sizeSelect.appendChild(option);
        });
        sizeSelect.value = layerSettings.iconSize;
        addAppListener(sizeSelect, "change", function () {
            layerSettings.iconSize = sizeSelect.value;
            saveLayerSettings();
            applyDensityPreferences();
        });
        sizeLabel.appendChild(sizeText);
        sizeLabel.appendChild(sizeSelect);

        opacityLabel.className = "layer-preference-row";
        opacityText.textContent = "Marker opacity";
        opacityControl.className = "layer-opacity-control";
        opacityInput.type = "range";
        opacityInput.min = "20";
        opacityInput.max = "100";
        opacityInput.step = "5";
        opacityInput.value = String(layerSettings.poiOpacity);
        opacityInput.setAttribute("aria-label", "POI marker opacity");
        opacityOutput.textContent = layerSettings.poiOpacity + "%";
        addAppListener(opacityInput, "input", function () {
            layerSettings.poiOpacity = sanitizePoiOpacity(opacityInput.value);
            opacityOutput.textContent = layerSettings.poiOpacity + "%";
            saveLayerSettings();
            applyPoiPreferences();
        });
        opacityControl.appendChild(opacityInput);
        opacityControl.appendChild(opacityOutput);
        opacityLabel.appendChild(opacityText);
        opacityLabel.appendChild(opacityControl);
        container.appendChild(dotsLabel);
        container.appendChild(sizeLabel);
        container.appendChild(opacityLabel);
        layersRows.appendChild(container);
    }

    function applyDensityPreferences() {
        if (!elements.mapPane) {
            return;
        }
        var scales = { s: "0.85", m: "1", l: "1.2" };
        elements.mapPane.classList.toggle("is-density-dots", layerSettings.densityDots);
        elements.mapPane.style.setProperty(
            "--marker-scale",
            scales[layerSettings.iconSize] || scales.m
        );
    }

    function applyPoiPreferences() {
        if (!elements.mapPane) {
            return;
        }
        POI_CATEGORIES.forEach(function (category) {
            var colorKey = layerSettings.poiColors[category.key];
            var choice = poiPaletteChoice(colorKey);
            var property = "--poi-cat-" + category.key;
            if (choice) {
                elements.mapPane.style.setProperty(property, choice.value);
            } else {
                elements.mapPane.style.removeProperty(property);
            }
        });
        layerSettings.poiOpacity = sanitizePoiOpacity(layerSettings.poiOpacity);
        elements.mapPane.style.setProperty(
            "--poi-opacity",
            String(layerSettings.poiOpacity / 100)
        );
        syncPoiColorControls();
    }

    function findMarkerNearLayer(layer, latLng) {
        var match = null;
        if (!layer || !latLng) {
            return null;
        }
        layer.eachLayer(function (candidate) {
            if (match || typeof candidate.getLatLng !== "function") {
                return;
            }
            var candidateLatLng = candidate.getLatLng();
            if (Math.abs(candidateLatLng.lat - latLng.lat) < 0.000001 &&
                Math.abs(candidateLatLng.lng - latLng.lng) < 0.000001) {
                match = candidate;
            }
        });
        return match;
    }

    function focusMapLocation(latLng, markerResolver, minimumZoom) {
        if (!map || !latLng) {
            return;
        }
        clearFollow();
        var requestedZoom = Number(minimumZoom);
        if (!Number.isFinite(requestedZoom)) {
            requestedZoom = 0;
        }
        var targetZoom = Math.min(
            map.getMaxZoom(),
            Math.max(map.getZoom(), 4, requestedZoom)
        );
        var popupOpened = false;
        function openPopup() {
            if (popupOpened || typeof markerResolver !== "function") {
                return;
            }
            var marker = markerResolver();
            if (marker && marker._map && typeof marker.openPopup === "function") {
                popupOpened = true;
                marker.openPopup();
            }
        }
        map.once("moveend", function () {
            window.setTimeout(openPopup, 0);
        });
        map.flyTo(latLng, targetZoom, { duration: 0.45 });
        window.setTimeout(openPopup, 700);
    }

    function jumpToPoiRecord(record) {
        var shouldOpen = layerSettings[record.group] === true;
        focusMapLocation(record.latLng, shouldOpen ? function () {
            return findMarkerNearLayer(poiLayers.get(record.group), record.latLng);
        } : null, poiGroupMinimumZoom(record.group));
    }

    function jumpToTombstone(id) {
        var tombstone = latestEntities.find(function (entity) {
            return entity.group === "tombstone" && entity.id === id;
        });
        if (!tombstone) {
            return;
        }

        var latLng = worldToLatLng(tombstone.x, tombstone.z);
        focusMapLocation(latLng, layerSettings.tombstone ? function () {
            return findMarkerNearLayer(entityLayers.get("tombstone"), latLng);
        } : null);
    }

    function jumpToPortal(id) {
        var portal = portalEntityById(id);
        if (!portal) {
            return;
        }

        focusMapLocation(worldToLatLng(portal.x, portal.z), function () {
            var record = portalMarkerRecords.get(id);
            return record ? record.marker : null;
        });
    }

    function groupBossJumpRecords(records) {
        var groupsByKey = new Map();
        var groups = [];

        records.forEach(function (record, recordIndex) {
            var iconKey = poiIconKey(record);
            var progressionBoss = null;
            var progressionIndex = BOSS_PROGRESSION.length;
            BOSS_PROGRESSION.some(function (boss, bossIndex) {
                if (boss.iconKey !== iconKey) {
                    return false;
                }
                progressionBoss = boss;
                progressionIndex = bossIndex;
                return true;
            });

            var rawIdentity = typeof record.name === "string"
                ? record.name.replace(/[^a-z0-9]/gi, "").toLowerCase()
                : "";
            var displayName = progressionBoss
                ? progressionBoss.name
                : (record.title || "Boss altar");
            var identityKey = progressionBoss
                ? progressionBoss.iconKey
                : "boss-unknown-" + (rawIdentity || displayName.toLowerCase());
            var group = groupsByKey.get(identityKey);
            if (!group) {
                group = {
                    displayName: displayName,
                    firstIndex: recordIndex,
                    iconKey: progressionBoss ? progressionBoss.iconKey : iconKey,
                    identityKey: identityKey,
                    instances: [],
                    progressionIndex: progressionIndex
                };
                groupsByKey.set(identityKey, group);
                groups.push(group);
            }
            group.instances.push(record);
        });

        groups.sort(function (left, right) {
            if (left.progressionIndex !== right.progressionIndex) {
                return left.progressionIndex - right.progressionIndex;
            }
            return left.firstIndex - right.firstIndex;
        });
        return groups;
    }

    function nearestBossInstanceIndex(instances) {
        var center = map ? latLngToWorld(map.getCenter()) : null;
        if (!center || instances.length < 2) {
            return 0;
        }

        var nearestIndex = 0;
        var nearestDistance = Infinity;
        instances.forEach(function (record, index) {
            var distance = worldDistance(center.x, center.z, record.x, record.z);
            if (distance < nearestDistance) {
                nearestDistance = distance;
                nearestIndex = index;
            }
        });
        return nearestIndex;
    }

    function nextBossJumpRecord(group) {
        var lastIndex = bossJumpServedIndices.get(group.identityKey);
        var nextIndex = Number.isFinite(lastIndex) &&
            lastIndex >= 0 && lastIndex < group.instances.length
            ? (lastIndex + 1) % group.instances.length
            : nearestBossInstanceIndex(group.instances);
        bossJumpServedIndices.set(group.identityKey, nextIndex);
        return group.instances[nextIndex];
    }

    function renderJumpChips() {
        var spawn = (poiRecords.get("spawn") || [])[0];
        var trader = (poiRecords.get("trader") || [])[0];
        var bosses = poiRecords.get("boss") || [];
        if (!spawn && !trader && bosses.length === 0) {
            return;
        }

        var strip = document.createElement("nav");
        strip.className = "layer-jump-chips";
        strip.setAttribute("aria-label", "Jump to map location");

        function appendDirectChip(labelText, record) {
            if (!record) {
                return;
            }
            var button = document.createElement("button");
            button.type = "button";
            button.className = "layer-jump-chip";
            button.textContent = labelText;
            addAppListener(button, "click", function () {
                jumpToPoiRecord(record);
            });
            strip.appendChild(button);
        }

        appendDirectChip("Spawn", spawn);
        appendDirectChip("Trader", trader);
        if (bosses.length > 0) {
            var bossGroups = groupBossJumpRecords(bosses);
            var dropdown = document.createElement("div");
            var toggle = document.createElement("button");
            var menu = document.createElement("ul");
            var menuButtons = [];
            dropdown.className = "layer-jump-dropdown";
            toggle.type = "button";
            toggle.className = "layer-jump-chip";
            toggle.textContent = "Bosses ▾";
            toggle.setAttribute("aria-haspopup", "menu");
            toggle.setAttribute("aria-expanded", "false");
            menu.className = "layer-jump-menu";
            menu.setAttribute("role", "menu");
            menu.hidden = true;

            function setOpen(isOpen, focusFirst) {
                menu.hidden = !isOpen;
                toggle.setAttribute("aria-expanded", String(isOpen));
                dropdown.classList.toggle("is-open", isOpen);
                if (isOpen && focusFirst && menuButtons.length > 0) {
                    menuButtons[0].focus();
                }
            }

            bossGroups.forEach(function (bossGroup) {
                var item = document.createElement("li");
                var button = document.createElement("button");
                var icon = document.createElement("span");
                var label = document.createElement("span");
                item.setAttribute("role", "none");
                button.type = "button";
                button.setAttribute("role", "menuitem");
                icon.className = "layer-jump-menu-icon";
                icon.setAttribute("aria-hidden", "true");
                icon.innerHTML = iconMarkup(
                    bossGroup.iconKey,
                    POI_GROUPS.boss.glyph
                );
                label.className = "layer-jump-menu-label";
                label.textContent = bossGroup.displayName +
                    (bossGroup.instances.length > 1
                        ? " · " + bossGroup.instances.length + " altars"
                        : "");
                button.appendChild(icon);
                button.appendChild(label);
                if (bossGroup.instances.length > 1) {
                    button.title = "click again for next altar";
                }
                addAppListener(button, "click", function () {
                    setOpen(false, false);
                    toggle.focus();
                    jumpToPoiRecord(nextBossJumpRecord(bossGroup));
                });
                item.appendChild(button);
                menu.appendChild(item);
                menuButtons.push(button);
            });
            addAppListener(toggle, "click", function () {
                setOpen(menu.hidden, false);
            });
            addAppListener(toggle, "keydown", function (event) {
                if (event.key === "ArrowDown") {
                    event.preventDefault();
                    setOpen(true, true);
                } else if (event.key === "Escape") {
                    setOpen(false, false);
                }
            });
            addAppListener(menu, "keydown", function (event) {
                var index = menuButtons.indexOf(document.activeElement);
                if (event.key === "Escape") {
                    event.preventDefault();
                    setOpen(false, false);
                    toggle.focus();
                } else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
                    event.preventDefault();
                    var direction = event.key === "ArrowDown" ? 1 : -1;
                    var next = (index + direction + menuButtons.length) % menuButtons.length;
                    menuButtons[next].focus();
                }
            });
            addAppListener(dropdown, "focusout", function () {
                window.setTimeout(function () {
                    if (!dropdown.contains(document.activeElement)) {
                        setOpen(false, false);
                    }
                }, 0);
            });
            dropdown.appendChild(toggle);
            dropdown.appendChild(menu);
            strip.appendChild(dropdown);
        }

        layersRows.appendChild(strip);
    }

    function appendLegendBlock() {
        var container = document.createElement("section");
        var toggle = document.createElement("button");
        var title = document.createElement("span");
        var chevron = document.createElement("span");

        container.className = "legend-block";
        toggle.type = "button";
        toggle.className = "legend-toggle";
        title.className = "legend-title";
        title.textContent = "Legend";
        chevron.className = "legend-chevron";
        chevron.textContent = "›";
        chevron.setAttribute("aria-hidden", "true");
        toggle.appendChild(title);
        toggle.appendChild(chevron);
        legendContent = document.createElement("div");
        legendContent.className = "legend-items";
        container.appendChild(toggle);
        container.appendChild(legendContent);
        layersRows.appendChild(container);

        function applyCollapsedState() {
            var isCollapsed = layerSettings.legendCollapsed;
            container.classList.toggle("is-collapsed", isCollapsed);
            legendContent.hidden = isCollapsed;
            toggle.setAttribute("aria-expanded", String(!isCollapsed));
        }

        addAppListener(toggle, "click", function () {
            layerSettings.legendCollapsed = !layerSettings.legendCollapsed;
            saveLayerSettings();
            applyCollapsedState();
        });
        applyCollapsedState();
        renderLegend();
    }

    function appendLegendItem(glyph, labelText, swatchClass) {
        var item = document.createElement("div");
        var swatch = document.createElement("span");
        var label = document.createElement("span");
        item.className = "legend-item";
        swatch.className = "legend-swatch layer-swatch layer-swatch-" + swatchClass;
        swatch.innerHTML = iconMarkup(layerIconKey(swatchClass), glyph);
        swatch.setAttribute("aria-hidden", "true");
        label.textContent = labelText;
        item.appendChild(swatch);
        item.appendChild(label);
        legendContent.appendChild(item);
    }

    function renderLegend() {
        if (!legendContent) {
            return;
        }

        legendContent.textContent = "";
        if (layerSettings.players) {
            appendLegendItem("●", "Players", "players");
        }
        if (layerSettings.pins) {
            appendLegendItem("⌖", "Pins", "pins");
        }
        if (webPinsAvailable && layerSettings.webpins) {
            appendLegendItem("✦", "Web pins", "webpins");
        }
        if (layerSettings.trails) {
            appendLegendItem("〰", "Trails", "trails");
        }
        if (layerSettings.portalNetwork && entityLayersAreAvailable()) {
            appendLegendItem("╌", "Portal network", "portal-network");
        }
        POI_GROUP_ORDER.forEach(function (group) {
            if (availablePoiGroups.has(group) && layerSettings[group] &&
                !isPoiGroupZoomGated(group)) {
                appendLegendItem(
                    POI_GROUPS[group].glyph,
                    POI_GROUPS[group].label + " · " + layerCountValue(group),
                    group
                );
            }
        });
        if (fogAvailable && layerSettings.fog) {
            appendLegendItem("≈", "Fog", "fog");
        }
        if (entityLayersAreAvailable()) {
            ENTITY_GROUP_ORDER.forEach(function (group) {
                if (layerSettings[group]) {
                    appendLegendItem(
                        ENTITY_GROUPS[group].glyph,
                        ENTITY_GROUPS[group].label,
                        group
                    );
                }
            });
        }
        if (currentRaidEvent) {
            appendLegendItem("◯", "Raid area", "raid");
        }
    }

    function setLayerVisible(layer, visible) {
        if (!map || !layer) {
            return;
        }

        if (visible && !map.hasLayer(layer)) {
            layer.addTo(map);
        } else if (!visible && map.hasLayer(layer)) {
            layer.removeFrom(map);
        }
    }

    function poiCategoryDefinition(categoryKey) {
        for (var index = 0; index < POI_CATEGORIES.length; index++) {
            if (POI_CATEGORIES[index].key === categoryKey) {
                return POI_CATEGORIES[index];
            }
        }
        return null;
    }

    function poiGroupMinimumZoom(group) {
        var definition = POI_GROUPS[group];
        var category = definition
            ? poiCategoryDefinition(definition.category)
            : null;
        return category && Number.isFinite(category.minimumZoom)
            ? category.minimumZoom
            : 0;
    }

    function poiGroupHasZoomGate(group) {
        return poiGroupMinimumZoom(group) > 0;
    }

    function isPoiGroupZoomGated(group) {
        var definition = POI_GROUPS[group];
        return Boolean(definition &&
            zoomGatedPoiCategories.has(definition.category));
    }

    function updatePoiZoomGateRows() {
        if (!layersRows) {
            return;
        }

        layersRows.querySelectorAll("[data-poi-category]").forEach(function (section) {
            var gated = zoomGatedPoiCategories.has(section.dataset.poiCategory);
            var header = section.querySelector(".poi-category-header");
            section.classList.toggle("is-zoom-gated", gated);
            if (header) {
                if (gated) {
                    header.title = "Zoom in to show";
                } else {
                    header.removeAttribute("title");
                }
            }
            section.querySelectorAll(".layer-row").forEach(function (row) {
                row.classList.toggle("is-zoom-gated", gated);
                if (gated) {
                    row.title = "Zoom in to show";
                } else {
                    row.removeAttribute("title");
                }
            });
        });
    }

    function applyPoiZoomGates() {
        if (!map) {
            return;
        }

        var zoom = map.getZoom();
        var previouslyGated = new Set(zoomGatedPoiCategories);
        zoomGatedPoiCategories.clear();
        POI_CATEGORIES.forEach(function (category) {
            if (Number.isFinite(category.minimumZoom) &&
                zoom < category.minimumZoom) {
                zoomGatedPoiCategories.add(category.key);
            }
        });
        POI_GROUP_ORDER.forEach(function (group) {
            var definition = POI_GROUPS[group];
            if (definition && previouslyGated.has(definition.category) &&
                !zoomGatedPoiCategories.has(definition.category)) {
                renderPoiGroup(group, false);
            }
        });
        updatePoiZoomGateRows();
        syncLayerVisibility();
    }

    function syncLayerVisibility() {
        if (!map) {
            return;
        }

        updateTimelapseRestoreVisibility();
        var historicalLayersVisible = timelapseIsActive();
        setLayerVisible(playerLayer, layerSettings.players);
        setLayerVisible(pinLayer, layerSettings.pins);
        setLayerVisible(webPinLayer, webPinsAvailable && layerSettings.webpins);
        markerRecords.forEach(function (record) {
            updatePlayerMarkerMotion(record);
        });
        POI_GROUP_ORDER.forEach(function (group) {
            setLayerVisible(
                poiLayers.get(group),
                availablePoiGroups.has(group) && layerSettings[group] &&
                    !isPoiGroupZoomGated(group) &&
                    !(historicalLayersVisible && group === "bases")
            );
        });
        setLayerVisible(
            fogOverlay,
            !historicalLayersVisible && fogAvailable && layerSettings.fog
        );
        var heatmapVisible = heatmapIsEnabled();
        setLayerVisible(heatmapLayer, heatmapVisible);
        syncHeatmapControls();
        if (heatmapVisible) {
            startHeatmapPolling();
        } else {
            stopHeatmapPolling();
        }
        updateRegionLayerVisibility();
        setLayerVisible(tintOverlay, layerSettings.tint);
        setLayerVisible(
            portalNetworkLayer,
            !historicalLayersVisible && entityLayersAreAvailable() &&
                layerSettings.portalNetwork
        );
        setLayerVisible(
            wardRadiusLayer,
            !historicalLayersVisible && entityLayersAreAvailable() &&
                layerSettings.ward
        );
        var shipHeadingsVisible = entityLayersAreAvailable() && layerSettings.ship;
        setLayerVisible(shipHeadingLayer, shipHeadingsVisible);
        if (shipHeadingsVisible) {
            updateShipHeadingLines(latestEntities);
        } else {
            clearShipHeadingLines();
        }
        ENTITY_GROUP_ORDER.forEach(function (group) {
            var hiddenByTimelapse = historicalLayersVisible &&
                (group === "portal" || group === "bed" || group === "ward");
            setLayerVisible(
                entityLayers.get(group),
                !hiddenByTimelapse && entityLayersAreAvailable() &&
                    layerSettings[group]
            );
        });
        if (minimapSetOpen) {
            minimapSetOpen(layerSettings.minimap, false);
        }
        renderPortalLinks();
        renderTrails();
        renderLegend();
        updateFeedStalenessDots();
        updateLazyPoiLoading();
        if (timelapseHasAccess() && layerSettings.timelapse === true &&
            timelapseAvailability === "available") {
            activateTimelapse();
        } else {
            deactivateTimelapse();
        }
    }

    function normalizeDungeonReference(value) {
        if (!value || typeof value !== "object") {
            return null;
        }

        var id = typeof value.id === "string" ? value.id.trim() : "";
        var label = typeof value.label === "string" ? value.label.trim() : "";
        return id && label ? { id: id, label: label } : null;
    }

    function normalizePlayers(payload) {
        if (!payload || !Array.isArray(payload.players)) {
            return [];
        }

        var nameOccurrences = Object.create(null);
        var anonymousIndex = 0;
        return payload.players.filter(function (player) {
            return player && typeof player.name === "string" &&
                Number.isFinite(Number(player.x)) && Number.isFinite(Number(player.z));
        }).map(function (player) {
            var rawName = player.name.trim();
            var playerId = typeof player.id === "string" ? player.id.trim() : "";
            var key;
            if (playerId) {
                key = "id:" + playerId;
            } else if (!rawName) {
                key = "anonymous:" + anonymousIndex;
                anonymousIndex++;
            } else {
                nameOccurrences[rawName] = (nameOccurrences[rawName] || 0) + 1;
                key = "named:" + rawName + ":" + nameOccurrences[rawName];
            }

            return {
                anonymous: !rawName,
                biome: typeof player.biome === "string" ? player.biome.trim() : "",
                dead: typeof player.dead === "boolean" ? player.dead : null,
                displayName: rawName || "Explorer",
                distanceTodayM: finiteNumberOrNull(player.distanceTodayM),
                headingDeg: finiteNumberOrNull(player.headingDeg),
                health: finiteNumberOrNull(player.health),
                id: playerId,
                inBed: typeof player.inBed === "boolean" ? player.inBed : null,
                inDungeon: normalizeDungeonReference(player.inDungeon),
                key: key,
                maxHealth: finiteNumberOrNull(player.maxHealth),
                name: rawName,
                pvp: typeof player.pvp === "boolean" ? player.pvp : null,
                sessionStartUnixMs: finiteNumberOrNull(player.sessionStartUnixMs),
                speedMps: finiteNumberOrNull(player.speedMps),
                trailKey: playerId ? "player:" + playerId : key,
                x: Number(player.x),
                y: Number(player.y),
                z: Number(player.z)
            };
        });
    }

    function updatePlayerMarkerMotion(record) {
        var markerElement = record.marker.getElement();
        if (!markerElement) {
            return;
        }

        var chevron = markerElement.querySelector(".player-marker-chevron");
        if (!chevron) {
            return;
        }
        var motion = playerMotion(record.player);
        var showHeading = Boolean(motion && motion.speedMps >= 0.3 &&
            Number.isFinite(motion.headingDeg));
        chevron.hidden = !showHeading;
        if (showHeading) {
            chevron.style.transform = "rotate(" + motion.headingDeg.toFixed(1) + "deg)";
        }

        var dungeonBadge = markerElement.querySelector(".player-marker-dungeon");
        if (dungeonBadge) {
            dungeonBadge.hidden = !record.player.inDungeon;
            dungeonBadge.title = record.player.inDungeon
                ? "In: " + record.player.inDungeon.label
                : "";
        }
        markerElement.classList.toggle("is-in-dungeon", Boolean(record.player.inDungeon));
    }

    function createPlayerMarker(player) {
        var icon = L.divIcon({
            className: "player-div-icon",
            html: '<span class="player-marker-shell"><span class="player-marker-dot"></span>' +
                '<span class="player-marker-chevron" style="transform: rotate(0deg)" hidden></span>' +
                '<span class="player-marker-dungeon" hidden>∩</span></span>',
            iconAnchor: [12, 12],
            iconSize: [24, 24]
        });
        var marker = L.marker(worldToLatLng(player.x, player.z), {
            icon: icon,
            title: player.displayName
        }).addTo(playerLayer);
        var record = {
            animationKey: "player-marker:" + player.key,
            marker: marker,
            player: player
        };
        marker.bindTooltip(buildPlayerTooltip(player), {
            className: "player-tooltip",
            direction: "top",
            offset: [0, -7],
            opacity: 1,
            permanent: !player.anonymous,
            interactive: true
        });
        bindMapPopup(marker, function () {
            return buildPlayerPopup(record.player);
        }, {
            kind: "player",
            trailKey: player.trailKey,
            trailKind: "player"
        });
        marker.on("click", function () {
            if (!cinemaState) {
                return;
            }
            cinemaLockPlayer(record.player.key);
            window.setTimeout(function () {
                if (map && map._popup) {
                    map.closePopup();
                }
            }, 0);
        });
        updatePlayerMarkerMotion(record);
        return record;
    }

    function updatePlayerMarkers(players, tweenDuration) {
        if (!map || !mapMetrics || !playerLayer) {
            return;
        }

        var activeKeys = new Set();
        var followWasCleared = false;
        players.forEach(function (player) {
            activeKeys.add(player.key);
            var target = worldToLatLng(player.x, player.z);
            var record = markerRecords.get(player.key);
            if (!record) {
                record = createPlayerMarker(player);
                markerRecords.set(player.key, record);
            } else {
                record.player = player;
                tweenPlayerMarker(record, target, tweenDuration || playerTweenDurationMs);
            }
            record.marker.setTooltipContent(buildPlayerTooltip(player));
            updatePlayerMarkerMotion(record);
        });

        markerRecords.forEach(function (record, key) {
            if (activeKeys.has(key)) {
                return;
            }

            cancelMarkerTween(record.animationKey);
            playerLayer.removeLayer(record.marker);
            markerRecords.delete(key);
            if (isFollowing("player", key)) {
                if (cinemaState && cinemaState.locked &&
                    cinemaState.locked.trailKey === record.player.trailKey) {
                    cinemaBeginWaiting(record.player);
                }
                followTarget = null;
                followWasCleared = true;
            }
        });

        updateChatBubblePositions();

        updateFollowStyles();
        updateFollowPill();
        renderTrails();
        if (followWasCleared) {
            renderPlayerList(latestPlayers);
            scheduleHashUpdate();
        }
    }

    function applyInitialPlayersView() {
        if (firstPlayersViewApplied || !map || !mapMetrics || latestPlayers.length === 0) {
            return;
        }

        firstPlayersViewApplied = true;
        if (hashViewApplied) {
            return;
        }

        var positions = latestPlayers.map(function (player) {
            return worldToLatLng(player.x, player.z);
        });
        if (positions.length === 1) {
            map.setView(
                positions[0],
                Math.min(map.getMaxZoom(), mapMetrics.baseZoom + 1),
                { animate: false }
            );
            return;
        }

        map.fitBounds(L.latLngBounds(positions).pad(0.3), {
            animate: false,
            maxZoom: map.getMaxZoom()
        });
    }

    function movePlayerMarker(record, latLng) {
        updateChatBubblesForPlayer(record.player.key, latLng);
        if (isFollowing("player", record.player.key) &&
            (!cinemaState || !cinemaState.raidJumpActive)) {
            map.panTo(latLng, { animate: false });
        }
    }

    function tweenPlayerMarker(record, target, duration) {
        tweenMarker(record.animationKey, record.marker, target, duration, {
            onMove: function (latLng) {
                movePlayerMarker(record, latLng);
            },
            trailKey: record.player.trailKey,
            trailKind: "player"
        });
    }

    function followPlayer(key, options) {
        options = options || {};
        var record = markerRecords.get(key);
        if (!record || !map) {
            return;
        }

        if (cinemaState && !options.cinemaTransient) {
            cinemaSetLockedPlayer(record.player);
        }

        followTarget = {
            id: key,
            kind: "player",
            trailKey: record.player.trailKey
        };
        updateEntityFocusPolling(false);
        requestTrailBackfill("player", record.player.trailKey, 1800);
        updateFollowStyles();
        updateFollowPill();
        renderPlayerList(latestPlayers);
        renderTrails();
        refreshOpenPopupContent();
        scheduleHashUpdate();
        if (!cinemaState || !cinemaState.raidJumpActive) {
            if (cinemaState) {
                cinemaFlyToPlayer(record);
            } else {
                map.panTo(record.marker.getLatLng(), {
                    animate: true,
                    duration: 0.35
                });
            }
        }
        renderCinemaHud();

        if (window.matchMedia("(max-width: 759px)").matches) {
            elements.sidebarState.checked = false;
        }
    }

    function followEntity(kind, key) {
        var record = entityMarkerRecords.get(key);
        if (!record || !map || !hasLiveAccess() ||
            (kind !== "ship" && kind !== "cart")) {
            return;
        }

        followTarget = {
            id: key,
            kind: kind,
            trailKey: record.entity.trailKey
        };
        entityFocusTweenDurationMs = POLL_INTERVAL_MS;
        lastEntityFocusUnixMs = 0;
        requestTrailBackfill(kind, record.entity.trailKey, 1800);
        updateFollowStyles();
        updateFollowPill();
        renderPlayerList(latestPlayers);
        renderTrails();
        refreshOpenPopupContent();
        scheduleHashUpdate();
        updateEntityFocusPolling(true);
        map.panTo(record.marker.getLatLng(), {
            animate: true,
            duration: 0.35
        });

        if (window.matchMedia("(max-width: 759px)").matches) {
            elements.sidebarState.checked = false;
        }
    }

    function clearFollow(options) {
        options = options || {};
        if (cinemaState && !options.keepCinemaMode) {
            cinemaUnlockToAuto();
            return;
        }
        if (!followTarget) {
            return;
        }

        followTarget = null;
        updateEntityFocusPolling(false);
        updateFollowStyles();
        updateFollowPill();
        renderPlayerList(latestPlayers);
        renderTrails();
        refreshOpenPopupContent();
        scheduleHashUpdate();
    }

    function updateFollowStyles() {
        markerRecords.forEach(function (record, key) {
            var markerElement = record.marker.getElement();
            if (markerElement) {
                markerElement.classList.toggle("is-followed", isFollowing("player", key));
            }
        });
        entityMarkerRecords.forEach(function (record, key) {
            var markerElement = record.marker.getElement();
            if (markerElement) {
                markerElement.classList.toggle(
                    "is-followed",
                    isFollowing(record.entity.group, key)
                );
            }
        });
    }

    function cinemaHasAccess() {
        return currentView === "admin" || currentView === "shared";
    }

    function playerByFollowReference(reference) {
        var requested = typeof reference === "string" ? reference.trim() : "";
        var requestedName = requested.toLocaleLowerCase();
        if (!requested) {
            return null;
        }
        return latestPlayers.find(function (player) {
            return player.key === requested || player.trailKey === requested ||
                player.displayName.toLocaleLowerCase() === requestedName;
        }) || null;
    }

    function cinemaPlayerRecordByTrailKey(trailKey) {
        var player = latestPlayers.find(function (candidate) {
            return candidate.trailKey === trailKey;
        });
        return player ? markerRecords.get(player.key) || null : null;
    }

    function cinemaCurrentPlayerRecord() {
        if (!followTarget || followTarget.kind !== "player") {
            return null;
        }
        return markerRecords.get(followTarget.id) || null;
    }

    function cinemaTargetZoom() {
        if (!map || !mapMetrics) {
            return 0;
        }
        var tighterDefault = Math.min(map.getMaxZoom(), mapMetrics.baseZoom + 1.5);
        return Math.max(map.getMinZoom(), Math.max(map.getZoom(), tighterDefault));
    }

    function cinemaFlyToPlayer(record) {
        if (!cinemaState || !record || !map || document.hidden ||
            cinemaState.raidJumpActive) {
            return;
        }
        map.flyTo(record.marker.getLatLng(), cinemaTargetZoom(), {
            duration: 0.85,
            easeLinearity: 0.24
        });
    }

    function cinemaClearCycleTimer(state) {
        window.clearTimeout(state.cycleTimer);
        state.cycleTimer = 0;
    }

    function cinemaClearWaitingTimer(state) {
        window.clearTimeout(state.waitingTimer);
        state.waitingTimer = 0;
    }

    function cinemaStopAmbient(state, stopMap) {
        window.clearTimeout(state.ambientTimer);
        state.ambientTimer = 0;
        if (stopMap && map) {
            map.stop();
        }
    }

    function cinemaPauseAmbientForUser() {
        if (!cinemaState || cinemaState.raidJumpActive ||
            latestPlayers.length > 0 || document.hidden) {
            return;
        }
        var state = cinemaState;
        cinemaStopAmbient(state, true);
        state.ambientTimer = window.setTimeout(function () {
            if (cinemaState !== state) {
                return;
            }
            state.ambientTimer = 0;
            cinemaAmbientStep();
        }, CINEMA_AMBIENT_STEP_MS);
    }

    function cinemaStablePlayers() {
        return latestPlayers.slice().sort(function (left, right) {
            return left.trailKey.localeCompare(right.trailKey) ||
                left.displayName.localeCompare(right.displayName);
        });
    }

    function cinemaScheduleCycle(delay) {
        if (!cinemaState) {
            return;
        }
        cinemaClearCycleTimer(cinemaState);
        if (cinemaState.locked || cinemaState.raidJumpActive || document.hidden ||
            latestPlayers.length === 0) {
            return;
        }
        var state = cinemaState;
        state.cycleTimer = window.setTimeout(function () {
            if (cinemaState !== state) {
                return;
            }
            state.cycleTimer = 0;
            cinemaCyclePlayer(false);
        }, delay);
    }

    function cinemaCyclePlayer(initial) {
        if (!cinemaState || cinemaState.locked || cinemaState.raidJumpActive ||
            document.hidden) {
            return;
        }
        var players = cinemaStablePlayers();
        if (players.length === 0) {
            cinemaStartAmbient();
            renderCinemaHud();
            return;
        }

        cinemaStopAmbient(cinemaState, false);
        var currentRecord = cinemaCurrentPlayerRecord();
        var currentTrailKey = currentRecord
            ? currentRecord.player.trailKey
            : cinemaState.currentAutoTrailKey;
        var currentIndex = players.findIndex(function (player) {
            return player.trailKey === currentTrailKey;
        });
        var nextIndex = initial || currentIndex < 0 ? Math.max(0, currentIndex) :
            (currentIndex + 1) % players.length;
        var player = players[nextIndex];
        cinemaState.currentAutoTrailKey = player.trailKey;
        followPlayer(player.key, { cinemaTransient: true });
        cinemaScheduleCycle(CINEMA_AUTO_CYCLE_MS);
        renderCinemaHud();
    }

    function cinemaLandmarkTour() {
        var landmarks = [];
        var spawn = (poiRecords.get("spawn") || [])[0];
        if (spawn) {
            landmarks.push(spawn);
        }

        var tradersByKey = new Map();
        (poiRecords.get("trader") || []).forEach(function (record) {
            var name = record.name.toLocaleLowerCase();
            var key = name.indexOf("hildir") !== -1
                ? "hildir"
                : name.indexOf("bogwitch") !== -1
                    ? "bogwitch"
                    : name.indexOf("vendor") !== -1
                        ? "vendor"
                        : name;
            if (!tradersByKey.has(key)) {
                tradersByKey.set(key, record);
            }
        });
        ["vendor", "hildir", "bogwitch"].forEach(function (key) {
            var record = tradersByKey.get(key);
            if (record) {
                landmarks.push(record);
                tradersByKey.delete(key);
            }
        });
        Array.from(tradersByKey.keys()).sort().forEach(function (key) {
            landmarks.push(tradersByKey.get(key));
        });

        groupBossJumpRecords(poiRecords.get("boss") || []).filter(function (group) {
            return group.progressionIndex < BOSS_PROGRESSION.length;
        }).slice(0, CINEMA_TOUR_BOSS_COUNT).forEach(function (group) {
            if (group.instances.length > 0) {
                landmarks.push(group.instances[0]);
            }
        });
        return landmarks;
    }

    function cinemaAmbientAnchors() {
        var anchors = [{ x: 0, z: 0 }];
        latestPins.forEach(function (pin) {
            anchors.push({ x: pin.x, z: pin.z });
        });
        POI_GROUP_ORDER.forEach(function (group) {
            (poiRecords.get(group) || []).forEach(function (record) {
                anchors.push({ x: record.x, z: record.z });
            });
        });
        trailBuffers.forEach(function (buffer) {
            if (buffer.samples.length > 0) {
                var sample = buffer.samples[buffer.samples.length - 1];
                anchors.push({ x: sample.x, z: sample.z });
            }
        });
        return anchors;
    }

    function cinemaAmbientStep() {
        if (!cinemaState) {
            return;
        }
        cinemaState.ambientTimer = 0;
        if (document.hidden || cinemaState.raidJumpActive || latestPlayers.length > 0 ||
            !map || !mapMetrics) {
            return;
        }

        var state = cinemaState;
        var landmarks = cinemaLandmarkTour();
        if (landmarks.length > 0) {
            if (!state.ambientTourActive) {
                state.ambientIndex = 0;
                state.ambientTourActive = true;
            }
            var landmark = landmarks[state.ambientIndex % landmarks.length];
            var landmarkDuration = state.ambientHasStarted
                ? CINEMA_AMBIENT_DURATION_SEC
                : CINEMA_ENTRY_DURATION_SEC;
            state.ambientIndex++;
            state.ambientHasStarted = true;
            var landmarkZoom = Math.min(
                map.getMaxZoom(),
                Math.max(map.getMinZoom(), mapMetrics.baseZoom + 1.15)
            );
            map.flyTo(landmark.latLng, landmarkZoom, {
                duration: landmarkDuration,
                easeLinearity: 0.12
            });
            state.ambientTimer = window.setTimeout(
                cinemaAmbientStep,
                (landmarkDuration * 1000) + CINEMA_AMBIENT_STEP_MS
            );
            return;
        }

        state.ambientTourActive = false;
        var anchors = cinemaAmbientAnchors();
        var anchor = anchors[state.ambientIndex % anchors.length];
        state.ambientIndex++;
        var worldExtent = mapMetrics.pixelSize * mapMetrics.textureSize / 2;
        var jitter = Math.min(900, worldExtent * 0.045);
        var x = Math.max(-worldExtent, Math.min(
            worldExtent,
            anchor.x + ((Math.random() - 0.5) * jitter)
        ));
        var z = Math.max(-worldExtent, Math.min(
            worldExtent,
            anchor.z + ((Math.random() - 0.5) * jitter)
        ));
        var zoom = Math.min(
            map.getMaxZoom(),
            Math.max(map.getMinZoom(), mapMetrics.baseZoom + 0.35 + Math.random() * 0.35)
        );
        var driftDuration = state.ambientHasStarted
            ? 16
            : CINEMA_ENTRY_DURATION_SEC;
        state.ambientHasStarted = true;
        map.flyTo(worldToLatLng(x, z), zoom, {
            duration: driftDuration,
            easeLinearity: 0.08
        });
        state.ambientTimer = window.setTimeout(
            cinemaAmbientStep,
            CINEMA_AMBIENT_STEP_MS
        );
    }

    function cinemaStartAmbient() {
        if (!cinemaState || cinemaState.ambientTimer || document.hidden ||
            cinemaState.raidJumpActive || latestPlayers.length > 0) {
            return;
        }
        cinemaClearCycleTimer(cinemaState);
        cinemaAmbientStep();
    }

    function cinemaSetLockedPlayer(player) {
        if (!cinemaState || !player) {
            return;
        }
        cinemaClearCycleTimer(cinemaState);
        cinemaClearWaitingTimer(cinemaState);
        cinemaStopAmbient(cinemaState, false);
        cinemaState.locked = {
            missingSince: 0,
            name: player.displayName,
            trailKey: player.trailKey
        };
        cinemaState.currentAutoTrailKey = "";
        requestTrailBackfill("player", player.trailKey, 1800);
    }

    function cinemaLockPlayer(key) {
        if (!cinemaState) {
            enterCinema(key);
            return;
        }
        var record = markerRecords.get(key);
        if (!record) {
            return;
        }
        cinemaSetLockedPlayer(record.player);
        followPlayer(key, { cinemaTransient: true });
        renderCinemaHud();
    }

    function cinemaBeginWaiting(player) {
        if (!cinemaState || !cinemaState.locked) {
            return;
        }
        var state = cinemaState;
        if (player) {
            state.locked.name = player.displayName;
            state.locked.trailKey = player.trailKey;
        }
        if (!state.locked.missingSince) {
            state.locked.missingSince = Date.now();
        }
        cinemaClearWaitingTimer(state);
        var remaining = Math.max(
            0,
            CINEMA_REFOLLOW_MS - (Date.now() - state.locked.missingSince)
        );
        state.waitingTimer = window.setTimeout(function () {
            if (cinemaState === state && state.locked &&
                !cinemaPlayerRecordByTrailKey(state.locked.trailKey)) {
                cinemaUnlockToAuto();
            }
        }, remaining);
        if (latestPlayers.length === 0) {
            cinemaStartAmbient();
        }
        renderCinemaHud();
    }

    function cinemaUnlockToAuto() {
        if (!cinemaState) {
            return;
        }
        cinemaClearWaitingTimer(cinemaState);
        cinemaState.locked = null;
        cinemaState.currentAutoTrailKey = "";
        followTarget = null;
        updateEntityFocusPolling(false);
        updateFollowStyles();
        updateFollowPill();
        renderPlayerList(latestPlayers);
        renderTrails();
        refreshOpenPopupContent();
        scheduleHashUpdate();
        if (latestPlayers.length > 0 && !cinemaState.raidJumpActive) {
            cinemaCyclePlayer(true);
        } else if (!cinemaState.raidJumpActive) {
            cinemaStartAmbient();
        }
        renderCinemaHud();
    }

    function updateCinemaFromPlayers() {
        if (!cinemaState) {
            return;
        }
        if (cinemaState.locked) {
            var lockedRecord = cinemaPlayerRecordByTrailKey(cinemaState.locked.trailKey);
            if (lockedRecord) {
                cinemaClearWaitingTimer(cinemaState);
                cinemaState.locked.missingSince = 0;
                cinemaState.locked.name = lockedRecord.player.displayName;
                cinemaStopAmbient(cinemaState, false);
                if (!isFollowing("player", lockedRecord.player.key)) {
                    followPlayer(lockedRecord.player.key, { cinemaTransient: true });
                }
            } else {
                cinemaBeginWaiting(null);
            }
            renderCinemaHud();
            return;
        }
        if (cinemaState.raidJumpActive) {
            renderCinemaHud();
            return;
        }
        if (latestPlayers.length === 0) {
            followTarget = null;
            updateFollowStyles();
            updateFollowPill();
            renderTrails();
            cinemaStartAmbient();
            renderCinemaHud();
            return;
        }

        cinemaStopAmbient(cinemaState, false);
        var record = cinemaCurrentPlayerRecord();
        if (!record) {
            cinemaCyclePlayer(true);
            return;
        }
        cinemaState.currentAutoTrailKey = record.player.trailKey;
        if (!cinemaState.cycleTimer) {
            cinemaScheduleCycle(CINEMA_AUTO_CYCLE_MS);
        }
        renderCinemaHud();
    }

    function cinemaFlyToRaid(event) {
        if (!cinemaState || !event || !map || document.hidden) {
            return;
        }
        cinemaClearCycleTimer(cinemaState);
        cinemaStopAmbient(cinemaState, true);
        cinemaState.raidJumpActive = true;
        cinemaState.raidEventId = event.id;
        appRoot.classList.add("is-cinema-raid");
        var center = worldToLatLng(event.x, event.z);
        var radius = Math.max(worldDistanceToMap(event.radius), 0.001);
        var bounds = L.latLngBounds([
            [center.lat - radius, center.lng - radius],
            [center.lat + radius, center.lng + radius]
        ]);
        var zoom = Math.max(
            map.getMinZoom(),
            Math.min(map.getMaxZoom(), map.getBoundsZoom(bounds) - 0.5)
        );
        map.flyTo(center, zoom, { duration: 1.15, easeLinearity: 0.2 });
        renderCinemaHud();
    }

    function cinemaResumeCamera() {
        if (!cinemaState || cinemaState.raidJumpActive || document.hidden) {
            return;
        }
        if (cinemaState.locked) {
            var lockedRecord = cinemaPlayerRecordByTrailKey(cinemaState.locked.trailKey);
            if (lockedRecord) {
                cinemaFlyToPlayer(lockedRecord);
            } else if (latestPlayers.length === 0) {
                cinemaStartAmbient();
            }
            return;
        }
        var record = cinemaCurrentPlayerRecord();
        if (record) {
            cinemaFlyToPlayer(record);
            cinemaScheduleCycle(CINEMA_AUTO_CYCLE_MS);
        } else if (latestPlayers.length > 0) {
            cinemaCyclePlayer(true);
        } else {
            cinemaStartAmbient();
        }
    }

    function syncCinemaRaid(previousEvent, nextEvent) {
        if (!cinemaState) {
            return;
        }
        var previousId = previousEvent ? previousEvent.id : "";
        var nextId = nextEvent ? nextEvent.id : "";
        if (cinemaState.raidEventId && cinemaState.raidEventId !== nextId) {
            cinemaState.raidJumpActive = false;
            cinemaState.raidEventId = "";
            appRoot.classList.remove("is-cinema-raid");
        }
        if (nextEvent && previousId !== nextId && !cinemaRaidOptOutIds.has(nextId)) {
            cinemaFlyToRaid(nextEvent);
            return;
        }
        if (!nextEvent && previousEvent) {
            renderCinemaHud();
            cinemaResumeCamera();
            return;
        }
        renderCinemaHud();
    }

    function cinemaStayOnTarget() {
        if (!cinemaState || !currentRaidEvent || !cinemaState.raidJumpActive) {
            return;
        }
        cinemaRaidOptOutIds.add(currentRaidEvent.id);
        cinemaState.raidJumpActive = false;
        appRoot.classList.remove("is-cinema-raid");
        renderCinemaHud();
        cinemaResumeCamera();
    }

    function renderCinemaHud() {
        if (!cinemaState || !elements.cinemaHud) {
            return;
        }
        elements.cinemaServerName.textContent = elements.serverName.textContent;
        elements.cinemaDay.textContent = elements.dayNumber.textContent;
        elements.cinemaClock.textContent = elements.worldClock.textContent;

        var record = cinemaCurrentPlayerRecord();
        elements.cinemaPlayerCard.hidden = !record;
        if (record) {
            var player = record.player;
            var motion = playerMotion(player);
            elements.cinemaPlayerName.textContent = player.displayName;
            elements.cinemaPlayerBiome.textContent = player.biome || "Unknown wilds";
            elements.cinemaPlayerSpeed.textContent = motion && Number.isFinite(motion.speedMps)
                ? motion.speedMps.toFixed(1) + " m/s · " + playerMovementMode(player, motion)
                : "— m/s · unknown";
            elements.cinemaPlayerHeading.textContent = motion &&
                Number.isFinite(motion.headingDeg)
                ? headingLabel(motion.headingDeg)
                : "—";
            elements.cinemaPlayerSession.textContent =
                Number.isFinite(player.sessionStartUnixMs) && player.sessionStartUnixMs > 0
                    ? formatSessionDuration(player.sessionStartUnixMs)
                    : "—";
        }

        var primary = "";
        var secondary = "";
        var showStayButton = false;
        var isIdle = latestPlayers.length === 0;
        if (isIdle) {
            primary = "No vikings ashore — touring the world until someone joins";
            if (cinemaState.raidJumpActive && currentRaidEvent) {
                secondary = "Raid · " + currentRaidEvent.name;
                showStayButton = true;
            } else if (cinemaState.locked) {
                secondary = "Waiting for " + cinemaState.locked.name + " to return…";
            }
        } else if (cinemaState.raidJumpActive && currentRaidEvent) {
            primary = "Raid · " + currentRaidEvent.name;
            showStayButton = true;
        } else if (cinemaState.locked) {
            primary = cinemaState.locked.missingSince
                ? "Waiting for " + cinemaState.locked.name + " to return…"
                : "Locked on " + cinemaState.locked.name;
        } else {
            primary = "Auto-cycling · click a player to lock";
        }
        elements.cinemaModeChip.textContent = primary;
        elements.cinemaModeChip.classList.toggle("is-idle", isIdle);
        elements.cinemaModeChip.hidden = !primary;
        elements.cinemaSecondaryChip.textContent = secondary;
        elements.cinemaSecondaryChip.hidden = !secondary;
        elements.cinemaStayTarget.hidden = !showStayButton;
        updateCinemaStalenessBadge();
    }

    function updateCinemaStalenessBadge() {
        if (!elements.cinemaStaleness) {
            return;
        }
        var playerFeed = feedStaleness("players");
        var statusFeed = feedStaleness("status");
        var priority = { grey: 0, green: 1, amber: 2, red: 3 };
        var worst = priority[playerFeed.state] >= priority[statusFeed.state]
            ? playerFeed
            : statusFeed;
        if (failedFeeds.has("players") || failedFeeds.has("status")) {
            worst = { state: "red", title: "feed reconnecting" };
        }
        var labels = {
            amber: "Feed delayed",
            green: "Live",
            grey: "Waiting for feeds",
            red: "Feed stale"
        };
        elements.cinemaStaleness.className = "cinema-staleness is-" + worst.state;
        elements.cinemaStaleness.textContent = labels[worst.state];
        elements.cinemaStaleness.title =
            "Players " + playerFeed.title + " · Status " + statusFeed.title;
    }

    function cinemaVisibilityChanged() {
        if (!cinemaState) {
            return;
        }
        cinemaClearCycleTimer(cinemaState);
        cinemaStopAmbient(cinemaState, document.hidden);
        if (!document.hidden) {
            if (cinemaState.raidJumpActive && currentRaidEvent) {
                cinemaFlyToRaid(currentRaidEvent);
            } else {
                cinemaResumeCamera();
            }
        }
    }

    function enterCinema(playerKey, waitingTrailKey) {
        if (!cinemaHasAccess() || !map || !mapMetrics) {
            return;
        }
        if (cinemaState) {
            if (playerKey) {
                cinemaLockPlayer(playerKey);
            }
            return;
        }

        var priorFollow = followTarget ? {
            id: followTarget.id,
            kind: followTarget.kind,
            trailKey: followTarget.trailKey
        } : null;
        var priorPendingFollow = pendingCinemaFromHash ? "" : pendingHashFollowName;
        var priorCenter = map.getCenter();
        cinemaState = {
            ambientHasStarted: false,
            ambientIndex: 0,
            ambientTimer: 0,
            ambientTourActive: false,
            currentAutoTrailKey: "",
            cycleTimer: 0,
            locked: null,
            prior: {
                activeTab: activeTab,
                center: L.latLng(priorCenter.lat, priorCenter.lng),
                followTarget: priorFollow,
                measureActive: measureActive,
                pendingHashFollowName: priorPendingFollow,
                pingArmed: pingArmed,
                sidebarChecked: elements.sidebarState.checked,
                zoom: map.getZoom()
            },
            raidEventId: "",
            raidJumpActive: false,
            stalenessTimer: 0,
            visibilityHandler: cinemaVisibilityChanged,
            waitingTimer: 0
        };
        pendingCinemaFromHash = false;
        pendingHashFollowName = "";
        setActiveTab("map", false);
        if (measureActive) {
            finishMeasurement();
        }
        disarmMapPing();
        disarmShipTow();
        if (map._popup) {
            map.closePopup();
        }
        appRoot.classList.add("is-cinema");
        elements.cinemaHud.hidden = false;
        addAppListener(document, "visibilitychange", cinemaState.visibilityHandler);
        cinemaState.stalenessTimer = window.setInterval(
            updateCinemaStalenessBadge,
            5000
        );
        map.invalidateSize({ animate: false });

        var record = playerKey ? markerRecords.get(playerKey) : null;
        if (record) {
            cinemaSetLockedPlayer(record.player);
            followPlayer(record.player.key, { cinemaTransient: true });
        } else if (waitingTrailKey && waitingTrailKey.startsWith("player:")) {
            cinemaState.locked = {
                missingSince: Date.now(),
                name: waitingTrailKey.slice("player:".length) || "viking",
                trailKey: waitingTrailKey
            };
            requestTrailBackfill("player", waitingTrailKey, 1800);
            cinemaBeginWaiting(null);
        } else {
            followTarget = null;
            updateFollowStyles();
            updateFollowPill();
            updateCinemaFromPlayers();
        }
        if (currentRaidEvent) {
            syncCinemaRaid(null, currentRaidEvent);
        }
        renderTrails();
        renderCinemaHud();
        scheduleHashUpdate();
    }

    function teardownCinemaState(state) {
        cinemaClearCycleTimer(state);
        cinemaClearWaitingTimer(state);
        cinemaStopAmbient(state, true);
        window.clearInterval(state.stalenessTimer);
        removeAppListener(document, "visibilitychange", state.visibilityHandler);
    }

    function exitCinema() {
        if (!cinemaState) {
            return;
        }
        var state = cinemaState;
        cinemaState = null;
        pendingCinemaFromHash = false;
        teardownCinemaState(state);
        appRoot.classList.remove("is-cinema", "is-cinema-raid");
        elements.cinemaHud.hidden = true;
        elements.sidebarState.checked = state.prior.sidebarChecked;

        followTarget = state.prior.followTarget;
        pendingHashFollowName = state.prior.pendingHashFollowName;
        updateEntityFocusPolling(Boolean(
            followTarget && (followTarget.kind === "ship" || followTarget.kind === "cart")
        ));
        updateFollowStyles();
        updateFollowPill();
        renderPlayerList(latestPlayers);
        renderTrails();
        refreshOpenPopupContent();
        setActiveTab(state.prior.activeTab, false);
        map.setView(state.prior.center, state.prior.zoom, { animate: false });
        map.invalidateSize({ animate: false });
        if (state.prior.measureActive && measureModeEnabled) {
            measureActive = true;
            measureDoubleClickZoomWasEnabled = map.doubleClickZoom.enabled();
            if (measureDoubleClickZoomWasEnabled) {
                map.doubleClickZoom.disable();
            }
            appRoot.classList.add("is-measuring");
            updateMeasureHud();
        }
        if (state.prior.pingArmed && currentView === "admin") {
            armMapPing();
        }
        scheduleHashUpdate();
    }

    function tryBootCinemaFromHash() {
        if (!pendingCinemaFromHash || cinemaState || !firstPlayersPayloadReceived ||
            !map || currentView === null) {
            return;
        }
        if (!cinemaHasAccess()) {
            pendingCinemaFromHash = false;
            pendingHashFollowName = "";
            scheduleHashUpdate();
            return;
        }

        var reference = pendingHashFollowName;
        var player = playerByFollowReference(reference);
        if (player) {
            enterCinema(player.key);
        } else {
            enterCinema("", reference);
        }
    }

    function bindCinemaEvents() {
        cinemaWindNeedle = elements.cinemaWind
            ? elements.cinemaWind.querySelector(".cinema-wind-needle")
            : null;
        addAppListener(elements.watchButton, "click", function () {
            var targetKey = followTarget && followTarget.kind === "player"
                ? followTarget.id
                : "";
            enterCinema(targetKey);
        });
        addAppListener(elements.cinemaExit, "click", exitCinema);
        addAppListener(elements.cinemaStayTarget, "click", cinemaStayOnTarget);
        addKeyboardListener(function (event) {
            if (event.key !== "Escape" || !cinemaState) {
                return;
            }
            event.preventDefault();
            event.stopImmediatePropagation();
            exitCinema();
        });
    }

    function renderPlayerList(players) {
        elements.playerList.textContent = "";
        if (players.length === 0) {
            var empty = document.createElement("li");
            empty.className = "empty-player-list";
            empty.textContent = "No vikings ashore";
            elements.playerList.appendChild(empty);
            return;
        }

        players.forEach(function (player) {
            var item = document.createElement("li");
            var button = document.createElement("button");
            var identity = document.createElement("span");
            var dot = document.createElement("span");
            var summary = document.createElement("span");
            var headline = document.createElement("span");
            var name = document.createElement("span");
            var coordinates = document.createElement("span");

            button.type = "button";
            button.className = "player-button";
            button.classList.toggle("is-followed", isFollowing("player", player.key));
            button.classList.toggle("is-dead", player.dead === true);
            addAppListener(button, "click", function () {
                followPlayer(player.key);
            });

            identity.className = "player-identity";
            dot.className = "player-dot";
            summary.className = "player-summary";
            headline.className = "player-headline";
            name.className = "player-name";
            name.textContent = player.displayName;
            coordinates.className = "player-coordinates";
            coordinates.textContent = "X " + Math.round(player.x) + " · Z " + Math.round(player.z);

            identity.appendChild(dot);
            headline.appendChild(name);
            if (player.pvp === true) {
                headline.appendChild(playerStateGlyph("⚔", "PvP on", "is-pvp"));
            }
            if (player.inBed === true) {
                headline.appendChild(playerStateGlyph("☾", "Sleeping", "is-sleeping"));
            }
            summary.appendChild(headline);
            if (hasPlayerHealth(player)) {
                summary.appendChild(buildPlayerHealth(player, false));
            }
            identity.appendChild(summary);
            button.appendChild(identity);
            button.appendChild(coordinates);
            item.appendChild(button);
            if (player.inDungeon) {
                var dungeonTag = document.createElement("button");
                dungeonTag.type = "button";
                dungeonTag.className = "player-dungeon-roster-tag";
                dungeonTag.textContent = "In: " + player.inDungeon.label;
                dungeonTag.title = "View " + player.inDungeon.label + " interior";
                addAppListener(dungeonTag, "click", function (event) {
                    event.preventDefault();
                    event.stopPropagation();
                    openDungeonInterior(player.inDungeon.id);
                });
                item.classList.add("has-dungeon");
                item.appendChild(dungeonTag);
            }
            elements.playerList.appendChild(item);
        });
    }

    function playerStateGlyph(glyph, label, className) {
        var state = document.createElement("span");
        state.className = "player-state-glyph " + className;
        state.textContent = glyph;
        state.title = label;
        state.setAttribute("aria-label", label);
        state.setAttribute("role", "img");
        return state;
    }

    function buildPlayerTooltip(player) {
        var tooltip = document.createElement("span");
        var name = document.createElement("span");
        tooltip.className = "player-tooltip-content";
        name.className = "player-tooltip-name";
        name.textContent = player.displayName;
        tooltip.appendChild(name);
        if (player.inBed === true) {
            var sleeping = document.createElement("span");
            sleeping.className = "player-tooltip-state";
            sleeping.textContent = " · ☾ Sleeping";
            tooltip.appendChild(sleeping);
        }
        if (player.inDungeon) {
            var dungeonTag = document.createElement("button");
            dungeonTag.type = "button";
            dungeonTag.className = "player-dungeon-tag";
            dungeonTag.textContent = "In: " + player.inDungeon.label;
            dungeonTag.title = "View dungeon interior";
            addAppListener(dungeonTag, "click", function (event) {
                event.preventDefault();
                event.stopPropagation();
                openDungeonInterior(player.inDungeon.id);
            });
            tooltip.appendChild(dungeonTag);
        }
        return tooltip;
    }

    function hasPlayerHealth(player) {
        return Number.isFinite(player.health) && Number.isFinite(player.maxHealth) &&
            player.maxHealth > 0;
    }

    function formatVitalNumber(value) {
        return Math.abs(value - Math.round(value)) < 0.05
            ? String(Math.round(value))
            : value.toFixed(1);
    }

    function buildPlayerHealth(player, popup) {
        var health = Math.max(0, player.health);
        var maxHealth = Math.max(0.1, player.maxHealth);
        var ratio = player.dead === true ? 0 : Math.max(0, Math.min(1, health / maxHealth));
        var healthRow = document.createElement("span");
        var track = document.createElement("span");
        var fill = document.createElement("span");
        var value = document.createElement("span");

        healthRow.className = "player-health" + (popup ? " is-popup" : "");
        healthRow.classList.toggle("is-dead", player.dead === true);
        track.className = "player-health-track";
        fill.className = "player-health-fill";
        fill.style.width = (ratio * 100).toFixed(1) + "%";
        value.className = "player-health-value";
        value.textContent = player.dead === true
            ? "☠ Dead"
            : formatVitalNumber(health) + " / " + formatVitalNumber(maxHealth);
        track.appendChild(fill);
        healthRow.appendChild(track);
        healthRow.appendChild(value);
        return healthRow;
    }

    function normalizePoiGroup(group) {
        var normalized = typeof group === "string" ? group.trim().toLowerCase() : "";
        return Object.prototype.hasOwnProperty.call(POI_GROUPS, normalized) ? normalized : "";
    }

    function isResourcePoiGroup(group) {
        var metadata = poiGroupMeta.get(group);
        if (metadata) {
            return metadata.resource === true;
        }
        return Object.prototype.hasOwnProperty.call(POI_GROUPS, group) &&
            POI_GROUPS[group].resource === true;
    }

    function isDungeonEntrancePoiGroup(group) {
        return Object.prototype.hasOwnProperty.call(POI_GROUPS, group) &&
            POI_GROUPS[group].dungeonEntrance === true;
    }

    function prettifyPoiName(name) {
        var pretty = typeof name === "string" ? name.trim() : "";
        if (!pretty) {
            return "Point of interest";
        }

        pretty = pretty.replace(/[_-]+/g, " ");
        pretty = pretty.replace(
            /^(?:Meadows|Black\s*Forest|Swamp|Mountain|Plains|Mistlands|Ashlands|Deep\s*North|Ocean)\s+/i,
            ""
        );
        pretty = pretty.replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2");
        pretty = pretty.replace(/([a-z0-9])([A-Z])/g, "$1 $2");
        pretty = pretty.replace(/\s*\d+$/g, "");
        pretty = pretty.replace(/\s+/g, " ").trim();
        return pretty || "Point of interest";
    }

    function prettifyEntityName(name) {
        var pretty = typeof name === "string" ? name.trim() : "";
        pretty = pretty.replace(/[_-]+/g, " ");
        pretty = pretty.replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2");
        pretty = pretty.replace(/([a-z0-9])([A-Z])/g, "$1 $2");
        pretty = pretty.replace(/\s+/g, " ").trim();
        return pretty || "Entity";
    }

    function positionPopupRow(x, z) {
        var roundedX = Math.round(x);
        var roundedZ = Math.round(z);
        return {
            copy: roundedX + ", " + roundedZ,
            label: "Position",
            value: "X " + roundedX + " · Z " + roundedZ
        };
    }

    function compassLabel(degrees) {
        var normalized = (degrees + 360) % 360;
        var directions = [
            "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
            "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"
        ];
        return directions[Math.round(normalized / 22.5) % directions.length];
    }

    function headingLabel(degrees) {
        var normalized = (degrees + 360) % 360;
        return compassLabel(normalized) + " · " + Math.round(normalized) + "°";
    }

    function playerMotion(player) {
        var fallback = derivedMotion(player.trailKey);
        var speedMps = Number.isFinite(player.speedMps)
            ? player.speedMps
            : fallback && Number.isFinite(fallback.speedMps) ? fallback.speedMps : null;
        var headingDeg = Number.isFinite(player.headingDeg)
            ? player.headingDeg
            : fallback && Number.isFinite(fallback.headingDeg) ? fallback.headingDeg : null;
        return speedMps === null && headingDeg === null
            ? null
            : { headingDeg: headingDeg, speedMps: speedMps };
    }

    function formatSessionDuration(sessionStartUnixMs) {
        var elapsedMinutes = Math.max(0, Math.floor(
            (Date.now() - sessionStartUnixMs) / 60000
        ));
        var hours = Math.floor(elapsedMinutes / 60);
        var minutes = elapsedMinutes % 60;
        return hours > 0 ? hours + "h " + minutes + "m" : minutes + "m";
    }

    function formatTraveledDistance(distanceM) {
        var distance = Math.max(0, distanceM);
        return distance < 1000
            ? Math.round(distance) + " m"
            : (distance / 1000).toFixed(1) + " km";
    }

    function derivePortalPairs(entities) {
        var portalsByTag = new Map();
        portalPairs = [];

        entities.forEach(function (entity) {
            if (entity.group !== "portal") {
                return;
            }

            entity.portalPair = { kind: "unpaired" };
            if (!entity.tag) {
                return;
            }
            if (!portalsByTag.has(entity.tag)) {
                portalsByTag.set(entity.tag, []);
            }
            portalsByTag.get(entity.tag).push(entity);
        });

        portalsByTag.forEach(function (portals) {
            if (portals.length === 2) {
                portals[0].portalPair = { kind: "paired", partner: portals[1] };
                portals[1].portalPair = { kind: "paired", partner: portals[0] };
                portalPairs.push(portals);
                return;
            }
            if (portals.length > 2) {
                portals.forEach(function (portal) {
                    portal.portalPair = { count: portals.length, kind: "conflict" };
                });
            }
        });
    }

    function portalLinkColor() {
        var color = window.getComputedStyle(styleRoot)
            .getPropertyValue("--accent").trim();
        return color || "#d9b168";
    }

    function drawPortalLink(layer, left, right, options) {
        if (!layer || !left || !right) {
            return;
        }

        L.polyline([
            worldToLatLng(left.x, left.z),
            worldToLatLng(right.x, right.z)
        ], {
            color: portalLinkColor(),
            dashArray: options.dashArray,
            interactive: false,
            opacity: options.opacity,
            pane: "trailPane",
            weight: options.weight
        }).addTo(layer);
    }

    function portalEntityById(id) {
        if (!id) {
            return null;
        }
        return latestEntities.find(function (entity) {
            return entity.group === "portal" && entity.id === id;
        }) || null;
    }

    function renderPortalLinks() {
        if (!map || !portalNetworkLayer || !portalPopupLinkLayer) {
            return;
        }

        portalNetworkLayer.clearLayers();
        portalPopupLinkLayer.clearLayers();
        if (layerSettings.portalNetwork && entityLayersAreAvailable()) {
            portalPairs.forEach(function (pair) {
                drawPortalLink(portalNetworkLayer, pair[0], pair[1], {
                    dashArray: "5 7",
                    opacity: 0.38,
                    weight: 1.35
                });
            });
        }

        var openPortal = portalEntityById(openPopupPortalId);
        if (openPortal && openPortal.portalPair &&
            openPortal.portalPair.kind === "paired") {
            drawPortalLink(
                portalPopupLinkLayer,
                openPortal,
                openPortal.portalPair.partner,
                { dashArray: "7 7", opacity: 0.9, weight: 2.1 }
            );
        }
    }

    function tombstoneAgeSec(entity) {
        if (!entity || !Number.isFinite(entity.deathAgeSec)) {
            return null;
        }

        var elapsedSec = Math.max(0, Date.now() - entity.deathAgeSampledAt) / 1000;
        return Math.max(0, entity.deathAgeSec + elapsedSec);
    }

    function formatRelativeAge(ageSec) {
        var seconds = Math.max(0, Math.floor(ageSec));
        if (seconds < 60) {
            return seconds + "s ago";
        }

        var minutes = Math.floor(seconds / 60);
        if (minutes < 60) {
            return minutes + "m ago";
        }

        var hours = Math.floor(minutes / 60);
        if (hours < 24) {
            return hours + "h " + (minutes % 60) + "m ago";
        }

        var days = Math.floor(hours / 24);
        return days + "d " + (hours % 24) + "h ago";
    }

    function formatPlayDuration(totalSeconds) {
        var minutes = Math.max(0, Math.floor(totalSeconds / 60));
        var hours = Math.floor(minutes / 60);
        return hours > 0
            ? hours + "h " + (minutes % 60) + "m"
            : minutes + "m";
    }

    function buildGhostPopup(ghost) {
        var ageSec = Math.max(0, (Date.now() - ghost.lastSeenUnixMs) / 1000);
        return popupShell({
            feed: "pois",
            glyph: "♙",
            kicker: "LAST SEEN",
            rows: [{
                label: "Last seen",
                value: formatRelativeAge(ageSec)
            }, {
                label: "Played",
                value: formatPlayDuration(ghost.totalPlaySeconds)
            }, positionPopupRow(ghost.x, ghost.z)],
            title: ghost.title
        });
    }

    function lastDeathForPlayer(player) {
        if (!player.name) {
            return null;
        }

        var newest = null;
        latestEntities.forEach(function (entity) {
            var ageSec = tombstoneAgeSec(entity);
            if (entity.group !== "tombstone" || entity.owner !== player.name) {
                return;
            }
            if (!newest || (ageSec !== null &&
                (newest.ageSec === null || ageSec < newest.ageSec)) ||
                (ageSec === null && newest.ageSec === null &&
                    entity.id < newest.entity.id)) {
                newest = { ageSec: ageSec, entity: entity };
            }
        });
        return newest;
    }

    function shipMovedRecently(entity) {
        if (!entity || !entity.trailKey) {
            return false;
        }

        var buffer = trailBuffers.get(entity.trailKey);
        return Boolean(buffer && buffer.lastMovedAt && Date.now() - buffer.lastMovedAt <= 25000);
    }

    function playerMovementMode(player, motion) {
        var aboardShip = latestEntities.some(function (entity) {
            return entity.group === "ship" && shipMovedRecently(entity) &&
                worldDistance(player.x, player.z, entity.x, entity.z) <= 12;
        });
        if (aboardShip) {
            return "aboard ship";
        }
        if (motion.speedMps < 0.2) {
            return "rest";
        }
        if (motion.speedMps < 2.6) {
            return "walking";
        }
        return "running";
    }

    function trailIsSelected(kind, key) {
        return selectedTrailTargets.has(trailTargetId(kind, key));
    }

    function buildPlayerPopup(player) {
        var motion = playerMotion(player);
        var rows = [];
        if (player.inDungeon) {
            rows.push({
                action: {
                    action: "dungeon-open",
                    key: player.inDungeon.id,
                    label: "View"
                },
                label: "In",
                value: player.inDungeon.label
            });
        }
        rows.push(positionPopupRow(player.x, player.z));
        if (hasPlayerHealth(player)) {
            rows.push({
                label: "Health",
                valueNode: buildPlayerHealth(player, true)
            });
        }
        if (player.dead === true) {
            rows.push({ label: "State", value: "Dead" });
        } else if (player.inBed === true) {
            rows.push({ label: "State", value: "☾ Sleeping" });
        }
        if (player.pvp === true) {
            rows.push({ label: "PvP", value: "On" });
        }
        if (motion && Number.isFinite(motion.speedMps)) {
            rows.push({
                label: "Speed",
                value: motion.speedMps.toFixed(1) + " m/s · " + playerMovementMode(player, motion)
            });
            if (motion.speedMps >= 0.3 && Number.isFinite(motion.headingDeg)) {
                rows.push({ label: "Heading", value: headingLabel(motion.headingDeg) });
            }
        }
        if (player.biome) {
            rows.push({ label: "Biome", value: player.biome });
        }
        if (Number.isFinite(player.sessionStartUnixMs) && player.sessionStartUnixMs > 0) {
            rows.push({
                label: "Session",
                value: formatSessionDuration(player.sessionStartUnixMs)
            });
        }
        if (Number.isFinite(player.distanceTodayM)) {
            rows.push({
                label: "Traveled today",
                value: formatTraveledDistance(player.distanceTodayM)
            });
        }
        var lastDeath = lastDeathForPlayer(player);
        if (lastDeath) {
            rows.push({
                action: {
                    action: "jump-tombstone",
                    key: lastDeath.entity.id,
                    kind: "tombstone",
                    label: "Jump"
                },
                label: "Last death",
                value: (lastDeath.ageSec === null
                    ? "time unknown"
                    : formatRelativeAge(lastDeath.ageSec)) + " · " +
                    formatTraveledDistance(worldDistance(
                        player.x,
                        player.z,
                        lastDeath.entity.x,
                        lastDeath.entity.z
                    )) + " away"
            });
        }

        var trailSelected = trailIsSelected("player", player.trailKey);
        var actions = [{
            action: "follow",
            kind: "player",
            key: player.key,
            label: isFollowing("player", player.key) ? "Unfollow" : "Follow"
        }];
        if (hasLiveAccess()) {
            actions.push({
                action: "watch",
                kind: "player",
                key: player.key,
                label: "Cinema"
            });
        }
        actions.push({
            action: "trail",
            active: trailSelected,
            key: player.trailKey,
            kind: "player",
            label: trailSelected ? "Hide trail" : "Trail 15m"
        });
        return popupShell({
            actions: actions,
            feed: "players",
            glyph: "●",
            kicker: "PLAYER",
            rows: rows,
            title: player.displayName
        });
    }

    function poiPopupKicker(group) {
        if (group === "boss") {
            return "BOSS ALTAR";
        }
        if (group === "spawn") {
            return "SPAWN";
        }
        if (group === "trader") {
            return "TRADER";
        }
        if (group.indexOf("dungeon_") === 0) {
            return "DUNGEON";
        }
        if (group.indexOf("spawner_") === 0) {
            return "SPAWNER";
        }
        if (group.indexOf("ore_") === 0) {
            return "ORE & DEPOSIT";
        }
        if (group.indexOf("forage_") === 0) {
            return "FORAGE";
        }
        if (group.indexOf("structure_") === 0) {
            return "STRUCTURE";
        }
        return "POINT OF INTEREST";
    }

    function resourcePoiTitle(group) {
        if (group === "bases") {
            return "Player base";
        }
        if (group === "spawner_greydwarf") {
            return "Greydwarf nest";
        }
        if (group === "spawner_bonepile") {
            return "Skeleton spawner";
        }
        if (group === "spawner_draugrpile") {
            return "Draugr spawner";
        }
        if (group === "ore_copper") {
            return "Copper deposit";
        }
        if (group === "ore_tin") {
            return "Tin deposit";
        }
        if (group === "ore_iron") {
            return "Muddy scrap pile";
        }
        if (group === "ore_silver") {
            return "Silver vein";
        }
        if (group === "ore_obsidian") {
            return "Obsidian deposit";
        }
        if (group === "ore_meteorite") {
            return "Meteorite";
        }
        if (group === "ore_leviathan") {
            return "Leviathan";
        }
        return POI_GROUPS[group].label;
    }

    function resourcePoiStateText(record) {
        if (record.group.indexOf("spawner_") === 0) {
            return "";
        }
        if (record.group.indexOf("forage_") === 0) {
            if (record.state === "respawning" || record.available === 0) {
                return "Picked — respawning";
            }
            if (record.available !== null) {
                return record.available + " of " + record.memberCount + " available";
            }
            return "";
        }
        if (record.group === "ore_leviathan") {
            return record.state === "submerged" ? "Submerged" : "";
        }
        if (record.minedPct > 0) {
            return record.minedPct + "% mined";
        }
        if (record.state === "partial") {
            return "Partially mined";
        }
        return "Intact";
    }

    function resourcePoiSurveyUnixMs(group) {
        var state = lazyPoiStates.get(group);
        if (state && Number.isFinite(state.scanUnixMs) && state.scanUnixMs > 0) {
            return state.scanUnixMs;
        }

        var metadata = poiGroupMeta.get(group);
        return metadata && Number.isFinite(metadata.scanUnixMs)
            ? metadata.scanUnixMs
            : 0;
    }

    function resourcePoiIsDimmed(record) {
        return record.state === "respawning" || record.state === "submerged" ||
            record.available === 0 || record.minedPct >= 100;
    }

    function dungeonPoiPlayerCount(record) {
        if (!isDungeonEntrancePoiGroup(record.group)) {
            return 0;
        }
        var dungeon = dungeonForEntrance(record);
        if (dungeon) {
            return dungeon.playersInside;
        }
        if (!firstPlayersPayloadReceived) {
            return 0;
        }
        return nearbyPlayers(record.x, record.z, 15).length;
    }

    function resetDungeonRegistry() {
        window.clearTimeout(dungeonRegistryState.timer);
        dungeonRegistryState = {
            dungeons: [],
            loaded: false,
            pending: false,
            ready: false,
            scanning: false,
            timer: 0
        };
    }

    function scheduleDungeonRegistryPoll(delay) {
        window.clearTimeout(dungeonRegistryState.timer);
        dungeonRegistryState.timer = 0;
        if (!hasLiveAccess() || dungeonRegistryState.ready || pollCircuitOpen) {
            return;
        }
        dungeonRegistryState.timer = window.setTimeout(function () {
            dungeonRegistryState.timer = 0;
            requestDungeonRegistry();
        }, Math.max(0, delay));
    }

    function normalizeDungeonRegistryEntry(dungeon) {
        if (!dungeon || typeof dungeon !== "object" ||
            typeof dungeon.id !== "string" || !dungeon.id.trim() ||
            !dungeon.entrance || !Number.isFinite(Number(dungeon.entrance.x)) ||
            !Number.isFinite(Number(dungeon.entrance.z))) {
            return null;
        }

        return {
            entrance: {
                x: Number(dungeon.entrance.x),
                y: finiteNumberOrNull(dungeon.entrance.y),
                z: Number(dungeon.entrance.z)
            },
            generated: dungeon.generated === true,
            hasInterior: dungeon.hasInterior === true,
            id: dungeon.id.trim(),
            label: typeof dungeon.label === "string" ? dungeon.label.trim() : "",
            playersInside: Math.max(0, Math.floor(Number(dungeon.playersInside) || 0)),
            roomCount: Math.max(0, Math.floor(Number(dungeon.roomCount) || 0)),
            type: typeof dungeon.type === "string" ? dungeon.type.trim() : ""
        };
    }

    async function requestDungeonRegistry() {
        if (!hasLiveAccess() || dungeonRegistryState.pending ||
            dungeonRegistryState.ready || pollCircuitOpen) {
            return;
        }
        if (document.hidden) {
            scheduleDungeonRegistryPoll(DUNGEON_REGISTRY_POLL_INTERVAL_MS);
            return;
        }

        var state = dungeonRegistryState;
        state.pending = true;
        try {
            var payload = await fetchJson("/api/dungeons");
            if (state !== dungeonRegistryState || !hasLiveAccess()) {
                return;
            }

            state.dungeons = (payload && Array.isArray(payload.dungeons)
                ? payload.dungeons
                : []).map(normalizeDungeonRegistryEntry).filter(Boolean);
            state.loaded = true;
            state.ready = payload && payload.ready === true;
            state.scanning = payload && payload.scanning === true;
            refreshOpenPopupContent();
        } catch (error) {
            if (state === dungeonRegistryState) {
                state.scanning = !state.ready;
            }
        } finally {
            if (state !== dungeonRegistryState) {
                return;
            }
            state.pending = false;
            if (!state.ready && hasLiveAccess()) {
                scheduleDungeonRegistryPoll(DUNGEON_REGISTRY_POLL_INTERVAL_MS);
            }
            refreshOpenPopupContent();
        }
    }

    function ensureDungeonRegistry() {
        if (!hasLiveAccess() || dungeonRegistryState.ready ||
            dungeonRegistryState.pending || dungeonRegistryState.timer) {
            return;
        }
        requestDungeonRegistry();
    }

    function dungeonForEntrance(record) {
        if (!record || !dungeonRegistryState.ready) {
            return null;
        }

        var nearest = null;
        var nearestDistance = DUNGEON_MATCH_DISTANCE_M;
        dungeonRegistryState.dungeons.forEach(function (dungeon) {
            var distance = worldDistance(
                record.x,
                record.z,
                dungeon.entrance.x,
                dungeon.entrance.z
            );
            if (distance <= nearestDistance) {
                nearest = dungeon;
                nearestDistance = distance;
            }
        });
        return nearest;
    }

    function dungeonPopupAction(record) {
        ensureDungeonRegistry();
        if (!dungeonRegistryState.ready) {
            return {
                action: "dungeon-open",
                disabled: true,
                label: "Surveying…",
                pending: true
            };
        }

        var dungeon = dungeonForEntrance(record);
        return dungeon
            ? {
                action: "dungeon-open",
                key: dungeon.id,
                label: "View Interior"
            }
            : {
                action: "dungeon-open",
                disabled: true,
                label: "Interior unavailable"
            };
    }

    function buildPoiPopup(record) {
        if (record.group === "bases") {
            return popupShell({
                glyph: POI_GROUPS.bases.glyph,
                iconKey: "bases",
                kicker: "PLAYER BASE",
                rows: [{
                    label: "Structures",
                    value: "≈ " + formatInteger(record.pieces)
                }, positionPopupRow(record.x, record.z)],
                surveyUnixMs: resourcePoiSurveyUnixMs("bases"),
                title: "Player base"
            });
        }

        var rows = [];
        var resource = isResourcePoiGroup(record.group);
        var stateText = resource ? resourcePoiStateText(record) : "";
        var matchedDungeon = isDungeonEntrancePoiGroup(record.group)
            ? dungeonForEntrance(record)
            : null;
        var dungeonPlayerCount = dungeonPoiPlayerCount(record);
        if (stateText) {
            rows.push({ label: "State", value: stateText });
        }
        if (record.memberCount > 1 && record.group.indexOf("forage_") !== 0) {
            rows.push({ label: "Cluster", value: "×" + record.memberCount });
        }
        if (dungeonPlayerCount > 0) {
            rows.push({ label: "Vikings inside", value: String(dungeonPlayerCount) });
        }
        rows.push(positionPopupRow(record.x, record.z));
        var actions = [];
        if (hasLiveAccess() && isDungeonEntrancePoiGroup(record.group)) {
            actions.push(dungeonPopupAction(record));
        }
        return popupShell({
            actions: actions,
            feed: "pois",
            glyph: POI_GROUPS[record.group].glyph,
            iconKey: bossIconKey(record),
            kicker: poiPopupKicker(record.group),
            rows: rows,
            surveyUnixMs: resource ? resourcePoiSurveyUnixMs(record.group) : 0,
            title: matchedDungeon && matchedDungeon.label
                ? matchedDungeon.label
                : record.title
        });
    }

    function dungeonMetadataById(id) {
        var registryMatch = dungeonRegistryState.dungeons.find(function (dungeon) {
            return dungeon.id === id;
        });
        if (registryMatch) {
            return registryMatch;
        }

        var playerMatch = latestPlayers.find(function (player) {
            return player.inDungeon && player.inDungeon.id === id;
        });
        return playerMatch
            ? {
                generated: null,
                id: id,
                label: playerMatch.inDungeon.label,
                playersInside: null,
                roomCount: null,
                type: ""
            }
            : null;
    }

    function setDungeonStage(stage) {
        elements.dungeonLoading.hidden = stage !== "loading";
        elements.dungeonCanvasShell.hidden = stage !== "canvas";
        elements.dungeonEmpty.hidden = stage !== "empty";
        elements.dungeonError.hidden = stage !== "error";
    }

    function renderDungeonHeader(dungeon, loading) {
        dungeon = dungeon || {};
        elements.dungeonTitle.textContent =
            typeof dungeon.label === "string" && dungeon.label
                ? dungeon.label
                : "Dungeon interior";
        var dungeonType = typeof dungeon.type === "string"
            ? dungeon.type.trim()
            : "";
        var dungeonTypeDefinition = Object.prototype.hasOwnProperty.call(
            POI_GROUPS,
            dungeonType
        ) ? POI_GROUPS[dungeonType] : null;
        elements.dungeonType.textContent = loading
            ? "Surveying"
            : dungeonTypeDefinition && dungeonTypeDefinition.label
                ? dungeonTypeDefinition.label
                : dungeonType ? prettifyEntityName(dungeonType) : "—";

        var roomCount = Math.max(0, Math.floor(Number(dungeon.roomCount) || 0));
        elements.dungeonRooms.textContent = loading
            ? "— rooms"
            : roomCount + (roomCount === 1 ? " room" : " rooms");
        elements.dungeonGenerated.textContent = loading
            ? "Surveying"
            : dungeon.generated === true ? "Generated" : "Unvisited";
        elements.dungeonGenerated.classList.toggle(
            "is-unvisited",
            !loading && dungeon.generated !== true
        );

        var playersInside = Math.max(
            0,
            Math.floor(Number(dungeon.playersInside) || 0)
        );
        elements.dungeonLiveStatus.textContent = loading
            ? "Reading the runes…"
            : playersInside + (playersInside === 1
                ? " viking inside"
                : " vikings inside");
    }

    function dungeonEntranceText(entrance) {
        if (!entrance || !Number.isFinite(Number(entrance.x)) ||
            !Number.isFinite(Number(entrance.z))) {
            return "Entrance coordinates unavailable";
        }

        var text = "Entrance · X " + Math.round(Number(entrance.x)) +
            " · Z " + Math.round(Number(entrance.z));
        if (Number.isFinite(Number(entrance.y))) {
            text += " · Y " + Math.round(Number(entrance.y));
        }
        return text;
    }

    function dungeonRoomsFromPayload(dungeon) {
        var rooms = dungeon && dungeon.interior &&
            Array.isArray(dungeon.interior.rooms)
            ? dungeon.interior.rooms
            : [];
        return rooms.filter(function (room) {
            return room && Number.isFinite(Number(room.x)) &&
                Number.isFinite(Number(room.y)) &&
                Number.isFinite(Number(room.z)) &&
                Number.isFinite(Number(room.sizeX)) &&
                Number.isFinite(Number(room.sizeZ)) &&
                Number(room.sizeX) >= 0 && Number(room.sizeZ) >= 0 &&
                (Number(room.sizeX) > 0 || Number(room.sizeZ) > 0);
        }).map(function (room) {
            return {
                name: typeof room.name === "string" ? room.name : "",
                rotYDeg: Number.isFinite(Number(room.rotYDeg))
                    ? Number(room.rotYDeg)
                    : 0,
                sizeX: Math.max(
                    DUNGEON_MIN_VISIBLE_ROOM_DIMENSION_M,
                    Number(room.sizeX)
                ),
                sizeZ: Math.max(
                    DUNGEON_MIN_VISIBLE_ROOM_DIMENSION_M,
                    Number(room.sizeZ)
                ),
                x: Number(room.x),
                y: Number(room.y),
                z: Number(room.z)
            };
        });
    }

    function dungeonPlayersFromPayload(dungeon) {
        var players = dungeon && Array.isArray(dungeon.livePlayers)
            ? dungeon.livePlayers
            : [];
        return players.filter(function (player) {
            return player && Number.isFinite(Number(player.x)) &&
                Number.isFinite(Number(player.z));
        }).map(function (player) {
            return {
                name: typeof player.name === "string" ? player.name : "",
                x: Number(player.x),
                y: finiteNumberOrNull(player.y),
                z: Number(player.z)
            };
        });
    }

    function rotatedRoomCorners(room, overlapM) {
        var radians = room.rotYDeg * Math.PI / 180;
        var cosine = Math.cos(radians);
        var sine = Math.sin(radians);
        var halfX = (room.sizeX / 2) + overlapM;
        var halfZ = (room.sizeZ / 2) + overlapM;
        return [
            { x: -halfX, z: -halfZ },
            { x: halfX, z: -halfZ },
            { x: halfX, z: halfZ },
            { x: -halfX, z: halfZ }
        ].map(function (corner) {
            return {
                x: room.x + (corner.x * cosine) - (corner.z * sine),
                z: room.z + (corner.x * sine) + (corner.z * cosine)
            };
        });
    }

    function dungeonRoomFill(roomY, minY, maxY) {
        var range = Math.max(0.001, maxY - minY);
        var elevation = Math.max(0, Math.min(1, (roomY - minY) / range));
        var low = [210, 185, 139];
        var high = [190, 161, 205];
        var red = Math.round(low[0] + ((high[0] - low[0]) * elevation));
        var green = Math.round(low[1] + ((high[1] - low[1]) * elevation));
        var blue = Math.round(low[2] + ((high[2] - low[2]) * elevation));
        return "rgba(" + red + "," + green + "," + blue + ",0.9)";
    }

    function niceDungeonScaleMeters(targetMeters) {
        if (!Number.isFinite(targetMeters) || targetMeters <= 0) {
            return 1;
        }

        var power = Math.pow(10, Math.floor(Math.log(targetMeters) / Math.LN10));
        var normalized = targetMeters / power;
        var step = normalized >= 5 ? 5 : normalized >= 2 ? 2 : 1;
        return step * power;
    }

    function drawDungeonCanvas(dungeon) {
        if (!dungeon || elements.dungeonCanvasShell.hidden) {
            return;
        }

        var canvas = elements.dungeonCanvas;
        var rooms = dungeonRoomsFromPayload(dungeon);
        if (!canvas || rooms.length === 0) {
            return;
        }

        var width = Math.max(320, Math.floor(canvas.clientWidth));
        var height = Math.max(280, Math.floor(canvas.clientHeight));
        var pixelRatio = Math.max(1, Math.min(2, window.devicePixelRatio || 1));
        canvas.width = Math.floor(width * pixelRatio);
        canvas.height = Math.floor(height * pixelRatio);
        var context = canvas.getContext("2d");
        context.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
        context.clearRect(0, 0, width, height);

        var background = context.createLinearGradient(0, 0, 0, height);
        background.addColorStop(0, "#3a2d20");
        background.addColorStop(1, "#241b14");
        context.fillStyle = background;
        context.fillRect(0, 0, width, height);
        context.strokeStyle = "rgba(217,177,104,0.055)";
        context.lineWidth = 1;
        for (var gridX = 16; gridX < width; gridX += 24) {
            context.beginPath();
            context.moveTo(gridX, 0);
            context.lineTo(gridX, height);
            context.stroke();
        }
        for (var gridY = 16; gridY < height; gridY += 24) {
            context.beginPath();
            context.moveTo(0, gridY);
            context.lineTo(width, gridY);
            context.stroke();
        }

        var entrance = dungeon.interior.entrance || dungeon.interior.origin;
        var players = dungeonPlayersFromPayload(dungeon);
        var bounds = [];
        rooms.forEach(function (room) {
            bounds = bounds.concat(rotatedRoomCorners(room, 0.35));
        });
        if (entrance && Number.isFinite(Number(entrance.x)) &&
            Number.isFinite(Number(entrance.z))) {
            bounds.push({ x: Number(entrance.x), z: Number(entrance.z) });
        }
        players.forEach(function (player) {
            bounds.push({ x: player.x, z: player.z });
        });

        var minX = Math.min.apply(null, bounds.map(function (point) {
            return point.x;
        }));
        var maxX = Math.max.apply(null, bounds.map(function (point) {
            return point.x;
        }));
        var minZ = Math.min.apply(null, bounds.map(function (point) {
            return point.z;
        }));
        var maxZ = Math.max.apply(null, bounds.map(function (point) {
            return point.z;
        }));
        if (maxX - minX < 10) {
            var missingX = 10 - (maxX - minX);
            minX -= missingX / 2;
            maxX += missingX / 2;
        }
        if (maxZ - minZ < 10) {
            var missingZ = 10 - (maxZ - minZ);
            minZ -= missingZ / 2;
            maxZ += missingZ / 2;
        }

        var padding = Math.min(58, Math.max(38, width * 0.065));
        var scale = Math.min(
            (width - (padding * 2)) / Math.max(1, maxX - minX),
            (height - (padding * 2)) / Math.max(1, maxZ - minZ)
        );
        var centerX = (minX + maxX) / 2;
        var centerZ = (minZ + maxZ) / 2;
        function project(x, z) {
            return {
                x: (width / 2) + ((x - centerX) * scale),
                y: (height / 2) - ((z - centerZ) * scale)
            };
        }

        var roomYs = rooms.map(function (room) {
            return room.y;
        });
        var minY = Math.min.apply(null, roomYs);
        var maxY = Math.max.apply(null, roomYs);
        rooms.slice().sort(function (left, right) {
            return left.y - right.y;
        }).forEach(function (room) {
            var center = project(room.x, room.z);
            var overlapM = Math.min(0.35, Math.min(room.sizeX, room.sizeZ) / 8);
            context.save();
            context.translate(center.x, center.y);
            context.rotate(-room.rotYDeg * Math.PI / 180);
            context.fillStyle = dungeonRoomFill(room.y, minY, maxY);
            context.strokeStyle = "rgba(70,48,31,0.9)";
            context.lineWidth = Math.max(1, Math.min(2, scale * 0.08));
            context.fillRect(
                -((room.sizeX / 2) + overlapM) * scale,
                -((room.sizeZ / 2) + overlapM) * scale,
                (room.sizeX + (overlapM * 2)) * scale,
                (room.sizeZ + (overlapM * 2)) * scale
            );
            context.strokeRect(
                -(room.sizeX / 2) * scale,
                -(room.sizeZ / 2) * scale,
                room.sizeX * scale,
                room.sizeZ * scale
            );
            context.restore();
        });

        if (entrance && Number.isFinite(Number(entrance.x)) &&
            Number.isFinite(Number(entrance.z))) {
            var entrancePoint = project(Number(entrance.x), Number(entrance.z));
            context.save();
            context.translate(entrancePoint.x, entrancePoint.y);
            context.fillStyle = "#d9b168";
            context.strokeStyle = "#271b12";
            context.lineWidth = 2;
            context.beginPath();
            context.moveTo(0, -9);
            context.lineTo(8, 7);
            context.lineTo(-8, 7);
            context.closePath();
            context.fill();
            context.stroke();
            context.font = "700 10px " +
                window.getComputedStyle(appRoot).fontFamily;
            context.textAlign = "center";
            context.textBaseline = "top";
            context.fillStyle = "#f0dcae";
            context.shadowColor = "#120e0a";
            context.shadowBlur = 3;
            context.fillText("ENTRANCE", 0, 11);
            context.restore();
        }

        players.forEach(function (player) {
            var point = project(player.x, player.z);
            context.save();
            context.translate(point.x, point.y);
            context.fillStyle = "#7eb1d6";
            context.strokeStyle = "#f5e6c8";
            context.lineWidth = 2;
            context.shadowColor = "rgba(126,177,214,0.8)";
            context.shadowBlur = 8;
            context.beginPath();
            context.arc(0, 0, 5.5, 0, Math.PI * 2);
            context.fill();
            context.stroke();
            context.shadowBlur = 0;
            if (player.name) {
                context.font = "700 11px " +
                    window.getComputedStyle(appRoot).fontFamily;
                var labelWidth = context.measureText(player.name).width + 10;
                context.fillStyle = "rgba(24,17,12,0.9)";
                context.fillRect(-labelWidth / 2, 9, labelWidth, 18);
                context.strokeStyle = "rgba(217,177,104,0.6)";
                context.lineWidth = 1;
                context.strokeRect(-labelWidth / 2, 9, labelWidth, 18);
                context.fillStyle = "#f2e4c8";
                context.textAlign = "center";
                context.textBaseline = "middle";
                context.fillText(player.name, 0, 18);
            }
            context.restore();
        });

        context.save();
        context.fillStyle = "#d9b168";
        context.font = "700 11px " +
            window.getComputedStyle(appRoot).fontFamily;
        context.textAlign = "center";
        context.fillText("N", width - 28, 24);
        context.beginPath();
        context.moveTo(width - 28, 31);
        context.lineTo(width - 28, 50);
        context.moveTo(width - 34, 38);
        context.lineTo(width - 28, 31);
        context.lineTo(width - 22, 38);
        context.strokeStyle = "#d9b168";
        context.lineWidth = 2;
        context.stroke();

        var scaleMeters = niceDungeonScaleMeters(82 / scale);
        var scalePixels = scaleMeters * scale;
        var scaleLeft = 22;
        var scaleBottom = height - 23;
        context.beginPath();
        context.moveTo(scaleLeft, scaleBottom - 6);
        context.lineTo(scaleLeft, scaleBottom);
        context.lineTo(scaleLeft + scalePixels, scaleBottom);
        context.lineTo(scaleLeft + scalePixels, scaleBottom - 6);
        context.strokeStyle = "#d9b168";
        context.lineWidth = 2;
        context.stroke();
        context.fillStyle = "#f0dcae";
        context.textAlign = "center";
        context.fillText(
            scaleMeters + " m",
            scaleLeft + (scalePixels / 2),
            scaleBottom - 10
        );
        context.restore();

        var elevationRange = Math.max(0, maxY - minY);
        elements.dungeonScale.textContent = "Scale · " + scaleMeters + " m";
        elements.dungeonElevation.hidden = elevationRange < 0.5;
        elements.dungeonElevation.textContent = elevationRange >= 0.5
            ? "Elevation tint · low 0 m → high +" +
                Math.round(elevationRange * 10) / 10 + " m"
            : "";
        canvas.setAttribute(
            "aria-label",
            (dungeon.label || "Dungeon") + " interior with " +
                rooms.length + " rooms and " +
                players.length + " visible players"
        );
    }

    function renderDungeonPayload(dungeon) {
        renderDungeonHeader(dungeon, false);
        elements.dungeonError.textContent = "";
        if (dungeon.generated !== true) {
            setDungeonStage("empty");
            elements.dungeonEmptyCopy.textContent =
                "Interior not yet generated — no viking has been here";
            elements.dungeonEntranceInfo.textContent =
                dungeonEntranceText(dungeon.entrance);
            return;
        }

        var rooms = dungeonRoomsFromPayload(dungeon);
        if (!dungeon.interior || rooms.length === 0) {
            setDungeonStage("empty");
            elements.dungeonEmptyCopy.textContent =
                "The entrance is known, but no interior rooms were found";
            elements.dungeonEntranceInfo.textContent =
                dungeonEntranceText(dungeon.entrance);
            return;
        }

        setDungeonStage("canvas");
        window.requestAnimationFrame(function () {
            if (activeDungeonId === dungeon.id) {
                drawDungeonCanvas(dungeon);
            }
        });
    }

    function renderDungeonLoading(metadata) {
        renderDungeonHeader(metadata, true);
        elements.dungeonError.textContent = "";
        setDungeonStage("loading");
    }

    function renderDungeonError(message) {
        elements.dungeonError.textContent =
            message || "The dungeon survey could not be loaded";
        elements.dungeonLiveStatus.textContent = "Survey unavailable";
        setDungeonStage("error");
    }

    function scheduleDungeonDetailPoll(delay) {
        window.clearTimeout(dungeonDetailPollTimer);
        dungeonDetailPollTimer = 0;
        if (!activeDungeonId || !hasLiveAccess() || pollCircuitOpen) {
            return;
        }
        dungeonDetailPollTimer = window.setTimeout(function () {
            dungeonDetailPollTimer = 0;
            requestActiveDungeonDetail();
        }, Math.max(0, delay));
    }

    async function requestActiveDungeonDetail() {
        if (!activeDungeonId || !hasLiveAccess() || pollCircuitOpen) {
            return;
        }
        if (document.hidden) {
            scheduleDungeonDetailPoll(POLL_INTERVAL_MS);
            return;
        }
        if (dungeonDetailRequestPending) {
            return;
        }

        var dungeonId = activeDungeonId;
        var requestSequence = dungeonDetailRequestSequence;
        dungeonDetailRequestPending = true;
        try {
            var dungeon = await fetchJson(
                "/api/dungeons/" + encodeURIComponent(dungeonId)
            );
            if (dungeonId !== activeDungeonId ||
                requestSequence !== dungeonDetailRequestSequence) {
                return;
            }
            if (!dungeon || typeof dungeon !== "object" ||
                typeof dungeon.id !== "string") {
                throw new Error("Invalid dungeon survey");
            }
            dungeonDetailCache.set(dungeonId, dungeon);
            renderDungeonPayload(dungeon);
        } catch (error) {
            if (dungeonId === activeDungeonId &&
                requestSequence === dungeonDetailRequestSequence &&
                !dungeonDetailCache.has(dungeonId)) {
                renderDungeonError(error && error.message);
            } else if (dungeonId === activeDungeonId &&
                requestSequence === dungeonDetailRequestSequence) {
                elements.dungeonLiveStatus.textContent = "Live update delayed";
            }
        } finally {
            if (requestSequence !== dungeonDetailRequestSequence) {
                return;
            }
            dungeonDetailRequestPending = false;
            if (dungeonId === activeDungeonId) {
                scheduleDungeonDetailPoll(POLL_INTERVAL_MS);
            }
        }
    }

    function openDungeonInterior(dungeonId) {
        if (!hasLiveAccess() || typeof dungeonId !== "string" || !dungeonId) {
            return;
        }

        if (map && map._popup) {
            map.closePopup();
        }
        dungeonReturnFocus = document.activeElement;
        activeDungeonId = dungeonId;
        dungeonDetailRequestSequence++;
        dungeonDetailRequestPending = false;
        window.clearTimeout(dungeonDetailPollTimer);
        dungeonDetailPollTimer = 0;
        elements.dungeonBackdrop.hidden = false;
        appRoot.classList.add("is-dungeon-open");

        var cached = dungeonDetailCache.get(dungeonId);
        if (cached) {
            renderDungeonPayload(cached);
        } else {
            renderDungeonLoading(dungeonMetadataById(dungeonId));
        }
        scheduleDungeonDetailPoll(0);
        window.requestAnimationFrame(function () {
            elements.dungeonClose.focus();
            if (cached && activeDungeonId === dungeonId) {
                drawDungeonCanvas(cached);
            }
        });
    }

    function closeDungeonInterior(restoreFocus) {
        if (elements.dungeonBackdrop.hidden) {
            return;
        }

        activeDungeonId = "";
        dungeonDetailRequestSequence++;
        dungeonDetailRequestPending = false;
        window.clearTimeout(dungeonDetailPollTimer);
        dungeonDetailPollTimer = 0;
        elements.dungeonBackdrop.hidden = true;
        appRoot.classList.remove("is-dungeon-open");
        if (restoreFocus !== false && dungeonReturnFocus &&
            styleRoot.contains(dungeonReturnFocus) &&
            typeof dungeonReturnFocus.focus === "function") {
            dungeonReturnFocus.focus();
        }
        dungeonReturnFocus = null;
    }

    function bindDungeonEvents() {
        addAppListener(elements.dungeonClose, "click", function () {
            closeDungeonInterior(true);
        });
        addAppListener(elements.dungeonBackdrop, "click", function (event) {
            if (event.target === elements.dungeonBackdrop) {
                closeDungeonInterior(true);
            }
        });
        addKeyboardListener(function (event) {
            if (event.key === "Escape" && !elements.dungeonBackdrop.hidden) {
                event.preventDefault();
                closeDungeonInterior(true);
            }
        });

        if (typeof window.ResizeObserver === "function") {
            dungeonResizeObserver = new window.ResizeObserver(function () {
                var dungeon = dungeonDetailCache.get(activeDungeonId);
                if (dungeon) {
                    drawDungeonCanvas(dungeon);
                }
            });
            dungeonResizeObserver.observe(elements.dungeonCanvasShell);
        } else {
            window.addEventListener("resize", function () {
                var dungeon = dungeonDetailCache.get(activeDungeonId);
                if (dungeon) {
                    drawDungeonCanvas(dungeon);
                }
            });
        }
    }

    function buildPinPopup(pin) {
        var rows = [];
        var author = typeof pin.author === "string" ? pin.author.trim() : "";
        if (pin.checked) {
            rows.push({ label: "Status", value: "✓ charted-off" });
        }
        if (author) {
            rows.push({ label: "Charted by", value: author });
        }
        rows.push(positionPopupRow(pin.x, pin.z));
        return popupShell({
            feed: "pins",
            glyph: "⌖",
            kicker: "CHARTED PIN",
            rows: rows,
            title: pin.name
        });
    }

    function buildWebPinPopup(pin) {
        var actions = [];
        if (canEditWebPin(pin)) {
            actions.push({
                action: "webpin-toggle",
                key: pin.id,
                label: pin.checked ? "Restore" : "Check off"
            });
            actions.push({ action: "webpin-edit", key: pin.id, label: "Edit" });
            actions.push({
                action: "webpin-delete",
                danger: true,
                key: pin.id,
                label: "Delete"
            });
        }
        var popup = popupShell({
            actions: actions,
            actionsInFooter: actions.length > 0,
            feed: "webpins",
            glyph: "✦",
            kicker: "WEB PIN",
            rows: [
                { label: "Status", value: pin.checked ? "✓ charted-off" : "Open" },
                { label: "Charted by", value: pin.author },
                positionPopupRow(pin.x, pin.z)
            ],
            title: pin.label || "Web pin"
        });
        popup.classList.toggle("webpin-checked", pin.checked);
        return popup;
    }

    function shipDisplayName(prefab) {
        var normalized = prefab.replace(/[^a-z0-9]/gi, "").toLowerCase();
        if (normalized.indexOf("ashlands") !== -1 || normalized.indexOf("drakkar") !== -1) {
            return "Drakkar";
        }
        if (normalized.indexOf("vikingship") !== -1 || normalized.indexOf("longship") !== -1) {
            return "Longship";
        }
        if (normalized.indexOf("karve") !== -1) {
            return "Karve";
        }
        if (normalized.indexOf("raft") !== -1) {
            return "Raft";
        }
        return prettifyEntityName(prefab);
    }

    function creatureDisplayName(entity) {
        var name = entity && entity.name ? entity.name : entity.prefab;
        name = typeof name === "string" ? name.replace(/^\$enemy_/i, "") : "";
        return prettifyEntityName(name || "Creature");
    }

    function nearbyPlayers(x, z, radius) {
        return latestPlayers.filter(function (player) {
            return worldDistance(x, z, player.x, player.z) <= radius;
        });
    }

    function nearestPlayer(x, z, radius) {
        var nearest = null;
        latestPlayers.forEach(function (player) {
            var distance = worldDistance(x, z, player.x, player.z);
            if (distance <= radius && (!nearest || distance < nearest.distance)) {
                nearest = { distance: distance, player: player };
            }
        });
        return nearest;
    }

    function buildShipPopup(entity) {
        var rows = [positionPopupRow(entity.x, entity.z)];
        var motion = derivedMotion(entity.trailKey);
        if (motion) {
            rows.push({
                label: "Speed",
                value: motion.speedMps.toFixed(1) + " m/s · " +
                    (motion.speedMps * 1.9438).toFixed(1) + " kn"
            });
            if (motion.speedMps >= SHIP_MOVING_SPEED_MPS) {
                rows.push({ label: "Heading", value: headingLabel(motion.headingDeg) });
                if (latestWind) {
                    var windTowardDeg = (latestWind.fromDeg + 180) % 360;
                    var relativeDeg = Math.abs(
                        ((motion.headingDeg - windTowardDeg + 540) % 360) - 180
                    );
                    var alignment = relativeDeg < 45
                        ? "Wind astern"
                        : relativeDeg < 100 ? "Wind abeam" : "Headwind";
                    rows.push({
                        label: "Wind",
                        value: alignment + " · " + Math.round(latestWind.intensity * 100) + "%"
                    });
                }
            }
        }
        var crew = nearbyPlayers(entity.x, entity.z, 12).map(function (player) {
            return player.displayName;
        });
        rows.push({ label: "Crew", value: crew.length > 0 ? crew.join(", ") : "None nearby" });

        var trailSelected = trailIsSelected("ship", entity.trailKey);
        var actions = [{
            action: "follow",
            key: entity.trailKey,
            kind: "ship",
            label: isFollowing("ship", entity.trailKey) ? "Unfollow" : "Follow"
        }, {
            action: "trail",
            active: trailSelected,
            key: entity.trailKey,
            kind: "ship",
            label: trailSelected ? "Hide trail" : "Trail 15m"
        }];
        if (currentView === "admin" && entity.id) {
            actions.push({
                action: "tow",
                key: entity.id,
                kind: "ship",
                label: "Tow"
            });
        }
        return popupShell({
            actions: actions,
            feed: "entities",
            glyph: ENTITY_GROUPS.ship.glyph,
            kicker: "SHIP",
            rows: rows,
            title: shipDisplayName(entity.prefab)
        });
    }

    function buildCartPopup(entity) {
        var rows = [positionPopupRow(entity.x, entity.z)];
        var puller = nearestPlayer(entity.x, entity.z, 6);
        if (puller) {
            rows.push({ label: "Pulled by", value: puller.player.displayName });
        } else {
            rows.push({ label: "Status", value: "Idle" });
        }
        var trailSelected = trailIsSelected("cart", entity.trailKey);
        return popupShell({
            actions: [{
                action: "follow",
                key: entity.trailKey,
                kind: "cart",
                label: isFollowing("cart", entity.trailKey) ? "Unfollow" : "Follow"
            }, {
                action: "trail",
                active: trailSelected,
                key: entity.trailKey,
                kind: "cart",
                label: trailSelected ? "Hide trail" : "Trail 15m"
            }],
            feed: "entities",
            glyph: ENTITY_GROUPS.cart.glyph,
            kicker: "CART",
            rows: rows,
            title: "Cart"
        });
    }

    function buildPortalPopup(entity) {
        var pair = entity.portalPair || { kind: "unpaired" };
        var rows = [{ label: "Tag", value: entity.tag || "—" }];
        var actions = [];
        if (pair.kind === "paired") {
            var partner = pair.partner;
            rows.push({
                label: "Status",
                value: "Paired → " + Math.round(partner.x) + ", " +
                    Math.round(partner.z) + " (" +
                    formatTraveledDistance(worldDistance(
                        entity.x,
                        entity.z,
                        partner.x,
                        partner.z
                    )) + ")"
            });
            actions.push({
                action: "jump-portal",
                key: partner.id,
                label: "Jump to pair"
            });
        } else if (pair.kind === "conflict") {
            rows.push({
                label: "Status",
                value: "Tag conflict (" + pair.count + " portals)"
            });
        } else {
            rows.push({ label: "Status", value: "Unpaired" });
        }
        rows.push(positionPopupRow(entity.x, entity.z));
        return popupShell({
            actions: actions,
            feed: "entities",
            glyph: ENTITY_GROUPS.portal.glyph,
            kicker: "PORTAL",
            rows: rows,
            title: entity.tag || "Portal"
        });
    }

    function buildTombstonePopup(entity) {
        var ageSec = tombstoneAgeSec(entity);
        return popupShell({
            feed: "entities",
            glyph: ENTITY_GROUPS.tombstone.glyph,
            kicker: "TOMBSTONE",
            rows: [{
                label: "Owner",
                value: entity.owner || "Unknown"
            }, {
                label: "Death",
                value: ageSec === null
                    ? "time unknown"
                    : "died " + formatRelativeAge(ageSec)
            }, positionPopupRow(entity.x, entity.z)],
            title: "Tombstone"
        });
    }

    function buildWardPopup(entity) {
        return popupShell({
            feed: "entities",
            glyph: ENTITY_GROUPS.ward.glyph,
            kicker: "WARD",
            rows: [{
                label: "Owner",
                value: entity.owner || "Unknown"
            }, {
                label: "Status",
                value: entity.wardEnabled ? "Active" : "Inactive"
            }, {
                label: "Radius",
                value: entity.wardRadius === null
                    ? "Unknown"
                    : formatScaleDistance(entity.wardRadius)
            }, positionPopupRow(entity.x, entity.z)],
            title: "Protected area"
        });
    }

    function buildBedPopup(entity) {
        return popupShell({
            feed: "entities",
            glyph: ENTITY_GROUPS.bed.glyph,
            kicker: "BED · SPAWN POINT",
            rows: [{
                label: "Owner",
                value: entity.owner || "None"
            }, {
                label: "Status",
                value: entity.owner ? "Claimed" : "Unclaimed"
            }, positionPopupRow(entity.x, entity.z)],
            title: "Spawn point"
        });
    }

    function buildCreaturePopup(entity) {
        var iconKey = creatureIconKey(entity);
        var rows = [];
        if (entity.stars !== null && entity.stars > 0) {
            rows.push({ label: "Level", value: entity.stars + "★" });
        }
        rows.push(positionPopupRow(entity.x, entity.z));
        return popupShell({
            feed: "entities",
            glyph: ENTITY_GROUPS.creatures.glyph,
            iconKey: iconKey,
            kicker: iconKey.indexOf("boss_") === 0
                ? "BOSS"
                : iconKey === "creature_serpent" ? "SEA CREATURE" : "RAID CREATURE",
            rows: rows,
            title: creatureDisplayName(entity)
        });
    }

    function buildEntityPopup(entity) {
        if (entity.group === "creatures") {
            return buildCreaturePopup(entity);
        }
        if (entity.group === "ship") {
            return buildShipPopup(entity);
        }
        if (entity.group === "cart") {
            return buildCartPopup(entity);
        }
        if (entity.group === "portal") {
            return buildPortalPopup(entity);
        }
        if (entity.group === "ward") {
            return buildWardPopup(entity);
        }
        if (entity.group === "bed") {
            return buildBedPopup(entity);
        }
        return buildTombstonePopup(entity);
    }

    function raidProgressState(event) {
        if (!event || !Number.isFinite(event.duration) || event.duration <= 0) {
            return null;
        }

        var sampledAgo = feedLastUpdated.status > 0
            ? Math.max(0, Date.now() - feedLastUpdated.status) / 1000
            : 0;
        var elapsed = Math.max(0, Math.min(
            event.duration,
            event.elapsed + sampledAgo
        ));
        return {
            elapsed: elapsed,
            percentage: elapsed / event.duration * 100,
            remaining: Math.max(0, event.duration - elapsed)
        };
    }

    function formatRaidRemaining(seconds) {
        if (seconds <= 10) {
            return "ending soon";
        }

        var wholeSeconds = Math.ceil(seconds);
        var minutes = Math.floor(wholeSeconds / 60);
        var remainder = wholeSeconds % 60;
        return minutes > 0
            ? minutes + "m " + remainder + "s left"
            : remainder + "s left";
    }

    function updateRaidProgress(element, event) {
        var state = raidProgressState(event);
        if (!element || !state) {
            return;
        }

        var fill = element.querySelector(".vo-raid-progress-fill");
        var text = element.querySelector(".vo-raid-progress-text");
        var remainingText = formatRaidRemaining(state.remaining);
        element.setAttribute("aria-valuenow", String(Math.round(state.percentage)));
        element.setAttribute("aria-valuetext", remainingText);
        if (fill) {
            fill.style.width = state.percentage.toFixed(1) + "%";
        }
        if (text) {
            text.textContent = remainingText;
        }
    }

    function buildRaidProgress(event) {
        if (!raidProgressState(event)) {
            return null;
        }

        var progress = document.createElement("span");
        var track = document.createElement("span");
        var fill = document.createElement("span");
        var text = document.createElement("span");
        progress.className = "vo-raid-progress";
        progress.setAttribute("role", "progressbar");
        progress.setAttribute("aria-label", "Raid progress");
        progress.setAttribute("aria-valuemin", "0");
        progress.setAttribute("aria-valuemax", "100");
        track.className = "vo-raid-progress-track";
        fill.className = "vo-raid-progress-fill";
        text.className = "vo-raid-progress-text";
        track.appendChild(fill);
        progress.appendChild(track);
        progress.appendChild(text);
        updateRaidProgress(progress, event);
        return progress;
    }

    function refreshOpenRaidProgress() {
        if (!map || !map._popup || !map._popup.getElement() || !currentRaidEvent) {
            return;
        }

        var source = map._popup._source;
        var progress = map._popup.getElement().querySelector(".vo-raid-progress");
        if (source && source._voPopupKind === "raid" && progress) {
            updateRaidProgress(progress, currentRaidEvent);
        }
    }

    function buildRaidPopup() {
        var event = currentRaidEvent;
        var progress = buildRaidProgress(event);
        var rows = [{
            label: "Radius",
            value: Math.round(event.radius) + " m"
        }, {
            label: "Vikings inside",
            value: String(nearbyPlayers(event.x, event.z, event.radius).length)
        }];
        if (progress) {
            rows.push({
                label: "Progress",
                valueNode: progress
            });
        }
        return popupShell({
            feed: "status",
            glyph: "◯",
            kicker: "RAID EVENT",
            rows: rows,
            title: event.name
        });
    }

    function clearPoiLayers() {
        stopAllLazyPoiPolling();
        poiLayers.forEach(function (layer) {
            layer.clearLayers();
        });
        poiRecords.forEach(function (records) {
            records.length = 0;
        });
        availablePoiGroups.clear();
        poiGroupMeta.clear();
        lazyPoiStates.clear();
        renderLayerRows();
        syncLayerVisibility();
    }

    function normalizePoiRecord(poi, group) {
        if (!poi || !group || !Number.isFinite(Number(poi.x)) ||
            !Number.isFinite(Number(poi.z))) {
            return null;
        }

        var memberCount = Math.floor(Number(poi.count));
        if (!Number.isFinite(memberCount) || memberCount < 1) {
            memberCount = 1;
        }
        var state = typeof poi.state === "string" ? poi.state.trim().toLowerCase() : "";
        if (["intact", "partial", "respawning", "submerged"].indexOf(state) === -1) {
            state = "";
        }
        var minedPct = Math.floor(Number(poi.minedPct));
        if (!Number.isFinite(minedPct) || minedPct < 1 ||
            group.indexOf("ore_") !== 0) {
            minedPct = 0;
        } else {
            minedPct = Math.min(100, minedPct);
        }
        var available = Math.floor(Number(poi.available));
        if (!Number.isFinite(available) || available < 0 ||
            group.indexOf("forage_") !== 0) {
            available = null;
        } else {
            available = Math.min(memberCount, available);
        }
        if (!state && group.indexOf("ore_") === 0 && group !== "ore_leviathan") {
            state = "intact";
        }
        if (!state && available === 0) {
            state = "respawning";
        }
        var resource = isResourcePoiGroup(group);
        var lastSeenUnixMs = finiteNumberOrNull(poi.lastSeenUnixMs);
        var lastSessionSeconds = finiteNumberOrNull(poi.lastSessionSeconds);
        var totalPlaySeconds = finiteNumberOrNull(poi.totalPlaySeconds);
        if (group === "ghosts" && (lastSeenUnixMs === null ||
            lastSessionSeconds === null || totalPlaySeconds === null)) {
            return null;
        }
        var baseRadius = group === "bases" ? finiteNumberOrNull(poi.radius) : null;
        var basePieces = group === "bases" ? Math.floor(Number(poi.pieces)) : 0;
        var baseId = group === "bases" && typeof poi.id === "string"
            ? poi.id.trim()
            : "";
        if (group === "bases" && (!baseId || baseRadius === null || baseRadius <= 0 ||
            !Number.isFinite(basePieces) || basePieces < 1)) {
            return null;
        }
        return {
            available: available,
            baseId: baseId,
            explored: poi.explored !== false,
            group: group,
            latLng: worldToLatLng(Number(poi.x), Number(poi.z)),
            lastSeenUnixMs: lastSeenUnixMs,
            lastSessionSeconds: lastSessionSeconds,
            memberCount: memberCount,
            minedPct: minedPct,
            name: typeof poi.name === "string" ? poi.name : "",
            pieces: basePieces,
            placed: poi.placed !== false,
            radius: baseRadius,
            state: state,
            title: group === "ghosts"
                ? (typeof poi.name === "string" && poi.name.trim()
                    ? poi.name.trim()
                    : "Unknown viking")
                : resource ? resourcePoiTitle(group) : prettifyPoiName(poi.name),
            totalPlaySeconds: totalPlaySeconds,
            x: Number(poi.x),
            z: Number(poi.z)
        };
    }

    function replacePoiGroupRecords(group, pois) {
        var records = poiRecords.get(group);
        if (!records) {
            return;
        }

        records.length = 0;
        pois.forEach(function (poi) {
            var record = normalizePoiRecord(poi, group);
            if (record) {
                records.push(record);
            }
        });
    }

    function bucketRecordsOnGrid(records, weightForRecord, accumulateRecord) {
        var buckets = Object.create(null);
        records.forEach(function (record) {
            var point = map.latLngToContainerPoint(record.latLng);
            var cell = Math.floor(point.x / OVERVIEW_CLUSTER_GRID_PX) + ":" +
                Math.floor(point.y / OVERVIEW_CLUSTER_GRID_PX);
            var weight = typeof weightForRecord === "function"
                ? Number(weightForRecord(record))
                : 1;
            if (!Number.isFinite(weight) || weight <= 0) {
                weight = 1;
            }
            if (!buckets[cell]) {
                buckets[cell] = {
                    count: 0,
                    latitude: 0,
                    longitude: 0,
                    records: [],
                    weight: 0
                };
            }
            var bucket = buckets[cell];
            bucket.count += weight;
            bucket.latitude += record.latLng.lat * weight;
            bucket.longitude += record.latLng.lng * weight;
            bucket.records.push(record);
            bucket.weight += weight;
            if (typeof accumulateRecord === "function") {
                accumulateRecord(bucket, record, weight);
            }
        });
        return Object.keys(buckets).map(function (cell) {
            return buckets[cell];
        });
    }

    function clusterBucketCenter(bucket) {
        return L.latLng(
            bucket.latitude / bucket.weight,
            bucket.longitude / bucket.weight
        );
    }

    function bindClusterZoom(marker) {
        marker.on("click", function () {
            if (!map) {
                return;
            }
            var targetZoom = Math.min(
                map.getMaxZoom(),
                Math.max(OVERVIEW_CLUSTER_ZOOM, map.getZoom() + 1)
            );
            map.setView(marker.getLatLng(), targetZoom, { animate: true });
        });
        return marker;
    }

    function createPoiMarker(record) {
        if (record.group === "ghosts") {
            return createGhostMarker(record);
        }

        var dimmed = resourcePoiIsDimmed(record);
        var markerMarkup = iconMarkup(
            poiIconKey(record),
            POI_GROUPS[record.group].glyph
        );
        var icon = L.divIcon({
            className: "poi-div-icon poi-" + record.group +
                (dimmed ? " is-resource-unavailable" : ""),
            html: '<span class="poi-marker-shell" aria-hidden="true">' +
                markerMarkup + "</span>",
            iconAnchor: [10, 10],
            iconSize: [20, 20]
        });
        var stateText = isResourcePoiGroup(record.group)
            ? resourcePoiStateText(record)
            : "";
        var hoverText = record.title + (stateText ? " — " + stateText : "") +
            (record.memberCount > 1 && record.group.indexOf("forage_") !== 0
                ? " ×" + record.memberCount
                : "");
        var tooltipCardOptions = {
            fallbackGlyph: POI_GROUPS[record.group].glyph,
            iconKey: poiIconKey(record),
            title: record.title
        };
        var marker = L.marker(record.latLng, {
            icon: icon,
            opacity: (record.placed ? 1 : 0.55) * (record.explored ? 1 : 0.45) *
                (dimmed ? 0.52 : 1),
            pane: "poiPane",
            title: hoverText
        });
        var tooltipContent = document.createElement("span");
        tooltipContent.textContent = hoverText;
        bindMarkerTooltip(marker, tooltipContent, tooltipCardOptions, {
            className: "map-tooltip poi-tooltip",
            direction: "top",
            offset: [0, -10],
            opacity: 1
        });
        marker.on("tooltipopen", function () {
            var dungeonPlayerCount = dungeonPoiPlayerCount(record);
            var freshHoverText = hoverText + (dungeonPlayerCount > 0
                ? " · " + dungeonPlayerCount + " inside"
                : "");
            updateMarkerTooltip(marker, freshHoverText, tooltipCardOptions);
        });
        bindMapPopup(marker, function () {
            return buildPoiPopup(record);
        }, { kind: "poi" });
        record.marker = marker;
        return marker;
    }

    function renderBaseArea(record, layer) {
        var radius = worldDistanceToMap(record.radius);
        if (!Number.isFinite(radius) || radius <= 0) {
            return;
        }

        var color = window.getComputedStyle(styleRoot)
            .getPropertyValue("--accent").trim() || "#d9b168";
        L.circle(record.latLng, {
            bubblingMouseEvents: false,
            className: "base-area",
            color: color,
            fill: true,
            fillColor: color,
            fillOpacity: 0.035,
            interactive: false,
            opacity: 0.34,
            pane: "baseAreaPane",
            radius: radius,
            weight: 1
        }).addTo(layer);
    }

    function createBaseMarker(record) {
        var markerMarkup = iconMarkup("bases", POI_GROUPS.bases.glyph);
        var icon = L.divIcon({
            className: "base-div-icon",
            html: '<span class="base-marker-shell" aria-hidden="true">' +
                markerMarkup + '</span><span class="base-marker-label">Base</span>',
            iconAnchor: [10, 10],
            iconSize: [58, 20]
        });
        var marker = L.marker(record.latLng, {
            icon: icon,
            pane: "poiPane",
            title: "Player base · approximately " + record.pieces + " structures"
        });
        var tooltipContent = document.createElement("span");
        tooltipContent.textContent = "Player base · ≈ " + record.pieces + " structures";
        bindMarkerTooltip(marker, tooltipContent, {
            fallbackGlyph: POI_GROUPS.bases.glyph,
            iconKey: "bases",
            title: "Player base"
        }, {
            className: "map-tooltip poi-tooltip",
            direction: "top",
            offset: [0, -10],
            opacity: 1
        });
        bindMapPopup(marker, function () {
            return buildPoiPopup(record);
        }, { kind: "poi" });
        record.marker = marker;
        return marker;
    }

    function createGhostMarker(record) {
        var icon = L.divIcon({
            className: "ghost-div-icon",
            html: '<span class="ghost-marker-shell" aria-hidden="true">' +
                iconMarkup("ghosts", "♙") + "</span>",
            iconAnchor: [11, 11],
            iconSize: [22, 22]
        });
        var marker = L.marker(record.latLng, {
            icon: icon,
            opacity: 0.62,
            pane: "poiPane",
            title: record.title
        });
        var tooltip = document.createElement("span");
        tooltip.textContent = record.title;
        marker.bindTooltip(tooltip, {
            className: "ghost-tooltip",
            direction: "top",
            offset: [0, -8],
            opacity: 1,
            permanent: true
        });
        bindMapPopup(marker, function () {
            return buildGhostPopup(record);
        }, { kind: "ghost" });
        record.marker = marker;
        return marker;
    }

    function createPoiClusterMarker(group, bucket) {
        var center = clusterBucketCenter(bucket);
        var count = bucket.count;
        var unavailable = bucket.activeWeight === 0;
        var clusterIconKey = bucket.records.length > 0
            ? poiIconKey(bucket.records[0])
            : group;
        if (bucket.records.some(function (record) {
            return poiIconKey(record) !== clusterIconKey;
        })) {
            clusterIconKey = group;
        }
        var clusterMarkup = iconMarkup(clusterIconKey, POI_GROUPS[group].glyph);
        var icon = L.divIcon({
            className: "poi-div-icon poi-cluster-icon poi-" + group +
                (unavailable ? " is-resource-unavailable" : ""),
            html: '<span class="poi-cluster-shell" aria-hidden="true">' +
                '<span class="poi-cluster-mark">' + clusterMarkup +
                '</span><strong>' + count + "</strong></span>",
            iconAnchor: [18, 12],
            iconSize: [36, 24]
        });
        var marker = L.marker(center, {
            icon: icon,
            opacity: (0.45 + (0.55 * bucket.exploredWeight / bucket.weight)) *
                (0.52 + (0.48 * bucket.activeWeight / bucket.weight)),
            pane: "poiPane",
            title: count + " " + POI_GROUPS[group].label
        });
        var tooltipContent = document.createElement("span");
        tooltipContent.textContent = count + " " + POI_GROUPS[group].label;
        bindMarkerTooltip(marker, tooltipContent, {
            fallbackGlyph: POI_GROUPS[group].glyph,
            iconKey: clusterIconKey,
            title: count + " " + POI_GROUPS[group].label
        }, {
            className: "map-tooltip poi-tooltip",
            direction: "top",
            offset: [0, -11],
            opacity: 1
        });
        return bindClusterZoom(marker);
    }

    function renderPoiGroup(group, useClusters) {
        var layer = poiLayers.get(group);
        var records = poiRecords.get(group) || [];
        if (!layer) {
            return;
        }

        layer.clearLayers();
        records.forEach(function (record) {
            record.marker = null;
        });
        if (group === "bases") {
            records.forEach(function (record) {
                createBaseMarker(record).addTo(layer);
                renderBaseArea(record, layer);
            });
            return;
        }
        if (!useClusters || group === "ghosts") {
            records.forEach(function (record) {
                createPoiMarker(record).addTo(layer);
            });
            return;
        }

        var buckets = bucketRecordsOnGrid(records, function (record) {
            var weight = record.memberCount || 1;
            return weight;
        }, function (bucket, record, weight) {
            bucket.activeWeight = bucket.activeWeight || 0;
            bucket.exploredWeight = bucket.exploredWeight || 0;
            if (record.explored) {
                bucket.exploredWeight += weight;
            }
            if (!resourcePoiIsDimmed(record)) {
                bucket.activeWeight += weight;
            }
        });
        buckets.forEach(function (bucket) {
            createPoiClusterMarker(group, bucket).addTo(layer);
        });
    }

    function renderPoiLayers() {
        if (!map) {
            return;
        }

        var useClusters = map.getZoom() < OVERVIEW_CLUSTER_ZOOM;
        POI_GROUP_ORDER.forEach(function (group) {
            if (isPoiGroupZoomGated(group)) {
                return;
            }
            renderPoiGroup(group, useClusters && !poiGroupHasZoomGate(group));
        });
    }

    function renderOverviewClustersAfterMove() {
        if (!map) {
            return;
        }

        var zoom = map.getZoom();
        var usedClusters = overviewClusterRenderZoom !== null &&
            overviewClusterRenderZoom < OVERVIEW_CLUSTER_ZOOM;
        var useClusters = zoom < OVERVIEW_CLUSTER_ZOOM;
        if (!useClusters && !usedClusters) {
            overviewClusterRenderZoom = zoom;
            return;
        }
        overviewClusterRenderZoom = zoom;
        renderPoiLayers();
        renderPins();
    }

    function getLazyPoiState(group) {
        var state = lazyPoiStates.get(group);
        if (!state) {
            state = {
                lastFetchAt: 0,
                loaded: false,
                requestPending: false,
                scanEtaSeconds: null,
                scanProgress: null,
                scanUnixMs: 0,
                scanning: false,
                timer: 0
            };
            lazyPoiStates.set(group, state);
        }
        return state;
    }

    function lazyPoiLoadingAllowed(group) {
        var metadata = poiGroupMeta.get(group);
        return Boolean(map && layerSettings[group] &&
            !isPoiGroupZoomGated(group) &&
            availablePoiGroups.has(group) &&
            metadata && metadata.inline === false);
    }

    function lazyPoiRefreshesWhileVisible(group) {
        return isResourcePoiGroup(group) ||
            (Object.prototype.hasOwnProperty.call(POI_GROUPS, group) &&
             POI_GROUPS[group].dynamic === true);
    }

    function lazyPoiRefreshInterval(group) {
        return group === "bases"
            ? BASE_POI_REFRESH_INTERVAL_MS
            : RESOURCE_POI_REFRESH_INTERVAL_MS;
    }

    function stopLazyPoiPolling(group) {
        var state = lazyPoiStates.get(group);
        if (!state) {
            return;
        }
        window.clearTimeout(state.timer);
        state.timer = 0;
    }

    function stopAllLazyPoiPolling() {
        lazyPoiStates.forEach(function (state) {
            window.clearTimeout(state.timer);
            state.timer = 0;
        });
    }

    function scheduleLazyPoiPoll(group, delay) {
        var state = getLazyPoiState(group);
        window.clearTimeout(state.timer);
        state.timer = 0;
        if (!lazyPoiLoadingAllowed(group) || document.hidden || pollCircuitOpen) {
            return;
        }
        state.timer = window.setTimeout(function () {
            state.timer = 0;
            loadLazyPoiGroup(group);
        }, Math.max(0, delay));
    }

    function updateLazyPoiLoading() {
        POI_GROUP_ORDER.forEach(function (group) {
            if (!lazyPoiLoadingAllowed(group)) {
                stopLazyPoiPolling(group);
                return;
            }

            var state = getLazyPoiState(group);
            var resource = isResourcePoiGroup(group);
            var refreshes = lazyPoiRefreshesWhileVisible(group);
            if (state.requestPending || state.timer) {
                return;
            }
            if (!state.loaded || (resource && state.scanning)) {
                state.scanning = resource;
                scheduleLazyPoiPoll(group, 0);
                return;
            }
            if (refreshes) {
                scheduleLazyPoiPoll(
                    group,
                    Math.max(
                        0,
                        state.lastFetchAt + lazyPoiRefreshInterval(group) - Date.now()
                    )
                );
            }
        });
        updateLayerCounts();
    }

    async function loadLazyPoiGroup(group) {
        if (!lazyPoiLoadingAllowed(group) || document.hidden || pollCircuitOpen) {
            stopLazyPoiPolling(group);
            return;
        }

        var state = getLazyPoiState(group);
        var resource = isResourcePoiGroup(group);
        var refreshes = lazyPoiRefreshesWhileVisible(group);
        if (state.requestPending) {
            return;
        }
        state.requestPending = true;
        if (resource && !state.loaded) {
            state.scanning = true;
        }
        updateLayerCounts();
        var nextDelay = RESOURCE_POI_POLL_INTERVAL_MS;
        try {
            var payload = await fetchJson("/api/pois?group=" + encodeURIComponent(group));
            recordPollSuccess("poi-" + group);
            if (lazyPoiStates.get(group) !== state ||
                normalizePoiGroup(payload && payload.group) !== group) {
                return;
            }

            var pois = payload && Array.isArray(payload.pois) ? payload.pois : [];
            replacePoiGroupRecords(group, pois);
            availablePoiGroups.add(group);
            var metadata = poiGroupMeta.get(group);
            var count = Math.floor(Number(payload.count));
            var cap = Math.floor(Number(payload.cap));
            var scanEtaSeconds = Math.floor(Number(payload.scanEtaSeconds));
            var scanProgress = Math.floor(Number(payload.scanProgress));
            var scanUnixMs = Math.floor(Number(payload.scanUnixMs));
            if (!Number.isFinite(count) || count < 0) {
                count = pois.reduce(function (total, poi) {
                    var memberCount = Math.floor(Number(poi && poi.count));
                    return total + (Number.isFinite(memberCount) && memberCount > 0
                        ? memberCount
                        : 1);
                }, 0);
            }
            if (!Number.isFinite(scanUnixMs) || scanUnixMs < 0) {
                scanUnixMs = 0;
            }
            if (!Number.isFinite(scanEtaSeconds) || scanEtaSeconds < 0) {
                scanEtaSeconds = null;
            }
            if (!Number.isFinite(scanProgress) || scanProgress < 0 ||
                scanProgress > 100) {
                scanProgress = null;
            }
            if (metadata) {
                metadata.count = count;
                metadata.cap = Number.isFinite(cap) && cap > 0 ? cap : count;
                metadata.truncated = payload && payload.truncated === true;
                metadata.pieceCap = Math.floor(Number(payload.pieceCap));
                metadata.piecesTruncated = payload && payload.piecesTruncated === true;
                if (resource) {
                    metadata.scanUnixMs = scanUnixMs;
                }
            }
            state.lastFetchAt = Date.now();
            state.loaded = resource ? scanUnixMs > 0 : true;
            state.scanEtaSeconds = resource ? scanEtaSeconds : null;
            state.scanProgress = resource ? scanProgress : null;
            state.scanUnixMs = resource ? scanUnixMs : 0;
            state.scanning = resource && payload && payload.scanning === true;
            if (resource) {
                if (count === 0 && state.scanning && layerSettings[group] &&
                    !resourceSurveyToastGroups.has(group)) {
                    resourceSurveyToastGroups.add(group);
                    showNoticeToast(
                        "Surveying the world for " + POI_GROUPS[group].label +
                        " — first results in a few minutes"
                    );
                }
                nextDelay = state.scanning
                    ? RESOURCE_POI_POLL_INTERVAL_MS
                    : lazyPoiRefreshInterval(group);
            } else if (refreshes) {
                nextDelay = lazyPoiRefreshInterval(group);
            }
            feedLastUpdated.pois = Date.now();
            setFeedState("pois", true);
            renderPoiGroup(
                group,
                map.getZoom() < OVERVIEW_CLUSTER_ZOOM &&
                    !poiGroupHasZoomGate(group)
            );
            syncLayerVisibility();
            if (searchControlElement && searchControlElement.classList.contains("is-open")) {
                renderMapSearchResults();
            }
        } catch (error) {
            recordPollFailure("poi-" + group);
            state.scanning = resource && !state.loaded;
        } finally {
            state.requestPending = false;
            if (refreshes && lazyPoiStates.get(group) === state &&
                lazyPoiLoadingAllowed(group)) {
                scheduleLazyPoiPoll(group, nextDelay);
            }
            updateLayerCounts();
        }
    }

    async function loadPoisForCurrentView() {
        var accessKey = currentView;
        if (!map || !currentView || lastPoiRequestedView === accessKey) {
            return;
        }

        lastPoiRequestedView = accessKey;
        var requestView = accessKey;
        var requestSequence = ++poiRequestSequence;
        poiLoadPending = true;
        clearPoiLayers();

        try {
            var payload = await fetchJson("/api/pois");
            if (requestSequence !== poiRequestSequence ||
                requestView !== currentView) {
                return;
            }

            var groups = payload && Array.isArray(payload.groups) ? payload.groups : [];
            groups.forEach(function (entry) {
                var group = normalizePoiGroup(entry && entry.key);
                if (!group) {
                    return;
                }

                var count = Math.floor(Number(entry.count));
                var cap = Math.floor(Number(entry.cap));
                var scanUnixMs = Math.floor(Number(entry.scanUnixMs));
                var metadata = {
                    cap: Number.isFinite(cap) && cap > 0
                        ? cap
                        : Number.isFinite(count) && count >= 0 ? count : 0,
                    category: typeof entry.category === "string" ? entry.category : "",
                    count: Number.isFinite(count) && count >= 0 ? count : 0,
                    inline: entry.inline !== false,
                    key: group,
                    label: typeof entry.label === "string" && entry.label.trim()
                        ? entry.label.trim()
                        : POI_GROUPS[group].label,
                    resource: entry.resource === true,
                    scanUnixMs: Number.isFinite(scanUnixMs) && scanUnixMs >= 0
                        ? scanUnixMs
                        : 0,
                    truncated: entry.truncated === true
                };
                poiGroupMeta.set(group, metadata);
                availablePoiGroups.add(group);
                if (isResourcePoiGroup(group)) {
                    getLazyPoiState(group).scanUnixMs = metadata.scanUnixMs;
                }
            });

            var pois = payload && Array.isArray(payload.pois) ? payload.pois : [];
            pois.forEach(function (poi) {
                var group = normalizePoiGroup(poi && poi.group);
                var record = normalizePoiRecord(poi, group);
                if (!record) {
                    return;
                }

                poiRecords.get(group).push(record);
                availablePoiGroups.add(group);
            });
            if (groups.length === 0) {
                POI_GROUP_ORDER.forEach(function (group) {
                    var count = (poiRecords.get(group) || []).length;
                    if (count > 0) {
                        poiGroupMeta.set(group, {
                            cap: count,
                            category: POI_GROUPS[group].category,
                            count: count,
                            inline: true,
                            key: group,
                            label: POI_GROUPS[group].label,
                            resource: POI_GROUPS[group].resource === true,
                            scanUnixMs: 0,
                            truncated: false
                        });
                    }
                });
            }

            feedLastUpdated.pois = Date.now();
            poiLoadPending = false;
            setFeedState("pois", true);
            renderPoiLayers();
            renderLayerRows();
            syncLayerVisibility();
        } catch (error) {
            if (requestSequence === poiRequestSequence &&
                requestView === currentView) {
                poiLoadPending = false;
                setFeedState("pois", false);
                renderLayerRows();
            }
        }
    }

    function entityLayersAreAvailable() {
        return hasLiveAccess() && entityAvailability === "available";
    }

    function entityDataIsNeeded() {
        return latestPlayers.length > 0 || layerSettings.portalNetwork ||
            ENTITY_GROUP_ORDER.some(function (group) {
            return layerSettings[group];
        });
    }

    function clearEntityLayers(preserveState) {
        entityMarkerRecords.forEach(function (record) {
            cancelMarkerTween(record.animationKey);
        });
        entityLayers.forEach(function (layer) {
            layer.clearLayers();
        });
        if (wardRadiusLayer) {
            wardRadiusLayer.clearLayers();
        }
        clearShipHeadingLines();
        entityMarkerRecords.clear();
        portalMarkerRecords.clear();
        if (!preserveState) {
            entityRevision = null;
            latestEntities = [];
            entityGroupMeta.clear();
            derivePortalPairs(latestEntities);
            openPopupPortalId = "";
        }
        renderPortalLinks();
    }

    function updateEntityAvailability(status) {
        if (!hasLiveAccess() || typeof status.entities !== "boolean") {
            return;
        }

        if (!status.entities) {
            window.clearTimeout(entityPollTimer);
            entityPollTimer = 0;
            if (followTarget && followTarget.kind !== "player") {
                clearFollow();
            }
            if (entityAvailability !== "unavailable") {
                entityAvailability = "unavailable";
                clearEntityLayers();
                setFeedState("entities", true);
                renderLayerRows();
                syncLayerVisibility();
            }
            return;
        }

        if (entityAvailability === "unavailable") {
            entityAvailability = "unknown";
            ensureEntityFeed();
        }
    }

    function normalizeEntityPayload(payload) {
        var entities = payload && Array.isArray(payload.entities) ? payload.entities : [];
        var normalized = [];
        var receivedAt = Date.now();
        var previousById = new Map();
        latestEntities.forEach(function (entity) {
            if (entity.id) {
                previousById.set(entity.id, entity);
            }
        });
        entities.forEach(function (entity) {
            var group = entity && typeof entity.group === "string"
                ? entity.group.trim().toLowerCase()
                : "";
            if (!Object.prototype.hasOwnProperty.call(ENTITY_GROUPS, group) ||
                !Number.isFinite(Number(entity.x)) || !Number.isFinite(Number(entity.z))) {
                return;
            }

            var prefab = typeof entity.prefab === "string" && entity.prefab.trim()
                ? entity.prefab.trim()
                : ENTITY_GROUPS[group].label;
            var entityId = typeof entity.id === "string" ? entity.id.trim() : "";
            var deathAgeSec = entity.deathAgeSec == null
                ? null
                : finiteNumberOrNull(entity.deathAgeSec);
            if (deathAgeSec !== null) {
                deathAgeSec = Math.max(0, deathAgeSec);
            }
            var wardRadius = group === "ward"
                ? finiteNumberOrNull(entity.wardRadius)
                : null;
            if (wardRadius !== null && wardRadius <= 0) {
                wardRadius = null;
            }
            var deathAgeSampledAt = receivedAt;
            var previous = previousById.get(entityId);
            if (previous && previous.deathAgeSec === deathAgeSec) {
                deathAgeSampledAt = previous.deathAgeSampledAt;
            }
            var creatureLevel = group === "creatures"
                ? finiteNumberOrNull(entity.level)
                : null;
            if (creatureLevel !== null) {
                creatureLevel = Math.max(1, Math.floor(creatureLevel));
            }
            var creatureStars = group === "creatures"
                ? finiteNumberOrNull(entity.stars)
                : null;
            if (creatureStars === null && creatureLevel !== null) {
                creatureStars = Math.max(0, creatureLevel - 1);
            } else if (creatureStars !== null) {
                creatureStars = Math.max(0, Math.floor(creatureStars));
            }
            normalized.push({
                deathAgeSampledAt: deathAgeSampledAt,
                deathAgeSec: deathAgeSec,
                group: group,
                id: entityId,
                isNewestDeath: false,
                owner: typeof entity.owner === "string" ? entity.owner.trim() : "",
                prefab: prefab,
                name: typeof entity.name === "string" ? entity.name.trim() : "",
                level: creatureLevel,
                rotYDeg: finiteNumberOrNull(entity.rotYDeg),
                stars: creatureStars,
                tag: typeof entity.tag === "string" ? entity.tag.trim() : "",
                trailKey: movingEntityGroup(group) && entityId
                    ? "entity:" + entityId
                    : "",
                wardEnabled: group === "ward" ? entity.wardEnabled === true : null,
                wardRadius: wardRadius,
                x: Number(entity.x),
                y: Number(entity.y),
                z: Number(entity.z)
            });
        });

        var previousShips = latestEntities.filter(function (entity) {
            return entity.group === "ship" && !entity.id && entity.trailKey;
        });
        var currentShips = normalized.filter(function (entity) {
            return entity.group === "ship" && !entity.trailKey;
        });
        var matches = [];
        currentShips.forEach(function (entity, currentIndex) {
            previousShips.forEach(function (previous, previousIndex) {
                var distance = worldDistance(entity.x, entity.z, previous.x, previous.z);
                if (distance <= SHIP_MATCH_DISTANCE) {
                    matches.push({
                        currentIndex: currentIndex,
                        distance: distance,
                        previousIndex: previousIndex
                    });
                }
            });
        });
        matches.sort(function (left, right) {
            return left.distance - right.distance;
        });
        var assignedCurrent = new Set();
        var assignedPrevious = new Set();
        matches.forEach(function (match) {
            if (assignedCurrent.has(match.currentIndex) || assignedPrevious.has(match.previousIndex)) {
                return;
            }
            currentShips[match.currentIndex].trailKey = previousShips[match.previousIndex].trailKey;
            assignedCurrent.add(match.currentIndex);
            assignedPrevious.add(match.previousIndex);
        });
        currentShips.forEach(function (entity) {
            if (!entity.trailKey) {
                entity.trailKey = "ship:" + nextShipTrackId;
                nextShipTrackId++;
            }
        });
        var newestTombstones = new Map();
        normalized.forEach(function (entity) {
            var ageSec = tombstoneAgeSec(entity);
            if (entity.group !== "tombstone" || !entity.owner || ageSec === null) {
                return;
            }

            var newest = newestTombstones.get(entity.owner);
            if (!newest || ageSec < newest.ageSec ||
                (ageSec === newest.ageSec && entity.id < newest.entity.id)) {
                newestTombstones.set(entity.owner, { ageSec: ageSec, entity: entity });
            }
        });
        newestTombstones.forEach(function (newest) {
            newest.entity.isNewestDeath = true;
        });
        return normalized;
    }

    function updateEntityGroupMetadata(payload) {
        entityGroupMeta.clear();
        var groups = payload && Array.isArray(payload.groups) ? payload.groups : [];
        groups.forEach(function (entry) {
            var group = entry && typeof entry.key === "string"
                ? entry.key.trim().toLowerCase()
                : "";
            if (!Object.prototype.hasOwnProperty.call(ENTITY_GROUPS, group)) {
                return;
            }

            var count = Math.floor(Number(entry.count));
            var cap = Math.floor(Number(entry.cap));
            if (!Number.isFinite(count) || count < 0) {
                count = 0;
            }
            if (!Number.isFinite(cap) || cap < 1) {
                cap = count;
            }
            entityGroupMeta.set(group, {
                cap: cap,
                count: count,
                truncated: entry.truncated === true
            });
        });
    }

    function entityMarkerTitle(entity) {
        if (entity.group === "creatures") {
            return creatureDisplayName(entity) +
                (entity.stars !== null && entity.stars > 0
                    ? " · " + entity.stars + "★"
                    : "");
        }
        if (entity.group === "tombstone" && entity.owner) {
            return entity.owner + " · Tombstone";
        }
        if (entity.group === "ward") {
            return (entity.owner || "Unknown owner") + " · " +
                (entity.wardEnabled ? "Active ward" : "Inactive ward");
        }
        if (entity.group === "bed") {
            return entity.owner ? entity.owner + " · Spawn point" : "Unclaimed bed";
        }
        return entity.prefab;
    }

    function renderWardRadius(entity) {
        if (!wardRadiusLayer || entity.group !== "ward" || entity.wardRadius === null) {
            return;
        }

        var radius = worldDistanceToMap(entity.wardRadius);
        if (!Number.isFinite(radius) || radius <= 0) {
            return;
        }

        var color = window.getComputedStyle(styleRoot)
            .getPropertyValue("--accent").trim() || "#d9b168";
        L.circle(worldToLatLng(entity.x, entity.z), {
            bubblingMouseEvents: false,
            className: "ward-radius " +
                (entity.wardEnabled ? "is-active" : "is-inactive"),
            color: color,
            dashArray: entity.wardEnabled ? null : "3 5",
            fill: entity.wardEnabled,
            fillColor: color,
            fillOpacity: entity.wardEnabled ? 0.14 : 0,
            interactive: false,
            opacity: entity.wardEnabled ? 0.58 : 0.24,
            pane: "wardRadiusPane",
            radius: radius,
            weight: entity.wardEnabled ? 1.5 : 1
        }).addTo(wardRadiusLayer);
    }

    function moveEntityMarker(record, latLng) {
        if (record.entity.group === "ship") {
            updateShipHeadingLine(record.entity, latLng);
        }
        if (isFollowing(record.entity.group, record.entity.trailKey)) {
            map.panTo(latLng, { animate: false });
        }
    }

    function tweenEntityMarker(record, target, duration) {
        var currentWorld = latLngToWorld(record.marker.getLatLng());
        var targetWorld = latLngToWorld(target);
        var allowTeleportTween = pendingShipTowTweenIds.has(record.entity.id) &&
            currentWorld && targetWorld &&
            worldDistance(currentWorld.x, currentWorld.z, targetWorld.x, targetWorld.z) > 0.01;
        if (allowTeleportTween) {
            pendingShipTowTweenIds.delete(record.entity.id);
        }
        tweenMarker(record.animationKey, record.marker, target, duration, {
            allowTeleportTween: allowTeleportTween,
            onMove: function (latLng) {
                moveEntityMarker(record, latLng);
            },
            trailKey: record.entity.trailKey,
            trailKind: record.entity.group
        });
    }

    function renderEntityPayload(entities, tweenDuration) {
        var popupSource = map && map._popup ? map._popup._source : null;
        var reopenEntityKey = popupSource &&
            movingEntityGroup(popupSource._voPopupKind)
            ? popupSource._voTrailKey
            : "";
        var reopenEntityId = popupSource ? popupSource._voEntityId : "";
        var movingStarts = new Map();
        entityMarkerRecords.forEach(function (record, key) {
            movingStarts.set(key, record.marker.getLatLng());
        });
        clearEntityLayers(true);
        var reopenMarker = null;
        entities.forEach(function (entity) {
            renderWardRadius(entity);
            var markerIconKey = entity.group === "creatures"
                ? creatureIconKey(entity)
                : entity.group;
            var markerMarkup = iconMarkup(
                markerIconKey,
                ENTITY_GROUPS[entity.group].glyph
            );
            var icon = L.divIcon({
                className: "entity-div-icon entity-" + entity.group,
                html: '<span class="entity-marker-shell" aria-hidden="true">' +
                    markerMarkup + "</span>",
                iconAnchor: [11, 11],
                iconSize: [22, 22]
            });
            if (entity.isNewestDeath) {
                icon.options.className += " is-newest-death";
            }
            if (entity.group === "ward" && !entity.wardEnabled) {
                icon.options.className += " is-inactive";
            }
            var markerTitle = entityMarkerTitle(entity);
            var target = worldToLatLng(entity.x, entity.z);
            var start = entity.trailKey ? movingStarts.get(entity.trailKey) : null;
            var marker = L.marker(start || target, {
                icon: icon,
                title: markerTitle
            });
            var tooltipContent = document.createElement("span");
            tooltipContent.textContent = markerTitle;
            bindMarkerTooltip(marker, tooltipContent, {
                fallbackGlyph: ENTITY_GROUPS[entity.group].glyph,
                iconKey: markerIconKey,
                title: markerTitle
            }, {
                className: "map-tooltip entity-tooltip",
                direction: "top",
                offset: [0, -11],
                opacity: 1
            });
            var record = {
                animationKey: "entity-marker:" + entity.trailKey,
                entity: entity,
                marker: marker
            };
            bindMapPopup(marker, function () {
                return buildEntityPopup(record.entity);
            }, {
                entityId: entity.id,
                kind: entity.group,
                trailKey: entity.trailKey,
                trailKind: movingEntityGroup(entity.group) ? entity.group : ""
            });
            marker.addTo(entityLayers.get(entity.group));
            if (entity.group === "portal" && entity.id) {
                portalMarkerRecords.set(entity.id, record);
            }
            if (movingEntityGroup(entity.group) && entity.trailKey) {
                entityMarkerRecords.set(entity.trailKey, record);
                if (start) {
                    tweenEntityMarker(record, target, tweenDuration || entityTweenDurationMs);
                }
                if (entity.trailKey === reopenEntityKey) {
                    reopenMarker = marker;
                }
            }
            if (entity.id && entity.id === reopenEntityId) {
                reopenMarker = marker;
            }
        });

        if (reopenMarker) {
            window.setTimeout(function () {
                if (map && reopenMarker._map) {
                    reopenMarker.openPopup();
                }
            }, 0);
        }
        if (followTarget && followTarget.kind !== "player" &&
            !entityMarkerRecords.has(followTarget.id)) {
            clearFollow();
        }
        updateFollowStyles();
        updateFollowPill();
        applyPendingHashFollow();
    }

    function updateEntityMarkerRecords(entities) {
        entities.forEach(function (entity) {
            if (movingEntityGroup(entity.group) &&
                entityMarkerRecords.has(entity.trailKey)) {
                entityMarkerRecords.get(entity.trailKey).entity = entity;
            }
            if (entity.group === "portal" && entity.id &&
                portalMarkerRecords.has(entity.id)) {
                var record = portalMarkerRecords.get(entity.id);
                record.entity = entity;
                record.marker.setLatLng(worldToLatLng(entity.x, entity.z));
            }
        });
    }

    function updateEntityPolling(immediate) {
        window.clearTimeout(entityPollTimer);
        entityPollTimer = 0;
        if (!map || !hasLiveAccess() || document.hidden || pollCircuitOpen ||
            entityAvailability === "unavailable" ||
            entityRequestPending || !entityDataIsNeeded()) {
            return;
        }

        entityPollTimer = window.setTimeout(
            pollEntities,
            immediate ? 0 : ENTITIES_POLL_INTERVAL_MS
        );
    }

    function entityRequestPath() {
        var groups = ENTITY_GROUP_ORDER.filter(function (group) {
            return layerSettings[group] === true;
        });
        if (layerSettings.portalNetwork && groups.indexOf("portal") === -1) {
            groups.push("portal");
        }
        return groups.length > 0
            ? "/api/entities?groups=" + encodeURIComponent(groups.join(","))
            : "/api/entities";
    }

    async function pollEntities() {
        if (!map || !hasLiveAccess() || document.hidden || pollCircuitOpen ||
            entityRequestPending ||
            entityAvailability === "unavailable") {
            return;
        }

        entityRequestPending = true;
        try {
            var response = await fetch(authorizedUrl(entityRequestPath()), {
                cache: "no-store",
                credentials: "same-origin"
            });
            if (response.status === 404) {
                recordPollSuccess("entities");
                entityAvailability = "unavailable";
                clearEntityLayers();
                setFeedState("entities", true);
                renderLayerRows();
                syncLayerVisibility();
                return;
            }
            if (!response.ok) {
                throw new Error("HTTP " + response.status);
            }

            var payload = await response.json();
            recordPollSuccess("entities");
            if (entityAvailability === "unavailable") {
                return;
            }
            var wasAvailable = entityAvailability === "available";
            entityAvailability = "available";
            feedLastUpdated.entities = Date.now();
            setFeedState("entities", true);
            var entities = normalizeEntityPayload(payload);
            var nextRevision = payload && payload.revision != null
                ? String(payload.revision)
                : "";
            var tweenDuration = entityPayloadTweenDuration(payload, nextRevision);
            updateEntityGroupMetadata(payload);
            recordEntityTrails(entities);
            latestEntities = entities;
            derivePortalPairs(latestEntities);
            updateLayerCounts();
            if (nextRevision !== entityRevision) {
                renderEntityPayload(entities, tweenDuration);
                entityRevision = nextRevision;
            } else {
                updateEntityMarkerRecords(entities);
            }
            if (!wasAvailable) {
                renderLayerRows();
            }
            syncLayerVisibility();
            renderTrails();
        } catch (error) {
            recordPollFailure("entities");
            setFeedState("entities", false);
        } finally {
            entityRequestPending = false;
            updateEntityPolling(false);
        }
    }

    function updateEntityFocusPolling(immediate) {
        window.clearTimeout(entityFocusPollTimer);
        entityFocusPollTimer = 0;
        if (!map || !hasLiveAccess() || document.hidden || pollCircuitOpen ||
            entityAvailability === "unavailable" ||
            entityFocusRequestPending || !followTarget ||
            (followTarget.kind !== "ship" && followTarget.kind !== "cart")) {
            return;
        }

        var record = entityMarkerRecords.get(followTarget.id);
        if (!record || !record.entity.id) {
            return;
        }

        entityFocusPollTimer = window.setTimeout(
            pollEntityFocus,
            immediate ? 0 : POLL_INTERVAL_MS
        );
    }

    async function pollEntityFocus() {
        if (document.hidden || pollCircuitOpen || !followTarget ||
            (followTarget.kind !== "ship" && followTarget.kind !== "cart")) {
            return;
        }

        var targetKey = followTarget.id;
        var record = entityMarkerRecords.get(targetKey);
        if (!record || !record.entity.id || entityFocusRequestPending) {
            return;
        }

        entityFocusRequestPending = true;
        try {
            var payload = await fetchJson(
                "/api/entities?focus=" + encodeURIComponent(record.entity.id)
            );
            recordPollSuccess("entity-focus");
            if (!isFollowing(record.entity.group, targetKey) ||
                !payload || !payload.focus || payload.focus.found !== true) {
                return;
            }

            var entities = normalizeEntityPayload({ entities: [payload.focus] });
            if (entities.length !== 1) {
                return;
            }

            var entity = entities[0];
            entity.trailKey = targetKey;
            record.entity = entity;
            var target = worldToLatLng(entity.x, entity.z);
            var tweenDuration = entityFocusPayloadTweenDuration(payload);
            var timestamp = Number(payload.focus.unixMs);
            appendTrailSample(
                targetKey,
                entity.group,
                entity.x,
                entity.z,
                Number.isFinite(timestamp) ? timestamp : Date.now()
            );
            tweenEntityMarker(record, target, tweenDuration);
            for (var index = 0; index < latestEntities.length; index++) {
                if (latestEntities[index].id === entity.id) {
                    latestEntities[index] = entity;
                    break;
                }
            }
            feedLastUpdated.entities = Date.now();
            setFeedState("entities", true);
            renderTrails();
            refreshOpenPopupContent();
        } catch (error) {
            recordPollFailure("entity-focus");
            setFeedState("entities", false);
        } finally {
            entityFocusRequestPending = false;
            updateEntityFocusPolling(false);
        }
    }

    function ensureEntityFeed() {
        if (!map || !hasLiveAccess() || entityAvailability === "unavailable") {
            return;
        }

        if (entityAvailability === "unknown" && !entityRequestPending) {
            pollEntities();
            return;
        }
        updateEntityPolling(true);
    }

    function normalizeRaidEvent(value) {
        if (!hasLiveAccess() || !value ||
            !Number.isFinite(Number(value.x)) || !Number.isFinite(Number(value.z)) ||
            !Number.isFinite(Number(value.radius)) || Number(value.radius) <= 0) {
            return null;
        }

        return {
            duration: Math.max(0, Number(value.duration) || 0),
            elapsed: Math.max(0, Number(value.elapsed) || 0),
            name: typeof value.name === "string" && value.name.trim() ? value.name.trim() : "Event",
            radius: Number(value.radius),
            x: Number(value.x),
            z: Number(value.z)
        };
    }

    function applyRaidEvent(value) {
        var previousEvent = currentRaidEvent;
        var hadRaid = Boolean(previousEvent);
        var nextEvent = normalizeRaidEvent(value);
        if (nextEvent) {
            var sameEvent = previousEvent && previousEvent.name === nextEvent.name &&
                Math.round(previousEvent.x) === Math.round(nextEvent.x) &&
                Math.round(previousEvent.z) === Math.round(nextEvent.z) &&
                nextEvent.elapsed + 2 >= previousEvent.elapsed;
            nextEvent.id = sameEvent
                ? previousEvent.id
                : "raid:" + nextCinemaRaidId++ + ":" +
                    nextEvent.name.toLocaleLowerCase().replace(/\s+/g, "-");
        }
        currentRaidEvent = nextEvent;
        elements.raidBadge.hidden = !currentRaidEvent;
        elements.raidBadge.textContent = currentRaidEvent ? "Raid: " + currentRaidEvent.name : "";

        if (!map || !currentRaidEvent) {
            if (raidCircle && map) {
                map.removeLayer(raidCircle);
            }
            raidCircle = null;
            if (hadRaid) {
                renderLayerRows();
            }
            renderLegend();
            updateLayerCounts();
            syncCinemaRaid(previousEvent, currentRaidEvent);
            return;
        }

        var center = worldToLatLng(currentRaidEvent.x, currentRaidEvent.z);
        var radius = worldDistanceToMap(currentRaidEvent.radius);
        if (!raidCircle) {
            var raidColor = window.getComputedStyle(styleRoot)
                .getPropertyValue("--raid").trim() || "#c96a52";
            raidCircle = L.circle(center, {
                className: "raid-ring",
                color: raidColor,
                fillColor: raidColor,
                fillOpacity: 0.22,
                interactive: true,
                opacity: 0.78,
                radius: radius,
                weight: 2
            }).addTo(map);
            bindMapPopup(raidCircle, buildRaidPopup, { kind: "raid" });
        } else {
            raidCircle.setLatLng(center);
            raidCircle.setRadius(radius);
        }
        if (!hadRaid) {
            renderLayerRows();
        }
        renderLegend();
        updateLayerCounts();
        syncCinemaRaid(previousEvent, currentRaidEvent);
    }

    function createPinTooltip(pin) {
        var tooltip = document.createElement("span");
        var name = document.createElement("span");
        var pinName = typeof pin.name === "string" && pin.name.trim() ? pin.name.trim() : "Pin";
        var author = typeof pin.author === "string" ? pin.author.trim() : "";

        if (pin.checked) {
            var check = document.createElement("span");
            check.className = "pin-tooltip-check";
            check.textContent = "✓ ";
            tooltip.appendChild(check);
        }

        name.className = "pin-tooltip-name";
        name.classList.toggle("is-checked", pin.checked);
        name.textContent = pinName;
        tooltip.appendChild(name);
        if (author) {
            tooltip.appendChild(document.createTextNode(" — " + author));
        }

        return tooltip;
    }

    function webPinById(id) {
        return latestWebPins.find(function (pin) {
            return pin.id === id;
        }) || null;
    }

    function normalizeWebPin(pin) {
        if (!pin || typeof pin.id !== "string" || !pin.id ||
            !Number.isFinite(Number(pin.x)) || !Number.isFinite(Number(pin.z))) {
            return null;
        }
        var icon = typeof pin.icon === "string" ? pin.icon.trim() : "";
        if (WEB_PIN_ICONS.indexOf(icon) === -1) {
            icon = "pin";
        }
        return {
            author: typeof pin.author === "string" ? pin.author.trim() : "",
            checked: pin.checked === true,
            createdUnixMs: Number(pin.createdUnixMs) || 0,
            icon: icon,
            id: pin.id,
            label: typeof pin.label === "string" ? pin.label.trim() : "",
            updatedUnixMs: Number(pin.updatedUnixMs) || 0,
            x: Number(pin.x),
            z: Number(pin.z)
        };
    }

    function createWebPinTooltip(pin) {
        var tooltip = document.createElement("span");
        var name = document.createElement("span");
        var author = document.createElement("span");
        tooltip.className = "webpin-tooltip-content" +
            (pin.checked ? " webpin-checked" : "");
        name.className = "webpin-tooltip-name";
        name.textContent = pin.label || "Web pin";
        author.className = "webpin-tooltip-author";
        author.textContent = "Charted by " + pin.author;
        tooltip.appendChild(name);
        tooltip.appendChild(document.createTextNode(" "));
        tooltip.appendChild(author);
        return tooltip;
    }

    function renderWebPins() {
        if (!webPinLayer) {
            return;
        }

        webPinLayer.clearLayers();
        if (!webPinsAvailable) {
            return;
        }
        latestWebPins.forEach(function (pin) {
            var editable = canEditWebPin(pin);
            var markerMarkup = iconMarkup(pin.icon, "✦");
            var icon = L.divIcon({
                className: "webpin-div-icon" +
                    (pin.checked ? " is-checked webpin-checked" : ""),
                html: '<span class="webpin-marker-shell" aria-hidden="true">' +
                    '<span class="webpin-marker-glyph">' + markerMarkup + "</span></span>",
                iconAnchor: [12, 12],
                iconSize: [24, 24]
            });
            var latLng = worldToLatLng(pin.x, pin.z);
            var marker = L.marker(latLng, {
                draggable: editable,
                icon: icon,
                opacity: pin.checked ? 0.55 : 1,
                title: pin.label || "Web pin"
            });
            bindMarkerTooltip(marker, createWebPinTooltip(pin), {
                fallbackGlyph: "✦",
                iconKey: pin.icon,
                title: pin.label || "Web pin"
            }, {
                className: "map-tooltip webpin-tooltip" +
                    (pin.checked ? " webpin-checked" : ""),
                direction: "top",
                offset: [0, -12],
                opacity: 1
            });
            bindMapPopup(marker, function () {
                return buildWebPinPopup(pin);
            }, { kind: "webpin" });
            if (editable) {
                marker.on("dragstart", function () {
                    marker._voWebPinOriginalLatLng = L.latLng(marker.getLatLng());
                });
                marker.on("dragend", async function () {
                    var originalLatLng = marker._voWebPinOriginalLatLng || latLng;
                    var world = latLngToWorld(marker.getLatLng());
                    if (!world) {
                        marker.setLatLng(originalLatLng);
                        return;
                    }
                    if (marker.dragging) {
                        marker.dragging.disable();
                    }
                    try {
                        await fetchJson(
                            "/api/webpins/" + encodeURIComponent(pin.id),
                            webPinWriteOptions("PATCH", {
                                x: world.x,
                                z: world.z
                            }, webPinOperatorAuthor())
                        );
                        requestWebPinsFetch();
                    } catch (error) {
                        marker.setLatLng(originalLatLng);
                        showNoticeToast("Pin move failed: " +
                            (error && error.message ? error.message : "request failed"));
                    } finally {
                        if (marker._map && marker.dragging && canEditWebPin(pin)) {
                            marker.dragging.enable();
                        }
                    }
                });
            }
            marker.addTo(webPinLayer);
            pin.latLng = marker.getLatLng();
            pin.marker = marker;
        });
    }

    async function updateWebPinChecked(id, button) {
        var pin = webPinById(id);
        if (!pin || !canEditWebPin(pin)) {
            return;
        }
        button.disabled = true;
        try {
            await fetchJson(
                "/api/webpins/" + encodeURIComponent(pin.id),
                webPinWriteOptions("PATCH", { checked: !pin.checked }, webPinOperatorAuthor())
            );
            requestWebPinsFetch();
        } catch (error) {
            button.disabled = false;
            showNoticeToast("Pin update failed: " +
                (error && error.message ? error.message : "request failed"));
        }
    }

    async function deleteWebPin(id, button) {
        var pin = webPinById(id);
        if (!pin || !canEditWebPin(pin)) {
            return;
        }
        button.disabled = true;
        try {
            await fetchJson(
                "/api/webpins/" + encodeURIComponent(pin.id),
                webPinWriteOptions("DELETE", null, webPinOperatorAuthor())
            );
            if (map && map._popup) {
                map.closePopup();
            }
            requestWebPinsFetch();
        } catch (error) {
            button.disabled = false;
            button.dataset.confirming = "false";
            button.textContent = "Delete";
            button.classList.remove("is-confirming");
            showNoticeToast("Pin delete failed: " +
                (error && error.message ? error.message : "request failed"));
        }
    }

    async function fetchWebPins() {
        if (document.hidden || pollCircuitOpen) {
            return;
        }
        if (webPinsFetchPending) {
            webPinsFetchQueued = true;
            return;
        }

        webPinsFetchPending = true;
        var requestView = currentView;
        var wasAvailable = webPinsAvailable;
        try {
            var payload = await fetchJson("/api/webpins");
            if (requestView !== currentView) {
                recordPollSuccess("webpins");
                webPinsFetchQueued = true;
                return;
            }
            if (!payload || !Array.isArray(payload.pins) ||
                !Number.isFinite(Number(payload.revision))) {
                throw new Error("Invalid web pin response");
            }
            recordPollSuccess("webpins");
            var nextPins = [];
            payload.pins.forEach(function (pin) {
                var normalized = normalizeWebPin(pin);
                if (normalized) {
                    nextPins.push(normalized);
                }
            });
            latestWebPins = nextPins;
            webPinsRevision = Number(payload.revision);
            webPinsSharedEditing = payload.sharedEditing === true;
            webPinsAvailable = true;
            feedLastUpdated.webpins = Date.now();
            setFeedState("webpins", true);
            renderWebPins();
            syncWebPinControl();
            syncLayerVisibility();
            if (!wasAvailable) {
                renderLayerRows();
            } else {
                updateLayerCounts();
            }
        } catch (error) {
            if (requestView !== currentView) {
                webPinsFetchQueued = true;
                return;
            }
            if (error && (error.status === 401 || error.status === 403)) {
                recordPollSuccess("webpins");
                latestWebPins = [];
                webPinsRevision = null;
                webPinsAvailable = false;
                webPinsSharedEditing = false;
                setFeedState("webpins", true);
                renderWebPins();
                syncWebPinControl();
                syncLayerVisibility();
                if (wasAvailable) {
                    renderLayerRows();
                }
            } else {
                recordPollFailure("webpins");
                setFeedState("webpins", false);
            }
        } finally {
            webPinsProbed = true;
            webPinsFetchPending = false;
            if (webPinsFetchQueued) {
                webPinsFetchQueued = false;
                fetchWebPins();
            }
        }
    }

    function requestWebPinsFetch() {
        if (webPinsFetchPending) {
            webPinsFetchQueued = true;
            return;
        }
        fetchWebPins();
    }

    function handleWebPinRevisionPayload(payload) {
        var revision = payload ? Number(payload.revision) : NaN;
        if (!Number.isFinite(revision)) {
            throw new Error("Invalid web pin event");
        }
        if (webPinsRevision === null || revision !== webPinsRevision) {
            requestWebPinsFetch();
        }
    }

    async function pollWebPins() {
        if (!map || !webPinLayer || document.hidden || pollCircuitOpen ||
            (eventSourceOpen && webPinsProbed)) {
            return;
        }
        requestWebPinsFetch();
    }

    function startWebPinsPolling() {
        if (webPinsPollingStarted) {
            return;
        }
        webPinsPollingStarted = true;
        startPolling(pollWebPins, PINS_POLL_INTERVAL_MS);
    }

    function createPinMarker(pin) {
        var isChecked = pin.checked === true;
        var icon = L.divIcon({
            className: "pin-div-icon" + (isChecked ? " is-checked" : ""),
            html: '<span class="pin-marker-shell"><span class="pin-marker-glyph">' +
                iconMarkup(isChecked ? "pin_checked" : "pin", isChecked ? "✓" : "•") +
                "</span></span>",
            iconAnchor: [10, 19],
            iconSize: [20, 20]
        });
        var marker = L.marker(pin.latLng, {
            icon: icon,
            title: pin.name
        });
        bindMarkerTooltip(marker, createPinTooltip(pin), {
            fallbackGlyph: isChecked ? "✓" : "•",
            iconKey: isChecked ? "pin_checked" : "pin",
            title: pin.name
        }, {
            className: "map-tooltip pin-tooltip",
            direction: "top",
            offset: [0, -17],
            opacity: 1
        });
        bindMapPopup(marker, function () {
            return buildPinPopup(pin);
        }, { kind: "pin" });
        pin.marker = marker;
        return marker;
    }

    function createPinClusterMarker(bucket) {
        var allChecked = bucket.checkedWeight === bucket.weight;
        var count = bucket.count;
        var label = count + " cartography " + (count === 1 ? "pin" : "pins");
        var iconKey = allChecked ? "pin_checked" : "pin";
        var icon = L.divIcon({
            className: "pin-div-icon pin-cluster-icon" +
                (allChecked ? " is-checked" : ""),
            html: '<span class="pin-cluster-shell" aria-hidden="true">' +
                '<span class="pin-cluster-mark">' +
                iconMarkup(iconKey, allChecked ? "✓" : "•") +
                '</span><strong>' + count + "</strong></span>",
            iconAnchor: [24, 12],
            iconSize: [48, 24]
        });
        var marker = L.marker(clusterBucketCenter(bucket), {
            icon: icon,
            opacity: 1 - (0.22 * bucket.checkedWeight / bucket.weight),
            title: label
        });
        var tooltipContent = document.createElement("span");
        tooltipContent.textContent = label;
        bindMarkerTooltip(marker, tooltipContent, {
            fallbackGlyph: allChecked ? "✓" : "•",
            iconKey: iconKey,
            title: label
        }, {
            className: "map-tooltip pin-tooltip",
            direction: "top",
            offset: [0, -11],
            opacity: 1
        });
        return bindClusterZoom(marker);
    }

    function renderPins() {
        if (!map || !pinLayer) {
            return;
        }

        pinLayer.clearLayers();
        latestPins.forEach(function (pin) {
            pin.marker = null;
        });
        if (map.getZoom() >= OVERVIEW_CLUSTER_ZOOM) {
            latestPins.forEach(function (pin) {
                createPinMarker(pin).addTo(pinLayer);
            });
            return;
        }

        var buckets = bucketRecordsOnGrid(latestPins, null, function (bucket, pin) {
            bucket.checkedWeight = (bucket.checkedWeight || 0) + (pin.checked ? 1 : 0);
        });
        buckets.forEach(function (bucket) {
            createPinClusterMarker(bucket).addTo(pinLayer);
        });
    }

    async function pollPins() {
        if (!map || !pinLayer || document.hidden || pollCircuitOpen) {
            return;
        }

        try {
            var payload = await fetchJson("/api/pins");
            var pins = payload && Array.isArray(payload.pins) ? payload.pins : [];
            var nextPins = [];
            var pinsWereLoaded = feedLastUpdated.pins > 0;
            pins.forEach(function (pin) {
                if (!pin || !Number.isFinite(Number(pin.x)) || !Number.isFinite(Number(pin.z))) {
                    return;
                }

                var pinRecord = {
                    author: typeof pin.author === "string" ? pin.author.trim() : "",
                    checked: pin.checked === true,
                    latLng: worldToLatLng(Number(pin.x), Number(pin.z)),
                    name: typeof pin.name === "string" && pin.name.trim() ? pin.name.trim() : "Pin",
                    x: Number(pin.x),
                    z: Number(pin.z)
                };
                nextPins.push(pinRecord);
            });
            latestPins = nextPins;
            recordPollSuccess("pins");
            renderPins();
            feedLastUpdated.pins = Date.now();
            setFeedState("pins", true);
            if (pinsWereLoaded) {
                updateLayerCounts();
            } else {
                renderLayerRows();
            }
        } catch (error) {
            recordPollFailure("pins");
            setFeedState("pins", false);
        }
    }

    function startPinsPolling() {
        if (pinsPollingStarted) {
            return;
        }

        pinsPollingStarted = true;
        startPolling(pollPins, PINS_POLL_INTERVAL_MS);
    }

    function setSagaCollapsed(isCollapsed) {
        elements.sagaPanel.classList.toggle("is-collapsed", isCollapsed);
        elements.sagaContent.hidden = isCollapsed;
        elements.sagaToggle.setAttribute("aria-expanded", String(!isCollapsed));
        if (!isCollapsed) {
            renderSagaRelativeTimes();
        }
    }

    function setChatCollapsed(isCollapsed) {
        elements.chatPanel.classList.toggle("is-collapsed", isCollapsed);
        elements.chatContent.hidden = isCollapsed;
        elements.chatToggle.setAttribute("aria-expanded", String(!isCollapsed));
        if (!isCollapsed) {
            renderChatHistory();
        }
    }

    function leaderboardIsExpanded() {
        return hasLiveAccess() && !elements.leaderboardPanel.hidden &&
            !elements.leaderboardPanel.classList.contains("is-collapsed");
    }

    function humanizeLeaderboardPlaytime(seconds) {
        var totalMinutes = Math.max(0, Math.floor(Number(seconds) / 60));
        var hours = Math.floor(totalMinutes / 60);
        var minutes = totalMinutes % 60;
        return hours > 0 ? hours + "h " + minutes + "m" : minutes + "m";
    }

    function renderLeaderboard() {
        elements.leaderboardList.textContent = "";
        var note = "";
        if (!leaderboardLoaded) {
            note = "Reading the runes…";
        } else if (leaderboardLoadFailed) {
            note = "Leaderboard unavailable";
        } else if (leaderboardPlayers.length === 0) {
            note = "No sagas recorded this wipe";
        }

        elements.leaderboardNote.hidden = !note;
        elements.leaderboardNote.textContent = note;
        elements.leaderboardTable.hidden = leaderboardPlayers.length === 0;
        leaderboardPlayers.forEach(function (player, index) {
            var item = document.createElement("li");
            var rank = document.createElement("span");
            var name = document.createElement("span");
            var playtime = document.createElement("span");
            var deaths = document.createElement("span");
            var distance = document.createElement("span");
            item.className = "leaderboard-entry" + (player.online ? " is-online" : "");
            rank.className = "leaderboard-rank";
            rank.textContent = String(index + 1);
            name.className = "leaderboard-name";
            name.textContent = player.name;
            name.title = player.name;
            playtime.className = "leaderboard-stat";
            playtime.textContent = humanizeLeaderboardPlaytime(player.playSeconds);
            deaths.className = "leaderboard-stat";
            deaths.textContent = String(player.deaths);
            distance.className = "leaderboard-stat";
            distance.textContent = (player.distanceMeters / 1000).toFixed(1) + " km";
            item.appendChild(rank);
            item.appendChild(name);
            item.appendChild(playtime);
            item.appendChild(deaths);
            item.appendChild(distance);
            elements.leaderboardList.appendChild(item);
        });
    }

    function normalizeLeaderboardPlayer(player) {
        if (!player || typeof player !== "object" ||
            typeof player.name !== "string" || !player.name.trim()) {
            return null;
        }

        var playSeconds = Number(player.playSeconds);
        var deaths = Number(player.deaths);
        var distanceMeters = Number(player.distanceMeters);
        if (!Number.isFinite(playSeconds) || playSeconds < 0 ||
            !Number.isFinite(deaths) || deaths < 0 ||
            !Number.isFinite(distanceMeters) || distanceMeters < 0) {
            return null;
        }

        return {
            deaths: Math.floor(deaths),
            distanceMeters: distanceMeters,
            name: player.name.trim(),
            online: player.online === true,
            playSeconds: Math.floor(playSeconds)
        };
    }

    function handleLeaderboardPayload(payload) {
        if (!payload || typeof payload !== "object" || !Array.isArray(payload.players)) {
            throw new Error("Invalid leaderboard payload");
        }

        leaderboardPlayers = [];
        payload.players.slice(0, 50).forEach(function (player) {
            var normalized = normalizeLeaderboardPlayer(player);
            if (normalized) {
                leaderboardPlayers.push(normalized);
            }
        });
        leaderboardLoaded = true;
        leaderboardLoadFailed = false;
        renderLeaderboard();
    }

    async function loadLeaderboard() {
        if (!leaderboardIsExpanded() || leaderboardRequestPending ||
            document.hidden || pollCircuitOpen) {
            return;
        }

        leaderboardRequestPending = true;
        var sequence = ++leaderboardRequestSequence;
        if (!leaderboardLoaded) {
            renderLeaderboard();
        }
        try {
            var payload = await fetchJson("/api/leaderboard");
            if (sequence !== leaderboardRequestSequence || !leaderboardIsExpanded()) {
                recordPollSuccess("leaderboard");
                return;
            }
            handleLeaderboardPayload(payload);
            recordPollSuccess("leaderboard");
        } catch (error) {
            recordPollFailure("leaderboard");
            if (sequence !== leaderboardRequestSequence || !leaderboardIsExpanded()) {
                return;
            }
            leaderboardLoaded = true;
            leaderboardLoadFailed = true;
            renderLeaderboard();
        } finally {
            if (sequence === leaderboardRequestSequence) {
                leaderboardRequestPending = false;
            }
        }
    }

    function clearLeaderboardPoll() {
        window.clearTimeout(leaderboardPollTimer);
        leaderboardPollTimer = 0;
    }

    function scheduleLeaderboardPoll(delay) {
        clearLeaderboardPoll();
        if (!leaderboardIsExpanded() || document.hidden || pollCircuitOpen) {
            return;
        }

        leaderboardPollTimer = window.setTimeout(async function () {
            leaderboardPollTimer = 0;
            await loadLeaderboard();
            scheduleLeaderboardPoll(LEADERBOARD_POLL_INTERVAL_MS);
        }, Math.max(0, delay));
    }

    function setLeaderboardCollapsed(isCollapsed) {
        elements.leaderboardPanel.classList.toggle("is-collapsed", isCollapsed);
        elements.leaderboardContent.hidden = isCollapsed;
        elements.leaderboardToggle.setAttribute("aria-expanded", String(!isCollapsed));
        if (isCollapsed) {
            clearLeaderboardPoll();
        } else {
            renderLeaderboard();
            scheduleLeaderboardPoll(0);
        }
    }

    function clearLeaderboard() {
        leaderboardRequestSequence++;
        leaderboardRequestPending = false;
        leaderboardPlayers = [];
        leaderboardLoaded = false;
        leaderboardLoadFailed = false;
        clearLeaderboardPoll();
        setLeaderboardCollapsed(true);
        renderLeaderboard();
    }

    function sagaName(data) {
        if (data && typeof data.name === "string" && data.name.trim()) {
            return data.name.trim();
        }
        return "A viking";
    }

    function sagaPresentation(event) {
        var data = event.data || {};
        switch (event.type) {
            case "player.join":
                return {
                    className: "is-join",
                    fallbackGlyph: "✦",
                    iconKey: "player",
                    text: sagaName(data) + " arrived"
                };
            case "player.leave":
                return {
                    className: "is-leave",
                    fallbackGlyph: "↠",
                    iconKey: "ship",
                    text: sagaName(data) + " departed"
                };
            case "player.death":
                return {
                    className: "is-death",
                    fallbackGlyph: "☠",
                    iconKey: "tombstone",
                    text: sagaName(data) + " fell"
                };
            case "raid.start":
                return {
                    className: "is-raid-start",
                    fallbackGlyph: "⚔",
                    iconKey: "boss",
                    text: "Raid: " + sagaName(data) + " started"
                };
            case "raid.end":
                return {
                    className: "is-raid-end",
                    fallbackGlyph: "◇",
                    iconKey: "boss",
                    text: "Raid: " + sagaName(data) + " ended"
                };
            case "world.save":
                return {
                    className: "is-save",
                    fallbackGlyph: "✓",
                    iconKey: "saga_save",
                    text: "World saved"
                };
            case "day.change":
                var day = Number(data.day);
                return {
                    className: "is-day",
                    fallbackGlyph: "☀",
                    iconKey: "saga_day",
                    text: Number.isFinite(day)
                        ? "Day " + Math.floor(day) + " dawns"
                        : "A new day dawns"
                };
            case "chat":
                var speaker = sagaName(data);
                var message = typeof data.text === "string" ? data.text.trim() : "";
                if (!message) {
                    return null;
                }
                return {
                    className: "is-chat" + (data.shout === true ? " is-shout" : ""),
                    fallbackGlyph: data.shout === true ? "📯" : "❝",
                    iconKey: "saga_chat",
                    text: "“" + message + "” — " +
                        (data.shout === true ? speaker.toUpperCase() : speaker)
                };
            default:
                return null;
        }
    }

    function normalizeSagaEvent(event) {
        if (!event || typeof event !== "object" ||
            typeof event.type !== "string" || !event.type) {
            return null;
        }

        var id = Number(event.id);
        var unixMs = Number(event.unixMs);
        if (!Number.isFinite(id) || id <= 0 ||
            !Number.isFinite(unixMs) || unixMs <= 0 || unixMs > 8640000000000000) {
            return null;
        }

        var normalized = {
            data: event.data && typeof event.data === "object" ? event.data : {},
            id: Math.floor(id),
            type: event.type,
            unixMs: Math.floor(unixMs)
        };
        return sagaPresentation(normalized) ? normalized : null;
    }

    function sagaRelativeTime(unixMs) {
        var elapsedSeconds = Math.max(0, Math.floor((Date.now() - unixMs) / 1000));
        if (elapsedSeconds < 10) {
            return "just now";
        }
        if (elapsedSeconds < 60) {
            return elapsedSeconds + "s ago";
        }

        var elapsedMinutes = Math.floor(elapsedSeconds / 60);
        if (elapsedMinutes < 60) {
            return elapsedMinutes + "m ago";
        }

        var elapsedHours = Math.floor(elapsedMinutes / 60);
        if (elapsedHours < 24) {
            return elapsedHours + "h ago";
        }

        var elapsedDays = Math.floor(elapsedHours / 24);
        return elapsedDays + "d ago";
    }

    function renderSagaRelativeTimes() {
        elements.sagaList.querySelectorAll("time[data-unix-ms]").forEach(function (time) {
            time.textContent = sagaRelativeTime(Number(time.dataset.unixMs));
        });
    }

    function renderSagaFeed() {
        elements.sagaList.textContent = "";
        var feedEvents = sagaEvents.concat(sagaChatEvents).sort(function (left, right) {
            return right.unixMs - left.unixMs;
        }).slice(0, SAGA_EVENT_LIMIT);
        var note = "";
        if (feedEvents.length > 0) {
            note = "";
        } else if (sagaEnabled === false) {
            note = "Activity log disabled";
        } else if (!sagaLoaded) {
            note = "Reading the runes…";
        } else if (sagaLoadFailed) {
            note = "Server events unavailable";
        } else {
            note = "No events recorded yet";
        }

        elements.sagaNote.hidden = !note;
        elements.sagaNote.textContent = note;
        if (note && feedEvents.length === 0) {
            return;
        }

        feedEvents.forEach(function (event) {
            var presentation = sagaPresentation(event);
            if (!presentation) {
                return;
            }

            var item = document.createElement("li");
            var icon = document.createElement("span");
            var copy = document.createElement("span");
            var text = document.createElement("span");
            var time = document.createElement("time");
            item.className = "saga-entry " + presentation.className;
            item.dataset.eventId = String(event.id);
            icon.className = "saga-entry-icon";
            icon.setAttribute("aria-hidden", "true");
            icon.innerHTML = iconMarkup(presentation.iconKey, presentation.fallbackGlyph);
            copy.className = "saga-entry-copy";
            text.className = "saga-entry-text";
            text.textContent = presentation.text;
            time.className = "saga-entry-time";
            time.dataset.unixMs = String(event.unixMs);
            time.dateTime = new Date(event.unixMs).toISOString();
            time.textContent = sagaRelativeTime(event.unixMs);
            copy.appendChild(text);
            copy.appendChild(time);
            item.appendChild(icon);
            item.appendChild(copy);
            elements.sagaList.appendChild(item);
        });
    }

    function handleActivityPayload(payload, replace) {
        if (!payload || typeof payload !== "object" || !Array.isArray(payload.events)) {
            throw new Error("Invalid activity payload");
        }

        sagaLoaded = true;
        sagaLoadFailed = false;
        sagaEnabled = payload.enabled !== false;
        var nextCursor = Number(payload.cursor);
        nextCursor = Number.isFinite(nextCursor) ? Math.max(0, Math.floor(nextCursor)) : sagaCursor;
        if (!sagaEnabled) {
            sagaEvents = [];
            sagaCursor = nextCursor;
            renderSagaFeed();
            return;
        }

        var merged = new Map();
        if (!replace) {
            sagaEvents.forEach(function (event) {
                merged.set(event.id, event);
            });
        }
        payload.events.forEach(function (event) {
            var normalized = normalizeSagaEvent(event);
            if (normalized) {
                merged.set(normalized.id, normalized);
            }
        });
        sagaEvents = Array.from(merged.values()).sort(function (left, right) {
            return right.id - left.id;
        }).slice(0, SAGA_EVENT_LIMIT);
        sagaCursor = replace ? nextCursor : Math.max(sagaCursor, nextCursor);
        renderSagaFeed();
    }

    function flushPendingSagaPayloads() {
        var pending = pendingSagaPayloads;
        pendingSagaPayloads = [];
        pending.forEach(function (payload) {
            handleActivityPayload(payload, false);
        });
    }

    function handleActivityStreamPayload(payload) {
        if (currentView === "public") {
            return;
        }
        if (sagaRequestPending) {
            pendingSagaPayloads.push(payload);
            if (pendingSagaPayloads.length > 10) {
                pendingSagaPayloads.shift();
            }
            return;
        }
        handleActivityPayload(payload, false);
        recordPollSuccess("saga");
    }

    async function loadSagaActivity(cursor, replace) {
        if (sagaRequestPending || currentView === "public" ||
            document.hidden || pollCircuitOpen) {
            return;
        }

        sagaRequestPending = true;
        var sequence = ++sagaRequestSequence;
        if (!sagaLoaded) {
            renderSagaFeed();
        }
        try {
            var payload = await fetchJson("/api/activity?cursor=" + encodeURIComponent(cursor));
            if (sequence !== sagaRequestSequence || currentView === "public") {
                recordPollSuccess("saga");
                return;
            }
            handleActivityPayload(payload, replace);
            recordPollSuccess("saga");
            flushPendingSagaPayloads();
        } catch (error) {
            recordPollFailure("saga");
            if (sequence !== sagaRequestSequence || currentView === "public") {
                return;
            }
            sagaLoaded = true;
            sagaLoadFailed = true;
            renderSagaFeed();
            flushPendingSagaPayloads();
        } finally {
            if (sequence === sagaRequestSequence) {
                sagaRequestPending = false;
            }
        }
    }

    function ensureSagaActivity() {
        if ((currentView === "admin" || currentView === "shared") && !sagaLoaded) {
            loadSagaActivity(0, true);
        }
    }

    async function pollSagaActivity() {
        if (eventSourceOpen || currentView === "public" || sagaRequestPending ||
            document.hidden || pollCircuitOpen) {
            return;
        }
        if (!sagaLoaded) {
            ensureSagaActivity();
            return;
        }

        var replace = sagaEnabled === false;
        await loadSagaActivity(replace ? 0 : sagaCursor, replace);
    }

    function clearSagaActivity() {
        sagaRequestSequence++;
        sagaRequestPending = false;
        sagaEvents = [];
        sagaChatEvents = [];
        chatHistory = [];
        chatHistoryRequestSequence++;
        chatHistoryRequested = false;
        chatSequences.clear();
        liveChatSequences.clear();
        sagaCursor = 0;
        sagaEnabled = null;
        sagaLoaded = false;
        sagaLoadFailed = false;
        pendingSagaPayloads = [];
        clearChatBubbles();
        setChatCollapsed(true);
        renderChatHistory();
        setSagaCollapsed(true);
        renderSagaFeed();
    }

    function updateView(view) {
        var nextView = view === "admin" || view === "shared" ? view : "public";
        if (nextView !== "admin") {
            disarmShipTow();
        }
        elements.publicViewBadge.hidden = nextView === "admin";
        elements.publicViewBadge.textContent = nextView === "shared"
            ? "Shared view"
            : "Public view";
        elements.watchButton.hidden = nextView === "public";
        elements.chatPanel.hidden = nextView === "public";
        elements.chatForm.hidden = nextView !== "admin";
        elements.sagaPanel.hidden = nextView === "public";
        elements.leaderboardPanel.hidden = nextView === "public";
        if (nextView !== "admin") {
            setChatSendNotice("");
        }
        if (nextView === currentView) {
            ensureSagaActivity();
            ensureChatHistory();
            return;
        }

        closeDungeonInterior(false);
        resetDungeonRegistry();
        dungeonDetailCache.clear();
        currentView = nextView;
        latestWebPins = [];
        webPinsRevision = null;
        webPinsAvailable = false;
        webPinsSharedEditing = false;
        webPinsProbed = false;
        feedLastUpdated.webpins = 0;
        setFeedState("webpins", true);
        closeWebPinDialog();
        renderWebPins();
        syncWebPinControl();
        if (currentView === "public") {
            clearSagaActivity();
            clearLeaderboard();
        } else {
            ensureSagaActivity();
            ensureChatHistory();
            if (leaderboardIsExpanded()) {
                scheduleLeaderboardPoll(0);
            }
        }
        dismissMapContextMenu();
        if (currentView === "public" && cinemaState) {
            exitCinema();
        }
        syncMapPingControl();
        if (map) {
            loadPoisForCurrentView();
            renderLayerRows();
            syncLayerVisibility();
            probeTimelapseAvailability();
            requestWebPinsFetch();
            if (hasLiveAccess()) {
                ensureEntityFeed();
            } else {
                window.clearTimeout(entityPollTimer);
                entityPollTimer = 0;
                if (followTarget && followTarget.kind !== "player") {
                    clearFollow();
                }
                setFeedState("entities", true);
                applyRaidEvent(null);
            }
        }
        tryBootCinemaFromHash();
    }

    function updateFogStatus(status) {
        var mode = status && typeof status.mode === "string" ? status.mode.toLowerCase() : "off";
        var revisionNumber = status ? Number(status.revision) : 0;
        var sizeNumber = status ? Number(status.size) : 0;
        fogStatus = {
            mode: mode,
            revision: Number.isFinite(revisionNumber) ? String(Math.max(0, Math.floor(revisionNumber))) : "0",
            size: Number.isFinite(sizeNumber) ? Math.max(0, Math.floor(sizeNumber)) : 0
        };

        var wasAvailable = fogAvailable;
        fogAvailable = fogStatus.mode !== "off" && !hasLiveAccess();
        if (wasAvailable !== fogAvailable) {
            renderLayerRows();
        }
        applyFogStatus();
    }

    function fogMapStyle() {
        return displayedMapStyle === "chart" ? "chart" : "default";
    }

    function fogCacheKey(revision) {
        return fogMapStyle() + "|" + revision;
    }

    function fogUrl(revision) {
        if (fogMapStyle() === "chart") {
            return authorizedUrl("/fog.png?style=chart&rev=" + encodeURIComponent(revision));
        }
        return authorizedUrl("/fog.png?rev=" + encodeURIComponent(revision));
    }

    function showFogCover() {
        if (fogCoverElement || !elements.mapPane) {
            return;
        }

        var cover = document.createElement("div");
        cover.className = "map-cover";
        elements.mapPane.appendChild(cover);
        fogCoverElement = cover;
        // Never brick the map if fog.png cannot load: reveal after a grace period.
        fogCoverTimer = window.setTimeout(hideFogCover, 8000);
    }

    function hideFogCover() {
        window.clearTimeout(fogCoverTimer);
        fogCoverTimer = 0;
        var cover = fogCoverElement;
        if (!cover) {
            return;
        }

        fogCoverElement = null;
        cover.classList.add("is-hidden");
        window.setTimeout(function () {
            if (cover.parentNode) {
                cover.parentNode.removeChild(cover);
            }
        }, 320);
    }

    function applyFogStatus() {
        if (!map || !worldBounds) {
            return;
        }

        if (!fogAvailable) {
            fogLoadSequence++;
            fogRequestedRevision = null;
            fogDisplayedRevision = null;
            if (fogOverlay) {
                setLayerVisible(fogOverlay, false);
                fogOverlay = null;
            }
            hideFogCover();
            syncLayerVisibility();
            return;
        }

        var revision = fogStatus.revision;
        var cacheKey = fogCacheKey(revision);
        var url = fogUrl(revision);
        if (!fogOverlay) {
            if (cacheKey === fogRequestedRevision) {
                syncLayerVisibility();
                return;
            }

            // Preload the first fog image before creating the overlay so the
            // cover only lifts once the fogged view is actually renderable.
            fogRequestedRevision = cacheKey;
            var initialSequence = ++fogLoadSequence;
            var initialImage = new window.Image();
            initialImage.onload = function () {
                if (initialSequence !== fogLoadSequence || !fogAvailable || fogOverlay ||
                    cacheKey !== fogCacheKey(fogStatus.revision)) {
                    return;
                }

                fogOverlay = L.imageOverlay(url, worldBounds, {
                    className: "fog-overlay",
                    interactive: false,
                    opacity: 1,
                    pane: "fogPane"
                });
                fogDisplayedRevision = cacheKey;
                fogRequestedRevision = cacheKey;
                feedLastUpdated.fog = Date.now();
                syncLayerVisibility();
                hideFogCover();
            };
            initialImage.onerror = function () {
                if (initialSequence === fogLoadSequence && fogRequestedRevision === cacheKey) {
                    fogRequestedRevision = null;
                }
            };
            initialImage.src = url;
            return;
        }

        if (cacheKey === fogDisplayedRevision || cacheKey === fogRequestedRevision) {
            syncLayerVisibility();
            return;
        }

        fogRequestedRevision = cacheKey;
        var loadSequence = ++fogLoadSequence;
        var image = new window.Image();
        image.onload = function () {
            if (loadSequence !== fogLoadSequence || !fogAvailable ||
                cacheKey !== fogCacheKey(fogStatus.revision) || !fogOverlay) {
                return;
            }

            fogOverlay.setUrl(url);
            fogDisplayedRevision = cacheKey;
            fogRequestedRevision = cacheKey;
            feedLastUpdated.fog = Date.now();
            syncLayerVisibility();
        };
        image.onerror = function () {
            if (loadSequence === fogLoadSequence && fogRequestedRevision === cacheKey) {
                fogRequestedRevision = null;
            }
        };
        image.src = url;
    }

    function handleStatusPayload(status) {
        if (!status || typeof status !== "object") {
            throw new Error("Invalid status payload");
        }

        handlePluginVersion(status.pluginVersion);
        feedLastUpdated.status = Date.now();
        latestStatusSnapshotStale = status.stale === true;
        setFeedState("status", true);
        elements.serverName.textContent = textOrDash(status.serverName);
        elements.worldName.textContent = textOrDash(status.worldName);
        updateJoinCode(status.joinCode);
        updateMapMetricsFromStatus(status);
        renderWorldTime(status.day, status.timeOfDay);
        renderBossProgression(status.globalKeys);
        updateWorldMetrics(status);
        updateLastSaved(status.lastSavedUnixMs);
        renderPlayerCount(status.players);
        updateRenderRevision(status.map);
        updateView(status.view);
        updateEntityAvailability(status);
        updateConsoleAvailability(status);
        updateFogStatus(status.map && status.map.fog);
        ensureMap(status.map);
        reconcileMapStyle(status.map);
        updateRenderStatus(status.map);
        applyRaidEvent(status.event);
        renderCinemaHud();
        tryBootCinemaFromHash();
        recordPollSuccess("status");
    }

    function updateJoinCode(joinCode) {
        var normalized = typeof joinCode === "string" ? joinCode.trim() : "";
        elements.joinCodeLine.hidden = normalized.length === 0;
        elements.joinCode.textContent = normalized;
        if (normalized) {
            elements.joinCodeCopy.setAttribute("data-copy", normalized);
        } else {
            elements.joinCodeCopy.removeAttribute("data-copy");
        }
    }

    function handlePluginVersion(pluginVersion) {
        if (typeof pluginVersion !== "string" || !pluginVersion) {
            return;
        }

        var storedVersion;
        try {
            storedVersion = window.localStorage.getItem(MOTD_VERSION_STORAGE_KEY);
        } catch (error) {
            return;
        }

        if (storedVersion === pluginVersion) {
            return;
        }
        if (storedVersion === null) {
            storageWrite(MOTD_VERSION_STORAGE_KEY, pluginVersion);
            return;
        }
        if (storageWrite(MOTD_VERSION_STORAGE_KEY, pluginVersion)) {
            showNoticeToast("Map updated — v" + pluginVersion);
        }
    }

    function handlePlayersPayload(payload) {
        if (!payload || typeof payload !== "object") {
            throw new Error("Invalid players payload");
        }

        feedLastUpdated.players = Date.now();
        setFeedState("players", true);
        firstPlayersPayloadReceived = true;
        var tweenDuration = playerPayloadTweenDuration(payload);
        var hadPlayers = latestPlayers.length > 0;
        var previousPlayerNames = latestPlayers.map(function (player) {
            return player.name || "";
        }).sort().join("\n");
        latestPlayers = normalizePlayers(payload);
        recordPlayerTrails(latestPlayers);
        if (layerSettings.trails) {
            backfillVisiblePlayerTrails();
        }
        var currentPlayerNames = latestPlayers.map(function (player) {
            return player.name || "";
        }).sort().join("\n");
        renderPlayerList(latestPlayers);
        renderConsolePlayers();
        updatePlayerMarkers(latestPlayers, tweenDuration);
        if (hadPlayers !== (latestPlayers.length > 0)) {
            updateEntityPolling(true);
        }
        applyInitialPlayersView();
        updateLayerCounts();
        applyPendingHashFollow();
        updateCinemaFromPlayers();
        if (previousPlayerNames !== currentPlayerNames &&
            document.activeElement === elements.commandInput &&
            findPlayerSuggestionContext(elements.commandInput.value.replace(/^\s+/, ""))) {
            renderCommandSuggestions();
        }
        tryBootCinemaFromHash();
        recordPollSuccess("players");
    }

    async function pollStatus() {
        if (eventSourceOpen || document.hidden || pollCircuitOpen) {
            return;
        }

        try {
            handleStatusPayload(await fetchJson("/api/status"));
        } catch (error) {
            recordPollFailure("status");
            setFeedState("status", false);
        }
    }

    async function pollPlayers() {
        if (eventSourceOpen || document.hidden || pollCircuitOpen) {
            return;
        }

        try {
            handlePlayersPayload(await fetchJson("/api/players"));
        } catch (error) {
            recordPollFailure("players");
            setFeedState("players", false);
        }
    }

    function resumePollingAfterEventStream() {
        if (destroyed) {
            return;
        }
        pollStatus();
        pollPlayers();
        pollWebPins();
        pollSagaActivity();
        if (consoleIsActive()) {
            pollConsoleLog();
        }
    }

    function refreshPollingAfterVisibility() {
        if (document.hidden || pollCircuitOpen) {
            return;
        }

        resumePollingAfterEventStream();
        pollPins();
        startHeatmapPolling();
        scheduleLeaderboardPoll(0);
        scheduleStatsPolling(0);
        updateEntityPolling(true);
        updateEntityFocusPolling(true);
        POI_GROUP_ORDER.forEach(function (group) {
            var state = getLazyPoiState(group);
            if (lazyPoiLoadingAllowed(group) && !state.requestPending) {
                scheduleLazyPoiPoll(group, 0);
            }
        });
        if (hasLiveAccess() && dungeonRegistryState.loaded &&
            !dungeonRegistryState.ready && !dungeonRegistryState.pending) {
            scheduleDungeonRegistryPoll(0);
        }
        if (activeDungeonId) {
            scheduleDungeonDetailPoll(0);
        }
    }

    function scheduleEventStreamRetry() {
        if (destroyed || typeof window.EventSource !== "function" || eventSourceRetryTimer) {
            return;
        }

        var delay = eventSourceRetryDelay;
        eventSourceRetryDelay = Math.min(eventSourceRetryDelay * 2, SSE_RETRY_MAX_MS);
        eventSourceRetryTimer = window.setTimeout(function () {
            eventSourceRetryTimer = 0;
            connectEventStream();
        }, delay);
    }

    function disconnectEventStream(source) {
        if (source && source !== eventSource) {
            return;
        }

        var activeSource = eventSource;
        eventSource = null;
        eventSourceOpen = false;
        eventSourceLogFlowing = false;
        if (activeSource) {
            activeSource.close();
        }
        if (!destroyed) {
            resumePollingAfterEventStream();
            ensureChatHistory();
            scheduleEventStreamRetry();
        }
    }

    function readEventStreamPayload(source, event, handler) {
        if (source !== eventSource) {
            return;
        }

        try {
            handler(JSON.parse(event.data));
        } catch (error) {
            disconnectEventStream(source);
        }
    }

    function connectEventStream() {
        if (destroyed || typeof window.EventSource !== "function" || eventSource) {
            return;
        }

        var source;
        try {
            source = new window.EventSource(authorizedUrl("/api/events"));
        } catch (error) {
            scheduleEventStreamRetry();
            return;
        }

        eventSource = source;
        eventSourceOpen = false;
        eventSourceLogFlowing = false;
        addAppListener(source, "open", function () {
            if (source !== eventSource) {
                return;
            }
            eventSourceOpen = true;
            eventSourceRetryDelay = SSE_RETRY_INITIAL_MS;
            ensureChatHistory();
            if ((currentView === "admin" || currentView === "shared") && sagaLoaded) {
                loadSagaActivity(0, true);
            }
        });
        addAppListener(source, "players", function (event) {
            readEventStreamPayload(source, event, handlePlayersPayload);
        });
        addAppListener(source, "status", function (event) {
            readEventStreamPayload(source, event, handleStatusPayload);
        });
        addAppListener(source, "webpins", function (event) {
            readEventStreamPayload(source, event, handleWebPinRevisionPayload);
        });
        addAppListener(source, "ping", function (event) {
            readEventStreamPayload(source, event, handlePingPayload);
        });
        addAppListener(source, "chat", function (event) {
            readEventStreamPayload(source, event, handleChatPayload);
        });
        addAppListener(source, "activity", function (event) {
            readEventStreamPayload(source, event, handleActivityStreamPayload);
        });
        addAppListener(source, "log", function (event) {
            readEventStreamPayload(source, event, function (payload) {
                eventSourceLogFlowing = true;
                handleConsoleLogPayload(payload, true);
                recordPollSuccess("console-log");
            });
        });
        addAppListener(source, "error", function () {
            disconnectEventStream(source);
        });
    }

    function startPolling(task, interval) {
        function schedule() {
            if (destroyed || pollCircuitOpen) {
                return;
            }
            var timer = window.setTimeout(function () {
                recurringPollTimers.delete(timer);
                run();
            }, interval);
            recurringPollTimers.add(timer);
        }

        async function run() {
            if (destroyed || pollCircuitOpen) {
                return;
            }
            if (!document.hidden) {
                await task();
            }
            if (!destroyed) {
                schedule();
            }
        }

        run();
    }

    function invalidateEmbedSize() {
        if (destroyed || !map) {
            return;
        }
        map.invalidateSize({ animate: false, pan: false });
        scheduleMinimapUpdate();
        var dungeon = dungeonDetailCache.get(activeDungeonId);
        if (dungeon) {
            drawDungeonCanvas(dungeon);
        }
    }

    function destroyApp() {
        if (destroyed) {
            return;
        }
        destroyed = true;
        pollCircuitOpen = true;
        deactivateTimelapse();
        timelapseFrameCache.clear();
        timelapseFrameRequests.clear();
        timelapseIndexPromise = null;
        if (timelapseScrubber && timelapseScrubber.parentNode) {
            timelapseScrubber.parentNode.removeChild(timelapseScrubber);
        }
        timelapseScrubber = null;
        timelapseTrack = null;
        timelapsePlayButton = null;
        timelapseReadoutDay = null;
        timelapseReadoutDate = null;
        timelapseSpeedControl = null;
        if (cinemaState) {
            teardownCinemaState(cinemaState);
            cinemaState = null;
        }
        window.clearTimeout(eventSourceRetryTimer);
        window.clearTimeout(hashUpdateTimer);
        window.clearTimeout(renderStatusFailureTimer);
        window.clearTimeout(mapLoadingTimeoutTimer);
        clearLeaderboardPoll();
        clearRecurringPollTimers();
        window.clearInterval(sagaRelativeTimer);
        stopAllLazyPoiPolling();
        window.clearInterval(popupRefreshTimer);
        window.clearInterval(raidProgressTimer);
        window.clearInterval(layersStalenessTimer);
        window.clearInterval(savedBadgeTimer);
        window.clearTimeout(dayToastTimer);
        window.clearTimeout(noticeToastTimer);
        window.clearTimeout(saveButtonTimer);
        window.clearTimeout(codexSearchTimer);
        window.clearTimeout(dungeonRegistryState.timer);
        window.clearTimeout(dungeonDetailPollTimer);
        window.clearTimeout(mapContextMenuTimer);
        window.clearTimeout(coordinateSearchTimer);
        window.clearTimeout(fogCoverTimer);
        if (dungeonResizeObserver) {
            dungeonResizeObserver.disconnect();
            dungeonResizeObserver = null;
        }
        removeAppListener(document, "visibilitychange", handleMarkerTweenVisibility);
        markerTweens.clear();
        window.cancelAnimationFrame(markerTweenFrame);
        markerTweenFrame = 0;
        window.cancelAnimationFrame(minimapFrame);
        minimapFrame = 0;
        activePingMarkers.forEach(function (record) {
            window.clearTimeout(record.timer);
        });
        clearCoordinateSearchMarker();
        if (eventSource) {
            eventSource.close();
            eventSource = null;
        }
        eventSourceOpen = false;
        eventSourceLogFlowing = false;
        if (map) {
            map.remove();
            map = null;
        }
        if (embedMode) {
            appRoot.classList.remove(
                "is-cinema",
                "is-cinema-raid",
                "is-codex-active",
                "is-console-active",
                "is-dropping-webpin",
                "is-dungeon-open",
                "is-measuring",
                "is-pinging",
                "is-timelapse",
                "is-towing"
            );
            var fullscreenElement =
                document.fullscreenElement || document.webkitFullscreenElement;
            if (fullscreenElement === appRoot) {
                var exitFullscreen =
                    document.exitFullscreen || document.webkitExitFullscreen;
                if (exitFullscreen) {
                    var exitResult = exitFullscreen.call(document);
                    if (exitResult && typeof exitResult.catch === "function") {
                        exitResult.catch(function () {
                            return;
                        });
                    }
                }
            }
            clearEmbedRuntime();
            hostWindow.ValheimOneEmbed = {
                destroy: function () {
                    return;
                },
                invalidateSize: function () {
                    return;
                }
            };
        }
    }

    bindCinemaEvents();
    bindConsoleEvents();
    bindCodexEvents();
    bindDungeonEvents();
    setActiveTab(requestedTab, false);
    bindPopupDocumentEvents();
    addAppListener(elements.sagaToggle, "click", function () {
        setSagaCollapsed(!elements.sagaPanel.classList.contains("is-collapsed"));
    });
    addAppListener(elements.chatToggle, "click", function () {
        setChatCollapsed(!elements.chatPanel.classList.contains("is-collapsed"));
    });
    addAppListener(elements.chatForm, "submit", function (event) {
        event.preventDefault();
        sendAdminChat();
    });
    addAppListener(elements.leaderboardToggle, "click", function () {
        setLeaderboardCollapsed(
            !elements.leaderboardPanel.classList.contains("is-collapsed")
        );
    });
    setSagaCollapsed(true);
    setChatCollapsed(true);
    setLeaderboardCollapsed(true);
    renderChatHistory();
    addAppListener(elements.sidebarState, "change", function () {
        if (!elements.sidebarState.checked ||
            !window.matchMedia("(max-width: 759px)").matches) {
            return;
        }
        if (layersSetCollapsed) {
            layersSetCollapsed(true);
        }
        setMapSearchOpen(false, false);
    });
    renderPlayerCount(latestPlayerCount);
    renderConsolePlayers();
    startMapLoadingTimeout();
    savedBadgeTimer = window.setInterval(renderSavedBadge, SAVED_BADGE_REFRESH_MS);
    sagaRelativeTimer = window.setInterval(renderSagaRelativeTimes, 30000);
    startPolling(pollStatus, POLL_INTERVAL_MS);
    startPolling(pollPlayers, POLL_INTERVAL_MS);
    startPolling(pollSagaActivity, ACTIVITY_POLL_INTERVAL_MS);
    connectEventStream();
    addAppListener(document, "visibilitychange", handleMarkerTweenVisibility);
    window.addEventListener("beforeunload", destroyApp);
    if (embedMode) {
        hostWindow.ValheimOneEmbed = {
            destroy: destroyApp,
            invalidateSize: invalidateEmbedSize
        };
    }
}(window));
