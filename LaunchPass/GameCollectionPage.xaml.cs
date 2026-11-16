using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LaunchPass.Catalog;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace RetroPass
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class GameCollectionPage : Page
    {
        private Playlist playlist;
        private IReadOnlyList<Playlist> internalCategories;
        private readonly Dictionary<Playlist, int> internalSelections = new Dictionary<Playlist, int>();
        private IDisposable cabinetInput;
        private bool internalStyleSet, cabinetLaunching;
        private int internalLoadGeneration;

        public class GameCollectionPageNavigateParams
        {
            public GameCollectionPageNavigateParams(PlaylistItem p1, Playlist p2, PlaylistPlayLater p3, IReadOnlyList<Playlist> categories = null)
            {
                playlistItem = p1;
                playlist = p2;
                playlistPlayLater = p3;
                internalCategories = categories;
            }

            public PlaylistItem playlistItem;
            public Playlist playlist;
            public PlaylistPlayLater playlistPlayLater;
            public IReadOnlyList<Playlist> internalCategories;
        }

        private PlaylistPlayLater playlistPlayLater;

        public GameCollectionPage()
        {
            this.InitializeComponent();
            this.Loaded += GameCollectionPage_Loaded;
        }

        async private void GameCollectionPage_Loaded(object sender, RoutedEventArgs e)
        {
            mediaPlayer.MediaPath = ((App)Application.Current).CurrentThemeSettings.GetMediaPath("GamePage");

            if (((App)Application.Current).InternalCatalogMode)
            {
                try { await LoadInternalPlaylistAsync(); }
                catch (Exception ex) { StartupLog.Write("Internal collection load failed: " + ex); }
                return;
            }

            if (PlatformGridView.ItemsSource == null || PlatformGridView.ItemsSource != playlist.PlaylistItems)
            {
                PlatformGridView.ItemsSource = playlist.PlaylistItems;
                NameCollection.Text = playlist.Name;
                PlatformGridView.SelectedIndex = 0;
            }

            lock (imageloading)
            {
                tasksImage.Clear();
            }

            if (playlist.PlaylistItems.Count > 0)
            {
                foreach (var item in playlist.PlaylistItems)
                {
                    item.bitmapImage = await item.game.GetImageThumbnailAsync();
                }
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    //UI code here
                    PlatformGridView.ItemsSource = null;
                    PlatformGridView.ItemsSource = playlist.PlaylistItems;
                    PlatformGridView.UpdateLayout();
                });
            }
        }

        protected override async void OnKeyDown(KeyRoutedEventArgs e)
        {
            // Cabinet keyboard actions are centralized; retain the existing gamepad/external path.
            if (CabinetInput.Enabled && !CabinetInput.IsGamepad(e.OriginalKey)) { base.OnKeyDown(e); return; }
            switch (e.Key)
            {
                case VirtualKey.GamepadX:
                case VirtualKey.X:
                    // Gamepad X button was pressed
                    if (PlatformGridView.SelectedItem != null)
                    {
                        int lastIndex = PlatformGridView.SelectedIndex;
                        PlaylistItem pl = PlatformGridView.SelectedItem as PlaylistItem;
                        playlistPlayLater.UpdatePlaylistPlayLater(pl);
                        PlayLaterControl.UpdatePlayLaterControl(pl, playlistPlayLater);
                        //if this is item from playlistPlayLater, then currentListView is playLate list view
                        //so set proper selected item in case of deletion
                        if (pl.playlist == playlistPlayLater)
                        {
                            UpdateNumGamesText(playlistPlayLater.PlaylistItems.Count);
                            PlatformGridView.SelectedIndex = Math.Min(lastIndex, playlistPlayLater.PlaylistItems.Count - 1);
                        }
                    }
                    break;

                case VirtualKey.GamepadY:
                case VirtualKey.Y:
                    await ShowSearchAsync();
                    break;

                case VirtualKey.GamepadMenu:
                    if (PlatformGridView.SelectedItem != null)
                    {
                        int lastIndex = PlatformGridView.SelectedIndex;
                        PlaylistItem pl = PlatformGridView.SelectedItem as PlaylistItem;
                        GameDetailsPage.StartContent(pl);
                    }
                    break;
            }
            base.OnKeyDown(e);
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
                e.Handled = true;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            var param = e.Parameter as GameCollectionPageNavigateParams;

            if (param.playlist != null)
            {
                playlist = param.playlist;
            }

            UpdateNumGamesText(playlist.PlaylistItems.Count);
            playlistPlayLater = param.playlistPlayLater;
            internalCategories = param.internalCategories;
            if (((App)Application.Current).InternalCatalogMode)
            {
                foreach (var old in internalSelections.Keys.Where(old => !ReferenceEquals(old, playlistPlayLater) &&
                    (internalCategories == null || !internalCategories.Contains(old))).ToArray()) internalSelections.Remove(old);
            }
            cabinetInput?.Dispose();
            cabinetInput = CabinetInput.Attach(this, HandleCabinetAction);

            base.OnNavigatedTo(e);
        }

        private void UpdateNumGamesText(int count)
        {
            NumGamesCollectionText.Text = count + " games in collection";
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            cabinetInput?.Dispose(); cabinetInput = null;
            internalLoadGeneration++;
            //CoreWindow.GetForCurrentThread().KeyDown -= OnKeyDown;
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            base.OnNavigatedFrom(e);
        }

        private async Task LoadInternalPlaylistAsync()
        {
            int generation = ++internalLoadGeneration;
            var loading = playlist;
            if (!internalStyleSet)
            {
                var style = new Style(typeof(GridViewItem)) { BasedOn = PlatformGridView.ItemContainerStyle };
                style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Windows.UI.Color.FromArgb(180, 35, 90, 150))));
                PlatformGridView.ItemContainerStyle = style;
                internalStyleSet = true;
            }
            int index;
            if (!internalSelections.TryGetValue(loading, out index)) index = 0;
            PlatformGridView.ItemsSource = loading.PlaylistItems;
            NameCollection.Text = loading.Name;
            SelectInternal(CabinetNavigation.Step(index, 0, loading.PlaylistItems.Count));
            foreach (var item in loading.PlaylistItems)
            {
                var image = await item.game.GetImageThumbnailAsync();
                if (generation != internalLoadGeneration || !ReferenceEquals(playlist, loading)) return;
                item.bitmapImage = image;
            }
        }
        private void SelectInternal(int index)
        {
            PlatformGridView.SelectedIndex = index;
            UpdateInternalSelection();
            if (index < 0) return;
            if (PlatformGridView.SelectedItem == null) return;
            PlatformGridView.ScrollIntoView(PlatformGridView.SelectedItem);
            PlatformGridView.UpdateLayout();
            var container = PlatformGridView.ContainerFromIndex(index) as Control;
            if (container != null)
            {
                var button = FindChild<Button>(container, "GameButton");
                (button ?? container).Focus(FocusState.Keyboard);
                container.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.5 });
            }
        }
        private void PlatformGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (((App)Application.Current).InternalCatalogMode) UpdateInternalSelection();
        }
        private void UpdateInternalSelection()
        {
            if (playlist == null) return;
            int index = PlatformGridView.SelectedIndex;
            internalSelections[playlist] = index;
            var item = PlatformGridView.SelectedItem as PlaylistItem;
            NumGamesCollectionText.Text = playlist.PlaylistItems.Count + " games in collection" +
                (item == null ? "" : " — " + (index + 1) + "/" + playlist.PlaylistItems.Count + " " + item.game.Title);
        }
        private async void HandleCabinetAction(CabinetAction action)
        {
            try
            {
                int count = playlist.PlaylistItems.Count;
                if (action == CabinetAction.Search) { await ShowSearchAsync(); return; }
                if (action == CabinetAction.Back) { if (Frame.CanGoBack) Frame.GoBack(); return; }
                if (action == CabinetAction.Select)
                {
                    var item = PlatformGridView.SelectedItem as PlaylistItem;
                    if (item == null || cabinetLaunching) return;
                    cabinetLaunching = true;
                    try { await GameDetailsPage.StartContent(item); } finally { cabinetLaunching = false; }
                    return;
                }
                if (action == CabinetAction.PreviousCategory || action == CabinetAction.NextCategory)
                {
                    if (internalCategories == null || internalCategories.Count < 2) return;
                    int category = -1;
                    for (int i = 0; i < internalCategories.Count; i++) if (ReferenceEquals(playlist, internalCategories[i])) category = i;
                    if (category < 0) return;
                    playlist = internalCategories[CabinetNavigation.Step(category, action == CabinetAction.PreviousCategory ? -1 : 1, internalCategories.Count, true)];
                    await LoadInternalPlaylistAsync();
                    return;
                }
                int delta = action == CabinetAction.PreviousGame || action == CabinetAction.PreviousLetter ? -1 : 1;
                int next = action == CabinetAction.PreviousLetter || action == CabinetAction.NextLetter ?
                    CabinetNavigation.Letter(playlist.PlaylistItems.Select(item => item.game.Title).ToArray(), PlatformGridView.SelectedIndex, delta) :
                    CabinetNavigation.Step(PlatformGridView.SelectedIndex, delta, count);
                SelectInternal(next);
            }
            catch (Exception ex) { StartupLog.Write("Internal cabinet navigation failed: " + ex); }
        }
        private async Task ShowSearchAsync()
        {
            var pl1 = new List<Playlist>();
            pl1.Add(playlist);
            SearchPage.Instance.OnNavigatedTo(pl1);
            try { await SearchPage.Instance.ShowAsync(); }
            finally { SearchPage.Instance.OnNavigatedFrom(); }
            if (SearchPage.Instance.selectedPlaylistItem != null)
            {
                GameDetailsPage popupDetails = new GameDetailsPage();
                popupDetails.OnNavigatedTo(SearchPage.Instance.selectedPlaylistItem);
                try { await popupDetails.ShowAsync(); }
                finally { popupDetails.OnNavigatedFrom(); }
            }
        }

        private void ScrollToCenter(UIElement sender, BringIntoViewRequestedEventArgs args)
        {
            if (args.VerticalAlignmentRatio != 0.5)  // Guard against our own request
            {
                args.Handled = true;
                // Swallow this request and restart it with a request to center the item.  We could instead have chosen
                // to adjust the TargetRect’s Y and Height values to add a specific amount of padding as it bubbles up,
                // but if we just want to center it then this is easier.

                // (Optional) Account for sticky headers if they exist
                var headerOffset = 0.0;

                // Issue a new request
                args.TargetElement.StartBringIntoView(new BringIntoViewOptions()
                {
                    AnimationDesired = true,
                    VerticalAlignmentRatio = 0.5, // a normalized alignment position (0 for the top, 1 for the bottom)
                    VerticalOffset = headerOffset, // applied after meeting the alignment ratio request
                });
            }
        }

        private void ItemsWrapGrid_BringIntoViewRequested(UIElement sender, BringIntoViewRequestedEventArgs args)
        {
            ScrollToCenter(sender, args);
        }

        public static object imageloading = new object();
        public static Dictionary<int, string> tasksImage = new Dictionary<int, string>();

        private void SetItemRecycled(UIElement element)
        {
            var templateRoot = element as Button;

            var image = (Image)templateRoot.FindName("ItemImage");
            image.Source = null;
            image.Opacity = 0;

            var GameNameDisplayEmpty = (UIElement)templateRoot.FindName("GameNameDisplayEmpty");
            GameNameDisplayEmpty.Visibility = Visibility.Collapsed;
            var GameNameDisplay = (UIElement)templateRoot.FindName("GameNameDisplay");
            GameNameDisplay.Visibility = Visibility.Collapsed;
        }

        private void SetItemText(UIElement element)
        {
            var templateRoot = element as Button;

            var image = (Image)templateRoot.FindName("ItemImage");
            image.Source = null;
            image.Opacity = 0;

            var GameNameDisplayEmpty = (UIElement)templateRoot.FindName("GameNameDisplayEmpty");
            GameNameDisplayEmpty.Visibility = Visibility.Visible;
            var GameNameDisplay = (UIElement)templateRoot.FindName("GameNameDisplay");
            GameNameDisplay.Visibility = Visibility.Collapsed;
        }

        private void SetItemImage(UIElement element)
        {
            var templateRoot = element as Button;

            var image = (Image)templateRoot.FindName("ItemImage");
            var GameNameDisplayEmpty = (UIElement)templateRoot.FindName("GameNameDisplayEmpty");
            GameNameDisplayEmpty.Visibility = Visibility.Collapsed;
            var GameNameDisplay = (UIElement)templateRoot.FindName("GameNameDisplay");
            GameNameDisplay.Visibility = Visibility.Collapsed;
            image.Opacity = 100;
        }

        private void GamesListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                SetItemRecycled(args.ItemContainer.ContentTemplateRoot);
                args.Handled = true;
            }

            if (args.Phase == 0)
            {
                SetItemText(args.ItemContainer.ContentTemplateRoot);

                var templateRoot = args.ItemContainer.ContentTemplateRoot as Button;
                var image = (Image)templateRoot.FindName("ItemImage");
                args.RegisterUpdateCallback(ShowImage);
                args.Handled = true;
            }
        }

        private async void ShowImage(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Phase == 1)
            {
                // It's phase 1, so show this item's image.
                var templateRoot = args.ItemContainer.ContentTemplateRoot as Button;
                var image = (Image)templateRoot.FindName("ItemImage");
                var item = args.Item as PlaylistItem;
                BitmapImage imageTmp = await item.game.GetImageThumbnailAsync();
                ////Trace.TraceInformation("Loaded " + hash + " Image Name:" + imageName);

                //image.Opacity = 0;
                image.Source = imageTmp;
                if (image.Source != null)
                {
                    SetItemImage(args.ItemContainer.ContentTemplateRoot);
                }
                else
                {
                    SetItemText(args.ItemContainer.ContentTemplateRoot);
                }

                args.Handled = true;
            }
        }

        public static T FindChild<T>(DependencyObject parent, string childName) where T : DependencyObject
        {
            if (parent == null) return null;
            T foundChild = null;
            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);

            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                T childType = child as T;

                if (childType == null)
                {
                    foundChild = FindChild<T>(child, childName);
                    if (foundChild != null) break;
                }
                else if (!string.IsNullOrEmpty(childName))
                {
                    var frameworkElement = child as FrameworkElement;
                    if (frameworkElement != null && frameworkElement.Name == childName)
                    {
                        foundChild = (T)child;
                        break;
                    }

                    foundChild = FindChild<T>(child, childName);
                    if (foundChild != null) break;
                }
                else
                {
                    foundChild = (T)child;
                    break;
                }
            }

            return foundChild;
        }

        private void Grid_GotFocus(object sender, RoutedEventArgs e)
        {
            var item = e.OriginalSource as DependencyObject;
            var childListView = FindChild<UIElement>(item, "GameNameDisplay");
            if (childListView != null) childListView.Visibility = Visibility.Visible;

            PlaylistItem playlistItem = PlatformGridView.SelectedItem as PlaylistItem;
            if (playlistItem != null) PlayLaterControl.UpdatePlayLaterControl(playlistItem, playlistPlayLater);
        }

        private void Grid_LostFocus(object sender, RoutedEventArgs e)
        {
            var item = e.OriginalSource as DependencyObject;
            var childListView = FindChild<UIElement>(item, "GameNameDisplay");
            if (childListView != null) childListView.Visibility = Visibility.Collapsed;
        }

        private async void PlatformGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as PlaylistItem;
            GameDetailsPage popup = new GameDetailsPage();
            popup.OnNavigatedTo(item);
            try { await popup.ShowAsync(); }
            finally { popup.OnNavigatedFrom(); }
        }
    }
}
