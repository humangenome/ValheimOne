using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using ValheimOne.ActivityLog;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class LiveMapHttpServer
{
    private const int MaximumRequestBodyBytes = 8 * 1024;
    private const int MaximumEventStreams = 8;
    private const int EventStreamTickMilliseconds = 1000;
    private const int EventStreamHeartbeatTicks = 15;
    private const int EventStreamLogBatchSize = 100;
    private const int EventStreamActivityBatchSize = 100;
    private const int MaximumInlineLocationPoisPerGroup = 400;
    private const float MaximumPingWorldRadius = 10500f;
    private const int MaximumPingLabelLength = 32;
    private const long ResourceRefreshMilliseconds = 180L * 1000L;

    private enum ViewLevel
    {
        Admin,
        Shared,
        Public,
    }

    private readonly int _port;
    private readonly string _bindIp;
    private readonly bool _publicView;
    private readonly bool _respectInGameVisibility;
    private readonly bool _publicShowPlayerNames;
    private readonly Func<LiveMapSnapshot> _getSnapshot;
    private readonly Func<PoiCatalog> _getPoiCatalog;
    private readonly Func<ResourcePoiMapSnapshot> _getResourcePoiSnapshot;
    private readonly Action _noteResourcesRequested;
    private readonly Func<MapTableSnapshot> _getMapTableSnapshot;
    private readonly Func<EntityMapSnapshot> _getEntitySnapshot;
    private readonly Func<EntityFocusSnapshot> _getEntityFocusSnapshot;
    private readonly Action _noteEntitiesRequested;
    private readonly Action<string> _noteEntityFocusRequested;
    private readonly PositionHistory _positionHistory;
    private readonly Func<string> _getFogMode;
    private readonly Func<float> _getEffectiveUpdateSeconds;
    private readonly FogTracker _fogTracker;
    private readonly WorldMapRenderer _renderer;
    private readonly Func<float, float, string, bool> _enqueueMapPing;
    private readonly LiveMapConfig _config;
    private readonly ConsoleBridge? _consoleBridge;
    private readonly LogRingBuffer? _logRingBuffer;
    private readonly ActivityLogModule _activityLog;
    private readonly ModLogger _log;
    private string AccessToken => NormalizeToken(_config.AccessToken);
    private string ShareToken => NormalizeToken(_config.ShareToken);
    private readonly object _fogPngLock = new object();
    private readonly object _exploredPctLock = new object();
    private HttpListener? _listener;
    private Thread? _listenerThread;
    private byte[]? _fogPng;
    private long _fogPngRevision = -1;
    private byte[]? _chartFogPng;
    private long _chartFogPngRevision = -1;
    private long _exploredPctRevision = -1;
    private double _exploredPctValue;
    private bool _accessTokenWarningLogged;
    private bool _consoleTokenWarningLogged;
    private int _eventStreamCount;
    private volatile bool _stopping;

    public LiveMapHttpServer(
        int port,
        string bindIp,
        bool adminSeesAll,
        bool publicView,
        bool respectInGameVisibility,
        bool publicShowPlayerNames,
        Func<LiveMapSnapshot> getSnapshot,
        Func<PoiCatalog> getPoiCatalog,
        Func<ResourcePoiMapSnapshot> getResourcePoiSnapshot,
        Action noteResourcesRequested,
        Func<MapTableSnapshot> getMapTableSnapshot,
        Func<EntityMapSnapshot> getEntitySnapshot,
        Func<EntityFocusSnapshot> getEntityFocusSnapshot,
        Action noteEntitiesRequested,
        Action<string> noteEntityFocusRequested,
        PositionHistory positionHistory,
        Func<string> getFogMode,
        Func<float> getEffectiveUpdateSeconds,
        FogTracker fogTracker,
        WorldMapRenderer renderer,
        Func<float, float, string, bool> enqueueMapPing,
        LiveMapConfig config,
        ConsoleBridge? consoleBridge,
        LogRingBuffer? logRingBuffer,
        ActivityLogModule activityLog,
        ModLogger log)
    {
        _port = port;
        _bindIp = bindIp.Trim();
        _publicView = publicView;
        _respectInGameVisibility = respectInGameVisibility;
        _publicShowPlayerNames = publicShowPlayerNames;
        _getSnapshot = getSnapshot;
        _getPoiCatalog = getPoiCatalog;
        _getResourcePoiSnapshot = getResourcePoiSnapshot;
        _noteResourcesRequested = noteResourcesRequested;
        _getMapTableSnapshot = getMapTableSnapshot;
        _getEntitySnapshot = getEntitySnapshot;
        _getEntityFocusSnapshot = getEntityFocusSnapshot;
        _noteEntitiesRequested = noteEntitiesRequested;
        _noteEntityFocusRequested = noteEntityFocusRequested;
        _positionHistory = positionHistory;
        _getFogMode = getFogMode;
        _getEffectiveUpdateSeconds = getEffectiveUpdateSeconds;
        _fogTracker = fogTracker;
        _renderer = renderer;
        _enqueueMapPing = enqueueMapPing;
        _config = config;
        _consoleBridge = consoleBridge;
        _logRingBuffer = logRingBuffer;
        _activityLog = activityLog;
        _log = log;
    }

    public void Start()
    {
        if (_listener != null)
        {
            return;
        }

        string accessToken = AccessToken;
        if (!_accessTokenWarningLogged && string.IsNullOrEmpty(accessToken))
        {
            _accessTokenWarningLogged = true;
            _log.Warning(
                "[LiveMap] AccessToken is empty — admin map view disabled until " +
                "LiveMap.AccessToken is set; save your panel config to auto-generate one");
        }

        if (!_consoleTokenWarningLogged &&
            _config.ConsoleEnabled &&
            string.IsNullOrEmpty(accessToken))
        {
            _consoleTokenWarningLogged = true;
            _log.Warning(
                "[LiveMap] web console requires a non-empty AccessToken; console endpoints disabled");
        }

        string preferredHost = string.IsNullOrEmpty(_bindIp) ? "*" : FormatHost(_bindIp);
        string preferredPrefix = $"http://{preferredHost}:{_port}/";
        if (!TryStartListener(preferredPrefix, out HttpListener? listener, out Exception? failure))
        {
            if (!string.IsNullOrEmpty(_bindIp))
            {
                _log.Error($"[LiveMap] HTTP listener could not bind {preferredPrefix}: {failure}");
                return;
            }

            string fallbackPrefix = $"http://127.0.0.1:{_port}/";
            _log.Warning(
                $"[LiveMap] HTTP listener could not bind {preferredPrefix} ({failure?.GetType().Name}); " +
                $"falling back to {fallbackPrefix}.");
            if (!TryStartListener(fallbackPrefix, out listener, out failure))
            {
                _log.Error($"[LiveMap] HTTP listener could not bind {fallbackPrefix}: {failure}");
                return;
            }

            preferredPrefix = fallbackPrefix;
        }

        _listener = listener;
        _listenerThread = new Thread(Listen)
        {
            IsBackground = true,
            Name = "ValheimOne.LiveMap.Http",
        };
        _listenerThread.Start();
        _log.Info($"[LiveMap] listening on {preferredPrefix}");
    }

    public bool IsRunning => _listener?.IsListening == true;

    public void Stop()
    {
        _stopping = true;
        HttpListener? listener = _listener;
        if (listener != null)
        {
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch (ObjectDisposedException)
            {
                // The listener is already stopped.
            }
        }

        Thread? thread = _listenerThread;
        if (thread != null && thread.IsAlive && !ReferenceEquals(Thread.CurrentThread, thread))
        {
            thread.Join();
        }

        _listenerThread = null;
        _listener = null;
    }

    private static bool TryStartListener(
        string prefix,
        out HttpListener? listener,
        out Exception? failure)
    {
        listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
            failure = null;
            return true;
        }
        catch (Exception exception)
        {
            failure = exception;
            try
            {
                listener.Close();
            }
            catch
            {
                // Preserve the original bind failure.
            }

            listener = null;
            return false;
        }
    }

    private void Listen()
    {
        HttpListener? listener = _listener;
        if (listener == null)
        {
            return;
        }

        while (!_stopping && listener.IsListening)
        {
            try
            {
                HttpListenerContext context = listener.GetContext();
                ThreadPool.QueueUserWorkItem(HandleRequest, context);
            }
            catch (HttpListenerException)
            {
                if (!_stopping)
                {
                    _log.Warning("[LiveMap] HTTP listener stopped accepting requests unexpectedly.");
                }

                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }
    }

    private void HandleRequest(object? state)
    {
        var context = state as HttpListenerContext;
        if (context == null)
        {
            return;
        }

        HttpListenerResponse response = context.Response;
        response.KeepAlive = false;
        try
        {
            HttpListenerRequest request = context.Request;
            string path = request.Url?.AbsolutePath ?? "/";
            bool isConsolePath = IsConsolePath(path);
            ViewLevel viewLevel;
            if (isConsolePath)
            {
                if (!HasConsoleToken(request))
                {
                    WriteJson(response, HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}");
                    return;
                }

                if (!_config.ConsoleEnabled || _consoleBridge == null || _logRingBuffer == null)
                {
                    WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
                    return;
                }

                viewLevel = ViewLevel.Admin;
            }
            else if (!TryResolveView(request, out viewLevel))
            {
                if (path == "/api/status" && _config.StatusPublic)
                {
                    viewLevel = ViewLevel.Public;
                }
                else
                {
                    WriteJson(response, HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}");
                    return;
                }
            }

            bool isGet = string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase);
            bool isPost = string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase);
            if (isGet && path == "/")
            {
                ServeIndex(response, viewLevel);
            }
            else if (isGet && path == "/favicon.ico")
            {
                ServeFavicon(response);
            }
            else if (isGet && path.StartsWith("/assets/", StringComparison.Ordinal))
            {
                ServeAsset(response, path.Substring("/assets/".Length));
            }
            else if (isGet && path == "/api/status")
            {
                ServeStatus(response, viewLevel);
            }
            else if (isGet && path == "/api/players")
            {
                ServePlayers(response, viewLevel);
            }
            else if (isGet && path == "/api/activity")
            {
                ServeActivity(request, response, viewLevel);
            }
            else if (isGet && path == "/api/trail")
            {
                ServeTrail(request, response, viewLevel);
            }
            else if (isGet && path == "/api/height")
            {
                ServeHeight(request, response);
            }
            else if (isPost && path == "/api/ping")
            {
                ServePing(request, response, viewLevel);
            }
            else if (isGet && path == "/api/entities")
            {
                ServeEntities(request, response, viewLevel);
            }
            else if (isGet && path == "/api/events")
            {
                ServeEvents(request, response, viewLevel);
            }
            else if (isGet && path == "/api/pois")
            {
                ServePois(request, response, viewLevel);
            }
            else if (isGet && path == "/api/regions")
            {
                ServeRegions(response);
            }
            else if (isGet && path == "/api/pins")
            {
                ServePins(response);
            }
            else if (isGet && path.StartsWith("/tiles/", StringComparison.Ordinal))
            {
                ServeTile(request, response, path.Substring("/tiles/".Length));
            }
            else if (isGet && path == "/base.png")
            {
                ServeBaseImage(request, response);
            }
            else if (isGet && path == "/fog.png")
            {
                ServeFogImage(request, response, viewLevel);
            }
            else if (isPost && path == "/api/console/exec")
            {
                ServeConsoleExec(request, response);
            }
            else if (isGet && path == "/api/console/log")
            {
                ServeConsoleLog(request, response);
            }
            else if (isGet && path == "/api/console/history")
            {
                ServeConsoleHistory(request, response);
            }
            else if (isGet && path == "/api/console/meta")
            {
                ServeConsoleMeta(response);
            }
            else if (isPost && path == "/api/admin/kick")
            {
                ServeAdminAction(request, response, "kick", _consoleBridge!.Kick);
            }
            else if (isPost && path == "/api/admin/ban")
            {
                ServeAdminAction(request, response, "ban", _consoleBridge!.Ban);
            }
            else if (isPost && path == "/api/admin/unban")
            {
                ServeAdminAction(request, response, "unban", _consoleBridge!.Unban);
            }
            else if (isGet && path == "/api/admin/banlist")
            {
                ServeBanList(response);
            }
            else if (isPost && path == "/api/admin/save")
            {
                ServeSave(request, response);
            }
            else if (isGet && path == "/api/stats")
            {
                ServeStats(response);
            }
            else
            {
                WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            }
        }
        catch (HttpListenerException)
        {
            // Client disconnects are routine on Mono's managed listener.
        }
        catch (IOException)
        {
            // Client disconnects are routine on Mono's managed listener.
        }
        catch (ObjectDisposedException)
        {
            // The server is stopping.
        }
        catch (Exception exception)
        {
            _log.Warning($"[LiveMap] HTTP request failed: {exception.GetType().Name}: {exception.Message}");
            TryWriteJson(response, HttpStatusCode.InternalServerError, "{\"error\":\"internal server error\"}");
        }
    }

    private bool TryResolveView(HttpListenerRequest request, out ViewLevel viewLevel)
    {
        string queryToken = request.QueryString["token"] ?? string.Empty;
        string headerToken = request.Headers["X-LiveMap-Token"] ?? string.Empty;
        string accessToken = AccessToken;
        if (!string.IsNullOrEmpty(accessToken))
        {
            bool isAdmin = FixedTimeEquals(accessToken, queryToken);
            isAdmin |= FixedTimeEquals(accessToken, headerToken);
            if (isAdmin)
            {
                viewLevel = ViewLevel.Admin;
                return true;
            }
        }

        string shareToken = ShareToken;
        if (!string.IsNullOrEmpty(shareToken))
        {
            bool isShared = FixedTimeEquals(shareToken, queryToken);
            isShared |= FixedTimeEquals(shareToken, headerToken);
            if (isShared)
            {
                viewLevel = ViewLevel.Shared;
                return true;
            }
        }

        viewLevel = ViewLevel.Public;
        return _publicView;
    }

    private bool HasConsoleToken(HttpListenerRequest request)
    {
        string accessToken = AccessToken;
        if (string.IsNullOrEmpty(accessToken))
        {
            return false;
        }

        string queryToken = request.QueryString["token"] ?? string.Empty;
        string headerToken = request.Headers["X-LiveMap-Token"] ?? string.Empty;
        bool matches = FixedTimeEquals(accessToken, queryToken);
        matches |= FixedTimeEquals(accessToken, headerToken);
        return matches;
    }

    private static bool IsConsolePath(string path)
    {
        return path.StartsWith("/api/console/", StringComparison.Ordinal) ||
               path.StartsWith("/api/admin/", StringComparison.Ordinal) ||
               path == "/api/stats";
    }

    private static void ReadAuditIdentity(
        HttpListenerRequest request,
        out string operatorName,
        out string source)
    {
        string? header = request.Headers["X-Operator"];
        operatorName = header ?? string.Empty;
        source = header == null ? "token" : "panel";
    }

    private void ServeIndex(HttpListenerResponse response, ViewLevel viewLevel)
    {
        string html = Encoding.UTF8.GetString(EmbeddedAssets.Get("index.html"));
        string token = viewLevel == ViewLevel.Admin
            ? AccessToken
            : viewLevel == ViewLevel.Shared ? ShareToken : string.Empty;
        string tokenQuery = string.IsNullOrEmpty(token)
            ? string.Empty
            : "?token=" + Uri.EscapeDataString(token);
        html = html.Replace("{{TOKEN_QUERY}}", tokenQuery);
        html = html.Replace("{{TOKEN_VALUE}}", HtmlAttributeEncode(token));
        WriteBytes(
            response,
            HttpStatusCode.OK,
            "text/html; charset=utf-8",
            Encoding.UTF8.GetBytes(html),
            "no-store");
    }

    private void ServeAsset(HttpListenerResponse response, string name)
    {
        string? assetName;
        string? contentType;
        switch (name)
        {
            case "leaflet.js":
                assetName = "leaflet.js";
                contentType = "application/javascript; charset=utf-8";
                break;
            case "leaflet.css":
                assetName = "leaflet.css";
                contentType = "text/css; charset=utf-8";
                break;
            case "app.js":
                assetName = "app.js";
                contentType = "application/javascript; charset=utf-8";
                break;
            case "icons.js":
                assetName = "icons.js";
                contentType = "application/javascript; charset=utf-8";
                break;
            case "app.css":
                assetName = "app.css";
                contentType = "text/css; charset=utf-8";
                break;
            case "icon-192.png":
                assetName = "icon-192.png";
                contentType = "image/png";
                break;
            case "icon-64.png":
                assetName = "icon-64.png";
                contentType = "image/png";
                break;
            case "metamorphous-v22-latin-regular.woff2":
                assetName = "metamorphous-v22-latin-regular.woff2";
                contentType = "font/woff2";
                break;
            case "averia-serif-libre-v19-latin-regular.woff2":
                assetName = "averia-serif-libre-v19-latin-regular.woff2";
                contentType = "font/woff2";
                break;
            case "averia-serif-libre-v19-latin-italic.woff2":
                assetName = "averia-serif-libre-v19-latin-italic.woff2";
                contentType = "font/woff2";
                break;
            case "averia-serif-libre-v19-latin-700.woff2":
                assetName = "averia-serif-libre-v19-latin-700.woff2";
                contentType = "font/woff2";
                break;
            case "averia-serif-libre-v19-latin-700italic.woff2":
                assetName = "averia-serif-libre-v19-latin-700italic.woff2";
                contentType = "font/woff2";
                break;
            case "OFL-Metamorphous.txt":
                assetName = "OFL-Metamorphous.txt";
                contentType = "text/plain; charset=utf-8";
                break;
            case "OFL-AveriaSerifLibre.txt":
                assetName = "OFL-AveriaSerifLibre.txt";
                contentType = "text/plain; charset=utf-8";
                break;
            default:
                assetName = null;
                contentType = null;
                break;
        }

        if (assetName == null || contentType == null)
        {
            WriteBytes(response, HttpStatusCode.NotFound, "text/plain", Array.Empty<byte>(), "no-store");
            return;
        }

        WriteBytes(
            response,
            HttpStatusCode.OK,
            contentType,
            EmbeddedAssets.Get(assetName),
            "public, max-age=3600");
    }

    private void ServeFavicon(HttpListenerResponse response)
    {
        WriteBytes(
            response,
            HttpStatusCode.OK,
            "image/x-icon",
            EmbeddedAssets.Get("favicon.ico"),
            "public, max-age=3600");
    }

    private void ServeStatus(HttpListenerResponse response, ViewLevel viewLevel)
    {
        LiveMapSnapshot snapshot = _getSnapshot();
        string json = BuildStatusJson(snapshot, viewLevel, out _);
        WriteJson(response, HttpStatusCode.OK, json);
    }

    private string BuildStatusJson(
        LiveMapSnapshot snapshot,
        ViewLevel viewLevel,
        out string changeKey)
    {
        bool seesAllPlayers = SeesAllPlayers(viewLevel);
        int visiblePlayers = 0;
        for (int index = 0; index < snapshot.Players.Length; index++)
        {
            if (seesAllPlayers || snapshot.Players[index].IsPublic)
            {
                visiblePlayers++;
            }
        }

        bool consoleAvailable = viewLevel == ViewLevel.Admin &&
                                _config.ConsoleEnabled &&
                                !string.IsNullOrEmpty(AccessToken) &&
                                _consoleBridge != null;
        bool hasSharedMapAccess = viewLevel != ViewLevel.Public;
        bool entitiesAvailable = hasSharedMapAccess && _config.EntityLayer;
        string mapState = _renderer.StateName;
        string mapProgress = JsonWriter.Number(_renderer.Progress);
        string renderRevision = _renderer.RenderRevision;
        string topoState = _renderer.GetStyleStateName(MapStyle.Topo);
        string topoProgress = JsonWriter.Number(_renderer.GetStyleProgress(MapStyle.Topo));
        string topoRevision = _renderer.GetStyleRevision(MapStyle.Topo);
        string chartState = _renderer.GetStyleStateName(MapStyle.Chart);
        string chartProgress = JsonWriter.Number(_renderer.GetStyleProgress(MapStyle.Chart));
        string chartRevision = _renderer.GetStyleRevision(MapStyle.Chart);
        string fogMode = GetEffectiveFogMode(viewLevel);
        FogMaskSnapshot fogSnapshot = _fogTracker.Snapshot;
        long fogRevision = fogMode == "off" ? 0 : fogSnapshot.Revision;
        double exploredPct = GetExploredPercentage(fogSnapshot);
        EntityMapSnapshot entitySnapshot = _getEntitySnapshot();
        RaidEventSnapshot? activeEvent = hasSharedMapAccess
            ? entitySnapshot.Event
            : null;
        long snapshotAgeMs = snapshot.UnixMs == 0
            ? 0
            : Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - snapshot.UnixMs);
        bool snapshotStale = snapshot.UnixMs != 0 &&
                             snapshotAgeMs > Math.Max(0.25f, _getEffectiveUpdateSeconds()) * 3000.0;
        long lastSavedUnixMs = WorldSavePatch.LastSavedUnixMs;
        string globalKeysChangeKey = JoinSortedStrings(snapshot.GlobalKeys);
        string modifiersChangeKey = JoinSortedStrings(snapshot.Modifiers);
        var json = new StringBuilder(640);
        json.Append('{');
        json.Append("\"serverName\":").Append(JsonWriter.Quote(snapshot.ServerName));
        json.Append(",\"worldName\":").Append(JsonWriter.Quote(snapshot.WorldName));
        json.Append(",\"pluginVersion\":").Append(JsonWriter.Quote(ValheimOnePlugin.PluginVersion));
        json.Append(",\"day\":").Append(snapshot.Day.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"timeOfDay\":").Append(JsonWriter.Number(snapshot.TimeOfDay));
        json.Append(",\"windDirDeg\":").Append(JsonWriter.Number(Math.Round(snapshot.WindDirDeg, 1)));
        json.Append(",\"windIntensity\":").Append(JsonWriter.Number(Math.Round(snapshot.WindIntensity, 3)));
        json.Append(",\"globalKeys\":");
        AppendStringArray(json, snapshot.GlobalKeys);
        json.Append(",\"modifiers\":");
        AppendStringArray(json, snapshot.Modifiers);
        json.Append(",\"exploredPct\":").Append(JsonWriter.Number(Math.Round(exploredPct, 1)));
        json.Append(",\"players\":").Append(visiblePlayers.ToString(CultureInfo.InvariantCulture));
        int maxPlayers = ValheimOne.Modules.ServerHostModule.EffectiveMaxPlayers();
        json.Append(",\"maxPlayers\":").Append(maxPlayers.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"view\":").Append(JsonWriter.Quote(
            viewLevel == ViewLevel.Admin
                ? "admin"
                : viewLevel == ViewLevel.Shared ? "shared" : "public"));
        json.Append(",\"console\":").Append(consoleAvailable ? "true" : "false");
        if (hasSharedMapAccess)
        {
            json.Append(",\"entities\":").Append(entitiesAvailable ? "true" : "false");
            json.Append(",\"event\":");
            AppendRaidEventJson(json, activeEvent);
        }

        json.Append(",\"map\":{");
        json.Append("\"state\":").Append(JsonWriter.Quote(mapState));
        json.Append(",\"progress\":").Append(mapProgress);
        json.Append(",\"renderRevision\":").Append(JsonWriter.Quote(renderRevision));
        json.Append(",\"textureSize\":").Append(_renderer.TextureSize.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"pixelSize\":").Append(JsonWriter.Number(WorldMapRenderer.PixelSize));
        json.Append(",\"worldRadius\":").Append(WorldMapRenderer.WorldRadius.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"baseZoom\":").Append(_renderer.BaseMaximumZoom.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"maxZoom\":").Append(_renderer.MaximumZoom.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"styles\":{");
        json.Append("\"topo\":{");
        json.Append("\"state\":").Append(JsonWriter.Quote(topoState));
        json.Append(",\"progress\":").Append(topoProgress);
        json.Append(",\"revision\":").Append(JsonWriter.Quote(topoRevision));
        json.Append("},\"chart\":{");
        json.Append("\"state\":").Append(JsonWriter.Quote(chartState));
        json.Append(",\"progress\":").Append(chartProgress);
        json.Append(",\"revision\":").Append(JsonWriter.Quote(chartRevision));
        json.Append("}}");
        json.Append(",\"fog\":{");
        json.Append("\"mode\":").Append(JsonWriter.Quote(fogMode));
        json.Append(",\"revision\":").Append(fogRevision.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"size\":").Append(FogTracker.Size.ToString(CultureInfo.InvariantCulture));
        json.Append("}}");
        json.Append(",\"unixMs\":").Append(snapshot.UnixMs.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"snapshotAgeMs\":").Append(snapshotAgeMs.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"lastSavedUnixMs\":").Append(
            lastSavedUnixMs.ToString(CultureInfo.InvariantCulture));
        if (snapshotStale)
        {
            json.Append(",\"stale\":true");
        }

        json.Append('}');

        var key = new StringBuilder(96);
        key.Append(snapshot.Day.ToString(CultureInfo.InvariantCulture)).Append('|');
        key.Append(JsonWriter.Number(Math.Round(snapshot.TimeOfDay, 3))).Append('|');
        double roundedWindDir = (Math.Round(snapshot.WindDirDeg / 5.0) * 5.0) % 360.0;
        key.Append(JsonWriter.Number(roundedWindDir)).Append('|');
        double roundedWindIntensity = Math.Round(snapshot.WindIntensity / 0.05) * 0.05;
        key.Append(JsonWriter.Number(roundedWindIntensity)).Append('|');
        key.Append(globalKeysChangeKey).Append('|');
        key.Append(modifiersChangeKey).Append('|');
        key.Append(JsonWriter.Number(Math.Round(exploredPct, 1))).Append('|');
        key.Append(visiblePlayers.ToString(CultureInfo.InvariantCulture)).Append('|');
        key.Append(maxPlayers.ToString(CultureInfo.InvariantCulture)).Append('|');
        key.Append(mapState).Append('|');
        key.Append(mapProgress).Append('|');
        key.Append(renderRevision).Append('|');
        key.Append(topoState).Append('|');
        key.Append(topoProgress).Append('|');
        key.Append(topoRevision).Append('|');
        key.Append(chartState).Append('|');
        key.Append(chartProgress).Append('|');
        key.Append(chartRevision).Append('|');
        key.Append(fogRevision.ToString(CultureInfo.InvariantCulture)).Append('|');
        long lastSavedMinute = lastSavedUnixMs > 0L ? lastSavedUnixMs / 60000L : 0L;
        key.Append(lastSavedMinute.ToString(CultureInfo.InvariantCulture)).Append('|');
        key.Append(snapshotStale ? "stale" : "fresh");
        if (hasSharedMapAccess)
        {
            key.Append('|').Append(entitiesAvailable ? "entities" : "no-entities");
            key.Append('|');
            if (activeEvent == null)
            {
                key.Append("no-event");
            }
            else
            {
                key.Append(activeEvent.Name).Append('|');
                key.Append(JsonWriter.Number(Math.Round(activeEvent.Elapsed)));
            }
        }

        changeKey = key.ToString();
        return json.ToString();
    }

    private static void AppendStringArray(StringBuilder json, string[] values)
    {
        json.Append('[');
        for (int index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            json.Append(JsonWriter.Quote(values[index]));
        }

        json.Append(']');
    }

    private static string JoinSortedStrings(string[] values)
    {
        string[] sorted = (string[])values.Clone();
        Array.Sort(sorted, StringComparer.Ordinal);
        return string.Join(",", sorted);
    }

    private void ServeEvents(
        HttpListenerRequest request,
        HttpListenerResponse response,
        ViewLevel viewLevel)
    {
        int streamCount = Interlocked.Increment(ref _eventStreamCount);
        try
        {
            if (streamCount > MaximumEventStreams)
            {
                WriteJson(
                    response,
                    HttpStatusCode.Conflict,
                    "{\"error\":\"too many event streams\"}");
                return;
            }

            bool sendLogs = viewLevel == ViewLevel.Admin &&
                            _config.ConsoleEnabled &&
                            HasConsoleToken(request) &&
                            _consoleBridge != null &&
                            _logRingBuffer != null;

            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/event-stream";
            response.Headers[HttpResponseHeader.CacheControl] = "no-store";
            response.SendChunked = true;
            response.KeepAlive = true;

            Stream output = response.OutputStream;
            long pingCursor = MapPingPatch.LatestCursor;
            var pendingPings = new List<MapPingSnapshot>(16);
            bool sendActivity = viewLevel != ViewLevel.Public;
            long activityCursor = _activityLog.LatestActivityCursor;
            var pendingActivity = new List<ActivityFeedEntry>(EventStreamActivityBatchSize);
            WriteEventStreamText(output, "retry: 5000\n\n");

            LiveMapSnapshot snapshot = _getSnapshot();
            LiveMapSnapshot lastPlayerSnapshot = snapshot;
            WriteEventStreamEvent(output, "players", BuildPlayersJson(snapshot, viewLevel));

            string statusJson = BuildStatusJson(snapshot, viewLevel, out string statusChangeKey);
            WriteEventStreamEvent(output, "status", statusJson);

            long logCursor = 0L;
            if (sendLogs)
            {
                string logJson = BuildConsoleLogJson(
                    logCursor,
                    EventStreamLogBatchSize,
                    out logCursor,
                    out _);
                WriteEventStreamEvent(output, "log", logJson);
            }

            int idleTicks = 0;
            while (!_stopping)
            {
                Thread.Sleep(EventStreamTickMilliseconds);
                if (_stopping)
                {
                    break;
                }

                bool sentEvent = false;
                snapshot = _getSnapshot();
                if (!ReferenceEquals(snapshot, lastPlayerSnapshot))
                {
                    WriteEventStreamEvent(output, "players", BuildPlayersJson(snapshot, viewLevel));
                    lastPlayerSnapshot = snapshot;
                    sentEvent = true;
                }

                statusJson = BuildStatusJson(snapshot, viewLevel, out string nextStatusChangeKey);
                if (!string.Equals(statusChangeKey, nextStatusChangeKey, StringComparison.Ordinal))
                {
                    WriteEventStreamEvent(output, "status", statusJson);
                    statusChangeKey = nextStatusChangeKey;
                    sentEvent = true;
                }

                pingCursor = MapPingPatch.CopyAfter(pingCursor, pendingPings);
                for (int index = 0; index < pendingPings.Count; index++)
                {
                    WriteEventStreamEvent(output, "ping", BuildPingJson(pendingPings[index]));
                    sentEvent = true;
                }

                if (sendActivity && _activityLog.ActivityFeedEnabled)
                {
                    string activityJson = BuildActivityJson(
                        activityCursor,
                        EventStreamActivityBatchSize,
                        pendingActivity,
                        out long nextActivityCursor,
                        out int activityCount);
                    if (activityCount > 0)
                    {
                        WriteEventStreamEvent(output, "activity", activityJson);
                        activityCursor = nextActivityCursor;
                        sentEvent = true;
                    }
                }

                if (sendLogs)
                {
                    string logJson = BuildConsoleLogJson(
                        logCursor,
                        EventStreamLogBatchSize,
                        out long nextLogCursor,
                        out int logLineCount);
                    if (logLineCount > 0)
                    {
                        WriteEventStreamEvent(output, "log", logJson);
                        logCursor = nextLogCursor;
                        sentEvent = true;
                    }
                }

                if (sentEvent)
                {
                    idleTicks = 0;
                }
                else if (++idleTicks >= EventStreamHeartbeatTicks)
                {
                    WriteEventStreamText(output, ": ping\n\n");
                    idleTicks = 0;
                }
            }
        }
        catch (HttpListenerException)
        {
            // The event-stream client disconnected.
        }
        catch (IOException)
        {
            // The event-stream client disconnected.
        }
        catch (ObjectDisposedException)
        {
            // The event-stream client disconnected or the server is stopping.
        }
        catch (InvalidOperationException)
        {
            // The event-stream response is no longer writable.
        }
        finally
        {
            Interlocked.Decrement(ref _eventStreamCount);
            TryCloseEventStream(response);
        }
    }

    private void ServeConsoleExec(HttpListenerRequest request, HttpListenerResponse response)
    {
        ReadAuditIdentity(request, out string operatorName, out string source);
        if (!TryReadRequiredString(request, response, "command", "command is required", out string line))
        {
            _activityLog.RecordAdminCommand(
                operatorName,
                string.Empty,
                "error",
                "command is required",
                source,
                string.Empty,
                appendHistory: false);
            return;
        }

        int separator = FindWhitespace(line);
        string commandName = separator < 0
            ? line.ToLowerInvariant()
            : line.Substring(0, separator).ToLowerInvariant();
        if (!_config.AllowAllCommands &&
            !string.Equals(commandName, "vo", StringComparison.Ordinal) &&
            !_config.ConsoleWhitelist.Contains(commandName))
        {
            const string error = "command not whitelisted";
            _activityLog.RecordAdminCommand(
                operatorName,
                line,
                "error",
                error,
                source,
                error,
                appendHistory: false);
            WriteJson(
                response,
                HttpStatusCode.Forbidden,
                "{\"ok\":false,\"error\":\"command not whitelisted\"}");
            return;
        }

        ConsoleExecResult result = _consoleBridge!.ExecuteCommand(line);
        if (!result.Ok)
        {
            _activityLog.RecordAdminCommand(
                operatorName,
                line,
                "error",
                result.Error,
                source,
                result.Error,
                appendHistory: true);
            HttpStatusCode status = string.Equals(
                result.Error,
                "unknown command",
                StringComparison.Ordinal)
                ? HttpStatusCode.BadRequest
                : HttpStatusCode.OK;
            WriteJson(
                response,
                status,
                "{\"ok\":false,\"error\":" + JsonWriter.Quote(result.Error) + "}");
            return;
        }

        _activityLog.RecordAdminCommand(
            operatorName,
            line,
            "ok",
            "completed",
            source,
            string.Join("\n", result.Output),
            appendHistory: true);

        var json = new StringBuilder(32 + (result.Output.Count * 64));
        json.Append("{\"ok\":true,\"output\":[");
        for (int index = 0; index < result.Output.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            json.Append(JsonWriter.Quote(result.Output[index]));
        }

        json.Append("]}");
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private void ServeConsoleLog(HttpListenerRequest request, HttpListenerResponse response)
    {
        long cursor = ParseLong(request.QueryString["cursor"], 0L);
        long requestedMaximum = ParseLong(request.QueryString["max"], 200L);
        int maximum = (int)Math.Max(1L, Math.Min(500L, requestedMaximum));
        string json = BuildConsoleLogJson(cursor, maximum, out _, out _);
        WriteJson(response, HttpStatusCode.OK, json);
    }

    private void ServeConsoleHistory(HttpListenerRequest request, HttpListenerResponse response)
    {
        long cursor = ParseLong(request.QueryString["cursor"], 0L);
        long requestedMaximum = ParseLong(request.QueryString["max"], 200L);
        int maximum = (int)Math.Max(1L, Math.Min(200L, requestedMaximum));
        var entries = new List<ConsoleHistoryEntry>(maximum);
        long latestCursor = _activityLog.CopyConsoleHistoryAfter(cursor, maximum, entries);
        var json = new StringBuilder(32 + (entries.Count * 192));
        json.Append("{\"cursor\":");
        json.Append(latestCursor.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"entries\":[");
        for (int index = 0; index < entries.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            ConsoleHistoryEntry entry = entries[index];
            json.Append('{');
            json.Append("\"id\":").Append(entry.Id.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"operator\":").Append(JsonWriter.Quote(entry.Operator));
            json.Append(",\"command\":").Append(JsonWriter.Quote(entry.Command));
            json.Append(",\"output\":").Append(JsonWriter.Quote(entry.Output));
            json.Append(",\"t\":").Append(entry.UnixMs.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"status\":").Append(JsonWriter.Quote(entry.Status));
            json.Append('}');
        }

        json.Append("]}");
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private void ServeActivity(
        HttpListenerRequest request,
        HttpListenerResponse response,
        ViewLevel viewLevel)
    {
        if (viewLevel == ViewLevel.Public)
        {
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

        long cursor = Math.Max(0L, ParseLong(request.QueryString["cursor"], 0L));
        var entries = new List<ActivityFeedEntry>(EventStreamActivityBatchSize);
        string json = BuildActivityJson(
            cursor,
            EventStreamActivityBatchSize,
            entries,
            out _,
            out _);
        WriteJson(response, HttpStatusCode.OK, json);
    }

    private string BuildActivityJson(
        long cursor,
        int maximum,
        List<ActivityFeedEntry> entries,
        out long latestCursor,
        out int eventCount)
    {
        bool enabled = _activityLog.ActivityFeedEnabled;
        entries.Clear();
        latestCursor = enabled
            ? _activityLog.CopyActivityAfter(cursor, maximum, entries)
            : _activityLog.LatestActivityCursor;
        eventCount = entries.Count;

        var json = new StringBuilder(48 + (entries.Count * 128));
        json.Append("{\"enabled\":").Append(enabled ? "true" : "false");
        json.Append(",\"cursor\":");
        json.Append(latestCursor.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"events\":[");
        for (int index = 0; index < entries.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            ActivityFeedEntry entry = entries[index];
            json.Append('{');
            json.Append("\"id\":").Append(entry.Id.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"unixMs\":").Append(
                entry.UnixMs.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"type\":").Append(JsonWriter.Quote(entry.Type));
            json.Append(",\"data\":").Append(entry.DataJson);
            json.Append('}');
        }

        json.Append("]}");
        return json.ToString();
    }

    private string BuildConsoleLogJson(
        long cursor,
        int maximum,
        out long latestCursor,
        out int lineCount)
    {
        var entries = new List<LogEntry>(maximum);
        latestCursor = _logRingBuffer!.CopyAfter(cursor, maximum, entries);
        lineCount = entries.Count;
        var json = new StringBuilder(32 + (entries.Count * 128));
        json.Append("{\"cursor\":");
        json.Append(latestCursor.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"lines\":[");
        for (int index = 0; index < entries.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            LogEntry entry = entries[index];
            json.Append('{');
            json.Append("\"seq\":").Append(entry.Seq.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"time\":").Append(entry.UnixMs.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"level\":").Append(JsonWriter.Quote(entry.Level));
            json.Append(",\"source\":").Append(JsonWriter.Quote(entry.Source));
            json.Append(",\"text\":").Append(JsonWriter.Quote(entry.Message));
            json.Append('}');
        }

        json.Append("]}");
        return json.ToString();
    }

    private void ServeConsoleMeta(HttpListenerResponse response)
    {
        bool allowAll = _config.AllowAllCommands;
        var whitelist = new List<string>(_config.ConsoleWhitelist);
        whitelist.Sort(StringComparer.Ordinal);
        List<ConsoleCommandInfo> commands = _consoleBridge!.GetKnownCommands();
        var json = new StringBuilder(256 + (whitelist.Count * 16) + (commands.Count * 160));
        json.Append("{\"allowAll\":").Append(allowAll ? "true" : "false");
        json.Append(",\"whitelist\":[");
        for (int index = 0; index < whitelist.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            json.Append(JsonWriter.Quote(whitelist[index]));
        }

        json.Append("],\"commands\":[");
        bool needsComma = false;
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<VoCommandDefinition> voCommands = VoCommandRegistry.All;
        for (int index = 0; index < voCommands.Count; index++)
        {
            VoCommandDefinition definition = voCommands[index];
            string name = "vo " + definition.Name;
            AppendConsoleCommandMetadata(
                json,
                ref needsComma,
                name,
                definition.Description,
                isCheat: false,
                definition);
            emitted.Add(name);
        }

        for (int index = 0; index < commands.Count; index++)
        {
            ConsoleCommandInfo command = commands[index];
            string lowerName = command.Name.ToLowerInvariant();
            bool implicitlyAllowed = string.Equals(lowerName, "vo", StringComparison.Ordinal);
            if (!allowAll && !implicitlyAllowed && !whitelist.Contains(lowerName))
            {
                continue;
            }

            if (emitted.Contains(command.Name))
            {
                continue;
            }

            VoCommandDefinition? definition = null;
            if (VoCommandRegistry.TryGetVanilla(lowerName, out VoCommandDefinition? curated))
            {
                definition = curated;
            }

            string description = definition?.Description ?? command.Description;
            AppendConsoleCommandMetadata(
                json,
                ref needsComma,
                command.Name,
                description,
                command.IsCheat,
                definition);
            emitted.Add(command.Name);
        }

        IReadOnlyList<VoCommandDefinition> vanillaCommands = VoCommandRegistry.Vanilla;
        for (int index = 0; index < vanillaCommands.Count; index++)
        {
            VoCommandDefinition definition = vanillaCommands[index];
            if (emitted.Contains(definition.Name) ||
                (!allowAll && !whitelist.Contains(definition.Name)))
            {
                continue;
            }

            AppendConsoleCommandMetadata(
                json,
                ref needsComma,
                definition.Name,
                definition.Description,
                string.Equals(definition.Name, "sleep", StringComparison.Ordinal),
                definition);
            emitted.Add(definition.Name);
        }

        json.Append("]}");
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private static void AppendConsoleCommandMetadata(
        StringBuilder json,
        ref bool needsComma,
        string name,
        string description,
        bool isCheat,
        VoCommandDefinition? definition)
    {
        if (needsComma)
        {
            json.Append(',');
        }

        json.Append('{');
        json.Append("\"name\":").Append(JsonWriter.Quote(name));
        json.Append(",\"description\":").Append(JsonWriter.Quote(description));
        json.Append(",\"cheat\":").Append(isCheat ? "true" : "false");
        json.Append(",\"usage\":").Append(JsonWriter.Quote(definition?.Usage ?? name));
        json.Append(",\"category\":").Append(JsonWriter.Quote(definition?.Category ?? "server"));
        json.Append(",\"examples\":[");
        string[] examples = definition?.Examples ?? Array.Empty<string>();
        for (int index = 0; index < examples.Length; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            json.Append(JsonWriter.Quote(examples[index]));
        }

        json.Append(']');
        json.Append(",\"playerArg\":").Append(definition?.PlayerArg == true ? "true" : "false");
        json.Append('}');
        needsComma = true;
    }

    private void ServeAdminAction(
        HttpListenerRequest request,
        HttpListenerResponse response,
        string actionName,
        Func<string, ConsoleActionResult> action)
    {
        ReadAuditIdentity(request, out string operatorName, out string source);
        if (!TryReadRequiredString(request, response, "player", "player is required", out string player))
        {
            _activityLog.RecordAdminAction(
                operatorName,
                actionName,
                null,
                "error",
                "player is required",
                source);
            return;
        }

        ConsoleActionResult result = action(player);
        _activityLog.RecordAdminAction(
            operatorName,
            actionName,
            player,
            result.Ok ? "ok" : "error",
            result.Ok ? "completed" : result.Error,
            source);
        string json = result.Ok
            ? "{\"ok\":true}"
            : "{\"ok\":false,\"error\":" + JsonWriter.Quote(result.Error) + "}";
        WriteJson(response, HttpStatusCode.OK, json);
    }

    private void ServeBanList(HttpListenerResponse response)
    {
        ConsoleBanListResult result = _consoleBridge!.BanList();
        if (!result.Ok)
        {
            WriteJson(
                response,
                HttpStatusCode.OK,
                "{\"ok\":false,\"error\":" + JsonWriter.Quote(result.Error) + "}");
            return;
        }

        var json = new StringBuilder(16 + (result.Banned.Count * 24));
        json.Append("{\"banned\":[");
        for (int index = 0; index < result.Banned.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            json.Append(JsonWriter.Quote(result.Banned[index]));
        }

        json.Append("]}");
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private void ServeSave(HttpListenerRequest request, HttpListenerResponse response)
    {
        ReadAuditIdentity(request, out string operatorName, out string source);
        ConsoleSaveResult result = _consoleBridge!.Save();
        _activityLog.RecordAdminAction(
            operatorName,
            "save",
            null,
            result.Ok ? "ok" : "error",
            result.Ok
                ? result.AlreadySaving ? "already saving" : "save requested"
                : result.Error,
            source);
        string json = result.Ok
            ? "{\"ok\":true,\"alreadySaving\":" + (result.AlreadySaving ? "true" : "false") + "}"
            : "{\"ok\":false,\"error\":" + JsonWriter.Quote(result.Error) + "}";
        WriteJson(response, HttpStatusCode.OK, json);
    }

    private void ServeStats(HttpListenerResponse response)
    {
        StatsSnapshot stats = _consoleBridge!.Stats;
        LiveMapSnapshot snapshot = _getSnapshot();
        var json = new StringBuilder(256);
        json.Append('{');
        json.Append("\"uptimeSeconds\":").Append(JsonWriter.Number(stats.UptimeSeconds));
        json.Append(",\"players\":").Append(stats.Players.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"peers\":").Append(stats.Peers.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"zdoCount\":").Append(stats.ZdoCount.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"monoHeapBytes\":").Append(stats.MonoHeapBytes.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"frameAvgMs\":").Append(JsonWriter.Number(stats.FrameAvgMs));
        json.Append(",\"frameMaxMs\":").Append(JsonWriter.Number(stats.FrameMaxMs));
        json.Append(",\"snapshotUnixMs\":").Append(stats.SnapshotUnixMs.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"worldName\":").Append(JsonWriter.Quote(snapshot.WorldName));
        json.Append(",\"day\":").Append(snapshot.Day.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"timeOfDay\":").Append(JsonWriter.Number(snapshot.TimeOfDay));
        ActivityLogHealthSnapshot activityHealth = _activityLog.GetHealth();
        json.Append(",\"activityLog\":{");
        json.Append("\"enabled\":").Append(activityHealth.Enabled ? "true" : "false");
        json.Append(",\"currentFile\":").Append(
            JsonWriter.Quote(activityHealth.CurrentFileName));
        json.Append(",\"eventsWrittenToday\":").Append(
            activityHealth.EventsWrittenToday.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"lastWriteAgeSeconds\":");
        if (activityHealth.LastWriteAgeSeconds.HasValue)
        {
            json.Append(JsonWriter.Number(activityHealth.LastWriteAgeSeconds.Value));
        }
        else
        {
            json.Append("null");
        }

        json.Append('}');
        json.Append('}');
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private void ServePlayers(HttpListenerResponse response, ViewLevel viewLevel)
    {
        LiveMapSnapshot snapshot = _getSnapshot();
        string json = BuildPlayersJson(snapshot, viewLevel);
        WriteJson(response, HttpStatusCode.OK, json);
    }

    private string BuildPlayersJson(LiveMapSnapshot snapshot, ViewLevel viewLevel)
    {
        bool seesAllPlayers = SeesAllPlayers(viewLevel);
        bool hasSharedMapAccess = viewLevel != ViewLevel.Public;
        bool showNames = hasSharedMapAccess || _publicShowPlayerNames;
        long snapshotAgeMs = snapshot.UnixMs == 0
            ? 0
            : Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - snapshot.UnixMs);
        bool snapshotStale = snapshot.UnixMs != 0 &&
                             snapshotAgeMs > Math.Max(0.25f, _getEffectiveUpdateSeconds()) * 3000.0;
        var json = new StringBuilder(128 + (snapshot.Players.Length * 96));
        json.Append("{\"players\":[");
        bool needsComma = false;
        for (int index = 0; index < snapshot.Players.Length; index++)
        {
            LiveMapPlayerSnapshot player = snapshot.Players[index];
            if (!seesAllPlayers && !player.IsPublic)
            {
                continue;
            }

            if (needsComma)
            {
                json.Append(',');
            }

            json.Append('{');
            json.Append("\"name\":").Append(JsonWriter.Quote(showNames ? player.Name : string.Empty));
            json.Append(",\"x\":").Append(JsonWriter.Number(player.X));
            json.Append(",\"y\":").Append(JsonWriter.Number(player.Y));
            json.Append(",\"z\":").Append(JsonWriter.Number(player.Z));
            if (hasSharedMapAccess)
            {
                json.Append(",\"id\":").Append(JsonWriter.Quote(
                    player.Id.ToString(CultureInfo.InvariantCulture)));
                json.Append(",\"biome\":").Append(JsonWriter.Quote(player.Biome));
                json.Append(",\"speedMps\":").Append(JsonWriter.Number(
                    Math.Round(player.SpeedMps, 2)));
                json.Append(",\"headingDeg\":").Append(JsonWriter.Number(
                    Math.Round(player.HeadingDeg, 1)));
                json.Append(",\"sessionStartUnixMs\":").Append(
                    player.SessionStartUnixMs.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"distanceTodayM\":").Append(JsonWriter.Number(
                    Math.Round(player.DistanceTodayM, 1)));
                json.Append(",\"health\":").Append(JsonWriter.Number(
                    Math.Round(player.Health, 1)));
                json.Append(",\"maxHealth\":").Append(JsonWriter.Number(
                    Math.Round(player.MaxHealth, 1)));
                json.Append(",\"dead\":").Append(player.Dead ? "true" : "false");
                json.Append(",\"pvp\":").Append(player.Pvp ? "true" : "false");
                json.Append(",\"inBed\":").Append(player.InBed ? "true" : "false");
            }

            json.Append('}');
            needsComma = true;
        }

        json.Append("],\"unixMs\":").Append(snapshot.UnixMs.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"snapshotAgeMs\":").Append(snapshotAgeMs.ToString(CultureInfo.InvariantCulture));
        if (snapshotStale)
        {
            json.Append(",\"stale\":true");
        }

        json.Append('}');
        return json.ToString();
    }

    private void ServeTrail(
        HttpListenerRequest request,
        HttpListenerResponse response,
        ViewLevel viewLevel)
    {
        string requestedId = (request.QueryString["id"] ?? string.Empty).Trim();
        if (!TryResolveHistoryKey(
                requestedId,
                out string historyKey,
                out bool isPlayer,
                out long playerId))
        {
            WriteJson(response, HttpStatusCode.BadRequest, "{\"error\":\"bad request\"}");
            return;
        }

        if (isPlayer)
        {
            if (viewLevel == ViewLevel.Public && !IsPlayerVisibleToPublic(playerId))
            {
                WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
                return;
            }
        }
        else if (viewLevel == ViewLevel.Public || !_config.EntityLayer)
        {
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

        long windowSeconds = Math.Max(
            60L,
            Math.Min(1800L, ParseLong(request.QueryString["window"], 1800L)));
        PositionSample[] points = _positionHistory.Snapshot(
            historyKey,
            windowSeconds * 1000L);
        long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var json = new StringBuilder(96 + (points.Length * 48));
        json.Append('{');
        json.Append("\"id\":").Append(JsonWriter.Quote(historyKey));
        json.Append(",\"window\":").Append(
            windowSeconds.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"points\":[");
        for (int index = 0; index < points.Length; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            PositionSample point = points[index];
            json.Append('{');
            json.Append("\"x\":").Append(JsonWriter.Number(point.X));
            json.Append(",\"z\":").Append(JsonWriter.Number(point.Z));
            json.Append(",\"t\":").Append(
                point.UnixMs.ToString(CultureInfo.InvariantCulture));
            json.Append('}');
        }

        json.Append("],\"unixMs\":").Append(unixMs.ToString(CultureInfo.InvariantCulture));
        json.Append('}');
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private bool IsPlayerVisibleToPublic(long playerId)
    {
        if (!_publicView)
        {
            return false;
        }

        LiveMapPlayerSnapshot[] players = _getSnapshot().Players;
        for (int index = 0; index < players.Length; index++)
        {
            if (players[index].Id == playerId)
            {
                return SeesAllPlayers(ViewLevel.Public) || players[index].IsPublic;
            }
        }

        return false;
    }

    private static bool TryResolveHistoryKey(
        string requestedId,
        out string historyKey,
        out bool isPlayer,
        out long playerId)
    {
        historyKey = string.Empty;
        isPlayer = false;
        playerId = 0L;
        if (string.IsNullOrEmpty(requestedId))
        {
            return false;
        }

        if (requestedId.StartsWith("player:", StringComparison.Ordinal))
        {
            string value = requestedId.Substring("player:".Length);
            isPlayer = true;
            if (!long.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out playerId))
            {
                return false;
            }

            historyKey = PositionHistory.PlayerKey(playerId);
            return true;
        }

        if (!requestedId.StartsWith("entity:", StringComparison.Ordinal))
        {
            return false;
        }

        string entityValue = requestedId.Substring("entity:".Length);
        if (!EntityTracker.TryParseEntityId(
                entityValue,
                out long userId,
                out uint objectId))
        {
            return false;
        }

        string entityId = userId.ToString(CultureInfo.InvariantCulture) + ":" +
                          objectId.ToString(CultureInfo.InvariantCulture);
        historyKey = PositionHistory.EntityKey(entityId);
        return true;
    }

    private static void ServeHeight(
        HttpListenerRequest request,
        HttpListenerResponse response)
    {
        if (!TryParseWorldCoordinate(request.QueryString["x"], out float x) ||
            !TryParseWorldCoordinate(request.QueryString["z"], out float z))
        {
            WriteJson(response, HttpStatusCode.BadRequest, "{\"error\":\"bad request\"}");
            return;
        }

        if (!LiveMapBehaviour.TryGetGroundHeight(x, z, out float height))
        {
            WriteJson(response, HttpStatusCode.ServiceUnavailable, "{\"error\":\"not ready\"}");
            return;
        }

        var json = new StringBuilder(64);
        json.Append('{');
        json.Append("\"x\":").Append(JsonWriter.Number(x));
        json.Append(",\"z\":").Append(JsonWriter.Number(z));
        json.Append(",\"height\":").Append(JsonWriter.Number(height));
        json.Append('}');
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private void ServePing(
        HttpListenerRequest request,
        HttpListenerResponse response,
        ViewLevel viewLevel)
    {
        if (viewLevel != ViewLevel.Admin)
        {
            WriteJson(
                response,
                HttpStatusCode.Forbidden,
                "{\"ok\":false,\"error\":\"forbidden\"}");
            return;
        }

        if (!TryReadPingRequest(request, response, out float x, out float z, out string label))
        {
            ReadAuditIdentity(request, out string invalidOperator, out string invalidSource);
            _activityLog.RecordAdminAction(
                invalidOperator,
                "ping",
                null,
                "error",
                "invalid request",
                invalidSource);
            return;
        }

        if (!_enqueueMapPing(x, z, label))
        {
            ReadAuditIdentity(request, out string busyOperator, out string busySource);
            _activityLog.RecordAdminAction(
                busyOperator,
                "ping",
                null,
                "error",
                "too many pending pings",
                busySource);
            WriteJson(
                response,
                (HttpStatusCode)429,
                "{\"ok\":false,\"error\":\"too many pending pings\"}");
            return;
        }

        ReadAuditIdentity(request, out string operatorName, out string source);
        _activityLog.RecordAdminAction(
            operatorName,
            "ping",
            null,
            "ok",
            "queued",
            source);

        WriteJson(response, HttpStatusCode.OK, "{\"ok\":true}");
    }

    private static bool TryReadPingRequest(
        HttpListenerRequest request,
        HttpListenerResponse response,
        out float x,
        out float z,
        out string label)
    {
        x = 0f;
        z = 0f;
        label = "Web ping";
        if (!TryReadRequestBody(request, out string json, out bool tooLarge))
        {
            HttpStatusCode status = tooLarge
                ? HttpStatusCode.RequestEntityTooLarge
                : HttpStatusCode.BadRequest;
            string error = tooLarge ? "payload too large" : "bad request";
            WriteJson(
                response,
                status,
                "{\"ok\":false,\"error\":" + JsonWriter.Quote(error) + "}");
            return false;
        }

        if (!TryReadJsonNumberProperty(json, "x", out x) ||
            !TryReadJsonNumberProperty(json, "z", out z) ||
            float.IsNaN(x) || float.IsInfinity(x) ||
            float.IsNaN(z) || float.IsInfinity(z) ||
            ((double)x * x) + ((double)z * z) >
            (double)MaximumPingWorldRadius * MaximumPingWorldRadius)
        {
            WriteJson(
                response,
                HttpStatusCode.BadRequest,
                "{\"ok\":false,\"error\":\"invalid coordinates\"}");
            return false;
        }

        if (TryFindJsonPropertyValue(json, "label", out int labelIndex))
        {
            if (!TryParseJsonString(json, labelIndex, out label))
            {
                WriteJson(
                    response,
                    HttpStatusCode.BadRequest,
                    "{\"ok\":false,\"error\":\"invalid label\"}");
                return false;
            }

            label = label.Trim();
            if (label.Length == 0)
            {
                label = "Web ping";
            }
            else if (label.Length > MaximumPingLabelLength)
            {
                WriteJson(
                    response,
                    HttpStatusCode.BadRequest,
                    "{\"ok\":false,\"error\":\"label too long\"}");
                return false;
            }
        }

        return true;
    }

    private static string BuildPingJson(MapPingSnapshot ping)
    {
        var json = new StringBuilder(96);
        json.Append('{');
        json.Append("\"x\":").Append(JsonWriter.Number(ping.X));
        json.Append(",\"z\":").Append(JsonWriter.Number(ping.Z));
        json.Append(",\"label\":").Append(JsonWriter.Quote(ping.Label));
        json.Append(",\"unixMs\":").Append(
            ping.UnixMs.ToString(CultureInfo.InvariantCulture));
        json.Append('}');
        return json.ToString();
    }

    private void ServeEntities(
        HttpListenerRequest request,
        HttpListenerResponse response,
        ViewLevel viewLevel)
    {
        if (viewLevel == ViewLevel.Public || !_config.EntityLayer)
        {
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

        string requestedFocus = (request.QueryString["focus"] ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(requestedFocus))
        {
            if (requestedFocus.StartsWith("entity:", StringComparison.Ordinal))
            {
                requestedFocus = requestedFocus.Substring("entity:".Length);
            }

            if (!EntityTracker.TryParseEntityId(
                    requestedFocus,
                    out long focusUserId,
                    out uint focusObjectId))
            {
                WriteJson(response, HttpStatusCode.BadRequest, "{\"error\":\"bad request\"}");
                return;
            }

            string focusId = focusUserId.ToString(CultureInfo.InvariantCulture) + ":" +
                             focusObjectId.ToString(CultureInfo.InvariantCulture);
            _noteEntityFocusRequested(focusId);
            ServeEntityFocus(response, focusId);
            return;
        }

        _noteEntitiesRequested();
        EntityMapSnapshot snapshot = _getEntitySnapshot();
        var json = new StringBuilder(64 + (snapshot.Entities.Length * 112));
        json.Append("{\"revision\":");
        json.Append(snapshot.Revision.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"time\":").Append(snapshot.UnixMs.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"entities\":[");
        for (int index = 0; index < snapshot.Entities.Length; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            TrackedEntitySnapshot entity = snapshot.Entities[index];
            json.Append('{');
            json.Append("\"id\":").Append(JsonWriter.Quote(entity.Id));
            json.Append(",\"group\":").Append(JsonWriter.Quote(entity.Group));
            json.Append(",\"prefab\":").Append(JsonWriter.Quote(entity.Prefab));
            json.Append(",\"x\":").Append(JsonWriter.Number(entity.X));
            json.Append(",\"y\":").Append(JsonWriter.Number(entity.Y));
            json.Append(",\"z\":").Append(JsonWriter.Number(entity.Z));
            json.Append(",\"rotYDeg\":").Append(JsonWriter.Number(
                Math.Round(entity.RotYDeg, 1)));
            if (!string.IsNullOrEmpty(entity.Tag))
            {
                json.Append(",\"tag\":").Append(JsonWriter.Quote(entity.Tag));
            }
            if (string.Equals(entity.Group, "tombstone", StringComparison.Ordinal))
            {
                json.Append(",\"owner\":").Append(JsonWriter.Quote(entity.Owner));
                if (entity.DeathAgeSec.HasValue)
                {
                    json.Append(",\"deathAgeSec\":").Append(JsonWriter.Number(
                        Math.Round(entity.DeathAgeSec.Value, 1)));
                }
            }
            else if (string.Equals(entity.Group, "ward", StringComparison.Ordinal))
            {
                json.Append(",\"owner\":").Append(JsonWriter.Quote(entity.Owner));
                json.Append(",\"wardEnabled\":").Append(
                    entity.WardEnabled == true ? "true" : "false");
                json.Append(",\"wardRadius\":").Append(JsonWriter.Number(
                    entity.WardRadius.GetValueOrDefault()));
            }
            else if (string.Equals(entity.Group, "bed", StringComparison.Ordinal))
            {
                json.Append(",\"owner\":").Append(JsonWriter.Quote(entity.Owner));
            }

            json.Append('}');
        }

        json.Append("],\"event\":");
        AppendRaidEventJson(json, snapshot.Event);
        json.Append('}');
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private void ServeEntityFocus(HttpListenerResponse response, string requestedId)
    {
        EntityFocusSnapshot focus = _getEntityFocusSnapshot();
        bool matches = string.Equals(focus.Id, requestedId, StringComparison.Ordinal);
        long unixMs = matches
            ? focus.UnixMs
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var json = new StringBuilder(192);
        // Focus responses contain one normal entity object so clients do not merge scan payloads.
        json.Append("{\"focus\":{");
        json.Append("\"id\":").Append(JsonWriter.Quote(requestedId));
        json.Append(",\"found\":").Append(matches && focus.Found ? "true" : "false");
        if (matches && focus.Found)
        {
            json.Append(",\"group\":").Append(JsonWriter.Quote(focus.Group));
            json.Append(",\"prefab\":").Append(JsonWriter.Quote(focus.Prefab));
            json.Append(",\"x\":").Append(JsonWriter.Number(focus.X));
            json.Append(",\"y\":").Append(JsonWriter.Number(focus.Y));
            json.Append(",\"z\":").Append(JsonWriter.Number(focus.Z));
            json.Append(",\"rotYDeg\":").Append(JsonWriter.Number(
                Math.Round(focus.RotYDeg, 1)));
            if (!string.IsNullOrEmpty(focus.Tag))
            {
                json.Append(",\"tag\":").Append(JsonWriter.Quote(focus.Tag));
            }
        }

        json.Append(",\"unixMs\":").Append(unixMs.ToString(CultureInfo.InvariantCulture));
        json.Append("}}");
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private static void AppendRaidEventJson(StringBuilder json, RaidEventSnapshot? activeEvent)
    {
        if (activeEvent == null)
        {
            json.Append("null");
            return;
        }

        json.Append('{');
        json.Append("\"name\":").Append(JsonWriter.Quote(activeEvent.Name));
        json.Append(",\"x\":").Append(JsonWriter.Number(activeEvent.X));
        json.Append(",\"z\":").Append(JsonWriter.Number(activeEvent.Z));
        json.Append(",\"radius\":").Append(JsonWriter.Number(activeEvent.Radius));
        json.Append(",\"elapsed\":").Append(JsonWriter.Number(activeEvent.Elapsed));
        json.Append(",\"duration\":").Append(JsonWriter.Number(activeEvent.Duration));
        json.Append('}');
    }

    private void ServePois(
        HttpListenerRequest request,
        HttpListenerResponse response,
        ViewLevel viewLevel)
    {
        string requestedGroup = (request.QueryString["group"] ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
        if (!string.IsNullOrEmpty(requestedGroup))
        {
            ServePoiGroup(response, viewLevel, requestedGroup);
            return;
        }

        PoiCatalog catalog = _getPoiCatalog();
        ResourcePoiMapSnapshot resourceSnapshot = _getResourcePoiSnapshot();
        IReadOnlyList<PoiSnapshot> pois = catalog.ServedPois;
        IReadOnlyList<PoiGroupDefinition> definitions = PoiGroups.All;
        FogMaskSnapshot fogSnapshot = _fogTracker.Snapshot;
        long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var json = new StringBuilder(128 + (pois.Count * 96));
        var deferredGroups = new HashSet<string>(StringComparer.Ordinal);
        json.Append("{\"unixMs\":");
        json.Append(unixMs.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"groups\":[");
        bool needsComma = false;
        for (int index = 0; index < definitions.Count; index++)
        {
            PoiGroupDefinition definition = definitions[index];
            if ((viewLevel == ViewLevel.Public && !PoiGroups.IsPublic(definition.Key)) ||
                (definition.Resource && !_config.ResourceLayers))
            {
                continue;
            }

            if (needsComma)
            {
                json.Append(',');
            }

            int count = catalog.GetCount(definition.Key);
            if (definition.Resource &&
                resourceSnapshot.TryGetGroup(
                    definition.Key,
                    out ResourcePoiGroupSnapshot? resourceGroup) &&
                resourceGroup != null)
            {
                count = resourceGroup.Count;
            }

            bool inline = !definition.Resource &&
                          count <= MaximumInlineLocationPoisPerGroup;
            if (!inline)
            {
                deferredGroups.Add(definition.Key);
            }

            json.Append('{');
            json.Append("\"key\":").Append(JsonWriter.Quote(definition.Key));
            json.Append(",\"label\":").Append(JsonWriter.Quote(definition.Label));
            json.Append(",\"category\":").Append(JsonWriter.Quote(definition.Category));
            json.Append(",\"count\":").Append(count.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"inline\":").Append(inline ? "true" : "false");
            json.Append(",\"resource\":").Append(definition.Resource ? "true" : "false");
            if (definition.Resource)
            {
                json.Append(",\"scanUnixMs\":").Append(
                    resourceSnapshot.LastScanUnixMs.ToString(CultureInfo.InvariantCulture));
            }

            json.Append('}');
            needsComma = true;
        }

        json.Append("],\"pois\":[");
        needsComma = false;
        for (int index = 0; index < pois.Count; index++)
        {
            PoiSnapshot poi = pois[index];
            if (viewLevel == ViewLevel.Public && !PoiGroups.IsPublic(poi.Group))
            {
                continue;
            }

            if (deferredGroups.Contains(poi.Group))
            {
                continue;
            }

            if (needsComma)
            {
                json.Append(',');
            }

            json.Append('{');
            json.Append("\"name\":").Append(JsonWriter.Quote(poi.Name));
            json.Append(",\"group\":").Append(JsonWriter.Quote(poi.Group));
            json.Append(",\"x\":").Append(JsonWriter.NumberOneDecimal(poi.X));
            json.Append(",\"z\":").Append(JsonWriter.NumberOneDecimal(poi.Z));
            json.Append(",\"placed\":").Append(poi.Placed ? "true" : "false");
            json.Append(",\"explored\":").Append(
                FogTracker.IsExplored(fogSnapshot, poi.X, poi.Z) ? "true" : "false");
            json.Append('}');
            needsComma = true;
        }

        json.Append("]}");
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private void ServePoiGroup(
        HttpListenerResponse response,
        ViewLevel viewLevel,
        string requestedGroup)
    {
        if (!PoiGroups.TryGet(requestedGroup, out PoiGroupDefinition? definition) ||
            definition == null ||
            (viewLevel == ViewLevel.Public && !PoiGroups.IsPublic(definition.Key)) ||
            (definition.Resource && !_config.ResourceLayers))
        {
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

        if (!definition.Resource)
        {
            ServeLocationPoiGroup(response, definition);
            return;
        }

        ServeResourcePoiGroup(response, definition);
    }

    private void ServeLocationPoiGroup(
        HttpListenerResponse response,
        PoiGroupDefinition definition)
    {
        PoiCatalog catalog = _getPoiCatalog();
        IReadOnlyList<PoiSnapshot> catalogPois = catalog.ServedPois;
        int count = catalog.GetCount(definition.Key);
        FogMaskSnapshot fogSnapshot = _fogTracker.Snapshot;
        var json = new StringBuilder(128 + (count * 96));
        json.Append("{\"group\":").Append(JsonWriter.Quote(definition.Key));
        json.Append(",\"label\":").Append(JsonWriter.Quote(definition.Label));
        json.Append(",\"count\":").Append(count.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"resource\":false,\"scanning\":false,\"pois\":[");
        bool needsComma = false;
        for (int index = 0; index < catalogPois.Count; index++)
        {
            PoiSnapshot poi = catalogPois[index];
            if (!string.Equals(poi.Group, definition.Key, StringComparison.Ordinal))
            {
                continue;
            }

            if (needsComma)
            {
                json.Append(',');
            }

            json.Append('{');
            json.Append("\"name\":").Append(JsonWriter.Quote(poi.Name));
            json.Append(",\"group\":").Append(JsonWriter.Quote(poi.Group));
            json.Append(",\"x\":").Append(JsonWriter.NumberOneDecimal(poi.X));
            json.Append(",\"z\":").Append(JsonWriter.NumberOneDecimal(poi.Z));
            json.Append(",\"placed\":").Append(poi.Placed ? "true" : "false");
            json.Append(",\"explored\":").Append(
                FogTracker.IsExplored(fogSnapshot, poi.X, poi.Z) ? "true" : "false");
            json.Append('}');
            needsComma = true;
        }

        json.Append("]}");
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private void ServeResourcePoiGroup(
        HttpListenerResponse response,
        PoiGroupDefinition definition)
    {
        _noteResourcesRequested();
        ResourcePoiMapSnapshot snapshot = _getResourcePoiSnapshot();
        snapshot.TryGetGroup(definition.Key, out ResourcePoiGroupSnapshot? group);
        ResourcePoiEntry[] pois = group?.Entries ?? Array.Empty<ResourcePoiEntry>();
        int count = group?.Count ?? 0;
        long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long scanAgeMs = snapshot.LastScanUnixMs == 0L
            ? long.MaxValue
            : Math.Max(0L, nowUnixMs - snapshot.LastScanUnixMs);
        bool scanning = snapshot.Scanning ||
                        snapshot.LastScanUnixMs == 0L ||
                        scanAgeMs >= ResourceRefreshMilliseconds;
        FogMaskSnapshot fogSnapshot = _fogTracker.Snapshot;
        var json = new StringBuilder(128 + (pois.Length * 96));
        json.Append("{\"group\":").Append(JsonWriter.Quote(definition.Key));
        json.Append(",\"label\":").Append(JsonWriter.Quote(definition.Label));
        json.Append(",\"count\":").Append(count.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"resource\":true");
        json.Append(",\"scanUnixMs\":").Append(
            snapshot.LastScanUnixMs.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"scanning\":").Append(scanning ? "true" : "false");
        json.Append(",\"pois\":[");
        for (int index = 0; index < pois.Length; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            ResourcePoiEntry poi = pois[index];
            json.Append('{');
            json.Append("\"name\":").Append(JsonWriter.Quote(poi.Name));
            json.Append(",\"group\":").Append(JsonWriter.Quote(poi.Group));
            json.Append(",\"x\":").Append(JsonWriter.NumberOneDecimal(poi.X));
            json.Append(",\"z\":").Append(JsonWriter.NumberOneDecimal(poi.Z));
            json.Append(",\"explored\":").Append(
                FogTracker.IsExplored(fogSnapshot, poi.X, poi.Z) ? "true" : "false");
            if (poi.Count > 1 || poi.Available >= 0)
            {
                json.Append(",\"count\":").Append(
                    poi.Count.ToString(CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrEmpty(poi.State))
            {
                json.Append(",\"state\":").Append(JsonWriter.Quote(poi.State));
            }

            if (poi.MinedPct > 0)
            {
                json.Append(",\"minedPct\":").Append(
                    poi.MinedPct.ToString(CultureInfo.InvariantCulture));
            }

            if (poi.Available >= 0)
            {
                json.Append(",\"available\":").Append(
                    poi.Available.ToString(CultureInfo.InvariantCulture));
            }

            json.Append('}');
        }

        json.Append("]}");
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private void ServeRegions(HttpListenerResponse response)
    {
        BiomeRegionSnapshot[] regions = _renderer.Regions;
        var json = new StringBuilder(16 + (regions.Length * 96));
        json.Append("{\"regions\":[");
        for (int index = 0; index < regions.Length; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            BiomeRegionSnapshot region = regions[index];
            json.Append('{');
            json.Append("\"name\":").Append(JsonWriter.Quote(region.Name));
            json.Append(",\"biome\":").Append(JsonWriter.Quote(region.Biome));
            json.Append(",\"x\":").Append(JsonWriter.NumberOneDecimal(region.X));
            json.Append(",\"z\":").Append(JsonWriter.NumberOneDecimal(region.Z));
            json.Append(",\"area\":").Append(
                region.Area.ToString(CultureInfo.InvariantCulture));
            json.Append('}');
        }

        json.Append("]}");
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private void ServePins(HttpListenerResponse response)
    {
        MapTablePin[] pins = _getMapTableSnapshot().Pins;
        var json = new StringBuilder(16 + (pins.Length * 128));
        json.Append("{\"pins\":[");
        for (int index = 0; index < pins.Length; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            MapTablePin pin = pins[index];
            json.Append('{');
            json.Append("\"name\":").Append(JsonWriter.Quote(pin.Name));
            json.Append(",\"x\":").Append(JsonWriter.Number(pin.X));
            json.Append(",\"z\":").Append(JsonWriter.Number(pin.Z));
            json.Append(",\"type\":").Append(pin.Type.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"icon\":").Append(JsonWriter.Quote(pin.Icon));
            json.Append(",\"author\":").Append(JsonWriter.Quote(pin.Author));
            json.Append(",\"checked\":").Append(pin.IsChecked ? "true" : "false");
            json.Append('}');
        }

        json.Append("]}");
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private void ServeTile(
        HttpListenerRequest request,
        HttpListenerResponse response,
        string relativePath)
    {
        if (!TryGetMapStyle(request, out MapStyle style) ||
            !TryParseTile(relativePath, out int zoom, out int x, out int y))
        {
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

        int tilesAcross = 1 << zoom;
        if (zoom > _renderer.MaximumZoom || x < 0 || y < 0 || x >= tilesAcross || y >= tilesAcross)
        {
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

        if (zoom <= _renderer.BaseMaximumZoom && !_renderer.IsStyleReady(style))
        {
            _renderer.RequestStyleRender(style);
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

        if (!_renderer.IsReady)
        {
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

        if (zoom > _renderer.BaseMaximumZoom)
        {
            // Detail zooms render lazily on the shared worker; this waits briefly
            // for a fresh tile and serves the cached file afterwards.
            if (_renderer.TryGetDetailTile(style, zoom, x, y, out string detailPath))
            {
                ServePngFile(response, detailPath);
            }
            else
            {
                WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            }

            return;
        }

        ServePngFile(response, _renderer.GetTilePath(style, zoom, x, y));
    }

    private void ServeBaseImage(
        HttpListenerRequest request,
        HttpListenerResponse response)
    {
        if (!TryGetMapStyle(request, out MapStyle style))
        {
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

        if (!_renderer.IsStyleReady(style))
        {
            _renderer.RequestStyleRender(style);
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

        if (!_renderer.IsReady)
        {
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

        ServePngFile(response, _renderer.GetBasePath(style));
    }

    private void ServeFogImage(
        HttpListenerRequest request,
        HttpListenerResponse response,
        ViewLevel viewLevel)
    {
        if (!TryGetMapStyle(request, out MapStyle style) ||
            GetEffectiveFogMode(viewLevel) == "off")
        {
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

        FogMaskSnapshot snapshot = _fogTracker.Snapshot;
        byte[] png;
        lock (_fogPngLock)
        {
            if (style == MapStyle.Chart)
            {
                if (_chartFogPng == null || _chartFogPngRevision != snapshot.Revision)
                {
                    _chartFogPng = BuildChartFogPng(snapshot.Mask, _renderer.Seed);
                    _chartFogPngRevision = snapshot.Revision;
                }

                png = _chartFogPng;
            }
            else
            {
                if (_fogPng == null || _fogPngRevision != snapshot.Revision)
                {
                    _fogPng = BuildFogPng(snapshot.Mask);
                    _fogPngRevision = snapshot.Revision;
                }

                png = _fogPng;
            }
        }

        WriteBytes(
            response,
            HttpStatusCode.OK,
            "image/png",
            png,
            "public, max-age=86400");
    }

    private bool SeesAllPlayers(ViewLevel viewLevel)
    {
        if (viewLevel == ViewLevel.Admin)
        {
            // The admin tier now always requires a non-empty access token.
            return !string.IsNullOrEmpty(AccessToken);
        }

        return !_respectInGameVisibility;
    }

    private string GetEffectiveFogMode(ViewLevel viewLevel)
    {
        return viewLevel != ViewLevel.Public ? "off" : _getFogMode();
    }

    private double GetExploredPercentage(FogMaskSnapshot snapshot)
    {
        lock (_exploredPctLock)
        {
            if (_exploredPctRevision == snapshot.Revision)
            {
                return _exploredPctValue;
            }

            const double halfWorld = FogTracker.WorldSpan / 2.0;
            double radiusSquared =
                (double)WorldMapRenderer.WorldRadius * WorldMapRenderer.WorldRadius;
            int revealedCells = 0;
            int worldCells = 0;
            for (int y = 0; y < FogTracker.Size; y++)
            {
                double worldZ = halfWorld - ((y + 0.5) * FogTracker.MetersPerPixel);
                for (int x = 0; x < FogTracker.Size; x++)
                {
                    double worldX = -halfWorld + ((x + 0.5) * FogTracker.MetersPerPixel);
                    if ((worldX * worldX) + (worldZ * worldZ) > radiusSquared)
                    {
                        continue;
                    }

                    worldCells++;
                    if (snapshot.Mask[(y * FogTracker.Size) + x] != 0)
                    {
                        revealedCells++;
                    }
                }
            }

            _exploredPctValue = worldCells == 0
                ? 0.0
                : revealedCells * 100.0 / worldCells;
            _exploredPctRevision = snapshot.Revision;
            return _exploredPctValue;
        }
    }

    private static byte[] BuildFogPng(byte[] mask)
    {
        int expectedLength = FogTracker.Size * FogTracker.Size;
        if (mask.Length != expectedLength)
        {
            throw new InvalidOperationException("Fog mask dimensions do not match its length.");
        }

        // Ghosted-fog treatment: unexplored terrain is dimmed and cooled toward a
        // neutral slate (~57% cover) instead of blacked out, so the world's shape,
        // biomes, and coastlines stay readable at every zoom while clearly fogged.
        const byte fogRed = 0x26;
        const byte fogGreen = 0x2e;
        const byte fogBlue = 0x3a;
        const int unrevealedAlpha = 145;
        const int featherRadius = 2;
        var rgba = new byte[expectedLength * 4];
        for (int y = 0; y < FogTracker.Size; y++)
        {
            int minimumY = Math.Max(0, y - featherRadius);
            int maximumY = Math.Min(FogTracker.Size - 1, y + featherRadius);
            for (int x = 0; x < FogTracker.Size; x++)
            {
                int minimumX = Math.Max(0, x - featherRadius);
                int maximumX = Math.Min(FogTracker.Size - 1, x + featherRadius);
                int alphaTotal = 0;
                int samples = 0;
                for (int sampleY = minimumY; sampleY <= maximumY; sampleY++)
                {
                    int row = sampleY * FogTracker.Size;
                    for (int sampleX = minimumX; sampleX <= maximumX; sampleX++)
                    {
                        if (mask[row + sampleX] == 0)
                        {
                            alphaTotal += unrevealedAlpha;
                        }

                        samples++;
                    }
                }

                int offset = ((y * FogTracker.Size) + x) * 4;
                rgba[offset] = fogRed;
                rgba[offset + 1] = fogGreen;
                rgba[offset + 2] = fogBlue;
                rgba[offset + 3] = (byte)((alphaTotal + (samples / 2)) / samples);
            }
        }

        return PngEncoder.EncodeRgba(rgba, FogTracker.Size, FogTracker.Size);
    }

    private static byte[] BuildChartFogPng(byte[] mask, int seed)
    {
        int expectedLength = FogTracker.Size * FogTracker.Size;
        if (mask.Length != expectedLength)
        {
            throw new InvalidOperationException("Fog mask dimensions do not match its length.");
        }

        const int unrevealedAlpha = 210;
        const int featherRadius = 2;
        const float halfWorld = FogTracker.WorldSpan / 2f;
        var rgba = new byte[expectedLength * 4];
        for (int y = 0; y < FogTracker.Size; y++)
        {
            int minimumY = Math.Max(0, y - featherRadius);
            int maximumY = Math.Min(FogTracker.Size - 1, y + featherRadius);
            float worldZ = halfWorld - ((y + 0.5f) * FogTracker.MetersPerPixel);
            for (int x = 0; x < FogTracker.Size; x++)
            {
                int minimumX = Math.Max(0, x - featherRadius);
                int maximumX = Math.Min(FogTracker.Size - 1, x + featherRadius);
                int alphaTotal = 0;
                int samples = 0;
                for (int sampleY = minimumY; sampleY <= maximumY; sampleY++)
                {
                    int row = sampleY * FogTracker.Size;
                    for (int sampleX = minimumX; sampleX <= maximumX; sampleX++)
                    {
                        if (mask[row + sampleX] == 0)
                        {
                            alphaTotal += unrevealedAlpha;
                        }

                        samples++;
                    }
                }

                float worldX = -halfWorld + ((x + 0.5f) * FogTracker.MetersPerPixel);
                int offset = ((y * FogTracker.Size) + x) * 4;
                MapStyleCompositor.ComposeChartFog(worldX, worldZ, seed)
                    .WriteRgba(rgba, offset);
                rgba[offset + 3] = (byte)((alphaTotal + (samples / 2)) / samples);
            }
        }

        return PngEncoder.EncodeRgba(rgba, FogTracker.Size, FogTracker.Size);
    }

    private static void ServePngFile(HttpListenerResponse response, string path)
    {
        if (!File.Exists(path))
        {
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

        WriteBytes(
            response,
            HttpStatusCode.OK,
            "image/png",
            File.ReadAllBytes(path),
            "public, max-age=86400");
    }

    private static bool TryParseTile(string path, out int zoom, out int x, out int y)
    {
        zoom = 0;
        x = 0;
        y = 0;
        string[] segments = path.Split('/');
        if (segments.Length != 2 || !segments[1].EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string coordinates = segments[1].Substring(0, segments[1].Length - 4);
        int separator = coordinates.IndexOf('-');
        if (separator <= 0 || separator != coordinates.LastIndexOf('-'))
        {
            return false;
        }

        return int.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out zoom) &&
               int.TryParse(coordinates.Substring(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out x) &&
               int.TryParse(coordinates.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out y) &&
               zoom >= 0 && x >= 0 && y >= 0;
    }

    private static bool TryGetMapStyle(HttpListenerRequest request, out MapStyle style)
    {
        return MapStyles.TryParse(request.QueryString["style"], out style);
    }

    private static bool TryReadRequiredString(
        HttpListenerRequest request,
        HttpListenerResponse response,
        string propertyName,
        string requiredError,
        out string value)
    {
        if (!TryReadStringProperty(request, propertyName, out value, out bool tooLarge))
        {
            if (tooLarge)
            {
                WriteJson(
                    response,
                    HttpStatusCode.RequestEntityTooLarge,
                    "{\"ok\":false,\"error\":\"payload too large\"}");
            }
            else
            {
                WriteJson(
                    response,
                    HttpStatusCode.BadRequest,
                    "{\"ok\":false,\"error\":" + JsonWriter.Quote(requiredError) + "}");
            }

            return false;
        }

        value = value.Trim();
        if (value.Length > 0)
        {
            return true;
        }

        WriteJson(
            response,
            HttpStatusCode.BadRequest,
            "{\"ok\":false,\"error\":" + JsonWriter.Quote(requiredError) + "}");
        return false;
    }

    private static bool TryReadStringProperty(
        HttpListenerRequest request,
        string propertyName,
        out string value,
        out bool tooLarge)
    {
        value = string.Empty;
        if (!TryReadRequestBody(request, out string json, out tooLarge))
        {
            return false;
        }

        return TryFindJsonPropertyValue(json, propertyName, out int valueIndex) &&
               TryParseJsonString(json, valueIndex, out value);
    }

    private static bool TryReadRequestBody(
        HttpListenerRequest request,
        out string json,
        out bool tooLarge)
    {
        json = string.Empty;
        tooLarge = request.ContentLength64 > MaximumRequestBodyBytes;
        if (tooLarge)
        {
            return false;
        }

        var bytes = new byte[MaximumRequestBodyBytes + 1];
        int length = 0;
        while (length < bytes.Length)
        {
            int read = request.InputStream.Read(bytes, length, bytes.Length - length);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > MaximumRequestBodyBytes)
        {
            tooLarge = true;
            return false;
        }

        json = Encoding.UTF8.GetString(bytes, 0, length);
        return true;
    }

    private static bool TryFindJsonPropertyValue(
        string json,
        string propertyName,
        out int valueIndex)
    {
        valueIndex = 0;
        string property = "\"" + propertyName + "\"";
        int searchIndex = 0;
        while (searchIndex < json.Length)
        {
            int propertyIndex = json.IndexOf(property, searchIndex, StringComparison.Ordinal);
            if (propertyIndex < 0)
            {
                return false;
            }

            valueIndex = propertyIndex + property.Length;
            SkipJsonWhitespace(json, ref valueIndex);
            if (valueIndex >= json.Length || json[valueIndex] != ':')
            {
                searchIndex = valueIndex;
                continue;
            }

            valueIndex++;
            SkipJsonWhitespace(json, ref valueIndex);
            return valueIndex < json.Length;
        }

        return false;
    }

    private static bool TryReadJsonNumberProperty(
        string json,
        string propertyName,
        out float value)
    {
        value = 0f;
        if (!TryFindJsonPropertyValue(json, propertyName, out int valueIndex))
        {
            return false;
        }

        int endIndex = valueIndex;
        while (endIndex < json.Length)
        {
            char character = json[endIndex];
            if (character == ',' || character == '}' ||
                character == ' ' || character == '\t' ||
                character == '\r' || character == '\n')
            {
                break;
            }

            endIndex++;
        }

        return endIndex > valueIndex &&
               float.TryParse(
                   json.Substring(valueIndex, endIndex - valueIndex),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static bool TryParseJsonString(string json, int startIndex, out string value)
    {
        value = string.Empty;
        if (startIndex >= json.Length || json[startIndex] != '"')
        {
            return false;
        }

        var output = new StringBuilder();
        for (int index = startIndex + 1; index < json.Length; index++)
        {
            char character = json[index];
            if (character == '"')
            {
                value = output.ToString();
                return true;
            }

            if (character < 0x20)
            {
                return false;
            }

            if (character != '\\')
            {
                output.Append(character);
                continue;
            }

            index++;
            if (index >= json.Length)
            {
                return false;
            }

            switch (json[index])
            {
                case '"':
                    output.Append('"');
                    break;
                case '\\':
                    output.Append('\\');
                    break;
                case '/':
                    output.Append('/');
                    break;
                case 'b':
                    output.Append('\b');
                    break;
                case 'f':
                    output.Append('\f');
                    break;
                case 'n':
                    output.Append('\n');
                    break;
                case 'r':
                    output.Append('\r');
                    break;
                case 't':
                    output.Append('\t');
                    break;
                case 'u':
                    if (index + 4 >= json.Length ||
                        !TryParseHexCharacter(json, index + 1, out char escapedCharacter))
                    {
                        return false;
                    }

                    output.Append(escapedCharacter);
                    index += 4;
                    break;
                default:
                    return false;
            }
        }

        return false;
    }

    private static bool TryParseHexCharacter(string value, int startIndex, out char character)
    {
        int codePoint = 0;
        for (int index = 0; index < 4; index++)
        {
            char digit = value[startIndex + index];
            int parsed;
            if (digit >= '0' && digit <= '9')
            {
                parsed = digit - '0';
            }
            else if (digit >= 'a' && digit <= 'f')
            {
                parsed = digit - 'a' + 10;
            }
            else if (digit >= 'A' && digit <= 'F')
            {
                parsed = digit - 'A' + 10;
            }
            else
            {
                character = '\0';
                return false;
            }

            codePoint = (codePoint << 4) | parsed;
        }

        character = (char)codePoint;
        return true;
    }

    private static void SkipJsonWhitespace(string value, ref int index)
    {
        while (index < value.Length)
        {
            char character = value[index];
            if (character != ' ' && character != '\t' && character != '\r' && character != '\n')
            {
                return;
            }

            index++;
        }
    }

    private static int FindWhitespace(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static long ParseLong(string? value, long fallback)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : fallback;
    }

    private static bool TryParseWorldCoordinate(string? value, out float coordinate)
    {
        return float.TryParse(
                   value,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out coordinate) &&
               !float.IsNaN(coordinate) &&
               !float.IsInfinity(coordinate) &&
               Math.Abs(coordinate) <= 100000f;
    }

    private static void WriteEventStreamEvent(Stream output, string eventName, string json)
    {
        WriteEventStreamText(output, "event: " + eventName + "\ndata: " + json + "\n\n");
    }

    private static void WriteEventStreamText(Stream output, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
    }

    private static void TryCloseEventStream(HttpListenerResponse response)
    {
        try
        {
            response.OutputStream.Close();
        }
        catch (HttpListenerException)
        {
            // The event-stream client already disconnected.
        }
        catch (IOException)
        {
            // The event-stream client already disconnected.
        }
        catch (ObjectDisposedException)
        {
            // The event-stream response is already closed.
        }
        catch (InvalidOperationException)
        {
            // The event-stream response can no longer be closed normally.
        }
    }

    private static void WriteJson(HttpListenerResponse response, HttpStatusCode status, string json)
    {
        WriteBytes(
            response,
            status,
            "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(json),
            "no-store");
    }

    private static void TryWriteJson(HttpListenerResponse response, HttpStatusCode status, string json)
    {
        try
        {
            WriteJson(response, status, json);
        }
        catch (HttpListenerException)
        {
            // The client already disconnected.
        }
        catch (IOException)
        {
            // The client already disconnected.
        }
        catch (ObjectDisposedException)
        {
            // The response has already been closed.
        }
        catch (InvalidOperationException)
        {
            // Headers may already have been sent by the failed response.
        }
    }

    private static void WriteBytes(
        HttpListenerResponse response,
        HttpStatusCode status,
        string contentType,
        byte[] content,
        string cacheControl)
    {
        try
        {
            response.StatusCode = (int)status;
            response.ContentType = contentType;
            response.Headers[HttpResponseHeader.CacheControl] = cacheControl;
            response.ContentLength64 = content.Length;
            response.OutputStream.Write(content, 0, content.Length);
            response.OutputStream.Close();
        }
        catch (HttpListenerException)
        {
            // Client disconnects are routine on Mono's managed listener.
        }
        catch (IOException)
        {
            // Client disconnects are routine on Mono's managed listener.
        }
        catch (ObjectDisposedException)
        {
            // The response has already been closed.
        }
    }

    private static bool FixedTimeEquals(string expected, string supplied)
    {
        int difference = expected.Length ^ supplied.Length;
        int length = Math.Max(expected.Length, supplied.Length);
        for (int index = 0; index < length; index++)
        {
            char expectedCharacter = index < expected.Length ? expected[index] : '\0';
            char suppliedCharacter = index < supplied.Length ? supplied[index] : '\0';
            difference |= expectedCharacter ^ suppliedCharacter;
        }

        return difference == 0;
    }

    private static string NormalizeToken(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string FormatHost(string host)
    {
        return host.IndexOf(':') >= 0 && !host.StartsWith("[", StringComparison.Ordinal)
            ? "[" + host + "]"
            : host;
    }

    private static string HtmlAttributeEncode(string value)
    {
        return value.Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
