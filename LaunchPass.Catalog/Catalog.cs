using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace LaunchPass.Catalog
{
    [DataContract]
    public sealed class CatalogDocument
    {
        [DataMember(Name = "version", IsRequired = true)] public int Version { get; set; }
        [DataMember(Name = "contentRoot", IsRequired = true)] public string ContentRoot { get; set; }
        [DataMember(Name = "platforms", IsRequired = true)] public List<CatalogPlatform> Platforms { get; set; }
        [DataMember(Name = "games", IsRequired = true)] public List<CatalogGame> Games { get; set; }
    }
    [DataContract]
    public sealed class CatalogPlatform
    {
        [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; }
        [DataMember(Name = "title", IsRequired = true)] public string Title { get; set; }
        [DataMember(Name = "defaultCore", IsRequired = true)] public string DefaultCore { get; set; }
    }
    [DataContract]
    public sealed class CatalogGame
    {
        [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; }
        [DataMember(Name = "platform", IsRequired = true)] public string Platform { get; set; }
        [DataMember(Name = "title", IsRequired = true)] public string Title { get; set; }
        [DataMember(Name = "rom", IsRequired = true)] public string Rom { get; set; }
        [DataMember(Name = "core")] public string Core { get; set; }
        [DataMember(Name = "artwork")] public string Artwork { get; set; }
    }
    public sealed class LaunchRequest
    {
        public string GameId { get; private set; }
        public string RomPath { get; private set; }
        public string Core { get; private set; }
        public string ProtocolUri { get; private set; }
        internal LaunchRequest(string id, string path, string core)
        {
            GameId = id; RomPath = path; Core = core;
            // Preserve the hardware-validated InternalStorageProbe wire format exactly.
            ProtocolUri = "retroarch:?cmd=retroarch -L cores\\" + core + " \"" +
                Uri.EscapeDataString(path) + "\"&launchOnExit=LaunchPass:";
        }
    }
    public sealed class CatalogEntry
    {
        public string Id { get; private set; }
        public string Title { get; private set; }
        public string PlatformId { get; private set; }
        public string PlatformTitle { get; private set; }
        public string Artwork { get; private set; }
        public LaunchRequest Launch { get; private set; }
        internal CatalogEntry(CatalogGame game, CatalogPlatform platform, string root)
        {
            Id = game.Id; Title = game.Title; PlatformId = platform.Id;
            PlatformTitle = platform.Title; Artwork = game.Artwork;
            string rom = WindowsPaths.Resolve(root, game.Rom);
            if (rom.Length == 3) throw new FormatException("ROM must name a file, not a drive root.");
            Launch = new LaunchRequest(Id, rom, game.Core ?? platform.DefaultCore);
        }
    }
    public static class WindowsPaths
    {
        // Deliberately independent of the host OS's System.IO.Path implementation.
        public static bool IsAbsolute(string value) => value != null && Regex.IsMatch(value, @"\A[A-Za-z]:\\");
        public static string Relative(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new FormatException("A relative path is required.");
            value = value.Replace('/', '\\');
            foreach (var segment in value.Split('\\'))
            {
                if (string.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".." ||
                    segment.EndsWith(".") || segment.EndsWith(" ") ||
                    Regex.IsMatch(segment, "[<>:\"|?*\\p{Cc}]"))
                    throw new FormatException("Invalid Windows path segment: " + segment);
            }
            return value;
        }
        public static string Absolute(string value)
        {
            value = value == null ? null : value.Replace('/', '\\');
            if (!IsAbsolute(value)) throw new FormatException("A fully qualified Windows drive path is required.");
            return value.Length == 3 ? value : value.Substring(0, 3) + Relative(value.Substring(3));
        }
        internal static string Root(string root)
        {
            if (root == null) throw new FormatException("Content root is required.");
            root = root.Replace('/', '\\');
            return root.Length == 3 ? root : root.TrimEnd('\\');
        }
        public static string Resolve(string root, string rom)
        {
            if (rom != null && IsAbsolute(rom.Replace('/', '\\'))) return Absolute(rom);
            return Absolute(Root(root)).TrimEnd('\\') + "\\" + Relative(rom);
        }
    }
    public static class CatalogReader
    {
        private static void Text(string text, string field)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl))
                throw new FormatException(field + " must contain nonempty text without control characters.");
        }
        private static void Id(string id)
        {
            if (id == null || !Regex.IsMatch(id, @"\A[a-zA-Z0-9_-]+\z"))
                throw new FormatException("IDs must use letters, numbers, underscores or hyphens.");
        }
        private static void Core(string core)
        {
            if (core == null || !Regex.IsMatch(core, @"\A[A-Za-z0-9_-][A-Za-z0-9_.-]*\.dll\z", RegexOptions.IgnoreCase))
                throw new FormatException("Core must be a DLL filename without directories or arguments.");
        }
        public static IReadOnlyList<CatalogEntry> Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Catalog is empty.");
            // The framework JSON reader can stop after one object and ignore a second one.
            // Reject trailing content explicitly, without relying on stream buffering positions.
            string trimmed = json.Trim();
            if (trimmed[0] != '{') throw new FormatException("Catalog must be a JSON object.");
            int depth = 0;
            bool inString = false, escaped = false;
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
                    if (depth == 0 && i != trimmed.Length - 1)
                        throw new FormatException("Unexpected content after the catalog document.");
                }
            }
            CatalogDocument doc;
            try
            {
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), System.Xml.XmlDictionaryReaderQuotas.Max))
                {
                    doc = (CatalogDocument)new DataContractJsonSerializer(typeof(CatalogDocument)).ReadObject(reader);
                    if (reader.MoveToContent() != System.Xml.XmlNodeType.None)
                        throw new FormatException("Unexpected content after the catalog document.");
                }
            }
            catch (Exception ex) when (ex is SerializationException || ex is System.Xml.XmlException)
            { throw new FormatException("Catalog is not valid JSON or is missing required fields.", ex); }
            if (doc == null || doc.Version != 1) throw new FormatException("Catalog version must be 1.");
            WindowsPaths.Absolute(WindowsPaths.Root(doc.ContentRoot));
            if (doc.Platforms == null || doc.Games == null || doc.Platforms.Count == 0 || doc.Games.Count == 0)
                throw new FormatException("Catalog needs platforms and games.");
            var platforms = new Dictionary<string, CatalogPlatform>(StringComparer.OrdinalIgnoreCase);
            foreach (var platform in doc.Platforms)
            {
                if (platform == null) throw new FormatException("Null platform.");
                Id(platform.Id); Text(platform.Title, "Platform title"); Core(platform.DefaultCore);
                if (platforms.ContainsKey(platform.Id)) throw new FormatException("Duplicate platform ID: " + platform.Id);
                platforms.Add(platform.Id, platform);
            }
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<CatalogEntry>();
            foreach (var game in doc.Games)
            {
                if (game == null) throw new FormatException("Null game.");
                Id(game.Id); Text(game.Title, "Game title"); Id(game.Platform);
                if (!ids.Add(game.Id)) throw new FormatException("Duplicate game ID: " + game.Id);
                if (!platforms.ContainsKey(game.Platform)) throw new FormatException("Unknown platform: " + game.Platform);
                if (game.Core != null) Core(game.Core);
                if (game.Artwork != null) game.Artwork = WindowsPaths.Relative(game.Artwork);
                result.Add(new CatalogEntry(game, platforms[game.Platform], doc.ContentRoot));
            }
            return result.AsReadOnly();
        }
    }
}
