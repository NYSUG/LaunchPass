using System;
using RetroPass;

// Only the UWP-bound model dependencies are substituted. Generator and Platform are linked production sources.
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

internal static class Program
{
    private static int assertions;
    private static void Check(bool result, string description)
    {
        if (!result) throw new Exception(description);
        assertions++;
    }

    private static Game Game(string path, string core) => new Game
    {
        ApplicationPathFull = path, CoreName = core,
        GamePlatform = new Platform { EmulatorType = Platform.EEmulatorType.retroarch }
    };

    // Model form-query decoding, including '+' semantics, independently of the generator.
    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace("+", " "));

    private static int Main()
    {
        try
        {
            string[] paths = {
                @"Q:\Synthetic\LocalState\roms\arcade\1941.zip",
                @"D:\path with spaces\1941.zip",
                @"C:\A&B\1941.zip", @"C:\plus+sign\1941.zip",
                @"C:\100%\1941.zip", @"C:\literal%20\1941.zip",
                @"C:\hash#fragment\1941.zip", @"C:\equals=semicolon;\1941.zip",
                @"C:\Unicode 日本語 café\1941.zip", @"C:\apostrophe's (parentheses)\1941.zip",
                @"C:\emoji 🎮\1941.zip", @"\\synthetic-server\share\1941.zip"
            };
            foreach (string core in new[] { "mame_libretro.dll", "mame2003_plus_libretro.dll", "CORE-1.2.DLL" })
            foreach (string path in paths)
            {
                string raw = UrlSchemeGenerator.GetUrl(Game(path, core));
                Check(raw.StartsWith("retroarch:?cmd=retroarch -L cores\\" + core + " \""), "CLI/core prefix");
                var uri = new Uri(raw);
                Check(uri.Scheme == "retroarch", "RetroArch scheme");
                Check(uri.Fragment == "", "ROM hash must not become a URI fragment");
                string[] query = uri.Query.Substring(1).Split('&');
                Check(query.Length == 2, "ROM ampersand must not add query parameters");
                Check(Decode(query[0]) == "cmd=retroarch -L cores\\" + core + " \"" + path + "\"", "Decoded command/path exact round trip");
                Check(Decode(query[1]) == "launchOnExit=LaunchPass:", "Return protocol");
                Check(!raw.Contains("%252520"), "No unexpected repeated percent encoding");
            }
            string known = UrlSchemeGenerator.GetUrl(Game(@"D:\A & B+%#\1941.zip", "mame_libretro.dll"));
            Check(known.Contains("D%3A%5CA%20%26%20B%2B%25%23%5C1941.zip"), "Known escape sequence");

            // Characterize existing limitations; do not silently fix application behavior in tooling work.
            string badCore = UrlSchemeGenerator.GetUrl(Game(paths[0], "unsafe&extra=value.dll"));
            Check(new Uri(badCore).Query.Split('&').Length == 3, "Existing generator leaves core unescaped; callers must validate");
            Check(UrlSchemeGenerator.GetUrl(Game(paths[0], "")).Contains("cores\\ \""), "Generator does not validate empty core");
            var platform = new Platform();
            platform.SetEmulatorType(@"C:\Emulators\RetroArch\retroarch.exe");
            Check(platform.EmulatorType == Platform.EEmulatorType.retroarch, "Windows emulator path selection");
            Console.WriteLine("PASS: " + assertions + " assertions against production URI generator/Platform.");
            Console.WriteLine("Synthetic paths only. No UWP activation, receiver decoding, or game loading was tested.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error.Message);
            return 1;
        }
    }
}
