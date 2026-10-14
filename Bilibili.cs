using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kotoshiro;

public record BilibiliQuality(int Id, string Name) { public override string ToString() => Name; }
public record BilibiliStream(long RoomId, Uri Source, string RoomPage, int Quality = 0, IReadOnlyList<BilibiliQuality>? Qualities = null);
public sealed class BilibiliResolver : IDisposable
{
    private readonly HttpClient client;
    public BilibiliResolver(HttpMessageHandler? handler = null)
    {
        client = handler == null ? new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) : new(handler);
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        client.DefaultRequestHeaders.Referrer = new Uri("https://live.bilibili.com/");
    }
    public static long RoomId(string input)
    {
        string value = input.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is not ("http" or "https") || uri.Host != "live.bilibili.com" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort) throw new ArgumentException("请粘贴 live.bilibili.com 的直播间链接，或输入房间号。");
            value = uri.AbsolutePath.Trim('/');
        }
        if (!Regex.IsMatch(value, "^[0-9]{1,12}$") || !long.TryParse(value, out long id) || id <= 0) throw new ArgumentException("直播间地址无效。示例：https://live.bilibili.com/22748536");
        return id;
    }
    public static string QualityName(int id) => id switch { 0 => "自动", 80 => "流畅", 150 => "高清", 250 => "超清", 400 => "蓝光", 10000 => "原画", 15000 => "2K", 20000 => "4K", 30000 => "杜比", _ => $"画质 {id}" };
    public async Task<BilibiliStream> ResolveAsync(string input, CancellationToken token, int quality = 0, string? cookieHeader = null)
    {
        if (quality < 0 || quality > 100000) throw new ArgumentException("清晰度参数无效。");
        long id = RoomId(input);
        var room = await ApiAsync($"https://api.live.bilibili.com/room/v1/Room/room_init?id={id}", token, cookieHeader);
        var data = room.GetProperty("data");
        if (data.GetProperty("live_status").GetInt32() != 1) throw new InvalidOperationException($"直播间 {id} 目前未开播，请开播后重新打开。");
        if ((data.TryGetProperty("encrypted", out var encrypted) && encrypted.GetBoolean()) || (data.TryGetProperty("is_locked", out var locked) && locked.GetBoolean())) throw new InvalidOperationException("此直播间需要额外授权或已被限制，暂时无法在独立播放器打开。");
        id = data.GetProperty("room_id").GetInt64();
        string? source = null; int actualQuality = 0; var qualities = new List<BilibiliQuality> { new(0, "自动") };
        try
        {
            var play = await ApiAsync($"https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?room_id={id}&no_playurl=0&mask=1&qn={quality}&platform=web&protocol=0,1&format=0,1,2&codec=0&dolby=5&panorama=1", token, cookieHeader);
            if (play.GetProperty("data").TryGetProperty("playurl_info", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                var playurl = info.GetProperty("playurl");
                var descriptions = new Dictionary<int, string>();
                if (playurl.TryGetProperty("g_qn_desc", out var names)) foreach (var name in names.EnumerateArray())
                { if (name.TryGetProperty("qn", out var qn) && qn.TryGetInt32(out int number) && name.TryGetProperty("desc", out var desc)) descriptions[number] = desc.GetString() ?? QualityName(number); }
                foreach (var stream in playurl.GetProperty("stream").EnumerateArray())
                foreach (var format in stream.GetProperty("format").EnumerateArray())
                foreach (var codec in format.GetProperty("codec").EnumerateArray())
                {
                    if (codec.GetProperty("codec_name").GetString() != "avc") continue;
                    if (codec.TryGetProperty("accept_qn", out var accepts)) foreach (var qn in accepts.EnumerateArray())
                    { int number = qn.GetInt32(); if (number > 0 && !qualities.Any(x => x.Id == number)) qualities.Add(new(number, descriptions.GetValueOrDefault(number, QualityName(number)))); }
                    if (source != null) continue; // Prefer the first FLV route; do not overwrite it with later HLS formats.
                    var route = codec.GetProperty("url_info").EnumerateArray().FirstOrDefault();
                    if (route.ValueKind == JsonValueKind.Object)
                    { source = route.GetProperty("host").GetString() + codec.GetProperty("base_url").GetString() + route.GetProperty("extra").GetString(); actualQuality = codec.TryGetProperty("current_qn", out var current) ? current.GetInt32() : 0; }
                }
            }
        }
        catch (HttpRequestException) { }
        if (source == null)
        {
            var play = await ApiAsync($"https://api.live.bilibili.com/room/v1/Room/playUrl?cid={id}&platform=web&quality=4", token, cookieHeader);
            if (play.GetProperty("data").TryGetProperty("durl", out var urls)) source = urls.EnumerateArray().Select(x => x.GetProperty("url").GetString()).FirstOrDefault(x => x != null);
        }
        if (!Uri.TryCreate(source, UriKind.Absolute, out var media) || media.Scheme is not ("https" or "http") || media.UserInfo.Length > 0 || media.IsLoopback) throw new InvalidOperationException("B 站未提供可用直播源。请确认房间已开播；需要登录或受限的房间暂不支持。");
        return new(id, media, $"https://live.bilibili.com/{id}", actualQuality, qualities.OrderBy(x => x.Id).ToArray());
    }
    private async Task<JsonElement> ApiAsync(string url, CancellationToken token, string? cookieHeader)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(cookieHeader)) request.Headers.Add("Cookie", cookieHeader);
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"B 站连接未完成（HTTP {(int)response.StatusCode}），请稍后再试。");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); var root = json.RootElement;
        if (!root.TryGetProperty("code", out var code) || code.GetInt32() != 0) throw new HttpRequestException("B 站未接受直播请求。请检查房间号或稍后重试。");
        return root.Clone();
    }
    public void Dispose() => client.Dispose();
}
