using System;
using System.Text;

namespace RadioChatter.Speech
{
    /// <summary>Tiny JSON helpers for the sidecar's flat request/response bodies; avoids a
    /// JSON dependency in net472. One implementation for every sidecar client so they cannot
    /// disagree on escaping.</summary>
    internal static class MiniJson
    {
        public static string Escape(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 32)
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            builder.Append(c);
                        break;
                }
            }
            return builder.ToString();
        }

        /// <summary>Value of the first string field named <paramref name="field"/>, with JSON
        /// escapes decoded; null when the field is absent or not a string.</summary>
        public static string ExtractString(string json, string field)
        {
            string marker = "\"" + field + "\"";
            int index = json.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
                return null;

            index = json.IndexOf(':', index + marker.Length);
            if (index < 0)
                return null;

            index = json.IndexOf('"', index);
            if (index < 0)
                return null;

            StringBuilder builder = new StringBuilder(64);
            for (int i = index + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"')
                    return builder.ToString();

                if (c == '\\' && i + 1 < json.Length)
                {
                    char escape = json[++i];
                    switch (escape)
                    {
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'u':
                            if (i + 4 < json.Length)
                            {
                                builder.Append((char)Convert.ToInt32(json.Substring(i + 1, 4), 16));
                                i += 4;
                            }
                            break;
                        default: builder.Append(escape); break;
                    }
                }
                else
                {
                    builder.Append(c);
                }
            }

            return null;
        }
    }
}
