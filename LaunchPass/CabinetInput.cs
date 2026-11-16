using LaunchPass.Catalog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Automation.Provider;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace RetroPass
{
    // One keyboard policy for internal pages/dialogs; original gamepad keys always pass through.
    internal static class CabinetInput
    {
        private static CabinetInputMap map = CabinetInputMap.Defaults();
        private static readonly CabinetRepeatGate gate = new CabinetRepeatGate();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static readonly List<Registration> targets = new List<Registration>();
        private static bool hooked, foreground = true;
        private static Task configuration;
        internal static bool Enabled => ((App)Application.Current).InternalCatalogMode && map.Enabled;
        internal static bool IsGamepad(VirtualKey key) => CabinetInputMap.IsGamepad((int)key);

        internal static IDisposable Attach(UIElement scope, Action<CabinetAction> action)
        {
            if (!((App)Application.Current).InternalCatalogMode) return null;
            if (!hooked)
            {
                var window = Window.Current.CoreWindow;
                window.Dispatcher.AcceleratorKeyActivated += Accelerator;
                window.Activated += (sender, args) => { foreground = args.WindowActivationState != CoreWindowActivationState.Deactivated; if (!foreground) gate.Reset(); };
                window.VisibilityChanged += (sender, args) => { if (!args.Visible) gate.Reset(); };
                hooked = true;
            }
            var registration = new Registration(scope, action);
            targets.Add(registration);
            if (configuration == null) configuration = LoadConfigurationAsync();
            return registration;
        }
        private static async Task LoadConfigurationAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync("CabinetInput.json") as StorageFile;
                if (file != null) map = CabinetInputMap.Parse(await FileIO.ReadTextAsync(file));
                StartupLog.Write("Cabinet input: " + (file == null ? "defaults (arrows/Enter/Escape)" : "CabinetInput.json loaded") + "; enabled=" + map.Enabled);
            }
            catch (Exception ex)
            {
                map = CabinetInputMap.Defaults();
                StartupLog.Write("Cabinet input: configuration rejected; using safe defaults: " + ex);
            }
            gate.Reset();
        }
        private static void Accelerator(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            try
            {
                // Ordinary keys use XAML preview + OriginalKey, preserving translated gamepad keys.
                // Alt/system keys may not take that route, so observe their system notifications too.
                bool down = args.EventType == CoreAcceleratorKeyEventType.SystemKeyDown;
                bool up = args.EventType == CoreAcceleratorKeyEventType.SystemKeyUp;
                if (down || up)
                {
                    if (Process(args.VirtualKey, down, args.KeyStatus.WasKeyDown)) args.Handled = true;
                }
            }
            catch (Exception ex) { StartupLog.Write("Cabinet input accelerator failed: " + ex); }
        }
        private static bool CanConsume(VirtualKey key)
        {
            int token; CabinetAction action;
            return ActiveTarget && map.TryResolve((int)key, out token, out action) &&
                (!EditingText() || action == CabinetAction.Back || action == CabinetAction.Select);
        }
        private static bool ActiveTarget
        {
            get
            {
                if (!Enabled || !foreground || targets.Count == 0) return false;
                var scope = targets[targets.Count - 1].Scope;
                if (scope is Page) return ReferenceEquals((Window.Current.Content as Frame)?.Content, scope);
                return (scope as FrameworkElement)?.IsLoaded == true;
            }
        }
        private static bool Process(VirtualKey key, bool down, bool wasDown)
        {
            int token; CabinetAction action;
            if (!Enabled) return false;
            if (!map.TryResolve((int)key, out token, out action))
                // Do not accidentally use unassigned Space as a second Select button.
                // Its physical cabinet identity is still unconfirmed; text editing remains native.
                return key == VirtualKey.Space && ActiveTarget && !EditingText();
            if (!down) { gate.Up(token); return CanConsume(key); }
            if (!CanConsume(key)) return false;
            if (gate.Down(token, action, wasDown, clock.ElapsedMilliseconds, map))
            {
                // Keep the press state across page/dialog transitions, until key-up.
                try
                {
                    if (action != CabinetAction.Favorites && action != CabinetAction.AddFavorite) targets[targets.Count - 1].Action(action);
                }
                catch (Exception ex) { StartupLog.Write("Cabinet action " + action + " failed: " + ex); }
            }
            return true; // Consume mapped keyboard keys before native focus/ScrollViewer processing.
        }
        private static bool EditingText()
        {
            var node = FocusManager.GetFocusedElement() as DependencyObject;
            while (node != null)
            {
                if (node is TextBox || node is PasswordBox || node is RichEditBox) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }
        internal static bool Contains(UIElement scope, DependencyObject element)
        {
            while (element != null) { if (ReferenceEquals(element, scope)) return true; element = VisualTreeHelper.GetParent(element); }
            return false;
        }
        internal static void Invoke(Button button)
        {
            if (button == null || !button.IsEnabled || button.Visibility != Visibility.Visible) return;
            var peer = FrameworkElementAutomationPeer.FromElement(button) ?? new ButtonAutomationPeer(button);
            var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
            if (invoke != null) invoke.Invoke();
        }
        internal static void MoveFocus(UIElement scope, CabinetAction action)
        {
            var direction = action == CabinetAction.PreviousGame ? FocusNavigationDirection.Up :
                action == CabinetAction.NextGame ? FocusNavigationDirection.Down :
                action == CabinetAction.PreviousCategory ? FocusNavigationDirection.Left : FocusNavigationDirection.Right;
            FocusManager.TryMoveFocus(direction, new FindNextElementOptions { SearchRoot = scope });
        }
        private sealed class Registration : IDisposable
        {
            private readonly UIElement scope;
            internal UIElement Scope => scope;
            internal readonly Action<CabinetAction> Action;
            private readonly KeyEventHandler down, up;
            internal Registration(UIElement scope, Action<CabinetAction> action)
            {
                this.scope = scope; Action = action;
                down = (sender, args) => Preview(args, true);
                up = (sender, args) => Preview(args, false);
                scope.AddHandler(UIElement.PreviewKeyDownEvent, down, true);
                scope.AddHandler(UIElement.PreviewKeyUpEvent, up, true);
            }
            private void Preview(KeyRoutedEventArgs args, bool isDown)
            {
                try
                {
                    if (targets.Count > 0 && targets[targets.Count - 1] == this && Process(args.OriginalKey, isDown, args.KeyStatus.WasKeyDown)) args.Handled = true;
                }
                catch (Exception ex) { StartupLog.Write("Cabinet input preview failed: " + ex); }
            }
            public void Dispose()
            {
                scope.RemoveHandler(UIElement.PreviewKeyDownEvent, down);
                scope.RemoveHandler(UIElement.PreviewKeyUpEvent, up);
                targets.Remove(this);
            }
        }
    }
}
