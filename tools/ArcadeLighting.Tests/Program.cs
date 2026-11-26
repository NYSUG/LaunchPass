using LaunchPass.Catalog;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

int checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
}

var settings = ArcadeLightingSettings.Parse("{\"enabled\":true,\"baseUrl\":\"http://192.168.68.79\",\"timeoutMs\":250}");
var handler = new FirmwareHandler();
var logs = new List<string>();
using var client = new ArcadeLightingClient(settings, logs.Add, handler);
using var disabled = new ArcadeLightingClient(ArcadeLightingSettings.Parse("{\"enabled\":false}"), logs.Add, handler);
Check(await disabled.GetStatusAsync() == null && handler.Count == 0, "Disabled settings send no traffic.");
foreach (string url in new[] { "https://192.168.68.79", "http://user@192.168.68.79", "http://192.168.68.79/api", "http://192.168.68.79/?test" })
{
    bool rejected = false;
    try { ArcadeLightingSettings.Parse(JsonSerializer.Serialize(new { enabled = true, baseUrl = url })); }
    catch (FormatException) { rejected = true; }
    Check(rejected, "Invalid origin: " + url);
}

var status = await client.GetStatusAsync();
Check(status?.FirmwareVersion == "0.2.0" && status.EnabledZoneCount == 2 && status.UptimeMs == 4294967296, "Status fields.");
var zones = await client.GetZonesAsync();
Check(zones?.Count == 2 && zones[0].Color.SequenceEqual(new byte[] { 0, 255, 0 }) && zones[1].Effect == "off", "Zone fields.");
Check((await client.SetZoneAsync("left", LightingEffect.Chase(255, 0, 0, 180, 0)))?.Revision == 7, "Zone accepted.");
using (var zone = JsonDocument.Parse(handler.Body))
{
    var root = zone.RootElement;
    Check(handler.Path == "/api/zones/left" && handler.MediaType == "application/json" &&
        root.GetProperty("effect").GetString() == "chase" && root.GetProperty("brightness").GetInt32() == 0 &&
        root.GetProperty("speed_ms").GetInt32() == 180, "Zone wire format.");
}
var green = LightingEffect.Breathe(0, 255, 0, 1600);
Check(await client.SetSceneAsync(new Dictionary<string, LightingEffect> { ["left"] = green, ["right"] = green }) != null, "Scene accepted.");
using (var scene = JsonDocument.Parse(handler.Body))
{
    var pair = scene.RootElement.GetProperty("zones");
    Check(pair.GetProperty("left").GetProperty("color")[1].GetInt32() == 255 &&
        pair.GetProperty("right").GetProperty("speed_ms").GetInt32() == 1600, "Scene object and RGB array wire format.");
}
Check(await client.BlackoutAsync() && handler.Body.Length == 0 && handler.Path == "/api/blackout", "Empty blackout POST.");
handler.Override = (HttpStatusCode.OK, "{\"blackout\":false}");
Check(!await client.BlackoutAsync(), "Blackout failure flag.");
handler.Override = (HttpStatusCode.Accepted, "{\"accepted\":true}");
Check(await client.SetZoneAsync("right", LightingEffect.Off()) == null, "Missing revision.");
handler.Override = (HttpStatusCode.Forbidden, "{\"error\":\"local IPv4 subnet required\"}");
Check(await client.GetStatusAsync() == null && logs.Last().Contains("403"), "HTTP failure.");
handler.Override = (HttpStatusCode.OK, "<html>");
Check(await client.GetStatusAsync() == null && logs.Last().Contains("failed"), "Malformed response.");
handler.Override = (HttpStatusCode.OK, new string('x', 17000));
Check(await client.GetStatusAsync() == null, "Oversized response.");
handler.Override = null;
handler.Slow = true;
using (var cancel = new CancellationTokenSource())
{
    var pending = client.GetStatusAsync(cancel.Token);
    Check(await client.GetZonesAsync() == null, "Concurrent call is dropped.");
    cancel.Cancel();
    Check(await pending == null && logs.Last().Contains("canceled"), "Caller cancellation.");
}
Check(await client.GetStatusAsync() == null && logs.Last().Contains("timed out"), "Short request timeout.");
handler.Slow = false;
Check(await client.GetStatusAsync() != null, "Recovery after cancellation and timeout.");
Console.WriteLine($"PASS: {checks} lighting assertions.");

sealed class FirmwareHandler : HttpMessageHandler
{
    public int Count;
    public bool Slow;
    public string Path, Body, MediaType;
    public (HttpStatusCode, string)? Override;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Count++;
        if (Slow) await Task.Delay(Timeout.Infinite, token);
        Path = request.RequestUri.AbsolutePath;
        Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync();
        MediaType = request.Content?.Headers.ContentType?.MediaType;
        var result = Override ?? (Path switch
        {
            "/api/status" when request.Method == HttpMethod.Get => (HttpStatusCode.OK,
                "{\"firmware_version\":\"0.2.0\",\"uptime_ms\":4294967296,\"ip_address\":\"192.168.68.79\",\"wifi_status\":\"connected\",\"enabled_zone_count\":2}"),
            "/api/zones" when request.Method == HttpMethod.Get => (HttpStatusCode.OK,
                "{\"zones\":[{\"name\":\"left\",\"gpio\":25,\"led_count\":10,\"enabled\":true,\"reverse\":false,\"effect\":\"breathe\",\"color\":[0,255,0],\"brightness\":255,\"speed_ms\":1600},{\"name\":\"right\",\"gpio\":26,\"led_count\":10,\"enabled\":true,\"reverse\":false,\"effect\":\"off\"}]}"),
            "/api/blackout" when request.Method == HttpMethod.Post => (HttpStatusCode.OK, "{\"blackout\":true}"),
            _ when request.Method == HttpMethod.Post => (HttpStatusCode.Accepted, "{\"accepted\":true,\"revision\":7}"),
            _ => throw new InvalidOperationException("Unexpected request.")
        });
        return new HttpResponseMessage(result.Item1) { Content = new StringContent(result.Item2, Encoding.UTF8, "application/json") };
    }
}
