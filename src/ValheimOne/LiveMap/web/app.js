(function () {
    "use strict";

    var POLL_INTERVAL_MS = 2000;
    var PINS_POLL_INTERVAL_MS = 60000;
    var MOVE_DURATION_MS = 400;
    var TILE_SIZE = 256;
    var WORLD_UNITS = 256;
    var LAYER_STORAGE_KEY = "vo-livemap-layers";

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

    var elements = {
        dayNumber: document.getElementById("day-number"),
        mapStatus: document.getElementById("render-status"),
        mapStatusText: document.getElementById("render-status-text"),
        offlineBadge: document.getElementById("offline-badge"),
        playerCount: document.getElementById("player-count"),
        playerList: document.getElementById("player-list"),
        publicViewBadge: document.getElementById("public-view-badge"),
        serverName: document.getElementById("server-name"),
        sidebarState: document.getElementById("sidebar-state"),
        skyIndicator: document.getElementById("sky-indicator"),
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

    renderPlayerCount(latestPlayerCount);
    startPolling(pollStatus, POLL_INTERVAL_MS);
    startPolling(pollPlayers, POLL_INTERVAL_MS);
}());
