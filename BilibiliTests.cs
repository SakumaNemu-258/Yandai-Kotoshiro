using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Kotoshiro;

internal static class BilibiliTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
    internal static async Task<List<string>> UnitAsync()
    {
        var results = new List<string>();
        Check(BilibiliResolver.RoomId(" https://live.bilibili.com/22748536?broadcast_type=0&is_room_feed=1 ") == 22748536 && BilibiliResolver.RoomId("22748536") == 22748536, "room and tracked URL parsing");
        foreach (string bad in new[] { "0", "-1", "https://example.com/123", "https://live.bilibili.com.evil.test/123", "https://secret@live.bilibili.com/123", "https://live.bilibili.com:8443/123", "https://live.bilibili.com/123/456", "file:///123" })
        { try { BilibiliResolver.RoomId(bad); throw new Exception("bad input accepted"); } catch (ArgumentException) { } }
        using (var resolver = new BilibiliResolver(new RoomHandler()))
        {
            var source = await resolver.ResolveAsync("123", CancellationToken.None);
            Check(source.RoomId == 22748536 && source.RoomPage == "https://live.bilibili.com/22748536" && source.Source.AbsoluteUri == "https://example.bilivideo.com/live.flv?fixture=1", "short room canonical id and live source");
        }
        using (var resolver = new BilibiliResolver(new RoomHandler { Offline = true }))
        { try { await resolver.ResolveAsync("123", CancellationToken.None); throw new Exception("offline source accepted"); } catch (InvalidOperationException ex) { Check(ex.Message.Contains("未开播"), "offline message"); } }
        using (var resolver = new BilibiliResolver(new RoomHandler { V2 = true }))
        { var source = await resolver.ResolveAsync("123", CancellationToken.None); Check(source.Source.AbsoluteUri == "https://example.bilivideo.com/live.m3u8?fixture=2", "v2 source fallback"); }
        var qualityHandler = new RoomHandler { Quality = true };
        using (var resolver = new BilibiliResolver(qualityHandler))
        {
            var source = await resolver.ResolveAsync("123", CancellationToken.None, 10000, "fixture=quality-cookie");
            Check(qualityHandler.RequestedQuality && qualityHandler.CookieSeen && source.Quality == 250, "requested quality and own account cookie, server downgrade exposed");
            Check(source.Qualities!.Select(x => x.Id).SequenceEqual(new[] { 0, 250, 400, 10000 }) && source.Qualities!.Last().Name == "原画", "quality choices derived from AVC accept list");
            Check(source.Source.AbsoluteUri.EndsWith("live.flv?fixture=3"), "first FLV route preserved across HLS alternatives");
        }
        results.Add("PASS: available room quality list, requested quality, account header, server downgrade and FLV route preference");
        results.Add("PASS: Bilibili tracked URL/room parsing, invalid input rejection, short room ID resolution, simulated public source and v2 fallback, offline status");
        var parts = VideoBuffer.ParsePlaylist("#EXTM3U\n#EXTINF:2.500,\npart00000000.ts\n#EXTINF:3.000,\npart00000001.ts\n");
        Check(parts.Count == 2 && parts[0].Index == 0 && parts[1].Duration == 3, "playlist durations");
        foreach (string bad in new[] { "#EXTINF:2,\n../../file.ts\n", "#EXTINF:NaN,\npart00000000.ts\n", "#EXTINF:-1,\npart00000000.ts\n" })
        { try { VideoBuffer.ParsePlaylist(bad); throw new Exception("unsafe playlist accepted"); } catch (InvalidDataException) { } }
        var cues = new[] { new SubtitleCue(0, 6, "first", "第一段"), new SubtitleCue(6, 12, "second", "第二段") };
        Check(SubtitleTimeline.At(cues, 5.9)?.Chinese == "第一段" && SubtitleTimeline.At(cues, 6)?.Chinese == "第二段" && SubtitleTimeline.At(cues, 12) == null, "subtitles use playback clock and interval boundaries");
        Check(SubtitleTimeline.ShouldHold(6, 20, 6, 10, true, false) && !SubtitleTimeline.ShouldHold(6, 20, 12, 10, true, false) && !SubtitleTimeline.ShouldHold(6, 20, 6, 10, false, false), "missing translation waits optionally and resumes when ready");
        results.Add("PASS: safe HLS playlist, duration checks, playback clock cue selection, subtitle wait/resume and wait-disabled behavior");
        Check(VideoBuffer.ClassifyFailure("[tls @ 123] error:0A000086 https://example.test/private?token=secret") == "HTTPS 证书校验或握手未完成", "TLS diagnostic classification");
        Check(VideoBuffer.ClassifyFailure("HTTP error 403 https://example.test/private?token=secret") == "直播源返回 HTTP 403", "HTTP diagnostic classification without signed URL");
        Check(VideoBuffer.ClassifyFailure("https://example.test/private?token=secret") == null, "raw signed URL is discarded");
        results.Add("PASS: live transport diagnostics retain categories only, signed URL and query parameters discarded");
        return results;
    }
    internal static async Task CheckRoomAsync(string room, string folder)
    {
        Directory.CreateDirectory(folder); using var resolver = new BilibiliResolver(); string outcome;
        try { var source = await resolver.ResolveAsync(room, CancellationToken.None); outcome = $"房间 {source.RoomId} 已开播，可取得直播源。未保存源地址或签名参数；本检查没有播放、识别或翻译。"; }
        catch (InvalidOperationException ex) when (ex.Message.Contains("未开播")) { outcome = ex.Message + "\n已完成真实 B 站房间状态请求；因未开播，尚未验证该房间的直播拉取、播放与翻译。"; }
        await File.WriteAllTextAsync(Path.Combine(folder, "bilibili-room.txt"), DateTimeOffset.Now.ToString("O") + "\n" + outcome);
    }
    internal static async Task PlayerAsync(string input, string folder)
    {
        Directory.CreateDirectory(folder); SettingsStore.SetModelDirectory(folder); var results = new List<string>();
        MediaTools.Check(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var token = timeout.Token;
        var buffer = new VideoBuffer(folder); string cache = buffer.Folder; using var player = new VideoPlayer();
        try
        {
            buffer.StartFile(input); var parts = new List<VideoPiece>(); var timer = Stopwatch.StartNew();
            while (buffer.Edge < 6) { parts.AddRange(buffer.TakeNewPieces()); if (buffer.Failed || timer.Elapsed.TotalSeconds > 20) throw new Exception("recorder produced no buffer"); await Task.Delay(150, token); }
            Check(timer.Elapsed.TotalSeconds >= 5.5, "buffered in real time before playback");
            using (var http = new HttpClient(new HttpClientHandler { UseProxy = false }))
            {
                using var wrong = await http.GetAsync(new Uri(new Uri(buffer.PlaybackUrl), "/invalid/live.m3u8"), token); Check(wrong.StatusCode == HttpStatusCode.NotFound, "local server denies unrecognized session");
                using var range = new HttpRequestMessage(HttpMethod.Get, buffer.PlaybackUrl.Replace("live.m3u8", parts[0].Name)); range.Headers.Range = new(0, 9);
                using var ranged = await http.SendAsync(range, token); var bytes = await ranged.Content.ReadAsByteArrayAsync(token);
                Check(ranged.StatusCode == HttpStatusCode.PartialContent && bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(buffer.Folder, parts[0].Name)).Take(10)), "local server byte range matches own segment");
            }
            byte[] wave = await buffer.DecodeAsync(parts, token);
            using (var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(wave)))
            { Check(reader.WaveFormat.SampleRate == 16000 && reader.WaveFormat.Channels == 1 && Math.Abs(reader.TotalTime.TotalSeconds - parts.Sum(x => x.Duration)) < 0.4, "real PCM audio aligned with buffered video durations"); }
            results.Add($"PASS: real FFmpeg HLS recording and early mono 16kHz decode; buffered {buffer.Edge:F2}s before player startup, decoded audio duration aligned within 0.4s");
            results.Add("PASS: loopback-only HTTP playback with private session path, invalid session rejected, valid byte range served");
            await player.StartAsync(buffer.PlaybackUrl, IntPtr.Zero, token, headless: true);
            double initial = await player.PositionAsync(token) ?? -1; Check(initial >= 0 && initial < 0.5, "starts at buffer beginning rather than live edge");
            var hasVideo = await player.PropertyAsync("video-params", token); var hasAudio = await player.PropertyAsync("audio-params", token);
            Check(hasVideo is { ValueKind: JsonValueKind.Object } && hasAudio is { ValueKind: JsonValueKind.Object }, "real player decodes audio and video");
            await player.CaptionAsync("日本語テスト\n中文字幕 · 与播放器时间同步", token);
            await player.PauseAsync(false, token); await Task.Delay(1500, token);
            double first = await player.PositionAsync(token) ?? -1; Check(first >= 1 && first < 3, "clock advances independently of incoming audio");
            parts.AddRange(buffer.TakeNewPieces()); Check(buffer.Edge - first >= 4, "input remains ahead of playback");
            await player.PauseAsync(true, token); double paused = await player.PositionAsync(token) ?? -1; await Task.Delay(700, token);
            double after = await player.PositionAsync(token) ?? -1; Check(Math.Abs(paused - after) < 0.1, "pause freezes clock");
            await player.ScreenshotAsync(Path.Combine(folder, "video-frame.png"), token); Check(File.Exists(Path.Combine(folder, "video-frame.png")), "decoded video screenshot");
            await player.PauseAsync(false, token); await Task.Delay(500, token); Check((await player.PositionAsync(token) ?? -1) > paused + 0.3, "resumes playback");
            results.Add($"PASS: real mpv video/audio decoding, media starts {initial:F2}s, advances, input remains ahead, pause/resume and decoded frame screenshot");
        }
        finally { player.Dispose(); buffer.Dispose(); }
        Check(!Directory.Exists(cache) && Directory.Exists(folder), "session cache deleted, parent preserved"); results.Add("PASS: child processes closed and own cache removed, parent folder preserved");
        await File.WriteAllLinesAsync(Path.Combine(folder, "player-test.txt"), results);
    }
    internal sealed class MockTranslation : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; await Task.Delay(500, token);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"示例中文字幕：这是按视频播放时间显示的翻译。\"}}]}", Encoding.UTF8, "application/json") };
        }
    }
    private sealed class RoomHandler : HttpMessageHandler
    {
        public bool Offline, V2, Quality, RequestedQuality, CookieSeen;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.Headers.Referrer?.Host == "live.bilibili.com" && request.Headers.UserAgent.Count > 0, "request headers");
            if (request.RequestUri!.AbsolutePath.Contains("getRoomPlayInfo")) { RequestedQuality = request.RequestUri.Query.Contains("qn=10000"); CookieSeen = request.Headers.TryGetValues("Cookie", out var cookies) && cookies.Single() == "fixture=quality-cookie"; }
            string body = request.RequestUri!.AbsolutePath switch
            {
                "/room/v1/Room/room_init" => JsonSerializer.Serialize(new { code = 0, data = new { room_id = 22748536, live_status = Offline ? 0 : 1, encrypted = false, is_locked = false } }),
                "/room/v1/Room/playUrl" => V2 ? "{\"code\":0,\"data\":{\"durl\":[]}}" : "{\"code\":0,\"data\":{\"durl\":[{\"url\":\"https://example.bilivideo.com/live.flv?fixture=1\"}]}}",
                _ => Quality ? "{\"code\":0,\"data\":{\"playurl_info\":{\"playurl\":{\"g_qn_desc\":[{\"qn\":10000,\"desc\":\"原画\"}],\"stream\":[{\"format\":[{\"codec\":[{\"codec_name\":\"avc\",\"base_url\":\"live.flv\",\"current_qn\":250,\"accept_qn\":[10000,400,250],\"url_info\":[{\"host\":\"https://example.bilivideo.com/\",\"extra\":\"?fixture=3\"}]}]},{\"codec\":[{\"codec_name\":\"avc\",\"base_url\":\"later.m3u8\",\"current_qn\":250,\"accept_qn\":[250],\"url_info\":[{\"host\":\"https://example.bilivideo.com/\",\"extra\":\"?fixture=4\"}]}]}]}]}}}}" : V2 ? "{\"code\":0,\"data\":{\"playurl_info\":{\"playurl\":{\"stream\":[{\"format\":[{\"codec\":[{\"codec_name\":\"avc\",\"base_url\":\"live.m3u8\",\"url_info\":[{\"host\":\"https://example.bilivideo.com/\",\"extra\":\"?fixture=2\"}]}]}]}]}}}}" : "{\"code\":0,\"data\":{\"playurl_info\":null}}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
