(function () {
    "use strict";

    var POLL_INTERVAL_MS = 2000;
    var PINS_POLL_INTERVAL_MS = 60000;
    var ENTITIES_POLL_INTERVAL_MS = 10000;
    var CONSOLE_LOG_POLL_INTERVAL_MS = 2000;
    var CONSOLE_STATS_POLL_INTERVAL_MS = 5000;
    var CONSOLE_LOG_LIMIT = 1000;
    var COMMAND_HISTORY_LIMIT = 50;
    var MOVE_DURATION_MS = 400;
    var TILE_SIZE = 256;
    var WORLD_UNITS = 256;
    var POI_CLUSTER_ZOOM = 2;
    var POI_CLUSTER_GRID_PX = 64;
    var RESOURCE_POI_POLL_INTERVAL_MS = 5000;
    var RESOURCE_POI_REFRESH_INTERVAL_MS = 3 * 60 * 1000;
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
    var SAVED_BADGE_REFRESH_MS = 30000;
    var SAVED_STALE_MS = 30 * 60 * 1000;
    var DAY_TOAST_DURATION_MS = 4000;
    var CINEMA_AUTO_CYCLE_MS = 20000;
    var CINEMA_REFOLLOW_MS = 10 * 60 * 1000;
    var CINEMA_AMBIENT_STEP_MS = 18000;
    var CINEMA_AMBIENT_DURATION_SEC = 16;
    var LAYER_STORAGE_KEY = "vo-livemap-layers-v2";
    var LEGACY_LAYER_STORAGE_KEY = "vo-livemap-layers";
    var LEGACY_MINIMAP_STORAGE_KEY = "vo-livemap-minimap";
    var TAB_SESSION_KEY = "vo-livemap-active-tab";
    var CONSOLE_CATEGORY_ORDER = ["server", "players", "moderation", "world", "diagnostics"];
    var CONSOLE_CATEGORY_LABELS = {
        server: "Server",
        players: "Players",
        moderation: "Moderation",
        world: "World",
        diagnostics: "Diagnostics"
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

    var POI_CATEGORIES = [
        {
            key: "bosses",
            label: "Bosses & Trader",
            groups: ["spawn", "boss", "trader"]
        },
        {
            key: "dungeons",
            label: "Dungeons",
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
            groups: [
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
        structure_tarpit: { label: "Tar Pits", glyph: "≋", category: "structures" },
        structure_shipwreck: { label: "Shipwrecks", glyph: "⚓", category: "structures" },
        structure_ruins: { label: "Ruins & Villages", glyph: "▥", category: "structures" },
        structure_mistlands: { label: "Mistlands Remains", glyph: "†", category: "structures" },
        structure_runestone: { label: "Runestones & Lore", glyph: "ᚱ", category: "structures" },
        misc: { label: "Misc", glyph: "◇", category: "structures" }
    };

    var POI_GROUP_ORDER = [];
    POI_CATEGORIES.forEach(function (category) {
        category.groups.forEach(function (group) {
            POI_GROUP_ORDER.push(group);
        });
    });

    var ENTITY_GROUP_ORDER = ["ship", "cart", "portal", "ward", "bed", "tombstone"];
    var ENTITY_GROUPS = {
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
        trails: false,
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
        structure_tarpit: false,
        structure_shipwreck: false,
        structure_ruins: false,
        structure_mistlands: false,
        structure_runestone: false,
        misc: false,
        fog: true,
        regions: true,
        tint: true,
        minimap: false,
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
        poiOpacity: 100,
        legendCollapsed: false,
        mapStyle: "default"
    };

    var query = new URLSearchParams(window.location.search);
    var token = query.get("token") || "";
    var failedFeeds = new Set();
    var consecutiveStatusFailures = 0;
    var markerRecords = new Map();
    var latestPlayers = [];
    var latestEntities = [];
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
    var pingLayer = null;
    var pendingMapPings = [];
    var activePingMarkers = new Set();
    var playerLayer = null;
    var pinLayer = null;
    var latestPins = [];
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
    var availablePoiGroups = new Set();
    var entityLayers = new Map();
    var entityAvailability = "unknown";
    var entityRequestPending = false;
    var entityPollTimer = 0;
    var entityFocusPollTimer = 0;
    var entityFocusRequestPending = false;
    var entityRevision = null;
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
    var latestMapStatus = null;
    var displayedMapStyle = "default";
    var mapStyleProbeRequested = "";
    var renderStatusFailureTimer = 0;
    var lastSavedUnixMs = 0;
    var savedBadgeTimer = 0;
    var dayToastTimer = 0;
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
    var searchControlElement = null;
    var searchInput = null;
    var searchResultsElement = null;
    var searchResultItems = [];
    var searchResultIndex = -1;
    var currentView = null;
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
    var requestedTab = loadRequestedTab();
    var activeTab = "map";
    var consoleAvailable = false;
    var consolePollingStarted = false;
    var consoleLogRequestPending = false;
    var consoleStatsRequestPending = false;
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
        players: 0,
        pois: 0,
        fog: 0,
        status: 0
    };

    var elements = {
        bannedCount: document.getElementById("console-banned-count"),
        bannedList: document.getElementById("console-banned-list"),
        commandForm: document.getElementById("console-command-form"),
        commandInput: document.getElementById("console-command"),
        commandReference: document.getElementById("console-command-reference"),
        commandReferenceBody: document.getElementById("console-command-reference-body"),
        commandReferenceClose: document.getElementById("console-command-reference-close"),
        commandsToggle: document.getElementById("console-commands-toggle"),
        cinemaClock: document.getElementById("cinema-clock"),
        cinemaDay: document.getElementById("cinema-day"),
        cinemaExit: document.getElementById("cinema-exit"),
        cinemaHud: document.getElementById("cinema-hud"),
        cinemaModeChip: document.getElementById("cinema-mode-chip"),
        cinemaPlayerBiome: document.getElementById("cinema-player-biome"),
        cinemaPlayerCard: document.getElementById("cinema-player-card"),
        cinemaPlayerHeading: document.getElementById("cinema-player-heading"),
        cinemaPlayerName: document.getElementById("cinema-player-name"),
        cinemaPlayerSession: document.getElementById("cinema-player-session"),
        cinemaPlayerSpeed: document.getElementById("cinema-player-speed"),
        cinemaSecondaryChip: document.getElementById("cinema-secondary-chip"),
        cinemaServerName: document.getElementById("cinema-server-name"),
        cinemaStaleness: document.getElementById("cinema-staleness"),
        cinemaStayTarget: document.getElementById("cinema-stay-target"),
        cinemaWind: document.getElementById("cinema-wind"),
        cinemaWindLabel: document.getElementById("cinema-wind-label"),
        confirmBackdrop: document.getElementById("console-confirm-backdrop"),
        confirmCancel: document.getElementById("console-confirm-cancel"),
        confirmMessage: document.getElementById("console-confirm-message"),
        confirmSubmit: document.getElementById("console-confirm-submit"),
        consoleLog: document.getElementById("console-log"),
        consolePane: document.getElementById("console-pane"),
        consoleResume: document.getElementById("console-resume"),
        consoleTab: document.getElementById("console-tab"),
        dayToast: document.getElementById("day-toast"),
        dayNumber: document.getElementById("day-number"),
        exploredChip: document.getElementById("explored-chip"),
        mapPane: document.getElementById("map"),
        mapTab: document.getElementById("map-tab"),
        mapStatus: document.getElementById("render-status"),
        mapStatusText: document.getElementById("render-status-text"),
        offlineBadge: document.getElementById("offline-badge"),
        playerCount: document.getElementById("player-count"),
        playerList: document.getElementById("player-list"),
        publicViewBadge: document.getElementById("public-view-badge"),
        raidBadge: document.getElementById("raid-badge"),
        saveButton: document.getElementById("console-save"),
        savedChip: document.getElementById("saved-chip"),
        saveStatus: document.getElementById("console-save-status"),
        serverName: document.getElementById("server-name"),
        sidebarState: document.getElementById("sidebar-state"),
        skyIndicator: document.getElementById("sky-indicator"),
        statFrameAvg: document.getElementById("console-stat-frame-avg"),
        statFrameMax: document.getElementById("console-stat-frame-max"),
        statHeap: document.getElementById("console-stat-heap"),
        statPlayers: document.getElementById("console-stat-players"),
        statUptime: document.getElementById("console-stat-uptime"),
        statZdo: document.getElementById("console-stat-zdo"),
        statusChips: document.getElementById("status-chips"),
        suggestionList: document.getElementById("console-suggestions"),
        tabList: document.getElementById("view-tabs"),
        consolePlayerCount: document.getElementById("console-player-count"),
        consolePlayerList: document.getElementById("console-player-list"),
        worldClock: document.getElementById("world-clock"),
        worldName: document.getElementById("world-name"),
        windChip: document.getElementById("wind-chip"),
        watchButton: document.getElementById("watch-button")
    };

    function hasLiveAccess() {
        return currentView !== "public";
    }

    function authorizedUrl(path) {
        if (!token) {
            return path;
        }

        return path + (path.indexOf("?") === -1 ? "?" : "&") +
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

    async function fetchJson(path) {
        var response = await fetch(authorizedUrl(path), {
            cache: "no-store",
            credentials: "same-origin"
        });
        if (!response.ok) {
            throw new Error("HTTP " + response.status);
        }

        return response.json();
    }

    function loadRequestedTab() {
        try {
            return window.sessionStorage.getItem(TAB_SESSION_KEY) === "console" ? "console" : "map";
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
        var nextTab = tab === "console" && consoleAvailable ? "console" : "map";
        if (persist) {
            requestedTab = nextTab;
            saveRequestedTab(nextTab);
            if (window.matchMedia("(max-width: 759px)").matches) {
                elements.sidebarState.checked = false;
            }
        }

        if (nextTab === activeTab && elements.consolePane.hidden === (nextTab !== "console")) {
            return;
        }

        activeTab = nextTab;
        var showConsole = activeTab === "console";
        setTabButtonState(elements.mapTab, !showConsole);
        setTabButtonState(elements.consoleTab, showConsole);
        elements.consolePane.hidden = !showConsole;
        elements.mapPane.setAttribute("aria-hidden", String(showConsole));
        document.body.classList.toggle("is-console-active", showConsole);

        if (!showConsole) {
            closeSuggestions();
            closeConfirmDialog();
            return;
        }

        if (!persist && window.matchMedia("(max-width: 759px)").matches) {
            elements.sidebarState.checked = false;
        }

        startConsolePolling();
        loadConsoleMeta();
        loadConsoleHistory().then(pollConsoleLog);
        pollConsoleStats();
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
        elements.tabList.hidden = !isAvailable;
        if (!isAvailable) {
            setActiveTab("map", false);
            return;
        }

        loadConsoleHistory();
        setActiveTab(requestedTab, false);
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
            throw new Error(message);
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

        option.addEventListener("mouseenter", function () {
            setConsoleSuggestionIndex(index);
        });
        option.addEventListener("mousedown", function (event) {
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
        return consoleCommands.map(function (command) {
            var lowerName = command.name.toLowerCase();
            var lowerDescription = command.description.toLowerCase();
            var rank = lowerName.indexOf(lowerQuery) === 0 ? 0 :
                (lowerName.indexOf(lowerQuery) !== -1 ? 1 :
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
            : suggestion.command.name + " ";
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
        var entries = payload && Array.isArray(payload.entries) ? payload.entries : [];
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
                text: "[" + operatorName + "] " + command
            });
            var output = entry && typeof entry.output === "string" ? entry.output : "";
            if (output) {
                recalled.push({
                    kind: entry.status === "error" ? "error" : "output",
                    text: entry.status === "error" ? "! " + output : output
                });
            }
        });
        appendConsoleEntries(recalled);

        commandHistory = entries.map(function (entry) {
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
                return;
            }
            handleConsoleLogPayload(payload);
        } catch (error) {
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

    async function pollConsoleStats() {
        if (!consoleIsActive() || consoleStatsRequestPending) {
            return;
        }

        consoleStatsRequestPending = true;
        try {
            var payload = await fetchConsoleJson("/api/stats");
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
            reportConsoleFailure("stats", "Server stats", error);
        } finally {
            consoleStatsRequestPending = false;
        }
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
        button.addEventListener("click", callback);
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
                openConfirmDialog("kick", player.name);
            }, cannotManage));
            actions.appendChild(createActionButton("Ban", "is-danger", function () {
                openConfirmDialog("ban", player.name);
            }, cannotManage));
            item.appendChild(name);
            item.appendChild(actions);
            elements.consolePlayerList.appendChild(item);
        });
    }

    function openConfirmDialog(action, player) {
        if (!player) {
            return;
        }

        confirmAction = { action: action, player: player };
        elements.confirmMessage.textContent = action.charAt(0).toUpperCase() + action.slice(1) + " " + player + "?";
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

        var action = confirmAction.action;
        var player = confirmAction.player;
        closeConfirmDialog();
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

    async function submitConsoleCommand() {
        var command = elements.commandInput.value.trim();
        if (!command) {
            return;
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
        window.setTimeout(async function pollLogLoop() {
            await pollConsoleLog();
            window.setTimeout(pollLogLoop, CONSOLE_LOG_POLL_INTERVAL_MS);
        }, CONSOLE_LOG_POLL_INTERVAL_MS);
        window.setTimeout(async function pollStatsLoop() {
            await pollConsoleStats();
            window.setTimeout(pollStatsLoop, CONSOLE_STATS_POLL_INTERVAL_MS);
        }, CONSOLE_STATS_POLL_INTERVAL_MS);
    }

    function bindConsoleEvents() {
        elements.mapTab.addEventListener("click", function () {
            setActiveTab("map", true);
        });
        elements.consoleTab.addEventListener("click", function () {
            setActiveTab("console", true);
        });
        elements.consoleLog.addEventListener("scroll", function () {
            var distanceFromBottom = elements.consoleLog.scrollHeight -
                elements.consoleLog.scrollTop - elements.consoleLog.clientHeight;
            consoleFollowLog = distanceFromBottom <= 36;
            if (consoleFollowLog) {
                elements.consoleResume.hidden = true;
            }
        });
        elements.consoleResume.addEventListener("click", function () {
            consoleFollowLog = true;
            elements.consoleLog.scrollTop = elements.consoleLog.scrollHeight;
            elements.consoleResume.hidden = true;
        });
        elements.commandsToggle.addEventListener("click", toggleCommandReference);
        elements.commandReferenceClose.addEventListener("click", function () {
            setCommandReferenceOpen(false);
            elements.commandsToggle.focus();
        });
        elements.commandForm.addEventListener("submit", function (event) {
            event.preventDefault();
            submitConsoleCommand();
        });
        elements.commandInput.addEventListener("input", function () {
            consoleSuggestionClosed = false;
            commandHistoryIndex = commandHistory.length;
            renderCommandSuggestions();
        });
        elements.commandInput.addEventListener("keydown", function (event) {
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
        elements.saveButton.addEventListener("click", saveWorld);
        elements.confirmCancel.addEventListener("click", closeConfirmDialog);
        elements.confirmSubmit.addEventListener("click", runConfirmedAction);
        elements.confirmBackdrop.addEventListener("click", function (event) {
            if (event.target === elements.confirmBackdrop) {
                closeConfirmDialog();
            }
        });
        document.addEventListener("keydown", function (event) {
            if (event.key === "Escape" && !elements.confirmBackdrop.hidden) {
                closeConfirmDialog();
            } else if (event.key === "Escape" && !elements.commandReference.hidden) {
                setCommandReferenceOpen(false);
                elements.commandsToggle.focus();
            }
        });
    }

    function setFeedState(feed, isOnline) {
        if (isOnline) {
            failedFeeds.delete(feed);
        } else {
            failedFeeds.add(feed);
        }

        if (feed === "status") {
            consecutiveStatusFailures = isOnline ? 0 : consecutiveStatusFailures + 1;
            if (consecutiveStatusFailures >= 3 && !elements.mapStatus.hidden) {
                elements.mapStatusText.textContent = "Server offline — waiting to reconnect";
                elements.mapStatus.querySelector(".spinner").hidden = true;
            }
        }

        elements.offlineBadge.hidden = failedFeeds.size === 0;
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

    function loadLayerSettings() {
        var settings = {};
        Object.keys(LAYER_DEFAULTS).forEach(function (key) {
            settings[key] = key === "poiColors" ? {} : LAYER_DEFAULTS[key];
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
                window.localStorage.setItem(LAYER_STORAGE_KEY, JSON.stringify(settings));
            }
        } catch (error) {
            return settings;
        }

        return settings;
    }

    function saveLayerSettings() {
        try {
            window.localStorage.setItem(LAYER_STORAGE_KEY, JSON.stringify(layerSettings));
        } catch (error) {
            return;
        }
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
        glyph.textContent = options.glyph || "•";
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

        if (options.actions && options.actions.length > 0) {
            var actions = document.createElement("div");
            actions.className = "vo-popup-actions";
            options.actions.forEach(function (action) {
                var button = document.createElement("button");
                button.type = "button";
                button.className = "vo-popup-action" + (action.active ? " is-active" : "");
                button.textContent = action.label;
                button.setAttribute("data-popup-action", action.action);
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
            shell.appendChild(actions);
        }

        var footer = document.createElement("div");
        footer.className = "vo-popup-footer";
        if (Number.isFinite(options.surveyUnixMs) && options.surveyUnixMs > 0) {
            footer.setAttribute("data-survey-unix-ms", String(options.surveyUnixMs));
            footer.textContent = popupSurveyStalenessText(options.surveyUnixMs);
        } else {
            footer.setAttribute("data-feed", options.feed);
            footer.textContent = popupStalenessText(options.feed);
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
            var scanUnixMs = Number(footer.getAttribute("data-survey-unix-ms"));
            footer.textContent = Number.isFinite(scanUnixMs) && scanUnixMs > 0
                ? popupSurveyStalenessText(scanUnixMs)
                : popupStalenessText(footer.getAttribute("data-feed"));
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
            document.body.appendChild(textarea);
            textarea.select();
            var copied = false;
            try {
                copied = document.execCommand("copy");
            } catch (error) {
                copied = false;
            }
            document.body.removeChild(textarea);
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
        document.addEventListener("click", function (event) {
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
            } else if (action === "watch" && kind === "player") {
                enterCinema(key);
            } else if (action === "jump-tombstone") {
                jumpToTombstone(key);
            } else if (action === "jump-portal") {
                jumpToPortal(key);
            }
        });
    }

    function worldDistance(leftX, leftZ, rightX, rightZ) {
        var deltaX = rightX - leftX;
        var deltaZ = rightZ - leftZ;
        return Math.sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
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

    function appendTrailSample(key, kind, x, z, timestamp) {
        var buffer = ensureTrailBuffer(key, kind, timestamp);
        var last = buffer.samples.length > 0 ? buffer.samples[buffer.samples.length - 1] : null;
        var distance = last ? worldDistance(last.x, last.z, x, z) : 0;
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
            var seenTimestamps = new Set();
            var prepend = points.filter(function (point) {
                var pointTimestamp = Number(point && point.t);
                if (!Number.isFinite(pointTimestamp) ||
                    !Number.isFinite(Number(point.x)) ||
                    !Number.isFinite(Number(point.z)) ||
                    pointTimestamp >= oldestBufferedTimestamp ||
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
        var color = window.getComputedStyle(document.documentElement)
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

    function updateShipHeadingLine(entity) {
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
        var points = [
            worldToLatLng(entity.x, entity.z),
            worldToLatLng(
                entity.x + Math.sin(headingRadians) * SHIP_HEADING_LENGTH_M,
                entity.z + Math.cos(headingRadians) * SHIP_HEADING_LENGTH_M
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
            updateShipHeadingLine(entity);
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
            if ((entity.group === "ship" || entity.group === "cart") && entity.trailKey) {
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
        var color = window.getComputedStyle(document.documentElement).getPropertyValue(token).trim();
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
        followPill.addEventListener("click", clearFollow);
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
            return;
        }

        var ageMs = Math.max(0, Date.now() - lastSavedUnixMs);
        elements.savedChip.textContent = "Saved " + formatSavedAge(ageMs);
        elements.savedChip.classList.toggle("is-stale", ageMs > SAVED_STALE_MS);
        elements.savedChip.hidden = false;
        elements.statusChips.hidden = false;
    }

    function updateLastSaved(value) {
        var timestamp = finiteNumberOrNull(value);
        lastSavedUnixMs = timestamp !== null && timestamp > 0 ? timestamp : 0;
        renderSavedBadge();
    }

    function renderWindStatus() {
        if (!latestWind) {
            return;
        }

        var direction = compassLabel(latestWind.fromDeg);
        var intensityPct = Math.round(latestWind.intensity * 100);
        elements.windChip.textContent = "⤢ " + direction + " " + intensityPct + "%";
        elements.windChip.hidden = false;
        elements.statusChips.hidden = false;

        // The server reports where wind comes from; the needle points where it blows toward.
        var towardDeg = (latestWind.fromDeg + 180) % 360;
        var scale = 0.3 + (0.7 * latestWind.intensity);
        var windTitle = "Wind from " + direction + " · " + intensityPct + "%";
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
            elements.exploredChip.textContent = "Explored " + exploredPct.toFixed(1) + "%";
            elements.exploredChip.hidden = false;
            elements.statusChips.hidden = false;
        }

        var windDirDeg = finiteNumberOrNull(status.windDirDeg);
        var windIntensity = finiteNumberOrNull(status.windIntensity);
        if (windDirDeg === null || windIntensity === null) {
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
        if (renderStatusFailureTimer) {
            return;
        }
        if (consecutiveStatusFailures >= 3) {
            elements.mapStatus.hidden = false;
            elements.mapStatusText.textContent = "Server offline — waiting to reconnect";
            elements.mapStatus.querySelector(".spinner").hidden = true;
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
        return {
            textureSize: textureSize,
            pixelSize: pixelSize,
            overviewZoom: overviewZoom,
            maximumZoom: maximumZoom,
            unitsPerPixel: WORLD_UNITS / textureSize
        };
    }

    function reconcileMapMetrics(nextMetrics) {
        if (nextMetrics.maximumZoom === mapMetrics.maximumZoom &&
            nextMetrics.overviewZoom === mapMetrics.baseZoom &&
            nextMetrics.textureSize === mapMetrics.textureSize &&
            nextMetrics.pixelSize === mapMetrics.pixelSize) {
            return;
        }

        var center = map.getCenter();
        var zoom = Math.max(map.getMinZoom(), Math.min(nextMetrics.maximumZoom, map.getZoom()));
        mapMetrics.textureSize = nextMetrics.textureSize;
        mapMetrics.pixelSize = nextMetrics.pixelSize;
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
            maximumZoom: maximumZoom,
            unitsPerPixel: nextMetrics.unitsPerPixel
        };

        worldBounds = L.latLngBounds([[-WORLD_UNITS, 0], [0, WORLD_UNITS]]);
        map = L.map("map", {
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
        map.createPane("fogPane");
        map.getPane("fogPane").style.zIndex = "350";
        map.getPane("fogPane").style.pointerEvents = "none";
        map.createPane("tintPane");
        map.getPane("tintPane").style.zIndex = "360";
        map.getPane("tintPane").style.pointerEvents = "none";
        map.createPane("regionPane");
        map.getPane("regionPane").style.zIndex = "370";
        map.getPane("regionPane").style.pointerEvents = "none";
        map.createPane("trailPane");
        map.getPane("trailPane").style.zIndex = "380";
        map.getPane("trailPane").style.pointerEvents = "none";
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
        initialiseDataLayers();
        bindMapPopupEvents();
        ensureFollowPill();
        createLayersControl();
        createCompassControl();
        createScaleBarControl();
        createMeasureControl();
        createPingControl();
        createFullscreenControl();
        createSearchControl();
        createCoordinateControl();
        bindMapContextMenu();
        createMinimapControl();
        applyDensityPreferences();
        applyPoiPreferences();
        map.on("dragstart", function () {
            if (!cinemaState) {
                clearFollow();
            }
        });
        map.on("zoomend", renderPoiLayers);
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
    }

    function initialiseDataLayers() {
        playerLayer = L.layerGroup();
        pinLayer = L.layerGroup();
        regionLayer = L.layerGroup();
        trailLayer = L.layerGroup().addTo(map);
        shipHeadingLayer = L.layerGroup();
        portalNetworkLayer = L.layerGroup();
        portalPopupLinkLayer = L.layerGroup().addTo(map);
        wardRadiusLayer = L.layerGroup();
        pingLayer = L.layerGroup().addTo(map);
        POI_GROUP_ORDER.forEach(function (group) {
            poiLayers.set(group, L.layerGroup());
            poiRecords.set(group, []);
        });
        ENTITY_GROUP_ORDER.forEach(function (group) {
            entityLayers.set(group, L.layerGroup());
        });
        pendingMapPings.splice(0).forEach(renderMapPing);
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

                button.addEventListener("click", function () {
                    window.clearTimeout(clickTimer);
                    clickTimer = window.setTimeout(function () {
                        map.flyTo(worldToLatLng(0, 0), map.getZoom(), { duration: 0.45 });
                    }, 250);
                });
                button.addEventListener("dblclick", function (event) {
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
        document.body.classList.remove("is-measuring");
        restoreMeasureDoubleClickZoom();
        updateMeasureHud();
    }

    function clearMeasurement() {
        measureActive = false;
        measureModeEnabled = false;
        measurePoints = [];
        measureLine = null;
        measureVertexMarkers = [];
        document.body.classList.remove("is-measuring");
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
        disarmMapPing();
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
        document.body.classList.add("is-measuring");
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
                measureButton.addEventListener("click", function () {
                    if (measureModeEnabled) {
                        clearMeasurement();
                    } else {
                        startMeasurement();
                    }
                });
                L.DomEvent.disableClickPropagation(container);
                L.DomEvent.disableScrollPropagation(container);
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
        map.on("dblclick", function (event) {
            if (!measureActive) {
                return;
            }
            if (event.originalEvent) {
                event.originalEvent.preventDefault();
            }
            finishMeasurement();
        });
        document.addEventListener("keydown", function (event) {
            if (event.key === "Escape") {
                if (measureActive) {
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

    function disarmMapPing() {
        pingArmed = false;
        document.body.classList.remove("is-pinging");
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
        if (measureModeEnabled) {
            clearMeasurement();
        }
        if (map._popup) {
            map.closePopup();
        }
        pingArmed = true;
        document.body.classList.add("is-pinging");
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
                pingButton.addEventListener("click", function () {
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
        document.addEventListener("keydown", function (event) {
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
                coordinateChip.addEventListener("click", function () {
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
            map.getContainer().addEventListener("mouseleave", function () {
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
        document.body.appendChild(mapContextMenu);
        return mapContextMenu;
    }

    function createMapContextItem(label, action) {
        var item = document.createElement("button");
        item.type = "button";
        item.className = "vo-context-item";
        item.setAttribute("role", "menuitem");
        item.textContent = label;
        item.addEventListener("click", function (event) {
            event.preventDefault();
            event.stopPropagation();
            action(item);
        });
        return item;
    }

    function positionMapContextMenu(menu, clientX, clientY) {
        var margin = 8;
        var viewportWidth = document.documentElement.clientWidth;
        var viewportHeight = document.documentElement.clientHeight;
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

        map.getContainer().addEventListener("contextmenu", function (event) {
            if (event._simulated ||
                (event.sourceCapabilities && event.sourceCapabilities.firesTouchEvents)) {
                return;
            }
            event.preventDefault();
            event.stopImmediatePropagation();
            showMapContextMenu(event);
        }, true);
        document.addEventListener("mousedown", function (event) {
            if (mapContextMenu && !mapContextMenu.hidden &&
                !mapContextMenu.contains(event.target)) {
                dismissMapContextMenu();
            }
        }, true);
        document.addEventListener("click", function (event) {
            if (mapContextMenu && !mapContextMenu.hidden &&
                !mapContextMenu.contains(event.target)) {
                dismissMapContextMenu();
            }
        }, true);
        document.addEventListener("keydown", function (event) {
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
                    return document.fullscreenElement || document.webkitFullscreenElement;
                }

                function syncFullscreenButton() {
                    var isFullscreen = Boolean(fullscreenElement());
                    button.textContent = isFullscreen ? "⤡" : "⛶";
                    button.title = isFullscreen ? "Exit fullscreen" : "Enter fullscreen";
                    button.setAttribute("aria-label", button.title);
                    button.setAttribute("aria-pressed", String(isFullscreen));
                }

                button.type = "button";
                button.addEventListener("click", function () {
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
                        action = document.documentElement.requestFullscreen ||
                            document.documentElement.webkitRequestFullscreen;
                        if (action) {
                            var result = action.call(document.documentElement);
                            if (result && typeof result.catch === "function") {
                                result.catch(function () {
                                    return;
                                });
                            }
                        }
                    }
                });
                document.addEventListener("fullscreenchange", syncFullscreenButton);
                document.addEventListener("webkitfullscreenchange", syncFullscreenButton);
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
                searchText: pin.name + " " + pin.author,
                x: pin.x,
                z: pin.z,
                markerResolver: function () {
                    return findMarkerNearLayer(pinLayer, pin.latLng);
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
        var queryText = searchInput.value.trim().toLocaleLowerCase();
        searchResultsElement.textContent = "";
        searchResultItems = [];
        searchResultIndex = -1;
        searchInput.removeAttribute("aria-activedescendant");
        if (!queryText) {
            searchResultsElement.hidden = true;
            searchInput.setAttribute("aria-expanded", "false");
            return;
        }

        searchResultItems = mapSearchRegistry().map(function (item) {
            return { item: item, rank: rankMapSearchItem(item, queryText) };
        }).filter(function (entry) {
            return entry.rank >= 0;
        }).sort(function (left, right) {
            return left.rank - right.rank || left.item.name.localeCompare(right.item.name);
        }).slice(0, 12).map(function (entry) {
            return entry.item;
        });

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
                glyph.textContent = item.glyph;
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
                button.addEventListener("click", function () {
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
                searchInput.placeholder = "Search the world";
                searchInput.autocomplete = "off";
                searchInput.spellcheck = false;
                searchInput.setAttribute("aria-label", "Search players, pins, and places");
                searchInput.setAttribute("role", "combobox");
                searchInput.setAttribute("aria-autocomplete", "list");
                searchInput.setAttribute("aria-controls", "map-search-results");
                searchInput.setAttribute("aria-expanded", "false");
                searchResultsElement.id = "map-search-results";
                searchResultsElement.className = "map-search-results";
                searchResultsElement.setAttribute("role", "listbox");
                searchResultsElement.hidden = true;

                toggle.addEventListener("click", function () {
                    setMapSearchOpen(!container.classList.contains("is-open"), true);
                });
                searchInput.addEventListener("input", renderMapSearchResults);
                searchInput.addEventListener("keydown", function (event) {
                    if (event.key === "ArrowDown" && searchResultItems.length > 0) {
                        event.preventDefault();
                        moveMapSearchSelection(1);
                    } else if (event.key === "ArrowUp" && searchResultItems.length > 0) {
                        event.preventDefault();
                        moveMapSearchSelection(-1);
                    } else if (event.key === "Enter" && searchResultIndex >= 0) {
                        event.preventDefault();
                        selectMapSearchResult(searchResultIndex);
                    } else if (event.key === "Escape") {
                        event.preventDefault();
                        setMapSearchOpen(false, false);
                        toggle.focus();
                    }
                });
                container.addEventListener("focusout", function () {
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
        document.addEventListener("keydown", function (event) {
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
                toggle.addEventListener("click", function () {
                    minimapSetOpen(!isOpen, true);
                });
                frame.addEventListener("click", function (event) {
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
        var parameters = new URLSearchParams(window.location.hash.replace(/^#/, ""));
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
                    key !== "poiColors" && key !== "poiOpacity" &&
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
        if (!map || !mapMetrics) {
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
                key !== "poiColors" && key !== "poiOpacity" &&
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
        if (window.location.hash !== hash) {
            window.history.replaceState(
                window.history.state,
                "",
                window.location.pathname + window.location.search + hash
            );
        }
    }

    function scheduleHashUpdate() {
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
                chevron.textContent = "⌃";
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
                    chevron.textContent = isCollapsed ? "⌄" : "⌃";
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
        renderJumpChips();

        var liveFeeds = hasLiveAccess() ? ["players", "entities"] : ["players"];
        var liveBody = appendLayerSection("live", "Live", liveFeeds);
        appendLayerRow(liveBody, "players", "Players", "●", "players");
        appendLayerRow(liveBody, "trails", "Trails", "〰", "trails");
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

        var placesBody = appendLayerSection("places", "Places", ["pins", "pois"]);
        appendLayerRow(placesBody, "pins", "Pins", "⌖", "pins");
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
            });
        });
        if (feedLastUpdated.pins === 0) {
            appendLayerStatus(placesBody, "Pins: no save yet");
        }
        if (feedLastUpdated.pois === 0 || poiLoadPending) {
            appendLayerStatus(placesBody, "POIs: no data yet");
        }

        var overlayFeeds = hasLiveAccess() ? ["fog", "entities"] : ["fog"];
        var overlaysBody = appendLayerSection("overlays", "Overlays", overlayFeeds);
        appendMapStyleControl(overlaysBody);
        if (fogAvailable) {
            appendLayerRow(overlaysBody, "fog", "Fog", "≈", "fog", { counted: false });
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
        allButton.addEventListener("click", function () {
            setSectionLayers(section, true);
        });
        noneButton.type = "button";
        noneButton.className = "layer-section-mini";
        noneButton.textContent = "none";
        noneButton.setAttribute("aria-label", "Hide all " + labelText.toLowerCase() + " layers");
        noneButton.addEventListener("click", function () {
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
                button.addEventListener("click", function () {
                    selectMapStyle(choice[0]);
                });
                segments.appendChild(button);
            });
        row.appendChild(title);
        row.appendChild(segments);
        parent.appendChild(row);
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
        var name = document.createElement("span");
        var actions = document.createElement("span");
        var count = document.createElement("span");
        var allButton = document.createElement("button");
        var noneButton = document.createElement("button");
        var body = document.createElement("div");

        section.className = "poi-category";
        section.dataset.poiCategory = category.key;
        header.className = "poi-category-header";
        name.className = "poi-category-name";
        name.textContent = category.label;
        actions.className = "poi-category-actions";
        count.className = "poi-category-count";
        count.dataset.poiCategoryCount = category.key;
        allButton.type = "button";
        allButton.className = "poi-category-mini";
        allButton.textContent = "all";
        allButton.setAttribute("aria-label", "Show all " + category.label.toLowerCase());
        allButton.addEventListener("click", function () {
            setSectionLayers(section, true);
        });
        noneButton.type = "button";
        noneButton.className = "poi-category-mini";
        noneButton.textContent = "none";
        noneButton.setAttribute("aria-label", "Hide all " + category.label.toLowerCase());
        noneButton.addEventListener("click", function () {
            setSectionLayers(section, false);
        });
        body.className = "poi-category-body";

        actions.appendChild(count);
        actions.appendChild(allButton);
        actions.appendChild(noneButton);
        header.appendChild(name);
        header.appendChild(actions);
        section.appendChild(header);
        appendPoiColorControls(section, category);
        section.appendChild(body);
        parent.appendChild(section);
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
            button.addEventListener("click", function () {
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
        checkbox.type = "checkbox";
        checkbox.checked = layerSettings[key];
        checkbox.dataset.layerKey = key;
        checkbox.setAttribute("aria-label", "Show " + labelText);
        checkbox.addEventListener("change", function () {
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

    function layerCountValue(key) {
        if (key === "players") {
            return latestPlayers.length;
        }
        if (key === "pins") {
            return latestPins.length;
        }
        if (key === "trails") {
            return latestPlayers.length;
        }
        if (key === "portalNetwork") {
            return portalPairs.length;
        }
        if (Object.prototype.hasOwnProperty.call(POI_GROUPS, key)) {
            var metadata = poiGroupMeta.get(key);
            return metadata ? metadata.count : (poiRecords.get(key) || []).length;
        }
        if (Object.prototype.hasOwnProperty.call(ENTITY_GROUPS, key)) {
            return latestEntities.filter(function (entity) {
                return entity.group === key;
            }).length;
        }
        return "";
    }

    function updateLayerCounts() {
        if (!layersRows) {
            return;
        }
        layersRows.querySelectorAll("[data-layer-count]").forEach(function (badge) {
            var key = badge.dataset.layerCount;
            var lazyState = lazyPoiStates.get(key);
            var surveying = isResourcePoiGroup(key) && layerSettings[key] &&
                lazyState && (lazyState.requestPending || lazyState.scanning);
            var loading = !surveying && layerSettings[key] && lazyState &&
                lazyState.requestPending;
            badge.textContent = surveying
                ? "Surveying…"
                : loading ? "Loading…" : String(layerCountValue(key));
            badge.classList.toggle("is-surveying", Boolean(surveying || loading));
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
            pins: PINS_POLL_INTERVAL_MS
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
        dotsToggle.addEventListener("change", function () {
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
        sizeSelect.addEventListener("change", function () {
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
        opacityInput.addEventListener("input", function () {
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

    function focusMapLocation(latLng, markerResolver) {
        if (!map || !latLng) {
            return;
        }
        clearFollow();
        var targetZoom = Math.min(map.getMaxZoom(), Math.max(map.getZoom(), 4));
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
        } : null);
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
            button.addEventListener("click", function () {
                jumpToPoiRecord(record);
            });
            strip.appendChild(button);
        }

        appendDirectChip("Spawn", spawn);
        appendDirectChip("Trader", trader);
        if (bosses.length > 0) {
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

            bosses.forEach(function (record) {
                var item = document.createElement("li");
                var button = document.createElement("button");
                item.setAttribute("role", "none");
                button.type = "button";
                button.setAttribute("role", "menuitem");
                button.textContent = record.title;
                button.addEventListener("click", function () {
                    setOpen(false, false);
                    jumpToPoiRecord(record);
                });
                item.appendChild(button);
                menu.appendChild(item);
                menuButtons.push(button);
            });
            toggle.addEventListener("click", function () {
                setOpen(menu.hidden, false);
            });
            toggle.addEventListener("keydown", function (event) {
                if (event.key === "ArrowDown") {
                    event.preventDefault();
                    setOpen(true, true);
                } else if (event.key === "Escape") {
                    setOpen(false, false);
                }
            });
            menu.addEventListener("keydown", function (event) {
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
            dropdown.addEventListener("focusout", function () {
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
        title.textContent = "Legend";
        chevron.className = "legend-chevron";
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
            chevron.textContent = isCollapsed ? "⌄" : "⌃";
        }

        toggle.addEventListener("click", function () {
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
        if (layerSettings.trails) {
            appendLegendItem("〰", "Trails", "trails");
        }
        if (layerSettings.portalNetwork && entityLayersAreAvailable()) {
            appendLegendItem("╌", "Portal network", "portal-network");
        }
        POI_GROUP_ORDER.forEach(function (group) {
            if (availablePoiGroups.has(group) && layerSettings[group]) {
                appendLegendItem(POI_GROUPS[group].glyph, POI_GROUPS[group].label, group);
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

    function syncLayerVisibility() {
        if (!map) {
            return;
        }

        setLayerVisible(playerLayer, layerSettings.players);
        setLayerVisible(pinLayer, layerSettings.pins);
        markerRecords.forEach(function (record) {
            updatePlayerMarkerMotion(record);
        });
        POI_GROUP_ORDER.forEach(function (group) {
            setLayerVisible(
                poiLayers.get(group),
                availablePoiGroups.has(group) && layerSettings[group]
            );
        });
        setLayerVisible(fogOverlay, fogAvailable && layerSettings.fog);
        updateRegionLayerVisibility();
        setLayerVisible(tintOverlay, layerSettings.tint);
        setLayerVisible(
            portalNetworkLayer,
            entityLayersAreAvailable() && layerSettings.portalNetwork
        );
        setLayerVisible(
            wardRadiusLayer,
            entityLayersAreAvailable() && layerSettings.ward
        );
        var shipHeadingsVisible = entityLayersAreAvailable() && layerSettings.ship;
        setLayerVisible(shipHeadingLayer, shipHeadingsVisible);
        if (shipHeadingsVisible) {
            updateShipHeadingLines(latestEntities);
        } else {
            clearShipHeadingLines();
        }
        ENTITY_GROUP_ORDER.forEach(function (group) {
            setLayerVisible(
                entityLayers.get(group),
                entityLayersAreAvailable() && layerSettings[group]
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
    }

    function createPlayerMarker(player) {
        var icon = L.divIcon({
            className: "player-div-icon",
            html: '<span class="player-marker-shell"><span class="player-marker-dot"></span>' +
                '<span class="player-marker-chevron" style="transform: rotate(0deg)" hidden></span></span>',
            iconAnchor: [12, 12],
            iconSize: [24, 24]
        });
        var marker = L.marker(worldToLatLng(player.x, player.z), {
            icon: icon,
            title: player.displayName
        }).addTo(playerLayer);
        var record = {
            animationFrame: 0,
            marker: marker,
            player: player
        };
        marker.bindTooltip(buildPlayerTooltip(player), {
            className: "player-tooltip",
            direction: "top",
            offset: [0, -7],
            opacity: 1,
            permanent: !player.anonymous
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

    function updatePlayerMarkers(players) {
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
                animateMarker(record, target);
            }
            record.marker.setTooltipContent(buildPlayerTooltip(player));
            updatePlayerMarkerMotion(record);
        });

        markerRecords.forEach(function (record, key) {
            if (activeKeys.has(key)) {
                return;
            }

            cancelAnimationFrame(record.animationFrame);
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

    function animateMarker(record, target) {
        cancelAnimationFrame(record.animationFrame);
        if (document.hidden) {
            record.marker.setLatLng(target);
            record.animationFrame = 0;
            return;
        }
        var start = record.marker.getLatLng();
        var startedAt = performance.now();

        function step(now) {
            var amount = Math.min(1, (now - startedAt) / MOVE_DURATION_MS);
            var eased = 1 - Math.pow(1 - amount, 3);
            var current = L.latLng(
                start.lat + ((target.lat - start.lat) * eased),
                start.lng + ((target.lng - start.lng) * eased)
            );
            record.marker.setLatLng(current);
            if (isFollowing("player", record.player.key) &&
                (!cinemaState || !cinemaState.raidJumpActive)) {
                map.panTo(current, { animate: false });
            }

            if (amount < 1) {
                record.animationFrame = requestAnimationFrame(step);
            } else {
                record.animationFrame = 0;
            }
        }

        record.animationFrame = requestAnimationFrame(step);
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

        var anchors = cinemaAmbientAnchors();
        var state = cinemaState;
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
        map.flyTo(worldToLatLng(x, z), zoom, {
            duration: CINEMA_AMBIENT_DURATION_SEC,
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
        document.body.classList.add("is-cinema-raid");
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
            document.body.classList.remove("is-cinema-raid");
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
        document.body.classList.remove("is-cinema-raid");
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
        if (cinemaState.raidJumpActive && currentRaidEvent) {
            primary = "Raid · " + currentRaidEvent.name;
            showStayButton = true;
        } else if (cinemaState.locked) {
            primary = cinemaState.locked.missingSince
                ? "Waiting for " + cinemaState.locked.name + " to return…"
                : "Locked on " + cinemaState.locked.name;
            if (latestPlayers.length === 0) {
                secondary = "No vikings ashore — " + elements.dayNumber.textContent;
            }
        } else if (latestPlayers.length === 0) {
            primary = "No vikings ashore — " + elements.dayNumber.textContent;
        } else {
            primary = "Auto-cycling · click a player to lock";
        }
        elements.cinemaModeChip.textContent = primary;
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
            ambientIndex: 0,
            ambientTimer: 0,
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
        if (map._popup) {
            map.closePopup();
        }
        document.body.classList.add("is-cinema");
        elements.cinemaHud.hidden = false;
        document.addEventListener("visibilitychange", cinemaState.visibilityHandler);
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
        document.removeEventListener("visibilitychange", state.visibilityHandler);
    }

    function exitCinema() {
        if (!cinemaState) {
            return;
        }
        var state = cinemaState;
        cinemaState = null;
        pendingCinemaFromHash = false;
        teardownCinemaState(state);
        document.body.classList.remove("is-cinema", "is-cinema-raid");
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
            document.body.classList.add("is-measuring");
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
        elements.watchButton.addEventListener("click", function () {
            var targetKey = followTarget && followTarget.kind === "player"
                ? followTarget.id
                : "";
            enterCinema(targetKey);
        });
        elements.cinemaExit.addEventListener("click", exitCinema);
        elements.cinemaStayTarget.addEventListener("click", cinemaStayOnTarget);
        document.addEventListener("keydown", function (event) {
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
            button.addEventListener("click", function () {
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
        tooltip.textContent = player.displayName;
        if (player.inBed === true) {
            var sleeping = document.createElement("span");
            sleeping.className = "player-tooltip-state";
            sleeping.textContent = " · ☾ Sleeping";
            tooltip.appendChild(sleeping);
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
        var color = window.getComputedStyle(document.documentElement)
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
        var rows = [positionPopupRow(player.x, player.z)];
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
                label: "Watch"
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
        if (!firstPlayersPayloadReceived || !isDungeonEntrancePoiGroup(record.group)) {
            return 0;
        }
        return nearbyPlayers(record.x, record.z, 15).length;
    }

    function buildPoiPopup(record) {
        var rows = [];
        var resource = isResourcePoiGroup(record.group);
        var stateText = resource ? resourcePoiStateText(record) : "";
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
        return popupShell({
            feed: "pois",
            glyph: POI_GROUPS[record.group].glyph,
            kicker: poiPopupKicker(record.group),
            rows: rows,
            surveyUnixMs: resource ? resourcePoiSurveyUnixMs(record.group) : 0,
            title: record.title
        });
    }

    function buildPinPopup(pin) {
        var rows = [];
        if (pin.checked) {
            rows.push({ label: "Status", value: "✓ charted-off" });
        }
        if (pin.author) {
            rows.push({ label: "Charted by", value: pin.author });
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
        return popupShell({
            actions: [{
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
            }],
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

    function buildEntityPopup(entity) {
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
        return {
            available: available,
            explored: poi.explored !== false,
            group: group,
            latLng: worldToLatLng(Number(poi.x), Number(poi.z)),
            memberCount: memberCount,
            minedPct: minedPct,
            name: typeof poi.name === "string" ? poi.name : "",
            placed: poi.placed !== false,
            state: state,
            title: resource ? resourcePoiTitle(group) : prettifyPoiName(poi.name),
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

    function createPoiMarker(record) {
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

    function createPoiClusterMarker(group, bucket) {
        var center = L.latLng(bucket.latitude / bucket.weight, bucket.longitude / bucket.weight);
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
        return marker;
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
        if (!useClusters) {
            records.forEach(function (record) {
                createPoiMarker(record).addTo(layer);
            });
            return;
        }

        var buckets = Object.create(null);
        records.forEach(function (record) {
            var point = map.latLngToContainerPoint(record.latLng);
            var cell = Math.floor(point.x / POI_CLUSTER_GRID_PX) + ":" +
                Math.floor(point.y / POI_CLUSTER_GRID_PX);
            var weight = record.memberCount || 1;
            if (!buckets[cell]) {
                buckets[cell] = {
                    activeWeight: 0,
                    count: 0,
                    exploredWeight: 0,
                    latitude: 0,
                    longitude: 0,
                    records: [],
                    weight: 0
                };
            }
            buckets[cell].count += weight;
            if (record.explored) {
                buckets[cell].exploredWeight += weight;
            }
            if (!resourcePoiIsDimmed(record)) {
                buckets[cell].activeWeight += weight;
            }
            buckets[cell].latitude += record.latLng.lat * weight;
            buckets[cell].longitude += record.latLng.lng * weight;
            buckets[cell].records.push(record);
            buckets[cell].weight += weight;
        });
        Object.keys(buckets).forEach(function (cell) {
            createPoiClusterMarker(group, buckets[cell]).addTo(layer);
        });
    }

    function renderPoiLayers() {
        if (!map) {
            return;
        }

        var useClusters = map.getZoom() < POI_CLUSTER_ZOOM;
        POI_GROUP_ORDER.forEach(function (group) {
            renderPoiGroup(group, useClusters);
        });
    }

    function getLazyPoiState(group) {
        var state = lazyPoiStates.get(group);
        if (!state) {
            state = {
                lastFetchAt: 0,
                loaded: false,
                requestPending: false,
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
            availablePoiGroups.has(group) &&
            metadata && metadata.inline === false);
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
        if (!lazyPoiLoadingAllowed(group)) {
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
            if (state.requestPending || state.timer) {
                return;
            }
            if (!state.loaded || (resource && state.scanning)) {
                state.scanning = resource;
                scheduleLazyPoiPoll(group, 0);
                return;
            }
            if (resource) {
                scheduleLazyPoiPoll(
                    group,
                    Math.max(
                        0,
                        state.lastFetchAt + RESOURCE_POI_REFRESH_INTERVAL_MS - Date.now()
                    )
                );
            }
        });
        updateLayerCounts();
    }

    async function loadLazyPoiGroup(group) {
        if (!lazyPoiLoadingAllowed(group)) {
            stopLazyPoiPolling(group);
            return;
        }

        var state = getLazyPoiState(group);
        var resource = isResourcePoiGroup(group);
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
            if (lazyPoiStates.get(group) !== state ||
                normalizePoiGroup(payload && payload.group) !== group) {
                return;
            }

            var pois = payload && Array.isArray(payload.pois) ? payload.pois : [];
            replacePoiGroupRecords(group, pois);
            availablePoiGroups.add(group);
            var metadata = poiGroupMeta.get(group);
            var count = Math.floor(Number(payload.count));
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
            if (metadata) {
                metadata.count = count;
                if (resource) {
                    metadata.scanUnixMs = scanUnixMs;
                }
            }
            state.lastFetchAt = Date.now();
            state.loaded = resource ? scanUnixMs > 0 : true;
            state.scanUnixMs = resource ? scanUnixMs : 0;
            state.scanning = resource && payload && payload.scanning === true;
            if (resource) {
                nextDelay = state.scanning
                    ? RESOURCE_POI_POLL_INTERVAL_MS
                    : RESOURCE_POI_REFRESH_INTERVAL_MS;
            }
            feedLastUpdated.pois = Date.now();
            setFeedState("pois", true);
            renderPoiGroup(group, map.getZoom() < POI_CLUSTER_ZOOM);
            syncLayerVisibility();
            if (searchControlElement && searchControlElement.classList.contains("is-open")) {
                renderMapSearchResults();
            }
        } catch (error) {
            state.scanning = resource && !state.loaded;
        } finally {
            state.requestPending = false;
            if (resource && lazyPoiStates.get(group) === state &&
                lazyPoiLoadingAllowed(group)) {
                scheduleLazyPoiPoll(group, nextDelay);
            }
            updateLayerCounts();
        }
    }

    async function loadPoisForCurrentView() {
        var accessKey = hasLiveAccess() ? "live" : "public";
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
                requestView !== (hasLiveAccess() ? "live" : "public")) {
                return;
            }

            var groups = payload && Array.isArray(payload.groups) ? payload.groups : [];
            groups.forEach(function (entry) {
                var group = normalizePoiGroup(entry && entry.key);
                if (!group) {
                    return;
                }

                var count = Math.floor(Number(entry.count));
                var scanUnixMs = Math.floor(Number(entry.scanUnixMs));
                var metadata = {
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
                        : 0
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
                            category: POI_GROUPS[group].category,
                            count: count,
                            inline: true,
                            key: group,
                            label: POI_GROUPS[group].label,
                            resource: POI_GROUPS[group].resource === true,
                            scanUnixMs: 0
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
                requestView === (hasLiveAccess() ? "live" : "public")) {
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
            normalized.push({
                deathAgeSampledAt: deathAgeSampledAt,
                deathAgeSec: deathAgeSec,
                group: group,
                id: entityId,
                isNewestDeath: false,
                owner: typeof entity.owner === "string" ? entity.owner.trim() : "",
                prefab: prefab,
                rotYDeg: finiteNumberOrNull(entity.rotYDeg),
                tag: typeof entity.tag === "string" ? entity.tag.trim() : "",
                trailKey: (group === "ship" || group === "cart") && entityId
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

    function entityMarkerTitle(entity) {
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

        var color = window.getComputedStyle(document.documentElement)
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

    function renderEntityPayload(entities) {
        var popupSource = map && map._popup ? map._popup._source : null;
        var reopenEntityKey = popupSource &&
            (popupSource._voPopupKind === "ship" || popupSource._voPopupKind === "cart")
            ? popupSource._voTrailKey
            : "";
        var reopenEntityId = popupSource ? popupSource._voEntityId : "";
        clearEntityLayers(true);
        var reopenMarker = null;
        entities.forEach(function (entity) {
            renderWardRadius(entity);
            var markerMarkup = iconMarkup(
                entity.group,
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
            var marker = L.marker(worldToLatLng(entity.x, entity.z), {
                icon: icon,
                title: markerTitle
            });
            var tooltipContent = document.createElement("span");
            tooltipContent.textContent = markerTitle;
            bindMarkerTooltip(marker, tooltipContent, {
                fallbackGlyph: ENTITY_GROUPS[entity.group].glyph,
                iconKey: entity.group,
                title: markerTitle
            }, {
                className: "map-tooltip entity-tooltip",
                direction: "top",
                offset: [0, -11],
                opacity: 1
            });
            var record = { entity: entity, marker: marker };
            bindMapPopup(marker, function () {
                return buildEntityPopup(record.entity);
            }, {
                entityId: entity.id,
                kind: entity.group,
                trailKey: entity.trailKey,
                trailKind: entity.group === "ship" || entity.group === "cart"
                    ? entity.group
                    : ""
            });
            marker.addTo(entityLayers.get(entity.group));
            if (entity.group === "portal" && entity.id) {
                portalMarkerRecords.set(entity.id, record);
            }
            if ((entity.group === "ship" || entity.group === "cart") && entity.trailKey) {
                entityMarkerRecords.set(entity.trailKey, record);
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
            if ((entity.group === "ship" || entity.group === "cart") &&
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
        if (!map || !hasLiveAccess() || entityAvailability === "unavailable" ||
            entityRequestPending || !entityDataIsNeeded()) {
            return;
        }

        entityPollTimer = window.setTimeout(
            pollEntities,
            immediate ? 0 : ENTITIES_POLL_INTERVAL_MS
        );
    }

    async function pollEntities() {
        if (!map || !hasLiveAccess() || entityRequestPending ||
            entityAvailability === "unavailable") {
            return;
        }

        entityRequestPending = true;
        try {
            var response = await fetch(authorizedUrl("/api/entities"), {
                cache: "no-store",
                credentials: "same-origin"
            });
            if (response.status === 404) {
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
            if (entityAvailability === "unavailable") {
                return;
            }
            var wasAvailable = entityAvailability === "available";
            entityAvailability = "available";
            feedLastUpdated.entities = Date.now();
            setFeedState("entities", true);
            var entities = normalizeEntityPayload(payload);
            recordEntityTrails(entities);
            latestEntities = entities;
            derivePortalPairs(latestEntities);
            updateLayerCounts();
            var nextRevision = payload && payload.revision != null
                ? String(payload.revision)
                : "";
            if (nextRevision !== entityRevision) {
                renderEntityPayload(entities);
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
            setFeedState("entities", false);
        } finally {
            entityRequestPending = false;
            updateEntityPolling(false);
        }
    }

    function updateEntityFocusPolling(immediate) {
        window.clearTimeout(entityFocusPollTimer);
        entityFocusPollTimer = 0;
        if (!map || !hasLiveAccess() || entityAvailability === "unavailable" ||
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
        if (!followTarget ||
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
            record.marker.setLatLng(target);
            var timestamp = Number(payload.focus.unixMs);
            appendTrailSample(
                targetKey,
                entity.group,
                entity.x,
                entity.z,
                Number.isFinite(timestamp) ? timestamp : Date.now()
            );
            updateShipHeadingLine(entity);
            for (var index = 0; index < latestEntities.length; index++) {
                if (latestEntities[index].id === entity.id) {
                    latestEntities[index] = entity;
                    break;
                }
            }
            feedLastUpdated.entities = Date.now();
            setFeedState("entities", true);
            map.panTo(target, { animate: false });
            renderTrails();
            refreshOpenPopupContent();
        } catch (error) {
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
            var raidColor = window.getComputedStyle(document.documentElement)
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

    async function pollPins() {
        if (!map || !pinLayer) {
            return;
        }

        try {
            var payload = await fetchJson("/api/pins");
            var pins = payload && Array.isArray(payload.pins) ? payload.pins : [];
            var nextPins = [];
            var pinsWereLoaded = feedLastUpdated.pins > 0;
            pinLayer.clearLayers();
            pins.forEach(function (pin) {
                if (!pin || !Number.isFinite(Number(pin.x)) || !Number.isFinite(Number(pin.z))) {
                    return;
                }

                var isChecked = pin.checked === true;
                var pinRecord = {
                    author: typeof pin.author === "string" ? pin.author.trim() : "",
                    checked: isChecked,
                    name: typeof pin.name === "string" && pin.name.trim() ? pin.name.trim() : "Pin",
                    x: Number(pin.x),
                    z: Number(pin.z)
                };
                var icon = L.divIcon({
                    className: "pin-div-icon" + (isChecked ? " is-checked" : ""),
                    html: '<span class="pin-marker-shell"><span class="pin-marker-glyph">' +
                        iconMarkup(isChecked ? "pin_checked" : "pin", isChecked ? "✓" : "•") +
                        "</span></span>",
                    iconAnchor: [10, 19],
                    iconSize: [20, 20]
                });
                var marker = L.marker(worldToLatLng(pinRecord.x, pinRecord.z), {
                    icon: icon,
                    title: pinRecord.name
                });
                bindMarkerTooltip(marker, createPinTooltip(pinRecord), {
                    fallbackGlyph: isChecked ? "✓" : "•",
                    iconKey: isChecked ? "pin_checked" : "pin",
                    title: pinRecord.name
                }, {
                    className: "map-tooltip pin-tooltip",
                    direction: "top",
                    offset: [0, -17],
                    opacity: 1
                });
                bindMapPopup(marker, function () {
                    return buildPinPopup(pinRecord);
                }, { kind: "pin" });
                marker.addTo(pinLayer);
                pinRecord.latLng = marker.getLatLng();
                pinRecord.marker = marker;
                nextPins.push(pinRecord);
            });
            latestPins = nextPins;
            feedLastUpdated.pins = Date.now();
            setFeedState("pins", true);
            if (pinsWereLoaded) {
                updateLayerCounts();
            } else {
                renderLayerRows();
            }
        } catch (error) {
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

    function updateView(view) {
        var nextView = view === "admin" || view === "shared" ? view : "public";
        elements.publicViewBadge.hidden = nextView === "admin";
        elements.publicViewBadge.textContent = nextView === "shared"
            ? "Shared view"
            : "Public view";
        elements.watchButton.hidden = nextView === "public";
        if (nextView === currentView) {
            return;
        }

        currentView = nextView;
        dismissMapContextMenu();
        if (currentView === "public" && cinemaState) {
            exitCinema();
        }
        syncMapPingControl();
        if (map) {
            loadPoisForCurrentView();
            renderLayerRows();
            syncLayerVisibility();
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

        feedLastUpdated.status = Date.now();
        latestStatusSnapshotStale = status.stale === true;
        setFeedState("status", true);
        elements.serverName.textContent = textOrDash(status.serverName);
        elements.worldName.textContent = textOrDash(status.worldName);
        renderWorldTime(status.day, status.timeOfDay);
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
    }

    function handlePlayersPayload(payload) {
        if (!payload || typeof payload !== "object") {
            throw new Error("Invalid players payload");
        }

        feedLastUpdated.players = Date.now();
        setFeedState("players", true);
        firstPlayersPayloadReceived = true;
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
        updatePlayerMarkers(latestPlayers);
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
    }

    async function pollStatus() {
        if (eventSourceOpen) {
            return;
        }

        try {
            handleStatusPayload(await fetchJson("/api/status"));
        } catch (error) {
            setFeedState("status", false);
        }
    }

    async function pollPlayers() {
        if (eventSourceOpen) {
            return;
        }

        try {
            handlePlayersPayload(await fetchJson("/api/players"));
        } catch (error) {
            setFeedState("players", false);
        }
    }

    function resumePollingAfterEventStream() {
        pollStatus();
        pollPlayers();
        if (consoleIsActive()) {
            pollConsoleLog();
        }
    }

    function scheduleEventStreamRetry() {
        if (typeof window.EventSource !== "function" || eventSourceRetryTimer) {
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
        resumePollingAfterEventStream();
        scheduleEventStreamRetry();
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
        if (typeof window.EventSource !== "function" || eventSource) {
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
        source.addEventListener("open", function () {
            if (source !== eventSource) {
                return;
            }
            eventSourceOpen = true;
            eventSourceRetryDelay = SSE_RETRY_INITIAL_MS;
        });
        source.addEventListener("players", function (event) {
            readEventStreamPayload(source, event, handlePlayersPayload);
        });
        source.addEventListener("status", function (event) {
            readEventStreamPayload(source, event, handleStatusPayload);
        });
        source.addEventListener("ping", function (event) {
            readEventStreamPayload(source, event, handlePingPayload);
        });
        source.addEventListener("log", function (event) {
            readEventStreamPayload(source, event, function (payload) {
                eventSourceLogFlowing = true;
                handleConsoleLogPayload(payload, true);
            });
        });
        source.addEventListener("error", function () {
            disconnectEventStream(source);
        });
    }

    function startPolling(task, interval) {
        async function run() {
            await task();
            window.setTimeout(run, interval);
        }

        run();
    }

    bindCinemaEvents();
    bindConsoleEvents();
    bindPopupDocumentEvents();
    elements.sidebarState.addEventListener("change", function () {
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
    savedBadgeTimer = window.setInterval(renderSavedBadge, SAVED_BADGE_REFRESH_MS);
    startPolling(pollStatus, POLL_INTERVAL_MS);
    startPolling(pollPlayers, POLL_INTERVAL_MS);
    connectEventStream();
    window.addEventListener("beforeunload", function () {
        if (cinemaState) {
            teardownCinemaState(cinemaState);
        }
        window.clearTimeout(eventSourceRetryTimer);
        window.clearTimeout(hashUpdateTimer);
        window.clearTimeout(entityFocusPollTimer);
        window.clearTimeout(renderStatusFailureTimer);
        stopAllLazyPoiPolling();
        window.clearInterval(popupRefreshTimer);
        window.clearInterval(raidProgressTimer);
        window.clearInterval(layersStalenessTimer);
        window.clearInterval(savedBadgeTimer);
        window.clearTimeout(dayToastTimer);
        window.cancelAnimationFrame(minimapFrame);
        activePingMarkers.forEach(function (record) {
            window.clearTimeout(record.timer);
        });
        if (eventSource) {
            eventSource.close();
        }
    });
}());
