using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace RetroPass
{
    [XmlRoot("InternalStorageProbe")]
    public sealed class InternalStorageProbeConfig
    {
        public bool Enabled { get; set; }
        public string RomPath { get; set; }
        public string CoreFileName { get; set; }
        [XmlIgnore] public string ConfigurationError { get; set; }

        internal static async Task<InternalStorageProbeConfig> Load()
        {
            var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync("InternalStorageProbe.xml") as StorageFile;
            if (file == null)
                return null;

            try
            {
                using (var reader = new StringReader(await FileIO.ReadTextAsync(file)))
                    return (InternalStorageProbeConfig)new XmlSerializer(typeof(InternalStorageProbeConfig)).Deserialize(reader);
            }
            catch (Exception ex)
            {
                // Keep a malformed probe configuration visible without entering USB/theme startup.
                return new InternalStorageProbeConfig { Enabled = true, ConfigurationError = ex.ToString() };
            }
        }

        internal void Validate()
        {
            if (!Enabled || ConfigurationError != null)
                throw new InvalidOperationException(ConfigurationError ?? "Probe is disabled.");

            // Require a fully qualified Windows drive path, not C:relative or \root-relative.
            // This checks syntax only; LaunchPass never opens or enumerates the target.
            if (string.IsNullOrWhiteSpace(RomPath) ||
                !Regex.IsMatch(RomPath, @"\A[A-Za-z]:\\(?:[^\\/:*?""<>|\p{Cc}]+\\)*1941\.zip\z", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("Configure a fully qualified drive path ending in 1941.zip, with no quotes or control characters.");

            // The existing generator adds cores\ itself. Do not accept paths or CLI switches here.
            if (string.IsNullOrWhiteSpace(CoreFileName) ||
                !Regex.IsMatch(CoreFileName, @"\A[A-Za-z0-9_-][A-Za-z0-9_.-]*\.dll\z", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("Configure the exact core DLL filename only (no directory, spaces, or command-line arguments).");
        }
    }

    internal static class InternalStorageProbeLog
    {
        internal static bool Enabled { get; set; }
        internal static string LastError { get; private set; }
        internal static string LogPath => Path.Combine(ApplicationData.Current.LocalFolder.Path, "InternalStorageProbe.log");

        internal static void Write(string message)
        {
            if (!Enabled)
                return;

            string line = DateTimeOffset.UtcNow.ToString("O") + " " + message;
            Debug.WriteLine(line);
            try
            {
                // Close after each append so requests/lifecycle events survive suspension or termination.
                File.AppendAllText(LogPath, line + Environment.NewLine);
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.WriteLine("Probe log write failed: " + ex);
            }
        }
    }

    // Temporary opt-in screen; never loads a library, theme, or private RetroArch StorageFile.
    public sealed class InternalStorageProbePage : Page
    {
        private readonly InternalStorageProbeConfig config;
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly Button launch = new Button { Content = "Launch 1941.zip in RetroArch" };

        public InternalStorageProbePage() : this(new InternalStorageProbeConfig()) { }

        internal InternalStorageProbePage(InternalStorageProbeConfig config)
        {
            this.config = config;
            var panel = new StackPanel { Margin = new Thickness(48), Spacing = 16 };
            panel.Children.Add(new TextBlock { Text = "Internal-storage launch probe: 1941.zip", FontSize = 28 });
            panel.Children.Add(new TextBlock
            {
                Text = "ROM: " + config.RomPath + "\nCore: " + config.CoreFileName +
                    "\nLog: " + InternalStorageProbeLog.LogPath,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(launch);
            panel.Children.Add(status);
            Content = new ScrollViewer { Content = panel };
            launch.Click += Launch_Click;
            Loaded += (sender, args) => launch.Focus(FocusState.Programmatic);
            status.Text = config.ConfigurationError == null
                ? "Ready. Protocol activation does not confirm that the game loaded."
                : "Configuration error: " + config.ConfigurationError;
        }

        private async void Launch_Click(object sender, RoutedEventArgs e)
        {
            launch.IsEnabled = false;
            string attempt = Guid.NewGuid().ToString("N");
            try
            {
                config.Validate();
                // This fixed, explicitly configured test entry is the only preflight bypass.
                // Existing GameDetailsPage.StartContent (and its file check) is unchanged.
                var game = new GameRetroPass
                {
                    Title = "1941.zip (internal-storage probe)",
                    ApplicationPathFull = config.RomPath,
                    CoreName = config.CoreFileName,
                    GamePlatform = new Platform { EmulatorType = Platform.EEmulatorType.retroarch }
                };
                string generatedUri = UrlSchemeGenerator.GetUrl(game);
                var uri = new Uri(generatedUri);
                InternalStorageProbeLog.Write("Attempt=" + attempt + " preflight=skipped for configured 1941.zip only; generated URI=" + generatedUri);
                InternalStorageProbeLog.Write("Attempt=" + attempt + " activation requested; URI=" + uri.AbsoluteUri);
                status.Text = "Activation requested. Confirm game loading on the Xbox screen and in RetroArch logs.";
                bool activated = await Launcher.LaunchUriAsync(uri);
                InternalStorageProbeLog.Write("Attempt=" + attempt + " LaunchUriAsync result=" + activated + "; game loading=unverified");
                status.Text = "LaunchUriAsync returned " + activated + ". Game loading remains unverified; check RetroArch.";
            }
            catch (Exception ex)
            {
                InternalStorageProbeLog.Write("Attempt=" + attempt + " validation/activation error: " + ex);
                status.Text = "Probe failed: " + ex.Message;
            }
            finally
            {
                if (InternalStorageProbeLog.LastError != null)
                    status.Text += "\nLog write failed: " + InternalStorageProbeLog.LastError;
                launch.IsEnabled = true;
            }
        }
    }
}
