using LaunchPass.Catalog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI.Xaml.Media.Imaging;

namespace RetroPass
{
    internal sealed class PremiumArtworkRequest : IEquatable<PremiumArtworkRequest>
    {
        internal readonly CatalogEntry Entry;
        internal readonly string Role;
        private readonly string key;
        internal PremiumArtworkRequest(CatalogEntry entry, string role) { Entry = entry; Role = role; key = entry.Id + "|" + role; }
        public bool Equals(PremiumArtworkRequest other) => other != null && StringComparer.OrdinalIgnoreCase.Equals(key, other.key);
        public override bool Equals(object obj) => Equals(obj as PremiumArtworkRequest);
        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(key);
    }
    internal sealed class PremiumArtworkImage
    {
        internal BitmapImage Image;
        internal long Bytes;
        internal string Path;
    }

    // UI-thread-owned store. Shared physical-file decodes have independent consumer leases.
    internal sealed class PremiumArtworkCache
    {
        private sealed class Decode
        {
            internal CancellationTokenSource Cancellation = new CancellationTokenSource();
            internal Task<PremiumArtworkImage> Task;
            internal int Consumers;
        }
        private const long MiB = 1024 * 1024;
        private readonly WeightedLru<string, PremiumArtworkImage> logos = new WeightedLru<string, PremiumArtworkImage>(12 * MiB, 24, StringComparer.OrdinalIgnoreCase);
        private readonly WeightedLru<string, PremiumArtworkImage> backgrounds = new WeightedLru<string, PremiumArtworkImage>(18 * MiB, 3, StringComparer.OrdinalIgnoreCase);
        private readonly WeightedLru<string, PremiumArtworkImage> screenshots = new WeightedLru<string, PremiumArtworkImage>(8 * MiB, 4, StringComparer.OrdinalIgnoreCase);
        private readonly WeightedLru<string, DateTimeOffset> negative = new WeightedLru<string, DateTimeOffset>(128 * 1024, 1024, StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Decode> pending = new Dictionary<string, Decode>(StringComparer.OrdinalIgnoreCase);
        private int epoch;
        internal long DecodeCount, DecodeFailures, CanceledDecodes, SharedDecodes, NegativeHits;
        internal double TotalDecodeMs, MaximumDecodeMs, TotalLoadMs;
        internal long Bytes => logos.Bytes + backgrounds.Bytes + screenshots.Bytes;
        internal string Snapshot => "cache MiB=" + (Bytes / (double)MiB).ToString("F1") + "/" + ((logos.Budget + backgrounds.Budget + screenshots.Budget) / MiB) + "; " +
            "logo H/M=" + logos.Hits + "/" + logos.Misses + " bg=" + backgrounds.Hits + "/" + backgrounds.Misses + " shot=" + screenshots.Hits + "/" + screenshots.Misses +
            "; entries=" + (logos.Count + backgrounds.Count + screenshots.Count) + " evict=" + (logos.Evictions + backgrounds.Evictions + screenshots.Evictions) +
            "; decode n=" + DecodeCount + " load/decode/max ms=" + (DecodeCount == 0 ? 0 : TotalLoadMs / DecodeCount).ToString("F1") + "/" + (DecodeCount == 0 ? 0 : TotalDecodeMs / DecodeCount).ToString("F1") + "/" + MaximumDecodeMs.ToString("F1") +
            " fail=" + DecodeFailures + " canceled=" + CanceledDecodes + " shared=" + SharedDecodes + " negative=" + NegativeHits;

        private WeightedLru<string, PremiumArtworkImage> Cache(string role) => role == "logo" ? logos : role == "background" ? backgrounds : screenshots;
        internal void SetPressure(bool pressure)
        {
            logos.Resize((pressure ? 4 : 12) * MiB, pressure ? 8 : 24);
            backgrounds.Resize((pressure ? 9 : 18) * MiB, pressure ? 1 : 3);
            screenshots.Resize((pressure ? 4 : 8) * MiB, pressure ? 1 : 4);
        }
        internal bool TryCached(PremiumArtworkRequest request, out PremiumArtworkImage image)
        {
            foreach (string path in PremiumArtwork.Candidates(request.Entry, request.Role))
                if (Cache(request.Role).TryGet(request.Role + "|" + path, out image)) return true;
            image = null; return false;
        }
        internal async Task<PremiumArtworkImage> LoadAsync(PremiumArtworkRequest request, CancellationToken cancellation)
        {
            foreach (string path in PremiumArtwork.Candidates(request.Entry, request.Role))
            {
                cancellation.ThrowIfCancellationRequested();
                string key = request.Role + "|" + path;
                PremiumArtworkImage cached;
                if (Cache(request.Role).TryGet(key, out cached)) return cached;
                DateTimeOffset until;
                if (negative.TryGet(key, out until) && until > DateTimeOffset.UtcNow) { NegativeHits++; continue; }
                Decode decode;
                if (!pending.TryGetValue(key, out decode) || decode.Cancellation.IsCancellationRequested)
                {
                    decode = new Decode(); pending[key] = decode;
                    decode.Task = DecodeAsync(path, request.Role, key, decode.Cancellation.Token, epoch);
                }
                else SharedDecodes++;
                decode.Consumers++;
                try
                {
                    var image = await AwaitConsumer(decode.Task, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if (image != null) return image;
                }
                finally
                {
                    if (--decode.Consumers == 0)
                    {
                        Decode current;
                        if (pending.TryGetValue(key, out current) && ReferenceEquals(current, decode)) pending.Remove(key);
                        if (!decode.Task.IsCompleted) decode.Cancellation.Cancel();
                        var completed = decode;
                        // Observe late faults and dispose only after the physical operation settles.
                        var cleanup = completed.Task.ContinueWith(task => { var error = task.Exception; completed.Cancellation.Dispose(); }, TaskScheduler.Default);
                    }
                }
            }
            return null;
        }
        private static async Task<T> AwaitConsumer<T>(Task<T> task, CancellationToken cancellation)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellation.Register(() => canceled.TrySetCanceled()))
            {
                await Task.WhenAny(task, canceled.Task);
                cancellation.ThrowIfCancellationRequested();
                return await task;
            }
        }
        private async Task<PremiumArtworkImage> DecodeAsync(string path, string role, string key, CancellationToken cancellation, int version)
        {
            var elapsed = Stopwatch.StartNew();
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(path).AsTask(cancellation);
                using (var stream = await file.OpenReadAsync().AsTask(cancellation))
                {
                    if (stream.Size > 30 * MiB) throw new InvalidOperationException("Artwork exceeds 30 MiB.");
                    var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellation);
                    if ((long)decoder.PixelWidth * decoder.PixelHeight > 40000000) throw new InvalidOperationException("Artwork exceeds 40 million pixels.");
                    int maxWidth = role == "background" ? 1920 : 1000;
                    int maxHeight = role == "background" ? 1080 : role == "screenshot" ? 800 : 400;
                    double factor = Math.Min(1, Math.Min((double)maxWidth / decoder.PixelWidth, (double)maxHeight / decoder.PixelHeight));
                    int width = Math.Max(1, (int)(decoder.PixelWidth * factor));
                    int height = Math.Max(1, (int)Math.Ceiling(decoder.PixelHeight * ((double)width / decoder.PixelWidth)));
                    if (height > maxHeight + 1) throw new InvalidOperationException("Unrepresentable artwork aspect ratio.");
                    cancellation.ThrowIfCancellationRequested();
                    stream.Seek(0);
                    var image = new BitmapImage { DecodePixelType = DecodePixelType.Physical, DecodePixelWidth = width };
                    var decodeTime = Stopwatch.StartNew();
                    await image.SetSourceAsync(stream).AsTask(cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    var result = new PremiumArtworkImage { Image = image, Bytes = (long)width * height * 4, Path = path };
                    if (version == epoch) Cache(role).Put(key, result, result.Bytes);
                    DecodeCount++; TotalLoadMs += elapsed.Elapsed.TotalMilliseconds; TotalDecodeMs += decodeTime.Elapsed.TotalMilliseconds;
                    MaximumDecodeMs = Math.Max(MaximumDecodeMs, decodeTime.Elapsed.TotalMilliseconds);
                    return result;
                }
            }
            catch (OperationCanceledException) { CanceledDecodes++; throw; }
            catch (FileNotFoundException) { if (version == epoch) negative.Put(key, DateTimeOffset.UtcNow.AddSeconds(30), 64); }
            catch (DirectoryNotFoundException) { if (version == epoch) negative.Put(key, DateTimeOffset.UtcNow.AddSeconds(30), 64); }
            catch (Exception)
            {
                if (cancellation.IsCancellationRequested) { CanceledDecodes++; throw new OperationCanceledException(cancellation); }
                DecodeFailures++; if (version == epoch) negative.Put(key, DateTimeOffset.UtcNow.AddSeconds(30), 64);
            }
            return null;
        }
        internal void Clear()
        {
            epoch++;
            foreach (var decode in pending.Values.ToArray()) decode.Cancellation.Cancel();
            pending.Clear(); logos.Clear(); backgrounds.Clear(); screenshots.Clear(); negative.Clear();
        }
    }
}
