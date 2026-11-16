using LaunchPass.Catalog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace RetroPass
{
    // Only this type may bypass the external-storage ROM preflight.
    public sealed class InternalCatalogGame : Game
    {
        public CatalogEntry Entry { get; private set; }
        private bool launching;
        public InternalCatalogGame(CatalogEntry entry, Platform platform)
        {
            Entry = entry; Title = entry.Title; GamePlatform = platform;
            ApplicationPathFull = entry.Launch.RomPath; CoreName = entry.Launch.Core;
        }
        public override void Init() { }
        public async Task LaunchAsync()
        {
            if (launching) return;
            launching = true;
            try
            {
                // Use the very same production generator and activation API as the probe.
                string url = UrlSchemeGenerator.GetUrl(this);
                if (url != Entry.Launch.ProtocolUri) throw new InvalidOperationException("Catalog launch format differs from the validated generator.");
                Log("Game=" + Entry.Id + " activation requested URI=" + url);
                bool activated = await Launcher.LaunchUriAsync(new Uri(url));
                Log("Game=" + Entry.Id + " LaunchUriAsync=" + activated + "; game loading requires Xbox verification");
                if (!activated) throw new InvalidOperationException("RetroArch protocol activation was declined.");
            }
            finally { launching = false; }
        }
        internal static void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine(message);
            try { File.AppendAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, "InternalCatalog.log"), DateTimeOffset.UtcNow.ToString("O") + " " + message + Environment.NewLine); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
        public async Task<BitmapImage> LoadArtworkAsync()
        {
            if (Entry.Artwork != null)
            {
                try
                {
                    var file = await ApplicationData.Current.LocalFolder.GetFileAsync(Entry.Artwork);
                    using (var stream = await file.OpenReadAsync())
                    {
                        var image = new BitmapImage();
                        await image.SetSourceAsync(stream);
                        return image;
                    }
                }
                catch (Exception ex) { Log("Artwork fallback game=" + Entry.Id + ": " + ex.Message); }
            }
            return new BitmapImage(new Uri("ms-appx:///Assets/empty.png"));
        }
    }
}
