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
        public BitmapImage Logo { get => logo; set { logo = value; Changed(nameof(Logo)); Changed(nameof(TitleVisibility)); } }
        public Visibility TitleVisibility => logo == null ? Visibility.Visible : Visibility.Collapsed;
        public double ItemHeight => selected ? 92 : 58;
        public double TitleSize => selected ? 23 : 17;
        public Brush Outline => new SolidColorBrush(selected ? Windows.UI.Color.FromArgb(255, 115, 225, 245) : Windows.UI.Colors.Transparent);
        public Brush Fill => new SolidColorBrush(selected ? Windows.UI.Color.FromArgb(220, 30, 64, 85) : Windows.UI.Colors.Transparent);
        public PremiumWheelItem(CatalogEntry entry) { Entry = entry; }
        public void Select(bool value) { selected = value; Changed(nameof(ItemHeight)); Changed(nameof(TitleSize)); Changed(nameof(Outline)); Changed(nameof(Fill)); }
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
        private bool active, loading, syncing, launching, searching, artworkRunning, artworkPending;
        private int generation;

        public PremiumBrowserPage()
        {
            StartupLog.Write("Premium browser: constructor entered");
            InitializeComponent();
            AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(GamepadDown), true);
            AddHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler(GamepadUp), true);
            Loaded += PageLoaded;
            StartupLog.Write("Premium browser: XAML initialized");
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            active = true;
            cabinetInput?.Dispose();
            cabinetInput = CabinetInput.Attach(this, HandleAction);
            SystemNavigationManager.GetForCurrentView().BackRequested += BackRequested;
            Window.Current.Activated += WindowActivated;
            if (selection != null) RenderCategory();
        }
        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            active = false; generation++; artworkPending = false;
            cabinetInput?.Dispose(); cabinetInput = null;
            SystemNavigationManager.GetForCurrentView().BackRequested -= BackRequested;
            Window.Current.Activated -= WindowActivated;
            gamepadGate.Reset();
            ClearArtwork();
            base.OnNavigatedFrom(e);
        }
        private async void PageLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await InputDiagnostics.RefreshAsync(PremiumRoot);
                if (selection != null) { RenderCategory(); return; }
                if (loading) return;
                loading = true;
                games.Clear();
                StartupLog.Write("Premium browser: catalog read begin");
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync("InternalCatalog.json");
                var entries = CatalogReader.Parse(await FileIO.ReadTextAsync(file));
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
                if (active) RenderCategory();
            }
            catch (Exception ex)
            {
                StartupLog.Write("Premium browser: load failed; using existing recovery page: " + ex);
                if (active) Frame.Navigate(typeof(InternalCatalogPage));
            }
            finally { loading = false; }
        }
        private void RenderCategory()
        {
            if (!active || selection == null) return;
            syncing = true;
            rows = selection.Games.Select(entry => new PremiumWheelItem(entry)).ToList();
            Wheel.ItemsSource = rows;
            syncing = false;
            RenderSelection();
        }
        private void RenderSelection()
        {
            if (!active || selection?.Current == null) return;
            syncing = true;
            Wheel.SelectedIndex = selection.SelectedIndex;
            for (int i = 0; i < rows.Count; i++) rows[i].Select(i == selection.SelectedIndex);
            syncing = false;
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
            StatusText.Text = "Ready · Y / configured Search opens search";
            generation++;
            ClearArtwork();
            // Retain only nearby wheel logos; large catalogs do not accumulate decoded images.
            for (int i = 0; i < rows.Count; i++) if (Math.Abs(i - selection.SelectedIndex) > 2) rows[i].Logo = null;
            FocusSelection();
            artworkPending = true;
            if (!artworkRunning) LoadArtwork();
        }
        private void FocusSelection()
        {
            if (Wheel.SelectedItem == null) return;
            Wheel.ScrollIntoView(Wheel.SelectedItem, ScrollIntoViewAlignment.Default);
            Wheel.UpdateLayout();
            var container = Wheel.ContainerFromIndex(Wheel.SelectedIndex) as ListViewItem;
            if (container != null)
            {
                container.Focus(FocusState.Programmatic);
                container.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.5 });
            }
            else Wheel.Focus(FocusState.Programmatic);
        }
        private void Wheel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (syncing || selection == null || Wheel.SelectedIndex < 0) return;
            selection.Select(Wheel.SelectedIndex); RenderSelection();
        }
        private void Wheel_ItemClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as PremiumWheelItem;
            if (selection == null || row == null) return;
            selection.SelectGame(row.Entry.Id); RenderSelection(); HandleAction(CabinetAction.Select);
        }
        private async void HandleAction(CabinetAction action)
        {
            try
            {
                if (!active || loading || launching) return;
                if (action == CabinetAction.Back) { Frame.Navigate(typeof(InternalCatalogPage)); return; }
                if (selection?.Current == null) return;
                if (action == CabinetAction.Select)
                {
                    launching = true;
                    try { await GameDetailsPage.StartContent(games[selection.Current.Id]); }
                    finally { launching = false; }
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
        private void BackRequested(object sender, BackRequestedEventArgs e)
        {
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
            if (e.WindowActivationState == CoreWindowActivationState.Deactivated) { gamepadGate.Reset(); generation++; artworkPending = false; ClearArtwork(); }
            else if (active && selection != null && !searching) RenderSelection();
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
        }
        private void GamepadUp(object sender, KeyRoutedEventArgs e)
        {
            CabinetAction action;
            if (searching || FindParentDialog(FocusManager.GetFocusedElement() as DependencyObject)) return;
            if (GamepadAction(e.OriginalKey, out action)) { gamepadGate.Up((int)e.OriginalKey); e.Handled = true; }
            else if (e.OriginalKey == VirtualKey.GamepadX) e.Handled = true;
        }
        private static bool FindParentDialog(DependencyObject node)
        {
            while (node != null) { if (node is ContentDialog) return true; node = VisualTreeHelper.GetParent(node); }
            return false;
        }
        private void ClearArtwork()
        {
            Backdrop.Source = HeroLogo.Source = Screenshot.Source = null;
            HeroTitle.Visibility = ScreenshotFallback.Visibility = Visibility.Visible;
        }
        private async void LoadArtwork()
        {
            artworkRunning = true;
            try
            {
                while (artworkPending && active)
                {
                    artworkPending = false;
                    int version = generation;
                    var entry = selection.Current;
                    var logo = await ReadArtwork(entry, "logo", 1000, version);
                    if (version != generation) continue;
                    HeroLogo.Source = logo; HeroTitle.Visibility = logo == null ? Visibility.Visible : Visibility.Collapsed;
                    var background = await ReadArtwork(entry, "background", 1920, version);
                    if (version != generation) continue;
                    Backdrop.Source = background;
                    var screenshot = await ReadArtwork(entry, "screenshot", 1000, version);
                    if (version != generation) continue;
                    Screenshot.Source = screenshot; ScreenshotFallback.Visibility = screenshot == null ? Visibility.Visible : Visibility.Collapsed;
                    foreach (var row in rows.Where((item, index) => Math.Abs(index - selection.SelectedIndex) <= 2).ToArray())
                    {
                        var wheelLogo = await ReadArtwork(row.Entry, "logo", 600, version);
                        if (version != generation) break;
                        row.Logo = wheelLogo;
                    }
                }
            }
            catch (Exception ex) { StartupLog.Write("Premium browser: artwork fallback: " + ex.Message); }
            finally { artworkRunning = false; }
        }
        private async Task<BitmapImage> ReadArtwork(CatalogEntry entry, string role, int width, int version)
        {
            foreach (string path in PremiumArtwork.Candidates(entry, role))
            {
                if (version != generation || !active) return null;
                try
                {
                    var file = await ApplicationData.Current.LocalFolder.GetFileAsync(path);
                    using (var stream = await file.OpenReadAsync())
                    {
                        if (stream.Size > 30 * 1024 * 1024) throw new InvalidOperationException("Artwork exceeds 30 MiB file limit.");
                        var decoder = await BitmapDecoder.CreateAsync(stream);
                        int height = role == "background" ? 1080 : role == "screenshot" ? 800 : width == 600 ? 160 : 400;
                        double scale = Math.Min(1, Math.Min((double)width / decoder.PixelWidth, (double)height / decoder.PixelHeight));
                        int decodeWidth = Math.Max(1, (int)(decoder.PixelWidth * scale));
                        stream.Seek(0);
                        var image = new BitmapImage { DecodePixelType = DecodePixelType.Physical, DecodePixelWidth = decodeWidth };
                        await image.SetSourceAsync(stream);
                        return version == generation ? image : null;
                    }
                }
                catch (System.IO.FileNotFoundException) { }
                catch (System.IO.DirectoryNotFoundException) { }
                catch (Exception ex) { StartupLog.Write("Premium browser: unusable " + role + " " + path + ": " + ex.Message); }
            }
            return null;
        }
    }
}
