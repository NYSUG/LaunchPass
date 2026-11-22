using LaunchPass.Catalog;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace RetroPass
{
    // Small platform selector; game browsing/search/details remain the existing library UI.
    public sealed partial class InternalCatalogPage : Page
    {
        private readonly StackPanel panel;
        private readonly PlaylistPlayLater playLater;
        private readonly List<Playlist> categories = new List<Playlist>();
        private readonly List<Button> choices = new List<Button>();
        private readonly List<Button> categoryChoices = new List<Button>();
        private IDisposable cabinetInput;
        public InternalCatalogPage()
        {
            StartupLog.Write("Catalog page: constructor entered");
            InitializeComponent();
            StartupLog.Write("Catalog page: XAML initialized");
            panel = new StackPanel { Margin = new Thickness(48), Spacing = 16 };
            StartupLog.Write("Catalog page: panel created");
            playLater = new PlaylistPlayLater();
            StartupLog.Write("Catalog page: session playlist created");
            NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Required;
            CatalogRoot.Children.Add(new ScrollViewer { Content = panel });
            Loaded += async (sender, args) => await ReloadWithDiagnostics();
            // Independent of catalog loading; diagnostic failures must not block navigation.
            Loaded += async (sender, args) => await InputDiagnostics.RefreshAsync(CatalogRoot);
            StartupLog.Write("Catalog page: constructor complete");
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            cabinetInput?.Dispose();
            cabinetInput = CabinetInput.Attach(this, HandleCabinetAction);
        }
        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            cabinetInput?.Dispose(); cabinetInput = null;
            base.OnNavigatedFrom(e);
        }
        private void HandleCabinetAction(CabinetAction action)
        {
            if (action == CabinetAction.PreviousLetter || action == CabinetAction.NextLetter || action == CabinetAction.Search) return;
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            int index = choices.FindIndex(button => CabinetInput.Contains(button, focused));
            if (action == CabinetAction.Select)
            {
                if (choices.Count > 0) CabinetInput.Invoke(choices[Math.Max(0, index)]);
                return;
            }
            if (action == CabinetAction.Back)
            {
                if (CanReturnToPremium) Frame.GoBack();
                return;
            }
            bool category = action == CabinetAction.PreviousCategory || action == CabinetAction.NextCategory;
            var list = category ? categoryChoices : choices;
            if (category && list.Count < 2) return;
            int current = list.FindIndex(button => CabinetInput.Contains(button, focused));
            int delta = action == CabinetAction.PreviousGame || action == CabinetAction.PreviousCategory || action == CabinetAction.PreviousLetter ? -1 : 1;
            int next = CabinetNavigation.Step(current, delta, list.Count, category);
            if (next >= 0)
            {
                list[next].Focus(FocusState.Keyboard);
                list[next].StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.5 });
            }
        }
        private async Task ReloadWithDiagnostics()
        {
            try { await Reload(); }
            catch (Exception ex) { ((App)Application.Current).ShowStartupFailure("Catalog page load", ex); }
        }
        private bool CanReturnToPremium => Frame != null && Frame.CanGoBack && Frame.BackStack.Last().SourcePageType == typeof(PremiumBrowserPage);
        private async Task Reload()
        {
            StartupLog.Write("Catalog page: load begin");
            panel.Children.Clear();
            categories.Clear(); choices.Clear(); categoryChoices.Clear();
            panel.Children.Add(new TextBlock { Text = "Internal game library", FontSize = 32 });
            if (CanReturnToPremium)
            {
                var premium = new Button { Content = "Return to premium browser" };
                premium.Click += (sender, args) => Frame.GoBack();
                panel.Children.Add(premium); choices.Add(premium);
            }
            try
            {
                StartupLog.Write("Catalog JSON: file lookup begin");
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync("InternalCatalog.json");
                StartupLog.Write("Catalog JSON: read begin");
                string json = await FileIO.ReadTextAsync(file);
                StartupLog.Write("Catalog JSON: parse begin; characters=" + json.Length);
                var entries = CatalogPresentationOrder.Sort(CatalogReader.Parse(json));
                StartupLog.Write("Catalog JSON: parse complete; games=" + entries.Count);
                foreach (var group in entries.GroupBy(e => e.PlatformId, StringComparer.OrdinalIgnoreCase))
                {
                    var platform = new Platform { Name = group.First().PlatformTitle, EmulatorType = Platform.EEmulatorType.retroarch };
                    var playlist = new Playlist { Name = platform.Name };
                    foreach (var entry in group)
                        playlist.PlaylistItems.Add(new PlaylistItem { playlist = playlist, game = new InternalCatalogGame(entry, platform) });
                    categories.Add(playlist);
                    var button = new Button { Content = platform.Name + " (" + playlist.PlaylistItems.Count + ")" };
                    button.Click += (sender, args) => Frame.Navigate(typeof(GameCollectionPage),
                        new GameCollectionPage.GameCollectionPageNavigateParams(null, playlist, playLater, categories.ToArray()));
                    panel.Children.Add(button);
                    choices.Add(button); categoryChoices.Add(button);
                }
                var later = new Button { Content = "Play later (this session)" };
                later.Click += (sender, args) => Frame.Navigate(typeof(GameCollectionPage),
                    new GameCollectionPage.GameCollectionPageNavigateParams(null, playLater, playLater, categories.ToArray()));
                panel.Children.Add(later);
                choices.Add(later);
                StartupLog.Write("Catalog page: platform controls created");
            }
            catch (Exception ex)
            {
                StartupLog.Write("Catalog JSON/UI load FAILED: " + ex);
                InternalCatalogGame.Log("Catalog load failed: " + ex);
                panel.Children.Add(new TextBlock { Text = "Cannot load LocalState\\InternalCatalog.json: " + ex.Message +
                    "\nCorrect the file and select Reload. Internal mode will not scan USB storage.", TextWrapping = TextWrapping.Wrap });
            }
            var reload = new Button { Content = "Reload catalog" };
            reload.Click += async (sender, args) => await ReloadWithDiagnostics();
            panel.Children.Add(reload);
            choices.Add(reload);
            var probe = new Button { Content = "Internal-storage diagnostic probe" };
            probe.Click += async (sender, args) =>
            {
                var config = await InternalStorageProbeConfig.Load();
                if (config == null) { await new ContentDialog { Title = "Probe configuration missing", Content = "Restore the known-working InternalStorageProbe.xml to LocalState.", CloseButtonText = "Close" }.ShowAsync(); return; }
                // Explicit diagnostic selection enables this in-memory copy only.
                config.Enabled = true;
                InternalStorageProbeLog.Enabled = true;
                Frame.Content = new InternalStorageProbePage(config);
            };
            panel.Children.Add(probe);
            choices.Add(probe);
            if (panel.Children.Count > 1 && panel.Children[1] is Control first) first.Focus(FocusState.Programmatic);
            StartupLog.Write("Catalog page: load complete");
        }
    }
}
