using LaunchPass.Catalog;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace RetroPass
{
    public sealed partial class PremiumBrowserPage
    {
        private LatestArtworkQueue<PremiumArtworkRequest, PremiumArtworkImage> museumQueue;
        private int museumEpoch;
        private bool releasingExperience, startupComplete;
        private readonly PremiumLaunchCover launchCover = new PremiumLaunchCover();
        private ApplicationViewBoundsMode? previousBounds;
        private void EnterExperienceBounds()
        {
            var view = ApplicationView.GetForCurrentView();
            previousBounds = view.DesiredBoundsMode;
            view.SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);
            view.TryEnterFullScreenMode();
        }
        private void LeaveExperienceBounds()
        {
            if (previousBounds.HasValue) ApplicationView.GetForCurrentView().SetDesiredBoundsMode(previousBounds.Value);
            previousBounds = null;
        }
        internal bool ExperienceReady => selection != null && !loading && !launching && !launchCover.Active && !pressure;
        internal bool ExperienceDialog => searching || FindParentDialog(FocusManager.GetFocusedElement() as DependencyObject);
        private void InitializeExperience()
        {
            museumQueue = new LatestArtworkQueue<PremiumArtworkRequest, PremiumArtworkImage>(artwork.LoadAsync, 1);
            ApplyExperienceSettings();
        }
        private async Task LoadExperience()
        {
            await ArcadeSessionCoordinator.Current.InitializeAsync();
            ApplyExperienceSettings();
        }
        private void ApplyExperienceSettings()
        {
            var settings = ArcadeSessionCoordinator.Current.Settings;
            LightingHint.Visibility = ArcadeSessionCoordinator.Current.LightingEnabled ? Visibility.Visible : Visibility.Collapsed;
            BrandTag.Visibility = settings.Branding.Enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!settings.Branding.Enabled)
            {
                ArcadeGradientStart.Color = Color.FromArgb(255,16,25,40);
                ArcadeGradientEnd.Color = Color.FromArgb(255,8,13,24);
                ((SolidColorBrush)Resources["ArcadeNavigationBrush"]).Color = Color.FromArgb(255,115,225,245);
                ((SolidColorBrush)Resources["ArcadeSelectedBrush"]).Color = Color.FromArgb(255,36,79,103);
                ((SolidColorBrush)Resources["ArcadeGlowBrush"]).Color = Colors.Transparent;
            }
            if (new AccessibilitySettings().HighContrast)
                ((SolidColorBrush)Resources["ArcadeGlowBrush"]).Color = Colors.Transparent;
            if (!startupComplete && settings.Splash.Enabled)
            {
                StartupEntrance.Source = new BitmapImage(new Uri("ms-appx:///Assets/Branding/StartupSplash.png")) { DecodePixelWidth = 1920 };
                StartupOverlay.Visibility = Visibility.Visible;
            }
        }
        private void CompleteExperienceStartup()
        {
            startupComplete = true;
            StartupOverlay.Visibility = Visibility.Collapsed;
            StartupEntrance.Source = null;
        }
        private void ExperienceFailed() { CompleteExperienceStartup(); }
        private async Task ShowLaunchCover(string title)
        {
            launchCover.Begin(ArcadeSessionCoordinator.Current.Now);
            LaunchTitle.Text = title;
            LaunchEntrance.Source = new BitmapImage(new Uri("ms-appx:///Assets/Branding/TimsArcadeSplash.png")) { DecodePixelWidth = 1920 };
            LaunchOverlay.Visibility = Visibility.Visible;
            var painted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<object> render = (sender, args) => painted.TrySetResult(true);
            CompositionTarget.Rendering += render;
            try { await Task.WhenAny(painted.Task, Task.Delay(150)); }
            finally { CompositionTarget.Rendering -= render; }
        }
        internal void LaunchActivationCompleted(bool accepted)
        {
            bool wasActive = launchCover.Active;
            launchCover.ActivationCompleted(accepted);
            if (!accepted && wasActive)
            {
                EndLaunchCover("activation declined or failed");
                if (active && foreground && selection != null) RenderCategory();
            }
        }
        private void LaunchCoverForegroundChanged(bool visible)
        {
            if (!visible) launchCover.Leave();
            else if (launchCover.Return()) EndLaunchCover("returned to launcher");
        }
        internal void TickLaunchCover(double now, bool visible)
        {
            if (launchCover.Expire(now, visible))
            {
                EndLaunchCover("activation accepted but no foreground departure observed within 10 seconds");
                ArcadeSessionCoordinator.LaunchFinished(false);
                if (active && selection != null) RenderCategory();
            }
        }
        private void EndLaunchCover(string reason)
        {
            launchCover.Cancel();
            LaunchOverlay.Visibility = Visibility.Collapsed;
            LaunchEntrance.Source = null;
            StartupLog.Write("Premium launch cover: " + reason);
        }
        internal void ExperienceEvent(string name)
        {
            try
            {
                if (name == "attract-enter")
                {
                    detailTimer.Stop(); artworkQueue.Clear(); ResetWheelMotion();
                    ElementCompositionPreview.GetElementVisual(BrowserScene).Opacity = .15f;
                    Museum.Begin(ArcadeSessionCoordinator.Current.Settings.Branding.Enabled);
                }
                else if (name == "attract-game") ShowMuseumGame();
                else if (name == "attract-exit")
                {
                    museumEpoch++; museumQueue.Clear(); Museum.End();
                    ElementCompositionPreview.GetElementVisual(BrowserScene).Opacity = 1;
                    if (!releasingExperience && active && foreground && selection != null)
                    { requestedId = null; RenderSelection(); }
                }
            }
            catch (Exception ex) { StartupLog.Write("Arcade museum: " + ex.Message); }
        }
        private async void ShowMuseumGame()
        {
            int epoch = ++museumEpoch;
            try
            {
                var engine = ArcadeSessionCoordinator.Current.Engine;
                var entry = rows.Select(row => row.Entry).FirstOrDefault(game => game.Id == engine.CurrentGame);
                if (entry == null) entry = games.Values.Select(item => ((InternalCatalogGame)item.game).Entry).FirstOrDefault(game => game.Id == engine.CurrentGame);
                if (entry == null) return;
                var request = new PremiumArtworkRequest(entry, "logo");
                var tasks = museumQueue.SetDemand(new[] { new ArtworkDemand<PremiumArtworkRequest>(request, 0) });
                var image = await tasks[request];
                if (epoch != museumEpoch || !engine.Input.AttractActive || !active) return;
                PremiumMetadata info;
                string details = metadata.TryGetValue(entry.Id, out info) ? string.Join(" · ", new[] { info.Year, info.Manufacturer, info.Genre }.Where(value => !string.IsNullOrWhiteSpace(value))) : "";
                Museum.Display(image?.Image, entry.Title, details, ArcadeSessionCoordinator.Current.Settings.Branding.Enabled && engine.Exhibits % 5 == 0, engine.Exhibits);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { StartupLog.Write("Museum artwork: " + ex.Message); }
        }
        private void ReleaseExperienceArtwork()
        {
            releasingExperience = true;
            try { ArcadeSessionCoordinator.Current.Engine.Reset(ArcadeSessionCoordinator.Current.Now); museumEpoch++; museumQueue.Clear(); Museum.End(); }
            finally { releasingExperience = false; }
        }
    }
}
