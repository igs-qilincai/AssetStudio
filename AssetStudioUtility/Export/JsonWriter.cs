using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;

namespace AssetStudio
{
    /// <summary>
    /// Minimal JSON writer for TypeTree dumps (OrderedDictionary / List / primitives).
    /// Avoids reflection-based serializers so it also works in trimmed WebAssembly builds.
    /// Output layout matches what AssetStudioGUI produces with Json.NET.
    /// </summary>
    public static class JsonWriter
    {
        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value, int indent)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case char ch:
                    WriteString(sb, ch.ToString());
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case float f:
                    WriteFloat(sb, f, f.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case double d:
                    WriteFloat(sb, d, d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case byte[] bytes:
                    WriteString(sb, Convert.ToBase64String(bytes));
                    break;
                case OrderedDictionary dict:
                    if (dict.Count == 0)
                    {
                        sb.Append("{}");
                        break;
                    }
                    sb.Append('{');
                    var first = true;
                    foreach (DictionaryEntry kv in dict)
                    {
                        sb.Append(first ? "\n" : ",\n");
                        first = false;
                        sb.Append(' ', (indent + 1) * 2);
                        WriteString(sb, kv.Key.ToString());
                        sb.Append(": ");
                        Write(sb, kv.Value, indent + 1);
                    }
                    sb.Append('\n').Append(' ', indent * 2).Append('}');
                    break;
                case KeyValuePair<object, object> pair:
                    sb.Append("{\n").Append(' ', (indent + 1) * 2).Append("\"Key\": ");
                    Write(sb, pair.Key, indent + 1);
                    sb.Append(",\n").Append(' ', (indent + 1) * 2).Append("\"Value\": ");
                    Write(sb, pair.Value, indent + 1);
                    sb.Append('\n').Append(' ', indent * 2).Append('}');
                    break;
                case IList list:
                    if (list.Count == 0)
                    {
                        sb.Append("[]");
                        break;
                    }
                    sb.Append('[');
                    for (int i = 0; i < list.Count; i++)
                    {
                        sb.Append(i == 0 ? "\n" : ",\n");
                        sb.Append(' ', (indent + 1) * 2);
                        Write(sb, list[i], indent + 1);
                    }
                    sb.Append('\n').Append(' ', indent * 2).Append(']');
                    break;
                case IFormattable formattable:
                    sb.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                    break;
                default:
                    WriteString(sb, value.ToString());
                    break;
            }
        }

        private static void WriteFloat(StringBuilder sb, double value, string text)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                WriteString(sb, text);
            else
                sb.Append(text);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
