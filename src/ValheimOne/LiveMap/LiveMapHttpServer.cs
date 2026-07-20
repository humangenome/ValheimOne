using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class LiveMapHttpServer
{
    private const int MaximumRequestBodyBytes = 8 * 1024;
    private const int MaximumEventStreams = 8;
    private const int EventStreamTickMilliseconds = 1000;
    private const int EventStreamHeartbeatTicks = 15;
    private const int EventStreamLogBatchSize = 100;

    private enum ViewLevel
    {
        Admin,
        Public,
    }

    private readonly int _port;
    private readonly string _bindIp;
    private readonly string _accessToken;
    private readonly bool _adminSeesAll;
    private readonly bool _publicView;
    private readonly bool _publicShowPlayerNames;
    private readonly Func<LiveMapSnapshot> _getSnapshot;
    private readonly Func<PoiCatalog> _getPoiCatalog;
    private readonly Func<MapTableSnapshot> _getMapTableSnapshot;
    private readonly Func<EntityMapSnapshot> _getEntitySnapshot;
    private readonly Func<string> _getFogMode;
    private readonly FogTracker _fogTracker;
    private readonly WorldMapRenderer _renderer;
    private readonly LiveMapConfig _config;
    private readonly ConsoleBridge? _consoleBridge;
    private readonly LogRingBuffer? _logRingBuffer;
    private readonly ModLogger _log;
    private readonly object _fogPngLock = new object();
    private HttpListener? _listener;
    private Thread? _listenerThread;
    private byte[]? _fogPng;
    private long _fogPngRevision = -1;
    private bool _consoleTokenWarningLogged;
    private int _eventStreamCount;
    private volatile bool _stopping;

    public LiveMapHttpServer(
        int port,
        string bindIp,
        string accessToken,
        bool adminSeesAll,
        bool publicView,
        bool publicShowPlayerNames,
        Func<LiveMapSnapshot> getSnapshot,
        Func<PoiCatalog> getPoiCatalog,
        Func<MapTableSnapshot> getMapTableSnapshot,
        Func<EntityMapSnapshot> getEntitySnapshot,
        Func<string> getFogMode,
        FogTracker fogTracker,
        WorldMapRenderer renderer,
        LiveMapConfig config,
        ConsoleBridge? consoleBridge,
        LogRingBuffer? logRingBuffer,
        ModLogger log)
    {
        _port = port;
        _bindIp = bindIp.Trim();
        _accessToken = accessToken;
        _adminSeesAll = adminSeesAll;
        _publicView = publicView;
        _publicShowPlayerNames = publicShowPlayerNames;
        _getSnapshot = getSnapshot;
        _getPoiCatalog = getPoiCatalog;
        _getMapTableSnapshot = getMapTableSnapshot;
        _getEntitySnapshot = getEntitySnapshot;
        _getFogMode = getFogMode;
        _fogTracker = fogTracker;
        _renderer = renderer;
        _config = config;
        _consoleBridge = consoleBridge;
        _logRingBuffer = logRingBuffer;
        _log = log;
    }

    public void Start()
    {
        if (_listener != null)
        {
            return;
        }

        if (!_consoleTokenWarningLogged &&
            _config.ConsoleEnabled &&
            string.IsNullOrEmpty(_config.AccessToken))
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
            else if (isGet && path == "/api/entities")
            {
                ServeEntities(response, viewLevel);
            }
            else if (isGet && path == "/api/events")
            {
                ServeEvents(request, response, viewLevel);
            }
            else if (isGet && path == "/api/pois")
            {
                ServePois(response, viewLevel);
            }
            else if (isGet && path == "/api/pins")
            {
                ServePins(response);
            }
            else if (isGet && path.StartsWith("/tiles/", StringComparison.Ordinal))
            {
                ServeTile(response, path.Substring("/tiles/".Length));
            }
            else if (isGet && path == "/base.png")
            {
                ServeBaseImage(response);
            }
            else if (isGet && path == "/fog.png")
            {
                ServeFogImage(response, viewLevel);
            }
            else if (isPost && path == "/api/console/exec")
            {
                ServeConsoleExec(request, response);
            }
            else if (isGet && path == "/api/console/log")
            {
                ServeConsoleLog(request, response);
            }
            else if (isGet && path == "/api/console/meta")
            {
                ServeConsoleMeta(response);
            }
            else if (isPost && path == "/api/admin/kick")
            {
                ServeAdminAction(request, response, _consoleBridge!.Kick);
            }
            else if (isPost && path == "/api/admin/ban")
            {
                ServeAdminAction(request, response, _consoleBridge!.Ban);
            }
            else if (isPost && path == "/api/admin/unban")
            {
                ServeAdminAction(request, response, _consoleBridge!.Unban);
            }
            else if (isGet && path == "/api/admin/banlist")
            {
                ServeBanList(response);
            }
            else if (isPost && path == "/api/admin/save")
            {
                ServeSave(response);
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
        if (string.IsNullOrEmpty(_accessToken))
        {
            viewLevel = ViewLevel.Admin;
            return true;
        }

        string queryToken = request.QueryString["token"] ?? string.Empty;
        string headerToken = request.Headers["X-LiveMap-Token"] ?? string.Empty;
        bool isAdmin = FixedTimeEquals(_accessToken, queryToken);
        isAdmin |= FixedTimeEquals(_accessToken, headerToken);
        viewLevel = isAdmin ? ViewLevel.Admin : ViewLevel.Public;
        return isAdmin || _publicView;
    }

    private bool HasConsoleToken(HttpListenerRequest request)
    {
        string accessToken = _config.AccessToken;
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

    private void ServeIndex(HttpListenerResponse response, ViewLevel viewLevel)
    {
        string html = Encoding.UTF8.GetString(EmbeddedAssets.Get("index.html"));
        string token = viewLevel == ViewLevel.Admin ? _accessToken : string.Empty;
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
                                !string.IsNullOrEmpty(_config.AccessToken) &&
                                _consoleBridge != null;
        bool entitiesAvailable = viewLevel == ViewLevel.Admin && _config.EntityLayer;
        string mapState = _renderer.StateName;
        string mapProgress = JsonWriter.Number(_renderer.Progress);
        string fogMode = GetEffectiveFogMode(viewLevel);
        FogMaskSnapshot fogSnapshot = _fogTracker.Snapshot;
        long fogRevision = fogMode == "off" ? 0 : fogSnapshot.Revision;
        EntityMapSnapshot entitySnapshot = _getEntitySnapshot();
        RaidEventSnapshot? activeEvent = viewLevel == ViewLevel.Admin
            ? entitySnapshot.Event
            : null;
        long snapshotAgeMs = snapshot.UnixMs == 0
            ? 0
            : Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - snapshot.UnixMs);
        bool snapshotStale = snapshot.UnixMs != 0 &&
                             snapshotAgeMs > Math.Max(0.25f, _config.PlayerUpdateSeconds) * 3000.0;
        var json = new StringBuilder(416);
        json.Append('{');
        json.Append("\"serverName\":").Append(JsonWriter.Quote(snapshot.ServerName));
        json.Append(",\"worldName\":").Append(JsonWriter.Quote(snapshot.WorldName));
        json.Append(",\"day\":").Append(snapshot.Day.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"timeOfDay\":").Append(JsonWriter.Number(snapshot.TimeOfDay));
        json.Append(",\"players\":").Append(visiblePlayers.ToString(CultureInfo.InvariantCulture));
        int maxPlayers = ValheimOne.Modules.ServerHostModule.EffectiveMaxPlayers();
        json.Append(",\"maxPlayers\":").Append(maxPlayers.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"view\":").Append(JsonWriter.Quote(
            viewLevel == ViewLevel.Admin ? "admin" : "public"));
        json.Append(",\"console\":").Append(consoleAvailable ? "true" : "false");
        if (viewLevel == ViewLevel.Admin)
        {
            json.Append(",\"entities\":").Append(entitiesAvailable ? "true" : "false");
            json.Append(",\"event\":");
            AppendRaidEventJson(json, activeEvent);
        }

        json.Append(",\"map\":{");
        json.Append("\"state\":").Append(JsonWriter.Quote(mapState));
        json.Append(",\"progress\":").Append(mapProgress);
        json.Append(",\"textureSize\":").Append(_renderer.TextureSize.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"pixelSize\":").Append(JsonWriter.Number(WorldMapRenderer.PixelSize));
        json.Append(",\"worldRadius\":").Append(WorldMapRenderer.WorldRadius.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"baseZoom\":").Append(_renderer.BaseMaximumZoom.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"maxZoom\":").Append(_renderer.MaximumZoom.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"fog\":{");
        json.Append("\"mode\":").Append(JsonWriter.Quote(fogMode));
        json.Append(",\"revision\":").Append(fogRevision.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"size\":").Append(FogTracker.Size.ToString(CultureInfo.InvariantCulture));
        json.Append("}}");
        json.Append(",\"unixMs\":").Append(snapshot.UnixMs.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"snapshotAgeMs\":").Append(snapshotAgeMs.ToString(CultureInfo.InvariantCulture));
        if (snapshotStale)
        {
            json.Append(",\"stale\":true");
        }

        json.Append('}');

        var key = new StringBuilder(96);
        key.Append(snapshot.Day.ToString(CultureInfo.InvariantCulture)).Append('|');
        key.Append(JsonWriter.Number(Math.Round(snapshot.TimeOfDay, 3))).Append('|');
        key.Append(visiblePlayers.ToString(CultureInfo.InvariantCulture)).Append('|');
        key.Append(maxPlayers.ToString(CultureInfo.InvariantCulture)).Append('|');
        key.Append(mapState).Append('|');
        key.Append(mapProgress).Append('|');
        key.Append(fogRevision.ToString(CultureInfo.InvariantCulture)).Append('|');
        key.Append(snapshotStale ? "stale" : "fresh");
        if (viewLevel == ViewLevel.Admin)
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
        if (!TryReadRequiredString(request, response, "command", "command is required", out string line))
        {
            return;
        }

        int separator = FindWhitespace(line);
        string commandName = separator < 0
            ? line.ToLowerInvariant()
            : line.Substring(0, separator).ToLowerInvariant();
        if (!_config.AllowAllCommands && !_config.ConsoleWhitelist.Contains(commandName))
        {
            WriteJson(
                response,
                HttpStatusCode.Forbidden,
                "{\"ok\":false,\"error\":\"command not whitelisted\"}");
            return;
        }

        ConsoleExecResult result = _consoleBridge!.ExecuteCommand(line);
        if (!result.Ok)
        {
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
        var json = new StringBuilder(64 + (whitelist.Count * 16) + (commands.Count * 96));
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
        for (int index = 0; index < commands.Count; index++)
        {
            ConsoleCommandInfo command = commands[index];
            if (!allowAll && !whitelist.Contains(command.Name.ToLowerInvariant()))
            {
                continue;
            }

            if (needsComma)
            {
                json.Append(',');
            }

            json.Append('{');
            json.Append("\"name\":").Append(JsonWriter.Quote(command.Name));
            json.Append(",\"description\":").Append(JsonWriter.Quote(command.Description));
            json.Append(",\"cheat\":").Append(command.IsCheat ? "true" : "false");
            json.Append('}');
            needsComma = true;
        }

        json.Append("]}");
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private static void ServeAdminAction(
        HttpListenerRequest request,
        HttpListenerResponse response,
        Func<string, ConsoleActionResult> action)
    {
        if (!TryReadRequiredString(request, response, "player", "player is required", out string player))
        {
            return;
        }

        ConsoleActionResult result = action(player);
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

    private void ServeSave(HttpListenerResponse response)
    {
        ConsoleSaveResult result = _consoleBridge!.Save();
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
        bool showNames = viewLevel == ViewLevel.Admin || _publicShowPlayerNames;
        long snapshotAgeMs = snapshot.UnixMs == 0
            ? 0
            : Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - snapshot.UnixMs);
        bool snapshotStale = snapshot.UnixMs != 0 &&
                             snapshotAgeMs > Math.Max(0.25f, _config.PlayerUpdateSeconds) * 3000.0;
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

    private void ServeEntities(HttpListenerResponse response, ViewLevel viewLevel)
    {
        if (viewLevel != ViewLevel.Admin || !_config.EntityLayer)
        {
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

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
            json.Append("\"group\":").Append(JsonWriter.Quote(entity.Group));
            json.Append(",\"prefab\":").Append(JsonWriter.Quote(entity.Prefab));
            json.Append(",\"x\":").Append(JsonWriter.Number(entity.X));
            json.Append(",\"y\":").Append(JsonWriter.Number(entity.Y));
            json.Append(",\"z\":").Append(JsonWriter.Number(entity.Z));
            json.Append('}');
        }

        json.Append("],\"event\":");
        AppendRaidEventJson(json, snapshot.Event);
        json.Append('}');
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

    private void ServePois(HttpListenerResponse response, ViewLevel viewLevel)
    {
        IReadOnlyList<PoiSnapshot> pois = _getPoiCatalog().ServedPois;
        var json = new StringBuilder(16 + (pois.Count * 96));
        json.Append("{\"pois\":[");
        bool needsComma = false;
        for (int index = 0; index < pois.Count; index++)
        {
            PoiSnapshot poi = pois[index];
            if (viewLevel == ViewLevel.Public && !IsPublicPoi(poi))
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
            json.Append('}');
            needsComma = true;
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

    private void ServeTile(HttpListenerResponse response, string relativePath)
    {
        if (!_renderer.IsReady || !TryParseTile(relativePath, out int zoom, out int x, out int y))
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

        if (zoom > _renderer.BaseMaximumZoom)
        {
            // Detail zooms render lazily on the shared worker; this waits briefly
            // for a fresh tile and serves the cached file afterwards.
            if (_renderer.TryGetDetailTile(zoom, x, y, out string detailPath))
            {
                ServePngFile(response, detailPath);
            }
            else
            {
                WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            }

            return;
        }

        string path = Path.Combine(
            _renderer.CacheDirectory,
            "tiles",
            zoom.ToString(CultureInfo.InvariantCulture),
            $"{x.ToString(CultureInfo.InvariantCulture)}-{y.ToString(CultureInfo.InvariantCulture)}.png");
        ServePngFile(response, path);
    }

    private void ServeBaseImage(HttpListenerResponse response)
    {
        if (!_renderer.IsReady)
        {
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

        ServePngFile(response, Path.Combine(_renderer.CacheDirectory, "base.png"));
    }

    private void ServeFogImage(HttpListenerResponse response, ViewLevel viewLevel)
    {
        if (GetEffectiveFogMode(viewLevel) == "off")
        {
            WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
            return;
        }

        FogMaskSnapshot snapshot = _fogTracker.Snapshot;
        byte[] png;
        lock (_fogPngLock)
        {
            if (_fogPng == null || _fogPngRevision != snapshot.Revision)
            {
                _fogPng = BuildFogPng(snapshot.Mask);
                _fogPngRevision = snapshot.Revision;
            }

            png = _fogPng;
        }

        WriteBytes(
            response,
            HttpStatusCode.OK,
            "image/png",
            png,
            "no-store");
    }

    private bool SeesAllPlayers(ViewLevel viewLevel)
    {
        return viewLevel == ViewLevel.Admin &&
               (!string.IsNullOrEmpty(_accessToken) || _adminSeesAll);
    }

    private string GetEffectiveFogMode(ViewLevel viewLevel)
    {
        return viewLevel == ViewLevel.Admin ? "off" : _getFogMode();
    }

    private static bool IsPublicPoi(PoiSnapshot poi)
    {
        return string.Equals(poi.Group, "spawn", StringComparison.Ordinal) ||
               string.Equals(poi.Group, "trader", StringComparison.Ordinal);
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

        string json = Encoding.UTF8.GetString(bytes, 0, length);
        string property = "\"" + propertyName + "\"";
        int searchIndex = 0;
        while (searchIndex < json.Length)
        {
            int propertyIndex = json.IndexOf(property, searchIndex, StringComparison.Ordinal);
            if (propertyIndex < 0)
            {
                return false;
            }

            int valueIndex = propertyIndex + property.Length;
            SkipJsonWhitespace(json, ref valueIndex);
            if (valueIndex >= json.Length || json[valueIndex] != ':')
            {
                searchIndex = valueIndex;
                continue;
            }

            valueIndex++;
            SkipJsonWhitespace(json, ref valueIndex);
            if (TryParseJsonString(json, valueIndex, out value))
            {
                return true;
            }

            searchIndex = valueIndex;
        }

        return false;
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
