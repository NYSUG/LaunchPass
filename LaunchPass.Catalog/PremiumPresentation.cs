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
    // Presentation order is independent of the JSON and never changes launch requests.
    public static class CatalogPresentationOrder
    {
        public static IReadOnlyList<CatalogEntry> Sort(IEnumerable<CatalogEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            return entries.OrderBy(e => e.PlatformTitle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.PlatformId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
        }
    }

    // Presentation-only state. Catalog data and launch requests are never rewritten.
    public sealed class PremiumSelection
    {
        private readonly List<List<CatalogEntry>> categories;
        private readonly Dictionary<string, string> selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public int CategoryIndex { get; private set; }
        public int CategoryCount => categories.Count;
        public IReadOnlyList<CatalogEntry> Games => categories.Count == 0 ? (IReadOnlyList<CatalogEntry>)new CatalogEntry[0] : categories[CategoryIndex];
        public int SelectedIndex { get; private set; } = -1;
        public CatalogEntry Current => SelectedIndex < 0 ? null : Games[SelectedIndex];
        public PremiumSelection(IEnumerable<CatalogEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            categories = CatalogPresentationOrder.Sort(entries).GroupBy(e => e.PlatformId, StringComparer.OrdinalIgnoreCase).Select(g => g.ToList()).ToList();
            if (categories.Count > 0) Select(0);
        }
        public void Select(int index)
        {
            SelectedIndex = CabinetNavigation.Step(index, 0, Games.Count);
            if (Current != null) selected[Current.PlatformId] = Current.Id;
        }
        public void MoveGame(int delta) => Select(CabinetNavigation.Step(SelectedIndex, delta, Games.Count));
        public void MoveCategory(int delta)
        {
            if (categories.Count < 2) return;
            CategoryIndex = CabinetNavigation.Step(CategoryIndex, delta, categories.Count, true);
            string id;
            int index = selected.TryGetValue(Games[0].PlatformId, out id) ? categories[CategoryIndex].FindIndex(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) : 0;
            Select(index);
        }
        public bool SelectGame(string id)
        {
            for (int c = 0; c < categories.Count; c++)
            {
                int index = categories[c].FindIndex(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
                if (index < 0) continue;
                CategoryIndex = c; Select(index); return true;
            }
            return false;
        }
    }

    // Keep the cover after URI acceptance; dismiss only on return, failure or a
    // foreground timeout when no departure ever occurred.
    public sealed class PremiumLaunchCover
    {
        public bool Active { get; private set; }
        public bool Departed { get; private set; }
        private double started;
        public void Begin(double now) { Active = true; Departed = false; started = now; }
        public void Leave() { if (Active) Departed = true; }
        public bool Return() { if (!Active || !Departed) return false; Cancel(); return true; }
        public void ActivationCompleted(bool accepted) { if (!accepted) Cancel(); }
        public bool Expire(double now, bool foreground)
        {
            if (!Active || Departed || !foreground || now - started < 10000) return false;
            Cancel(); return true;
        }
        public void Cancel() { Active = Departed = false; }
    }

    public static class PremiumArtwork
    {
        // Catalog IDs already allow only letters/digits/_/-. Encode Windows device names.
        public static string Folder(string id)
        {
            if (id == null || !Regex.IsMatch(id, @"\A[a-zA-Z0-9_-]+\z")) throw new FormatException("Invalid artwork ID.");
            return Regex.IsMatch(id, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase) ? "~" + BitConverter.ToString(Encoding.UTF8.GetBytes(id.ToLowerInvariant())).Replace("-", "") : id;
        }
        public static IReadOnlyList<string> Candidates(CatalogEntry entry, string role)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (role != "logo" && role != "background" && role != "screenshot") throw new ArgumentException("Unknown artwork role.", nameof(role));
            var paths = new List<string>();
            Add(paths, "Artwork\\games\\" + Folder(entry.Id), role);
            if (role == "screenshot" && entry.Artwork != null) paths.Add(WindowsPaths.Relative(entry.Artwork));
            if (role != "screenshot") Add(paths, "Artwork\\platforms\\" + Folder(entry.PlatformId), role);
            return paths.AsReadOnly();
        }
        private static void Add(List<string> paths, string directory, string role)
        {
            foreach (string extension in new[] { ".png", ".jpg", ".jpeg" }) paths.Add(directory + "\\" + role + extension);
        }
    }

    [DataContract]
    public sealed class PremiumMetadata
    {
        [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; }
        [DataMember(Name = "year")] public string Year { get; set; }
        [DataMember(Name = "manufacturer")] public string Manufacturer { get; set; }
        [DataMember(Name = "genre")] public string Genre { get; set; }
    }
    [DataContract]
    public sealed class PremiumMetadataDocument
    {
        [DataMember(Name = "version", IsRequired = true)] public int Version { get; set; }
        [DataMember(Name = "games", IsRequired = true)] public List<PremiumMetadata> Games { get; set; }
        public static IReadOnlyDictionary<string, PremiumMetadata> Parse(string json)
        {
            // The framework JSON reader can stop at the first root value; reject extra roots.
            string text = (json ?? "").Trim();
            bool quoted = false, escaped = false;
            int depth = 0;
            if (text.Length == 0 || text[0] != '{') throw new FormatException("Presentation must be a JSON object.");
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') quoted = false;
                }
                else if (ch == '"') quoted = true;
                else if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0 && i != text.Length - 1) throw new FormatException("Trailing presentation data.");
            }
            PremiumMetadataDocument doc;
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json ?? ""), System.Xml.XmlDictionaryReaderQuotas.Max))
            {
                doc = (PremiumMetadataDocument)new DataContractJsonSerializer(typeof(PremiumMetadataDocument)).ReadObject(reader);
                if (reader.MoveToContent() != System.Xml.XmlNodeType.None) throw new FormatException("Trailing presentation data.");
            }
            if (doc == null || doc.Version != 1 || doc.Games == null) throw new FormatException("Presentation version must be 1 with a games array.");
            var result = new Dictionary<string, PremiumMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in doc.Games)
            {
                if (game == null) throw new FormatException("Null presentation game.");
                PremiumArtwork.Folder(game.Id);
                if (result.ContainsKey(game.Id)) throw new FormatException("Duplicate presentation ID.");
                result.Add(game.Id, game);
            }
            return result;
        }
    }
}
