using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Kotoshiro;

internal static class BilibiliDanmakuTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
    internal static async Task<List<string>> UnitAsync()
    {
        var results = new List<string>();
        byte[] normal = BilibiliDanmakuPackets.Make(5, Encoding.UTF8.GetBytes("{\"cmd\":\"DANMU_MSG:4:0:2:2:2:0\",\"info\":[[],\"你好😀\\n直播\",[321,\"观众\\u202eA\"]]}"), 0);
        var plain = BilibiliDanmakuPackets.Parse(normal);
        Check(plain.Comments.Count == 1 && plain.Comments[0].UserId == 321 && plain.Comments[0].UserName == "观众 A" && plain.Comments[0].Text == "你好😀 直播", "normal comment extraction and safe unicode");
        byte[] auth = BilibiliDanmakuPackets.Make(8, "{\"code\":0}"u8.ToArray());
        foreach (ushort version in new ushort[] { 2, 3 })
        {
            byte[] inner = normal.Concat(normal).ToArray();
            var batch = BilibiliDanmakuPackets.Parse(auth.Concat(BilibiliDanmakuPackets.Make(5, Compress(inner, version), version)).ToArray());
            Check(batch.AuthCode == 0 && batch.Comments.Count == 2, "auth + multiple compressed packet comments");
        }
        var heartbeat = BilibiliDanmakuPackets.Make(3, [0, 0, 0, 100]);
        Check(BilibiliDanmakuPackets.Parse(heartbeat.Concat("{}"u8.ToArray()).ToArray()).Comments.Count == 0, "heartbeat server trailing bytes do not interrupt connection");
        Check(BilibiliDanmakuPackets.Parse(heartbeat.Concat(normal).ToArray()).Comments.Count == 1, "heartbeat does not drop valid following comment packet");
        foreach (byte[] malformed in new[] { new byte[15], new byte[16], normal[..^1] })
        { try { BilibiliDanmakuPackets.Parse(malformed); throw new Exception("bad packet accepted"); } catch (InvalidDataException) { } }
        byte[] badHeader = normal.ToArray(); BinaryPrimitives.WriteUInt16BigEndian(badHeader.AsSpan(4), ushort.MaxValue);
        try { BilibiliDanmakuPackets.Parse(badHeader); throw new Exception("bad header accepted"); } catch (InvalidDataException) { }
        byte[] bomb = new byte[BilibiliDanmakuPackets.MaximumBytes + 1];
        try { BilibiliDanmakuPackets.Parse(BilibiliDanmakuPackets.Make(5, Compress(bomb, 3), 3)); throw new Exception("compressed bomb accepted"); } catch (InvalidDataException) { }
        Check(BilibiliDanmakuPackets.SafeText("😀😀😀", 2) == "😀😀", "unicode length clamp does not split surrogate pair");
        results.Add("PASS: danmaku op 8 auth, op 5 plain/multiple/zlib/Brotli comments, heartbeat trailing data, malformed packet/header rejection, decompression limit and unicode sanitization");

        using (var keys = JsonDocument.Parse("{\"data\":{\"wbi_img\":{\"img_url\":\"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png\",\"sub_url\":\"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png\"}}}"))
        {
            string mixin = BilibiliDanmakuSigning.Mixin(keys.RootElement);
            Check(mixin == "ea1db124af3c7062474693fa704f4ff8", "website WBI mixin permutation");
            string signed = BilibiliDanmakuSigning.Sign(22748536, mixin, 1702204169);
            Check(signed.StartsWith("id=22748536&type=0&wts=1702204169&w_rid=") && signed.Length == 72, "signed sorted room query");
        }
        results.Add("PASS: website-issued WBI key permutation and sorted room query signing");
        Check(BilibiliDanmakuHttp.ChatServer("broadcastlv.chat.bilibili.com", 2245)?.Port == 2245 && BilibiliDanmakuHttp.ChatServer("broadcastlv.chat.bilibili.com", 443)?.Port == 443, "official WSS current and legacy ports");
        foreach (var invalid in new[] { ("127.0.0.1", 2245), ("evil.chat.bilibili.com.evil.test", 2245), ("broadcastlv.chat.bilibili.com", 80), ("secret@broadcastlv.chat.bilibili.com", 2245) })
            Check(BilibiliDanmakuHttp.ChatServer(invalid.Item1, invalid.Item2) == null, "untrusted chat host or port rejected");
        results.Add("PASS: current official WSS port 2245 and legacy 443 accepted; unrelated hosts, credentials and other ports rejected");

        var account = new BilibiliAccount(321, "模拟用户", "SESSDATA=fake-session; bili_jct=fake-csrf; DedeUserID=321", "fake-csrf", DateTimeOffset.UtcNow.AddHours(1));
        var handler = new SendHandler();
        using (var sender = new BilibiliDanmakuSender(handler))
        {
            await sender.SendCoreAsync(22748536, "模拟弹幕 😀", account, CancellationToken.None);
            Check(handler.Count == 1 && handler.Uri == "https://api.live.bilibili.com/msg/send" && handler.Method == HttpMethod.Post && handler.Cookie == account.CookieHeader, "only official HTTPS send endpoint and own account cookie");
            var fields = handler.Body.Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => WebUtility.UrlDecode(x[1]));
            Check(fields["roomid"] == "22748536" && fields["msg"] == "模拟弹幕 😀" && fields["csrf"] == "fake-csrf" && fields["csrf_token"] == "fake-csrf", "form encoding and own-session CSRF match");
            handler.Response = "{\"code\":10031,\"message\":\"raw-secret-body\"}";
            try { await sender.SendCoreAsync(22748536, "模拟弹幕", account, CancellationToken.None); throw new Exception("frequency rejected send accepted"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("过快") && !ex.Message.Contains("secret"), "safe numeric send errors"); }
            handler.Response = "{\"code\":0,\"msg\":\"raw risk failure\"}";
            try { await sender.SendCoreAsync(22748536, "模拟弹幕", account, CancellationToken.None); throw new Exception("code 0 risk message accepted"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("未确认"), "nonempty failure message cannot falsely mark sent"); }
            int before = handler.Count;
            foreach (string bad in new[] { " ", "含有\n换行", new string('中', 101) })
            { try { await sender.SendCoreAsync(22748536, bad, account, CancellationToken.None); throw new Exception("invalid comment sent"); } catch (ArgumentException) { } }
            var mismatch = new BilibiliAccount(321, "模拟用户", "SESSDATA=fake-session; bili_jct=other", "fake-csrf", DateTimeOffset.UtcNow.AddHours(1));
            try { await sender.SendCoreAsync(22748536, "模拟弹幕", mismatch, CancellationToken.None); throw new Exception("csrf mismatch sent"); } catch (InvalidOperationException) { }
            Check(handler.Count == before, "invalid text and mismatched own-session CSRF never sent");
        }
        results.Add("PASS: fake transport only; send endpoint/own cookie/CSRF/form text, safe rejection/rate/length handling and code-0 failure message. No public message sent.");
        return results;
    }

    private static byte[] Compress(byte[] bytes, ushort version)
    {
        using var output = new MemoryStream();
        using (Stream compressor = version == 3 ? new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true) : new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) compressor.Write(bytes);
        return output.ToArray();
    }
    private sealed class SendHandler : HttpMessageHandler
    {
        internal int Count; internal string Uri = "", Cookie = "", Body = "", Response = "{\"code\":0,\"message\":\"0\",\"msg\":\"\"}";
        internal HttpMethod? Method;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Count++; Uri = request.RequestUri!.AbsoluteUri; Method = request.Method;
            Cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single() : "";
            Body = await request.Content!.ReadAsStringAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
        }
    }

    // Bounded, anonymous, read-only integration check. Report stores only counts
    // and our safe status labels; viewer names, message text and auth tokens stay out.
    internal static async Task LiveReadAsync(long roomId, string folder, int seconds = 30)
    {
        Directory.CreateDirectory(folder); int count = 0, connected = 0;
        var statuses = new ConcurrentQueue<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 5));
        await using var client = new BilibiliDanmakuClient();
        client.CommentReceived += _ => Interlocked.Increment(ref count);
        client.StatusChanged += value => { statuses.Enqueue(value); if (value == "实时弹幕已连接") Interlocked.Exchange(ref connected, 1); };
        await client.StartAsync(roomId, null, deadline.Token);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        deadline.Cancel();
        await client.DisposeAsync();
        await File.WriteAllTextAsync(Path.Combine(folder, "bilibili-danmaku-read.txt"), DateTimeOffset.Now.ToString("O") + $"\nAnonymous read-only room {roomId}; elapsed {seconds} seconds.\nWebSocket authenticated: {connected == 1}; received comment count: {count}.\n" + string.Join("\n", statuses.Distinct()) + "\nSafe diagnostic (stage/type only): " + client.LastDiagnostic + "\nNo viewer identity, message text, cookie or auth token saved. No public message sent.");
    }
}
