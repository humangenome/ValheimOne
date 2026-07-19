using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class LiveMapHttpServer
{
    private readonly int _port;
    private readonly string _bindIp;
    private readonly string _accessToken;
    private readonly bool _adminSeesAll;
    private readonly Func<LiveMapSnapshot> _getSnapshot;
    private readonly WorldMapRenderer _renderer;
    private readonly ModLogger _log;
    private HttpListener? _listener;
    private Thread? _listenerThread;
    private volatile bool _stopping;

    public LiveMapHttpServer(
        int port,
        string bindIp,
        string accessToken,
        bool adminSeesAll,
        Func<LiveMapSnapshot> getSnapshot,
        WorldMapRenderer renderer,
        ModLogger log)
    {
        _port = port;
        _bindIp = bindIp.Trim();
        _accessToken = accessToken;
        _adminSeesAll = adminSeesAll;
        _getSnapshot = getSnapshot;
        _renderer = renderer;
        _log = log;
    }

    public void Start()
    {
        if (_listener != null)
        {
            return;
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
            if (!IsAuthorized(context.Request))
            {
                WriteJson(response, HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}");
                return;
            }

            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(response, HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
                return;
            }

            string path = context.Request.Url?.AbsolutePath ?? "/";
            if (path == "/")
            {
                ServeIndex(response);
            }
            else if (path.StartsWith("/assets/", StringComparison.Ordinal))
            {
                ServeAsset(response, path.Substring("/assets/".Length));
            }
            else if (path == "/api/status")
            {
                ServeStatus(response);
            }
            else if (path == "/api/players")
            {
                ServePlayers(response);
            }
            else if (path.StartsWith("/tiles/", StringComparison.Ordinal))
            {
                ServeTile(response, path.Substring("/tiles/".Length));
            }
            else if (path == "/base.png")
            {
                ServeBaseImage(response);
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

    private bool IsAuthorized(HttpListenerRequest request)
    {
        if (string.IsNullOrEmpty(_accessToken))
        {
            return true;
        }

        string supplied = request.QueryString["token"] ??
                          request.Headers["X-LiveMap-Token"] ??
                          string.Empty;
        return FixedTimeEquals(_accessToken, supplied);
    }

    private void ServeIndex(HttpListenerResponse response)
    {
        string html = Encoding.UTF8.GetString(EmbeddedAssets.Get("index.html"));
        string tokenQuery = string.IsNullOrEmpty(_accessToken)
            ? string.Empty
            : "?token=" + Uri.EscapeDataString(_accessToken);
        html = html.Replace("{{TOKEN_QUERY}}", tokenQuery);
        html = html.Replace("{{TOKEN_VALUE}}", HtmlAttributeEncode(_accessToken));
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

    private void ServeStatus(HttpListenerResponse response)
    {
        LiveMapSnapshot snapshot = _getSnapshot();
        int visiblePlayers = 0;
        for (int index = 0; index < snapshot.Players.Length; index++)
        {
            if (_adminSeesAll || snapshot.Players[index].IsPublic)
            {
                visiblePlayers++;
            }
        }

        var json = new StringBuilder(320);
        json.Append('{');
        json.Append("\"serverName\":").Append(JsonWriter.Quote(snapshot.ServerName));
        json.Append(",\"worldName\":").Append(JsonWriter.Quote(snapshot.WorldName));
        json.Append(",\"day\":").Append(snapshot.Day.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"timeOfDay\":").Append(JsonWriter.Number(snapshot.TimeOfDay));
        json.Append(",\"players\":").Append(visiblePlayers.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"map\":{");
        json.Append("\"state\":").Append(JsonWriter.Quote(_renderer.StateName));
        json.Append(",\"progress\":").Append(JsonWriter.Number(_renderer.Progress));
        json.Append(",\"textureSize\":").Append(_renderer.TextureSize.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"pixelSize\":").Append(JsonWriter.Number(WorldMapRenderer.PixelSize));
        json.Append(",\"worldRadius\":").Append(WorldMapRenderer.WorldRadius.ToString(CultureInfo.InvariantCulture));
        json.Append("}}");
        WriteJson(response, HttpStatusCode.OK, json.ToString());
    }

    private void ServePlayers(HttpListenerResponse response)
    {
        LiveMapSnapshot snapshot = _getSnapshot();
        var json = new StringBuilder(128 + (snapshot.Players.Length * 96));
        json.Append("{\"players\":[");
        bool needsComma = false;
        for (int index = 0; index < snapshot.Players.Length; index++)
        {
            LiveMapPlayerSnapshot player = snapshot.Players[index];
            if (!_adminSeesAll && !player.IsPublic)
            {
                continue;
            }

            if (needsComma)
            {
                json.Append(',');
            }

            json.Append('{');
            json.Append("\"name\":").Append(JsonWriter.Quote(player.Name));
            json.Append(",\"x\":").Append(JsonWriter.Number(player.X));
            json.Append(",\"y\":").Append(JsonWriter.Number(player.Y));
            json.Append(",\"z\":").Append(JsonWriter.Number(player.Z));
            json.Append('}');
            needsComma = true;
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
