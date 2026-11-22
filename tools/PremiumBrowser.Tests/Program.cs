using System;
using System.IO;
using System.Linq;
using LaunchPass.Catalog;

class Program
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    static void Bad(Action action, string name)
    {
        try { action(); } catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is System.Runtime.Serialization.SerializationException || ex is System.Xml.XmlException) { checks++; return; }
        throw new Exception("Accepted invalid data: " + name);
    }
    static void Main()
    {
        var entries = CatalogReader.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "example.json")));
        string[] protocols = entries.Select(e => e.Launch.ProtocolUri).ToArray();
        var state = new PremiumSelection(entries);
        Check(state.CategoryCount == 3 && state.Current.Id == "arcade-1941", "initial alphabetic category/game");
        state.MoveGame(-1); Check(state.SelectedIndex == 0, "clamp first");
        state.MoveGame(1); Check(state.Current.Id == "arcade-1942", "keyboard next");
        state.MoveGame(1); Check(state.Current.Id == "arcade-override", "controller next shares same selection");
        state.MoveGame(1); Check(state.SelectedIndex == 2, "clamp last");
        state.MoveCategory(1); Check(state.Current.Id == "genesis-example", "next category");
        state.MoveCategory(1); Check(state.Current.Id == "snes-example", "next category again");
        state.MoveCategory(1); Check(state.Current.Id == "arcade-override", "wrap and restore prior game");
        state.MoveCategory(-1); Check(state.Current.Id == "snes-example", "backward category wrap");
        Check(state.SelectGame("ARCADE-1942") && state.CategoryIndex == 0 && state.SelectedIndex == 1, "restore by stable ID/search result");
        Check(!state.SelectGame("missing") && state.Current.Id == "arcade-1942", "unknown ID leaves selection intact");
        string retained = state.Current.Id;
        var launch = state.Current.Launch;
        Check(state.Current.Id == retained && launch.ProtocolUri == protocols[1], "launch and resume retain same state and URI");
        for (int i = 0; i < 100; i++) { state.MoveGame(i % 2 == 0 ? 1 : -1); state.MoveCategory(i % 2 == 0 ? 1 : -1); Check(state.Current != null && state.SelectedIndex >= 0 && state.SelectedIndex < state.Games.Count, "rapid mixed device/category navigation"); }
        var empty = new PremiumSelection(new CatalogEntry[0]); empty.MoveGame(1); empty.MoveCategory(-1); empty.Select(42);
        Check(empty.Current == null && empty.SelectedIndex == -1 && empty.CategoryCount == 0, "empty state safe");
        var single = new PremiumSelection(entries.Take(1)); single.MoveCategory(1); single.MoveGame(1);
        Check(single.Current.Id == entries[0].Id && single.CategoryIndex == 0, "single game/category stable");
        string manyJson = "{\"version\":1,\"contentRoot\":\"U:\\\\roms\",\"platforms\":[{\"id\":\"arcade\",\"title\":\"Arcade\",\"defaultCore\":\"mame2003_plus_libretro.dll\"}],\"games\":[" + string.Join(",", Enumerable.Range(0, 100).Select(i => "{\"id\":\"game" + i + "\",\"title\":\"Game " + i + "\",\"platform\":\"arcade\",\"rom\":\"game" + i + ".zip\"}")) + "]}";
        var large = new PremiumSelection(CatalogReader.Parse(manyJson));
        for (int i = 0; i < 100; i++) { Check(large.SelectedIndex == i, "100-game sequential wheel selection"); large.MoveGame(1); }
        Check(large.SelectedIndex == 99, "100-game wheel clamps at end");
        Check(entries.Select(e => e.Launch.ProtocolUri).SequenceEqual(protocols), "presentation never mutates launch data");
        string unorderedJson = "{\"version\":1,\"contentRoot\":\"U:\\\\roms\",\"platforms\":[{\"id\":\"z\",\"title\":\"Zebra\",\"defaultCore\":\"mame.dll\"},{\"id\":\"a\",\"title\":\"Arcade\",\"defaultCore\":\"mame.dll\"}],\"games\":[{\"id\":\"zulu\",\"title\":\"Zulu\",\"platform\":\"z\",\"rom\":\"z.zip\"},{\"id\":\"beta\",\"title\":\"beta\",\"platform\":\"a\",\"rom\":\"b.zip\"},{\"id\":\"alpha-b\",\"title\":\"Alpha\",\"platform\":\"a\",\"rom\":\"ab.zip\"},{\"id\":\"alpha-a\",\"title\":\"alpha\",\"platform\":\"a\",\"rom\":\"aa.zip\"}]}";
        var unordered = CatalogReader.Parse(unorderedJson);
        string[] originalIds = unordered.Select(e => e.Id).ToArray();
        string[] originalUris = unordered.Select(e => e.Launch.ProtocolUri).ToArray();
        var sorted = CatalogPresentationOrder.Sort(unordered);
        Check(sorted.Select(e => e.Id).SequenceEqual(new[] { "alpha-a", "alpha-b", "beta", "zulu" }), "category/title order ignores case and uses stable ID ties");
        Check(CatalogPresentationOrder.Sort(unordered.Reverse()).Select(e => e.Id).SequenceEqual(sorted.Select(e => e.Id)), "JSON shuffle cannot change browsing order");
        var orderedWheel = new PremiumSelection(unordered);
        Check(orderedWheel.Current.Id == "alpha-a", "wheel starts at alphabetically first category and title");
        orderedWheel.MoveGame(1); Check(orderedWheel.Current.Id == "alpha-b", "next follows sorted title order");
        orderedWheel.MoveGame(1); Check(orderedWheel.Current.Id == "beta", "keyboard/controller next share alphabetical order");
        orderedWheel.MoveCategory(1); Check(orderedWheel.Current.Id == "zulu", "categories alphabetical");
        orderedWheel.MoveCategory(-1); Check(orderedWheel.Current.Id == "beta", "sorted category selection restored by ID");
        Check(unordered.Select(e => e.Id).SequenceEqual(originalIds) && unordered.Select(e => e.Launch.ProtocolUri).SequenceEqual(originalUris), "ordering never changes parsed catalog or launch requests");
        var logo = PremiumArtwork.Candidates(entries[0], "logo");
        Check(logo[0] == @"Artwork\games\arcade-1941\logo.png" && logo[3] == @"Artwork\platforms\arcade\logo.png", "game then platform logo priority");
        Check(logo.Count == 6 && !logo.Contains(entries[0].Artwork), "legacy artwork is not mistaken for transparent logo");
        var screenshot = PremiumArtwork.Candidates(entries[0], "screenshot");
        Check(screenshot.Count == 4 && screenshot[3] == entries[0].Artwork, "legacy artwork screenshot fallback");
        Check(PremiumArtwork.Candidates(entries[1], "screenshot").Count == 3, "missing screenshot uses presentation fallback");
        Check(PremiumArtwork.Candidates(entries[0], "background")[5] == @"Artwork\platforms\arcade\background.jpeg", "background extensions and category fallback");
        Check(PremiumArtwork.Folder("CON") == PremiumArtwork.Folder("con") && PremiumArtwork.Folder("CON").StartsWith("~"), "Windows device name encoding case-stable");
        Check(PremiumArtwork.Folder("arcade-1941") == "arcade-1941", "stable readable directory");
        foreach (string id in new[] { "../escape", @"..\escape", @"C:\outside", "/root", "bad:name", "a.b", "", "~636F6E" }) Bad(() => PremiumArtwork.Folder(id), "unsafe ID " + id);
        Bad(() => PremiumArtwork.Candidates(entries[0], "../video"), "unsupported asset role");
        foreach (var entry in entries) foreach (var role in new[] { "logo", "background", "screenshot" }) foreach (string path in PremiumArtwork.Candidates(entry, role)) Check(WindowsPaths.Relative(path) == path && !WindowsPaths.IsAbsolute(path), "local relative path");
        var meta = PremiumMetadataDocument.Parse("{\"version\":1,\"games\":[{\"id\":\"arcade-1941\",\"year\":\"1990\",\"manufacturer\":\"Capcom\",\"genre\":\"Shooter\"}]}");
        Check(meta["ARCADE-1941"].Year == "1990" && meta["arcade-1941"].Manufacturer == "Capcom", "optional sidecar metadata");
        Check(PremiumMetadataDocument.Parse("{\"version\":1,\"games\":[{\"id\":\"arcade-1941\"}]}")["arcade-1941"].Year == null, "metadata fields optional");
        Check(PremiumMetadataDocument.Parse("{\"version\":1,\"games\":[]}").Count == 0, "empty sidecar allowed");
        foreach (string json in new[] { "{}", "null", "{\"version\":2,\"games\":[]}", "{\"version\":1,\"games\":[null]}", "{\"version\":1,\"games\":[{\"id\":\"../bad\"}]}", "{\"version\":1,\"games\":[{\"id\":\"a\"},{\"id\":\"A\"}]}", "{\"version\":1,\"games\":[]} {}" }) Bad(() => PremiumMetadataDocument.Parse(json), "invalid metadata: " + json);
        Console.WriteLine("PASS: " + checks + " premium browser assertions");
    }
}
