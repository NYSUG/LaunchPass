using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LaunchPass.Catalog
{
    // Measured decoded pixels, not compressed file size. Callers own visible references.
    public sealed class WeightedLru<TKey, TValue>
    {
        private sealed class Item { public TKey Key; public TValue Value; public long Bytes; }
        private readonly Dictionary<TKey, LinkedListNode<Item>> items;
        private readonly LinkedList<Item> order = new LinkedList<Item>();
        public long Budget { get; private set; }
        public long Bytes { get; private set; }
        public int Count => items.Count;
        public long Hits { get; private set; }
        public long Misses { get; private set; }
        public long Evictions { get; private set; }
        public int Capacity { get; private set; }
        public WeightedLru(long budget, int capacity, IEqualityComparer<TKey> comparer = null)
        {
            if (budget < 0 || capacity < 0) throw new ArgumentOutOfRangeException();
            Budget = budget; Capacity = capacity;
            items = new Dictionary<TKey, LinkedListNode<Item>>(comparer ?? EqualityComparer<TKey>.Default);
        }
        public bool TryGet(TKey key, out TValue value)
        {
            LinkedListNode<Item> node;
            if (!items.TryGetValue(key, out node)) { Misses++; value = default(TValue); return false; }
            order.Remove(node); order.AddFirst(node); Hits++; value = node.Value.Value; return true;
        }
        public void Put(TKey key, TValue value, long bytes)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            LinkedListNode<Item> old;
            if (items.TryGetValue(key, out old)) { order.Remove(old); items.Remove(key); Bytes -= old.Value.Bytes; }
            if (bytes > Budget || Capacity == 0) return;
            var node = order.AddFirst(new Item { Key = key, Value = value, Bytes = bytes });
            items.Add(key, node); Bytes += bytes; Trim();
        }
        public void Resize(long budget, int capacity)
        {
            if (budget < 0 || capacity < 0) throw new ArgumentOutOfRangeException();
            Budget = budget; Capacity = capacity; Trim();
        }
        private void Trim()
        {
            while (Bytes > Budget || items.Count > Capacity)
            {
                var node = order.Last; order.RemoveLast(); items.Remove(node.Value.Key);
                Bytes -= node.Value.Bytes; Evictions++;
            }
        }
        public void Clear() { items.Clear(); order.Clear(); Bytes = 0; }
    }

    public sealed class ArtworkDemand<TKey>
    {
        public TKey Key { get; private set; }
        public int Priority { get; private set; }
        public ArtworkDemand(TKey key, int priority) { Key = key; Priority = priority; }
    }

    // Latest demand replaces the queue. Active obsolete work is cooperatively canceled.
    // Lock protects portable tests/loader completions; the UWP caller invokes on its UI thread.
    public sealed class LatestArtworkQueue<TKey, TValue>
    {
        private sealed class Job
        {
            public TKey Key; public int Priority; public long Order; public bool Active, Requeue;
            public CancellationTokenSource Cancellation = new CancellationTokenSource();
            public TaskCompletionSource<TValue> Completion = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly object sync = new object();
        private readonly Dictionary<TKey, Job> jobs = new Dictionary<TKey, Job>();
        private readonly Func<TKey, CancellationToken, Task<TValue>> load;
        private readonly int concurrency;
        private long sequence;
        private int running;
        public long Started { get; private set; }
        public long Deduplicated { get; private set; }
        public long Canceled { get; private set; }
        public long Preempted { get; private set; }
        public int Running { get { lock (sync) return running; } }
        public int Pending { get { lock (sync) return jobs.Values.Count(j => !j.Active); } }
        public bool HasRequest(TKey key) { lock (sync) return jobs.ContainsKey(key); }
        public LatestArtworkQueue(Func<TKey, CancellationToken, Task<TValue>> loader, int concurrency = 2)
        {
            if (loader == null) throw new ArgumentNullException(nameof(loader));
            if (concurrency < 1) throw new ArgumentOutOfRangeException(nameof(concurrency));
            load = loader; this.concurrency = concurrency;
        }
        public IReadOnlyDictionary<TKey, Task<TValue>> SetDemand(IEnumerable<ArtworkDemand<TKey>> demand)
        {
            var wanted = demand.GroupBy(d => d.Key).ToDictionary(g => g.Key, g => g.Min(d => d.Priority));
            var result = new Dictionary<TKey, Task<TValue>>();
            lock (sync)
            {
                foreach (var old in jobs.Values.ToArray())
                {
                    if (wanted.ContainsKey(old.Key)) continue;
                    Cancel(old);
                }
                foreach (var item in wanted)
                {
                    Job job;
                    if (jobs.TryGetValue(item.Key, out job)) { job.Priority = item.Value; Deduplicated++; }
                    else { job = new Job { Key = item.Key, Priority = item.Value, Order = sequence++ }; jobs.Add(item.Key, job); }
                    result.Add(item.Key, job.Completion.Task);
                }
                // A speculative decode cannot occupy a selected-game slot indefinitely.
                int? urgent = jobs.Values.Where(j => !j.Active).Select(j => (int?)j.Priority).Min();
                if (running >= concurrency && urgent.HasValue)
                    foreach (var job in jobs.Values.Where(j => j.Active && !j.Cancellation.IsCancellationRequested && j.Priority >= 10 && j.Priority > urgent.Value).ToArray())
                    {
                        job.Requeue = true; Preempted++; job.Cancellation.Cancel();
                    }
            }
            Pump(); return result;
        }
        private void Cancel(Job job)
        {
            jobs.Remove(job.Key); Canceled++; job.Requeue = false;
            job.Cancellation.Cancel(); job.Completion.TrySetCanceled();
            if (!job.Active) job.Cancellation.Dispose();
        }
        private void Pump()
        {
            List<Job> start = new List<Job>();
            lock (sync)
            {
                while (running < concurrency)
                {
                    var next = jobs.Values.Where(j => !j.Active).OrderBy(j => j.Priority).ThenBy(j => j.Order).FirstOrDefault();
                    if (next == null) break;
                    next.Active = true; running++; Started++; start.Add(next);
                }
            }
            foreach (var job in start) Run(job);
        }
        private async void Run(Job job)
        {
            try
            {
                var value = await load(job.Key, job.Cancellation.Token);
                if (job.Cancellation.IsCancellationRequested) { if (!job.Requeue) job.Completion.TrySetCanceled(); }
                else job.Completion.TrySetResult(value);
            }
            catch (OperationCanceledException) { if (!job.Requeue) job.Completion.TrySetCanceled(); }
            catch (Exception ex) { job.Completion.TrySetException(ex); }
            finally
            {
                CancellationTokenSource previous;
                lock (sync)
                {
                    Job current;
                    previous = job.Cancellation;
                    if (jobs.TryGetValue(job.Key, out current) && ReferenceEquals(current, job))
                    {
                        if (job.Requeue)
                        {
                            job.Active = false; job.Requeue = false; job.Order = sequence++;
                            job.Cancellation = new CancellationTokenSource();
                        }
                        else jobs.Remove(job.Key);
                    }
                    running--;
                }
                previous.Dispose(); Pump();
            }
        }
        public void Clear() { SetDemand(new ArtworkDemand<TKey>[0]); }
    }

    // Matches Composition's cubic-bezier (1/3,1, 2/3,1). No queued animations.
    public sealed class WheelMotion
    {
        private double start, began;
        public const double DurationMs = 150;
        public double At(double milliseconds)
        {
            double t = Math.Max(0, Math.Min(1, (milliseconds - began) / DurationMs));
            return start * Math.Pow(1 - t, 3);
        }
        public double Retarget(double scrollDelta, double milliseconds, double maximum)
        {
            start = Math.Max(-maximum, Math.Min(maximum, At(milliseconds) + scrollDelta));
            began = milliseconds; return start;
        }
        public void Reset() { start = 0; began = 0; }
        public static IReadOnlyList<int> Neighbors(int index, int count, int radius = 3)
        {
            var result = new List<int>();
            if (index < 0 || index >= count) return result;
            for (int d = 1; d <= radius; d++) { if (index - d >= 0) result.Add(index - d); if (index + d < count) result.Add(index + d); }
            return result;
        }
    }
}
