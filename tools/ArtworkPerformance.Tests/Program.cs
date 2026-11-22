using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LaunchPass.Catalog;

class Program
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    sealed class Image { public readonly string Id; public Image(string id) { Id = id; } }
    sealed class Call
    {
        public string Key;
        public TaskCompletionSource<Image> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    sealed class Loader
    {
        readonly object sync = new();
        readonly List<Call> calls = new();
        int active;
        public int Maximum;
        public bool IgnoreCancellation;
        public Call[] Calls { get { lock (sync) return calls.ToArray(); } }
        public async Task<Image> Load(string key, CancellationToken cancellation)
        {
            var call = new Call { Key = key };
            lock (sync) { calls.Add(call); active++; Maximum = Math.Max(Maximum, active); }
            try { return IgnoreCancellation ? await call.Result.Task : await call.Result.Task.WaitAsync(cancellation); }
            finally { lock (sync) active--; }
        }
    }
    static ArtworkDemand<string> Want(string key, int priority = 0) => new(key, priority);
    static async Task Until(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!done()) await Task.Delay(1, timeout.Token);
    }
    static async Task Canceled(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { checks++; return; }
        throw new Exception("Expected cancellation: " + name);
    }
    static async Task Main()
    {
        var lru = new WeightedLru<string, Image>(5, 10);
        var a = new Image("a"); var b = new Image("b"); var c = new Image("c");
        lru.Put("a", a, 3); lru.Put("b", b, 2);
        Check(lru.TryGet("a", out var hit) && ReferenceEquals(hit, a), "decoded object reused rather than recreated");
        lru.Put("c", c, 2);
        Check(!lru.TryGet("b", out _) && lru.TryGet("a", out _) && lru.TryGet("c", out _), "least recently used entry evicted");
        Check(lru.Bytes == 5 && lru.Evictions == 1, "byte weight accounting");
        lru.Resize(2, 10); Check(lru.Bytes <= 2 && lru.Count == 1, "pressure trim");
        lru.Put("huge", a, 99); Check(!lru.TryGet("huge", out _) && lru.Bytes <= 2, "oversized image is not retained");
        lru.Clear(); Check(lru.Bytes == 0 && lru.Count == 0, "lifecycle release");
        var bounded = new WeightedLru<int, Image>(1024 * 1024, 24);
        for (int i = 0; i < 256; i++) { bounded.Put(i, a, 100); Check(bounded.Count <= 24 && bounded.Bytes <= 2400, "large catalog cannot grow beyond entry limit"); }
        var byBytes = new WeightedLru<int, Image>(12 * 1024 * 1024, 24);
        for (int i = 0; i < 99; i++) { byBytes.Put(i, a, 1000 * 400 * 4); Check(byBytes.Bytes <= 12 * 1024 * 1024, "99 maximum-size logos stay inside budget"); }
        var backgrounds = new WeightedLru<int, Image>(18 * 1024 * 1024, 3);
        for (int i = 0; i < 10; i++) backgrounds.Put(i, a, 1920 * 1080 * 4);
        Check(backgrounds.Count == 2 && backgrounds.Bytes < 18 * 1024 * 1024, "background budget distinct and tight");
        for (int i = 0; i < 99; i++)
        {
            var neighbors = WheelMotion.Neighbors(i, 99);
            Check(neighbors.Count <= 6 && neighbors.Distinct().Count() == neighbors.Count && neighbors.All(n => n >= 0 && n < 99 && n != i && Math.Abs(n - i) <= 3), "bounded +/-3 prefetch window");
        }
        Check(WheelMotion.Neighbors(40, 99).SequenceEqual(new[] {39, 41, 38, 42, 37, 43}), "nearest neighbors first");
        Check(WheelMotion.Neighbors(0, 99).SequenceEqual(new[] {1, 2, 3}) && WheelMotion.Neighbors(-1, 0).Count == 0, "edge/empty prefetch");
        var motion = new WheelMotion();
        Check(motion.Retarget(82, 0, 164) == 82, "compensate instant viewport movement");
        double current = motion.At(75);
        Check(Math.Abs(current - 10.25) < 0.001, "compositor cubic easing matches portable motion");
        Check(Math.Abs(motion.Retarget(82, 75, 164) - (current + 82)) < 0.001, "interrupted motion resumes current position, no queue");
        Check(Math.Abs(motion.Retarget(-82, 75, 164) - current) < 0.001, "direction reversal retains visual continuity");
        Check(motion.At(225) == 0, "150ms settle");
        Check(motion.Retarget(1000000, 250, 164) == 164, "large jumps bounded");
        motion.Reset(); Check(motion.At(250) == 0, "category/cleanup resets animation");

        var loader = new Loader();
        var queue = new LatestArtworkQueue<string, Image>(loader.Load, 1);
        var first = queue.SetDemand(new[] { Want("same", 10), Want("same", 0) });
        var second = queue.SetDemand(new[] { Want("same", 0) });
        Check(queue.HasRequest("same") && !queue.HasRequest("other"), "in-flight look-ahead can be promoted without cancel/restart");
        Check(ReferenceEquals(first["same"], second["same"]) && loader.Calls.Length == 1 && queue.Deduplicated == 1, "concurrent request dedup/promotion");
        var decoded = new Image("same"); loader.Calls[0].Result.SetResult(decoded);
        Check(ReferenceEquals(await first["same"], decoded) && ReferenceEquals(await second["same"], decoded), "all consumers receive the same decoded object");
        await Until(() => queue.Running == 0);

        loader = new Loader(); queue = new LatestArtworkQueue<string, Image>(loader.Load, 1);
        var jobs = queue.SetDemand(new[] { Want("prefetch", 11), Want("screenshot", 2), Want("background", 1), Want("selected-logo", 0) });
        foreach (string key in new[] { "selected-logo", "background", "screenshot", "prefetch" })
        {
            await Until(() => loader.Calls.Any(call => call.Key == key));
            var call = loader.Calls.Last(); Check(call.Key == key, "visible assets precede speculation");
            call.Result.SetResult(new Image(key)); await jobs[key];
        }
        Check(loader.Maximum == 1, "decoder concurrency respected");

        loader = new Loader(); queue = new LatestArtworkQueue<string, Image>(loader.Load, 1);
        var speculative = queue.SetDemand(new[] { Want("neighbor", 11) });
        var priority = queue.SetDemand(new[] { Want("neighbor", 11), Want("visible", 0) });
        await Until(() => loader.Calls.Any(call => call.Key == "visible"));
        loader.Calls.Last().Result.SetResult(new Image("visible"));
        Check((await priority["visible"]).Id == "visible", "selected request overtakes active speculation");
        await Until(() => loader.Calls.Count(call => call.Key == "neighbor") == 2);
        loader.Calls.Last().Result.SetResult(new Image("neighbor"));
        Check((await speculative["neighbor"]).Id == "neighbor" && ReferenceEquals(speculative["neighbor"], priority["neighbor"]) && queue.Preempted == 1, "preempted neighbor resumes after visible assets without losing consumers");

        loader = new Loader { IgnoreCancellation = true }; queue = new LatestArtworkQueue<string, Image>(loader.Load, 1);
        var old = queue.SetDemand(new[] { Want("old") });
        var latest = queue.SetDemand(new[] { Want("new") });
        await Canceled(old["old"], "obsolete result canceled immediately");
        loader.Calls[0].Result.SetResult(new Image("stale"));
        await Until(() => loader.Calls.Length == 2);
        loader.Calls[1].Result.SetResult(new Image("new"));
        Check((await latest["new"]).Id == "new" && old["old"].IsCanceled, "late obsolete completion cannot become current result");

        loader = new Loader(); queue = new LatestArtworkQueue<string, Image>(loader.Load, 2);
        var many = queue.SetDemand(Enumerable.Range(0, 99).Select(i => Want("old-" + i, 10 + i)));
        var newest = queue.SetDemand(new[] { Want("current") });
        await Until(() => loader.Calls.Any(call => call.Key == "current"));
        loader.Calls.Single(call => call.Key == "current").Result.SetResult(new Image("current"));
        Check((await newest["current"]).Id == "current" && many.Values.All(task => task.IsCanceled), "latest selection discards an entire obsolete queue");
        Check(loader.Maximum <= 2 && queue.Pending == 0, "no unbounded concurrent work");
        await Until(() => queue.Running == 0);
        var clear = queue.SetDemand(new[] { Want("clear") }); queue.Clear();
        await Canceled(clear["clear"], "clear cancels work on launch/memory pressure");
        await Until(() => queue.Running == 0);
        Check(queue.Pending == 0, "clear releases queued requests");

        var errors = new LatestArtworkQueue<string, Image>((key, ct) => key == "bad" ? Task.FromException<Image>(new InvalidOperationException("corrupt")) : Task.FromResult(new Image(key)));
        try { await errors.SetDemand(new[] { Want("bad") })["bad"]; throw new Exception("Failure was hidden"); }
        catch (InvalidOperationException) { checks++; }
        Check((await errors.SetDemand(new[] { Want("good") })["good"]).Id == "good", "corrupt load does not stall later requests");
        var stress = new LatestArtworkQueue<string, Image>(async (key, cancellation) => { await Task.Delay(2, cancellation); return new Image(key); }, 2);
        IReadOnlyDictionary<string, Task<Image>> final = null;
        for (int i = 0; i < 100; i++) final = stress.SetDemand(new[] { Want("game-" + i), Want("neighbor-" + i, 11) });
        Check((await final["game-99"].WaitAsync(TimeSpan.FromSeconds(5))).Id == "game-99", "100 rapid selections settle on latest game");
        await final["neighbor-99"].WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => stress.Running == 0);
        Check(stress.Pending == 0, "rapid-input stress leaves no accumulated queue");
        Console.WriteLine("PASS: " + checks + " artwork cache/priority/cancellation/motion assertions. UWP decode/GPU/focus require hardware.");
    }
}
