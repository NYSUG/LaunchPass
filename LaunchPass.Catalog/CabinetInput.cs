using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LaunchPass.Catalog
{
    public enum CabinetAction { PreviousGame, NextGame, PreviousCategory, NextCategory, Select, Back, PreviousLetter, NextLetter, Favorites, AddFavorite, Search, NextTrack }

    [DataContract]
    internal sealed class CabinetInputDocument
    {
        [DataMember(Name = "version", IsRequired = true)] public int Version { get; set; }
        [DataMember(Name = "enabled")] public bool? Enabled { get; set; }
        [DataMember(Name = "repeatDelayMs")] public int? RepeatDelay { get; set; }
        [DataMember(Name = "repeatIntervalMs")] public int? RepeatInterval { get; set; }
        [DataMember(Name = "bindings", IsRequired = true)] public List<CabinetBinding> Bindings { get; set; }
    }
    [DataContract]
    internal sealed class CabinetBinding
    {
        [DataMember(Name = "key", IsRequired = true)] public string Key { get; set; }
        [DataMember(Name = "action", IsRequired = true)] public string Action { get; set; }
    }

    // Integer virtual-key codes keep policy/repeat/navigation tests independent of UWP.
    public sealed class CabinetInputMap
    {
        private readonly Dictionary<int, CabinetAction> bindings = new Dictionary<int, CabinetAction>();
        public bool Enabled { get; private set; } = true;
        public int RepeatDelayMs { get; private set; } = 350;
        public int RepeatIntervalMs { get; private set; } = 140;
        public static CabinetInputMap Defaults()
        {
            var map = new CabinetInputMap();
            map.bindings.Add(38, CabinetAction.PreviousGame); map.bindings.Add(40, CabinetAction.NextGame);
            map.bindings.Add(37, CabinetAction.PreviousCategory); map.bindings.Add(39, CabinetAction.NextCategory);
            // Standard keyboard fallbacks, not claims about physical cabinet buttons.
            map.bindings.Add(13, CabinetAction.Select); map.bindings.Add(27, CabinetAction.Back);
            map.bindings.Add(89, CabinetAction.Search); // Preserve the existing keyboard Y search shortcut.
            return map;
        }
        public static CabinetInputMap Parse(string json)
        {
            string trimmed = (json ?? "").Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') throw new FormatException("CabinetInput.json must contain one JSON object.");
            int depth = 0; bool inString = false, escaped = false;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') inString = true;
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth == 0 && i != trimmed.Length - 1) throw new FormatException("Unexpected data after CabinetInput.json.");
                }
            }
            CabinetInputDocument doc;
            try
            {
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json ?? ""), System.Xml.XmlDictionaryReaderQuotas.Max))
                {
                    doc = (CabinetInputDocument)new DataContractJsonSerializer(typeof(CabinetInputDocument)).ReadObject(reader);
                    if (reader.MoveToContent() != System.Xml.XmlNodeType.None) throw new FormatException("Unexpected data after CabinetInput.json.");
                }
            }
            catch (Exception ex) when (ex is SerializationException || ex is System.Xml.XmlException)
            { throw new FormatException("CabinetInput.json is invalid or missing required fields.", ex); }
            if (doc == null || doc.Version != 1 || doc.Bindings == null || doc.Bindings.Count > 128) throw new FormatException("Expected version 1 and at most 128 bindings.");
            var map = new CabinetInputMap { Enabled = doc.Enabled ?? true, RepeatDelayMs = doc.RepeatDelay ?? 350, RepeatIntervalMs = doc.RepeatInterval ?? 140 };
            if (map.RepeatDelayMs < 150 || map.RepeatDelayMs > 2000 || map.RepeatIntervalMs < 60 || map.RepeatIntervalMs > 1000) throw new FormatException("Invalid repeat timing.");
            foreach (var binding in doc.Bindings)
            {
                CabinetAction action;
                if (binding == null || string.IsNullOrWhiteSpace(binding.Action) || !Enum.TryParse(binding.Action, false, out action) || !Enum.IsDefined(typeof(CabinetAction), action) || binding.Action != action.ToString()) throw new FormatException("Unknown cabinet action.");
                int key = KeyCode(binding.Key);
                if (map.bindings.ContainsKey(key)) throw new FormatException("Duplicate key: " + binding.Key);
                map.bindings.Add(key, action);
            }
            return map;
        }
        public bool TryResolve(int rawKey, out int bindingKey, out CabinetAction action)
        {
            bindingKey = rawKey; action = default(CabinetAction);
            if (!Enabled || IsGamepad(rawKey)) return false;
            if (bindings.TryGetValue(rawKey, out action)) return true;
            // Generic modifier binding also accepts the corresponding left/right event variants.
            if (rawKey == 160 || rawKey == 161) bindingKey = 16;
            if (rawKey == 162 || rawKey == 163) bindingKey = 17;
            if (rawKey == 164 || rawKey == 165) bindingKey = 18;
            return bindings.TryGetValue(bindingKey, out action);
        }
        public static bool IsGamepad(int key) { return key >= 195 && key <= 218; }
        public static bool Repeats(CabinetAction action)
        {
            return action == CabinetAction.PreviousGame || action == CabinetAction.NextGame || action == CabinetAction.PreviousCategory || action == CabinetAction.NextCategory;
        }
        public static int KeyCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new FormatException("A virtual-key name or number is required.");
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                {"Up",38},{"Down",40},{"Left",37},{"Right",39},{"Enter",13},{"Escape",27},{"Space",32},{"Tab",9},{"Back",8},
                {"Control",17},{"LeftControl",162},{"RightControl",163},{"Menu",18},{"Alt",18},{"LeftMenu",164},{"RightMenu",165},
                {"Shift",16},{"LeftShift",160},{"RightShift",161},{"PageUp",33},{"PageDown",34},{"Home",36},{"End",35}
            };
            int key;
            if (!names.TryGetValue(value, out key))
            {
                if (value.Length == 1 && char.ToUpperInvariant(value[0]) >= 'A' && char.ToUpperInvariant(value[0]) <= 'Z') key = char.ToUpperInvariant(value[0]);
                else if (value.StartsWith("NumberPad", StringComparison.OrdinalIgnoreCase) && value.Length == 10 && char.IsDigit(value[9]) && value[9] <= '9') key = 96 + value[9] - '0';
                else if (value.StartsWith("Number", StringComparison.OrdinalIgnoreCase) && value.Length == 7 && value[6] >= '0' && value[6] <= '9') key = 48 + value[6] - '0';
                else if (value.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(value.Substring(1), out key) && key >= 1 && key <= 24) key += 111;
                else if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                { if (!int.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out key)) throw new FormatException("Invalid virtual-key code."); }
                else if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out key)) throw new FormatException("Unknown virtual-key name: " + value);
            }
            if (key < 8 || key > 254 || IsGamepad(key)) throw new FormatException("Only keyboard virtual keys may be configured.");
            return key;
        }
    }

    public sealed class CabinetRepeatGate
    {
        private sealed class Press { public long First, Last; }
        private readonly Dictionary<int, Press> held = new Dictionary<int, Press>();
        public bool Down(int token, CabinetAction action, bool wasAlreadyDown, long milliseconds, CabinetInputMap map)
        {
            Press press;
            if (!held.TryGetValue(token, out press))
            {
                held[token] = new Press { First = milliseconds, Last = milliseconds };
                // Do not launch/back on a held key inherited after return from another app.
                return !wasAlreadyDown;
            }
            if (!CabinetInputMap.Repeats(action) || milliseconds - press.First < map.RepeatDelayMs || milliseconds - press.Last < map.RepeatIntervalMs) return false;
            press.Last = milliseconds; return true;
        }
        public void Up(int token) { held.Remove(token); }
        public void Reset() { held.Clear(); }
    }

    public static class CabinetNavigation
    {
        public static int Step(int index, int delta, int count, bool wrap = false)
        {
            if (count <= 0) return -1;
            if (index < 0 || index >= count) return delta < 0 ? count - 1 : 0;
            long next = (long)index + delta;
            return wrap ? (int)((next % count + count) % count) : (int)Math.Max(0, Math.Min(count - 1, next));
        }
        public static int Letter(IReadOnlyList<string> titles, int index, int delta)
        {
            if (titles.Count == 0) return -1;
            Func<string, string> letter = title => string.IsNullOrWhiteSpace(title) ? "#" : title.TrimStart().Substring(0, 1).ToUpperInvariant();
            var letters = titles.Select(letter).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
            string current = letter(titles[Math.Max(0, Math.Min(titles.Count - 1, index))]);
            string next = letters[Step(letters.IndexOf(current), delta, letters.Count, true)];
            for (int i = 0; i < titles.Count; i++) if (letter(titles[i]) == next) return i;
            return index;
        }
    }
}
