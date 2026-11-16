using System;
using System.IO;
using System.Linq;
using LaunchPass.Catalog;
using RetroPass;
namespace Windows.Storage { public class StorageFolder { } }
namespace RetroPass
{
    public class Game
    {
        public Platform GamePlatform { get; set; }
        public string CoreName { get; set; }
        public string ApplicationPathFull { get; set; }
    }
}
class Program
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    static void Bad(Action action, string name)
    {
        try { action(); } catch (FormatException) { checks++; return; }
        throw new Exception("Accepted invalid input: " + name);
    }
    static void Main()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "example.json"));
        var games = CatalogReader.Parse(json);
        Check(games.Count == 5, "all platforms/games");
        Check(games[0].Launch.Core == games[1].Launch.Core, "arcade default");
        Check(games[2].Launch.Core == "fbneo_libretro.dll", "override");
        Check(games[3].Launch.Core == "genesis_plus_gx_libretro.dll", "genesis");
        Check(games[4].Launch.Core == "snes9x_libretro.dll", "snes");
        Check(games[0].Launch.RomPath.EndsWith(@"\roms\arcade\1941.zip"), "known probe path suffix");
        Check(games[1].Artwork == null, "optional artwork");
        Check(games[0].Artwork == @"Artwork\1941.png", "relative artwork");
        Check(CatalogReader.Parse(json.Replace("\"title\": \"1941\"", "\"title\": \"Renamed\""))[0].Id == games[0].Id, "stable identity after rename");
        foreach (string path in new[] { @"D:\space name\A&B+%#=.zip", @"U:\日本語\🎮.zip", @"C:\literal%20\game.zip" })
        {
            string input = json.Replace("arcade/1941.zip", path.Replace("\\", "\\\\"));
            var request = CatalogReader.Parse(input)[0].Launch;
            Check(request.RomPath == path, "absolute override");
            var model = new Game { ApplicationPathFull = path, CoreName = request.Core, GamePlatform = new Platform() };
            Check(request.ProtocolUri == UrlSchemeGenerator.GetUrl(model), "exact production format parity");
            var uri = new Uri(request.ProtocolUri);
            var query = uri.Query.Substring(1).Split('&');
            Check(query.Length == 2 && uri.Fragment == "", "query isolation");
            Check(Uri.UnescapeDataString(query[0].Replace("+", " ")) == "cmd=retroarch -L cores\\" + request.Core + " \"" + path + "\"", "URI decoded command");
            Check(query[1] == "launchOnExit=LaunchPass:", "return protocol");
        }
        Check(WindowsPaths.Resolve(@"U:\roms\", "arcade/1941.zip") == @"U:\roms\arcade\1941.zip", "host-independent combination");
        Check(WindowsPaths.Resolve(@"C:\", "game.zip") == @"C:\game.zip", "drive root");
        foreach (string path in new[] { "../escape.zip", @"C:relative.zip", @"\root.zip", @"\\server\share\game.zip", "x/./rom.zip", "x//rom.zip", "x/rom.zip ", "x/rom.zip.", "x/\"rom.zip", "x/rom\n.zip", "" })
            Bad(() => WindowsPaths.Resolve(@"U:\roms", path), path);
        foreach (string invalid in new[] { "", "{", "null", "{}", json + " garbage", json + " {}", json.Replace("\"version\": 1,", ""), json.Replace("\"title\": \"1941\",", ""), json.Replace("\"rom\": \"arcade/1941.zip\"", "\"rom\": null"), json.Replace("\"contentRoot\":", "\"unusedRoot\":"), json.Replace("\"core\": \"fbneo_libretro.dll\"", "\"core\": \"\""), json.Replace("\"version\": 1", "\"version\": 2"), json.Replace("arcade-1942", "arcade-1941"), json.Replace("\"id\": \"genesis\"", "\"id\": \"arcade\""), json.Replace("\"platform\": \"snes\"", "\"platform\": \"unknown\""), json.Replace("fbneo_libretro.dll", "bad&arg=1.dll"), json.Replace("Artwork/1941.png", "../1941.png"), json.Replace("\"games\": [", "\"games\": [null,") })
            Bad(() => CatalogReader.Parse(invalid), "malformed catalog: " + invalid);
        Console.WriteLine("PASS: " + checks + " portable catalog assertions; UWP/artwork IO/activation require Windows/Xbox.");
    }
}
