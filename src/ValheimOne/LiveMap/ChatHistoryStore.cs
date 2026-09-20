using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class ChatHistoryStore : IDisposable
{
    internal const int Capacity = 200;
    private const int PersistedFileMaximumBytes = 512 * 1024;
    private const int WriteDebounceMilliseconds = 750;
    private const int ShutdownFlushMilliseconds = 2000;

    private readonly object _lock = new object();
    private readonly AutoResetEvent _writeSignal = new AutoResetEvent(false);
    private readonly ManualResetEvent _stopSignal = new ManualResetEvent(false);
    private readonly string _path;
    private readonly ModLogger _log;
    private readonly Thread _writerThread;
    private MapChatSnapshot[] _pending = Array.Empty<MapChatSnapshot>();
    private int _dirty;
    private int _writeFailureWarningLogged;
    private bool _disposed;

    public ChatHistoryStore(string dataDirectory, ModLogger log)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
        }

        _log = log ?? throw new ArgumentNullException(nameof(log));
        _path = Path.Combine(dataDirectory, "chat-history.json");
        _writerThread = new Thread(RunWriter)
        {
            IsBackground = true,
            Name = "ValheimOne.ChatHistory",
        };
        _writerThread.Start();
    }

    public MapChatSnapshot[] Load()
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<MapChatSnapshot>();
        }

        try
        {
            var info = new FileInfo(_path);
            if (info.Length <= 0L || info.Length > PersistedFileMaximumBytes)
            {
                throw new FormatException("Chat history file is empty or too large.");
            }

            string json = File.ReadAllText(_path, Encoding.UTF8);
            List<MapChatSnapshot> loaded = ChatHistoryJsonParser.Parse(json);
            int first = Math.Max(0, loaded.Count - Capacity);
            if (first == 0 && loaded.Count <= Capacity)
            {
                return loaded.ToArray();
            }

            var trimmed = new MapChatSnapshot[loaded.Count - first];
            loaded.CopyTo(first, trimmed, 0, trimmed.Length);
            return trimmed;
        }
        catch (Exception exception)
        {
            _log.Warning(
                $"[LiveMap] chat history could not be loaded ({exception.GetType().Name}); " +
                "starting with an empty chat log.");
            return Array.Empty<MapChatSnapshot>();
        }
    }

    public void ScheduleSave(MapChatSnapshot[] chats)
    {
        if (chats == null)
        {
            return;
        }

        lock (_lock)
        {
            _pending = chats;
            Volatile.Write(ref _dirty, 1);
            _writeSignal.Set();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopSignal.Set();
        if (!_writerThread.Join(ShutdownFlushMilliseconds))
        {
            _log.Warning("[LiveMap] chat history writer did not flush within the shutdown window.");
        }

        _writeSignal.Dispose();
        _stopSignal.Dispose();
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

        MapChatSnapshot[] snapshot;
        lock (_lock)
        {
            snapshot = _pending;
        }

        try
        {
            Persist(snapshot);
            Interlocked.Exchange(ref _writeFailureWarningLogged, 0);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _dirty, 1);
            if (Interlocked.Exchange(ref _writeFailureWarningLogged, 1) == 0)
            {
                _log.Warning(
                    $"[LiveMap] chat history could not be persisted " +
                    $"({exception.GetType().Name}: {SingleLineMessage(exception)}). " +
                    "The writer will retry.");
            }

            if (!_stopSignal.WaitOne(0))
            {
                _writeSignal.Set();
            }
        }
    }

    private void Persist(MapChatSnapshot[] chats)
    {
        var json = new StringBuilder(32 + (chats.Length * 256));
        json.Append("{\"chats\":[");
        for (int index = 0; index < chats.Length; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            MapChatSnapshot chat = chats[index];
            json.Append('{');
            json.Append("\"sequence\":").Append(chat.Sequence.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"x\":").Append(JsonWriter.Number(chat.X));
            json.Append(",\"z\":").Append(JsonWriter.Number(chat.Z));
            json.Append(",\"playerName\":").Append(JsonWriter.Quote(chat.PlayerName));
            json.Append(",\"text\":").Append(JsonWriter.Quote(chat.Text));
            json.Append(",\"shout\":").Append(chat.Shout ? "true" : "false");
            json.Append(",\"unixMs\":").Append(chat.UnixMs.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"serverOriginated\":").Append(chat.ServerOriginated ? "true" : "false");
            json.Append('}');
        }

        json.Append("]}");
        string directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = _path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            json.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(_path))
        {
            File.Replace(temporaryPath, _path, null);
        }
        else
        {
            File.Move(temporaryPath, _path);
        }
    }

    private static string SingleLineMessage(Exception exception)
    {
        return (exception.Message ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private static class ChatHistoryJsonParser
    {
        public static List<MapChatSnapshot> Parse(string json)
        {
            return new Parser(json).ParseDocument();
        }

        private sealed class Parser
        {
            private readonly string _json;
            private int _index;

            public Parser(string json)
            {
                _json = json ?? string.Empty;
            }

            public List<MapChatSnapshot> ParseDocument()
            {
                Expect('{');
                ExpectProperty("chats");
                List<MapChatSnapshot> chats = ParseChats();
                Expect('}');
                EnsureEnd();
                return chats;
            }

            private List<MapChatSnapshot> ParseChats()
            {
                var chats = new List<MapChatSnapshot>();
                Expect('[');
                if (TryConsume(']'))
                {
                    return chats;
                }

                while (true)
                {
                    chats.Add(ParseChat());
                    if (chats.Count > Capacity)
                    {
                        throw new FormatException("Chat history JSON contains too many messages.");
                    }

                    if (TryConsume(']'))
                    {
                        return chats;
                    }

                    Expect(',');
                }
            }

            private MapChatSnapshot ParseChat()
            {
                Expect('{');
                ExpectProperty("sequence");
                long sequence = ReadInt64();
                Expect(',');
                ExpectProperty("x");
                float x = ReadSingle();
                Expect(',');
                ExpectProperty("z");
                float z = ReadSingle();
                Expect(',');
                ExpectProperty("playerName");
                string playerName = ReadString();
                Expect(',');
                ExpectProperty("text");
                string text = ReadString();
                Expect(',');
                ExpectProperty("shout");
                bool shout = ReadBoolean();
                Expect(',');
                ExpectProperty("unixMs");
                long unixMs = ReadInt64();
                Expect(',');
                ExpectProperty("serverOriginated");
                bool serverOriginated = ReadBoolean();
                Expect('}');
                if (sequence <= 0L || unixMs <= 0L || string.IsNullOrWhiteSpace(text))
                {
                    throw new FormatException("Chat history JSON contains invalid values.");
                }

                return new MapChatSnapshot(
                    sequence,
                    x,
                    z,
                    playerName ?? string.Empty,
                    text,
                    shout,
                    unixMs,
                    serverOriginated);
            }

            private void ExpectProperty(string name)
            {
                string actual = ReadString();
                if (!string.Equals(actual, name, StringComparison.Ordinal))
                {
                    throw new FormatException("Chat history has an unexpected property.");
                }

                Expect(':');
            }

            private long ReadInt64()
            {
                SkipWhitespace();
                int start = _index;
                if (_index < _json.Length && _json[_index] == '-')
                {
                    _index++;
                }

                while (_index < _json.Length && char.IsDigit(_json[_index]))
                {
                    _index++;
                }

                if (_index == start ||
                    !long.TryParse(
                        _json.Substring(start, _index - start),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long value))
                {
                    throw new FormatException("Chat history contains an invalid number.");
                }

                return value;
            }

            private float ReadSingle()
            {
                SkipWhitespace();
                int start = _index;
                if (_index < _json.Length && (_json[_index] == '-' || _json[_index] == '+'))
                {
                    _index++;
                }

                bool digits = false;
                while (_index < _json.Length && char.IsDigit(_json[_index]))
                {
                    digits = true;
                    _index++;
                }

                if (_index < _json.Length && _json[_index] == '.')
                {
                    _index++;
                    while (_index < _json.Length && char.IsDigit(_json[_index]))
                    {
                        digits = true;
                        _index++;
                    }
                }

                if (!digits ||
                    !float.TryParse(
                        _json.Substring(start, _index - start),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out float value) ||
                    float.IsNaN(value) ||
                    float.IsInfinity(value))
                {
                    throw new FormatException("Chat history contains an invalid coordinate.");
                }

                return value;
            }

            private bool ReadBoolean()
            {
                SkipWhitespace();
                if (TryConsumeWord("true"))
                {
                    return true;
                }

                if (TryConsumeWord("false"))
                {
                    return false;
                }

                throw new FormatException("Chat history contains an invalid boolean.");
            }

            private string ReadString()
            {
                SkipWhitespace();
                if (_index >= _json.Length || _json[_index++] != '"')
                {
                    throw new FormatException("Chat history contains an invalid string.");
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
                        throw new FormatException("Chat history contains a control character.");
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
                            if (_index + 4 > _json.Length)
                            {
                                throw new FormatException("Chat history contains an invalid unicode escape.");
                            }

                            string hex = _json.Substring(_index, 4);
                            _index += 4;
                            if (!ushort.TryParse(
                                    hex,
                                    NumberStyles.AllowHexSpecifier,
                                    CultureInfo.InvariantCulture,
                                    out ushort codeUnit))
                            {
                                throw new FormatException("Chat history contains an invalid unicode escape.");
                            }

                            value.Append((char)codeUnit);
                            break;
                        default:
                            throw new FormatException("Chat history contains an invalid escape.");
                    }
                }

                throw new FormatException("Chat history contains an unterminated string.");
            }

            private void Expect(char character)
            {
                SkipWhitespace();
                if (_index >= _json.Length || _json[_index] != character)
                {
                    throw new FormatException("Chat history JSON is malformed.");
                }

                _index++;
            }

            private bool TryConsume(char character)
            {
                SkipWhitespace();
                if (_index >= _json.Length || _json[_index] != character)
                {
                    return false;
                }

                _index++;
                return true;
            }

            private bool TryConsumeWord(string word)
            {
                if (_index + word.Length > _json.Length)
                {
                    return false;
                }

                for (int offset = 0; offset < word.Length; offset++)
                {
                    if (_json[_index + offset] != word[offset])
                    {
                        return false;
                    }
                }

                char next = _index + word.Length < _json.Length ? _json[_index + word.Length] : '\0';
                if (char.IsLetterOrDigit(next))
                {
                    return false;
                }

                _index += word.Length;
                return true;
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
                    throw new FormatException("Chat history JSON has trailing content.");
                }
            }
        }
    }
}
