using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LaunchPass.Catalog
{
    // Called by the cabinet session on its UI dispatcher. One pending selection replaces the previous one.
    public sealed class ArcadeLightingDirector
    {
        private const double CoalesceMs = 125;
        private const double MinimumSceneIntervalMs = 250;

        private static readonly LightingEffect Green = LightingEffect.Breathe(0, 255, 0, 1600);
        private static readonly Dictionary<string, LightingEffect> StartupScene = Pair(Green, Green);
        private static readonly Dictionary<string, LightingEffect> FirstPalette =
            Pair(LightingEffect.Solid(255, 0, 0), LightingEffect.Solid(0, 0, 255));
        private static readonly Dictionary<string, LightingEffect> SecondPalette =
            Pair(LightingEffect.Solid(0, 160, 255), LightingEffect.Solid(255, 140, 0));

        private readonly ArcadeLightingClient client;
        private readonly Action<string> log;
        private string selection;
        private bool startupPending = true;
        private bool statusPending = true;
        private bool blackoutPending;
        private bool selectionPending;
        private bool busy;
        private bool secondPalette;
        private double selectionReadyAt;
        private double nextSceneAt;

        public ArcadeLightingDirector(ArcadeLightingClient client, Action<string> log)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.log = log;
        }

        public void SelectionChanged(string id, double now)
        {
            if (string.IsNullOrEmpty(id) || string.Equals(selection, id, StringComparison.OrdinalIgnoreCase)) return;
            bool firstSelection = selection == null;
            selection = id;
            if (firstSelection) return;

            if (!selectionPending) selectionReadyAt = Math.Max(now + CoalesceMs, nextSceneAt);
            selectionPending = true;
        }

        public void Blackout()
        {
            startupPending = false;
            selectionPending = false;
            blackoutPending = true;
        }

        public async Task TickAsync(double now)
        {
            if (busy) return;
            busy = true;
            try
            {
                if (blackoutPending)
                {
                    blackoutPending = false;
                    await client.BlackoutAsync();
                }
                else if (startupPending)
                {
                    startupPending = false;
                    nextSceneAt = now + MinimumSceneIntervalMs;
                    await client.SetSceneAsync(StartupScene);
                }
                else if (statusPending)
                {
                    statusPending = false;
                    var status = await client.GetStatusAsync();
                    if (status != null)
                        await LogAsync("ESP32 firmware " + status.FirmwareVersion + " at " + status.IpAddress +
                            ", Wi-Fi " + status.WifiStatus + ", enabled zones " + status.EnabledZoneCount);
                }
                else if (selectionPending && now >= Math.Max(selectionReadyAt, nextSceneAt))
                {
                    selectionPending = false;
                    nextSceneAt = now + MinimumSceneIntervalMs;
                    var palette = secondPalette ? SecondPalette : FirstPalette;
                    secondPalette = !secondPalette;
                    var id = selection;
                    var result = await client.SetSceneAsync(palette);
                    if (result != null) await LogAsync("Selection " + id + " queued at revision " + result.Revision);
                }
            }
            finally { busy = false; }
        }

        private Task LogAsync(string message) => log == null ? Task.CompletedTask : Task.Run(() => log(message));

        private static Dictionary<string, LightingEffect> Pair(LightingEffect left, LightingEffect right)
            => new Dictionary<string, LightingEffect> { ["left"] = left, ["right"] = right };
    }
}
