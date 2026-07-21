using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using ValheimOne.Infrastructure;
using ValheimOne.LiveMap;

namespace ValheimOne.Discord;

internal sealed class DiscordWebhookWorker
{
    private const int QueueCapacity = 64;
    private const int MaximumEmbedsPerPost = 10;
    private const int DebounceMilliseconds = 2000;
    private const int RequestTimeoutMilliseconds = 5000;

    private readonly object _queueLock = new object();
    private readonly object _requestLock = new object();
    private readonly Queue<DiscordEventRecord> _queue =
        new Queue<DiscordEventRecord>(QueueCapacity);
    private readonly AutoResetEvent _queueSignal = new AutoResetEvent(false);
    private readonly ManualResetEvent _stopSignal = new ManualResetEvent(false);
    private readonly ModLogger _log;
    private readonly HttpClient _httpClient;
    private DiscordDeliverySettings _settings = DiscordDeliverySettings.Disabled;
    private Thread? _thread;
    private CancellationTokenSource? _activeRequestCancellation;
    private long _shutdownDeadlineUtcTicks;
    private int _overflowWarningLogged;
    private int _stopping;

    public DiscordWebhookWorker(ModLogger log)
    {
        _log = log;
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "ValheimOne/" + ValheimOnePlugin.PluginVersion);
    }

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ValheimOne.DiscordWebhook",
        };
        _thread = thread;
        thread.Start();
    }

    public void UpdateSettings(DiscordDeliverySettings settings)
    {
        DiscordDeliverySettings previous = Volatile.Read(ref _settings);
        Volatile.Write(ref _settings, settings);

        bool destinationChanged = !string.Equals(
            previous.WebhookUrl,
            settings.WebhookUrl,
            StringComparison.Ordinal);
        if (!settings.Enabled || destinationChanged)
        {
            ClearPending();
        }

        if (!settings.Enabled || destinationChanged)
        {
            CancelActiveRequest();
        }

        _queueSignal.Set();
    }

    public void Enqueue(DiscordEventRecord record)
    {
        if (Volatile.Read(ref _stopping) != 0 || !Volatile.Read(ref _settings).Enabled)
        {
            return;
        }

        lock (_queueLock)
        {
            if (_queue.Count >= QueueCapacity)
            {
                if (Interlocked.Exchange(ref _overflowWarningLogged, 1) == 0)
                {
                    _log.Warning(
                        "[Discord] notification queue is full; excess events will be dropped.");
                }

                return;
            }

            _queue.Enqueue(record);
        }

        _queueSignal.Set();
    }

    public void StopAndFlush(int timeoutMilliseconds)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        int boundedTimeout = Math.Max(0, Math.Min(2000, timeoutMilliseconds));
        Interlocked.Exchange(
            ref _shutdownDeadlineUtcTicks,
            DateTime.UtcNow.AddMilliseconds(boundedTimeout).Ticks);
        _stopSignal.Set();
        _queueSignal.Set();

        // Cancellation is the only cross-thread operation performed on a request. The worker
        // remains the sole creator and sender of every HTTP request.
        CancelActiveRequest();

        Thread? thread = _thread;
        if (thread != null && thread.IsAlive && !thread.Join(boundedTimeout))
        {
            CancelActiveRequest();
            _log.Warning("[Discord] webhook worker did not exit within the shutdown flush window.");
        }
    }

    private void Run()
    {
        try
        {
            RunWorker();
        }
        finally
        {
            _httpClient.Dispose();
        }
    }

    private void RunWorker()
    {
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
            // The request will report a generic delivery failure if TLS 1.2 is unavailable.
        }

        WaitHandle[] signals = { _queueSignal, _stopSignal };
        while (true)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                FlushForShutdown();
                return;
            }

            int signal = WaitHandle.WaitAny(signals);
            if (signal == 1 || Volatile.Read(ref _stopping) != 0)
            {
                FlushForShutdown();
                return;
            }

            if (!HasPendingEvents())
            {
                continue;
            }

            if (_stopSignal.WaitOne(DebounceMilliseconds))
            {
                FlushForShutdown();
                return;
            }

            SendPending(shutdownFlush: false);
        }
    }

    private void FlushForShutdown()
    {
        if (RemainingShutdownMilliseconds() <= 0)
        {
            ClearPending();
            return;
        }

        SendPending(shutdownFlush: true);
        ClearPending();
    }

    private void SendPending(bool shutdownFlush)
    {
        List<DiscordEventRecord> records = DrainPending();
        if (records.Count == 0)
        {
            return;
        }

        DiscordDeliverySettings settings = Volatile.Read(ref _settings);
        if (!settings.Enabled ||
            !TryCreateWebhookUri(settings.WebhookUrl, out Uri? webhookUri) ||
            webhookUri == null)
        {
            if (settings.Enabled)
            {
                _log.Warning(
                    "[Discord] WebhookUrl is not a valid HTTPS URL; notification batch dropped.");
            }

            return;
        }

        for (int offset = 0; offset < records.Count; offset += MaximumEmbedsPerPost)
        {
            if (shutdownFlush && RemainingShutdownMilliseconds() <= 0)
            {
                return;
            }

            int count = Math.Min(MaximumEmbedsPerPost, records.Count - offset);
            string payload = BuildPayload(settings.Username, records, offset, count);
            SendPayload(webhookUri, settings.WebhookUrl, payload, shutdownFlush);
        }
    }

    private void SendPayload(
        Uri webhookUri,
        string expectedWebhookUrl,
        string payload,
        bool shutdownFlush)
    {
        int attempts = shutdownFlush ? 1 : 2;
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (Volatile.Read(ref _settings).Enabled == false ||
                !string.Equals(
                    Volatile.Read(ref _settings).WebhookUrl,
                    expectedWebhookUrl,
                    StringComparison.Ordinal))
            {
                return;
            }

            int timeout = shutdownFlush
                ? Math.Min(RequestTimeoutMilliseconds, RemainingShutdownMilliseconds())
                : RequestTimeoutMilliseconds;
            if (timeout <= 0)
            {
                return;
            }

            try
            {
                Post(webhookUri, expectedWebhookUrl, payload, timeout, shutdownFlush);
                return;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                if (Volatile.Read(ref _stopping) != 0 ||
                    !string.Equals(
                        Volatile.Read(ref _settings).WebhookUrl,
                        expectedWebhookUrl,
                        StringComparison.Ordinal))
                {
                    return;
                }
            }
        }

        _log.Warning(
            $"[Discord] webhook delivery failed after {attempts.ToString(CultureInfo.InvariantCulture)} " +
            $"attempt(s) ({lastFailure?.GetType().Name ?? "unknown error"}); batch dropped.");
    }

    private void Post(
        Uri webhookUri,
        string expectedWebhookUrl,
        string payload,
        int timeoutMilliseconds,
        bool shutdownFlush)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(timeoutMilliseconds);

        lock (_requestLock)
        {
            DiscordDeliverySettings settings = Volatile.Read(ref _settings);
            if ((!shutdownFlush && Volatile.Read(ref _stopping) != 0) ||
                !settings.Enabled ||
                !string.Equals(
                    settings.WebhookUrl,
                    expectedWebhookUrl,
                    StringComparison.Ordinal))
            {
                return;
            }

            _activeRequestCancellation = cancellation;
        }

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = _httpClient
                .PostAsync(webhookUri, content, cancellation.Token)
                .GetAwaiter()
                .GetResult();
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            lock (_requestLock)
            {
                if (ReferenceEquals(_activeRequestCancellation, cancellation))
                {
                    _activeRequestCancellation = null;
                }
            }
        }
    }

    private void CancelActiveRequest()
    {
        CancellationTokenSource? cancellation;
        lock (_requestLock)
        {
            cancellation = _activeRequestCancellation;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch
        {
            // Request cancellation is best effort during disable, URL rotation, and shutdown.
        }
    }

    private bool HasPendingEvents()
    {
        lock (_queueLock)
        {
            return _queue.Count != 0;
        }
    }

    private List<DiscordEventRecord> DrainPending()
    {
        lock (_queueLock)
        {
            var records = new List<DiscordEventRecord>(_queue.Count);
            while (_queue.Count != 0)
            {
                records.Add(_queue.Dequeue());
            }

            return records;
        }
    }

    private void ClearPending()
    {
        lock (_queueLock)
        {
            _queue.Clear();
        }
    }

    private int RemainingShutdownMilliseconds()
    {
        long deadlineTicks = Interlocked.Read(ref _shutdownDeadlineUtcTicks);
        if (deadlineTicks == 0L)
        {
            return RequestTimeoutMilliseconds;
        }

        long remainingTicks = deadlineTicks - DateTime.UtcNow.Ticks;
        if (remainingTicks <= 0L)
        {
            return 0;
        }

        long milliseconds = remainingTicks / TimeSpan.TicksPerMillisecond;
        return milliseconds >= int.MaxValue ? int.MaxValue : (int)milliseconds;
    }

    private static bool TryCreateWebhookUri(string value, out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri candidate) &&
            string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            uri = candidate;
            return true;
        }

        uri = null;
        return false;
    }

    private static string BuildPayload(
        string username,
        IReadOnlyList<DiscordEventRecord> records,
        int offset,
        int count)
    {
        var json = new StringBuilder(512 + (count * 256));
        json.Append("{\"username\":")
            .Append(JsonWriter.Quote(Truncate(username, 80)))
            .Append(",\"allowed_mentions\":{\"parse\":[]},\"embeds\":[");

        for (int index = 0; index < count; index++)
        {
            if (index != 0)
            {
                json.Append(',');
            }

            DiscordEventRecord record = records[offset + index];
            json.Append("{\"title\":")
                .Append(JsonWriter.Quote(Truncate(record.Title, 256)))
                .Append(",\"description\":")
                .Append(JsonWriter.Quote(Truncate(record.Description, 4096)))
                .Append(",\"color\":")
                .Append(record.Color.ToString(CultureInfo.InvariantCulture))
                .Append(",\"timestamp\":")
                .Append(JsonWriter.Quote(record.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)))
                .Append('}');
        }

        json.Append("]}");
        return json.ToString();
    }

    private static string Truncate(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        return value.Substring(0, maximumLength - 1) + "…";
    }
}

internal sealed class DiscordDeliverySettings
{
    public static readonly DiscordDeliverySettings Disabled =
        new DiscordDeliverySettings(false, string.Empty, "Valheim");

    public DiscordDeliverySettings(bool enabled, string webhookUrl, string username)
    {
        Enabled = enabled;
        WebhookUrl = webhookUrl;
        Username = username;
    }

    public bool Enabled { get; }

    public string WebhookUrl { get; }

    public string Username { get; }
}

internal sealed class DiscordEventRecord
{
    public DiscordEventRecord(string title, string description, int color)
    {
        Title = title;
        Description = description;
        Color = color;
        TimestampUtc = DateTimeOffset.UtcNow;
    }

    public string Title { get; }

    public string Description { get; }

    public int Color { get; }

    public DateTimeOffset TimestampUtc { get; }
}
