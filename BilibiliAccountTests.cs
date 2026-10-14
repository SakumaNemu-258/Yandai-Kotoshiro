using System.Net;
using System.Text;
using System.Text.Json;

namespace Kotoshiro;

internal static class BilibiliAccountTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
    internal static async Task<List<string>> RunAsync(string folder)
    {
        Directory.CreateDirectory(folder); var results = new List<string>();
        string root = Path.Combine(folder, "bilibili-account-fixture");
        var handler = new AccountHandler(); using var store = new BilibiliAccountStore(root, handler);
        Check(await store.GetAsync() == null && handler.Calls == 0, "no account means no network read");
        var ticket = await store.BeginQrAsync(CancellationToken.None);
        Check(ticket.Url.StartsWith("https://account.bilibili.com/") && ticket.Key.Length == 32, "official QR payload");
        handler.PollCode = 86101; Check((await store.PollQrAsync(ticket, CancellationToken.None)).Status == BilibiliQrStatus.Waiting, "waiting state");
        handler.PollCode = 86090; Check((await store.PollQrAsync(ticket, CancellationToken.None)).Status == BilibiliQrStatus.Scanned, "scanned state");
        handler.PollCode = 86038; Check((await store.PollQrAsync(ticket, CancellationToken.None)).Status == BilibiliQrStatus.Expired, "server expiry");
        int before = handler.Calls;
        Check((await store.PollQrAsync(ticket with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, CancellationToken.None)).Status == BilibiliQrStatus.Expired && handler.Calls == before, "expired QR does not poll");
        handler.PollCode = 0;
        var success = await store.PollQrAsync(ticket, CancellationToken.None);
        Check(success.Status == BilibiliQrStatus.LoggedIn && success.Account?.UserId == 123456 && success.Account.DisplayName == "测试账号", "UID verified before login saved");
        Check(store.IsLoggedIn && handler.LastCookieHeader.Contains("SESSDATA=fixture-session") && !handler.LastCookieHeader.Contains("tracker"), "only session whitelist sent");
        byte[] vault = await File.ReadAllBytesAsync(Path.Combine(root, "bilibili.dat"));
        Check(!Encoding.UTF8.GetString(vault).Contains("fixture-session") && !Encoding.UTF8.GetString(vault).Contains("fixture-csrf"), "entire credential file DPAPI encrypted");
        before = handler.Calls;
        using (var second = new BilibiliAccountStore(root, new AccountHandler()))
        { var loaded = await second.GetAsync(); Check(loaded?.UserId == 123456 && loaded.Csrf == "fixture-csrf" && second.IsLoggedIn, "encrypted login restored"); }
        Check((await store.GetAsync())?.UserId == 123456 && before == handler.Calls, "GetAsync is local-only");
        handler.NavCode = -101; Check(await store.CheckAsync() == null && !store.IsLoggedIn && !File.Exists(Path.Combine(root, "bilibili.dat")), "invalid session cleared");
        handler.NavCode = 0; ticket = await store.BeginQrAsync(CancellationToken.None); await store.PollQrAsync(ticket, CancellationToken.None);
        handler.HttpFailure = HttpStatusCode.ServiceUnavailable;
        try { await store.CheckAsync(); throw new Exception("temporary server failure ignored"); } catch (HttpRequestException ex) { Check(!ex.Message.Contains("fixture-session") && !ex.Message.Contains("qrcode_key"), "safe server failure"); }
        Check(File.Exists(Path.Combine(root, "bilibili.dat")) && store.IsLoggedIn, "temporary failure preserves login");
        handler.HttpFailure = null;
        await store.LogoutAsync(); Check(await store.GetAsync() == null && !File.Exists(Path.Combine(root, "bilibili.dat")), "logout clears own session locally");
        results.Add("PASS: Bilibili QR waiting/scanned/expired/success, server verified UID, local-only encrypted restore, invalid login clear, temporary failure retain, local logout");

        foreach (string issue in new[] { "missing", "domain", "expired", "uid", "invalid-cookie", "redirect-qr", "malformed-json" })
        {
            string badRoot = Path.Combine(folder, "bilibili-reject-" + issue);
            var invalid = new AccountHandler { Issue = issue }; using var rejected = new BilibiliAccountStore(badRoot, invalid);
            try
            {
                var qr = await rejected.BeginQrAsync(CancellationToken.None); await rejected.PollQrAsync(qr, CancellationToken.None);
                throw new Exception("invalid account accepted: " + issue);
            }
            catch (InvalidDataException ex) { Check(!ex.Message.Contains("fixture-session"), "invalid secret not in message"); }
            Check(!File.Exists(Path.Combine(badRoot, "bilibili.dat")), "invalid login never persisted");
        }
        results.Add("PASS: missing or expired credentials, foreign cookie domain, mismatched UID, cookie injection, foreign QR URL and malformed JSON rejected without persistence");

        string concurrentRoot = Path.Combine(folder, "bilibili-concurrent-logout");
        var blocking = new BlockingNavHandler(); using var firstStore = new BilibiliAccountStore(concurrentRoot, blocking);
        using var logoutStore = new BilibiliAccountStore(concurrentRoot, new AccountHandler());
        var concurrentTicket = await firstStore.BeginQrAsync(CancellationToken.None); await firstStore.PollQrAsync(concurrentTicket, CancellationToken.None);
        blocking.Block = true; var checkTask = firstStore.CheckAsync();
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await logoutStore.LogoutAsync(); blocking.Release.TrySetResult(); await checkTask;
        Check(!File.Exists(Path.Combine(concurrentRoot, "bilibili.dat")) && await firstStore.GetAsync() == null, "in-flight check cannot restore logged-out session");
        Check((await firstStore.PollQrAsync(concurrentTicket, CancellationToken.None)).Status == BilibiliQrStatus.Expired, "old QR cannot restore logged-out session");
        results.Add("PASS: logout wins concurrent account validation and invalidates old QR login attempts across store instances");
        return results;
    }

    private sealed class BlockingNavHandler : HttpMessageHandler
    {
        private readonly HttpMessageInvoker inner = new(new AccountHandler());
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Block && request.RequestUri?.AbsolutePath == "/x/web-interface/nav")
            { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return await inner.SendAsync(request, cancellationToken);
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class AccountHandler : HttpMessageHandler
    {
        public int Calls, PollCode, NavCode;
        public string Issue = "", LastCookieHeader = "";
        public HttpStatusCode? HttpFailure;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Check(request.Method == HttpMethod.Get && request.RequestUri!.Scheme == "https", "only explicit official HTTPS GET");
            Check(request.Headers.UserAgent.ToString().Contains("Mozilla/5.0"), "browser-compatible user agent");
            if (HttpFailure.HasValue) return Task.FromResult(new HttpResponseMessage(HttpFailure.Value) { Content = new StringContent("sensitive fixture-session") });
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/qrcode/generate"))
            {
                if (Issue == "malformed-json") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not JSON fixture-session") });
                return Task.FromResult(Json(new { code = 0, data = new { url = (Issue == "redirect-qr" ? "https://example.test/" : "https://account.bilibili.com/h5/account-h5/auth/scan-web?navhide=1&") + "qrcode_key=" + new string('a', 32), qrcode_key = new string('a', 32) } }));
            }
            if (path.EndsWith("/qrcode/poll"))
            {
                var response = Json(new { code = 0, data = new { code = PollCode, refresh_token = "ignored-fixture-refresh", url = "https://example.test/ignored-redirect" } });
                if (PollCode == 0)
                {
                    string domain = Issue == "domain" ? "example.test" : ".bilibili.com";
                    string session = Issue == "invalid-cookie" ? "injected,bad" : "fixture-session";
                    foreach (string item in new[] { "DedeUserID=123456", "DedeUserID__ckMd5=fixture-id-check", "SESSDATA=" + session, "bili_jct=fixture-csrf", "tracker=ignore-this" })
                    {
                        if (Issue == "missing" && item.StartsWith("bili_jct=")) continue;
                        response.Headers.TryAddWithoutValidation("Set-Cookie", item + "; Domain=" + domain + "; Path=/; Secure; Max-Age=" + (Issue == "expired" ? "0" : "15552000"));
                    }
                }
                return Task.FromResult(response);
            }
            if (path == "/x/web-interface/nav")
            {
                LastCookieHeader = request.Headers.TryGetValues("Cookie", out var values) ? values.Single() : "";
                return Task.FromResult(Json(new { code = NavCode, data = new { isLogin = NavCode == 0, mid = Issue == "uid" ? 999999 : 123456, uname = "测试账号\n" } }));
            }
            throw new Exception("Unexpected test request");
        }
        private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
    }
}
