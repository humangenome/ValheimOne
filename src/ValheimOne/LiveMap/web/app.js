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
    var SSE_RETRY_INITIAL_MS = 5000;
    var SSE_RETRY_MAX_MS = 60000;
    var TRAIL_MAX_AGE_MS = 30 * 60 * 1000;
    var TRAIL_TARGET_AGE_MS = 15 * 60 * 1000;
    var TRAIL_ALL_PLAYERS_AGE_MS = 5 * 60 * 1000;
    var TRAIL_EVICT_AGE_MS = 10 * 60 * 1000;
    var TRAIL_MAX_POINTS = 900;
    var TRAIL_BUCKET_COUNT = 10;
    var SHIP_MATCH_DISTANCE = 40;
    var LAYER_STORAGE_KEY = "vo-livemap-layers";
    var TAB_SESSION_KEY = "vo-livemap-active-tab";
    var CONSOLE_CATEGORY_ORDER = ["server", "players", "moderation", "world", "diagnostics"];
    var CONSOLE_CATEGORY_LABELS = {
        server: "Server",
        players: "Players",
        moderation: "Moderation",
        world: "World",
        diagnostics: "Diagnostics"
    };

    var POI_GROUP_ORDER = [
        "spawn",
        "trader",
        "boss",
        "dungeon",
        "spawner",
        "misc",
        "other"
    ];

    var POI_GROUPS = {
        spawn: { label: "Spawn", glyph: "⌂" },
        trader: { label: "Trader", glyph: "◉" },
        boss: { label: "Boss altars", glyph: "☠" },
        dungeon: { label: "Dungeons", glyph: "∩" },
        spawner: { label: "Spawners", glyph: "•" },
        misc: { label: "Misc", glyph: "◆" },
        other: { label: "Other", glyph: "◇" }
    };

    var ENTITY_GROUP_ORDER = ["ship", "cart", "portal"];
    var ENTITY_GROUPS = {
        ship: { label: "Ships", glyph: "⛵" },
        cart: { label: "Carts", glyph: "▣" },
        portal: { label: "Portals", glyph: "◊" }
    };

    var LAYER_DEFAULTS = {
        players: true,
        pins: true,
        trails: false,
        spawn: true,
        trader: true,
        boss: true,
        dungeon: false,
        spawner: false,
        misc: false,
        other: false,
        fog: true,
        ship: true,
        cart: true,
        portal: true,
        legendCollapsed: false
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
    var mapMetrics = null;
    var worldBounds = null;
    var followedPlayer = null;
    var followPill = null;
    var playerLayer = null;
    var pinLayer = null;
    var trailLayer = null;
    var trailBuffers = new Map();
    var selectedTrailTargets = new Map();
    var openPopupTrailTarget = null;
    var popupRefreshTimer = 0;
    var nextShipTrackId = 1;
    var poiLayers = new Map();
    var poiRecords = new Map();
    var availablePoiGroups = new Set();
    var entityLayers = new Map();
    var entityAvailability = "unknown";
    var entityRequestPending = false;
    var entityPollTimer = 0;
    var entityRevision = null;
    var entityMarkerRecords = new Map();
    var raidCircle = null;
    var currentRaidEvent = null;
    var layerSettings = loadLayerSettings();
    var layersRows = null;
    var legendContent = null;
    var currentView = null;
    var lastPoiRequestedView = null;
    var poiRequestSequence = 0;
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
    var feedLastUpdated = {
        entities: 0,
        pins: 0,
        players: 0,
        pois: 0,
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
        confirmBackdrop: document.getElementById("console-confirm-backdrop"),
        confirmCancel: document.getElementById("console-confirm-cancel"),
        confirmMessage: document.getElementById("console-confirm-message"),
        confirmSubmit: document.getElementById("console-confirm-submit"),
        consoleLog: document.getElementById("console-log"),
        consolePane: document.getElementById("console-pane"),
        consoleResume: document.getElementById("console-resume"),
        consoleTab: document.getElementById("console-tab"),
        dayNumber: document.getElementById("day-number"),
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
        suggestionList: document.getElementById("console-suggestions"),
        tabList: document.getElementById("view-tabs"),
        consolePlayerCount: document.getElementById("console-player-count"),
        consolePlayerList: document.getElementById("console-player-list"),
        worldClock: document.getElementById("world-clock"),
        worldName: document.getElementById("world-name")
    };

    function authorizedUrl(path) {
        if (!token) {
            return path;
        }

        return path + (path.indexOf("?") === -1 ? "?" : "&") +
            "token=" + encodeURIComponent(token);
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
        pollConsoleLog();
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

    async function pollConsoleLog() {
        if (!consoleIsActive() || consoleLogRequestPending ||
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
    }

    function textOrDash(value) {
        return typeof value === "string" && value.trim() ? value : "—";
    }

    function loadLayerSettings() {
        var settings = {};
        Object.keys(LAYER_DEFAULTS).forEach(function (key) {
            settings[key] = LAYER_DEFAULTS[key];
        });

        try {
            var saved = JSON.parse(window.localStorage.getItem(LAYER_STORAGE_KEY));
            if (saved && typeof saved === "object") {
                Object.keys(LAYER_DEFAULTS).forEach(function (key) {
                    if (typeof saved[key] === "boolean") {
                        settings[key] = saved[key];
                    }
                });
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

    function popupStalenessText(feed) {
        var updatedAt = feedLastUpdated[feed] || Date.now();
        var seconds = Math.max(0, Math.floor((Date.now() - updatedAt) / 1000));
        if (seconds < 60) {
            return "as of " + seconds + "s ago";
        }

        return "as of " + Math.floor(seconds / 60) + "m ago";
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
            valueText.textContent = row.value;
            value.appendChild(valueText);
            if (typeof row.copy === "string") {
                var copy = document.createElement("button");
                copy.type = "button";
                copy.className = "vo-copy";
                copy.textContent = "Copy";
                copy.setAttribute("data-copy", row.copy);
                copy.setAttribute("aria-label", "Copy " + row.label.toLowerCase());
                value.appendChild(copy);
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
        footer.setAttribute("data-feed", options.feed);
        footer.textContent = popupStalenessText(options.feed);
        shell.appendChild(footer);
        return shell;
    }

    function bindMapPopup(marker, builder, metadata) {
        marker._voPopupKind = metadata && metadata.kind ? metadata.kind : "";
        marker._voTrailKind = metadata && metadata.trailKind ? metadata.trailKind : "";
        marker._voTrailKey = metadata && metadata.trailKey ? metadata.trailKey : "";
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

        var footers = map._popup.getElement().querySelectorAll(".vo-popup-footer[data-feed]");
        Array.prototype.forEach.call(footers, function (footer) {
            footer.textContent = popupStalenessText(footer.getAttribute("data-feed"));
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

    function toggleSelectedTrail(kind, key) {
        var id = trailTargetId(kind, key);
        if (selectedTrailTargets.has(id)) {
            selectedTrailTargets.delete(id);
        } else {
            selectedTrailTargets.set(id, { kind: kind, key: key });
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
                copyText(copyButton.getAttribute("data-copy")).then(function () {
                    flashCopyButton(copyButton);
                }).catch(function () {
                    return;
                });
                return;
            }

            var actionButton = target.closest(".vo-popup-action[data-popup-action]");
            if (!actionButton) {
                return;
            }

            event.preventDefault();
            var action = actionButton.getAttribute("data-popup-action");
            var key = actionButton.getAttribute("data-target-key") || "";
            if (action === "follow") {
                if (followedPlayer === key) {
                    clearFollow();
                } else {
                    followPlayer(key);
                }
            } else if (action === "trail") {
                toggleSelectedTrail(
                    actionButton.getAttribute("data-trail-kind") || "player",
                    key
                );
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

    function appendTrailSample(key, kind, x, z, timestamp) {
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

    function evictTrailBuffers(timestamp) {
        trailBuffers.forEach(function (buffer, key) {
            if (timestamp - buffer.lastSeen <= TRAIL_EVICT_AGE_MS) {
                return;
            }

            trailBuffers.delete(key);
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

    function recordPlayerTrails(players) {
        var timestamp = Date.now();
        players.forEach(function (player) {
            appendTrailSample(player.key, "player", player.x, player.z, timestamp);
        });
        evictTrailBuffers(timestamp);
    }

    function recordEntityTrails(entities) {
        var timestamp = Date.now();
        entities.forEach(function (entity) {
            if (entity.group === "ship" && entity.trailKey) {
                appendTrailSample(entity.trailKey, "ship", entity.x, entity.z, timestamp);
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
        var token = kind === "ship" ? "--frost" : "--accent";
        var color = window.getComputedStyle(document.documentElement).getPropertyValue(token).trim();
        return color || (kind === "ship" ? "#7eb1d6" : "#d9b168");
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
        if (followedPlayer) {
            addVisibleTrailTarget(targets, "player", followedPlayer, TRAIL_TARGET_AGE_MS);
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
                    player.key,
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
            openPopupTrailTarget = source && source._voTrailKind && source._voTrailKey
                ? { kind: source._voTrailKind, key: source._voTrailKey }
                : null;
            window.clearInterval(popupRefreshTimer);
            refreshOpenPopupFooter();
            popupRefreshTimer = window.setInterval(refreshOpenPopupFooter, 5000);
            renderTrails();
        });
        map.on("popupclose", function () {
            openPopupTrailTarget = null;
            window.clearInterval(popupRefreshTimer);
            popupRefreshTimer = 0;
            renderTrails();
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
        var record = followedPlayer ? markerRecords.get(followedPlayer) : null;
        followPill.hidden = !record;
        followPill.textContent = record
            ? "Following " + record.player.displayName + " — click to release"
            : "";
    }

    function renderWorldTime(day, timeOfDay) {
        var dayNumber = Number(day);
        elements.dayNumber.textContent = "Day " +
            (Number.isFinite(dayNumber) ? Math.max(0, Math.floor(dayNumber)) : "—");

        var fraction = Number(timeOfDay);
        if (!Number.isFinite(fraction)) {
            elements.worldClock.textContent = "--:--";
            return;
        }

        fraction = ((fraction % 1) + 1) % 1;
        var totalMinutes = Math.floor(fraction * 24 * 60);
        var hours = Math.floor(totalMinutes / 60);
        var minutes = totalMinutes % 60;
        elements.worldClock.textContent = padTwo(hours) + ":" + padTwo(minutes);

        var isDaytime = fraction >= 0.15 && fraction < 0.85;
        elements.skyIndicator.textContent = isDaytime ? "☀" : "☾";
        elements.skyIndicator.classList.toggle("is-sun", isDaytime);
        elements.skyIndicator.classList.toggle("is-moon", !isDaytime);
        elements.skyIndicator.setAttribute("aria-label", isDaytime ? "Daytime" : "Nighttime");
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
        if (mapStatus && mapStatus.state === "ready") {
            elements.mapStatus.hidden = true;
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
            nextMetrics.textureSize === mapMetrics.textureSize &&
            nextMetrics.pixelSize === mapMetrics.pixelSize) {
            return;
        }

        var center = map.getCenter();
        var zoom = Math.max(map.getMinZoom(), Math.min(nextMetrics.maximumZoom, map.getZoom()));
        mapMetrics.textureSize = nextMetrics.textureSize;
        mapMetrics.pixelSize = nextMetrics.pixelSize;
        mapMetrics.maximumZoom = nextMetrics.maximumZoom;
        mapMetrics.unitsPerPixel = nextMetrics.unitsPerPixel;
        map.setMaxZoom(nextMetrics.maximumZoom);
        tileLayer.options.maxZoom = nextMetrics.maximumZoom;
        tileLayer.options.maxNativeZoom = nextMetrics.maximumZoom;
        tileLayer.removeFrom(map);
        tileLayer.addTo(map);
        map.setView(center, zoom, { animate: false });
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
            zoomControl: true
        });

        map.createPane("fogPane");
        map.getPane("fogPane").style.zIndex = "350";
        map.getPane("fogPane").style.pointerEvents = "none";
        map.createPane("trailPane");
        map.getPane("trailPane").style.zIndex = "380";
        map.getPane("trailPane").style.pointerEvents = "none";

        // With fog active, keep an ocean-colored cover over the pane until the
        // fog image has loaded so the unfogged world never flashes on first paint.
        if (fogAvailable) {
            showFogCover();
        }

        var tileTemplate = authorizedUrl("/tiles/{z}/{x}-{y}.png");
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
        initialiseDataLayers();
        bindMapPopupEvents();
        ensureFollowPill();
        createLayersControl();
        map.setView(worldToLatLng(0, 0), Math.max(0, overviewZoom - 1));
        map.on("dragstart", clearFollow);
        map.on("zoomend", renderPoiLayers);
        syncLayerVisibility();
        updatePlayerMarkers(latestPlayers);
        loadPoisForCurrentView();
        applyFogStatus();
        applyRaidEvent(currentRaidEvent);
        ensureEntityFeed();
        startPinsPolling();
    }

    function initialiseDataLayers() {
        playerLayer = L.layerGroup();
        pinLayer = L.layerGroup();
        trailLayer = L.layerGroup().addTo(map);
        POI_GROUP_ORDER.forEach(function (group) {
            poiLayers.set(group, L.layerGroup());
            poiRecords.set(group, []);
        });
        ENTITY_GROUP_ORDER.forEach(function (group) {
            entityLayers.set(group, L.layerGroup());
        });
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

    function worldDistanceToMap(distance) {
        if (!mapMetrics) {
            return 0;
        }

        return Math.abs(distance / mapMetrics.pixelSize * mapMetrics.unitsPerPixel);
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
                    container.classList.toggle("is-collapsed", isCollapsed);
                    layersRows.hidden = isCollapsed;
                    toggle.setAttribute("aria-expanded", String(!isCollapsed));
                    chevron.textContent = isCollapsed ? "⌄" : "⌃";
                }

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
        appendLayerRow("players", "Players", "●", "players");
        appendLayerRow("pins", "Pins", "⌖", "pins");
        appendLayerRow("trails", "Trails", "〰", "trails");

        POI_GROUP_ORDER.forEach(function (group) {
            if (availablePoiGroups.has(group)) {
                appendLayerRow(group, POI_GROUPS[group].label, POI_GROUPS[group].glyph, group);
            }
        });

        if (fogAvailable) {
            appendLayerRow("fog", "Fog", "≈", "fog");
        }

        if (entityLayersAreAvailable()) {
            ENTITY_GROUP_ORDER.forEach(function (group) {
                appendLayerRow(
                    group,
                    ENTITY_GROUPS[group].label,
                    ENTITY_GROUPS[group].glyph,
                    group
                );
            });
        }

        appendLegendBlock();
    }

    function appendLayerRow(key, labelText, glyph, swatchClass) {
        var label = document.createElement("label");
        var checkbox = document.createElement("input");
        var swatch = document.createElement("span");
        var text = document.createElement("span");

        label.className = "layer-row";
        checkbox.type = "checkbox";
        checkbox.checked = layerSettings[key];
        checkbox.setAttribute("aria-label", "Show " + labelText);
        checkbox.addEventListener("change", function () {
            layerSettings[key] = checkbox.checked;
            saveLayerSettings();
            syncLayerVisibility();
            if (Object.prototype.hasOwnProperty.call(ENTITY_GROUPS, key)) {
                updateEntityPolling(true);
            }
        });

        swatch.className = "layer-swatch layer-swatch-" + swatchClass;
        swatch.textContent = glyph;
        swatch.setAttribute("aria-hidden", "true");
        text.className = "layer-label";
        text.textContent = labelText;

        label.appendChild(checkbox);
        label.appendChild(swatch);
        label.appendChild(text);
        layersRows.appendChild(label);
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
        swatch.textContent = glyph;
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
        ENTITY_GROUP_ORDER.forEach(function (group) {
            setLayerVisible(
                entityLayers.get(group),
                entityLayersAreAvailable() && layerSettings[group]
            );
        });
        renderTrails();
        renderLegend();
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
            var key;
            if (!rawName) {
                key = "anonymous:" + anonymousIndex;
                anonymousIndex++;
            } else {
                nameOccurrences[rawName] = (nameOccurrences[rawName] || 0) + 1;
                key = "named:" + rawName + ":" + nameOccurrences[rawName];
            }

            return {
                anonymous: !rawName,
                displayName: rawName || "Explorer",
                key: key,
                name: rawName,
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
        var motion = derivedMotion(record.player.key);
        var showHeading = Boolean(motion && motion.speedMps >= 0.3);
        chevron.hidden = !showHeading;
        if (showHeading) {
            chevron.style.transform = "rotate(" + motion.headingDeg.toFixed(1) + "deg)";
        }
    }

    function createPlayerMarker(player) {
        var tooltipContent = document.createElement("span");
        tooltipContent.textContent = player.displayName;
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
        marker.bindTooltip(tooltipContent, {
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
            trailKey: player.key,
            trailKind: "player"
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
            updatePlayerMarkerMotion(record);
        });

        markerRecords.forEach(function (record, key) {
            if (activeKeys.has(key)) {
                return;
            }

            cancelAnimationFrame(record.animationFrame);
            playerLayer.removeLayer(record.marker);
            markerRecords.delete(key);
            if (followedPlayer === key) {
                followedPlayer = null;
                followWasCleared = true;
            }
        });

        updateFollowStyles();
        updateFollowPill();
        renderTrails();
        if (followWasCleared) {
            renderPlayerList(latestPlayers);
        }
    }

    function animateMarker(record, target) {
        cancelAnimationFrame(record.animationFrame);
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
            if (followedPlayer === record.player.key) {
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

    function followPlayer(key) {
        var record = markerRecords.get(key);
        if (!record || !map) {
            return;
        }

        followedPlayer = key;
        updateFollowStyles();
        updateFollowPill();
        renderPlayerList(latestPlayers);
        renderTrails();
        refreshOpenPopupContent();
        map.panTo(record.marker.getLatLng(), {
            animate: true,
            duration: 0.35
        });

        if (window.matchMedia("(max-width: 759px)").matches) {
            elements.sidebarState.checked = false;
        }
    }

    function clearFollow() {
        if (!followedPlayer) {
            return;
        }

        followedPlayer = null;
        updateFollowStyles();
        updateFollowPill();
        renderPlayerList(latestPlayers);
        renderTrails();
        refreshOpenPopupContent();
    }

    function updateFollowStyles() {
        markerRecords.forEach(function (record, key) {
            var markerElement = record.marker.getElement();
            if (markerElement) {
                markerElement.classList.toggle("is-followed", key === followedPlayer);
            }
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
            var name = document.createElement("span");
            var coordinates = document.createElement("span");

            button.type = "button";
            button.className = "player-button";
            button.classList.toggle("is-followed", player.key === followedPlayer);
            button.addEventListener("click", function () {
                followPlayer(player.key);
            });

            identity.className = "player-identity";
            dot.className = "player-dot";
            name.className = "player-name";
            name.textContent = player.displayName;
            coordinates.className = "player-coordinates";
            coordinates.textContent = "X " + Math.round(player.x) + " · Z " + Math.round(player.z);

            identity.appendChild(dot);
            identity.appendChild(name);
            button.appendChild(identity);
            button.appendChild(coordinates);
            item.appendChild(button);
            elements.playerList.appendChild(item);
        });
    }

    function normalizePoiGroup(group) {
        var normalized = typeof group === "string" ? group.trim().toLowerCase() : "";
        return Object.prototype.hasOwnProperty.call(POI_GROUPS, normalized) ? normalized : "other";
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

    function headingLabel(degrees) {
        var normalized = (degrees + 360) % 360;
        var directions = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        return directions[Math.round(normalized / 45) % directions.length] +
            " · " + Math.round(normalized) + "°";
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
        var motion = derivedMotion(player.key);
        var rows = [positionPopupRow(player.x, player.z)];
        if (motion) {
            rows.push({
                label: "Speed",
                value: motion.speedMps.toFixed(1) + " m/s · " + playerMovementMode(player, motion)
            });
            if (motion.speedMps >= 0.3) {
                rows.push({ label: "Heading", value: headingLabel(motion.headingDeg) });
            }
        }

        var trailSelected = trailIsSelected("player", player.key);
        return popupShell({
            actions: [{
                action: "follow",
                key: player.key,
                label: followedPlayer === player.key ? "Unfollow" : "Follow"
            }, {
                action: "trail",
                active: trailSelected,
                key: player.key,
                kind: "player",
                label: trailSelected ? "Hide trail" : "Trail 15m"
            }],
            feed: "players",
            glyph: "●",
            kicker: "PLAYER",
            rows: rows,
            title: player.displayName
        });
    }

    function poiPopupKicker(group) {
        var labels = {
            boss: "BOSS ALTAR",
            dungeon: "DUNGEON",
            misc: "POINT OF INTEREST",
            other: "POINT OF INTEREST",
            spawn: "SPAWN",
            spawner: "SPAWNER",
            trader: "TRADER"
        };
        return labels[group] || "POINT OF INTEREST";
    }

    function buildPoiPopup(record) {
        return popupShell({
            feed: "pois",
            glyph: POI_GROUPS[record.group].glyph,
            kicker: poiPopupKicker(record.group),
            rows: [positionPopupRow(record.x, record.z)],
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
            if (motion.speedMps >= 0.3) {
                rows.push({ label: "Heading", value: headingLabel(motion.headingDeg) });
            }
        }
        var crew = nearbyPlayers(entity.x, entity.z, 12).map(function (player) {
            return player.displayName;
        });
        rows.push({ label: "Crew", value: crew.length > 0 ? crew.join(", ") : "None nearby" });

        var trailSelected = trailIsSelected("ship", entity.trailKey);
        return popupShell({
            actions: [{
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
        return popupShell({
            feed: "entities",
            glyph: ENTITY_GROUPS.cart.glyph,
            kicker: "CART",
            rows: rows,
            title: "Cart"
        });
    }

    function buildPortalPopup(entity) {
        return popupShell({
            feed: "entities",
            glyph: ENTITY_GROUPS.portal.glyph,
            kicker: "PORTAL",
            rows: [positionPopupRow(entity.x, entity.z)],
            title: "Portal"
        });
    }

    function buildEntityPopup(entity) {
        if (entity.group === "ship") {
            return buildShipPopup(entity);
        }
        if (entity.group === "cart") {
            return buildCartPopup(entity);
        }
        return buildPortalPopup(entity);
    }

    function buildRaidPopup() {
        var event = currentRaidEvent;
        return popupShell({
            feed: "status",
            glyph: "◯",
            kicker: "RAID EVENT",
            rows: [{
                label: "Radius",
                value: Math.round(event.radius) + " m"
            }, {
                label: "Vikings inside",
                value: String(nearbyPlayers(event.x, event.z, event.radius).length)
            }],
            title: event.name
        });
    }

    function clearPoiLayers() {
        poiLayers.forEach(function (layer) {
            layer.clearLayers();
        });
        poiRecords.forEach(function (records) {
            records.length = 0;
        });
        availablePoiGroups.clear();
        renderLayerRows();
        syncLayerVisibility();
    }

    function createPoiMarker(record) {
        var icon = L.divIcon({
            className: "poi-div-icon poi-" + record.group,
            html: '<span class="poi-marker-shell" aria-hidden="true">' +
                POI_GROUPS[record.group].glyph + "</span>",
            iconAnchor: [10, 10],
            iconSize: [20, 20]
        });
        var marker = L.marker(record.latLng, {
            icon: icon,
            opacity: record.placed ? 1 : 0.55,
            title: record.title
        });
        var tooltipContent = document.createElement("span");
        tooltipContent.textContent = record.title;
        marker.bindTooltip(tooltipContent, {
            className: "map-tooltip poi-tooltip",
            direction: "top",
            offset: [0, -10],
            opacity: 1
        });
        bindMapPopup(marker, function () {
            return buildPoiPopup(record);
        }, { kind: "poi" });
        return marker;
    }

    function createPoiClusterMarker(group, bucket) {
        var center = L.latLng(bucket.latitude / bucket.records.length, bucket.longitude / bucket.records.length);
        var count = bucket.records.length;
        var icon = L.divIcon({
            className: "poi-div-icon poi-cluster-icon poi-" + group,
            html: '<span class="poi-cluster-shell" aria-hidden="true"><span>' +
                POI_GROUPS[group].glyph + '</span><strong>' + count + "</strong></span>",
            iconAnchor: [16, 12],
            iconSize: [32, 24]
        });
        var marker = L.marker(center, {
            icon: icon,
            title: count + " " + POI_GROUPS[group].label
        });
        var tooltipContent = document.createElement("span");
        tooltipContent.textContent = count + " " + POI_GROUPS[group].label;
        marker.bindTooltip(tooltipContent, {
            className: "map-tooltip poi-tooltip",
            direction: "top",
            offset: [0, -11],
            opacity: 1
        });
        return marker;
    }

    function renderPoiLayers() {
        if (!map) {
            return;
        }

        var useClusters = map.getZoom() < POI_CLUSTER_ZOOM;
        POI_GROUP_ORDER.forEach(function (group) {
            var layer = poiLayers.get(group);
            var records = poiRecords.get(group) || [];
            layer.clearLayers();
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
                if (!buckets[cell]) {
                    buckets[cell] = { latitude: 0, longitude: 0, records: [] };
                }
                buckets[cell].latitude += record.latLng.lat;
                buckets[cell].longitude += record.latLng.lng;
                buckets[cell].records.push(record);
            });
            Object.keys(buckets).forEach(function (cell) {
                createPoiClusterMarker(group, buckets[cell]).addTo(layer);
            });
        });
    }

    async function loadPoisForCurrentView() {
        if (!map || !currentView || lastPoiRequestedView === currentView) {
            return;
        }

        lastPoiRequestedView = currentView;
        var requestView = currentView;
        var requestSequence = ++poiRequestSequence;
        clearPoiLayers();

        try {
            var payload = await fetchJson("/api/pois");
            if (requestSequence !== poiRequestSequence || requestView !== currentView) {
                return;
            }

            var pois = payload && Array.isArray(payload.pois) ? payload.pois : [];
            pois.forEach(function (poi) {
                if (!poi || !Number.isFinite(Number(poi.x)) || !Number.isFinite(Number(poi.z))) {
                    return;
                }

                var group = normalizePoiGroup(poi.group);
                var title = prettifyPoiName(poi.name);
                poiRecords.get(group).push({
                    group: group,
                    latLng: worldToLatLng(Number(poi.x), Number(poi.z)),
                    placed: poi.placed !== false,
                    title: title,
                    x: Number(poi.x),
                    z: Number(poi.z)
                });
                availablePoiGroups.add(group);
            });

            feedLastUpdated.pois = Date.now();
            setFeedState("pois", true);
            renderPoiLayers();
            renderLayerRows();
            syncLayerVisibility();
        } catch (error) {
            if (requestSequence === poiRequestSequence && requestView === currentView) {
                setFeedState("pois", false);
            }
        }
    }

    function entityLayersAreAvailable() {
        return currentView === "admin" && entityAvailability === "available";
    }

    function anyEntityLayerEnabled() {
        return ENTITY_GROUP_ORDER.some(function (group) {
            return layerSettings[group];
        });
    }

    function clearEntityLayers(preserveState) {
        entityLayers.forEach(function (layer) {
            layer.clearLayers();
        });
        entityMarkerRecords.clear();
        if (!preserveState) {
            entityRevision = null;
            latestEntities = [];
        }
    }

    function updateEntityAvailability(status) {
        if (status.view !== "admin" || typeof status.entities !== "boolean") {
            return;
        }

        if (!status.entities) {
            window.clearTimeout(entityPollTimer);
            entityPollTimer = 0;
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
            normalized.push({
                group: group,
                prefab: prefab,
                trailKey: "",
                x: Number(entity.x),
                y: Number(entity.y),
                z: Number(entity.z)
            });
        });

        var previousShips = latestEntities.filter(function (entity) {
            return entity.group === "ship" && entity.trailKey;
        });
        var currentShips = normalized.filter(function (entity) {
            return entity.group === "ship";
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
        return normalized;
    }

    function renderEntityPayload(entities) {
        var popupSource = map && map._popup ? map._popup._source : null;
        var reopenShipKey = popupSource && popupSource._voPopupKind === "ship"
            ? popupSource._voTrailKey
            : "";
        clearEntityLayers(true);
        var reopenMarker = null;
        entities.forEach(function (entity) {
            var icon = L.divIcon({
                className: "entity-div-icon entity-" + entity.group,
                html: '<span class="entity-marker-shell" aria-hidden="true">' +
                    ENTITY_GROUPS[entity.group].glyph + "</span>",
                iconAnchor: [11, 11],
                iconSize: [22, 22]
            });
            var marker = L.marker(worldToLatLng(entity.x, entity.z), {
                icon: icon,
                title: entity.prefab
            });
            var tooltipContent = document.createElement("span");
            tooltipContent.textContent = entity.prefab;
            marker.bindTooltip(tooltipContent, {
                className: "map-tooltip entity-tooltip",
                direction: "top",
                offset: [0, -11],
                opacity: 1
            });
            var record = { entity: entity, marker: marker };
            bindMapPopup(marker, function () {
                return buildEntityPopup(record.entity);
            }, {
                kind: entity.group,
                trailKey: entity.trailKey,
                trailKind: entity.group === "ship" ? "ship" : ""
            });
            marker.addTo(entityLayers.get(entity.group));
            if (entity.group === "ship") {
                entityMarkerRecords.set(entity.trailKey, record);
                if (entity.trailKey === reopenShipKey) {
                    reopenMarker = marker;
                }
            }
        });

        if (reopenMarker) {
            window.setTimeout(function () {
                if (map && reopenMarker._map) {
                    reopenMarker.openPopup();
                }
            }, 0);
        }
    }

    function updateEntityMarkerRecords(entities) {
        entities.forEach(function (entity) {
            if (entity.group === "ship" && entityMarkerRecords.has(entity.trailKey)) {
                entityMarkerRecords.get(entity.trailKey).entity = entity;
            }
        });
    }

    function updateEntityPolling(immediate) {
        window.clearTimeout(entityPollTimer);
        entityPollTimer = 0;
        if (!map || currentView !== "admin" || entityAvailability === "unavailable" ||
            entityRequestPending || !anyEntityLayerEnabled()) {
            return;
        }

        entityPollTimer = window.setTimeout(
            pollEntities,
            immediate ? 0 : ENTITIES_POLL_INTERVAL_MS
        );
    }

    async function pollEntities() {
        if (!map || currentView !== "admin" || entityRequestPending ||
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

    function ensureEntityFeed() {
        if (!map || currentView !== "admin" || entityAvailability === "unavailable") {
            return;
        }

        if (entityAvailability === "unknown" && !entityRequestPending) {
            pollEntities();
            return;
        }
        updateEntityPolling(true);
    }

    function normalizeRaidEvent(value) {
        if (currentView !== "admin" || !value ||
            !Number.isFinite(Number(value.x)) || !Number.isFinite(Number(value.z)) ||
            !Number.isFinite(Number(value.radius)) || Number(value.radius) <= 0) {
            return null;
        }

        return {
            name: typeof value.name === "string" && value.name.trim() ? value.name.trim() : "Event",
            radius: Number(value.radius),
            x: Number(value.x),
            z: Number(value.z)
        };
    }

    function applyRaidEvent(value) {
        currentRaidEvent = normalizeRaidEvent(value);
        elements.raidBadge.hidden = !currentRaidEvent;
        elements.raidBadge.textContent = currentRaidEvent ? "Raid: " + currentRaidEvent.name : "";

        if (!map || !currentRaidEvent) {
            if (raidCircle && map) {
                map.removeLayer(raidCircle);
            }
            raidCircle = null;
            renderLegend();
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
        renderLegend();
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
                        (isChecked ? "✓" : "•") + "</span></span>",
                    iconAnchor: [10, 19],
                    iconSize: [20, 20]
                });
                var marker = L.marker(worldToLatLng(pinRecord.x, pinRecord.z), {
                    icon: icon,
                    title: pinRecord.name
                });
                marker.bindTooltip(createPinTooltip(pinRecord), {
                    className: "map-tooltip pin-tooltip",
                    direction: "top",
                    offset: [0, -17],
                    opacity: 1
                });
                bindMapPopup(marker, function () {
                    return buildPinPopup(pinRecord);
                }, { kind: "pin" });
                marker.addTo(pinLayer);
            });
            feedLastUpdated.pins = Date.now();
            setFeedState("pins", true);
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
        var nextView = view === "admin" ? "admin" : "public";
        elements.publicViewBadge.hidden = nextView !== "public";
        if (nextView === currentView) {
            return;
        }

        currentView = nextView;
        if (map) {
            loadPoisForCurrentView();
            renderLayerRows();
            syncLayerVisibility();
            if (currentView === "admin") {
                ensureEntityFeed();
            } else {
                window.clearTimeout(entityPollTimer);
                entityPollTimer = 0;
                setFeedState("entities", true);
                applyRaidEvent(null);
            }
        }
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
        fogAvailable = fogStatus.mode !== "off" && currentView !== "admin";
        if (wasAvailable !== fogAvailable) {
            renderLayerRows();
        }
        applyFogStatus();
    }

    function fogUrl(revision) {
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
        var url = fogUrl(revision);
        if (!fogOverlay) {
            if (revision === fogRequestedRevision) {
                syncLayerVisibility();
                return;
            }

            // Preload the first fog image before creating the overlay so the
            // cover only lifts once the fogged view is actually renderable.
            fogRequestedRevision = revision;
            var initialSequence = ++fogLoadSequence;
            var initialImage = new window.Image();
            initialImage.onload = function () {
                if (initialSequence !== fogLoadSequence || !fogAvailable || fogOverlay) {
                    return;
                }

                fogOverlay = L.imageOverlay(url, worldBounds, {
                    className: "fog-overlay",
                    interactive: false,
                    opacity: 1,
                    pane: "fogPane"
                });
                fogDisplayedRevision = revision;
                fogRequestedRevision = revision;
                syncLayerVisibility();
                hideFogCover();
            };
            initialImage.onerror = function () {
                if (initialSequence === fogLoadSequence && fogRequestedRevision === revision) {
                    fogRequestedRevision = null;
                }
            };
            initialImage.src = url;
            return;
        }

        if (revision === fogDisplayedRevision || revision === fogRequestedRevision) {
            syncLayerVisibility();
            return;
        }

        fogRequestedRevision = revision;
        var loadSequence = ++fogLoadSequence;
        var image = new window.Image();
        image.onload = function () {
            if (loadSequence !== fogLoadSequence || !fogAvailable ||
                revision !== fogStatus.revision || !fogOverlay) {
                return;
            }

            fogOverlay.setUrl(url);
            fogDisplayedRevision = revision;
            fogRequestedRevision = revision;
            syncLayerVisibility();
        };
        image.onerror = function () {
            if (loadSequence === fogLoadSequence && fogRequestedRevision === revision) {
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
        setFeedState("status", true);
        elements.serverName.textContent = textOrDash(status.serverName);
        elements.worldName.textContent = textOrDash(status.worldName);
        renderWorldTime(status.day, status.timeOfDay);
        renderPlayerCount(status.players);
        updateRenderStatus(status.map);
        updateEntityAvailability(status);
        updateView(status.view);
        updateConsoleAvailability(status);
        updateFogStatus(status.map && status.map.fog);
        ensureMap(status.map);
        applyRaidEvent(status.event);
    }

    function handlePlayersPayload(payload) {
        if (!payload || typeof payload !== "object") {
            throw new Error("Invalid players payload");
        }

        feedLastUpdated.players = Date.now();
        setFeedState("players", true);
        var previousPlayerNames = latestPlayers.map(function (player) {
            return player.name || "";
        }).sort().join("\n");
        latestPlayers = normalizePlayers(payload);
        recordPlayerTrails(latestPlayers);
        var currentPlayerNames = latestPlayers.map(function (player) {
            return player.name || "";
        }).sort().join("\n");
        renderPlayerList(latestPlayers);
        renderConsolePlayers();
        updatePlayerMarkers(latestPlayers);
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

    bindConsoleEvents();
    bindPopupDocumentEvents();
    renderPlayerCount(latestPlayerCount);
    renderConsolePlayers();
    startPolling(pollStatus, POLL_INTERVAL_MS);
    startPolling(pollPlayers, POLL_INTERVAL_MS);
    connectEventStream();
    window.addEventListener("beforeunload", function () {
        window.clearTimeout(eventSourceRetryTimer);
        window.clearInterval(popupRefreshTimer);
        if (eventSource) {
            eventSource.close();
        }
    });
}());
