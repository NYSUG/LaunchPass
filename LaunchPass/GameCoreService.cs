using LaunchPass.Catalog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace RetroPass
{
    internal sealed class LocalGameSettingsFiles : IGameSettingsFiles
    {
        private static void Name(string name)
        {
            if (name != GameOverridePersistence.FileName && name != "CoreRegistry.json" && name != "GameLaunchHistory.json" &&
                !System.Text.RegularExpressions.Regex.IsMatch(name, @"\AGameOverrides\.corrupt\.[a-f0-9]{32}\.json\z"))
                throw new ArgumentException("Invalid settings filename.");
        }
        public async Task<string> ReadAsync(string name)
        {
            Name(name);
            var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(name);
            if (item == null) return null;
            var file = item as StorageFile;
            if (file == null || (await file.GetBasicPropertiesAsync()).Size > CoreSettingsJson.MaximumBytes)
                throw new FormatException(name + " is not a settings file of at most 1 MiB.");
            return await FileIO.ReadTextAsync(file);
        }
        public async Task WriteNewAsync(string name, string data)
        {
            Name(name);
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(name, CreationCollisionOption.FailIfExists);
            await FileIO.WriteTextAsync(file, data);
        }
        public async Task ReplaceAsync(string name, string data, string expected)
        {
            Name(name);
            if (await ReadAsync(name) != expected) throw new InvalidOperationException(name + " changed; reopen settings before saving.");
            var folder = ApplicationData.Current.LocalFolder;
            string temporaryName = "." + name + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var temp = await folder.CreateFileAsync(temporaryName, CreationCollisionOption.FailIfExists);
            bool moved = false;
            try
            {
                await FileIO.WriteTextAsync(temp, data);
                using (var stream = await temp.OpenAsync(FileAccessMode.ReadWrite)) await stream.FlushAsync();
                if (await ReadAsync(name) != expected) throw new InvalidOperationException(name + " changed while saving; preserved external edits.");
                if (expected == null) await temp.RenameAsync(name, NameCollisionOption.FailIfExists);
                else await temp.MoveAndReplaceAsync(await folder.GetFileAsync(name));
                moved = true;
            }
            finally
            {
                if (!moved)
                    try
                    {
                        // Resolve the original temporary name: a StorageFile object
                        // can follow a rename and must never be used to delete a target.
                        var leftover = await folder.TryGetItemAsync(temporaryName) as StorageFile;
                        if (leftover != null) await leftover.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                    catch { }
            }
        }
    }
    internal sealed class GameCoreContext
    {
        internal CoreRegistry Registry;
        internal GameOverrideDocument Overrides;
        internal GameCoreHistory History;
        internal string Effective, Warning;
    }
    internal sealed class PreparedCoreLaunch
    {
        internal LaunchRequest Request;
        internal string AttemptId;
    }
    internal sealed class GameCoreService
    {
        private static GameCoreService current;
        internal static GameCoreService Current => current ?? (current = new GameCoreService());
        private readonly IGameSettingsFiles files = new LocalGameSettingsFiles();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly List<CatalogEntry> catalog = new List<CatalogEntry>();
        private GameCoreService()
        {
            Window.Current.CoreWindow.Activated += (sender, args) => Observe(args.WindowActivationState == CoreWindowActivationState.Deactivated, false);
            Application.Current.EnteredBackground += (sender, args) => Observe(true, false);
        }
        internal void RegisterCatalog(IEnumerable<CatalogEntry> entries) { catalog.Clear(); catalog.AddRange(entries); }
        private static string Now => DateTimeOffset.UtcNow.ToString("O");
        internal async Task<GameCoreContext> LoadAsync(CatalogEntry entry)
        {
            await gate.WaitAsync();
            try { return await LoadUnlocked(entry); }
            finally { gate.Release(); }
        }
        private async Task<GameCoreContext> LoadUnlocked(CatalogEntry entry)
        {
            var context = new GameCoreContext { Registry = new CoreRegistry { Source = "Catalog configured cores; installation not independently verified" }, Overrides = new GameOverrideDocument(), History = new GameCoreHistory() };
            try { string raw = await files.ReadAsync("CoreRegistry.json"); if (raw != null) context.Registry = CoreRegistry.Parse(raw); }
            catch (Exception ex) { context.Warning += "Core registry unavailable; using catalog-configured cores. " + ex.Message + "\n"; }
            context.Registry.IncludeConfigured(catalog.Select(game => game.Launch.Core).Concat(new[] { entry.Launch.Core }));
            try { context.Overrides = await new GameOverridePersistence(files).LoadAsync(); }
            catch (CoreSettingsVersionException ex) { context.Warning += "Newer override schema: catalog default is in use, and saving is blocked. " + ex.Message + "\n"; }
            catch (Exception ex) { context.Warning += "Overrides unreadable; catalog default is in use. Repairing with Save/Reset backs up corrupt JSON and starts a fresh override map. " + ex.Message + "\n"; }
            try { string raw = await files.ReadAsync("GameLaunchHistory.json"); if (raw != null) context.History = GameCoreHistory.Parse(raw); }
            catch (Exception ex) { context.Warning += "History unavailable: " + ex.Message + "\n"; }
            context.Effective = GameCoreRules.Resolve(context.Overrides, entry.Id, entry.CatalogCoreOverride, entry.PlatformDefaultCore);
            if (!context.Registry.Contains(context.Effective)) context.Warning += "Effective core is unavailable or no longer configured: " + context.Effective + ". Choose another core or Reset.\n";
            return context;
        }
        internal async Task SaveAsync(CatalogEntry entry, string core)
        {
            await gate.WaitAsync();
            try
            {
                var context = await LoadUnlocked(entry);
                if (core != null && !context.Registry.Contains(core)) throw new InvalidOperationException("That core is unavailable; choose a configured core.");
                await new GameOverridePersistence(files).SaveAsync(entry.Id, core);
            }
            finally { gate.Release(); }
        }
        private async Task SaveHistory(GameCoreHistory history, string previous)
        {
            string data = CoreSettingsJson.Write(history);
            while (System.Text.Encoding.UTF8.GetByteCount(data) > 256 * 1024 && history.Attempts.Count > 1)
            { history.Attempts.RemoveAt(0); data = CoreSettingsJson.Write(history); }
            if (System.Text.Encoding.UTF8.GetByteCount(data) > 256 * 1024) throw new FormatException("History exceeds 256 KiB.");
            await files.ReplaceAsync("GameLaunchHistory.json", data, previous);
        }
        internal async Task<PreparedCoreLaunch> PrepareAsync(CatalogEntry entry)
        {
            await gate.WaitAsync();
            try
            {
                var context = await LoadUnlocked(entry);
                var result = new PreparedCoreLaunch { Request = entry.WithCore(context.Effective) };
                if (!string.IsNullOrEmpty(context.Warning)) StartupLog.Write("Game core settings: " + context.Warning);
                try
                {
                    string previous = await files.ReadAsync("GameLaunchHistory.json");
                    // Never overwrite an unreadable or newer history file on launch.
                    var history = previous == null ? new GameCoreHistory() : GameCoreHistory.Parse(previous);
                    var attempt = history.Begin(entry.Id, context.Effective, Now);
                    result.AttemptId = attempt?.Id;
                    if (attempt != null) await SaveHistory(history, previous);
                }
                catch (Exception ex) { result.AttemptId = null; StartupLog.Write("Core history record skipped: " + ex.Message); }
                if (!context.Registry.Contains(context.Effective))
                {
                    var error = new InvalidOperationException("Core " + context.Effective + " is unavailable. Open Game Settings and select another core or Reset to Default.");
                    await CompleteUnlocked(result.AttemptId, false, error);
                    throw error;
                }
                return result;
            }
            finally { gate.Release(); }
        }
        internal async Task CompleteAsync(string id, bool handoff, Exception error = null)
        {
            if (id == null) return;
            await gate.WaitAsync();
            try { await CompleteUnlocked(id, handoff, error); }
            finally { gate.Release(); }
        }
        private async Task CompleteUnlocked(string id, bool handoff, Exception error)
        {
            if (id == null) return;
            try
            {
                var previous = await files.ReadAsync("GameLaunchHistory.json");
                if (previous == null) return;
                var history = GameCoreHistory.Parse(previous); history.Handoff(id, handoff, error);
                await SaveHistory(history, previous);
            }
            catch (Exception ex) { StartupLog.Write("Core history outcome skipped: " + ex.Message); }
        }
        private async void Observe(bool departure, bool protocol)
        { await ObserveAsync(departure, protocol); }
        internal async Task ObserveAsync(bool departure, bool protocol)
        {
            await gate.WaitAsync();
            try
            {
                string previous = await files.ReadAsync("GameLaunchHistory.json");
                if (previous == null) return;
                var history = GameCoreHistory.Parse(previous);
                if (history.Observe(Now, departure, protocol)) await SaveHistory(history, previous);
            }
            catch (Exception ex) { StartupLog.Write("Core history lifecycle skipped: " + ex.Message); }
            finally { gate.Release(); }
        }
    }
}
