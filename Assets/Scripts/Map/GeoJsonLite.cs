using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TramRush.Map
{
    /// <summary>
    /// Tiny JSON reader used only for the exported Overpass GeoJSON. Keeping it
    /// local avoids adding a package dependency to the jam build.
    /// </summary>
    public static class GeoJsonLite
    {
        public static Dictionary<string, object> ParseObject(string json)
        {
            object value = Parser.Parse(json);
            if (value is Dictionary<string, object> result)
                return result;

            throw new FormatException("GeoJSON root must be an object.");
        }

        private sealed class Parser : IDisposable
        {
            private readonly StringReader reader;

            private Parser(string json) => reader = new StringReader(json);

            public static object Parse(string json)
            {
                using var parser = new Parser(json);
                return parser.ReadValue();
            }

            public void Dispose() => reader.Dispose();

            private object ReadValue()
            {
                SkipWhitespace();
                int next = reader.Peek();
                return next switch
                {
                    '{' => ReadObject(),
                    '[' => ReadArray(),
                    '"' => ReadString(),
                    't' => ReadLiteral("true", true),
                    'f' => ReadLiteral("false", false),
                    'n' => ReadLiteral("null", null),
                    '-' or >= '0' and <= '9' => ReadNumber(),
                    _ => throw new FormatException($"Unexpected JSON token '{(char)next}'.")
                };
            }

            private Dictionary<string, object> ReadObject()
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                reader.Read();
                SkipWhitespace();
                if (reader.Peek() == '}')
                {
                    reader.Read();
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    string key = ReadString();
                    SkipWhitespace();
                    Expect(':');
                    result[key] = ReadValue();
                    SkipWhitespace();
                    int separator = reader.Read();
                    if (separator == '}') return result;
                    if (separator != ',') throw new FormatException("Expected ',' or '}' in object.");
                }
            }

            private List<object> ReadArray()
            {
                var result = new List<object>();
                reader.Read();
                SkipWhitespace();
                if (reader.Peek() == ']')
                {
                    reader.Read();
                    return result;
                }

                while (true)
                {
                    result.Add(ReadValue());
                    SkipWhitespace();
                    int separator = reader.Read();
                    if (separator == ']') return result;
                    if (separator != ',') throw new FormatException("Expected ',' or ']' in array.");
                }
            }

            private string ReadString()
            {
                Expect('"');
                var result = new StringBuilder();
                while (true)
                {
                    int value = reader.Read();
                    if (value < 0) throw new EndOfStreamException("Unterminated JSON string.");
                    if (value == '"') return result.ToString();
                    if (value != '\\')
                    {
                        result.Append((char)value);
                        continue;
                    }

                    int escaped = reader.Read();
                    result.Append(escaped switch
                    {
                        '"' => '"',
                        '\\' => '\\',
                        '/' => '/',
                        'b' => '\b',
                        'f' => '\f',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        'u' => ReadUnicode(),
                        _ => throw new FormatException("Invalid JSON escape sequence.")
                    });
                }
            }

            private char ReadUnicode()
            {
                var chars = new char[4];
                if (reader.Read(chars, 0, 4) != 4) throw new EndOfStreamException("Incomplete unicode escape.");
                return (char)int.Parse(new string(chars), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            private object ReadNumber()
            {
                var value = new StringBuilder();
                while (reader.Peek() is int c && c >= 0 && "-+0123456789.eE".IndexOf((char)c) >= 0)
                    value.Append((char)reader.Read());

                string text = value.ToString();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
                    return integer;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                    return number;
                throw new FormatException($"Invalid JSON number '{text}'.");
            }

            private object ReadLiteral(string literal, object value)
            {
                foreach (char expected in literal) Expect(expected);
                return value;
            }

            private void SkipWhitespace()
            {
                while (reader.Peek() is int c && c >= 0 && char.IsWhiteSpace((char)c)) reader.Read();
            }

            private void Expect(char expected)
            {
                int actual = reader.Read();
                if (actual != expected) throw new FormatException($"Expected '{expected}', got '{(char)actual}'.");
            }
        }
    }
}
