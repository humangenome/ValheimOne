using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

public sealed class WebPinEntry
{
    internal WebPinEntry(
        string id,
        float x,
        float z,
        string icon,
        string label,
        string author,
        bool isChecked,
        long createdUnixMs,
        long updatedUnixMs)
    {
        Id = id;
        X = x;
        Z = z;
        Icon = icon;
        Label = label;
        Author = author;
        Checked = isChecked;
        CreatedUnixMs = createdUnixMs;
        UpdatedUnixMs = updatedUnixMs;
    }

    public string Id { get; }

    public float X { get; }

    public float Z { get; }

    public string Icon { get; }

    public string Label { get; }

    public string Author { get; }

    public bool Checked { get; }

    public long CreatedUnixMs { get; }

    public long UpdatedUnixMs { get; }
}

public sealed class WebPinStore : IDisposable
{
    public const string ErrorNotFound = "not found";
    public const string ErrorForbidden = "forbidden";

    private const float MaximumWorldRadius = 10500f;
    private const int MaximumLabelLength = 60;
    private const int MaximumAuthorLength = 32;
    private const int MaximumPinsPerAuthor = 100;
    private const int MaximumPins = 500;
    private const int IdentifierByteCount = 6;
    private const int PersistedFileMaximumBytes = 1024 * 1024;
    private const int WriteDebounceMilliseconds = 1000;
    private const int ShutdownFlushMilliseconds = 2000;
    private const int EditorKeyHashLength = 64;

    // Who may change a pin from the shared link (0.13.32): the page there makes a random key once per browser and
    // sends it with its pin calls; the store keeps only the key's SHA-256 per pin, in this file beside webpins.json.
    // webpins.json keeps its exact old format, so ValheimOne 0.13.31 still reads every pin after a downgrade (its
    // reader takes no unknown field). A pin without an entry here (placed from the admin view, or before 0.13.32)
    // changes only from the admin view.
    internal const string EditorsFileName = "webpins-editors.json";

    private static readonly string[] AllowedIconNames =
    {
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
        "spawner_greydwarf",
    };

    private static readonly ReadOnlyCollection<string> AllowedIconView =
        Array.AsReadOnly(AllowedIconNames);
    private static readonly HashSet<string> AllowedIconSet =
        new HashSet<string>(AllowedIconNames, StringComparer.Ordinal);

    private readonly object _lock = new object();
    private readonly Dictionary<string, WebPinEntry> _pins =
        new Dictionary<string, WebPinEntry>(StringComparer.OrdinalIgnoreCase);
    private readonly AutoResetEvent _writeSignal = new AutoResetEvent(false);
    private readonly ManualResetEvent _stopSignal = new ManualResetEvent(false);
    private readonly Dictionary<string, string> _editors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly RandomNumberGenerator _random = RandomNumberGenerator.Create();
    private readonly string _path;
    private readonly string _editorsPath;
    private readonly ModLogger _log;
    private readonly Thread _writerThread;
    private long _revision;
    private int _dirty;
    private int _writeFailureWarningLogged;
    private bool _disposed;

    public WebPinStore(string dataDirectory, ModLogger log)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
        }

        _log = log ?? throw new ArgumentNullException(nameof(log));
        _path = Path.Combine(dataDirectory, "webpins.json");
        _editorsPath = Path.Combine(dataDirectory, EditorsFileName);
        Load();
        LoadEditors();

        _writerThread = new Thread(RunWriter)
        {
            IsBackground = true,
            Name = "ValheimOne.WebPins",
        };
        _writerThread.Start();
    }

    public static IReadOnlyList<string> AllowedIcons => AllowedIconView;

    public long Revision
    {
        get
        {
            lock (_lock)
            {
                return _revision;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _pins.Count;
            }
        }
    }

    public static bool IsAllowedIcon(string? icon)
    {
        return icon != null && AllowedIconSet.Contains(icon);
    }

    public static string SanitizeAuthor(string? author)
    {
        return Sanitize(author, MaximumAuthorLength);
    }

    public bool TryCreate(
        float x,
        float z,
        string icon,
        string label,
        string author,
        out WebPinEntry? pin,
        out string error)
    {
        return TryCreate(x, z, icon, label, author, null, out pin, out error);
    }

    // editorKeyHash: the shared-link caller's key hash (EditorKeyHash), or null for an admin pin, which no shared
    // caller can ever change.
    public bool TryCreate(
        float x,
        float z,
        string icon,
        string label,
        string author,
        string? editorKeyHash,
        out WebPinEntry? pin,
        out string error)
    {
        lock (_lock)
        {
            pin = null;
            error = string.Empty;
            if (_disposed)
            {
                error = "store disposed";
                return false;
            }

            if (!AreValidCoordinates(x, z))
            {
                error = "invalid coordinates";
                return false;
            }

            string safeIcon = (icon ?? string.Empty).Trim();
            if (!IsAllowedIcon(safeIcon))
            {
                error = "invalid icon";
                return false;
            }

            string safeAuthor = SanitizeAuthor(author);
            if (safeAuthor.Length == 0)
            {
                error = "author required";
                return false;
            }

            string safeLabel = Sanitize(label, MaximumLabelLength);
            if (CountAuthorPinsLocked(safeAuthor) >= MaximumPinsPerAuthor)
            {
                RemoveOldestLocked(safeAuthor);
            }

            if (_pins.Count >= MaximumPins)
            {
                RemoveOldestLocked(null);
            }

            long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            pin = new WebPinEntry(
                CreateUniqueIdLocked(),
                x,
                z,
                safeIcon,
                safeLabel,
                safeAuthor,
                false,
                nowUnixMs,
                nowUnixMs);
            _pins.Add(pin.Id, pin);
            if (IsEditorKeyHash(editorKeyHash))
            {
                _editors[pin.Id] = editorKeyHash!;
            }

            MarkMutationLocked();
            return true;
        }
    }

    public bool TryUpdate(
        string id,
        float? x,
        float? z,
        string? icon,
        string? label,
        bool? isChecked,
        string? editorKeyHash,
        bool isAdmin,
        out WebPinEntry? pin,
        out string error)
    {
        lock (_lock)
        {
            pin = null;
            error = string.Empty;
            if (_disposed)
            {
                error = "store disposed";
                return false;
            }

            if (!_pins.TryGetValue(id ?? string.Empty, out WebPinEntry? existing))
            {
                error = ErrorNotFound;
                return false;
            }

            if (!isAdmin && !IsEditorLocked(existing.Id, editorKeyHash))
            {
                error = ErrorForbidden;
                return false;
            }

            if (!x.HasValue && !z.HasValue && icon == null && label == null &&
                !isChecked.HasValue)
            {
                error = "no changes";
                return false;
            }

            float nextX = x ?? existing.X;
            float nextZ = z ?? existing.Z;
            if (!AreValidCoordinates(nextX, nextZ))
            {
                error = "invalid coordinates";
                return false;
            }

            string nextIcon = existing.Icon;
            if (icon != null)
            {
                nextIcon = icon.Trim();
                if (!IsAllowedIcon(nextIcon))
                {
                    error = "invalid icon";
                    return false;
                }
            }

            string nextLabel = label == null
                ? existing.Label
                : Sanitize(label, MaximumLabelLength);
            long updatedUnixMs = NextUpdatedUnixMs(existing.UpdatedUnixMs);
            pin = new WebPinEntry(
                existing.Id,
                nextX,
                nextZ,
                nextIcon,
                nextLabel,
                existing.Author,
                isChecked ?? existing.Checked,
                existing.CreatedUnixMs,
                updatedUnixMs);
            _pins[existing.Id] = pin;
            MarkMutationLocked();
            return true;
        }
    }

    public bool TryDelete(
        string id,
        string? editorKeyHash,
        bool isAdmin,
        out string error)
    {
        lock (_lock)
        {
            error = string.Empty;
            if (_disposed)
            {
                error = "store disposed";
                return false;
            }

            if (!_pins.TryGetValue(id ?? string.Empty, out WebPinEntry? existing))
            {
                error = ErrorNotFound;
                return false;
            }

            if (!isAdmin && !IsEditorLocked(existing.Id, editorKeyHash))
            {
                error = ErrorForbidden;
                return false;
            }

            _pins.Remove(existing.Id);
            _editors.Remove(existing.Id);
            MarkMutationLocked();
            return true;
        }
    }

    public WebPinEntry[] Snapshot()
    {
        lock (_lock)
        {
            WebPinEntry[] snapshot = new WebPinEntry[_pins.Count];
            _pins.Values.CopyTo(snapshot, 0);
            Array.Sort(snapshot, ComparePins);
            return snapshot;
        }
    }

    // The pins and, in the same moment, which of them the caller with this key hash may change (empty for no key).
    public WebPinEntry[] Snapshot(string? editorKeyHash, out HashSet<string> editable)
    {
        lock (_lock)
        {
            editable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            WebPinEntry[] snapshot = new WebPinEntry[_pins.Count];
            _pins.Values.CopyTo(snapshot, 0);
            Array.Sort(snapshot, ComparePins);
            if (IsEditorKeyHash(editorKeyHash))
            {
                for (int index = 0; index < snapshot.Length; index++)
                {
                    if (IsEditorLocked(snapshot[index].Id, editorKeyHash))
                    {
                        editable.Add(snapshot[index].Id);
                    }
                }
            }

            return snapshot;
        }
    }

    public bool IsEditor(string id, string? editorKeyHash)
    {
        lock (_lock)
        {
            return IsEditorLocked(id ?? string.Empty, editorKeyHash);
        }
    }

    // The hash the store keeps for a shared-link editor key: exactly 32 lowercase hex characters (the page's random
    // 128 bits), else null, which can change no pin.
    public static string? EditorKeyHash(string? key)
    {
        if (key == null || key.Length != 32)
        {
            return null;
        }

        for (int index = 0; index < key.Length; index++)
        {
            char character = key[index];
            if ((character < '0' || character > '9') && (character < 'a' || character > 'f'))
            {
                return null;
            }
        }

        byte[] digest;
        using (SHA256 sha = SHA256.Create())
        {
            digest = sha.ComputeHash(Encoding.ASCII.GetBytes(key));
        }

        var hex = new StringBuilder(EditorKeyHashLength);
        for (int index = 0; index < digest.Length; index++)
        {
            hex.Append(digest[index].ToString("x2", CultureInfo.InvariantCulture));
        }

        return hex.ToString();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopSignal.Set();
            _writeSignal.Set();
        }

        if (!ReferenceEquals(Thread.CurrentThread, _writerThread) &&
            _writerThread.IsAlive &&
            !_writerThread.Join(ShutdownFlushMilliseconds))
        {
            _log.Warning("[LiveMap] web-pin writer did not exit within the shutdown flush window.");
        }

        _random.Dispose();
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            if (new FileInfo(_path).Length > PersistedFileMaximumBytes)
            {
                throw new FormatException("Web-pin JSON is too large.");
            }

            string json = File.ReadAllText(_path, Encoding.UTF8);
            PersistedWebPins loaded = WebPinJsonParser.Parse(json);
            ValidateLoadedPins(loaded.Pins);
            for (int index = 0; index < loaded.Pins.Count; index++)
            {
                WebPinEntry entry = loaded.Pins[index];
                if (_pins.ContainsKey(entry.Id))
                {
                    throw new FormatException("Web-pin JSON contains a duplicate identifier.");
                }

                _pins.Add(entry.Id, entry);
            }

            _revision = loaded.Revision;
        }
        catch (Exception exception)
        {
            _pins.Clear();
            _revision = 0L;
            _log.Warning(
                $"[LiveMap] web pins could not be loaded ({exception.GetType().Name}); " +
                "starting with an empty store.");
        }
    }

    // Entries for pins that are gone (deleted while an older ValheimOne ran, say) are dropped. A file that cannot be
    // read costs no pin: the shared link just cannot change any until a new pin is made there.
    private void LoadEditors()
    {
        if (!File.Exists(_editorsPath))
        {
            return;
        }

        try
        {
            if (new FileInfo(_editorsPath).Length > PersistedFileMaximumBytes)
            {
                throw new FormatException("Web-pin editor JSON is too large.");
            }

            Dictionary<string, string> loaded =
                EditorJsonParser.Parse(File.ReadAllText(_editorsPath, Encoding.UTF8));
            foreach (KeyValuePair<string, string> entry in loaded)
            {
                if (_pins.ContainsKey(entry.Key))
                {
                    _editors[entry.Key] = entry.Value;
                }
            }
        }
        catch (Exception exception)
        {
            _editors.Clear();
            _log.Warning(
                $"[LiveMap] web pin editor keys could not be loaded ({exception.GetType().Name}); " +
                "pins made from the shared link can be changed from the admin view only.");
        }
    }

    private void RunWriter()
    {
        WaitHandle[] signals = { _writeSignal, _stopSignal };
        while (true)
        {
            int signal = WaitHandle.WaitAny(signals);
            if (signal == 1)
            {
                FlushPendingWrite();
                return;
            }

            if (_stopSignal.WaitOne(WriteDebounceMilliseconds))
            {
                FlushPendingWrite();
                return;
            }

            FlushPendingWrite();
        }
    }

    private void FlushPendingWrite()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }

        try
        {
            Persist();
            Interlocked.Exchange(ref _writeFailureWarningLogged, 0);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _dirty, 1);
            if (Interlocked.Exchange(ref _writeFailureWarningLogged, 1) == 0)
            {
                _log.Warning(
                    $"[LiveMap] web pins could not be persisted " +
                    $"({exception.GetType().Name}: {SingleLineMessage(exception)}). " +
                    "The writer will retry.");
            }

            if (!_stopSignal.WaitOne(0))
            {
                _writeSignal.Set();
            }
        }
    }

    private void Persist()
    {
        WebPinEntry[] snapshot;
        long revision;
        KeyValuePair<string, string>[] editors;
        lock (_lock)
        {
            snapshot = new WebPinEntry[_pins.Count];
            _pins.Values.CopyTo(snapshot, 0);
            revision = _revision;
            editors = new KeyValuePair<string, string>[_editors.Count];
            ((ICollection<KeyValuePair<string, string>>)_editors).CopyTo(editors, 0);
        }

        Array.Sort(snapshot, ComparePins);
        var json = new StringBuilder(32 + (snapshot.Length * 224));
        json.Append("{\"revision\":");
        json.Append(revision.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"pins\":[");
        for (int index = 0; index < snapshot.Length; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            WebPinEntry entry = snapshot[index];
            json.Append('{');
            json.Append("\"id\":").Append(JsonWriter.Quote(entry.Id));
            json.Append(",\"x\":").Append(JsonWriter.Number(entry.X));
            json.Append(",\"z\":").Append(JsonWriter.Number(entry.Z));
            json.Append(",\"icon\":").Append(JsonWriter.Quote(entry.Icon));
            json.Append(",\"label\":").Append(JsonWriter.Quote(entry.Label));
            json.Append(",\"author\":").Append(JsonWriter.Quote(entry.Author));
            json.Append(",\"checked\":").Append(entry.Checked ? "true" : "false");
            json.Append(",\"createdUnixMs\":").Append(
                entry.CreatedUnixMs.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"updatedUnixMs\":").Append(
                entry.UpdatedUnixMs.ToString(CultureInfo.InvariantCulture));
            json.Append('}');
        }

        json.Append("]}");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        WriteAtomically(_path, json.ToString());

        // After the pins: a stop between the two writes leaves a new pin without its key (admin view only) or a key
        // for a pin that is gone (dropped at the next load), never a key on someone else's pin. A server that never
        // had a shared-link pin gets no file.
        if (editors.Length == 0 && !File.Exists(_editorsPath))
        {
            return;
        }

        Array.Sort(editors, (left, right) => string.CompareOrdinal(left.Key, right.Key));
        var editorJson = new StringBuilder(32 + (editors.Length * 88));
        editorJson.Append("{\"version\":1,\"editors\":{");
        for (int index = 0; index < editors.Length; index++)
        {
            if (index > 0)
            {
                editorJson.Append(',');
            }

            editorJson.Append(JsonWriter.Quote(editors[index].Key)).Append(':')
                .Append(JsonWriter.Quote(editors[index].Value));
        }

        editorJson.Append("}}");
        WriteAtomically(_editorsPath, editorJson.ToString());
    }

    private static void WriteAtomically(string path, string text)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            text,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(path))
        {
            File.Replace(temporaryPath, path, null);
        }
        else
        {
            File.Move(temporaryPath, path);
        }
    }

    private bool IsEditorLocked(string id, string? editorKeyHash)
    {
        return IsEditorKeyHash(editorKeyHash) &&
               _editors.TryGetValue(id, out string? stored) &&
               FixedTimeEquals(stored, editorKeyHash!);
    }

    private static bool IsEditorKeyHash(string? value)
    {
        if (value == null || value.Length != EditorKeyHashLength)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if ((character < '0' || character > '9') && (character < 'a' || character > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        int difference = 0;
        for (int index = 0; index < left.Length; index++)
        {
            difference |= left[index] ^ right[index];
        }

        return difference == 0;
    }

    private void MarkMutationLocked()
    {
        if (_revision < long.MaxValue)
        {
            _revision++;
        }

        Volatile.Write(ref _dirty, 1);
        _writeSignal.Set();
    }

    private int CountAuthorPinsLocked(string author)
    {
        int count = 0;
        foreach (WebPinEntry entry in _pins.Values)
        {
            if (string.Equals(entry.Author, author, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private void RemoveOldestLocked(string? author)
    {
        WebPinEntry? oldest = null;
        foreach (WebPinEntry candidate in _pins.Values)
        {
            if (author != null && !string.Equals(
                    candidate.Author,
                    author,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (oldest == null || ComparePins(candidate, oldest) < 0)
            {
                oldest = candidate;
            }
        }

        if (oldest != null)
        {
            _pins.Remove(oldest.Id);
            _editors.Remove(oldest.Id);
        }
    }

    private string CreateUniqueIdLocked()
    {
        var bytes = new byte[IdentifierByteCount];
        var identifier = new StringBuilder(IdentifierByteCount * 2);
        while (true)
        {
            _random.GetBytes(bytes);
            identifier.Clear();
            for (int index = 0; index < bytes.Length; index++)
            {
                identifier.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
            }

            string value = identifier.ToString();
            if (!_pins.ContainsKey(value))
            {
                return value;
            }
        }
    }

    private static void ValidateLoadedPins(List<WebPinEntry> pins)
    {
        if (pins.Count > MaximumPins)
        {
            throw new FormatException("Web-pin JSON exceeds the global cap.");
        }

        var authorCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < pins.Count; index++)
        {
            WebPinEntry entry = pins[index];
            if (!IsValidId(entry.Id) || !AreValidCoordinates(entry.X, entry.Z) ||
                !IsAllowedIcon(entry.Icon) ||
                !string.Equals(
                    entry.Label,
                    Sanitize(entry.Label, MaximumLabelLength),
                    StringComparison.Ordinal) ||
                entry.Author.Length == 0 ||
                !string.Equals(
                    entry.Author,
                    SanitizeAuthor(entry.Author),
                    StringComparison.Ordinal) ||
                entry.CreatedUnixMs <= 0L || entry.UpdatedUnixMs < entry.CreatedUnixMs)
            {
                throw new FormatException("Web-pin JSON contains invalid values.");
            }

            authorCounts.TryGetValue(entry.Author, out int authorCount);
            authorCount++;
            if (authorCount > MaximumPinsPerAuthor)
            {
                throw new FormatException("Web-pin JSON exceeds the per-author cap.");
            }

            authorCounts[entry.Author] = authorCount;
        }
    }

    private static bool AreValidCoordinates(float x, float z)
    {
        return !float.IsNaN(x) && !float.IsInfinity(x) &&
               !float.IsNaN(z) && !float.IsInfinity(z) &&
               ((double)x * x) + ((double)z * z) <=
               (double)MaximumWorldRadius * MaximumWorldRadius;
    }

    private static string Sanitize(string? value, int maximumLength)
    {
        string input = (value ?? string.Empty).Trim();
        var sanitized = new StringBuilder(input.Length);
        for (int index = 0; index < input.Length; index++)
        {
            char character = input[index];
            if (!char.IsControl(character) && character != '<' && character != '>')
            {
                sanitized.Append(character);
            }
        }

        string result = sanitized.ToString().Trim();
        return result.Length <= maximumLength
            ? result
            : result.Substring(0, maximumLength);
    }

    private static bool IsValidId(string id)
    {
        if (id.Length != IdentifierByteCount * 2)
        {
            return false;
        }

        for (int index = 0; index < id.Length; index++)
        {
            char character = id[index];
            if (!char.IsDigit(character) && (character < 'a' || character > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static long NextUpdatedUnixMs(long previousUnixMs)
    {
        long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return nowUnixMs > previousUnixMs || previousUnixMs == long.MaxValue
            ? Math.Max(nowUnixMs, previousUnixMs)
            : previousUnixMs + 1L;
    }

    private static int ComparePins(WebPinEntry left, WebPinEntry right)
    {
        int createdComparison = left.CreatedUnixMs.CompareTo(right.CreatedUnixMs);
        return createdComparison != 0
            ? createdComparison
            : string.CompareOrdinal(left.Id, right.Id);
    }

    private static string SingleLineMessage(Exception exception)
    {
        return (exception.Message ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private sealed class PersistedWebPins
    {
        public PersistedWebPins(long revision, List<WebPinEntry> pins)
        {
            Revision = revision;
            Pins = pins;
        }

        public long Revision { get; }

        public List<WebPinEntry> Pins { get; }
    }

    private static class WebPinJsonParser
    {
        public static PersistedWebPins Parse(string json)
        {
            return new Parser(json).ParseDocument();
        }

        private sealed class Parser
        {
            private readonly string _json;
            private int _index;

            public Parser(string json)
            {
                _json = json;
            }

            public PersistedWebPins ParseDocument()
            {
                Expect('{');
                ExpectProperty("revision");
                long revision = ReadInt64();
                Expect(',');
                ExpectProperty("pins");
                List<WebPinEntry> pins = ParsePins();
                Expect('}');
                EnsureEnd();
                if (revision < 0L)
                {
                    throw new FormatException("Web-pin JSON contains an invalid revision.");
                }

                return new PersistedWebPins(revision, pins);
            }

            private List<WebPinEntry> ParsePins()
            {
                var pins = new List<WebPinEntry>();
                Expect('[');
                if (TryConsume(']'))
                {
                    return pins;
                }

                while (true)
                {
                    pins.Add(ParsePin());
                    if (pins.Count > MaximumPins)
                    {
                        throw new FormatException("Web-pin JSON contains too many pins.");
                    }

                    if (TryConsume(']'))
                    {
                        return pins;
                    }

                    Expect(',');
                }
            }

            private WebPinEntry ParsePin()
            {
                Expect('{');
                ExpectProperty("id");
                string id = ReadString();
                Expect(',');
                ExpectProperty("x");
                float x = ReadSingle();
                Expect(',');
                ExpectProperty("z");
                float z = ReadSingle();
                Expect(',');
                ExpectProperty("icon");
                string icon = ReadString();
                Expect(',');
                ExpectProperty("label");
                string label = ReadString();
                Expect(',');
                ExpectProperty("author");
                string author = ReadString();
                Expect(',');
                ExpectProperty("checked");
                bool isChecked = ReadBoolean();
                Expect(',');
                ExpectProperty("createdUnixMs");
                long createdUnixMs = ReadInt64();
                Expect(',');
                ExpectProperty("updatedUnixMs");
                long updatedUnixMs = ReadInt64();
                Expect('}');
                return new WebPinEntry(
                    id,
                    x,
                    z,
                    icon,
                    label,
                    author,
                    isChecked,
                    createdUnixMs,
                    updatedUnixMs);
            }

            private void ExpectProperty(string expected)
            {
                string actual = ReadString();
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    throw new FormatException("Web-pin JSON has an unexpected property.");
                }

                Expect(':');
            }

            private float ReadSingle()
            {
                string token = ReadNumberToken();
                if (!float.TryParse(
                        token,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out float value) ||
                    float.IsNaN(value) || float.IsInfinity(value))
                {
                    throw new FormatException("Web-pin JSON contains an invalid coordinate.");
                }

                return value;
            }

            private long ReadInt64()
            {
                string token = ReadNumberToken();
                if (!long.TryParse(
                        token,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long value))
                {
                    throw new FormatException("Web-pin JSON contains an invalid integer.");
                }

                return value;
            }

            private string ReadNumberToken()
            {
                SkipWhitespace();
                int start = _index;
                if (_index < _json.Length && (_json[_index] == '-' || _json[_index] == '+'))
                {
                    _index++;
                }

                while (_index < _json.Length)
                {
                    char character = _json[_index];
                    if (!char.IsDigit(character) && character != '.' &&
                        character != 'e' && character != 'E' &&
                        character != '+' && character != '-')
                    {
                        break;
                    }

                    _index++;
                }

                if (_index == start)
                {
                    throw new FormatException("Web-pin JSON is missing a number.");
                }

                return _json.Substring(start, _index - start);
            }

            private bool ReadBoolean()
            {
                SkipWhitespace();
                if (Matches("true"))
                {
                    _index += 4;
                    return true;
                }

                if (Matches("false"))
                {
                    _index += 5;
                    return false;
                }

                throw new FormatException("Web-pin JSON contains an invalid boolean.");
            }

            private bool Matches(string value)
            {
                return _index + value.Length <= _json.Length &&
                       string.CompareOrdinal(_json, _index, value, 0, value.Length) == 0;
            }

            private string ReadString()
            {
                SkipWhitespace();
                if (_index >= _json.Length || _json[_index++] != '"')
                {
                    throw new FormatException("Web-pin JSON contains an invalid string.");
                }

                var value = new StringBuilder();
                while (_index < _json.Length)
                {
                    char character = _json[_index++];
                    if (character == '"')
                    {
                        return value.ToString();
                    }

                    if (character < 0x20)
                    {
                        throw new FormatException("Web-pin JSON contains a control character.");
                    }

                    if (character != '\\')
                    {
                        value.Append(character);
                        continue;
                    }

                    if (_index >= _json.Length)
                    {
                        break;
                    }

                    char escaped = _json[_index++];
                    switch (escaped)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            value.Append(escaped);
                            break;
                        case 'b':
                            value.Append('\b');
                            break;
                        case 'f':
                            value.Append('\f');
                            break;
                        case 'n':
                            value.Append('\n');
                            break;
                        case 'r':
                            value.Append('\r');
                            break;
                        case 't':
                            value.Append('\t');
                            break;
                        case 'u':
                            value.Append(ReadUnicodeEscape());
                            break;
                        default:
                            throw new FormatException("Web-pin JSON contains an invalid escape.");
                    }
                }

                throw new FormatException("Web-pin JSON contains an unterminated string.");
            }

            private char ReadUnicodeEscape()
            {
                if (_index + 4 > _json.Length ||
                    !ushort.TryParse(
                        _json.Substring(_index, 4),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out ushort value))
                {
                    throw new FormatException("Web-pin JSON contains an invalid Unicode escape.");
                }

                _index += 4;
                return (char)value;
            }

            private bool TryConsume(char expected)
            {
                SkipWhitespace();
                if (_index >= _json.Length || _json[_index] != expected)
                {
                    return false;
                }

                _index++;
                return true;
            }

            private void Expect(char expected)
            {
                if (!TryConsume(expected))
                {
                    throw new FormatException("Web-pin JSON is malformed.");
                }
            }

            private void SkipWhitespace()
            {
                while (_index < _json.Length && char.IsWhiteSpace(_json[_index]))
                {
                    _index++;
                }
            }

            private void EnsureEnd()
            {
                SkipWhitespace();
                if (_index != _json.Length)
                {
                    throw new FormatException("Web-pin JSON contains trailing data.");
                }
            }
        }
    }

    // webpins-editors.json: {"version":1,"editors":{"<pin id>":"<sha256 hex>",...}}. Ids and hashes are plain hex, so
    // nothing in it is escaped; anything else is refused.
    private static class EditorJsonParser
    {
        public static Dictionary<string, string> Parse(string json)
        {
            var editors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int index = 0;
            Expect(json, ref index, '{');
            ExpectHexString(json, ref index, "version");
            Expect(json, ref index, ':');
            Expect(json, ref index, '1');
            Expect(json, ref index, ',');
            ExpectHexString(json, ref index, "editors");
            Expect(json, ref index, ':');
            Expect(json, ref index, '{');
            if (!TryConsume(json, ref index, '}'))
            {
                while (true)
                {
                    string id = ReadHexString(json, ref index);
                    Expect(json, ref index, ':');
                    string hash = ReadHexString(json, ref index);
                    if (!IsValidId(id) || !IsEditorKeyHash(hash) || editors.ContainsKey(id) ||
                        editors.Count >= MaximumPins)
                    {
                        throw new FormatException("Web-pin editor JSON contains an invalid entry.");
                    }

                    editors.Add(id, hash);
                    if (TryConsume(json, ref index, '}'))
                    {
                        break;
                    }

                    Expect(json, ref index, ',');
                }
            }

            Expect(json, ref index, '}');
            SkipWhitespace(json, ref index);
            if (index != json.Length)
            {
                throw new FormatException("Web-pin editor JSON contains trailing data.");
            }

            return editors;
        }

        private static void ExpectHexString(string json, ref int index, string expected)
        {
            if (!string.Equals(ReadHexString(json, ref index), expected, StringComparison.Ordinal))
            {
                throw new FormatException("Web-pin editor JSON has an unexpected property.");
            }
        }

        // a quoted run of letters and digits only (the two property names, the ids, the hashes)
        private static string ReadHexString(string json, ref int index)
        {
            Expect(json, ref index, '"');
            int start = index;
            while (index < json.Length && json[index] != '"')
            {
                char character = json[index];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'z')))
                {
                    throw new FormatException("Web-pin editor JSON contains an unexpected character.");
                }

                index++;
            }

            if (index >= json.Length || index - start > EditorKeyHashLength)
            {
                throw new FormatException("Web-pin editor JSON is malformed.");
            }

            string value = json.Substring(start, index - start);
            index++;
            return value;
        }

        private static void Expect(string json, ref int index, char expected)
        {
            if (!TryConsume(json, ref index, expected))
            {
                throw new FormatException("Web-pin editor JSON is malformed.");
            }
        }

        private static bool TryConsume(string json, ref int index, char expected)
        {
            SkipWhitespace(json, ref index);
            if (index < json.Length && json[index] == expected)
            {
                index++;
                return true;
            }

            return false;
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }
        }
    }
}
