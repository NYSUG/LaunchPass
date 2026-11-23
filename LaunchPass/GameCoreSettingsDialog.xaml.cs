using LaunchPass.Catalog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;

namespace RetroPass
{
    public sealed partial class GameCoreSettingsDialog : ContentDialog
    {
        private CatalogEntry entry;
        private GameCoreContext context;
        private List<RegisteredCore> cores;
        private List<GameCoreAttempt> attempts;
        private readonly CoreMenuNavigation navigation = new CoreMenuNavigation();
        private readonly CabinetRepeatGate gamepad = new CabinetRepeatGate();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private IDisposable registration;
        private Button[] buttons;
        private bool closed;
        internal bool TestLaunchRequested { get; private set; }
        public GameCoreSettingsDialog()
        {
            InitializeComponent();
            buttons = new[] { CoreButton, SaveButton, TestButton, ResetButton, HistoryButton, CancelButton };
            AddHandler(PreviewKeyDownEvent, new KeyEventHandler(GamepadDown), true);
            AddHandler(PreviewKeyUpEvent, new KeyEventHandler(GamepadUp), true);
            Opened += (sender, args) => { registration = CabinetInput.Attach(this, Action); FocusRow(); };
            Closing += (sender, args) => { if (navigation.Busy) { navigation.RequestCancel(); args.Cancel = true; } };
            Closed += (sender, args) => { closed = true; registration?.Dispose(); registration = null; GameLogo.Source = null; };
        }
        internal void Initialize(CatalogEntry game, GameCoreContext settings, BitmapImage image)
        {
            entry = game; context = settings; GameTitle.Text = game.Title; GameLogo.Source = image;
            cores = context.Registry.Cores.Where(core => core.Available != false).OrderBy(core => core.Name, StringComparer.OrdinalIgnoreCase).ToList();
            attempts = context.History.Attempts.Where(item => string.Equals(item.GameId, entry.Id, StringComparison.OrdinalIgnoreCase)).Reverse().ToList();
            navigation.CoreIndex = cores.FindIndex(core => string.Equals(core.File, context.Effective, StringComparison.OrdinalIgnoreCase));
            Refresh();
        }
        private void Refresh()
        {
            var chosen = navigation.CoreIndex >= 0 && navigation.CoreIndex < cores.Count ? cores[navigation.CoreIndex] : null;
            string origin = context.Overrides.Get(entry.Id) != null ? "GameOverrides" : entry.CatalogCoreOverride != null ? "catalog game" : "platform default";
            EffectiveCore.Text = "Effective (" + origin + "): " + context.Effective + "\nCatalog default: " + entry.Launch.Core;
            RegistrySource.Text = context.Registry.Source ?? "Configured registry; installation not independently verified";
            CoreName.Text = chosen?.Name ?? "Choose a configured core with Left/Right";
            CoreFile.Text = chosen?.File ?? "No available selection";
            // Keep buttons focusable even without a selection, so Back/Reset remain reliable.
            HistoryButton.Content = attempts.Count == 0 ? "History: no recorded attempts" : "History " + (navigation.HistoryIndex + 1) + "/" + attempts.Count + " — Select / Left / Right";
            TriedCores.Text = "Previously tried: " + string.Join(", ", attempts.Select(item => item.Core).Distinct(StringComparer.OrdinalIgnoreCase));
            if (attempts.Count > 0)
            {
                navigation.HistoryIndex = Math.Max(0, navigation.HistoryIndex);
                var attempt = attempts[navigation.HistoryIndex];
                HistoryText.Text = attempt.At + "\n" + attempt.Core + "\nHandoff: " + (attempt.HandedOff == null ? "unknown" : attempt.HandedOff == true ? "accepted" : "not accepted") +
                    "\nLauncher resume: " + (attempt.ResumedAt ?? "not observed") + " " + attempt.ResumeObservation +
                    (attempt.Message == null ? "" : "\n" + attempt.ErrorCode + " " + attempt.Message);
            }
            else HistoryText.Text = context.History.Enabled == false ? "History recording is disabled." : "No gameplay outcome is inferred from launch or return.";
            Notice.Text = context.Warning ?? "";
        }
        private void FocusRow()
        {
            var button = buttons[navigation.Row]; button.Focus(FocusState.Keyboard);
            button.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = navigation.Row == 0 ? 1 : .5 });
        }
        private void RowFocused(object sender, RoutedEventArgs args) { navigation.Row = int.Parse((string)((Button)sender).Tag); }
        private void Action(CabinetAction action)
        {
            if (action == CabinetAction.Back) { CancelFromBack(); return; }
            if (navigation.Busy) return;
            if (action == CabinetAction.Select) { CabinetInput.Invoke(buttons[navigation.Row]); return; }
            navigation.Move(action, cores.Count, attempts.Count); Refresh(); FocusRow();
        }
        internal void CancelFromBack() { navigation.RequestCancel(); if (!navigation.Busy) Hide(); }
        private void GamepadDown(object sender, KeyRoutedEventArgs args)
        {
            if (!CabinetInput.IsGamepad(args.OriginalKey)) return;
            args.Handled = true;
            CabinetAction action;
            switch (args.OriginalKey)
            {
                case VirtualKey.GamepadDPadUp: case VirtualKey.GamepadLeftThumbstickUp: action = CabinetAction.PreviousGame; break;
                case VirtualKey.GamepadDPadDown: case VirtualKey.GamepadLeftThumbstickDown: action = CabinetAction.NextGame; break;
                case VirtualKey.GamepadDPadLeft: case VirtualKey.GamepadLeftThumbstickLeft: action = CabinetAction.PreviousCategory; break;
                case VirtualKey.GamepadDPadRight: case VirtualKey.GamepadLeftThumbstickRight: action = CabinetAction.NextCategory; break;
                case VirtualKey.GamepadA: case VirtualKey.GamepadMenu: action = CabinetAction.Select; break;
                case VirtualKey.GamepadB: action = CabinetAction.Back; break;
                default: return;
            }
            if (gamepad.Down((int)args.OriginalKey, action, args.KeyStatus.WasKeyDown, clock.ElapsedMilliseconds, CabinetInputMap.Defaults())) Action(action);
        }
        private void GamepadUp(object sender, KeyRoutedEventArgs args)
        { if (CabinetInput.IsGamepad(args.OriginalKey)) { gamepad.Up((int)args.OriginalKey); args.Handled = true; } }
        private void CoreClicked(object sender, RoutedEventArgs args)
        { if (!navigation.Busy) { navigation.CoreIndex = CabinetNavigation.Step(navigation.CoreIndex, 1, cores.Count, true); Refresh(); } }
        private async Task Save(bool test, bool reset)
        {
            if (closed || navigation.Busy || navigation.CancelRequested || TestLaunchRequested) return;
            if (!reset && (navigation.CoreIndex < 0 || navigation.CoreIndex >= cores.Count))
            {
                Notice.Text = "Choose a configured core with Left/Right first.";
                Notice.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 1 });
                return;
            }
            navigation.Busy = true;
            try
            {
                await GameCoreService.Current.SaveAsync(entry, reset ? null : cores[navigation.CoreIndex].File);
                context = await GameCoreService.Current.LoadAsync(entry);
                if (reset) navigation.CoreIndex = cores.FindIndex(core => string.Equals(core.File, context.Effective, StringComparison.OrdinalIgnoreCase));
                Refresh(); Notice.Text = (context.Warning ?? "") + (reset ? "Override reset to catalog default." : "Override saved.");
                if (test && !navigation.CancelRequested) { TestLaunchRequested = true; navigation.Busy = false; Hide(); }
            }
            catch (Exception ex)
            {
                Notice.Text = "Not saved; no test launch: " + ex.Message;
                Notice.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 1 });
                StartupLog.Write("Game core settings save: " + ex);
            }
            finally
            {
                navigation.Busy = false;
                if (navigation.CancelRequested) { TestLaunchRequested = false; Hide(); }
            }
        }
        private async void SaveClicked(object sender, RoutedEventArgs args) { await Save(false, false); }
        private async void TestClicked(object sender, RoutedEventArgs args) { await Save(true, false); }
        private async void ResetClicked(object sender, RoutedEventArgs args) { await Save(false, true); }
        private void CancelClicked(object sender, RoutedEventArgs args) { CancelFromBack(); }
        private void HistoryClicked(object sender, RoutedEventArgs args)
        { if (!navigation.Busy) { navigation.HistoryIndex = CabinetNavigation.Step(navigation.HistoryIndex, 1, attempts.Count, true); Refresh(); } }
    }
}
