using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace RetroPass
{
    // Observation only: never changes Handled, focus, navigation or key mappings.
    internal sealed class InputDiagnostics
    {
        private const int HistoryLimit = 10;
        private const int PendingLimit = 256;
        private const int LogLimit = 256 * 1024;
        private static InputDiagnostics current;
        private readonly Grid host;
        private readonly CoreWindow window;
        private readonly UIElement routedRoot;
        private readonly Border display;
        private readonly TextBlock status = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock historyDisplay = new TextBlock { FontSize = 14, MaxHeight = 110, TextWrapping = TextWrapping.Wrap };
        private readonly Queue<string> history = new Queue<string>();
        private readonly Queue<string> pending = new Queue<string>();
        private readonly Dictionary<string, DateTimeOffset> repeatTimes = new Dictionary<string, DateTimeOffset>();
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly KeyEventHandler routedDown;
        private readonly KeyEventHandler routedUp;
        private readonly string logPath;
        private readonly string previousPath;
        private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
        private DateTimeOffset lastHeartbeat = DateTimeOffset.MinValue;
        private string activation = "not yet observed";
        private string logError;
        private long coreEvents, routedEvents, acceleratorEvents, repeatsSuppressed, dropped;
        private bool flushing, stopped;

        internal static async Task RefreshAsync(Grid host)
        {
            try
            {
                bool enabled = await ApplicationData.Current.LocalFolder.TryGetItemAsync("InputDiagnostics.enabled") is StorageFile;
                if (!enabled || !((App)Application.Current).InternalCatalogMode)
                {
                    if (current != null) { current.Stop(); current = null; }
                    return;
                }
                if (current != null && current.host == host) return;
                if (current != null) current.Stop();
                var diagnostics = new InputDiagnostics(host);
                try
                {
                    diagnostics.Start();
                    current = diagnostics;
                }
                catch { diagnostics.Stop(); throw; }
            }
            catch (Exception ex) { StartupLog.Write("Input diagnostics unavailable (startup continues): " + ex); }
        }

        private InputDiagnostics(Grid host)
        {
            this.host = host;
            window = Window.Current.CoreWindow;
            routedRoot = Window.Current.Content as UIElement;
            logPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "Input.log");
            previousPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "Input.previous.log");
            routedDown = (sender, args) => Routed("Down", args);
            routedUp = (sender, args) => Routed("Up", args);
            var content = new StackPanel { Spacing = 3 };
            content.Children.Add(new TextBlock { Text = "Input diagnostics — listening (observation only)", FontSize = 20 });
            content.Children.Add(status);
            content.Children.Add(historyDisplay);
            display = new Border
            {
                Background = new SolidColorBrush(Colors.Black),
                BorderBrush = new SolidColorBrush(Colors.Gray), BorderThickness = new Thickness(1),
                Padding = new Thickness(12), Margin = new Thickness(48, 0, 48, 12),
                Child = content, IsHitTestVisible = false, MaxHeight = 300
            };
        }

        private void Start()
        {
            Grid.SetRow(display, 1);
            host.Children.Add(display);
            window.KeyDown += CoreDown;
            window.KeyUp += CoreUp;
            window.Dispatcher.AcceleratorKeyActivated += Accelerator;
            window.Activated += Activated;
            window.VisibilityChanged += VisibilityChanged;
            if (routedRoot != null)
            {
                routedRoot.AddHandler(UIElement.KeyDownEvent, routedDown, true);
                routedRoot.AddHandler(UIElement.KeyUpEvent, routedUp, true);
            }
            timer.Tick += Tick;
            timer.Start();
            Record("SESSION enabled; UTC=" + started.ToString("O") + "; CoreWindow + accelerators + routed handledEventsToo; marker=InputDiagnostics.enabled");
            Render();
        }

        private void Stop()
        {
            stopped = true;
            // Stop is also safe after partially completed subscription setup.
            timer.Stop(); timer.Tick -= Tick;
            window.KeyDown -= CoreDown; window.KeyUp -= CoreUp;
            window.Dispatcher.AcceleratorKeyActivated -= Accelerator;
            window.Activated -= Activated; window.VisibilityChanged -= VisibilityChanged;
            if (routedRoot != null)
            {
                routedRoot.RemoveHandler(UIElement.KeyDownEvent, routedDown);
                routedRoot.RemoveHandler(UIElement.KeyUpEvent, routedUp);
            }
            host.Children.Remove(display);
        }

        private void CoreDown(CoreWindow sender, KeyEventArgs args) { Core("Down", args); }
        private void CoreUp(CoreWindow sender, KeyEventArgs args) { Core("Up", args); }
        private void Core(string type, KeyEventArgs args)
        {
            try { coreEvents++; Observe("Core", type, args.VirtualKey, args.VirtualKey, args.KeyStatus, args.Handled); }
            catch (Exception ex) { DiagnosticError(ex); }
        }
        private void Routed(string type, KeyRoutedEventArgs args)
        {
            try { routedEvents++; Observe("Routed", type, args.Key, args.OriginalKey, args.KeyStatus, args.Handled); }
            catch (Exception ex) { DiagnosticError(ex); }
        }
        private void Accelerator(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            try { acceleratorEvents++; Observe("Accelerator", args.EventType.ToString(), args.VirtualKey, args.VirtualKey, args.KeyStatus, args.Handled); }
            catch (Exception ex) { DiagnosticError(ex); }
        }
        private void Activated(CoreWindow sender, WindowActivatedEventArgs args)
        {
            try { activation = args.WindowActivationState.ToString(); Record("WINDOW activation=" + activation + " focus=" + Focus()); }
            catch (Exception ex) { DiagnosticError(ex); }
        }
        private void VisibilityChanged(CoreWindow sender, VisibilityChangedEventArgs args)
        {
            try { Record("WINDOW visible=" + args.Visible + " focus=" + Focus()); }
            catch (Exception ex) { DiagnosticError(ex); }
        }
        private void Observe(string source, string type, VirtualKey key, VirtualKey original, CorePhysicalKeyStatus physical, bool handled)
        {
            if (stopped) return;
            var now = DateTimeOffset.UtcNow;
            string repeatKey = source + ":" + type + ":" + (int)original;
            DateTimeOffset last;
            if (physical.WasKeyDown && !physical.IsKeyReleased && repeatTimes.TryGetValue(repeatKey, out last) && (now - last).TotalMilliseconds < 150)
            { repeatsSuppressed++; return; }
            // VirtualKey has a bounded value domain; cap this auxiliary dictionary as well.
            if (repeatTimes.Count >= 256) repeatTimes.Clear();
            repeatTimes[repeatKey] = now;
            string hint = original.ToString().StartsWith("Gamepad", StringComparison.Ordinal) ? "gamepad-VK" : "keyboard-style-VK";
            Record(source + " " + type + " " + Key(key) + " original=" + Key(original) + " " + hint +
                " scan=" + physical.ScanCode + " extended=" + physical.IsExtendedKey + " repeat=" + physical.RepeatCount +
                " wasDown=" + physical.WasKeyDown + " released=" + physical.IsKeyReleased + " handled=" + handled + " focus=" + Focus());
        }
        private static string Key(VirtualKey key) { return key + "(" + (int)key + "/0x" + ((int)key).ToString("X2") + ")"; }
        private static string Short(string text, int maximum)
        {
            text = (text ?? "").Replace('\r', ' ').Replace('\n', ' ');
            return text.Length <= maximum ? text : text.Substring(0, maximum);
        }
        private static string Focus()
        {
            try
            {
                object focused = FocusManager.GetFocusedElement();
                if (focused == null) return "<none>";
                var element = focused as FrameworkElement;
                return focused.GetType().Name + (element == null || string.IsNullOrEmpty(element.Name) ? "" : "#" + Short(element.Name, 64));
            }
            catch { return "<unavailable>"; }
        }
        private void Record(string message)
        {
            string line = DateTimeOffset.UtcNow.ToString("O") + " " + Short(message, 700);
            if (history.Count >= HistoryLimit) history.Dequeue();
            history.Enqueue(line);
            if (pending.Count >= PendingLimit) { pending.Dequeue(); dropped++; }
            pending.Enqueue(line);
        }
        private void DiagnosticError(Exception exception) { logError = Short(exception.Message, 160); }
        private void Render()
        {
            bool empty = coreEvents + routedEvents + acceleratorEvents == 0;
            status.Text = "Listening " + (int)(DateTimeOffset.UtcNow - started).TotalSeconds + "s; Core=" + coreEvents +
                " Routed=" + routedEvents + " Accelerator=" + acceleratorEvents + "; repeat-suppressed=" + repeatsSuppressed + " dropped=" + dropped +
                "\nFocus=" + Focus() + "; visible=" + window.Visible + "; activation=" + activation +
                "; log=" + (logError ?? "LocalState/Input.log (flush every 0.5s)") +
                (empty ? "\nNo key events received yet. Test the X-Arcade, then a standard USB keyboard or Xbox controller for comparison." : "\nOne physical press may appear in multiple event sources. Device identity is not available here.") +
                "\nDisable: remove InputDiagnostics.enabled and restart (or return to this page).";
            historyDisplay.Text = string.Join("\n", history.Reverse().Select(line => Short(line, 230)));
        }
        private async void Tick(object sender, object args)
        {
            if (stopped || flushing) return;
            try
            {
                if ((DateTimeOffset.UtcNow - lastHeartbeat).TotalSeconds >= 10)
                {
                    lastHeartbeat = DateTimeOffset.UtcNow;
                    Record("HEARTBEAT core=" + coreEvents + " routed=" + routedEvents + " accelerator=" + acceleratorEvents +
                        " repeat-suppressed=" + repeatsSuppressed + " dropped=" + dropped + " focus=" + Focus() + " visible=" + window.Visible);
                }
                Render();
                if (pending.Count == 0) return;
                var batch = new StringBuilder();
                // Even worst-case UTF-8 (4 bytes/character) stays below LogLimit per batch.
                int count = 0;
                while (pending.Count > 0 && count < 64) { batch.AppendLine(pending.Dequeue()); count++; }
                string text = batch.ToString();
                flushing = true;
                try
                {
                    await Task.Run(() =>
                    {
                        int size = Encoding.UTF8.GetByteCount(text);
                        if (File.Exists(logPath) && new FileInfo(logPath).Length + size > LogLimit)
                        {
                            if (File.Exists(previousPath)) File.Delete(previousPath);
                            File.Move(logPath, previousPath);
                        }
                        File.AppendAllText(logPath, text, new UTF8Encoding(false));
                    });
                    logError = null;
                }
                catch (Exception ex) { DiagnosticError(ex); dropped += count; }
                finally { flushing = false; }
            }
            catch (Exception ex) { DiagnosticError(ex); }
        }
    }
}
