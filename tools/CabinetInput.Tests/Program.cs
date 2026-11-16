using System;
using System.IO;
using LaunchPass.Catalog;

class Program
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    static void Bad(string json) { try { CabinetInputMap.Parse(json); } catch (FormatException) { checks++; return; } throw new Exception("Accepted bad configuration: " + json); }
    static CabinetAction Resolve(CabinetInputMap map, int key)
    {
        int token; CabinetAction action;
        if (!map.TryResolve(key, out token, out action)) throw new Exception("Missing key " + key);
        checks++; return action;
    }
    static void Main()
    {
        var map = CabinetInputMap.Defaults();
        Check(Resolve(map, 38) == CabinetAction.PreviousGame, "observed Up 38");
        Check(Resolve(map, 40) == CabinetAction.NextGame, "observed Down 40");
        Check(Resolve(map, 37) == CabinetAction.PreviousCategory, "observed Left 37");
        Check(Resolve(map, 39) == CabinetAction.NextCategory, "observed Right 39");
        Check(Resolve(map, 13) == CabinetAction.Select && Resolve(map, 27) == CabinetAction.Back, "keyboard fallbacks");
        Check(Resolve(map, 89) == CabinetAction.Search, "preserve keyboard search");
        int token; CabinetAction action;
        foreach (int unconfirmed in new[] { 17, 18, 16, 32, 49, 50, 70, 82, 71, 68, 88, 90, 67, 104 })
            Check(!map.TryResolve(unconfirmed, out token, out action), "do not guess cabinet identity " + unconfirmed);
        var candidate = CabinetInputMap.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "candidate.json")));
        Check(Resolve(candidate, 17) == CabinetAction.Select && Resolve(candidate, 162) == CabinetAction.Select, "generic/left control");
        Check(Resolve(candidate, 16) == CabinetAction.Back && Resolve(candidate, 161) == CabinetAction.Back, "shift aliases");
        Check(Resolve(candidate, 18) == CabinetAction.PreviousLetter && Resolve(candidate, 164) == CabinetAction.PreviousLetter, "Alt aliases");
        Check(Resolve(candidate, 88) == CabinetAction.AddFavorite && Resolve(candidate, 32) == CabinetAction.Favorites, "reserved favorite actions");
        var exact = CabinetInputMap.Parse("{\"version\":1,\"bindings\":[{\"key\":\"Control\",\"action\":\"Select\"},{\"key\":\"RightControl\",\"action\":\"Back\"}]}");
        Check(Resolve(exact, 163) == CabinetAction.Back && Resolve(exact, 162) == CabinetAction.Select, "specific modifier override");
        for (int key = 195; key <= 218; key++) Check(!candidate.TryResolve(key, out token, out action), "gamepad pass-through " + key);
        Check(CabinetInputMap.KeyCode("Number1") == 49 && CabinetInputMap.KeyCode("NumberPad1") == 97, "top-row vs keypad");
        Check(CabinetInputMap.KeyCode("0x11") == 17 && CabinetInputMap.KeyCode("17") == 17 && CabinetInputMap.KeyCode("f") == 70 && CabinetInputMap.KeyCode("F12") == 123, "key formats");
        var disabled = CabinetInputMap.Parse("{\"version\":1,\"enabled\":false,\"bindings\":[{\"key\":\"Up\",\"action\":\"PreviousGame\"}]}");
        Check(!disabled.TryResolve(38, out token, out action), "disable policy");
        CabinetInputMap.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "defaults.json")));
        foreach (string bad in new[] { "", "{}", "null", "{\"version\":2,\"bindings\":[]}", "{\"version\":1,\"bindings\":null}", "{\"version\":1,\"bindings\":[null]}",
            "{\"version\":1,\"repeatDelayMs\":0,\"bindings\":[]}", "{\"version\":1,\"repeatIntervalMs\":10,\"bindings\":[]}",
            "{\"version\":1,\"bindings\":[{\"key\":\"Up\",\"action\":\"LaunchAnything\"}]}",
            "{\"version\":1,\"bindings\":[{\"key\":\"195\",\"action\":\"Select\"}]}",
            "{\"version\":1,\"bindings\":[{\"key\":\"Up\",\"action\":\"0\"}]}",
            "{\"version\":1,\"bindings\":[{\"key\":\"17\",\"action\":\"Select\"},{\"key\":\"Control\",\"action\":\"Back\"}]}",
            "{\"version\":1,\"bindings\":[{\"key\":\"Up\"}]}", "{\"version\":1,\"bindings\":[]} {}" }) Bad(bad);
        var gate = new CabinetRepeatGate();
        Check(gate.Down(38, CabinetAction.PreviousGame, false, 0, map), "initial move");
        Check(!gate.Down(38, CabinetAction.PreviousGame, false, 1, map), "duplicate delivery");
        Check(!gate.Down(38, CabinetAction.PreviousGame, true, 349, map), "initial repeat delay");
        Check(gate.Down(38, CabinetAction.PreviousGame, true, 350, map), "direction repeat");
        Check(!gate.Down(38, CabinetAction.PreviousGame, true, 489, map), "repeat interval");
        Check(gate.Down(38, CabinetAction.PreviousGame, true, 490, map), "next direction repeat");
        gate.Up(38);
        Check(gate.Down(38, CabinetAction.PreviousGame, false, 491, map), "fresh press immediately after release");
        Check(gate.Down(13, CabinetAction.Select, false, 500, map), "initial select");
        Check(!gate.Down(13, CabinetAction.Select, false, 501, map) && !gate.Down(13, CabinetAction.Select, true, 5000, map), "select never repeats across target changes");
        gate.Reset();
        Check(!gate.Down(13, CabinetAction.Select, true, 6000, map), "held select after RetroArch return");
        gate.Up(13); Check(gate.Down(13, CabinetAction.Select, false, 6001, map), "rearm select after release");
        Check(gate.Down(27, CabinetAction.Back, false, 7000, map) && !gate.Down(27, CabinetAction.Back, true, 8000, map), "Back once per press");
        Check(CabinetNavigation.Step(0, -1, 0) == -1, "empty library");
        Check(CabinetNavigation.Step(0, -1, 1) == 0 && CabinetNavigation.Step(0, 1, 1) == 0, "single game");
        Check(CabinetNavigation.Step(0, -1, 25) == 0 && CabinetNavigation.Step(24, 1, 25) == 24, "list boundaries");
        for (int i = 0; i < 24; i++) Check(CabinetNavigation.Step(i, 1, 25) == i + 1, "sequential games across grid rows");
        Check(CabinetNavigation.Step(0, -1, 3, true) == 2 && CabinetNavigation.Step(2, 1, 3, true) == 0, "category wrap");
        Check(CabinetNavigation.Step(-1, 1, 4) == 0 && CabinetNavigation.Step(-1, -1, 4) == 3, "missing selection");
        string[] titles = { "Zelda", "1941", "alpha", "Beta", "1942", "Another" };
        Check(CabinetNavigation.Letter(titles, 2, 1) == 3 && CabinetNavigation.Letter(titles, 3, -1) == 2, "alphabet groups without reordering catalog");
        Check(CabinetNavigation.Letter(titles, 0, 1) == 1 && CabinetNavigation.Letter(Array.Empty<string>(), 0, 1) == -1, "alphabet wrap/empty");
        Console.WriteLine("PASS: " + checks + " cabinet mapping/repeat/navigation assertions. UWP focus and Xbox hardware require device checks.");
    }
}
