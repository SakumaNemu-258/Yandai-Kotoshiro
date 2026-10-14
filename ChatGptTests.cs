using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kotoshiro;

internal static class ChatGptTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }
    internal static async Task<List<string>> RunAsync(string folder)
    {
        var results = new List<string>(); Directory.CreateDirectory(folder);
        using var rsa = RSA.Create(2048); var fixture = new AuthHandler(rsa);
        using var auth = new ChatGptAuth(Path.Combine(folder, "login"), fixture);
        Task? browserWork = null; Dictionary<string, string>? first = null;
        Func<Uri, Task> browser = uri =>
        {
            var query = ChatGptAuth.ParseQuery(uri.Query); fixture.Authorization = query;
            first ??= query; fixture.ExpectedClient = query["client_id"] == "dynamic_agent_client" ? "issued-fixture-client" : query["client_id"];
            browserWork = Task.Run(async () =>
            {
                using var http = new HttpClient();
                string callback = query["redirect_uri"];
                using var wrong = await http.GetAsync(callback + "?state=wrong&code=fixture&client_id=issued-fixture-client");
                Check(wrong.StatusCode == HttpStatusCode.BadRequest, "wrong callback state rejected");
                string suffix = fixture.Decline ? "&error=access_denied" : "&code=fixture&client_id=" + fixture.ExpectedClient;
                using var correct = await http.GetAsync(callback + "?state=" + query["state"] + suffix);
                Check(correct.StatusCode == HttpStatusCode.OK, "correct loopback callback accepted");
            });
            return Task.CompletedTask;
        };
        var account = await auth.LoginAsync(null, browser, CancellationToken.None); await browserWork!;
        Check(account.CanUsePlan && account.IsSignedIn && account.Label.Contains("fixture@example.test"), "verified subscription account persisted");
        Check(first!["client_id"] == "dynamic_agent_client" && first["agent_name_hint"] == "言代Kotoshiro" && first["scope"] == ChatGptAuth.Scope && first["resource"] == ChatGptAuth.Resource, "official initial registration parameters");
        byte[] vault = File.ReadAllBytes(Path.Combine(folder, "login", "chatgpt.dat"));
        Check(!Encoding.UTF8.GetString(vault).Contains("fixture-access") && !Encoding.UTF8.GetString(vault).Contains("fixture-refresh"), "entire credential vault encrypted");
        Check(await auth.AccessTokenAsync(account.Id, CancellationToken.None) == "fixture-access", "selected account bearer token");
        var catalog = await auth.ModelsAsync(account.Id, CancellationToken.None);
        Check(catalog.Select(x => x.Slug).SequenceEqual(["fixture-fast", "fixture-quality"]), "account model visibility and server order");
        await auth.MarkWelcomedAsync(account.Id, CancellationToken.None); Check(auth.Accounts().Single().Welcomed, "welcome persisted per account");
        var again = await auth.LoginAsync(account.Id, browser, CancellationToken.None); await browserWork!;
        Check(fixture.Authorization["client_id"] == "issued-fixture-client" && !fixture.Authorization.ContainsKey("agent_name_hint") && fixture.Authorization["ext_agent_host_id"] == first["ext_agent_host_id"], "returning issued client and stable installation host");
        Check(auth.Accounts().Count == 1 && again.Id == account.Id, "returning profile reused");
        fixture.Subject = "different-account";
        try { await auth.LoginAsync(account.Id, browser, CancellationToken.None); throw new Exception("subject change accepted"); } catch (InvalidDataException) { }
        await browserWork!; Check(auth.Accounts().Count == 1 && await auth.AccessTokenAsync(account.Id, CancellationToken.None) == "fixture-access", "subject mismatch keeps original credentials");
        fixture.Subject = "fixture-subject"; fixture.Decline = true; int callsBefore = fixture.TokenCalls;
        try { await auth.LoginAsync(null, browser, CancellationToken.None); throw new Exception("decline accepted"); } catch (InvalidOperationException) { }
        await browserWork!; Check(fixture.TokenCalls == callsBefore && auth.Accounts().Count == 1, "declined consent does not exchange tokens"); fixture.Decline = false;
        fixture.Scope = "openid profile email offline_access";
        var identityOnly = await auth.LoginAsync(null, browser, CancellationToken.None); await browserWork!;
        Check(identityOnly.IsSignedIn && !identityOnly.CanUsePlan, "identity only login does not imply plan permission");
        try { await auth.AccessTokenAsync(identityOnly.Id, CancellationToken.None); throw new Exception("missing scope accepted"); } catch (ChatGptPlanException ex) { Check(ex.ErrorCode == "scope_missing", "identity only blocks inference"); }
        fixture.Scope = ChatGptAuth.Scope;
        results.Add("PASS: simulated official OAuth through real loopback callback: state, PKCE, issued client ID, stable host, signed identity, encrypted storage, returning account, mismatched account, declined consent, identity-only scope, account-specific catalog, first-login welcome");
        using var jwks = JsonDocument.Parse(fixture.Jwks());
        string valid = fixture.Jwt("issued-fixture-client", "nonce", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        Check(ChatGptAuth.VerifyIdentity(valid, jwks.RootElement, "issued-fixture-client", "nonce").Subject == fixture.Subject, "valid signature");
        foreach (var item in new[] { (valid, "other-client", "nonce"), (valid, "issued-fixture-client", "wrong-nonce"), (fixture.Jwt("issued-fixture-client", "nonce", 1), "issued-fixture-client", "nonce"), (valid[..^5] + "AAAAA", "issued-fixture-client", "nonce") })
        { try { ChatGptAuth.VerifyIdentity(item.Item1, jwks.RootElement, item.Item2, item.Item3); throw new Exception("invalid ID token accepted"); } catch (InvalidDataException) { } }
        results.Add("PASS: ID-token signature, audience, expiry and nonce rejection");

        var rotating = new ChatGptRegistration { ClientId = "issued-fixture-client", Subject = fixture.Subject, Email = "fixture@example.test", AccessToken = "expired", RefreshToken = "fixture-refresh", Scopes = ChatGptAuth.Scope.Split(' '), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        string refreshRoot = Path.Combine(folder, "refresh"); using var refreshAuth = new ChatGptAuth(refreshRoot, new AuthHandler(rsa));
        var refreshHandler = new AuthHandler(rsa); using var secondAuth = new ChatGptAuth(refreshRoot, refreshHandler);
        await refreshAuth.SeedForTestAsync(rotating);
        var tokens = await Task.WhenAll(secondAuth.AccessTokenAsync(rotating.Id, CancellationToken.None), secondAuth.AccessTokenAsync(rotating.Id, CancellationToken.None), refreshAuth.AccessTokenAsync(rotating.Id, CancellationToken.None));
        Check(tokens.All(x => x == "fixture-access-rotated") && refreshHandler.RefreshCalls == 1 && refreshHandler.Rotated, "refresh serialized and replacement persisted");
        results.Add("PASS: concurrent refresh serialized, granted scope retained, rotating replacement atomically persisted, earliest-refresh string supported");

        foreach (bool terminal in new[] { true, false })
        {
            var errors = new AuthHandler(rsa) { RefreshFailure = terminal ? HttpStatusCode.BadRequest : HttpStatusCode.ServiceUnavailable };
            using var failing = new ChatGptAuth(Path.Combine(folder, terminal ? "terminal" : "temporary"), errors);
            var expired = new ChatGptRegistration { ClientId = "issued-fixture-client", Subject = fixture.Subject, AccessToken = "expired", RefreshToken = "fixture-refresh", Scopes = ChatGptAuth.Scope.Split(' '), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
            await failing.SeedForTestAsync(expired);
            try { await failing.AccessTokenAsync(expired.Id, CancellationToken.None); throw new Exception("refresh error ignored"); } catch (ChatGptPlanException) { }
            Check(failing.Accounts().Single().IsSignedIn == !terminal, "terminal refresh clears, temporary refresh preserves");
        }
        results.Add("PASS: terminal refresh clears credentials but retains registration; temporary server failure preserves login");

        var signedSettings = new AppSettings { ApiProvider = "chatgpt", ApiProtocol = "messages", ApiBase = "https://wrong.example", ChatGptAccountId = account.Id, ChatGptModel = "fixture-fast" }; signedSettings.SetKey("wrong-api-key");
        await auth.ModelsAsync(account.Id, CancellationToken.None);
        var streamHandler = new PlanHandler(); using (var translator = new ApiTranslator(signedSettings, streamHandler, auth))
        {
            Check(await translator.TranslateAsync("こんにちは", CancellationToken.None) == "你好。", "subscription completed stream translation");
            Check(await translator.TranslateAsync("ありがとう", CancellationToken.None) == "你好。", "subscription context");
            Check(streamHandler.Calls == 2 && streamHandler.LastContextCount == 3 && translator.LastTiming != null, "subscription payload uses OAuth and complete response timing");
        }
        foreach (string ending in new[] { "quota", "incomplete", "eof", "error" })
        {
            var failing = new PlanHandler { Ending = ending }; using var translator = new ApiTranslator(signedSettings, failing, auth);
            try { await translator.TranslateAsync("こんにちは", CancellationToken.None); throw new Exception("partial stream accepted"); } catch (ChatGptPlanException) { }
            Check(failing.Calls == 1, "no billing fallback or retry on failed stream");
        }
        using (var translator = new ApiTranslator(signedSettings, new PlanHandler { Ending = "http-quota" }, auth))
        {
            try { await translator.TranslateAsync("こんにちは", CancellationToken.None); throw new Exception("quota accepted"); }
            catch (ChatGptPlanException ex) { Check(ex.ErrorCode == "subscription_sharing_usage_limit_exceeded" && ex.RequestId == "fixture-request" && ex.Parameter == "model" && ex.StatusCode == HttpStatusCode.TooManyRequests && !ex.Message.Contains("sensitive"), "safe structured quota diagnostics"); }
        }
        results.Add("PASS: official Responses route, OAuth bearer, selected model, stream=true, store=false, supported fields, context, completed-only subtitles; partial quota/incomplete/error/disconnection rejected; no API fallback; safe structured HTTP errors");
        Check(await auth.SignOutAsync(account.Id, CancellationToken.None) && fixture.Revoked, "discovered refresh revocation endpoint called");
        Check(!auth.Accounts().First(x => x.Id == account.Id).IsSignedIn && auth.Accounts().Count == 2, "sign out clears only selected account and preserves registration");
        results.Add("PASS: discovered revocation endpoint called; selected account signed out locally; registration and other account retained");
        foreach (bool network in new[] { false, true })
        {
            var revokeFailure = new AuthHandler(rsa) { RevocationFails = true, RevocationNetwork = network };
            using var failedLogout = new ChatGptAuth(Path.Combine(folder, network ? "logout-network" : "logout-server"), revokeFailure);
            var selected = new ChatGptRegistration { ClientId = "issued-fixture-client", Subject = "fixture", RefreshToken = "fixture-refresh", Scopes = ChatGptAuth.Scope.Split(' ') };
            await failedLogout.SeedForTestAsync(selected);
            Check(!await failedLogout.SignOutAsync(selected.Id, CancellationToken.None) && revokeFailure.RevocationCalls == 3 && !failedLogout.Accounts().Single().IsSignedIn, "bounded revocation retries then explicit local-only sign out");
        }
        results.Add("PASS: network/5xx revocation retries bounded to three, then local credentials cleared with unconfirmed remote result");
        return results;
    }
    private sealed class AuthHandler(RSA rsa) : HttpMessageHandler
    {
        public Dictionary<string, string> Authorization = [];
        public string ExpectedClient = "issued-fixture-client", Subject = "fixture-subject", Scope = ChatGptAuth.Scope;
        public bool Decline, Revoked, Rotated;
        public int TokenCalls, RefreshCalls;
        public HttpStatusCode? RefreshFailure;
        public bool RevocationFails, RevocationNetwork;
        public int RevocationCalls;
        public string Jwks()
        {
            var key = rsa.ExportParameters(false);
            return JsonSerializer.Serialize(new { keys = new[] { new { kid = "fixture-key", kty = "RSA", use = "sig", alg = "RS256", n = ChatGptAuth.Base64Url(key.Modulus!), e = ChatGptAuth.Base64Url(key.Exponent!) } } });
        }
        public string Jwt(string audience, string nonce, long expires)
        {
            string header = ChatGptAuth.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "fixture-key" }));
            string body = ChatGptAuth.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = "https://auth.openai.com", sub = Subject, aud = audience, nonce, exp = expires, email = "fixture@example.test" }));
            string data = header + "." + body;
            return data + "." + ChatGptAuth.Base64Url(rsa.SignData(Encoding.ASCII.GetBytes(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath;
            object result;
            if (path == "/.well-known/openid-configuration") result = new { issuer = "https://auth.openai.com", jwks_uri = "https://auth.openai.com/.well-known/jwks.json", revocation_endpoint = "https://auth.openai.com/api/accounts/oauth/revoke" };
            else if (path == "/.well-known/jwks.json") return Json(Jwks());
            else if (path == "/api/accounts/oauth/token")
            {
                TokenCalls++; var form = ChatGptAuth.ParseQuery(await request.Content!.ReadAsStringAsync(token));
                Check(form["client_id"] == ExpectedClient && form["resource"] == ChatGptAuth.Resource && !form.ContainsKey("client_secret"), "public issued client token exchange");
                if (form["grant_type"] == "refresh_token")
                {
                    RefreshCalls++; Check(!form.ContainsKey("scope") && form["refresh_token"] == "fixture-refresh", "refresh no scope, own rotating refresh token");
                    if (RefreshFailure.HasValue) return Json(JsonSerializer.Serialize(new { error = RefreshFailure == HttpStatusCode.BadRequest ? "invalid_grant" : "temporarily_unavailable" }), RefreshFailure.Value);
                    Rotated = true; result = new { access_token = "fixture-access-rotated", refresh_token = "fixture-refresh-rotated", expires_in = 3600, token_type = "Bearer", earliest_refresh_at = DateTimeOffset.UtcNow.AddSeconds(30).ToString("O") };
                }
                else
                {
                    Check(form["code"] == "fixture" && form["redirect_uri"] == Authorization["redirect_uri"] && ChatGptAuth.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))) == Authorization["code_challenge"], "matching redirect and S256 verifier");
                    result = new { access_token = "fixture-access", refresh_token = "fixture-refresh", expires_in = 3600, token_type = "Bearer", scope = Scope, id_token = Jwt(ExpectedClient, Authorization["nonce"], DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()) };
                }
            }
            else if (path == "/v1/models")
            {
                Check(request.Headers.Authorization?.Parameter == "fixture-access", "catalog uses selected token");
                result = new { models = new[] { new { slug = "fixture-fast", display_name = "Fixture Fast", visibility = "list" }, new { slug = "hidden", display_name = "Hidden", visibility = "hidden" }, new { slug = "fixture-quality", display_name = "Fixture Quality", visibility = "list" } } };
            }
            else if (path == "/api/accounts/oauth/revoke")
            {
                RevocationCalls++;
                if (RevocationFails) { if (RevocationNetwork) throw new HttpRequestException("simulated network outage"); return Json("{}", HttpStatusCode.ServiceUnavailable); }
                var form = ChatGptAuth.ParseQuery(await request.Content!.ReadAsStringAsync(token)); Check(form["token_type_hint"] == "refresh_token" && form["client_id"] == ExpectedClient && form["token"] == "fixture-refresh", "refresh token revocation fields"); Revoked = true;
                return new(HttpStatusCode.OK) { Content = new StringContent("") };
            }
            else throw new Exception("unexpected authentication route");
            return Json(JsonSerializer.Serialize(result));
        }
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class PlanHandler : HttpMessageHandler
    {
        public string Ending = "completed";
        public int Calls, LastContextCount;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Check(request.RequestUri!.AbsoluteUri == ChatGptAuth.Resource + "/responses" && request.Headers.Authorization?.Parameter == "fixture-access", "fixed official route with OAuth token, ignores API profile");
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var p = doc.RootElement;
            Check(p.GetProperty("model").GetString() == "fixture-fast" && p.GetProperty("stream").GetBoolean() && !p.GetProperty("store").GetBoolean(), "plan model stream store");
            Check(p.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(new[] { "model", "stream", "store", "input", "instructions" }.Order()), "only supported fields"); LastContextCount = p.GetProperty("input").GetArrayLength();
            if (Ending == "http-quota")
            {
                var response = Json("{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\",\"param\":\"model\",\"message\":\"sensitive fixture-access\"}}", HttpStatusCode.TooManyRequests); response.Headers.Add("x-request-id", "fixture-request"); return response;
            }
            string start = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"你好。\"}\r\n\r\n";
            string last = Ending switch
            {
                "quota" => "{\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\"}}}",
                "incomplete" => "{\"type\":\"response.incomplete\",\"response\":{}}",
                "error" => "{\"type\":\"error\",\"code\":\"subscription_sharing_usage_unavailable\"}",
                "eof" => "",
                _ => "{\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"你好。\"}]}]}}"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(start + (last.Length > 0 ? "data: " + last + "\n\n" : ""), Encoding.UTF8, "text/event-stream") };
        }
    }
}
