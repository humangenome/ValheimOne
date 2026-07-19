(function () {
    "use strict";

    var POLL_INTERVAL_MS = 2000;
    var PINS_POLL_INTERVAL_MS = 60000;
    var CONSOLE_LOG_POLL_INTERVAL_MS = 2000;
    var CONSOLE_STATS_POLL_INTERVAL_MS = 5000;
    var CONSOLE_LOG_LIMIT = 1000;
    var COMMAND_HISTORY_LIMIT = 50;
    var MOVE_DURATION_MS = 400;
    var TILE_SIZE = 256;
    var WORLD_UNITS = 256;
    var LAYER_STORAGE_KEY = "vo-livemap-layers";
    var TAB_SESSION_KEY = "vo-livemap-active-tab";

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

    var LAYER_DEFAULTS = {
        players: true,
        pins: true,
        spawn: true,
        trader: true,
        boss: true,
        dungeon: false,
        spawner: false,
        misc: false,
        other: false,
        fog: true
    };

    var query = new URLSearchParams(window.location.search);
    var token = query.get("token") || "";
    var failedFeeds = new Set();
    var markerRecords = new Map();
    var latestPlayers = [];
    var latestPlayerCount = 0;
    var map = null;
    var tileLayer = null;
    var mapMetrics = null;
    var worldBounds = null;
    var followedPlayer = null;
    var playerLayer = null;
    var pinLayer = null;
    var poiLayers = new Map();
    var availablePoiGroups = new Set();
    var layerSettings = loadLayerSettings();
    var layersRows = null;
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
    var consoleCursor = 0;
    var consoleFollowLog = true;
    var consoleCommands = [];
    var consoleSuggestions = [];
    var consoleSuggestionClosed = false;
    var commandHistory = [];
    var commandHistoryIndex = 0;
    var commandHistoryDraft = "";
    var consoleFailures = Object.create(null);
    var confirmAction = null;
    var saveButtonTimer = 0;

    var elements = {
        bannedCount: document.getElementById("console-banned-count"),
        bannedList: document.getElementById("console-banned-list"),
        commandForm: document.getElementById("console-command-form"),
        commandInput: document.getElementById("console-command"),
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
        elements.suggestionList.textContent = "";
        elements.suggestionList.hidden = true;
        elements.commandInput.setAttribute("aria-expanded", "false");
    }

    function renderCommandSuggestions() {
        closeSuggestions();
        if (!consoleMetaLoaded || consoleSuggestionClosed) {
            return;
        }

        var input = elements.commandInput.value.replace(/^\s+/, "");
        if (!input || /\s/.test(input)) {
            return;
        }

        var prefix = input.toLowerCase();
        consoleSuggestions = consoleCommands.filter(function (command) {
            return command.name.toLowerCase().indexOf(prefix) === 0;
        }).slice(0, 8);
        if (consoleSuggestions.length === 0) {
            return;
        }

        consoleSuggestions.forEach(function (command, index) {
            var option = document.createElement("button");
            var heading = document.createElement("span");
            var name = document.createElement("span");
            option.type = "button";
            option.className = "console-suggestion";
            option.classList.toggle("is-selected", index === 0);
            option.setAttribute("role", "option");
            option.setAttribute("aria-selected", String(index === 0));
            heading.className = "console-suggestion-heading";
            name.className = "console-suggestion-name";
            name.textContent = command.name;
            heading.appendChild(name);

            if (command.cheat) {
                var badge = document.createElement("span");
                badge.className = "console-cheat-badge";
                badge.textContent = "Cheat";
                heading.appendChild(badge);
            }

            option.appendChild(heading);
            if (command.description) {
                var description = document.createElement("span");
                description.className = "console-suggestion-description";
                description.textContent = command.description;
                option.appendChild(description);
            }

            option.addEventListener("mousedown", function (event) {
                event.preventDefault();
                completeSuggestion(index);
            });
            elements.suggestionList.appendChild(option);
        });

        elements.suggestionList.hidden = false;
        elements.commandInput.setAttribute("aria-expanded", "true");
    }

    function completeSuggestion(index) {
        var suggestion = consoleSuggestions[index];
        if (!suggestion) {
            return;
        }

        elements.commandInput.value = suggestion.name + " ";
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

            var command = { name: name, description: "", cheat: false, whitelisted: true };
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
                command = { name: name, description: "", cheat: false, whitelisted: false };
                byName[key] = command;
                commands.push(command);
            }
            command.description = entry && typeof entry.description === "string" ? entry.description.trim() : "";
            command.cheat = entry && entry.cheat === true;
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
        if (!consoleIsActive() || consoleMetaLoaded || consoleMetaRequestPending) {
            return;
        }

        consoleMetaRequestPending = true;
        try {
            var payload = await fetchConsoleJson("/api/console/meta");
            consoleCommands = normalizeConsoleCommands(payload);
            consoleMetaLoaded = true;
            clearConsoleFailure("meta");
            renderCommandSuggestions();
        } catch (error) {
            reportConsoleFailure("meta", "Command metadata", error);
        } finally {
            consoleMetaRequestPending = false;
        }
    }

    async function pollConsoleLog() {
        if (!consoleIsActive() || consoleLogRequestPending) {
            return;
        }

        consoleLogRequestPending = true;
        try {
            var payload = await fetchConsoleJson(
                "/api/console/log?cursor=" + encodeURIComponent(consoleCursor) + "&max=250"
            );
            var lines = payload && Array.isArray(payload.lines) ? payload.lines : [];
            appendConsoleEntries(lines.map(function (line) {
                return {
                    kind: "server",
                    time: line && line.time,
                    level: line && line.level,
                    text: line && line.text
                };
            }));

            var nextCursor = payload ? Number(payload.cursor) : NaN;
            if (Number.isFinite(nextCursor)) {
                consoleCursor = Math.max(0, Math.floor(nextCursor));
            }
            clearConsoleFailure("log");
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
            empty.textContent = "No players online";
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
                walkCommandHistory(-1);
            } else if (event.key === "ArrowDown") {
                event.preventDefault();
                walkCommandHistory(1);
            } else if (event.key === "Tab" && consoleSuggestions.length > 0) {
                event.preventDefault();
                completeSuggestion(0);
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
            }
        });
    }

    function setFeedState(feed, isOnline) {
        if (isOnline) {
            failedFeeds.delete(feed);
        } else {
            failedFeeds.add(feed);
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

    function ensureMap(statusMap) {
        if (tileLayer || !statusMap || statusMap.state !== "ready") {
            return;
        }

        var textureSize = Number(statusMap.textureSize);
        var pixelSize = Number(statusMap.pixelSize);
        if (!Number.isFinite(textureSize) || textureSize < TILE_SIZE ||
            !Number.isFinite(pixelSize) || pixelSize <= 0) {
            return;
        }

        var maximumZoom = calculateMaximumZoom(textureSize);
        mapMetrics = {
            textureSize: textureSize,
            pixelSize: pixelSize,
            maximumZoom: maximumZoom,
            unitsPerPixel: WORLD_UNITS / textureSize
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
        createLayersControl();
        map.setView(worldToLatLng(0, 0), Math.max(0, maximumZoom - 1));
        map.on("dragstart", clearFollow);
        syncLayerVisibility();
        updatePlayerMarkers(latestPlayers);
        loadPoisForCurrentView();
        applyFogStatus();
        startPinsPolling();
    }

    function initialiseDataLayers() {
        playerLayer = L.layerGroup();
        pinLayer = L.layerGroup();
        POI_GROUP_ORDER.forEach(function (group) {
            poiLayers.set(group, L.layerGroup());
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
        appendLayerRow("players", "Players", "●", "players");
        appendLayerRow("pins", "Pins", "⌖", "pins");

        POI_GROUP_ORDER.forEach(function (group) {
            if (availablePoiGroups.has(group)) {
                appendLayerRow(group, POI_GROUPS[group].label, POI_GROUPS[group].glyph, group);
            }
        });

        if (fogAvailable) {
            appendLayerRow("fog", "Fog", "≈", "fog");
        }
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
        POI_GROUP_ORDER.forEach(function (group) {
            setLayerVisible(
                poiLayers.get(group),
                availablePoiGroups.has(group) && layerSettings[group]
            );
        });
        setLayerVisible(fogOverlay, fogAvailable && layerSettings.fog);
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

    function createPlayerMarker(player) {
        var tooltipContent = document.createElement("span");
        tooltipContent.textContent = player.displayName;
        var marker = L.circleMarker(worldToLatLng(player.x, player.z), {
            className: "player-marker",
            color: "#f7fbff",
            fillColor: "#4d9fec",
            fillOpacity: 0.95,
            radius: 6,
            weight: 2
        }).addTo(playerLayer);
        marker.bindTooltip(tooltipContent, {
            className: "player-tooltip",
            direction: "top",
            offset: [0, -7],
            opacity: 1,
            permanent: !player.anonymous
        });
        marker.on("click", function () {
            followPlayer(player.key);
        });

        return {
            animationFrame: 0,
            marker: marker,
            player: player
        };
    }

    function updatePlayerMarkers(players) {
        if (!map || !mapMetrics || !playerLayer) {
            return;
        }

        var activeKeys = new Set();
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
            }
        });

        updateFollowStyles();
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
        renderPlayerList(latestPlayers);
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
        renderPlayerList(latestPlayers);
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
            empty.textContent = "No players online";
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

    function clearPoiLayers() {
        poiLayers.forEach(function (layer) {
            layer.clearLayers();
        });
        availablePoiGroups.clear();
        renderLayerRows();
        syncLayerVisibility();
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
                var icon = L.divIcon({
                    className: "poi-div-icon poi-" + group,
                    html: '<span class="poi-marker-shell" aria-hidden="true">' +
                        POI_GROUPS[group].glyph + "</span>",
                    iconAnchor: [10, 10],
                    iconSize: [20, 20]
                });
                var marker = L.marker(worldToLatLng(Number(poi.x), Number(poi.z)), {
                    icon: icon,
                    opacity: poi.placed === false ? 0.55 : 1,
                    title: title
                });
                var tooltipContent = document.createElement("span");
                tooltipContent.textContent = title;
                marker.bindTooltip(tooltipContent, {
                    className: "map-tooltip poi-tooltip",
                    direction: "top",
                    offset: [0, -10],
                    opacity: 1
                });
                marker.addTo(poiLayers.get(group));
                availablePoiGroups.add(group);
            });

            setFeedState("pois", true);
            renderLayerRows();
            syncLayerVisibility();
        } catch (error) {
            if (requestSequence === poiRequestSequence && requestView === currentView) {
                setFeedState("pois", false);
            }
        }
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
                var icon = L.divIcon({
                    className: "pin-div-icon" + (isChecked ? " is-checked" : ""),
                    html: '<span class="pin-marker-shell"><span class="pin-marker-glyph">' +
                        (isChecked ? "✓" : "•") + "</span></span>",
                    iconAnchor: [10, 19],
                    iconSize: [20, 20]
                });
                var marker = L.marker(worldToLatLng(Number(pin.x), Number(pin.z)), {
                    icon: icon,
                    title: typeof pin.name === "string" ? pin.name : "Pin"
                });
                marker.bindTooltip(createPinTooltip({
                    author: pin.author,
                    checked: isChecked,
                    name: pin.name
                }), {
                    className: "map-tooltip pin-tooltip",
                    direction: "top",
                    offset: [0, -17],
                    opacity: 1
                });
                marker.addTo(pinLayer);
            });
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
            syncLayerVisibility();
            return;
        }

        var revision = fogStatus.revision;
        var url = fogUrl(revision);
        if (!fogOverlay) {
            fogOverlay = L.imageOverlay(url, worldBounds, {
                className: "fog-overlay",
                interactive: false,
                opacity: 1,
                pane: "fogPane"
            });
            fogDisplayedRevision = revision;
            fogRequestedRevision = revision;
            syncLayerVisibility();
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

    async function pollStatus() {
        try {
            var status = await fetchJson("/api/status");
            setFeedState("status", true);
            elements.serverName.textContent = textOrDash(status.serverName);
            elements.worldName.textContent = textOrDash(status.worldName);
            renderWorldTime(status.day, status.timeOfDay);
            renderPlayerCount(status.players);
            updateRenderStatus(status.map);
            updateView(status.view);
            updateConsoleAvailability(status);
            updateFogStatus(status.map && status.map.fog);
            ensureMap(status.map);
        } catch (error) {
            setFeedState("status", false);
        }
    }

    async function pollPlayers() {
        try {
            var payload = await fetchJson("/api/players");
            setFeedState("players", true);
            latestPlayers = normalizePlayers(payload);
            renderPlayerList(latestPlayers);
            renderConsolePlayers();
            updatePlayerMarkers(latestPlayers);
        } catch (error) {
            setFeedState("players", false);
        }
    }

    function startPolling(task, interval) {
        async function run() {
            await task();
            window.setTimeout(run, interval);
        }

        run();
    }

    bindConsoleEvents();
    renderPlayerCount(latestPlayerCount);
    renderConsolePlayers();
    startPolling(pollStatus, POLL_INTERVAL_MS);
    startPolling(pollPlayers, POLL_INTERVAL_MS);
}());
