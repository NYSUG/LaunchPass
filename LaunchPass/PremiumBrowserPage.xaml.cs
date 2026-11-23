using LaunchPass.Catalog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Graphics.Imaging;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace RetroPass
{
    public sealed class PremiumWheelItem : INotifyPropertyChanged
    {
        public CatalogEntry Entry { get; private set; }
        public string Title => Entry.Title;
        private BitmapImage logo;
        private bool selected;
        public BitmapImage Logo { get => logo; set { if (ReferenceEquals(logo, value)) return; logo = value; Changed(nameof(Logo)); Changed(nameof(TitleVisibility)); } }
        public Visibility TitleVisibility => logo == null ? Visibility.Visible : Visibility.Collapsed;
        public double TitleSize => 23;
        private readonly Brush outline, fill, glow;
        private static readonly Brush transparent = new SolidColorBrush(Windows.UI.Colors.Transparent);
        public Brush Outline => selected ? outline : transparent;
        public Brush Fill => selected ? fill : transparent;
        public Brush Glow => selected ? glow : transparent;
        public PremiumWheelItem(CatalogEntry entry, Brush outline, Brush fill, Brush glow) { Entry = entry; this.outline=outline;this.fill=fill;this.glow=glow; }
        public void Select(bool value) { if (selected == value) return; selected = value; Changed(nameof(Outline)); Changed(nameof(Fill)); Changed(nameof(Glow)); }
        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public sealed partial class PremiumBrowserPage : Page
    {
        private PremiumSelection selection;
        private readonly Dictionary<string, PlaylistItem> games = new Dictionary<string, PlaylistItem>(StringComparer.OrdinalIgnoreCase);
        private readonly PlaylistPlayLater playLater = new PlaylistPlayLater();
        private IReadOnlyDictionary<string, PremiumMetadata> metadata = new Dictionary<string, PremiumMetadata>();
        private List<PremiumWheelItem> rows = new List<PremiumWheelItem>();
        private readonly CabinetRepeatGate gamepadGate = new CabinetRepeatGate();
        private readonly CabinetInputMap gamepadTiming = CabinetInputMap.Defaults();
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private IDisposable cabinetInput;
        private bool active, loading, syncing, launching, searching;
        private int generation;
        private GameCoreSettingsDialog coreSettingsDialog;
        private bool coreSettingsOpening, cancelCoreSettingsOpen;

        public PremiumBrowserPage()
        {
            StartupLog.Write("Premium browser: constructor entered");
            InitializeComponent();
            InitializePresentation();
            InitializeExperience();
            AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(GamepadDown), true);
            AddHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler(GamepadUp), true);
            Loaded += PageLoaded;
            StartupLog.Write("Premium browser: XAML initialized");
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            active = true;
            EnterExperienceBounds();
            cabinetInput?.Dispose();
            cabinetInput = CabinetInput.Attach(this, HandleAction);
            SystemNavigationManager.GetForCurrentView().BackRequested += BackRequested;
            Window.Current.Activated += WindowActivated;
            StartPresentation();
            ArcadeSessionCoordinator.Current.Attach(this);
            if (selection != null) RenderCategory();
        }
        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            active = false;
            EndLaunchCover("page navigation");
            LeaveExperienceBounds();
            ArcadeSessionCoordinator.Current.Detach(this);
            cabinetInput?.Dispose(); cabinetInput = null;
            SystemNavigationManager.GetForCurrentView().BackRequested -= BackRequested;
            Window.Current.Activated -= WindowActivated;
            gamepadGate.Reset();
            StopPresentation();
            base.OnNavigatedFrom(e);
        }
        private async void PageLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadExperience();
                await InputDiagnostics.RefreshAsync(PremiumRoot);
                await performance.RefreshAsync();
                if (!active) { performance.Stop(); return; }
                if (selection != null) { RenderCategory(); return; }
                if (loading) return;
                loading = true;
                games.Clear();
                StartupLog.Write("Premium browser: catalog read begin");
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync("InternalCatalog.json");
                var entries = CatalogPresentationOrder.Sort(CatalogReader.Parse(await FileIO.ReadTextAsync(file)));
                GameCoreService.Current.RegisterCatalog(entries);
                foreach (var group in entries.GroupBy(entry => entry.PlatformId, StringComparer.OrdinalIgnoreCase))
                {
                    var platform = new Platform { Name = group.First().PlatformTitle, EmulatorType = Platform.EEmulatorType.retroarch };
                    var playlist = new Playlist { Name = platform.Name };
                    foreach (var entry in group)
                    {
                        var item = new PlaylistItem { playlist = playlist, game = new InternalCatalogGame(entry, platform) };
                        playlist.PlaylistItems.Add(item); games.Add(entry.Id, item);
                    }
                }
                try
                {
                    var sidecar = await ApplicationData.Current.LocalFolder.TryGetItemAsync("Presentation.json") as StorageFile;
                    if (sidecar != null) metadata = PremiumMetadataDocument.Parse(await FileIO.ReadTextAsync(sidecar));
                }
                catch (Exception ex) { StartupLog.Write("Premium browser: optional metadata ignored: " + ex.Message); }
                selection = new PremiumSelection(entries);
                try
                {
                    var settings = ApplicationData.Current.LocalSettings.Values;
                    string first = selection.Current?.Id;
                    foreach (var group in entries.GroupBy(entry => entry.PlatformId, StringComparer.OrdinalIgnoreCase))
                    {
                        object remembered;
                        if (settings.TryGetValue("PremiumBrowser.Category." + group.Key.ToLowerInvariant(), out remembered) && group.Any(entry => string.Equals(entry.Id, remembered as string, StringComparison.OrdinalIgnoreCase))) selection.SelectGame((string)remembered);
                    }
                    object last;
                    if (!settings.TryGetValue("PremiumBrowser.SelectedGame", out last) || !selection.SelectGame(last as string)) selection.SelectGame(first);
                }
                catch (Exception ex) { StartupLog.Write("Premium browser: selection restore skipped: " + ex.Message); }
                StartupLog.Write("Premium browser: catalog ready; games=" + entries.Count);
                ArcadeSessionCoordinator.Current.Engine.SetGames(games.Keys);
                if (active) RenderCategory();
            }
            catch (Exception ex)
            {
                ExperienceFailed();
                StartupLog.Write("Premium browser: load failed; using existing recovery page: " + ex);
                if (active) Frame.Navigate(typeof(InternalCatalogPage));
            }
            finally
            {
                loading = false;
                if (active && selection?.Current != null) QueueWheelRestore();
                else CompleteExperienceStartup();
            }
        }
        private void RenderCategory()
        {
            if (!active || selection == null) return;
            syncing = true;
            if (rows.Count == 0 || !string.Equals(rows[0].Entry.PlatformId, selection.Current?.PlatformId, StringComparison.OrdinalIgnoreCase))
            {
                rows = selection.Games.Select(entry => new PremiumWheelItem(entry, (Brush)Resources["ArcadeNavigationBrush"], (Brush)Resources["ArcadeSelectedBrush"], (Brush)Resources["ArcadeGlowBrush"])).ToList();
                Wheel.ItemsSource = rows;
                ResetWheelMotion();
            }
            syncing = false;
            RenderSelection();
            QueueWheelRestore();
        }
        private void RenderSelection()
        {
            if (!active || selection?.Current == null) return;
            SynchronizeWheelSelection();
            for (int i = 0; i < rows.Count; i++) rows[i].Select(i == selection.SelectedIndex);
            var entry = selection.Current;
            try
            {
                ApplicationData.Current.LocalSettings.Values["PremiumBrowser.SelectedGame"] = entry.Id;
                ApplicationData.Current.LocalSettings.Values["PremiumBrowser.Category." + entry.PlatformId.ToLowerInvariant()] = entry.Id;
            }
            catch (Exception ex) { StartupLog.Write("Premium browser: selection save skipped: " + ex.Message); }
            CategoryText.Text = "◀ " + entry.PlatformTitle.ToUpperInvariant() + " ▶";
            PositionText.Text = (selection.SelectedIndex + 1) + " / " + selection.Games.Count + "   ·   CATEGORY " + (selection.CategoryIndex + 1) + " / " + selection.CategoryCount;
            HeroTitle.Text = GameTitle.Text = FallbackTitle.Text = entry.Title;
            PremiumMetadata info;
            MetadataText.Text = metadata.TryGetValue(entry.Id, out info) ? string.Join("  ·  ", new[] { info.Year, info.Manufacturer, info.Genre }.Where(s => !string.IsNullOrWhiteSpace(s))) : "";
            StatusText.Text = "Ready · Y / configured Search · View / configured GameSettings opens core settings";
            for (int i = 0; i < rows.Count; i++) if (Math.Abs(i - selection.SelectedIndex) > 3 || (pressure && i != selection.SelectedIndex)) rows[i].Logo = null;
            FocusSelection();
            RequestArtwork();
            ArcadeSessionCoordinator.Current.SelectionChanged(entry.Id);
        }
        private void FocusSelection()
        {
            CenterWheel();
        }
        private void Wheel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // XAML focus/virtualization can select a different row during realization.
            // Cabinet actions, search and ItemClick own the model, never layout events.
            if (!syncing) SynchronizeWheelSelection();
        }
        private void SynchronizeWheelSelection()
        {
            if (selection == null || selection.SelectedIndex < 0 || selection.SelectedIndex >= rows.Count) return;
            syncing = true;
            try
            {
                var expected = rows[selection.SelectedIndex];
                if (!ReferenceEquals(Wheel.SelectedItem, expected)) Wheel.SelectedItem = expected;
            }
            finally { syncing = false; }
        }
        private void Wheel_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (launchCover.Active || ArcadeSessionCoordinator.BlockActions) return;
            var row = e.ClickedItem as PremiumWheelItem;
            if (selection == null || row == null) return;
            selection.SelectGame(row.Entry.Id); RenderSelection(); HandleAction(CabinetAction.Select);
        }
        private async void HandleAction(CabinetAction action)
        {
            try
            {
                if (action == CabinetAction.Back && coreSettingsOpening) { cancelCoreSettingsOpen = true; return; }
                if (action == CabinetAction.Back && coreSettingsDialog != null) { coreSettingsDialog.CancelFromBack(); return; }
                if (!active || loading || launching || searching || launchCover.Active || ArcadeSessionCoordinator.BlockActions) return;
                if (action == CabinetAction.Back) { Frame.Navigate(typeof(InternalCatalogPage)); return; }
                if (selection?.Current == null) return;
                if (action == CabinetAction.GameSettings) { await OpenGameSettings(); return; }
                if (action == CabinetAction.Select)
                {
                    launching = true;
                    bool completed = false;
                    try
                    {
                        await ShowLaunchCover(selection.Current.Title);
                        ReleaseArtwork("launch");
                        await GameDetailsPage.StartContent(games[selection.Current.Id]);
                        completed = true;
                    }
                    finally
                    {
                        launching = false;
                        if (!completed) EndLaunchCover("launch error");
                        if (!launchCover.Active && active && foreground && selection != null) RenderCategory();
                    }
                    return;
                }
                if (action == CabinetAction.Search) { await Search(); return; }
                if (action == CabinetAction.PreviousCategory || action == CabinetAction.NextCategory)
                {
                    selection.MoveCategory(action == CabinetAction.PreviousCategory ? -1 : 1); RenderCategory(); return;
                }
                if (action == CabinetAction.PreviousGame || action == CabinetAction.NextGame)
                    selection.MoveGame(action == CabinetAction.PreviousGame ? -1 : 1);
                else if (action == CabinetAction.PreviousLetter || action == CabinetAction.NextLetter)
                    selection.Select(CabinetNavigation.Letter(selection.Games.Select(entry => entry.Title).ToArray(), selection.SelectedIndex, action == CabinetAction.PreviousLetter ? -1 : 1));
                else return;
                RenderSelection();
            }
            catch (Exception ex) { StartupLog.Write("Premium browser: action failed: " + ex); StatusText.Text = "Action failed — retrieve Startup.log; Back opens the existing library."; }
        }
        private async Task Search()
        {
            if (searching) return;
            searching = true;
            SearchPage.Instance.selectedPlaylistItem = null;
            try
            {
                SearchPage.Instance.OnNavigatedTo(games.Values.Select(item => item.playlist).Distinct().ToList());
                await SearchPage.Instance.ShowAsync();
            }
            finally { SearchPage.Instance.OnNavigatedFrom(); searching = false; }
            var result = SearchPage.Instance.selectedPlaylistItem?.game as InternalCatalogGame;
            if (result != null) selection.SelectGame(result.Entry.Id);
            if (active) RenderCategory();
        }
        private async Task OpenGameSettings()
        {
            if (searching || coreSettingsOpening || coreSettingsDialog != null || selection?.Current == null) return;
            searching = coreSettingsOpening = true; cancelCoreSettingsOpen = false;
            bool test = false;
            try
            {
                var entry = selection.Current;
                var context = await GameCoreService.Current.LoadAsync(entry);
                if (!active || cancelCoreSettingsOpen) return;
                coreSettingsDialog = new GameCoreSettingsDialog();
                coreSettingsDialog.Initialize(entry, context, HeroLogo.Source as BitmapImage);
                coreSettingsOpening = false;
                await coreSettingsDialog.ShowAsync();
                test = coreSettingsDialog.TestLaunchRequested;
            }
            finally
            {
                coreSettingsDialog = null; coreSettingsOpening = false; searching = false; gamepadGate.Reset();
                if (active && selection != null) RenderCategory();
            }
            if (test && active) HandleAction(CabinetAction.Select);
        }
        private void BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (launchCover.Active) { e.Handled = true; return; }
            if (ArcadeSessionCoordinator.ConsumeBack()) { e.Handled = true; return; }
            if (coreSettingsOpening) { cancelCoreSettingsOpen = true; e.Handled = true; return; }
            if (coreSettingsDialog != null) { coreSettingsDialog.CancelFromBack(); e.Handled = true; return; }
            if (!active) return;
            if (searching)
            {
                SearchPage.Instance.selectedPlaylistItem = null;
                SearchPage.Instance.Hide(); e.Handled = true; return;
            }
            e.Handled = true; HandleAction(CabinetAction.Back);
        }
        private void WindowActivated(object sender, WindowActivatedEventArgs e)
        {
            foreground = e.WindowActivationState != CoreWindowActivationState.Deactivated;
            LaunchCoverForegroundChanged(foreground);
            if (foreground) RefreshMemoryPolicy();
            if (!foreground) { gamepadGate.Reset(); ReleaseArtwork("deactivated"); }
            else if (active && selection != null && !searching) RenderCategory();
        }
        private static bool GamepadAction(VirtualKey key, out CabinetAction action)
        {
            switch (key)
            {
                case VirtualKey.GamepadDPadUp: case VirtualKey.GamepadLeftThumbstickUp: action = CabinetAction.PreviousGame; return true;
                case VirtualKey.GamepadDPadDown: case VirtualKey.GamepadLeftThumbstickDown: action = CabinetAction.NextGame; return true;
                case VirtualKey.GamepadDPadLeft: case VirtualKey.GamepadLeftThumbstickLeft: action = CabinetAction.PreviousCategory; return true;
                case VirtualKey.GamepadDPadRight: case VirtualKey.GamepadLeftThumbstickRight: action = CabinetAction.NextCategory; return true;
                case VirtualKey.GamepadA: case VirtualKey.GamepadMenu: action = CabinetAction.Select; return true;
                case VirtualKey.GamepadB: action = CabinetAction.Back; return true;
                case VirtualKey.GamepadY: action = CabinetAction.Search; return true;
                default: action = default(CabinetAction); return false;
            }
        }
        private void GamepadDown(object sender, KeyRoutedEventArgs e)
        {
            if (ArcadeSessionCoordinator.ConsumeKey(e.OriginalKey, true)) { e.Handled = true; return; }
            if (launchCover.Active) { e.Handled = true; return; }
            if (!active || searching || !ReferenceEquals(Frame?.Content, this)) return;
            // Dialogs register their own cabinet target and retain native controller handling.
            if (searching || FindParentDialog(FocusManager.GetFocusedElement() as DependencyObject)) return;
            CabinetAction action;
            if (GamepadAction(e.OriginalKey, out action))
            {
                e.Handled = true;
                if (gamepadGate.Down((int)e.OriginalKey, action, e.KeyStatus.WasKeyDown, clock.ElapsedMilliseconds, gamepadTiming)) HandleAction(action);
            }
            else if (e.OriginalKey == VirtualKey.GamepadX)
            {
                e.Handled = true;
                if (!e.KeyStatus.WasKeyDown && selection?.Current != null)
                {
                    playLater.UpdatePlaylistPlayLater(games[selection.Current.Id]);
                    StatusText.Text = "Play later (premium session): " + playLater.PlaylistItems.Count;
                }
            }
            else if (e.OriginalKey == VirtualKey.GamepadView)
            {
                e.Handled = true;
                if (gamepadGate.Down((int)e.OriginalKey, CabinetAction.GameSettings, e.KeyStatus.WasKeyDown, clock.ElapsedMilliseconds, gamepadTiming)) HandleAction(CabinetAction.GameSettings);
            }
        }
        private void GamepadUp(object sender, KeyRoutedEventArgs e)
        {
            if (ArcadeSessionCoordinator.ConsumeKey(e.OriginalKey, false)) { e.Handled = true; return; }
            CabinetAction action;
            if (searching || FindParentDialog(FocusManager.GetFocusedElement() as DependencyObject)) return;
            if (GamepadAction(e.OriginalKey, out action)) { gamepadGate.Up((int)e.OriginalKey); e.Handled = true; }
            else if (e.OriginalKey == VirtualKey.GamepadX || e.OriginalKey == VirtualKey.GamepadView) { gamepadGate.Up((int)e.OriginalKey); e.Handled = true; }
        }
        private static bool FindParentDialog(DependencyObject node)
        {
            while (node != null) { if (node is ContentDialog) return true; node = VisualTreeHelper.GetParent(node); }
            return false;
        }
    }
}
