using LaunchPass;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace RetroPass
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    sealed partial class App : Application
    {
        public static readonly string SettingsAutoPlayVideo = "SettingsAutoPlayVideo";
        public static readonly string SettingsLoggingEnabled = "SettingsLoggingEnabled";
        public LaunchPassThemeSettings CurrentThemeSettings { get; set; }
        public string LaunchPassRootPath { get; set; }
        public bool InternalCatalogMode { get; private set; }
        public bool IsLoadMainPage { get; set; } = true;
        private static readonly TimeSpan RetroArchExitGracePeriod = TimeSpan.FromSeconds(2);
        private readonly Stopwatch timeSinceEmulatorReturn = new Stopwatch();

        internal async Task<bool> LaunchGameUriAsync(Uri uri)
        {
            // RetroArch sends launchOnExit before its background handler exits the process.
            if (uri.Scheme.Equals("retroarch", StringComparison.OrdinalIgnoreCase) && timeSinceEmulatorReturn.IsRunning)
            {
                TimeSpan remaining = RetroArchExitGracePeriod - timeSinceEmulatorReturn.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    StartupLog.Write("Waiting for RetroArch shutdown before relaunch: " + remaining.TotalMilliseconds.ToString("F0") + " ms");
                    await Task.Delay(remaining);
                }
            }
            return await Windows.System.Launcher.LaunchUriAsync(uri);
        }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.UnhandledException += OnUnhandledException;
            StartupLog.Write("App constructor: before InitializeComponent");
            this.InitializeComponent();
            StartupLog.Write("App constructor: InitializeComponent complete");
            //bool result = Windows.UI.ViewManagement.ApplicationViewScaling.TrySetDisableLayoutScaling(true);
            this.RequiresPointerMode = Windows.UI.Xaml.ApplicationRequiresPointerMode.WhenRequested;
            this.Suspending += OnSuspending;
            this.Resuming += (sender, args) => InternalStorageProbeLog.Write("Lifecycle: Resuming");
            this.EnteredBackground += (sender, args) => InternalStorageProbeLog.Write("Lifecycle: EnteredBackground");
            this.LeavingBackground += (sender, args) => InternalStorageProbeLog.Write("Lifecycle: LeavingBackground");

            var settings = ApplicationData.Current;
            //If getting LocalSettings before getting LocalCacheFoler, the first time the app starts, it gets restarted.
            //Strange behaviour and possibly some kind of a bug, but this prevents it.
            var localCacheFolder = settings.LocalCacheFolder;

            var localSettings = settings.LocalSettings;

            if (localSettings.Values[SettingsAutoPlayVideo] == null)
            {
                localSettings.Values[SettingsAutoPlayVideo] = false;
            }

            if (localSettings.Values[SettingsLoggingEnabled] == null)
            {
                localSettings.Values[SettingsLoggingEnabled] = false;
            }
            StartupLog.Write("App constructor: settings initialized");
        }

        private void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            StartupLog.Write("UnhandledException: " + e.Exception + "; message=" + e.Message);
            InternalStorageProbeLog.Write("Lifecycle: UnhandledException " + e.Exception);
            e.Handled = true;
            Trace.TraceError(e.Message);
            Trace.TraceError(e.Exception.StackTrace);
        }

        private void OnLaunchedOrActivated(ApplicationExecutionState previousExecutionState, bool prelaunchActivated)
        {
            Windows.UI.ViewManagement.ApplicationView.GetForCurrentView().SetDesiredBoundsMode(Windows.UI.ViewManagement.ApplicationViewBoundsMode.UseCoreWindow);

            //if (AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox")
            {
                this.FocusVisualKind = FocusVisualKind.HighVisibility;
            }

            Frame rootFrame = Window.Current.Content as Frame;

            // Do not repeat app initialization when the Window already has content,
            // just ensure that the window is active
            if (rootFrame == null)
            {
                // Create a Frame to act as the navigation context and navigate to the first page
                rootFrame = new Frame();

                rootFrame.NavigationFailed += OnNavigationFailed;

                if (previousExecutionState == ApplicationExecutionState.Terminated)
                {
                    //TODO: Load state from previously suspended application
                }

                // Place the frame in the current Window
                Window.Current.Content = rootFrame;
            }

            if (prelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    // When the navigation stack isn't restored navigate to the first page,
                    // configuring the new page by passing required information as a navigation
                    // parameter
                    rootFrame.Navigate(typeof(MainPage), null);
                }
                // Ensure the current window is active
                Window.Current.Activate();
            }
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        async protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            try
            {
            StartupLog.Write("OnLaunched: previous=" + e.PreviousExecutionState + " prelaunch=" + e.PrelaunchActivated);
            StartupLog.Write("Probe selection: begin");
            if (await TryShowInternalStorageProbe("OnLaunched previous=" + e.PreviousExecutionState + " prelaunch=" + e.PrelaunchActivated, e.PrelaunchActivated))
            {
                StartupLog.Write("Probe selection: active");
                return;
            }
            StartupLog.Write("Probe selection: inactive");

            if (await TryShowInternalCatalog(e.PrelaunchActivated))
                return;

            DataSourceManager manager = new DataSourceManager();
            StartupLog.Write("External mode: theme preparation begin");
            await manager.PreparelaunchPassFolder();
            StartupLog.Write("External mode: theme preparation complete; settings=" + (CurrentThemeSettings != null));

            string fontPath = ((App)Application.Current).CurrentThemeSettings.GetFontFilePath();
            ((App)Application.Current).Resources["ApplicationFonts"] = "Assets/Fonts/" + Path.GetFileName(fontPath) + "#" + Path.GetFileNameWithoutExtension(fontPath);

            OnLaunchedOrActivated(e.PreviousExecutionState, e.PrelaunchActivated);
            StartupLog.Write("External mode: navigation/activation complete");
            }
            catch (Exception ex) { ShowStartupFailure("OnLaunched", ex); }
        }

        protected override async void OnActivated(IActivatedEventArgs args)
        {
            try
            {
            StartupLog.Write("OnActivated: kind=" + args.Kind + " previous=" + args.PreviousExecutionState);
            var protocol = args as ProtocolActivatedEventArgs;
            bool returnedFromEmulator = protocol != null && protocol.Uri.Scheme.Equals("launchpass", StringComparison.OrdinalIgnoreCase);
            if (returnedFromEmulator)
                timeSinceEmulatorReturn.Restart();
            if (await TryShowInternalStorageProbe("OnActivated kind=" + args.Kind + " previous=" + args.PreviousExecutionState +
                (protocol == null ? "" : " URI=" + protocol.Uri)))
            {
                base.OnActivated(args);
                return;
            }

            if (returnedFromEmulator)
                await GameCoreService.Current.ObserveAsync(false, true);
            if (await TryShowInternalCatalog())
            {
                base.OnActivated(args);
                return;
            }

            OnLaunchedOrActivated(args.PreviousExecutionState, false);
            base.OnActivated(args);
            }
            catch (Exception ex) { ShowStartupFailure("OnActivated", ex); }
        }

        private async System.Threading.Tasks.Task<bool> TryShowInternalCatalog(bool prelaunch = false)
        {
            StartupLog.Write("Catalog selection: marker check begin; latched=" + InternalCatalogMode);
            if (!InternalCatalogMode && await ApplicationData.Current.LocalFolder.TryGetItemAsync("InternalCatalog.enabled") == null)
            {
                StartupLog.Write("Catalog selection: marker missing");
                return false;
            }
            StartupLog.Write("Catalog selection: active");
            InternalCatalogMode = true;
            // No removable-storage discovery or theme preparation in this mode.
            CurrentThemeSettings = new LaunchPassThemeSettings();
            StartupLog.Write("Catalog theme: initialized; prelaunch=" + prelaunch);
            if (prelaunch) return true;
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                StartupLog.Write("Catalog frame: creation begin");
                frame = new Frame();
                frame.NavigationFailed += (sender, args) => StartupLog.Write("Catalog NavigationFailed: page=" + args.SourcePageType + " exception=" + args.Exception);
                Window.Current.Content = frame;
                StartupLog.Write("Catalog frame: attached");
            }
            // Retain the collection/details/navigation state on RetroArch return activation.
            if (frame.Content == null)
            {
                StartupLog.Write("Catalog navigation: begin");
                bool premium = await ApplicationData.Current.LocalFolder.TryGetItemAsync("PremiumBrowser.enabled") is StorageFile;
                if (premium)
                {
                    await ArcadeSessionCoordinator.Current.InitializeAsync();
                    var view = Windows.UI.ViewManagement.ApplicationView.GetForCurrentView();
                    view.SetDesiredBoundsMode(Windows.UI.ViewManagement.ApplicationViewBoundsMode.UseCoreWindow);
                    view.TryEnterFullScreenMode();
                    if (ArcadeSessionCoordinator.Current.Settings.Splash.Enabled)
                    {
                        // Fill the Frame while navigation constructs the premium page.
                        // The OS-owned manifest splash itself cannot be stretched.
                        frame.Background = new Windows.UI.Xaml.Media.ImageBrush
                        {
                            ImageSource = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Branding/StartupSplash.png")),
                            Stretch = Windows.UI.Xaml.Media.Stretch.UniformToFill
                        };
                        Window.Current.Activate();
                    }
                }
                StartupLog.Write("Catalog presentation: " + (premium ? "premium browser" : "existing library"));
                bool navigated = frame.Navigate(premium ? typeof(PremiumBrowserPage) : typeof(InternalCatalogPage));
                StartupLog.Write("Catalog navigation: returned " + navigated);
                if (!navigated) throw new InvalidOperationException("Internal catalog navigation returned false.");
                frame.Background = null;
            }
            InternalCatalogGame.Log("Lifecycle: catalog activation");
            Window.Current.Activate();
            StartupLog.Write("Catalog window: activated");
            return true;
        }

        private bool showingStartupFailure;
        internal void ShowStartupFailure(string stage, Exception exception)
        {
            StartupLog.Write(stage + " FAILED: HRESULT=0x" + exception.HResult.ToString("X8") + " " + exception);
            if (showingStartupFailure) return;
            showingStartupFailure = true;
            try
            {
                Window.Current.Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Margin = new Thickness(48), FontSize = 24, TextWrapping = TextWrapping.Wrap,
                        Text = "LaunchPass startup failed at " + stage + ".\n" + exception.GetType().FullName +
                            " (0x" + exception.HResult.ToString("X8") + "): " + exception.Message +
                            "\nRetrieve LaunchPass LocalState\\Startup.log through Device Portal.\n" +
                            "If absent, check LocalCache\\Startup.log.\nLog write status: " + (StartupLog.LastError ?? "OK")
                    }
                };
                Window.Current.Activate();
            }
            catch (Exception displayError) { StartupLog.Write("Startup failure display FAILED: " + displayError); }
            finally { showingStartupFailure = false; }
        }

        private async System.Threading.Tasks.Task<bool> TryShowInternalStorageProbe(string activation, bool prelaunch = false)
        {
            var config = await InternalStorageProbeConfig.Load();
            if (config == null || !config.Enabled)
                return false;

            InternalStorageProbeLog.Enabled = true;
            InternalStorageProbeLog.Write("Lifecycle: " + activation);
            if (prelaunch)
                return true;

            // Opt-in startup fallback: reach the probe before USB theme or library initialization.
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                Window.Current.Content = frame;
                Window.Current.Activated += (sender, args) => InternalStorageProbeLog.Write("Lifecycle: Window.Activated state=" + args.WindowActivationState);
                Window.Current.VisibilityChanged += (sender, args) => InternalStorageProbeLog.Write("Lifecycle: Window.VisibilityChanged visible=" + args.Visible);
            }
            if (!(frame.Content is InternalStorageProbePage))
                frame.Content = new InternalStorageProbePage(config);
            Window.Current.Activate();
            return true;
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        /// <summary>
        /// Invoked when application execution is being suspended.  Application state is saved
        /// without knowing whether the application will be terminated or resumed with the contents
        /// of memory still intact.
        /// </summary>
        /// <param name="sender">The source of the suspend request.</param>
        /// <param name="e">Details about the suspend request.</param>
        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            InternalStorageProbeLog.Write("Lifecycle: Suspending");
            //TODO: Save application state and stop any background activity
            deferral.Complete();
        }
    }

    // Independent of probe/legacy logging settings; close each append before continuing.
    internal static class StartupLog
    {
        internal static string LastError { get; private set; }
        internal static void Write(string message)
        {
            string line = DateTimeOffset.UtcNow.ToString("O") + " " + message + Environment.NewLine;
            try
            {
                // Preserve the existing first-run LocalCache-before-LocalSettings workaround.
                var cache = ApplicationData.Current.LocalCacheFolder;
                try { File.AppendAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, "Startup.log"), line); }
                catch (Exception localError)
                {
                    File.AppendAllText(Path.Combine(cache.Path, "Startup.log"), line + "LocalState log failed: " + localError + Environment.NewLine);
                }
                LastError = null;
            }
            catch (Exception ex) { LastError = ex.Message; Debug.WriteLine("Startup log failed: " + ex); }
        }
    }
}
