using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LaunchPass.Catalog
{
    public sealed class CoreSettingsVersionException : FormatException
    { public CoreSettingsVersionException() : base("Unsupported settings version; this file will not be overwritten.") { } }

    [DataContract]
    public sealed class CoreSettingsHeader
    {
        [DataMember(Name = "version", IsRequired = true)] public int Version { get; set; }
    }

    public static class CoreSettingsJson
    {
        public const int MaximumBytes = 1024 * 1024;
        private static DataContractJsonSerializer Serializer(Type type) => new DataContractJsonSerializer(type,
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true, MaxItemsInObjectGraph = 60000 });
        public static T Read<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
                throw new FormatException("Missing or oversized core settings JSON.");
            string trimmed = json.Trim();
            if (trimmed[0] != '{') throw new FormatException("Expected a settings object.");
            int depth = 0; bool quoted = false, escaped = false;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char ch = trimmed[i];
                if (quoted) { if (escaped) escaped = false; else if (ch == '\\') escaped = true; else if (ch == '"') quoted = false; }
                else if (ch == '"') quoted = true;
                else if (ch == '{' || ch == '[') { if (++depth > 32) throw new FormatException("Settings nesting exceeds 32."); }
                else if (ch == '}' || ch == ']') { if (--depth == 0 && i != trimmed.Length - 1) throw new FormatException("Trailing JSON data."); }
            }
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json))) return (T)Serializer(typeof(T)).ReadObject(stream);
            }
            catch (Exception ex) when (ex is SerializationException || ex is System.Xml.XmlException || ex is ArgumentException)
            { throw new FormatException("Invalid core settings JSON.", ex); }
        }
        public static string Write<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                Serializer(typeof(T)).WriteObject(stream, value);
                if (stream.Length > MaximumBytes) throw new FormatException("Core settings exceed 1 MiB.");
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }
    public static class GameCoreRules
    {
        public static string Id(string value)
        {
            if (value == null || !Regex.IsMatch(value, @"\A[A-Za-z0-9_-]+\z")) throw new FormatException("Invalid stable game ID.");
            return value;
        }
        public static string File(string value)
        {
            if (value == null || value.Length > 255 || !Regex.IsMatch(value, @"\A[A-Za-z0-9_-][A-Za-z0-9_.-]*\.dll\z", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(value, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\.", RegexOptions.IgnoreCase))
                throw new FormatException("Core must be a DLL basename, without paths or arguments.");
            return value;
        }
        public static string Resolve(GameOverrideDocument overrides, string id, string catalogCore, string platformCore)
        { return File(overrides.Get(Id(id)) ?? catalogCore ?? platformCore); }
    }
    [DataContract]
    public sealed class GameCoreOverride : IExtensibleDataObject
    {
        [DataMember(Name = "core")] public string Core { get; set; }
        public ExtensionDataObject ExtensionData { get; set; }
    }
    [DataContract]
    public sealed class GameOverrideDocument : IExtensibleDataObject
    {
        [DataMember(Name = "version", IsRequired = true)] public int Version { get; set; } = 1;
        [DataMember(Name = "overrides", IsRequired = true)] public Dictionary<string, GameCoreOverride> Overrides { get; set; } = new Dictionary<string, GameCoreOverride>(StringComparer.OrdinalIgnoreCase);
        public ExtensionDataObject ExtensionData { get; set; }
        public static GameOverrideDocument Parse(string json)
        {
            // Check the version before requiring v1 members: a valid future
            // schema may replace the overrides map entirely.
            if (CoreSettingsJson.Read<CoreSettingsHeader>(json).Version != 1) throw new CoreSettingsVersionException();
            var doc = CoreSettingsJson.Read<GameOverrideDocument>(json);
            if (doc.Version != 1) throw new CoreSettingsVersionException();
            if (doc.Overrides == null || doc.Overrides.Count > 5000) throw new FormatException("Invalid overrides map.");
            var map = new Dictionary<string, GameCoreOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in doc.Overrides)
            {
                GameCoreRules.Id(pair.Key);
                if (pair.Value == null || map.ContainsKey(pair.Key)) throw new FormatException("Null or duplicate stable game ID.");
                if (pair.Value.Core != null) GameCoreRules.File(pair.Value.Core);
                map.Add(pair.Key, pair.Value);
            }
            doc.Overrides = map; return doc;
        }
        public string Get(string id) { GameCoreOverride value; return Overrides.TryGetValue(id, out value) ? value.Core : null; }
        public void Set(string id, string file)
        {
            GameCoreRules.Id(id); if (file != null) GameCoreRules.File(file);
            GameCoreOverride value;
            if (!Overrides.TryGetValue(id, out value)) Overrides[id] = value = new GameCoreOverride();
            // Keep extension fields when resetting, rather than erasing a future record.
            value.Core = file;
        }
    }
    [DataContract]
    public sealed class RegisteredCore
    {
        [DataMember(Name = "file", IsRequired = true)] public string File { get; set; }
        [DataMember(Name = "name", IsRequired = true)] public string Name { get; set; }
        [DataMember(Name = "available")] public bool? Available { get; set; }
        public override string ToString() => Name + " — " + File;
    }
    [DataContract]
    public sealed class CoreRegistry
    {
        [DataMember(Name = "version", IsRequired = true)] public int Version { get; set; } = 1;
        [DataMember(Name = "source")] public string Source { get; set; }
        [DataMember(Name = "cores", IsRequired = true)] public List<RegisteredCore> Cores { get; set; } = new List<RegisteredCore>();
        public static CoreRegistry Parse(string json)
        {
            var registry = CoreSettingsJson.Read<CoreRegistry>(json);
            if (registry.Version != 1) throw new CoreSettingsVersionException();
            if (registry.Cores == null || registry.Cores.Count > 1000) throw new FormatException("Invalid core registry.");
            if (registry.Source?.Length > 512) throw new FormatException("Core registry source exceeds 512 characters.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var core in registry.Cores)
            {
                if (core == null || !names.Add(GameCoreRules.File(core.File)) || string.IsNullOrWhiteSpace(core.Name) || core.Name.Length > 160 || core.Name.Any(char.IsControl))
                    throw new FormatException("Invalid or duplicate registered core.");
            }
            return registry;
        }
        public bool Contains(string file) => Cores.Any(core => core.Available != false && string.Equals(core.File, file, StringComparison.OrdinalIgnoreCase));
        public void IncludeConfigured(IEnumerable<string> files)
        {
            foreach (string file in files.Distinct(StringComparer.OrdinalIgnoreCase))
                if (!Cores.Any(core => string.Equals(core.File, file, StringComparison.OrdinalIgnoreCase)))
                    Cores.Add(new RegisteredCore { File = GameCoreRules.File(file), Name = file.Replace("_libretro.dll", "").Replace('_', ' ') + " (catalog configured)" });
        }
    }
    public interface IGameSettingsFiles
    {
        Task<string> ReadAsync(string name);
        Task WriteNewAsync(string name, string data);
        Task ReplaceAsync(string name, string data, string expected);
    }
    public sealed class GameOverridePersistence
    {
        public const string FileName = "GameOverrides.json";
        private readonly IGameSettingsFiles files;
        public GameOverridePersistence(IGameSettingsFiles files) { this.files = files; }
        public async Task<GameOverrideDocument> LoadAsync()
        { var text = await files.ReadAsync(FileName); return text == null ? new GameOverrideDocument() : GameOverrideDocument.Parse(text); }
        public async Task SaveAsync(string id, string core)
        {
            GameCoreRules.Id(id); if (core != null) GameCoreRules.File(core);
            string previous = await files.ReadAsync(FileName);
            GameOverrideDocument doc;
            try { doc = previous == null ? new GameOverrideDocument() : GameOverrideDocument.Parse(previous); }
            catch (CoreSettingsVersionException) { throw; }
            catch (FormatException)
            {
                await files.WriteNewAsync("GameOverrides.corrupt." + Guid.NewGuid().ToString("N") + ".json", previous);
                doc = new GameOverrideDocument();
            }
            doc.Set(id, core);
            await files.ReplaceAsync(FileName, CoreSettingsJson.Write(doc), previous);
        }
    }
    [DataContract]
    public sealed class GameCoreAttempt
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "gameId")] public string GameId { get; set; }
        [DataMember(Name = "core")] public string Core { get; set; }
        [DataMember(Name = "at")] public string At { get; set; }
        [DataMember(Name = "handedOff")] public bool? HandedOff { get; set; }
        [DataMember(Name = "departedAt")] public string DepartedAt { get; set; }
        [DataMember(Name = "resumedAt")] public string ResumedAt { get; set; }
        [DataMember(Name = "resumeObservation")] public string ResumeObservation { get; set; }
        [DataMember(Name = "errorCode")] public string ErrorCode { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
    }
    [DataContract]
    public sealed class GameCoreHistory
    {
        [DataMember(Name = "version", IsRequired = true)] public int Version { get; set; } = 1;
        [DataMember(Name = "enabled")] public bool? Enabled { get; set; }
        [DataMember(Name = "pending")] public string Pending { get; set; }
        [DataMember(Name = "attempts", IsRequired = true)] public List<GameCoreAttempt> Attempts { get; set; } = new List<GameCoreAttempt>();
        public static GameCoreHistory Parse(string json)
        {
            var value = CoreSettingsJson.Read<GameCoreHistory>(json);
            if (value.Version != 1 || value.Attempts == null || value.Attempts.Count > 1000) throw new FormatException("Invalid history document.");
            foreach (var attempt in value.Attempts)
                if (attempt == null || string.IsNullOrWhiteSpace(attempt.Id) || attempt.Id.Length > 64 || attempt.Message?.Length > 400)
                    throw new FormatException("Invalid history attempt.");
                else { GameCoreRules.Id(attempt.GameId); GameCoreRules.File(attempt.Core); }
            return value;
        }
        public GameCoreAttempt Begin(string id, string core, string at)
        {
            if (Enabled == false) return null;
            var attempt = new GameCoreAttempt { Id = Guid.NewGuid().ToString("N"), GameId = GameCoreRules.Id(id), Core = GameCoreRules.File(core), At = at };
            Attempts.Add(attempt); Pending = attempt.Id;
            while (Attempts.Count(item => string.Equals(item.GameId, id, StringComparison.OrdinalIgnoreCase)) > 20)
                Attempts.Remove(Attempts.First(item => string.Equals(item.GameId, id, StringComparison.OrdinalIgnoreCase)));
            while (Attempts.Count > 1000) Attempts.RemoveAt(0);
            return attempt;
        }
        public void Handoff(string id, bool accepted, Exception error = null)
        {
            var attempt = Attempts.FirstOrDefault(item => item.Id == id); if (attempt == null) return;
            attempt.HandedOff = accepted;
            if (!accepted && Pending == id) Pending = null;
            if (error != null) { attempt.ErrorCode = "0x" + error.HResult.ToString("X8"); attempt.Message = error.Message.Length > 400 ? error.Message.Substring(0, 400) : error.Message; }
            else if (!accepted) attempt.Message = "LaunchUriAsync returned false; no gameplay outcome is known.";
        }
        public bool Observe(string at, bool departure, bool protocol)
        {
            var attempt = Attempts.FirstOrDefault(item => item.Id == Pending);
            if (Enabled == false || attempt == null || attempt.HandedOff == false) return false;
            if (departure) { if (attempt.DepartedAt != null) return false; attempt.DepartedAt = at; return true; }
            if (!protocol && attempt.DepartedAt == null) return false;
            attempt.ResumedAt = at; attempt.ResumeObservation = protocol ? "LaunchPass protocol callback observed" : "Window foreground after recorded departure";
            Pending = null; return true;
        }
    }
    public sealed class CoreMenuNavigation
    {
        public int Row { get; set; }
        public int CoreIndex { get; set; } = -1;
        public int HistoryIndex { get; set; }
        public bool Busy { get; set; }
        public bool CancelRequested { get; private set; }
        public void RequestCancel() { CancelRequested = true; }
        public void Move(CabinetAction action, int cores, int history)
        {
            if (Busy) return;
            if (action == CabinetAction.PreviousGame || action == CabinetAction.NextGame)
                Row = CabinetNavigation.Step(Row, action == CabinetAction.PreviousGame ? -1 : 1, 6);
            else if (action == CabinetAction.PreviousCategory || action == CabinetAction.NextCategory)
            {
                int delta = action == CabinetAction.PreviousCategory ? -1 : 1;
                if (Row == 4) HistoryIndex = CabinetNavigation.Step(HistoryIndex, delta, history, true);
                else CoreIndex = CabinetNavigation.Step(CoreIndex, delta, cores, true);
            }
        }
    }
}
