using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LoopHost
{

    public static class MiniJson
    {
        public const int MaxJsonChars = 64 * 1024;
        public const int MaxDepth = 64;

        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) throw new ArgumentException("empty json");
            if (json.Length > MaxJsonChars)
                throw new FormatException("json too long: " + json.Length + " > " + MaxJsonChars);
            var reader = new StringReader(json);
            var parser = new Parser(reader.ReadToEnd());
            var value = parser.ParseValue();
            parser.SkipWhitespace();
            if (!parser.AtEnd) throw new FormatException("trailing characters at " + parser.Position);
            return value;
        }

        public static object TryParse(string json)
        {
            try { return Parse(json); }
            catch { return null; }
        }

        public static Dictionary<string, object> ParseObject(string json)
        {
            return Parse(json) as Dictionary<string, object>;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            private int _depth;

            public Parser(string s) { _s = s; }

            public bool AtEnd { get { return _i >= _s.Length; } }
            public int Position { get { return _i; } }

            public void SkipWhitespace()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            private char Peek()
            {
                if (_i >= _s.Length) throw new FormatException("unexpected end of input");
                return _s[_i];
            }

            private char Next()
            {
                char c = Peek();
                _i++;
                return c;
            }

            public object ParseValue()
            {
                if (++_depth > MaxDepth)
                    throw new FormatException("nesting too deep (> " + MaxDepth + ") at " + _i);
                try
                {
                    SkipWhitespace();
                    char c = Peek();
                    switch (c)
                    {
                        case '{': return ParseObject();
                        case '[': return ParseArray();
                        case '"': return ParseString();
                        case 't': ExpectLiteral("true"); return true;
                        case 'f': ExpectLiteral("false"); return false;
                        case 'n': ExpectLiteral("null"); return null;
                        default: return ParseNumber();
                    }
                }
                finally { _depth--; }
            }

            private void ExpectLiteral(string literal)
            {
                if (string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                    throw new FormatException("invalid literal at " + _i);
                _i += literal.Length;
            }

            private Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                Next();
                SkipWhitespace();
                if (Peek() == '}') { Next(); return dict; }
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"') throw new FormatException("expected object key at " + _i);
                    string key = ParseString();
                    SkipWhitespace();
                    if (Next() != ':') throw new FormatException("expected ':' at " + (_i - 1));
                    dict[key] = ParseValue();
                    SkipWhitespace();
                    char c = Next();
                    if (c == '}') return dict;
                    if (c != ',') throw new FormatException("expected ',' or '}' at " + (_i - 1));
                }
            }

            private List<object> ParseArray()
            {
                var list = new List<object>();
                Next();
                SkipWhitespace();
                if (Peek() == ']') { Next(); return list; }
                while (true)
                {
                    list.Add(ParseValue());
                    SkipWhitespace();
                    char c = Next();
                    if (c == ']') return list;
                    if (c != ',') throw new FormatException("expected ',' or ']' at " + (_i - 1));
                }
            }

            private string ParseString()
            {
                var sb = new StringBuilder();
                Next();
                while (true)
                {
                    char c = Next();
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        char esc = Next();
                        switch (esc)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (_i + 4 > _s.Length) throw new FormatException("bad \\u escape");
                                sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                _i += 4;
                                break;
                            default: throw new FormatException("bad escape '\\" + esc + "'");
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }

            private object ParseNumber()
            {
                int start = _i;
                bool isDouble = false;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (char.IsDigit(c)) { _i++; continue; }
                    if (c == '-' || c == '+') { _i++; continue; }
                    if (c == '.' || c == 'e' || c == 'E') { isDouble = true; _i++; continue; }
                    break;
                }
                string token = _s.Substring(start, _i - start);
                if (token.Length == 0) throw new FormatException("invalid number at " + start);
                if (isDouble)
                    return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
                long l;
                if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                    return l;
                return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        public static string Write(object value)
        {
            var sb = new StringBuilder(64);
            WriteValue(sb, value);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }

            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }

            if (value is long || value is int || value is short || value is byte)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            if (value is double || value is float || value is decimal)
            {
                sb.Append(Convert.ToDouble(value, CultureInfo.InvariantCulture)
                    .ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            var dict = value as IDictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kvp in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kvp.Key);
                    sb.Append(':');
                    WriteValue(sb, kvp.Value);
                }
                sb.Append('}');
                return;
            }

            var enumerable = value as System.Collections.IEnumerable;
            if (enumerable != null)
            {
                sb.Append('[');
                bool first2 = true;
                foreach (var item in enumerable)
                {
                    if (!first2) sb.Append(',');
                    first2 = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, value.ToString());
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}

