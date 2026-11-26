using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LaunchPass.Catalog
{
    [DataContract]
    public sealed class ArcadeLightingSettings
    {
        [DataMember(Name = "enabled")] public bool Enabled { get; private set; }
        [DataMember(Name = "baseUrl")] public string BaseUrl { get; private set; }
        [DataMember(Name = "timeoutMs")] public int? TimeoutMs { get; private set; }

        public Uri Endpoint { get; private set; }
        public TimeSpan Timeout => TimeSpan.FromMilliseconds(TimeoutMs ?? 1000);

        public static ArcadeLightingSettings Parse(string json)
        {
            var settings = CoreSettingsJson.Read<ArcadeLightingSettings>(json);
            if (!settings.Enabled) return settings;
            Uri endpoint;
            if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out endpoint) || endpoint.Scheme != Uri.UriSchemeHttp ||
                endpoint.HostNameType != UriHostNameType.IPv4 || endpoint.AbsolutePath != "/" ||
                endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                throw new FormatException("Lighting baseUrl must be an HTTP IPv4 origin, optionally with a port.");
            if (settings.TimeoutMs.HasValue && (settings.TimeoutMs < 250 || settings.TimeoutMs > 3000))
                throw new FormatException("Lighting timeoutMs must be 250..3000.");
            settings.Endpoint = endpoint;
            return settings;
        }
    }

    [DataContract]
    public sealed class LightingEffect
    {
        [DataMember(Name = "effect")] public string Name { get; private set; }
        [DataMember(Name = "color", EmitDefaultValue = false)] public byte[] Color { get; private set; }
        [DataMember(Name = "brightness", EmitDefaultValue = false)] public byte? Brightness { get; private set; }
        [DataMember(Name = "speed_ms", EmitDefaultValue = false)] public int? SpeedMs { get; private set; }

        private LightingEffect(string name, byte[] color = null, byte? brightness = null, int? speedMs = null)
        {
            if (speedMs.HasValue && (speedMs < 20 || speedMs > 60000))
                throw new ArgumentOutOfRangeException(nameof(speedMs));
            Name = name;
            Color = color;
            Brightness = brightness;
            SpeedMs = speedMs;
        }

        public static LightingEffect Off() => new LightingEffect("off");
        public static LightingEffect Solid(byte r, byte g, byte b, byte? brightness = null)
            => new LightingEffect("solid", new[] { r, g, b }, brightness);
        public static LightingEffect Breathe(byte r, byte g, byte b, int? speedMs = null, byte? brightness = null)
            => new LightingEffect("breathe", new[] { r, g, b }, brightness, speedMs);
        public static LightingEffect Chase(byte r, byte g, byte b, int? speedMs = null, byte? brightness = null)
            => new LightingEffect("chase", new[] { r, g, b }, brightness, speedMs);
    }

    [DataContract]
    public sealed class LightingStatus
    {
        [DataMember(Name = "firmware_version", IsRequired = true)] public string FirmwareVersion { get; private set; }
        [DataMember(Name = "uptime_ms", IsRequired = true)] public ulong UptimeMs { get; private set; }
        [DataMember(Name = "ip_address", IsRequired = true)] public string IpAddress { get; private set; }
        [DataMember(Name = "wifi_status", IsRequired = true)] public string WifiStatus { get; private set; }
        [DataMember(Name = "enabled_zone_count", IsRequired = true)] public int EnabledZoneCount { get; private set; }
    }

    [DataContract]
    public sealed class LightingZone
    {
        [DataMember(Name = "name", IsRequired = true)] public string Name { get; private set; }
        [DataMember(Name = "gpio", IsRequired = true)] public int Gpio { get; private set; }
        [DataMember(Name = "led_count", IsRequired = true)] public int LedCount { get; private set; }
        [DataMember(Name = "enabled", IsRequired = true)] public bool Enabled { get; private set; }
        [DataMember(Name = "reverse", IsRequired = true)] public bool Reverse { get; private set; }
        [DataMember(Name = "effect", IsRequired = true)] public string Effect { get; private set; }
        [DataMember(Name = "color")] public byte[] Color { get; private set; }
        [DataMember(Name = "brightness")] public byte? Brightness { get; private set; }
        [DataMember(Name = "speed_ms")] public int? SpeedMs { get; private set; }
    }

    public sealed class ArcadeLightingClient : IDisposable
    {
        [DataContract]
        private sealed class ZonesResponse
        {
            [DataMember(Name = "zones", IsRequired = true)] public LightingZone[] Zones { get; set; }
        }

        [DataContract]
        private sealed class Scene
        {
            [DataMember(Name = "zones")] public Dictionary<string, LightingEffect> Zones { get; set; }
        }

        [DataContract]
        public sealed class Acceptance
        {
            [DataMember(Name = "accepted", IsRequired = true)] public bool Accepted { get; private set; }
            [DataMember(Name = "revision", IsRequired = true)] public uint Revision { get; private set; }
        }

        [DataContract]
        private sealed class BlackoutResponse
        {
            [DataMember(Name = "blackout", IsRequired = true)] public bool Blackout { get; set; }
        }

        private const int MaximumRequestBytes = 2048;
        private readonly ArcadeLightingSettings settings;
        private readonly HttpClient http;
        private readonly Action<string> log;
        private int sending;

        public ArcadeLightingClient(ArcadeLightingSettings settings, Action<string> log, HttpMessageHandler handler = null)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.log = log;
            if (settings.Enabled)
            {
                http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
                {
                    Timeout = System.Threading.Timeout.InfiniteTimeSpan,
                    MaxResponseContentBufferSize = 16 * 1024
                };
            }
        }

        public Task<LightingStatus> GetStatusAsync(CancellationToken token = default)
            => SendAsync<LightingStatus>(HttpMethod.Get, "/api/status", HttpStatusCode.OK, null,
                status => !string.IsNullOrWhiteSpace(status.FirmwareVersion) &&
                    IPAddress.TryParse(status.IpAddress, out _) && !string.IsNullOrWhiteSpace(status.WifiStatus) &&
                    status.EnabledZoneCount >= 0, token);

        public async Task<IReadOnlyList<LightingZone>> GetZonesAsync(CancellationToken token = default)
        {
            var reply = await SendAsync<ZonesResponse>(HttpMethod.Get, "/api/zones", HttpStatusCode.OK, null,
                value => value.Zones != null && Array.TrueForAll(value.Zones, ValidZone), token).ConfigureAwait(false);
            return reply?.Zones;
        }

        public Task<Acceptance> SetZoneAsync(string name, LightingEffect effect, CancellationToken token = default)
            => QueueAsync("/api/zones/" + Uri.EscapeDataString(name), effect, token);

        public Task<Acceptance> SetSceneAsync(Dictionary<string, LightingEffect> zones, CancellationToken token = default)
            => QueueAsync("/api/scene", new Scene { Zones = zones }, token);

        public async Task<bool> BlackoutAsync(CancellationToken token = default)
        {
            var reply = await SendAsync<BlackoutResponse>(HttpMethod.Post, "/api/blackout", HttpStatusCode.OK, null,
                value => value.Blackout, token).ConfigureAwait(false);
            return reply != null;
        }

        private Task<Acceptance> QueueAsync<T>(string path, T body, CancellationToken token)
            => SendAsync<Acceptance>(HttpMethod.Post, path, HttpStatusCode.Accepted, () => CoreSettingsJson.Write(body),
                value => value.Accepted, token);

        private static bool ValidZone(LightingZone zone)
        {
            if (zone == null || string.IsNullOrWhiteSpace(zone.Name) || zone.LedCount < 0) return false;
            if (zone.Effect == "off") return true;
            if (zone.Color?.Length != 3 || !zone.Brightness.HasValue) return false;
            if (zone.Effect == "solid") return true;
            return (zone.Effect == "breathe" || zone.Effect == "chase") && zone.SpeedMs >= 20 && zone.SpeedMs <= 60000;
        }

        private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpStatusCode expected,
            Func<string> body, Func<T, bool> valid, CancellationToken token) where T : class
        {
            if (http == null || token.IsCancellationRequested || Interlocked.CompareExchange(ref sending, 1, 0) != 0)
                return null;

            string operation = method + " " + path;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                using (var request = new HttpRequestMessage(method, new Uri(settings.Endpoint, path)))
                {
                    timeout.CancelAfter(settings.Timeout);
                    if (body != null)
                    {
                        string json = body();
                        if (Encoding.UTF8.GetByteCount(json) > MaximumRequestBytes)
                            throw new FormatException("Request exceeds firmware body limit.");
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    }

                    using (var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false))
                    {
                        if (response.StatusCode != expected)
                        {
                            log?.Invoke(operation + " returned HTTP " + (int)response.StatusCode);
                            return null;
                        }

                        var reply = CoreSettingsJson.Read<T>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                        if (reply == null || !valid(reply)) throw new FormatException("Unexpected response fields.");
                        log?.Invoke(operation + " succeeded (HTTP " + (int)expected + ")");
                        return reply;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                log?.Invoke(operation + (token.IsCancellationRequested ? " canceled" : " timed out"));
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is FormatException || ex is SerializationException ||
                ex is InvalidOperationException || ex is System.IO.IOException)
            {
                log?.Invoke(operation + " failed: " + ex.Message);
            }
            finally { Volatile.Write(ref sending, 0); }
            return null;
        }

        public void Dispose() => http?.Dispose();
    }
}
