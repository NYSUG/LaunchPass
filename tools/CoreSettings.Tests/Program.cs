using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LaunchPass.Catalog;

class Program
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    static void Bad(Action action) { try { action(); } catch (FormatException) { checks++; return; } throw new Exception("Invalid settings accepted"); }
    sealed class Files : IGameSettingsFiles
    {
        public readonly Dictionary<string,string> Data = new Dictionary<string,string>();
        public bool FailCommit, ExternalEdit;
        public Task<string> ReadAsync(string name) => Task.FromResult(Data.TryGetValue(name, out var value) ? value : null);
        public Task WriteNewAsync(string name, string data) { if (Data.ContainsKey(name)) throw new Exception("clobber"); Data.Add(name, data); return Task.CompletedTask; }
        public Task ReplaceAsync(string name, string data, string expected)
        {
            if (ExternalEdit) Data[name] = "external";
            if ((Data.TryGetValue(name, out var actual) ? actual : null) != expected || FailCommit) throw new InvalidOperationException("commit rejected");
            Data[name] = data; return Task.CompletedTask;
        }
    }
    static async Task Main()
    {
        try { await Run(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    }
    static async Task Run()
    {
        var files = new Files(); var persistence = new GameOverridePersistence(files);
        var overrides = await persistence.LoadAsync();
        Check(GameCoreRules.Resolve(overrides,"game",null,"platform.dll") == "platform.dll", "platform default");
        Check(GameCoreRules.Resolve(overrides,"game","catalog.dll","platform.dll") == "catalog.dll", "catalog override");
        await persistence.SaveAsync("arcade-1941", "Exact_CASE_libretro.dll");
        overrides = await persistence.LoadAsync();
        Check(GameCoreRules.Resolve(overrides,"ARCADE-1941","catalog.dll","platform.dll") == "Exact_CASE_libretro.dll", "persistent override and stable case-insensitive ID");
        var restarted = new GameOverridePersistence(files);
        Check((await restarted.LoadAsync()).Get("arcade-1941") == "Exact_CASE_libretro.dll", "restart reload");
        var regenerated = CatalogReader.Parse("{\"version\":1,\"contentRoot\":\"D:\\\\DevelopmentFiles\\\\roms\\\\arcade\",\"platforms\":[{\"id\":\"arcade\",\"title\":\"Arcade\",\"defaultCore\":\"platform.dll\"}],\"games\":[{\"id\":\"arcade-1941\",\"title\":\"New title\",\"platform\":\"arcade\",\"rom\":\"1941.zip\",\"core\":\"catalog.dll\"}]}");
        var entry = regenerated[0];
        Check(GameCoreRules.Resolve(overrides,entry.Id,entry.CatalogCoreOverride,entry.PlatformDefaultCore) == "Exact_CASE_libretro.dll", "catalog regeneration, ROM-root and title changes preserve stable-ID override");
        var launch = entry.WithCore("Exact_CASE_libretro.dll");
        Check(launch.RomPath == entry.Launch.RomPath && launch.ProtocolUri.Contains("cores\\Exact_CASE_libretro.dll") && launch.ProtocolUri.EndsWith("&launchOnExit=LaunchPass:"), "test launch request preserves path and callback");
        Check(entry.Launch.Core == "catalog.dll", "override never mutates catalog launch request");
        await persistence.SaveAsync("arcade-1941", null);
        overrides = await persistence.LoadAsync();
        Check(GameCoreRules.Resolve(overrides,entry.Id,entry.CatalogCoreOverride,entry.PlatformDefaultCore) == "catalog.dll", "Reset falls through to catalog per-game core");
        Check(GameCoreRules.Resolve(overrides,entry.Id,null,entry.PlatformDefaultCore) == "platform.dll", "Reset falls through to platform core");
        files.Data[GameOverridePersistence.FileName] = "{\"version\":1,\"future\":{\"keep\":true},\"overrides\":{\"arcade-1941\":{\"core\":\"old.dll\",\"note\":\"keep me\"}}}";
        await persistence.SaveAsync("arcade-1941","new.dll");
        Check(files.Data[GameOverridePersistence.FileName].Contains("keep me") && files.Data[GameOverridePersistence.FileName].Contains("future"), "unknown root and per-game fields survive save");
        await persistence.SaveAsync("arcade-1941",null);
        Check(files.Data[GameOverridePersistence.FileName].Contains("keep me"), "unknown per-game fields survive Reset");
        string original = files.Data[GameOverridePersistence.FileName];
        files.FailCommit = true;
        try { await persistence.SaveAsync("arcade-1941","other.dll"); throw new Exception("accepted failed commit"); } catch(InvalidOperationException) { checks++; }
        Check(files.Data[GameOverridePersistence.FileName] == original, "failed replacement preserves prior JSON");
        files.FailCommit = false; files.ExternalEdit = true;
        try { await persistence.SaveAsync("arcade-1941","other.dll"); throw new Exception("ignored external edit"); } catch(InvalidOperationException) { checks++; }
        Check(files.Data[GameOverridePersistence.FileName] == "external", "concurrent edits not overwritten");
        files.ExternalEdit = false;
        await persistence.SaveAsync("arcade-1941","repaired.dll");
        Check(files.Data.Any(pair => pair.Key.StartsWith("GameOverrides.corrupt.") && pair.Value == "external"), "corrupt source backed up before explicit repair");
        Check((await persistence.LoadAsync()).Get("arcade-1941") == "repaired.dll", "cabinet Save repairs invalid JSON after backup");
        files.Data[GameOverridePersistence.FileName] = "{\"version\":2,\"overrides\":{}}";
        try { await persistence.SaveAsync("arcade-1941","a.dll"); throw new Exception("downgraded future version"); } catch(CoreSettingsVersionException) { checks++; }
        Check(files.Data[GameOverridePersistence.FileName].Contains("2"), "newer schema is never overwritten");
        files.Data[GameOverridePersistence.FileName] = "{\"version\":2,\"futureSchema\":[]}";
        try { await persistence.SaveAsync("arcade-1941","a.dll"); throw new Exception("downgraded future schema with new members"); } catch(CoreSettingsVersionException) { checks++; }
        Check(files.Data[GameOverridePersistence.FileName].Contains("futureSchema"), "future schema without v1 members remains untouched");
        foreach(string core in new[]{"", "../evil.dll", @"cores\evil.dll", "core.dll x", "NUL.dll", "core.zip", "a.dll&launchOnExit=evil"}) Bad(()=>GameCoreRules.File(core));
        foreach(string id in new[]{"../bad", "bad/name", "a.b", "", "bad id"}) Bad(()=>GameCoreRules.Id(id));
        foreach(string raw in new[]{"[]","{}","{\"version\":1,\"overrides\":null}","{\"version\":1,\"overrides\":{\"a\":null}}","{\"version\":1,\"overrides\":{\"a\":{\"core\":\"x.dll\"},\"A\":{\"core\":\"y.dll\"}}}","{\"version\":1,\"overrides\":{}} {}"}) Bad(()=>GameOverrideDocument.Parse(raw));
        var registry = CoreRegistry.Parse("{\"version\":1,\"source\":\"explicit package\",\"cores\":[{\"file\":\"new.dll\",\"name\":\"New core\"},{\"file\":\"removed.dll\",\"name\":\"Removed\",\"available\":false}]}");
        registry.IncludeConfigured(new[]{"catalog.dll","removed.dll"});
        Check(registry.Contains("new.dll") && registry.Contains("catalog.dll") && !registry.Contains("removed.dll"), "registry config fallback preserves explicit unavailability");
        Check(!registry.Contains("unlisted.dll"), "no universal or unconfigured cores exposed");
        Bad(()=>CoreRegistry.Parse("{\"version\":1,\"cores\":[{\"file\":\"a.dll\",\"name\":\"A\"},{\"file\":\"A.dll\",\"name\":\"duplicate\"}]}"));
        var history = new GameCoreHistory(); var attempt = history.Begin("arcade-1941","new.dll","start");
        Check(!history.Observe("startup",false,false), "normal startup is not an inferred return");
        history.Handoff(attempt.Id,true); Check(attempt.HandedOff == true && attempt.ResumedAt == null, "handoff not gameplay success or return");
        Check(history.Observe("left",true,false) && history.Observe("back",false,false), "foreground resume after departure recorded");
        Check(attempt.ResumeObservation.Contains("Window") && history.Pending == null, "foreground observation distinguished from callback");
        history = GameCoreHistory.Parse(CoreSettingsJson.Write(history));
        attempt = history.Begin("arcade-1941","new.dll","start2"); history.Handoff(attempt.Id,true);
        var cold = GameCoreHistory.Parse(CoreSettingsJson.Write(history));
        Check(cold.Observe("callback",false,true) && cold.Attempts.Last().ResumeObservation.Contains("protocol"), "callback after process restart recorded without crash inference");
        Check(!cold.Observe("duplicate",false,true), "duplicate callback not assigned to another attempt");
        var failure = cold.Begin("arcade-1941","removed.dll","failure"); cold.Handoff(failure.Id,false,new InvalidOperationException("actual failure"));
        Check(failure.HandedOff == false && failure.ErrorCode.StartsWith("0x") && failure.Message == "actual failure" && !cold.Observe("later",false,true), "actual failure and HRESULT recorded; unrelated callback ignored");
        for(int i=0;i<1500;i++) cold.Begin("game"+i,"new.dll","at");
        Check(cold.Attempts.Count == 1000, "bounded global history");
        for(int i=0;i<30;i++) cold.Begin("one","new.dll","at");
        Check(cold.Attempts.Count(item=>item.GameId=="one")==20 && cold.Attempts.Count<=1000, "bounded per-game history");
        cold.Enabled = false; Check(cold.Begin("one","new.dll","off")==null, "optional history disabled");
        var menu = new CoreMenuNavigation(); menu.Move(CabinetAction.NextCategory,3,0); Check(menu.CoreIndex==0,"menu chooses first known core");
        menu.Move(CabinetAction.PreviousCategory,3,0); Check(menu.CoreIndex==2,"core cycling wraps");
        for(int i=0;i<4;i++) menu.Move(CabinetAction.NextGame,3,2);
        menu.Move(CabinetAction.NextCategory,3,2); Check(menu.Row==4 && menu.HistoryIndex==1 && menu.CoreIndex==2,"history navigation separate from core choice");
        menu.Busy=true; menu.Move(CabinetAction.NextGame,3,2); Check(menu.Row==4,"saving consumes input without moving actions");
        menu.RequestCancel(); menu.Busy=false; Check(menu.CancelRequested,"Back during a save is retained and vetoes test-launch on completion");
        int token; CabinetAction action;
        Check(!CabinetInputMap.Defaults().TryResolve(71,out token,out action),"no physical GameSettings default assumed");
        var map = CabinetInputMap.Parse("{\"version\":1,\"bindings\":[{\"key\":\"F\",\"action\":\"GameSettings\"}]}");
        Check(map.TryResolve(70,out token,out action) && action==CabinetAction.GameSettings,"configurable settings action");
        var gate = new CabinetRepeatGate(); Check(gate.Down(70,action,false,0,map) && !gate.Down(70,action,true,1000,map),"settings press does not reopen or repeat");
        gate.Reset(); Check(!gate.Down(13,CabinetAction.Select,true,1000,map),"held closing/test-launch key cannot trigger another launch");
        Console.WriteLine("PASS: "+checks+" core settings/persistence/history/input assertions. WinRT replacement and modal focus require Xbox.");
    }
}
