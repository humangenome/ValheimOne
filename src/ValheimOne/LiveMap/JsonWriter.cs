using System;
using System.Globalization;
using System.Text;

namespace ValheimOne.LiveMap;

internal static class JsonWriter
{
    public static string Quote(string? value)
    {
        if (value == null)
        {
            return "null";
        }

        var output = new StringBuilder(value.Length + 2);
        output.Append('"');
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            switch (character)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                case '\u2028':
                    output.Append("\\u2028");
                    break;
                case '\u2029':
                    output.Append("\\u2029");
                    break;
                default:
                    if (character < 0x20)
                    {
                        output.Append("\\u");
                        output.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                    {
                        output.Append(character).Append(value[index + 1]);
                        index++;
                    }
                    else if (char.IsSurrogate(character))
                    {
                        // Half of a character pair (from player-typed text): stricter JSON readers refuse it.
                        output.Append('\uFFFD');
                    }
                    else
                    {
                        output.Append(character);
                    }

                    break;
            }
        }

        output.Append('"');
        return output.ToString();
    }

    public static string Number(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? "0"
            : value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    public static string NumberOneDecimal(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? "0.0"
            : value.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
