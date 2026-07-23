using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimOne.LiveMap;

internal static class ItemCatalogJsonParser
{
    public static ItemCatalog.ConsoleItem[] ParseConsoleItems(string json)
    {
        return new Parser(json).ParseDocument();
    }

    private sealed class Parser
    {
        private const int MaximumNestingDepth = 64;

        private readonly string _json;
        private int _index;

        public Parser(string json)
        {
            _json = json ?? throw new ArgumentNullException(nameof(json));
        }

        public ItemCatalog.ConsoleItem[] ParseDocument()
        {
            Expect('{');
            ExpectProperty("version");
            SkipValue(0);
            Expect(',');
            ExpectProperty("generatedUtc");
            ReadString();
            Expect(',');
            ExpectProperty("items");
            ItemCatalog.ConsoleItem[] items = ParseItems();
            Expect('}');
            EnsureEnd();
            return items;
        }

        private ItemCatalog.ConsoleItem[] ParseItems()
        {
            var items = new List<ItemCatalog.ConsoleItem>();
            Expect('[');
            if (TryConsume(']'))
            {
                return items.ToArray();
            }

            while (true)
            {
                items.Add(ParseItem());
                if (TryConsume(']'))
                {
                    return items.ToArray();
                }

                Expect(',');
            }
        }

        private ItemCatalog.ConsoleItem ParseItem()
        {
            Expect('{');
            ExpectProperty("token");
            string token = ReadString();
            Expect(',');
            ExpectProperty("name");
            string name = ReadString();
            Expect(',');
            ExpectProperty("description");
            ReadString();
            Expect(',');
            ExpectProperty("type");
            string type = ReadString();
            Expect(',');
            ExpectProperty("maxQuality");
            int maxQuality = ReadInt32();
            Expect(',');
            ExpectProperty("toolTier");
            int toolTier = ReadInt32();
            Expect(',');
            ExpectProperty("weight");
            float weight = ReadSingle();
            Expect(',');
            ExpectProperty("maxStackSize");
            int maxStackSize = ReadInt32();
            Expect(',');
            ExpectProperty("teleportable");
            bool teleportable = ReadBoolean();
            Expect(',');

            string property = ReadPropertyName();
            ItemCatalog.ConsoleArmor? armor = null;
            if (string.Equals(property, "armor", StringComparison.Ordinal))
            {
                armor = ParseArmor();
                Expect(',');
                property = ReadPropertyName();
            }

            ItemCatalog.ConsoleDamageSummary? damage = null;
            if (string.Equals(property, "damage", StringComparison.Ordinal))
            {
                damage = ParseDamageSummary();
                Expect(',');
                property = ReadPropertyName();
            }

            ExpectPropertyName(property, "recipes");
            ItemCatalog.ConsoleRecipe[] recipes = ParseRecipes();
            Expect(',');
            ExpectProperty("sources");
            ItemCatalog.ConsoleSource[] sources = ParseSources();
            Expect(',');
            ExpectProperty("uses");
            SkipValue(0);
            Expect(',');
            ExpectProperty("droppedBy");
            ItemCatalog.ConsoleDrop[] droppedBy = ParseDrops();
            Expect('}');

            return new ItemCatalog.ConsoleItem
            {
                Token = token,
                Name = name,
                Type = type,
                MaxQuality = maxQuality,
                ToolTier = toolTier,
                Weight = weight,
                MaxStackSize = maxStackSize,
                Teleportable = teleportable,
                Armor = armor,
                Damage = damage,
                Recipes = recipes,
                Sources = sources,
                DroppedBy = droppedBy,
            };
        }

        private ItemCatalog.ConsoleArmor ParseArmor()
        {
            Expect('{');
            ExpectProperty("base");
            float baseArmor = ReadSingle();
            Expect(',');
            ExpectProperty("perLevel");
            float perLevel = ReadSingle();
            Expect('}');
            return new ItemCatalog.ConsoleArmor
            {
                Base = baseArmor,
                PerLevel = perLevel,
            };
        }

        private ItemCatalog.ConsoleDamageSummary ParseDamageSummary()
        {
            Expect('{');
            ExpectProperty("base");
            ItemCatalog.ConsoleDamage baseDamage = ParseDamage();
            Expect(',');
            ExpectProperty("perLevel");
            ItemCatalog.ConsoleDamage perLevel = ParseDamage();
            Expect('}');
            return new ItemCatalog.ConsoleDamageSummary
            {
                Base = baseDamage,
                PerLevel = perLevel,
            };
        }

        private ItemCatalog.ConsoleDamage ParseDamage()
        {
            var damage = new ItemCatalog.ConsoleDamage();
            Expect('{');
            if (TryConsume('}'))
            {
                return damage;
            }

            while (true)
            {
                string property = ReadPropertyName();
                float value = ReadSingle();
                switch (property)
                {
                    case "generic":
                        damage.Generic = value;
                        break;
                    case "blunt":
                        damage.Blunt = value;
                        break;
                    case "slash":
                        damage.Slash = value;
                        break;
                    case "pierce":
                        damage.Pierce = value;
                        break;
                    case "chop":
                        damage.Chop = value;
                        break;
                    case "pickaxe":
                        damage.Pickaxe = value;
                        break;
                    case "fire":
                        damage.Fire = value;
                        break;
                    case "frost":
                        damage.Frost = value;
                        break;
                    case "lightning":
                        damage.Lightning = value;
                        break;
                    case "poison":
                        damage.Poison = value;
                        break;
                    case "spirit":
                        damage.Spirit = value;
                        break;
                    default:
                        throw Malformed("contains an unexpected damage property");
                }

                if (TryConsume('}'))
                {
                    return damage;
                }

                Expect(',');
            }
        }

        private ItemCatalog.ConsoleRecipe[] ParseRecipes()
        {
            var recipes = new List<ItemCatalog.ConsoleRecipe>();
            Expect('[');
            if (TryConsume(']'))
            {
                return recipes.ToArray();
            }

            while (true)
            {
                recipes.Add(ParseRecipe());
                if (TryConsume(']'))
                {
                    return recipes.ToArray();
                }

                Expect(',');
            }
        }

        private ItemCatalog.ConsoleRecipe ParseRecipe()
        {
            Expect('{');
            ExpectProperty("enabled");
            bool enabled = ReadBoolean();
            Expect(',');
            ExpectProperty("amount");
            int amount = ReadInt32();
            Expect(',');
            ExpectProperty("station");
            ItemCatalog.ConsoleStation? station = ParseNullableStation();
            Expect(',');
            ExpectProperty("minStationLevel");
            int minimumStationLevel = ReadInt32();
            Expect(',');
            ExpectProperty("ingredients");
            ItemCatalog.ConsoleIngredient[] ingredients = ParseIngredients();
            Expect('}');
            return new ItemCatalog.ConsoleRecipe
            {
                Enabled = enabled,
                Amount = amount,
                Station = station,
                MinimumStationLevel = minimumStationLevel,
                Ingredients = ingredients,
            };
        }

        private ItemCatalog.ConsoleIngredient[] ParseIngredients()
        {
            var ingredients = new List<ItemCatalog.ConsoleIngredient>();
            Expect('[');
            if (TryConsume(']'))
            {
                return ingredients.ToArray();
            }

            while (true)
            {
                ingredients.Add(ParseIngredient());
                if (TryConsume(']'))
                {
                    return ingredients.ToArray();
                }

                Expect(',');
            }
        }

        private ItemCatalog.ConsoleIngredient ParseIngredient()
        {
            Expect('{');
            ExpectProperty("prefab");
            string prefab = ReadString();
            Expect(',');
            ExpectProperty("name");
            string name = ReadString();
            Expect(',');
            ExpectProperty("amount");
            int amount = ReadInt32();
            Expect(',');
            ExpectProperty("amountPerLevel");
            int amountPerLevel = ReadInt32();
            Expect('}');
            return new ItemCatalog.ConsoleIngredient
            {
                Prefab = prefab,
                Name = name,
                Amount = amount,
                AmountPerLevel = amountPerLevel,
            };
        }

        private ItemCatalog.ConsoleSource[] ParseSources()
        {
            var sources = new List<ItemCatalog.ConsoleSource>();
            Expect('[');
            if (TryConsume(']'))
            {
                return sources.ToArray();
            }

            while (true)
            {
                sources.Add(ParseSource());
                if (TryConsume(']'))
                {
                    return sources.ToArray();
                }

                Expect(',');
            }
        }

        private ItemCatalog.ConsoleSource ParseSource()
        {
            Expect('{');
            ExpectProperty("method");
            string method = ReadString();
            Expect(',');
            ExpectProperty("station");
            ItemCatalog.ConsoleStation? station = ParseNullableStation();
            Expect(',');
            ExpectProperty("input");
            ItemCatalog.ConsoleItemReference? input = ParseNullableItemReference();
            Expect(',');
            ExpectProperty("amount");
            int amount = ReadInt32();
            Expect('}');
            return new ItemCatalog.ConsoleSource
            {
                Method = method,
                Station = station,
                Input = input,
                Amount = amount,
            };
        }

        private ItemCatalog.ConsoleStation? ParseNullableStation()
        {
            if (TryConsumeLiteral("null"))
            {
                return null;
            }

            Expect('{');
            ExpectProperty("prefab");
            string prefab = ReadString();
            Expect(',');
            ExpectProperty("name");
            string name = ReadString();
            Expect('}');
            return new ItemCatalog.ConsoleStation
            {
                Prefab = prefab,
                Name = name,
            };
        }

        private ItemCatalog.ConsoleItemReference? ParseNullableItemReference()
        {
            if (TryConsumeLiteral("null"))
            {
                return null;
            }

            Expect('{');
            ExpectProperty("prefab");
            string prefab = ReadString();
            Expect(',');
            ExpectProperty("name");
            string name = ReadString();
            Expect('}');
            return new ItemCatalog.ConsoleItemReference
            {
                Prefab = prefab,
                Name = name,
            };
        }

        private ItemCatalog.ConsoleDrop[] ParseDrops()
        {
            var drops = new List<ItemCatalog.ConsoleDrop>();
            Expect('[');
            if (TryConsume(']'))
            {
                return drops.ToArray();
            }

            while (true)
            {
                drops.Add(ParseDrop());
                if (TryConsume(']'))
                {
                    return drops.ToArray();
                }

                Expect(',');
            }
        }

        private ItemCatalog.ConsoleDrop ParseDrop()
        {
            Expect('{');
            ExpectProperty("creature");
            string creature = ReadString();
            Expect(',');
            ExpectProperty("name");
            string name = ReadString();
            Expect(',');
            ExpectProperty("chance");
            float chance = ReadSingle();
            Expect('}');
            return new ItemCatalog.ConsoleDrop
            {
                Creature = creature,
                Name = name,
                Chance = chance,
            };
        }

        private void SkipValue(int depth)
        {
            if (depth >= MaximumNestingDepth)
            {
                throw Malformed("is nested too deeply");
            }

            SkipWhitespace();
            if (_index >= _json.Length)
            {
                throw Malformed("ends before a value");
            }

            switch (_json[_index])
            {
                case '"':
                    ReadString();
                    return;
                case '{':
                    SkipObject(depth + 1);
                    return;
                case '[':
                    SkipArray(depth + 1);
                    return;
                case 't':
                    ExpectLiteral("true");
                    return;
                case 'f':
                    ExpectLiteral("false");
                    return;
                case 'n':
                    ExpectLiteral("null");
                    return;
                default:
                    ReadNumberToken();
                    return;
            }
        }

        private void SkipObject(int depth)
        {
            Expect('{');
            if (TryConsume('}'))
            {
                return;
            }

            while (true)
            {
                ReadPropertyName();
                SkipValue(depth);
                if (TryConsume('}'))
                {
                    return;
                }

                Expect(',');
            }
        }

        private void SkipArray(int depth)
        {
            Expect('[');
            if (TryConsume(']'))
            {
                return;
            }

            while (true)
            {
                SkipValue(depth);
                if (TryConsume(']'))
                {
                    return;
                }

                Expect(',');
            }
        }

        private void ExpectProperty(string expected)
        {
            ExpectPropertyName(ReadPropertyName(), expected);
        }

        private string ReadPropertyName()
        {
            string property = ReadString();
            Expect(':');
            return property;
        }

        private static void ExpectPropertyName(string actual, string expected)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw Malformed("contains an unexpected property");
            }
        }

        private bool ReadBoolean()
        {
            if (TryConsumeLiteral("true"))
            {
                return true;
            }

            if (TryConsumeLiteral("false"))
            {
                return false;
            }

            throw Malformed("contains an invalid boolean");
        }

        private int ReadInt32()
        {
            string token = ReadNumberToken();
            if (!int.TryParse(
                    token,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int value))
            {
                throw Malformed("contains an invalid integer");
            }

            return value;
        }

        private float ReadSingle()
        {
            string token = ReadNumberToken();
            if (!float.TryParse(
                    token,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float value) ||
                float.IsNaN(value) ||
                float.IsInfinity(value))
            {
                throw Malformed("contains an invalid number");
            }

            return value;
        }

        private string ReadNumberToken()
        {
            SkipWhitespace();
            int start = _index;
            if (_index < _json.Length && _json[_index] == '-')
            {
                _index++;
            }

            if (_index >= _json.Length)
            {
                throw Malformed("is missing a number");
            }

            if (_json[_index] == '0')
            {
                _index++;
                if (_index < _json.Length && char.IsDigit(_json[_index]))
                {
                    throw Malformed("contains an invalid number");
                }
            }
            else
            {
                int integerStart = _index;
                while (_index < _json.Length && char.IsDigit(_json[_index]))
                {
                    _index++;
                }

                if (_index == integerStart)
                {
                    throw Malformed("is missing a number");
                }
            }

            if (_index < _json.Length && _json[_index] == '.')
            {
                _index++;
                int fractionStart = _index;
                while (_index < _json.Length && char.IsDigit(_json[_index]))
                {
                    _index++;
                }

                if (_index == fractionStart)
                {
                    throw Malformed("contains an invalid number");
                }
            }

            if (_index < _json.Length &&
                (_json[_index] == 'e' || _json[_index] == 'E'))
            {
                _index++;
                if (_index < _json.Length &&
                    (_json[_index] == '+' || _json[_index] == '-'))
                {
                    _index++;
                }

                int exponentStart = _index;
                while (_index < _json.Length && char.IsDigit(_json[_index]))
                {
                    _index++;
                }

                if (_index == exponentStart)
                {
                    throw Malformed("contains an invalid number");
                }
            }

            return _json.Substring(start, _index - start);
        }

        private string ReadString()
        {
            SkipWhitespace();
            if (_index >= _json.Length || _json[_index++] != '"')
            {
                throw Malformed("contains an invalid string");
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
                    throw Malformed("contains a control character");
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
                        throw Malformed("contains an invalid escape sequence");
                }
            }

            throw Malformed("contains an unterminated string");
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
                throw Malformed("contains an invalid unicode escape");
            }

            _index += 4;
            return (char)value;
        }

        private bool TryConsumeLiteral(string expected)
        {
            SkipWhitespace();
            if (_index + expected.Length > _json.Length ||
                string.CompareOrdinal(
                    _json,
                    _index,
                    expected,
                    0,
                    expected.Length) != 0)
            {
                return false;
            }

            _index += expected.Length;
            return true;
        }

        private void ExpectLiteral(string expected)
        {
            if (!TryConsumeLiteral(expected))
            {
                throw Malformed("contains an invalid literal");
            }
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
                throw Malformed("is malformed");
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
                throw Malformed("contains trailing data");
            }
        }

        private static FormatException Malformed(string detail)
        {
            return new FormatException("Item catalog JSON " + detail + ".");
        }
    }
}
