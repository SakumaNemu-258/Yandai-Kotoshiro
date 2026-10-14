using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kotoshiro;

public sealed record BilibiliComment(long UserId, string UserName, string Text);

// The Bilibili web client protocol is also implemented by blivedm. We use the
// website's endpoints directly, with the user's own login session when present.
public sealed class BilibiliDanmakuClient : IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly object gate = new();
    private CancellationTokenSource? lifetime;
    private Task? running;
    private ClientWebSocket? activeSocket;
    private bool disposed;
    private string phase = "not-started";
    internal string LastDiagnostic { get; private set; } = "";
    public event Action<BilibiliComment>? CommentReceived;
    public event Action<string>? StatusChanged;

    public BilibiliDanmakuClient(HttpMessageHandler? handler = null)
    {
        http = BilibiliDanmakuHttp.Create(handler);
    }

    // Receiving continues in the background. Failure of the comment connection
    // never tears down the independent video/translation pipeline.
    public Task StartAsync(long roomId, BilibiliAccount? account, CancellationToken token)
    {
        if (roomId <= 0) throw new ArgumentException("直播间房间号无效。", nameof(roomId));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running != null) throw new InvalidOperationException("弹幕连接已经启动。");
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            CancellationToken runToken = lifetime.Token;
            running = Task.Run(() => RunAsync(roomId, account, runToken), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    private void Status(string text) { try { StatusChanged?.Invoke(text); } catch { } }
    private void Comment(BilibiliComment value) { try { CommentReceived?.Invoke(value); } catch { } }

    private async Task RunAsync(long roomId, BilibiliAccount? account, CancellationToken token)
    {
        int attempt = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                Status(attempt == 0 ? "正在连接弹幕…" : "正在重新连接弹幕…");
                var connection = await GetConnectionAsync(roomId, account, token).ConfigureAwait(false);
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("User-Agent", BilibiliDanmakuHttp.UserAgent);
                socket.Options.SetRequestHeader("Origin", "https://live.bilibili.com");
                if (connection.Cookie.Length > 0) socket.Options.SetRequestHeader("Cookie", connection.Cookie);
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                lock (gate) { if (disposed) return; activeSocket = socket; }
                using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
                phase = "websocket-connect";
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    await socket.ConnectAsync(connection.Servers[attempt % connection.Servers.Count], deadline.Token).ConfigureAwait(false);
                }
                var auth = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    uid = account?.UserId ?? 0, roomid = roomId, protover = 3,
                    platform = "web", type = 2, buvid = connection.Buvid, key = connection.Token
                });
                phase = "websocket-auth";
                await socket.SendAsync(BilibiliDanmakuPackets.Make(7, auth), WebSocketMessageType.Binary, true, session.Token).ConfigureAwait(false);
                using var sendGate = new SemaphoreSlim(1, 1);
                var authenticated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task receive = ReceiveAsync(socket, authenticated, session.Token);
                Task heartbeat = HeartbeatAsync(socket, sendGate, authenticated.Task, session.Token);
                try
                {
                    Task completed = await Task.WhenAny(receive, heartbeat).ConfigureAwait(false);
                    await completed.ConfigureAwait(false);
                    throw new IOException("弹幕连接已断开。");
                }
                finally
                {
                    session.Cancel(); socket.Abort();
                    try { await Task.WhenAll(receive, heartbeat).ConfigureAwait(false); } catch { }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (BilibiliDanmakuException ex)
            {
                LastDiagnostic = phase + "/" + ex.GetType().Name;
                Status(ex.Message);
                if (ex.RequiresLogin) return;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                LastDiagnostic = phase + "/" + ex.GetType().Name;
                Status(ex switch
                {
                    HttpRequestException => "弹幕网络连接失败，将自动重试。",
                    WebSocketException or IOException => "弹幕连接已断开，将自动重试。",
                    OperationCanceledException => "弹幕连接超时，将自动重试。",
                    _ => "弹幕服务暂时不可用，将自动重试。"
                });
            }
            finally { lock (gate) activeSocket = null; }
            attempt = Math.Min(attempt + 1, 1000);
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 3 * (1 << Math.Min(attempt - 1, 4)))), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, TaskCompletionSource authenticated, CancellationToken token)
    {
        bool ready = false;
        byte[] scratch = new byte[16384];
        using var message = new MemoryStream();
        while (!token.IsCancellationRequested)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(ready ? 45 : 10));
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(scratch), deadline.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) throw new IOException("弹幕连接已断开。");
                if (message.Length + result.Count > BilibiliDanmakuPackets.MaximumBytes) throw new InvalidDataException("弹幕数据超过允许大小。");
                message.Write(scratch, 0, result.Count);
            } while (!result.EndOfMessage);
            if (result.MessageType != WebSocketMessageType.Binary) continue;
            var batch = BilibiliDanmakuPackets.Parse(message.ToArray());
            if (batch.AuthCode is int code)
            {
                if (code != 0) throw new BilibiliDanmakuException("弹幕授权未完成，请重新登录 B 站后重试。", requiresLogin: true);
                if (!ready) { ready = true; phase = "websocket-receive"; authenticated.TrySetResult(); Status("实时弹幕已连接"); }
            }
            if (ready) foreach (var comment in batch.Comments) Comment(comment);
        }
    }

    private static async Task HeartbeatAsync(ClientWebSocket socket, SemaphoreSlim sendGate, Task ready, CancellationToken token)
    {
        await ready.WaitAsync(token).ConfigureAwait(false);
        byte[] packet = BilibiliDanmakuPackets.Make(2, "{}"u8.ToArray());
        while (!token.IsCancellationRequested)
        {
            await sendGate.WaitAsync(token).ConfigureAwait(false);
            try { await socket.SendAsync(packet, WebSocketMessageType.Binary, true, token).ConfigureAwait(false); }
            finally { sendGate.Release(); }
            await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        }
    }

    private sealed record Connection(string Token, string Buvid, string Cookie, List<Uri> Servers);
    private async Task<Connection> GetConnectionAsync(long roomId, BilibiliAccount? account, CancellationToken token)
    {
        string cookie = account?.CookieHeader ?? "";
        string buvid = BilibiliDanmakuHttp.CookieValue(cookie, "buvid3");
        if (buvid.Length == 0)
        {
            // A website-issued anonymous device identifier, not a fabricated ID.
            phase = "buvid";
            using var device = await BilibiliDanmakuHttp.GetJsonAsync(http, "https://api.bilibili.com/x/frontend/finger/spi", cookie, token).ConfigureAwait(false);
            if (device.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("b_3", out var value)) buvid = value.GetString() ?? "";
            if (buvid.Length == 0 || buvid.Length > 256 || buvid.Any(c => c < 32 || c == ';')) throw new InvalidDataException("无法取得 B 站弹幕设备信息。");
            cookie = cookie.TrimEnd(';', ' ') + (cookie.Length == 0 ? "" : "; ") + "buvid3=" + buvid;
        }
        phase = "nav";
        using var nav = await BilibiliDanmakuHttp.GetJsonAsync(http, "https://api.bilibili.com/x/web-interface/nav", cookie, token).ConfigureAwait(false);
        string mixin = BilibiliDanmakuSigning.Mixin(nav.RootElement);
        string query = BilibiliDanmakuSigning.Sign(roomId, mixin, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        phase = "getDanmuInfo";
        using var info = await BilibiliDanmakuHttp.GetJsonAsync(http, "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?" + query, cookie, token).ConfigureAwait(false);
        int code = BilibiliDanmakuHttp.Code(info.RootElement);
        if (code == -101) throw new BilibiliDanmakuException("B 站登录已失效，请重新登录后连接弹幕。", true);
        if (code != 0) throw new BilibiliDanmakuException(code == -352 ? "B 站暂时限制弹幕连接，请稍后重试或登录账号。" : $"B 站弹幕服务暂时不可用（{code}），将自动重试。");
        var payload = info.RootElement.GetProperty("data");
        string key = payload.GetProperty("token").GetString() ?? "";
        if (key.Length == 0 || key.Length > 8192) throw new InvalidDataException("弹幕授权信息无效。");
        var servers = new List<Uri>();
        foreach (var entry in payload.GetProperty("host_list").EnumerateArray().Take(20))
        {
            string host = entry.GetProperty("host").GetString() ?? "";
            int port = entry.TryGetProperty("wss_port", out var p) && p.TryGetInt32(out int parsed) ? parsed : 443;
            if (BilibiliDanmakuHttp.ChatServer(host, port) is Uri server) servers.Add(server);
        }
        if (servers.Count == 0) throw new InvalidDataException("没有可用的 B 站弹幕服务器。");
        return new Connection(key, buvid, cookie, servers);
    }

    public async ValueTask DisposeAsync()
    {
        Task? task;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; lifetime?.Cancel(); activeSocket?.Abort(); task = running;
        }
        if (task != null) { try { await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { } }
        http.Dispose(); lifetime?.Dispose();
    }
}

public sealed class BilibiliDanmakuSender : IDisposable
{
    private readonly HttpClient http;
    public BilibiliDanmakuSender(HttpMessageHandler? handler = null) { http = BilibiliDanmakuHttp.Create(handler); }
    public static async Task SendAsync(long roomId, string text, BilibiliAccount account, CancellationToken token)
    {
        using var sender = new BilibiliDanmakuSender();
        await sender.SendCoreAsync(roomId, text, account, token).ConfigureAwait(false);
    }
    internal async Task SendCoreAsync(long roomId, string text, BilibiliAccount account, CancellationToken token)
    {
        if (roomId <= 0) throw new ArgumentException("直播间房间号无效。");
        string content = text.Trim();
        if (content.Length == 0) throw new ArgumentException("请先输入弹幕内容。");
        if (content.EnumerateRunes().Count() > 100) throw new ArgumentException("弹幕不能超过 100 个字符，实际长度限制以直播间为准。");
        if (content.Any(char.IsControl)) throw new ArgumentException("弹幕内容不能包含换行或控制字符。");
        if (account.UserId <= 0 || account.ExpiresAt <= DateTimeOffset.UtcNow || account.CookieHeader.Length == 0 || account.Csrf.Length == 0 || BilibiliDanmakuHttp.CookieValue(account.CookieHeader, "bili_jct") != account.Csrf)
            throw new InvalidOperationException("请先登录 B 站账号后发送弹幕。");
        using var request = BilibiliDanmakuHttp.Request(HttpMethod.Post, "https://api.live.bilibili.com/msg/send", account.CookieHeader);
        request.Headers.Referrer = new Uri("https://live.bilibili.com/" + roomId.ToString(CultureInfo.InvariantCulture));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["roomid"] = roomId.ToString(CultureInfo.InvariantCulture), ["msg"] = content,
            ["csrf"] = account.Csrf, ["csrf_token"] = account.Csrf,
            ["rnd"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["color"] = "16777215", ["fontsize"] = "25", ["mode"] = "1", ["bubble"] = "0",
            ["room_type"] = "0", ["jumpfrom"] = "0", ["reply_mid"] = "0",
            ["statistics"] = "{\"appId\":100,\"platform\":5}"
        });
        JsonDocument response;
        try { response = await BilibiliDanmakuHttp.SendJsonAsync(http, request, token).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new IOException("弹幕发送请求未完成，发送结果不确定。请查看弹幕区后再决定是否重发。"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("弹幕发送请求超时，发送结果不确定。请查看弹幕区后再决定是否重发。"); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { throw new IOException("B 站未返回有效的发送确认，发送结果不确定。请查看弹幕区后再决定是否重发。"); }
        using (response)
        {
            int code = BilibiliDanmakuHttp.Code(response.RootElement);
            if (code != 0) throw new InvalidOperationException(SendFailure(code));
            // Some web endpoint rejections use code=0 and a nonempty message.
            foreach (string name in new[] { "message", "msg" })
                if (response.RootElement.TryGetProperty(name, out var message) && message.ValueKind == JsonValueKind.String && message.GetString() is string value && value.Length > 0 && value != "0")
                    throw new InvalidOperationException("B 站未确认弹幕发送，请检查账号状态或直播间发言限制。");
        }
    }
    internal static string SendFailure(int code) => code switch
    {
        -101 or -111 => "B 站登录已失效，请重新登录后发送弹幕。",
        10031 => "弹幕发送过快，请稍等后再发送。",
        1003212 => "弹幕超过直播间允许的长度，请缩短后再发送。",
        -403 or -412 or -352 => "B 站限制了这次发言，请在官方直播间检查账号或完成验证。",
        _ => $"B 站未允许发送这条弹幕（{code}），请检查直播间发言限制。"
    };
    public void Dispose() => http.Dispose();
}

internal sealed class BilibiliDanmakuException(string message, bool requiresLogin = false) : Exception(message)
{
    internal bool RequiresLogin { get; } = requiresLogin;
}

internal static class BilibiliDanmakuHttp
{
    internal const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    internal static HttpClient Create(HttpMessageHandler? handler) => new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };
    internal static HttpRequestMessage Request(HttpMethod method, string url, string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Referrer = new Uri("https://live.bilibili.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
        if (cookie.Length > 0)
        {
            if (cookie.Any(c => c < 32 || c == 127)) throw new InvalidOperationException("B 站登录信息无效，请重新登录。");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
        return request;
    }
    internal static string CookieValue(string cookie, string name)
    {
        foreach (var piece in cookie.Split(';'))
        {
            int separator = piece.IndexOf('=');
            if (separator > 0 && piece[..separator].Trim() == name) return piece[(separator + 1)..].Trim();
        }
        return "";
    }
    internal static Uri? ChatServer(string host, int port)
    {
        if (!host.EndsWith(".chat.bilibili.com", StringComparison.OrdinalIgnoreCase) || Uri.CheckHostName(host) != UriHostNameType.Dns || port is not (443 or 2245)) return null;
        return new UriBuilder("wss", host, port, "/sub").Uri;
    }
    internal static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, string cookie, CancellationToken token)
    {
        using var request = Request(HttpMethod.Get, url, cookie);
        return await SendJsonAsync(http, request, token).ConfigureAwait(false);
    }
    internal static async Task<JsonDocument> SendJsonAsync(HttpClient http, HttpRequestMessage request, CancellationToken token)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("B 站请求未完成（HTTP " + (int)response.StatusCode + "）。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        if (response.Content.Headers.ContentLength > 1048576) throw new InvalidDataException("B 站服务响应过大。");
        using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var bytes = new MemoryStream(); byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false); if (count == 0) break;
            if (bytes.Length + count > 1048576) throw new InvalidDataException("B 站服务响应过大。");
            bytes.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }
    internal static int Code(JsonElement root) => root.TryGetProperty("code", out var code) && code.TryGetInt32(out int parsed) ? parsed : throw new InvalidDataException("B 站服务响应无效。");
}

internal static class BilibiliDanmakuSigning
{
    private static readonly int[] Order = [46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13];
    internal static string Mixin(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("wbi_img", out var images)) throw new InvalidDataException("无法取得 B 站弹幕授权信息。");
        string Key(string name)
        {
            if (!images.TryGetProperty(name, out var image) || !Uri.TryCreate(image.GetString(), UriKind.Absolute, out var uri)) throw new InvalidDataException("B 站授权信息无效。");
            string key = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            if (key.Length != 32 || key.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("B 站授权信息无效。");
            return key;
        }
        string joined = Key("img_url") + Key("sub_url");
        return new string(Order.Select(index => joined[index]).ToArray());
    }
    internal static string Sign(long roomId, string mixin, long timestamp)
    {
        string query = $"id={roomId.ToString(CultureInfo.InvariantCulture)}&type=0&wts={timestamp.ToString(CultureInfo.InvariantCulture)}";
        string hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + mixin))).ToLowerInvariant();
        return query + "&w_rid=" + hash;
    }
}

internal sealed record BilibiliDanmakuBatch(List<BilibiliComment> Comments, int? AuthCode);
internal static class BilibiliDanmakuPackets
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal static byte[] Make(int operation, byte[] body, ushort version = 1)
    {
        if (body.Length > MaximumBytes - 16) throw new InvalidDataException("弹幕数据超过允许大小。");
        byte[] packet = new byte[16 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet, packet.Length);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), version);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), operation);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), 1); body.CopyTo(packet, 16);
        return packet;
    }
    internal static BilibiliDanmakuBatch Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("弹幕数据超过允许大小。");
        var comments = new List<BilibiliComment>(); int? auth = null; int remainingBudget = MaximumBytes;
        ParseInto(bytes, 0, comments, ref auth, ref remainingBudget);
        return new BilibiliDanmakuBatch(comments, auth);
    }
    private static void ParseInto(byte[] bytes, int depth, List<BilibiliComment> comments, ref int? auth, ref int remainingBudget)
    {
        if (depth > 2) throw new InvalidDataException("弹幕数据嵌套过深。");
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 16) throw new InvalidDataException("弹幕数据头不完整。");
            var header = bytes.AsSpan(offset, 16);
            int length = BinaryPrimitives.ReadInt32BigEndian(header);
            int headerLength = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
            int version = BinaryPrimitives.ReadUInt16BigEndian(header[6..]);
            int operation = BinaryPrimitives.ReadInt32BigEndian(header[8..]);
            if (length < 16 || length > bytes.Length - offset || headerLength < 16 || headerLength > length) throw new InvalidDataException("弹幕数据长度无效。");
            byte[] body = bytes.AsSpan(offset + headerLength, length - headerLength).ToArray();
            offset += length;
            if (operation == 5 && version is 2 or 3)
            {
                using var input = new MemoryStream(body, writable: false);
                using Stream compressed = version == 3 ? new BrotliStream(input, CompressionMode.Decompress) : body.Length >= 2 && body[0] == 0x1f && body[1] == 0x8b ? new GZipStream(input, CompressionMode.Decompress) : new ZLibStream(input, CompressionMode.Decompress);
                using var expanded = new MemoryStream(); byte[] scratch = new byte[8192];
                while (true)
                {
                    int count = compressed.Read(scratch, 0, scratch.Length); if (count == 0) break;
                    remainingBudget -= count; if (remainingBudget < 0) throw new InvalidDataException("弹幕解压数据超过允许大小。");
                    expanded.Write(scratch, 0, count);
                }
                ParseInto(expanded.ToArray(), depth + 1, comments, ref auth, ref remainingBudget);
            }
            else if (operation == 8)
            {
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 }); auth = BilibiliDanmakuHttp.Code(document.RootElement);
            }
            else if (operation == 5 && version is 0 or 1 && body.Length > 0)
            {
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
                if (ReadComment(document.RootElement) is BilibiliComment comment)
                {
                    if (comments.Count >= 2000) throw new InvalidDataException("弹幕数据条数过多。");
                    comments.Add(comment);
                }
            }
            else if (operation == 3 && offset < bytes.Length)
            {
                // Some servers append heartbeat counters outside pack_len. Preserve real following packets.
                int remaining = bytes.Length - offset;
                if (remaining < 16) break;
                int nextLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
                int nextHeader = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 4, 2));
                if (nextLength < 16 || nextLength > remaining || nextHeader < 16 || nextHeader > nextLength) break;
            }
        }
    }
    private static BilibiliComment? ReadComment(JsonElement root)
    {
        if (!root.TryGetProperty("cmd", out var command) || command.ValueKind != JsonValueKind.String || !(command.GetString() ?? "").Split(':')[0].Equals("DANMU_MSG", StringComparison.Ordinal)) return null;
        if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array || info.GetArrayLength() < 3 || info[1].ValueKind != JsonValueKind.String || info[2].ValueKind != JsonValueKind.Array || info[2].GetArrayLength() < 2) return null;
        long userId = info[2][0].ValueKind == JsonValueKind.Number && info[2][0].TryGetInt64(out long uid) ? Math.Max(0, uid) : 0;
        string user = info[2][1].ValueKind == JsonValueKind.String ? SafeText(info[2][1].GetString() ?? "", 80) : "观众";
        string text = SafeText(info[1].GetString() ?? "", 280);
        return text.Length == 0 ? null : new BilibiliComment(userId, user.Length == 0 ? "观众" : user, text);
    }
    internal static string SafeText(string text, int maxRunes)
    {
        var result = new StringBuilder(); int count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count++ >= maxRunes) break;
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) result.Append(' ');
            else result.Append(rune.ToString());
        }
        return result.ToString().Trim();
    }
}
