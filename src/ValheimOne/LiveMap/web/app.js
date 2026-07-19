(function () {
    "use strict";

    var POLL_INTERVAL_MS = 2000;
    var MOVE_DURATION_MS = 400;
    var TILE_SIZE = 256;
    var WORLD_UNITS = 256;

    var query = new URLSearchParams(window.location.search);
    var token = query.get("token") || "";
    var failedFeeds = new Set();
    var markerRecords = new Map();
    var latestPlayers = [];
    var latestPlayerCount = 0;
    var map = null;
    var tileLayer = null;
    var mapMetrics = null;
    var followedPlayer = null;

    var elements = {
        dayNumber: document.getElementById("day-number"),
        mapStatus: document.getElementById("render-status"),
        mapStatusText: document.getElementById("render-status-text"),
        offlineBadge: document.getElementById("offline-badge"),
        playerCount: document.getElementById("player-count"),
        playerList: document.getElementById("player-list"),
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

        var worldBounds = L.latLngBounds([[-WORLD_UNITS, 0], [0, WORLD_UNITS]]);
        map = L.map("map", {
            attributionControl: false,
            crs: L.CRS.Simple,
            maxBounds: worldBounds.pad(0.08),
            maxBoundsViscosity: 0.72,
            maxZoom: maximumZoom,
            minZoom: 0,
            zoomControl: true
        });

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

        // Renderer row zero is the south edge. Reverse tile rows here; app.css
        // flips the pixels inside each tile, completing the north-up reflection.
        tileLayer.getTileUrl = function (coordinates) {
            var reversedY = (Math.pow(2, coordinates.z) - 1) - coordinates.y;
            return L.Util.template(this._url, {
                x: coordinates.x,
                y: reversedY,
                z: coordinates.z
            });
        };

        tileLayer.addTo(map);
        map.setView(worldToLatLng(0, 0), Math.max(0, maximumZoom - 1));
        map.on("dragstart", clearFollow);
        updatePlayerMarkers(latestPlayers);
    }

    function worldToLatLng(worldX, worldZ) {
        if (!mapMetrics) {
            return L.latLng(0, 0);
        }

        var pixelX = (worldX / mapMetrics.pixelSize) + (mapMetrics.textureSize / 2);
        var pixelY = (worldZ / mapMetrics.pixelSize) + (mapMetrics.textureSize / 2);
        return L.latLng(
            -(mapMetrics.textureSize - pixelY) * mapMetrics.unitsPerPixel,
            pixelX * mapMetrics.unitsPerPixel
        );
    }

    function normalizePlayers(payload) {
        if (!payload || !Array.isArray(payload.players)) {
            return [];
        }

        return payload.players.filter(function (player) {
            return player && typeof player.name === "string" &&
                Number.isFinite(Number(player.x)) && Number.isFinite(Number(player.z));
        }).map(function (player) {
            return {
                name: player.name,
                x: Number(player.x),
                y: Number(player.y),
                z: Number(player.z)
            };
        });
    }

    function createPlayerMarker(player) {
        var tooltipContent = document.createElement("span");
        tooltipContent.textContent = player.name;
        var marker = L.circleMarker(worldToLatLng(player.x, player.z), {
            className: "player-marker",
            color: "#f7fbff",
            fillColor: "#4d9fec",
            fillOpacity: 0.95,
            radius: 6,
            weight: 2
        }).addTo(map);
        marker.bindTooltip(tooltipContent, {
            className: "player-tooltip",
            direction: "top",
            offset: [0, -7],
            opacity: 1,
            permanent: true
        });
        marker.on("click", function () {
            followPlayer(player.name);
        });

        return {
            animationFrame: 0,
            marker: marker,
            player: player
        };
    }

    function updatePlayerMarkers(players) {
        if (!map || !mapMetrics) {
            return;
        }

        var activeNames = new Set();
        players.forEach(function (player) {
            activeNames.add(player.name);
            var target = worldToLatLng(player.x, player.z);
            var record = markerRecords.get(player.name);
            if (!record) {
                record = createPlayerMarker(player);
                markerRecords.set(player.name, record);
            } else {
                record.player = player;
                animateMarker(record, target);
            }
        });

        markerRecords.forEach(function (record, name) {
            if (activeNames.has(name)) {
                return;
            }

            cancelAnimationFrame(record.animationFrame);
            record.marker.removeFrom(map);
            markerRecords.delete(name);
            if (followedPlayer === name) {
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
            if (followedPlayer === record.player.name) {
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

    function followPlayer(name) {
        var record = markerRecords.get(name);
        if (!record || !map) {
            return;
        }

        followedPlayer = name;
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
        markerRecords.forEach(function (record, name) {
            var markerElement = record.marker.getElement();
            if (markerElement) {
                markerElement.classList.toggle("is-followed", name === followedPlayer);
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
            button.classList.toggle("is-followed", player.name === followedPlayer);
            button.addEventListener("click", function () {
                followPlayer(player.name);
            });

            identity.className = "player-identity";
            dot.className = "player-dot";
            name.className = "player-name";
            name.textContent = player.name;
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

    async function pollStatus() {
        try {
            var status = await fetchJson("/api/status");
            setFeedState("status", true);
            elements.serverName.textContent = textOrDash(status.serverName);
            elements.worldName.textContent = textOrDash(status.worldName);
            renderWorldTime(status.day, status.timeOfDay);
            renderPlayerCount(status.players);
            updateRenderStatus(status.map);
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

    function startPolling(task) {
        async function run() {
            await task();
            window.setTimeout(run, POLL_INTERVAL_MS);
        }

        run();
    }

    renderPlayerCount(latestPlayerCount);
    startPolling(pollStatus);
    startPolling(pollPlayers);
}());
