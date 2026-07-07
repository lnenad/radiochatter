using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Logging;

namespace RadioChatter.Speech
{
    /// <summary>Phrase templates and slot filling. Templates come from phrases.json: a loose copy
    /// next to the plugin DLL wins (user-editable without rebuilding), otherwise the copy embedded
    /// in the assembly is used. Rendering picks a random variant, avoiding the last-used one per key.</summary>
    internal sealed class PhraseEngine
    {
        private const string PhrasesFileName = "phrases.json";

        private readonly Random _random = new Random();
        private readonly Dictionary<string, string[]> _templates = new Dictionary<string, string[]>();
        private readonly Dictionary<string, int> _lastVariant = new Dictionary<string, int>();

        public PhraseEngine(ManualLogSource log)
        {
            try
            {
                string source;
                string json = ReadPhrasesJson(out source);
                foreach (KeyValuePair<string, string[]> bank in PhraseJson.Parse(json))
                    _templates[bank.Key] = bank.Value;

                log?.LogInfo($"Loaded {_templates.Count} phrase banks from {source}.");
            }
            catch (Exception ex)
            {
                log?.LogError($"Failed to load {PhrasesFileName} ({ex.Message}); radio calls will speak raw event keys.");
            }
        }

        public string Render(string eventKey, IDictionary<string, string> slots)
        {
            string[] variants;
            if (!_templates.TryGetValue(eventKey, out variants) || variants.Length == 0)
                return eventKey;

            int index = PickVariant(eventKey, variants.Length);
            string text = variants[index];

            foreach (KeyValuePair<string, string> slot in slots)
                text = text.Replace("{" + slot.Key + "}", slot.Value ?? string.Empty);

            return text.Replace("  ", " ").Trim();
        }

        private int PickVariant(string key, int count)
        {
            if (count == 1)
                return 0;

            int previous;
            _lastVariant.TryGetValue(key, out previous);

            int next = _random.Next(count);
            if (next == previous)
                next = (next + 1) % count;

            _lastVariant[key] = next;
            return next;
        }

        private static string ReadPhrasesJson(out string source)
        {
            Assembly assembly = typeof(PhraseEngine).Assembly;

            string pluginDir = Path.GetDirectoryName(assembly.Location);
            string loosePath = string.IsNullOrEmpty(pluginDir) ? null : Path.Combine(pluginDir, PhrasesFileName);
            if (loosePath != null && File.Exists(loosePath))
            {
                source = loosePath;
                return File.ReadAllText(loosePath);
            }

            foreach (string resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.EndsWith(PhrasesFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    source = "embedded resource";
                    return reader.ReadToEnd();
                }
            }

            throw new FileNotFoundException($"no embedded {PhrasesFileName} resource");
        }
    }

    /// <summary>Minimal JSON reader for the phrases file shape: an object mapping each key to an
    /// array of template strings. Anything else is a format error.</summary>
    internal static class PhraseJson
    {
        public static Dictionary<string, string[]> Parse(string json)
        {
            Dictionary<string, string[]> result = new Dictionary<string, string[]>();
            int i = 0;

            Expect(json, ref i, '{');
            SkipWhitespace(json, ref i);
            if (Peek(json, i) == '}')
            {
                i++;
                return result;
            }

            while (true)
            {
                string key = ParseString(json, ref i);
                Expect(json, ref i, ':');
                result[key] = ParseStringArray(json, ref i);

                SkipWhitespace(json, ref i);
                char c = Next(json, ref i);
                if (c == '}')
                    return result;

                if (c != ',')
                    throw Error(i, $"expected ',' or '}}' but found '{c}'");
            }
        }

        private static string[] ParseStringArray(string json, ref int i)
        {
            Expect(json, ref i, '[');
            List<string> items = new List<string>(4);

            SkipWhitespace(json, ref i);
            if (Peek(json, i) == ']')
            {
                i++;
                return items.ToArray();
            }

            while (true)
            {
                items.Add(ParseString(json, ref i));

                SkipWhitespace(json, ref i);
                char c = Next(json, ref i);
                if (c == ']')
                    return items.ToArray();

                if (c != ',')
                    throw Error(i, $"expected ',' or ']' but found '{c}'");
            }
        }

        private static string ParseString(string json, ref int i)
        {
            Expect(json, ref i, '"');
            StringBuilder builder = new StringBuilder(32);

            while (true)
            {
                char c = Next(json, ref i);
                if (c == '"')
                    return builder.ToString();

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                char escape = Next(json, ref i);
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (i + 4 > json.Length)
                            throw Error(i, "truncated \\u escape");
                        builder.Append((char)Convert.ToInt32(json.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default:
                        throw Error(i, $"unsupported escape '\\{escape}'");
                }
            }
        }

        private static void Expect(string json, ref int i, char expected)
        {
            SkipWhitespace(json, ref i);
            char c = Next(json, ref i);
            if (c != expected)
                throw Error(i, $"expected '{expected}' but found '{c}'");
        }

        private static void SkipWhitespace(string json, ref int i)
        {
            while (i < json.Length && char.IsWhiteSpace(json[i]))
                i++;
        }

        private static char Peek(string json, int i)
        {
            if (i >= json.Length)
                throw Error(i, "unexpected end of file");

            return json[i];
        }

        private static char Next(string json, ref int i)
        {
            if (i >= json.Length)
                throw Error(i, "unexpected end of file");

            return json[i++];
        }

        private static Exception Error(int i, string message)
        {
            return new InvalidDataException($"{message} at offset {i}");
        }
    }
}
