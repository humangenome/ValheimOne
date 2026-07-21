using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimOne.ActivityLog;

internal sealed class PlayerGhostEntry
{
    public PlayerGhostEntry(
        string characterName,
        float x,
        float z,
        long lastSeenUnixMs,
        long lastSessionSeconds,
        long totalPlaySeconds,
        bool positionShared)
    {
        CharacterName = characterName;
        X = x;
        Z = z;
        LastSeenUnixMs = lastSeenUnixMs;
        LastSessionSeconds = lastSessionSeconds;
        TotalPlaySeconds = totalPlaySeconds;
        PositionShared = positionShared;
    }

    public string CharacterName { get; }

    public float X { get; }

    public float Z { get; }

    public long LastSeenUnixMs { get; }

    public long LastSessionSeconds { get; }

    public long TotalPlaySeconds { get; }

    public bool PositionShared { get; }
}

internal static class PlayerGhostJsonParser
{
    public static List<PlayerGhostEntry> Parse(string json)
    {
        return new Parser(json).ParseEntries();
    }

    private sealed class Parser
    {
        private readonly string _json;
        private int _index;

        public Parser(string json)
        {
            _json = json;
        }

        public List<PlayerGhostEntry> ParseEntries()
        {
            var entries = new List<PlayerGhostEntry>();
            Expect('[');
            if (TryConsume(']'))
            {
                EnsureEnd();
                return entries;
            }

            while (true)
            {
                entries.Add(ParseEntry());
                if (TryConsume(']'))
                {
                    EnsureEnd();
                    return entries;
                }

                Expect(',');
            }
        }

        private PlayerGhostEntry ParseEntry()
        {
            Expect('{');
            ExpectProperty("characterName");
            string characterName = ReadString().Trim();
            Expect(',');
            ExpectProperty("x");
            float x = ReadSingle();
            Expect(',');
            ExpectProperty("z");
            float z = ReadSingle();
            Expect(',');
            ExpectProperty("lastSeenUnixMs");
            long lastSeenUnixMs = ReadInt64();
            Expect(',');
            ExpectProperty("lastSessionSeconds");
            long lastSessionSeconds = ReadInt64();
            Expect(',');
            ExpectProperty("totalPlaySeconds");
            long totalPlaySeconds = ReadInt64();
            Expect(',');
            ExpectProperty("positionShared");
            bool positionShared = ReadBoolean();
            Expect('}');

            if (characterName.Length == 0 || lastSeenUnixMs <= 0L ||
                lastSessionSeconds < 0L || totalPlaySeconds < 0L)
            {
                throw new FormatException("Player ghost contains invalid values.");
            }

            return new PlayerGhostEntry(
                characterName,
                x,
                z,
                lastSeenUnixMs,
                lastSessionSeconds,
                Math.Max(lastSessionSeconds, totalPlaySeconds),
                positionShared);
        }

        private void ExpectProperty(string expected)
        {
            string actual = ReadString();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new FormatException("Player ghost has an unexpected property.");
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
                throw new FormatException("Player ghost contains an invalid coordinate.");
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
                throw new FormatException("Player ghost contains an invalid integer.");
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
                throw new FormatException("Player ghost is missing a number.");
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

            throw new FormatException("Player ghost contains an invalid boolean.");
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
                throw new FormatException("Player ghost contains an invalid string.");
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
                    throw new FormatException("Player ghost contains a control character.");
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
                        throw new FormatException("Player ghost contains an invalid escape.");
                }
            }

            throw new FormatException("Player ghost contains an unterminated string.");
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
                throw new FormatException("Player ghost contains an invalid Unicode escape.");
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
                throw new FormatException("Player ghost JSON is malformed.");
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
                throw new FormatException("Player ghost JSON contains trailing data.");
            }
        }
    }
}
