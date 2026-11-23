using LaunchPass.Catalog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace RetroPass
{
    public sealed partial class PremiumBrowserPage
    {
        private const double WheelPitch = 82;
        private readonly PremiumArtworkCache artwork = new PremiumArtworkCache();
        private readonly WheelMotion wheelMotion = new WheelMotion();
        private LatestArtworkQueue<PremiumArtworkRequest, PremiumArtworkImage> artworkQueue;
        private PremiumPerformanceDiagnostics performance;
        private ScrollViewer wheelScroll;
        private Visual wheelVisual;
        private bool wheelTranslationUnavailable;
        private bool wheelTranslationStarted;
        private readonly WheelRestoreBudget wheelRestore = new WheelRestoreBudget();
        private readonly DispatcherTimer wheelRestoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        private double wheelOffset;
        private bool foreground = true, subscribed, pressure, focusPending, scrollingMotion, snapWheelOnce;
        private string lastRelease = "startup";
        private string requestedId;
        private string lastCategory;
        private int lastIndex = -1, prefetchDirection = 1;
        private long selectionStarted, prefetchCount, prefetchFailures, staleResults;
        private double logoLatency, backgroundLatency, screenshotLatency;
        private bool logoMeasured, backgroundMeasured, screenshotMeasured;
        private readonly DispatcherTimer detailTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        private int detailGeneration;
        private int backgroundFront, backgroundEpoch;
        private bool backgroundFading, backgroundPending;
        private PremiumArtworkImage pendingBackground;
        private int pendingBackgroundGeneration;

        private void InitializePresentation()
        {
            artworkQueue = new LatestArtworkQueue<PremiumArtworkRequest, PremiumArtworkImage>(artwork.LoadAsync, 2);
            performance = new PremiumPerformanceDiagnostics(PerformanceText, PerformanceSnapshot);
            detailTimer.Tick += (sender, args) => { detailTimer.Stop(); if (CurrentArtwork(detailGeneration)) QueueArtwork(detailGeneration, true); };
            Wheel.Loaded += (sender, args) => QueueWheelRestore();
            Wheel.SizeChanged += (sender, args) => QueueWheelRestore();
            Wheel.BringIntoViewRequested += (sender, args) => args.Handled = true;
            wheelRestoreTimer.Tick += (sender, args) => RestoreWheelTick();
            Wheel.ContainerContentChanging += (sender, args) =>
            {
                if (args.InRecycleQueue) return;
                // Phase zero can still contain the previous category's template.
                // Apply transforms after the new item bindings have been processed.
                args.RegisterUpdateCallback((list, updated) =>
                {
                    if (!updated.InRecycleQueue)
                        ApplyRowVisual(updated.ItemContainer as ListViewItem, updated.ItemIndex, false);
                });
            };
        }
        private void StartPresentation()
        {
            foreground = true;
            RefreshMemoryPolicy();
            if (subscribed) return;
            subscribed = true;
            MemoryManager.AppMemoryUsageIncreased += MemoryIncreased;
            MemoryManager.AppMemoryUsageDecreased += MemoryDecreased;
            MemoryManager.AppMemoryUsageLimitChanging += MemoryLimitChanging;
            Application.Current.EnteredBackground += PresentationEnteredBackground;
            Application.Current.LeavingBackground += PresentationLeavingBackground;
            Application.Current.Suspending += PresentationSuspending;
        }
        private void StopPresentation()
        {
            wheelRestore.Cancel(); wheelRestoreTimer.Stop();
            if (subscribed)
            {
                MemoryManager.AppMemoryUsageIncreased -= MemoryIncreased;
                MemoryManager.AppMemoryUsageDecreased -= MemoryDecreased;
                MemoryManager.AppMemoryUsageLimitChanging -= MemoryLimitChanging;
                Application.Current.EnteredBackground -= PresentationEnteredBackground;
                Application.Current.LeavingBackground -= PresentationLeavingBackground;
                Application.Current.Suspending -= PresentationSuspending;
                subscribed = false;
            }
            performance.Stop(); ReleaseArtwork("navigation");
        }
        private void PresentationEnteredBackground(object sender, EnteredBackgroundEventArgs e) { foreground = false; LaunchCoverForegroundChanged(false); ReleaseArtwork("background"); }
        private void PresentationLeavingBackground(object sender, LeavingBackgroundEventArgs e)
        {
            foreground = true;
            LaunchCoverForegroundChanged(true);
            if (active && selection != null && !searching) RenderCategory();
        }
        private void PresentationSuspending(object sender, SuspendingEventArgs e) { ReleaseArtwork("suspending"); }
        private void MemoryIncreased(object sender, object e)
        {
            if (MemoryManager.AppMemoryUsageLevel == AppMemoryUsageLevel.High || MemoryManager.AppMemoryUsageLevel == AppMemoryUsageLevel.OverLimit)
                OnPresentationThread(() => { pressure = true; artwork.SetPressure(true); ReleaseArtwork("memory pressure"); });
        }
        private void MemoryLimitChanging(object sender, AppMemoryUsageLimitChangingEventArgs e)
        {
            if (e.NewLimit < e.OldLimit) OnPresentationThread(() => { pressure = true; artwork.SetPressure(true); ReleaseArtwork("memory limit reduced"); });
            else if (e.NewLimit > e.OldLimit) OnPresentationThread(() => RefreshMemoryPolicy());
        }
        private void MemoryDecreased(object sender, object e)
        {
            if (MemoryManager.AppMemoryUsageLevel == AppMemoryUsageLevel.Low)
                OnPresentationThread(() => { bool resume = pressure; RefreshMemoryPolicy(); if (resume && active && foreground && selection != null) { requestedId = null; RenderSelection(); } });
        }
        private void RefreshMemoryPolicy()
        {
            pressure = MemoryManager.AppMemoryUsageLevel == AppMemoryUsageLevel.High || MemoryManager.AppMemoryUsageLevel == AppMemoryUsageLevel.OverLimit;
            artwork.SetPressure(pressure);
        }
        private async void OnPresentationThread(Action action)
        {
            try { if (Dispatcher.HasThreadAccess) action(); else await Dispatcher.RunAsync(CoreDispatcherPriority.High, () => { if (active) action(); }); }
            catch (Exception ex) { StartupLog.Write("Premium presentation lifecycle: " + ex.Message); }
        }
        private string PerformanceSnapshot()
        {
            return "game=" + selection?.Current?.Id + " foreground=" + foreground + " release=" + lastRelease + "; " + artwork.Snapshot + "; queue run/wait=" + artworkQueue.Running + "/" + artworkQueue.Pending +
                " dedup=" + artworkQueue.Deduplicated + " canceled=" + artworkQueue.Canceled + " preempted=" + artworkQueue.Preempted +
                "; prefetch requested/fail=" + prefetchCount + "/" + prefetchFailures + " stale ignored=" + staleResults +
                "; assign latency logo/bg/shot ms=" + logoLatency.ToString("F1") + "/" + backgroundLatency.ToString("F1") + "/" + screenshotLatency.ToString("F1") +
                "; app MiB/limit=" + (MemoryManager.AppMemoryUsage / 1048576.0).ToString("F1") + "/" + (MemoryManager.AppMemoryUsageLimit / 1048576.0).ToString("F1") + " pressure=" + pressure;
        }
        private void RequestArtwork()
        {
            if (ArcadeSessionCoordinator.Current.Engine.Input.AttractActive) return;
            if (!active || !foreground || selection?.Current == null) return;
            if (requestedId == selection.Current.Id && clock.ElapsedMilliseconds - selectionStarted < 30000) return;
            if (lastCategory == selection.Current.PlatformId && lastIndex != selection.SelectedIndex)
                prefetchDirection = selection.SelectedIndex < lastIndex ? -1 : 1;
            else if (lastCategory != selection.Current.PlatformId) prefetchDirection = 1;
            lastCategory = selection.Current.PlatformId; lastIndex = selection.SelectedIndex;
            requestedId = selection.Current.Id; selectionStarted = clock.ElapsedMilliseconds;
            int version = ++generation;
            logoMeasured = backgroundMeasured = screenshotMeasured = false;
            logoLatency = backgroundLatency = screenshotLatency = -1;
            backgroundPending = false; pendingBackground = null;
            var entry = selection.Current;
            var logo = new PremiumArtworkRequest(entry, "logo");
            var background = new PremiumArtworkRequest(entry, "background");
            var screenshot = new PremiumArtworkRequest(entry, "screenshot");
            PremiumArtworkImage ready;
            if (artwork.TryCached(logo, out ready)) ApplyVisibleImage("logo", ready, version);
            else { HeroLogo.Source = rows[selection.SelectedIndex].Logo; HeroTitle.Visibility = HeroLogo.Source == null ? Visibility.Visible : Visibility.Collapsed; }
            if (artwork.TryCached(screenshot, out ready)) ApplyVisibleImage("screenshot", ready, version);
            else { Screenshot.Source = null; ScreenshotFallback.Visibility = Visibility.Visible; }
            if (artwork.TryCached(background, out ready)) ApplyVisibleImage("background", ready, version);
            QueueArtwork(version, false);
            // Let marquee prefetch run during fast joystick movement. Heavy visible assets
            // take precedence as soon as the latest selection settles for 90 milliseconds.
            detailTimer.Stop(); detailGeneration = version; detailTimer.Start();
        }
        private void QueueArtwork(int version, bool details)
        {
            if (!CurrentArtwork(version) || selection?.Current == null) return;
            var entry = selection.Current;
            var logo = new PremiumArtworkRequest(entry, "logo");
            var background = new PremiumArtworkRequest(entry, "background");
            var screenshot = new PremiumArtworkRequest(entry, "screenshot");
            var requests = new List<ArtworkDemand<PremiumArtworkRequest>> { new ArtworkDemand<PremiumArtworkRequest>(logo, 0) };
            bool requestBackground = details || artworkQueue.HasRequest(background);
            bool requestScreenshot = details || artworkQueue.HasRequest(screenshot);
            if (requestBackground) requests.Add(new ArtworkDemand<PremiumArtworkRequest>(background, 1));
            if (requestScreenshot) requests.Add(new ArtworkDemand<PremiumArtworkRequest>(screenshot, 2));
            PremiumArtworkImage ready;
            var neighbors = pressure ? new int[0] : WheelMotion.Neighbors(selection.SelectedIndex, rows.Count);
            foreach (int index in neighbors)
            {
                var request = new PremiumArtworkRequest(rows[index].Entry, "logo");
                requests.Add(new ArtworkDemand<PremiumArtworkRequest>(request, 10 + Math.Abs(index - selection.SelectedIndex)));
                if (artwork.TryCached(request, out ready)) rows[index].Logo = ready.Image;
            }
            // Heavy look-ahead is limited to one likely next game, fitting current+next
            // background/screenshot in their tighter budgets instead of thrashing +/-3.
            int lookAhead = selection.SelectedIndex + prefetchDirection;
            var heavy = new List<PremiumArtworkRequest>();
            if (details && !pressure && lookAhead >= 0 && lookAhead < rows.Count)
            {
                heavy.Add(new PremiumArtworkRequest(rows[lookAhead].Entry, "background"));
                heavy.Add(new PremiumArtworkRequest(rows[lookAhead].Entry, "screenshot"));
                requests.Add(new ArtworkDemand<PremiumArtworkRequest>(heavy[0], 20));
                requests.Add(new ArtworkDemand<PremiumArtworkRequest>(heavy[1], 21));
            }
            var tasks = artworkQueue.SetDemand(requests);
            ObserveVisible(tasks[logo], "logo", version);
            if (requestBackground) ObserveVisible(tasks[background], "background", version);
            if (requestScreenshot) ObserveVisible(tasks[screenshot], "screenshot", version);
            foreach (int index in neighbors)
            {
                prefetchCount++;
                ObserveNeighbor(tasks[new PremiumArtworkRequest(rows[index].Entry, "logo")], rows[index], version);
            }
            foreach (var request in heavy) { prefetchCount++; ObserveSpeculative(tasks[request], version); }
        }
        private bool CurrentArtwork(int version) => active && foreground && version == generation;
        private async void ObserveVisible(Task<PremiumArtworkImage> task, string role, int version)
        {
            try
            {
                var result = await task;
                if (!CurrentArtwork(version)) { staleResults++; return; }
                ApplyVisibleImage(role, result, version);
            }
            catch (OperationCanceledException) { staleResults++; }
            catch (Exception ex) { StartupLog.Write("Premium artwork request: " + ex.Message); }
        }
        private async void ObserveNeighbor(Task<PremiumArtworkImage> task, PremiumWheelItem row, int version)
        {
            try
            {
                var result = await task;
                if (!CurrentArtwork(version)) { staleResults++; return; }
                if (result == null) prefetchFailures++;
                row.Logo = result?.Image;
            }
            catch (OperationCanceledException) { staleResults++; }
            catch (Exception) { prefetchFailures++; }
        }
        private async void ObserveSpeculative(Task<PremiumArtworkImage> task, int version)
        {
            try
            {
                var result = await task;
                if (!CurrentArtwork(version)) staleResults++;
                else if (result == null) prefetchFailures++;
            }
            catch (OperationCanceledException) { staleResults++; }
            catch (Exception) { prefetchFailures++; }
        }
        private void ApplyVisibleImage(string role, PremiumArtworkImage result, int version)
        {
            if (!CurrentArtwork(version)) return;
            double elapsed = clock.ElapsedMilliseconds - selectionStarted;
            if (role == "logo")
            {
                if (!ReferenceEquals(HeroLogo.Source, result?.Image)) HeroLogo.Source = result?.Image;
                HeroTitle.Visibility = result == null ? Visibility.Visible : Visibility.Collapsed;
                rows[selection.SelectedIndex].Logo = result?.Image;
                if (!logoMeasured && result != null) { logoLatency = elapsed; logoMeasured = true; }
            }
            else if (role == "screenshot")
            {
                if (!ReferenceEquals(Screenshot.Source, result?.Image)) Screenshot.Source = result?.Image;
                ScreenshotFallback.Visibility = result == null ? Visibility.Visible : Visibility.Collapsed;
                if (!screenshotMeasured && result != null) { screenshotLatency = elapsed; screenshotMeasured = true; }
            }
            else ShowBackground(result, version);
        }
        private void ReleaseArtwork(string reason)
        {
            ReleaseExperienceArtwork();
            lastRelease = reason;
            generation++; requestedId = null;
            detailTimer.Stop();
            artworkQueue.Clear(); artwork.Clear();
            backgroundEpoch++; backgroundFading = backgroundPending = false; pendingBackground = null;
            foreach (var layer in new[] { BackgroundLayerA, BackgroundLayerB }) ElementCompositionPreview.GetElementVisual(layer).StopAnimation("Opacity");
            BackgroundLayerA.Opacity = 1; BackgroundLayerB.Opacity = 0; backgroundFront = 0;
            Backdrop.Source = BackdropNext.Source = HeroLogo.Source = Screenshot.Source = null;
            HeroTitle.Visibility = ScreenshotFallback.Visibility = Visibility.Visible;
            foreach (var row in rows) row.Logo = null;
            ResetWheelMotion();
        }
        private void ShowBackground(PremiumArtworkImage result, int version)
        {
            if (!CurrentArtwork(version)) return;
            var images = new[] { Backdrop, BackdropNext };
            if (backgroundFading)
            {
                if (ReferenceEquals(images[1 - backgroundFront].Source, result?.Image)) { RecordBackgroundLatency(result); return; }
                // One latest replacement, never a backlog or a mid-fade source replacement.
                pendingBackground = result; pendingBackgroundGeneration = version; backgroundPending = true; return;
            }
            if (ReferenceEquals(images[backgroundFront].Source, result?.Image)) { RecordBackgroundLatency(result); return; }
            if (pressure) { images[backgroundFront].Source = result?.Image; RecordBackgroundLatency(result); return; }
            var layers = new[] { BackgroundLayerA, BackgroundLayerB };
            int old = backgroundFront, next = 1 - old, token = backgroundEpoch;
            images[next].Source = result?.Image;
            RecordBackgroundLatency(result);
            layers[next].Opacity = 0;
            var incoming = ElementCompositionPreview.GetElementVisual(layers[next]);
            var outgoing = ElementCompositionPreview.GetElementVisual(layers[old]);
            var compositor = incoming.Compositor;
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            backgroundFading = true;
            var fadeIn = compositor.CreateScalarKeyFrameAnimation();
            fadeIn.InsertKeyFrame(0, 0); fadeIn.InsertKeyFrame(1, 1); fadeIn.Duration = TimeSpan.FromMilliseconds(230);
            var fadeOut = compositor.CreateScalarKeyFrameAnimation();
            fadeOut.InsertKeyFrame(0, 1); fadeOut.InsertKeyFrame(1, 0); fadeOut.Duration = fadeIn.Duration;
            incoming.StartAnimation("Opacity", fadeIn); outgoing.StartAnimation("Opacity", fadeOut);
            batch.Completed += (sender, args) =>
            {
                try
                {
                    if (token != backgroundEpoch) return;
                    layers[old].Opacity = 0; layers[next].Opacity = 1;
                    images[old].Source = null; backgroundFront = next; backgroundFading = false;
                    if (backgroundPending)
                    {
                        var latest = pendingBackground; int latestVersion = pendingBackgroundGeneration;
                        pendingBackground = null; backgroundPending = false;
                        if (CurrentArtwork(latestVersion)) ShowBackground(latest, latestVersion);
                    }
                }
                catch (Exception ex) { StartupLog.Write("Premium background transition: " + ex.Message); }
                finally { batch.Dispose(); }
            };
            batch.End();
        }
        private void RecordBackgroundLatency(PremiumArtworkImage result)
        {
            if (!backgroundMeasured && result != null) { backgroundLatency = clock.ElapsedMilliseconds - selectionStarted; backgroundMeasured = true; }
        }
        private static T VisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T value) return value;
                var found = VisualChild<T>(child); if (found != null) return found;
            }
            return null;
        }
        private void InitializeWheel()
        {
            if (wheelScroll == null)
            {
                wheelScroll = VisualChild<ScrollViewer>(Wheel);
                if (wheelScroll == null) return;
                wheelScroll.ViewChanged += WheelViewChanged;
            }
            var presenter = wheelScroll.Content as UIElement;
            if (presenter != null && wheelVisual == null && !wheelTranslationUnavailable)
            {
                try
                {
                    ElementCompositionPreview.SetIsTranslationEnabled(presenter, true);
                    wheelVisual = ElementCompositionPreview.GetElementVisual(presenter);
                }
                catch (ArgumentException ex) { DisableWheelTranslation(ex); }
            }
            double padding = Math.Max(0, (Wheel.ActualHeight - WheelPitch) / 2);
            if (Math.Abs(WheelTopSpacer.Height - padding) > 0.1 || double.IsNaN(WheelTopSpacer.Height)) WheelTopSpacer.Height = padding;
            if (Math.Abs(WheelBottomSpacer.Height - padding) > 0.1 || double.IsNaN(WheelBottomSpacer.Height)) WheelBottomSpacer.Height = padding;
        }
        private void QueueWheelRestore()
        {
            wheelRestore.Begin(selection?.Current?.Id);
            if (wheelRestore.Pending) wheelRestoreTimer.Start();
        }
        private void RestoreWheelTick()
        {
            // Never mutate scroll/layout from LayoutUpdated, ViewChanged or a
            // container-realization callback. Yield to input between bounded ticks.
            try
            {
                if (!active || !foreground || searching || launchCover.Active || !Wheel.IsLoaded ||
                    !wheelRestore.Take(selection?.Current?.Id))
                {
                    wheelRestore.Cancel(); return;
                }
                InitializeWheel();
                SynchronizeWheelSelection();
                if (wheelScroll != null && selection?.Current != null)
                {
                    // Fixed 82-DIP rows and symmetric header/footer spacers make
                    // this independent of animated/transformed container coordinates.
                    double target = Math.Min(wheelScroll.ScrollableHeight, selection.SelectedIndex * WheelPitch);
                    if (Math.Abs(target - wheelScroll.VerticalOffset) > 1)
                    {
                        scrollingMotion = false;
                        wheelScroll.ChangeView(null, target, null, true);
                    }
                    if (focusPending) FocusRealizedSelection();
                    UpdateRowVisuals(false);
                }
            }
            catch (Exception ex)
            {
                wheelRestore.Cancel();
                StartupLog.Write("Premium wheel restore failed: " + ex);
            }
            finally
            {
                // Failure to center must never block input or leave startup covered.
                if (!loading) CompleteExperienceStartup();
                if (!wheelRestore.Pending)
                {
                    wheelRestoreTimer.Stop();
                    StartupLog.Write("Premium wheel restore ended: game=" + selection?.Current?.Id +
                        "; index=" + selection?.SelectedIndex + "; rows=" + rows.Count +
                        "; attempts=" + wheelRestore.Attempts + "; offset=" + wheelScroll?.VerticalOffset +
                        "; extent=" + wheelScroll?.ScrollableHeight);
                }
            }
        }
        private void CenterWheel(bool animate = true)
        {
            if (!active || selection?.Current == null) return;
            InitializeWheel();
            if (wheelScroll != null)
            {
                scrollingMotion = animate && !snapWheelOnce; snapWheelOnce = false;
                wheelScroll.ChangeView(null, Math.Min(wheelScroll.ScrollableHeight, selection.SelectedIndex * WheelPitch), null, true);
            }
            focusPending = true; FocusRealizedSelection();
            UpdateRowVisuals(animate);
        }
        private void FocusRealizedSelection()
        {
            if (!active || searching || selection?.Current == null) { focusPending = false; return; }
            var container = Wheel.ContainerFromIndex(selection.SelectedIndex) as ListViewItem;
            if (container == null || selection.SelectedIndex >= rows.Count ||
                !ReferenceEquals(container.Content, rows[selection.SelectedIndex])) return;
            focusPending = false; container.Focus(FocusState.Programmatic);
        }
        private void WheelViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
        {
            double delta = wheelScroll.VerticalOffset - wheelOffset;
            wheelOffset = wheelScroll.VerticalOffset;
            if (wheelVisual != null && Math.Abs(delta) > 0.01)
            {
                double start = scrollingMotion ? wheelMotion.Retarget(delta, clock.Elapsed.TotalMilliseconds, WheelPitch * 2) : 0;
                try
                {
                    var offset = new Vector3(0, (float)start, 0);
                    if (wheelTranslationStarted) wheelVisual.StopAnimation("Translation");
                    wheelVisual.Properties.InsertVector3("Translation", offset);
                    var animation = wheelVisual.Compositor.CreateVector3KeyFrameAnimation();
                    animation.InsertKeyFrame(0, offset);
                    animation.InsertKeyFrame(1, Vector3.Zero, wheelVisual.Compositor.CreateCubicBezierEasingFunction(new Vector2(1f / 3, 1), new Vector2(2f / 3, 1)));
                    animation.Duration = TimeSpan.FromMilliseconds(WheelMotion.DurationMs);
                    wheelVisual.StartAnimation("Translation", animation);
                    wheelTranslationStarted = true;
                }
                catch (ArgumentException ex) { DisableWheelTranslation(ex); }
            }
            UpdateRowVisuals(scrollingMotion);
        }
        private void ResetWheelMotion()
        {
            wheelMotion.Reset(); scrollingMotion = false; snapWheelOnce = true;
            if (wheelVisual != null && wheelTranslationStarted)
            {
                wheelTranslationStarted = false;
                try
                {
                    wheelVisual.StopAnimation("Translation");
                    wheelVisual.Properties.InsertVector3("Translation", Vector3.Zero);
                }
                catch (ArgumentException ex) { DisableWheelTranslation(ex); }
            }
            if (wheelScroll != null) wheelOffset = wheelScroll.VerticalOffset;
        }
        private void DisableWheelTranslation(ArgumentException error)
        {
            wheelVisual = null;
            wheelTranslationStarted = false;
            wheelTranslationUnavailable = true;
            StartupLog.Write("Premium browser: wheel translation disabled: " + error.Message);
        }
        private void UpdateRowVisuals(bool animate)
        {
            if (selection == null) return;
            for (int i = Math.Max(0, selection.SelectedIndex - 6); i < Math.Min(rows.Count, selection.SelectedIndex + 7); i++)
                ApplyRowVisual(Wheel.ContainerFromIndex(i) as ListViewItem, i, animate);
        }
        private void ApplyRowVisual(ListViewItem container, int index, bool animate)
        {
            if (index < 0 || index >= rows.Count || !ReferenceEquals(container?.Content, rows[index])) return;
            var root = container?.ContentTemplateRoot as FrameworkElement;
            if (root == null || selection == null) return;
            var visual = ElementCompositionPreview.GetElementVisual(root);
            visual.CenterPoint = new Vector3((float)root.ActualWidth / 2, (float)root.ActualHeight / 2, 0);
            if (!ReferenceEquals(root.Tag, this))
            {
                root.Tag = this;
                root.SizeChanged += (sender, args) =>
                {
                    var item = (FrameworkElement)sender;
                    ElementCompositionPreview.GetElementVisual(item).CenterPoint = new Vector3((float)item.ActualWidth / 2, (float)item.ActualHeight / 2, 0);
                };
            }
            int distance = Math.Abs(index - selection.SelectedIndex);
            float scale = distance == 0 ? 1 : distance == 1 ? 0.86f : distance == 2 ? 0.75f : 0.68f;
            float opacity = distance == 0 ? 1 : distance == 1 ? 0.82f : distance == 2 ? 0.63f : 0.45f;
            if (!animate) { visual.StopAnimation("Scale"); visual.StopAnimation("Opacity"); visual.Scale = new Vector3(scale, scale, 1); visual.Opacity = opacity; return; }
            var size = visual.Compositor.CreateVector3KeyFrameAnimation();
            size.InsertExpressionKeyFrame(0, "this.StartingValue"); size.InsertKeyFrame(1, new Vector3(scale, scale, 1)); size.Duration = TimeSpan.FromMilliseconds(150);
            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertExpressionKeyFrame(0, "this.StartingValue"); fade.InsertKeyFrame(1, opacity); fade.Duration = size.Duration;
            visual.StartAnimation("Scale", size); visual.StartAnimation("Opacity", fade);
        }
    }
}
