using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace RetroPass
{
    internal sealed class PremiumPerformanceDiagnostics
    {
        private readonly TextBlock display;
        private readonly Func<string> snapshot;
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private bool enabled, writing;
        private string lastError;
        internal PremiumPerformanceDiagnostics(TextBlock display, Func<string> snapshot)
        {
            this.display = display; this.snapshot = snapshot;
            timer.Tick += async (sender, args) => await CaptureAsync();
        }
        internal async Task RefreshAsync()
        {
            try
            {
                enabled = await ApplicationData.Current.LocalFolder.TryGetItemAsync("PerformanceDiagnostics.enabled") is StorageFile;
                display.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
                if (enabled) { timer.Start(); await CaptureAsync(); } else timer.Stop();
            }
            catch (Exception ex) { lastError = ex.Message; Stop(); }
        }
        internal void Stop() { enabled = false; timer.Stop(); display.Visibility = Visibility.Collapsed; }
        private async Task CaptureAsync()
        {
            if (!enabled || writing) return;
            try
            {
                string line = snapshot();
                display.Text = "Performance (2s samples) " + line + (lastError == null ? "" : " log: " + lastError);
                string folder = ApplicationData.Current.LocalFolder.Path;
                writing = true;
                await Task.Run(() =>
                {
                    string path = Path.Combine(folder, "Performance.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                    {
                        string previous = Path.Combine(folder, "Performance.previous.log");
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(path, previous);
                    }
                    File.AppendAllText(path, DateTimeOffset.UtcNow.ToString("O") + " " + line + Environment.NewLine);
                });
            }
            catch (Exception ex) { lastError = ex.Message; }
            finally { writing = false; }
        }
    }
}
