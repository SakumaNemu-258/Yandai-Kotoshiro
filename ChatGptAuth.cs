using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kotoshiro;

public sealed class ChatGptRegistration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ClientId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Email { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string IdToken { get; set; } = "";
    public string[] Scopes { get; set; } = [];
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset EarliestRefreshAt { get; set; }
    public bool Welcomed { get; set; }
    public List<ChatGptModel> Models { get; set; } = [];
    public bool IsSignedIn => AccessToken.Length > 0 || RefreshToken.Length > 0;
    public bool CanUsePlan => IsSignedIn && Scopes.Contains("chatgpt.tokens.use.direct");
    public string Label => (Email.Length > 0 ? Email : "ChatGPT 账号") + " · " + Id[..6] + (IsSignedIn ? "" : "（已退出）");
    public void ClearTokens() { AccessToken = RefreshToken = IdToken = ""; Scopes = []; ExpiresAt = EarliestRefreshAt = default; Models = []; }
}
public record ChatGptModel(string Slug, string DisplayName)
{
    public override string ToString() => DisplayName;
}
public record ChatGptAccount(string Id, string Label, bool IsSignedIn, bool CanUsePlan, bool Welcomed, List<ChatGptModel> Models)
{
    public override string ToString() => Label;
}
internal sealed class ChatGptVault
{
    public string HostId { get; set; } = "urn:uuid:" + Guid.NewGuid();
    public List<ChatGptRegistration> Accounts { get; set; } = [];
}
public sealed class ChatGptPlanException : HttpRequestException
{
    public string ErrorCode { get; }
    public string RequestId { get; }
    public string BodyShape { get; }
    public string Parameter { get; }
    public ChatGptPlanException(string code, HttpStatusCode? status = null, string requestId = "", string shape = "error", string parameter = "")
        : base(MessageFor(code, status) + (requestId.Length > 0 ? $"\n请求编号：{requestId}" : ""), null, status)
    { ErrorCode = code; RequestId = requestId; BodyShape = shape; Parameter = parameter; }
    private static string MessageFor(string code, HttpStatusCode? status) => code switch
    {
        "subscription_sharing_usage_limit_exceeded" => "ChatGPT 订阅或本软件的使用额度已达到上限。请打开“管理订阅用量”查看。",
        "subscription_sharing_user_not_eligible" => "此账号或工作区目前不支持授权使用 ChatGPT 订阅额度。请检查账号资格。",
        "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable" => "ChatGPT 订阅服务暂时不可用，请稍后再试。登录凭证已保留。",
        "subscription_sharing_unsupported_capability" or "subscription_sharing_route_not_supported" => "所选模型或请求能力不适用于 ChatGPT 订阅，请刷新模型列表后重新选择。",
        "scope_missing" => "该账号尚未授权使用 ChatGPT 订阅额度，请点击 Continue with ChatGPT 重新授权。",
        "refresh_invalid" or "subscription_sharing_invalid_user" => "ChatGPT 登录已失效，请在设置中重新登录。",
        "stream_interrupted" => "ChatGPT 译文未完整返回，此段未完成。请检查网络后重试。",
        "response_incomplete" => "ChatGPT 译文未完成，此段已跳过。",
        _ when status == HttpStatusCode.Unauthorized => "ChatGPT 登录或订阅授权未被接受，请在设置中重新登录。",
        _ when status == HttpStatusCode.Forbidden => "ChatGPT 拒绝访问，请检查账号授权、工作区设置或可用地区。",
        _ when status == HttpStatusCode.TooManyRequests => "ChatGPT 使用受到限制，请打开“管理订阅用量”查看后再试。",
        _ => "ChatGPT 请求未完成" + (status.HasValue ? $"（HTTP {(int)status}）" : "") + "。请检查授权与网络。"
    };
    internal static ChatGptPlanException FromJson(JsonElement root, HttpStatusCode? status = null, string requestId = "")
    {
        if (root.ValueKind != JsonValueKind.Object) return new("unknown", status, SafeIdentifier(requestId), "other");
        string shape = root.TryGetProperty("error", out var error) ? "error" : root.TryGetProperty("detail", out _) ? "detail" : "other";
        string code = "unknown";
        if (shape == "error" && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = SafeIdentifier(c.GetString());
        string parameter = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("param", out var param) && param.ValueKind == JsonValueKind.String ? SafeIdentifier(param.GetString()) : "";
        return new(code, status, SafeIdentifier(requestId), shape, parameter);
    }
    internal static string SafeIdentifier(string? text) => text != null && text.Length <= 128 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? text : "";
}

public sealed class ChatGptAuth : IDisposable
{
    public const string Resource = "https://api.openai.com/v1";
    public const string AuthorizeEndpoint = "https://auth.openai.com/api/accounts/authorize";
    public const string TokenEndpoint = "https://auth.openai.com/api/accounts/oauth/token";
    public const string UsageUrl = "https://chatgpt.com/settings/usage";
    public const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string root;
    private readonly HttpClient client;
    private string VaultPath => Path.Combine(root, "chatgpt.dat");
    public ChatGptAuth(string? root = null, HttpMessageHandler? handler = null)
    {
        this.root = root ?? SettingsStore.Root;
        client = handler == null ? new(new HttpClientHandler { AllowAutoRedirect = false }) : new(handler);
        client.Timeout = TimeSpan.FromSeconds(25);
    }
    private ChatGptVault ReadVault()
    {
        if (!File.Exists(VaultPath)) return new();
        try { return JsonSerializer.Deserialize<ChatGptVault>(ProtectedData.Unprotect(File.ReadAllBytes(VaultPath), null, DataProtectionScope.CurrentUser)) ?? throw new InvalidDataException(); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException) { throw new InvalidDataException("无法读取本机 ChatGPT 登录信息。请备份 chatgpt.dat 后重新登录。"); }
    }
    private void SaveVault(ChatGptVault vault)
    {
        Directory.CreateDirectory(root); string temporary = VaultPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(vault), null, DataProtectionScope.CurrentUser)); File.Move(temporary, VaultPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task<FileStream> LockAsync(CancellationToken token)
    {
        Directory.CreateDirectory(root);
        for (int i = 0; ; i++)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(root, "chatgpt.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (i < 150) { await Task.Delay(100, token); }
        }
    }
    public List<ChatGptAccount> Accounts() => ReadVault().Accounts.Select(ToAccount).ToList();
    private static ChatGptAccount ToAccount(ChatGptRegistration account) => new(account.Id, account.Label, account.IsSignedIn, account.CanUsePlan, account.Welcomed, account.Models);
    private static ChatGptRegistration Find(ChatGptVault vault, string id) => vault.Accounts.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("请先选择并登录 ChatGPT 账号。");
    public async Task<ChatGptAccount> LoginAsync(string? registrationId, Func<Uri, Task> openBrowser, CancellationToken token)
    {
        ChatGptRegistration? existing; string hostId;
        await Gate.WaitAsync(token);
        try { using var fileLock = await LockAsync(token); var vault = ReadVault(); SaveVault(vault); hostId = vault.HostId; existing = registrationId == null ? null : Find(vault, registrationId); }
        finally { Gate.Release(); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(5)); token = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        string callback = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/auth/callback";
        string state = RandomValue(), nonce = RandomValue(), verifier = RandomValue();
        try
        {
            Uri authorization = AuthorizationUri(hostId, existing, callback, state, nonce, verifier);
            await openBrowser(authorization);
            var returned = await CallbackAsync(listener, new Uri(callback), state, token);
            string clientId = ValidateCallback(returned, state, existing?.ClientId);
            var values = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = returned["code"], ["code_verifier"] = verifier, ["redirect_uri"] = callback, ["resource"] = Resource };
            var response = await TokenAsync(values, token);
            var identity = await VerifyIdentityAsync(response.GetProperty("id_token").GetString()!, clientId, nonce, token);
            if (existing != null && identity.Subject != existing.Subject) throw new InvalidDataException("登录账号与原注册账号不一致，原账号凭证未修改。请使用“添加账号”。");
            await Gate.WaitAsync(token);
            try
            {
                using var fileLock = await LockAsync(token); var vault = ReadVault();
                var account = existing == null ? new ChatGptRegistration { ClientId = clientId, Subject = identity.Subject } : Find(vault, existing.Id);
                account.Email = identity.Email; ApplyTokens(account, response, initial: true);
                if (existing == null) vault.Accounts.Add(account); SaveVault(vault); return ToAccount(account);
            }
            finally { Gate.Release(); }
        }
        finally { listener.Stop(); }
    }
    internal static string RandomValue() => Base64Url(RandomNumberGenerator.GetBytes(32));
    internal static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static byte[] Decode64(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    internal static Uri AuthorizationUri(string hostId, ChatGptRegistration? account, string callback, string state, string nonce, string verifier)
    {
        var fields = new Dictionary<string, string> { ["client_id"] = account?.ClientId ?? "dynamic_agent_client", ["ext_agent_host_id"] = hostId, ["response_type"] = "code", ["redirect_uri"] = callback, ["scope"] = Scope, ["resource"] = Resource, ["state"] = state, ["nonce"] = nonce, ["code_challenge_method"] = "S256", ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) };
        if (account == null) fields["agent_name_hint"] = "言代Kotoshiro";
        else
        {
            if (account.IdToken.Length > 0) fields["id_token_hint"] = account.IdToken;
            if (account.Email.Length > 0) fields["login_hint"] = account.Email;
            if (!account.Scopes.Contains("chatgpt.tokens.use.direct")) fields["prompt"] = "consent";
        }
        return new Uri(AuthorizeEndpoint + "?" + string.Join("&", fields.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value))));
    }
    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>();
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2); string key = Uri.UnescapeDataString(pieces[0].Replace('+', ' '));
            if (!result.TryAdd(key, pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1].Replace('+', ' ')) : "")) throw new InvalidDataException("授权返回包含重复参数。");
        }
        return result;
    }
    internal static string ValidateCallback(Dictionary<string, string> result, string state, string? registeredClient)
    {
        if (!result.TryGetValue("state", out var actual) || !FixedEquals(actual, state)) throw new InvalidDataException("登录状态校验失败，请重新登录。");
        if (result.TryGetValue("error", out _)) throw new InvalidOperationException("登录或订阅额度授权未完成。你可以在设置中重新授权。");
        if (!result.TryGetValue("code", out var code) || code.Length == 0) throw new InvalidDataException("登录未返回授权码。");
        string clientId = result.GetValueOrDefault("client_id") ?? registeredClient ?? "";
        if (clientId.Length == 0 || clientId == "dynamic_agent_client" || (registeredClient != null && clientId != registeredClient)) throw new InvalidDataException("账号注册编号不匹配，原账号凭证未修改。");
        return clientId;
    }
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    private static async Task<Dictionary<string, string>> CallbackAsync(TcpListener listener, Uri callback, string state, CancellationToken token)
    {
        while (true)
        {
            using var connection = await listener.AcceptTcpClientAsync(token); using var stream = connection.GetStream();
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); requestTimeout.CancelAfter(5000);
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            string? line;
            try { line = await reader.ReadLineAsync(requestTimeout.Token); } catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; } catch (IOException) { continue; }
            var parts = (line ?? "").Split(' '); string host = ""; int bytes = line?.Length ?? 0;
            try { while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(requestTimeout.Token))) { bytes += line.Length; if (bytes > 16000) break; if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) host = line[5..].Trim(); } }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
            catch (IOException) { continue; }
            bool valid = bytes <= 16000 && parts.Length == 3 && parts[0] == "GET" && host == callback.Authority && parts[1].StartsWith("/auth/callback?");
            Dictionary<string, string>? fields = null;
            try { if (valid) fields = ParseQuery(parts[1][parts[1].IndexOf('?')..]); } catch (InvalidDataException) { valid = false; }
            valid = valid && fields != null && fields.TryGetValue("state", out var actual) && FixedEquals(actual, state);
            byte[] body = Encoding.UTF8.GetBytes(valid ? "已收到授权结果，请返回言代Kotoshiro。" : "无效请求，请返回言代Kotoshiro重新登录。");
            byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            try { await stream.WriteAsync(head, token); await stream.WriteAsync(body, token); } catch (IOException) { }
            if (valid) return fields!;
        }
    }
    private async Task<JsonElement> TokenAsync(Dictionary<string, string> values, CancellationToken token)
    {
        using var response = await client.PostAsync(TokenEndpoint, new FormUrlEncodedContent(values), token);
        string body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            string code = ""; try { using var error = JsonDocument.Parse(body); if (error.RootElement.TryGetProperty("error", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString()!; } catch (JsonException) { }
            if (values["grant_type"] == "refresh_token" && new[] { "invalid_grant", "invalid_refresh_token", "token_expired", "refresh_token_expired", "refresh_token_invalidated", "refresh_token_reused" }.Contains(code)) throw new ChatGptPlanException("refresh_invalid", response.StatusCode);
            throw new ChatGptPlanException(code, response.StatusCode);
        }
        try { using var doc = JsonDocument.Parse(body); return doc.RootElement.Clone(); }
        catch (JsonException) { throw new InvalidDataException("ChatGPT 登录服务返回了无法读取的结果。"); }
    }
    private static void ApplyTokens(ChatGptRegistration account, JsonElement response, bool initial)
    {
        string access = response.GetProperty("access_token").GetString() ?? "";
        string refresh = response.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : initial ? "" : account.RefreshToken;
        string id = response.TryGetProperty("id_token", out var it) ? it.GetString() ?? "" : account.IdToken;
        if (access.Length == 0 || !response.TryGetProperty("token_type", out var tt) || !string.Equals(tt.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) || !response.TryGetProperty("expires_in", out var expiry) || !expiry.TryGetInt64(out long seconds) || seconds <= 0 || seconds > 86400) throw new InvalidDataException("ChatGPT 登录凭证缺少必要信息。");
        string[] scopes = response.TryGetProperty("scope", out var scope) ? (scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries) : initial ? [] : account.Scopes;
        account.AccessToken = access; account.RefreshToken = refresh; account.IdToken = id; account.Scopes = scopes; account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        account.EarliestRefreshAt = default;
        if (response.TryGetProperty("earliest_refresh_at", out var earliest))
        {
            if (earliest.ValueKind == JsonValueKind.Number && earliest.TryGetInt64(out long unix)) account.EarliestRefreshAt = DateTimeOffset.FromUnixTimeSeconds(unix);
            else if (earliest.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(earliest.GetString(), out var time)) account.EarliestRefreshAt = time;
        }
    }
    public async Task<string> AccessTokenAsync(string id, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            using var fileLock = await LockAsync(token); var vault = ReadVault(); var account = Find(vault, id);
            if (!account.CanUsePlan) throw new ChatGptPlanException(account.IsSignedIn ? "scope_missing" : "refresh_invalid");
            if (account.AccessToken.Length > 0 && account.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(45)) return account.AccessToken;
            if (account.RefreshToken.Length == 0) throw new ChatGptPlanException("refresh_invalid");
            if (account.EarliestRefreshAt > DateTimeOffset.UtcNow) { if (account.ExpiresAt > DateTimeOffset.UtcNow) return account.AccessToken; throw new ChatGptPlanException("refresh_invalid"); }
            JsonElement response;
            try { response = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = account.ClientId, ["refresh_token"] = account.RefreshToken, ["resource"] = Resource }, token); }
            catch (ChatGptPlanException ex) when (ex.ErrorCode == "refresh_invalid") { account.ClearTokens(); SaveVault(vault); throw; }
            if (response.TryGetProperty("id_token", out var idToken))
            {
                var identity = await VerifyIdentityAsync(idToken.GetString()!, account.ClientId, null, token);
                if (identity.Subject != account.Subject) throw new InvalidDataException("ChatGPT 续期账号不一致，已停止请求。");
            }
            ApplyTokens(account, response, initial: false); SaveVault(vault);
            if (!account.CanUsePlan) throw new ChatGptPlanException("scope_missing"); return account.AccessToken;
        }
        finally { Gate.Release(); }
    }
    public async Task<List<ChatGptModel>> ModelsAsync(string id, CancellationToken token)
    {
        string access = await AccessTokenAsync(id, token);
        using var request = new HttpRequestMessage(HttpMethod.Get, Resource + "/models"); request.Headers.Authorization = new("Bearer", access);
        using var response = await client.SendAsync(request, token); string body = await response.Content.ReadAsStringAsync(token);
        using var doc = JsonDocument.Parse(body);
        if (!response.IsSuccessStatusCode) throw ChatGptPlanException.FromJson(doc.RootElement, response.StatusCode);
        if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) throw new InvalidDataException("ChatGPT 未返回订阅可用模型，请重新登录并检查授权。");
        var result = models.EnumerateArray().Where(x => x.TryGetProperty("visibility", out var v) && v.GetString() == "list")
            .Select(x => new ChatGptModel(x.GetProperty("slug").GetString()!, x.GetProperty("display_name").GetString()!)).Where(x => x.Slug.Length > 0).ToList();
        await Gate.WaitAsync(token); try { using var fileLock = await LockAsync(token); var vault = ReadVault(); Find(vault, id).Models = result; SaveVault(vault); } finally { Gate.Release(); }
        return result;
    }
    public async Task MarkWelcomedAsync(string id, CancellationToken token)
    {
        await Gate.WaitAsync(token); try { using var fileLock = await LockAsync(token); var vault = ReadVault(); Find(vault, id).Welcomed = true; SaveVault(vault); } finally { Gate.Release(); }
    }
    public async Task<bool> SignOutAsync(string id, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            using var fileLock = await LockAsync(token); var vault = ReadVault(); var account = Find(vault, id); bool revoked = account.RefreshToken.Length == 0;
            if (!revoked)
            {
                try
                {
                    string endpoint = await DiscoveryEndpointAsync("revocation_endpoint", token);
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            using var response = await client.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = account.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = account.ClientId }), token);
                            if (response.StatusCode == HttpStatusCode.OK) { revoked = true; break; }
                            if ((int)response.StatusCode < 500) break;
                        }
                        catch (HttpRequestException) { }
                        catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
                        if (attempt < 2) await Task.Delay(250 * (attempt + 1), token);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException) { }
            }
            account.ClearTokens(); SaveVault(vault); return revoked;
        }
        finally { Gate.Release(); }
    }
    private async Task<string> DiscoveryEndpointAsync(string field, CancellationToken token)
    {
        using var response = await client.GetAsync("https://auth.openai.com/.well-known/openid-configuration", token); response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); string value = doc.RootElement.GetProperty(field).GetString()!;
        if (doc.RootElement.GetProperty("issuer").GetString() != "https://auth.openai.com" || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "auth.openai.com" || uri.UserInfo.Length > 0) throw new InvalidDataException("ChatGPT 登录服务配置不符合预期。");
        return value;
    }
    private async Task<(string Subject, string Email)> VerifyIdentityAsync(string jwt, string clientId, string? nonce, CancellationToken token)
    {
        string jwksUrl = await DiscoveryEndpointAsync("jwks_uri", token);
        using var response = await client.GetAsync(jwksUrl, token); response.EnsureSuccessStatusCode();
        using var jwks = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return VerifyIdentity(jwt, jwks.RootElement, clientId, nonce);
    }
    internal static (string Subject, string Email) VerifyIdentity(string jwt, JsonElement jwks, string clientId, string? nonce)
    {
        try
        {
            var parts = jwt.Split('.'); if (parts.Length != 3) throw new CryptographicException();
            using var header = JsonDocument.Parse(Decode64(parts[0])); using var payload = JsonDocument.Parse(Decode64(parts[1])); var h = header.RootElement; var p = payload.RootElement;
            if (h.GetProperty("alg").GetString() != "RS256") throw new CryptographicException();
            string kid = h.GetProperty("kid").GetString()!;
            var key = jwks.GetProperty("keys").EnumerateArray().FirstOrDefault(x => x.GetProperty("kid").GetString() == kid && x.GetProperty("kty").GetString() == "RSA");
            if (key.ValueKind != JsonValueKind.Object || (key.TryGetProperty("use", out var use) && use.GetString() != "sig") || (key.TryGetProperty("alg", out var alg) && alg.GetString() != "RS256")) throw new CryptographicException();
            using var rsa = RSA.Create(); rsa.ImportParameters(new RSAParameters { Modulus = Decode64(key.GetProperty("n").GetString()!), Exponent = Decode64(key.GetProperty("e").GetString()!) });
            if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var audience = p.GetProperty("aud");
            bool matches = audience.ValueKind == JsonValueKind.String ? audience.GetString() == clientId : audience.ValueKind == JsonValueKind.Array && audience.EnumerateArray().Any(x => x.GetString() == clientId);
            if (!matches || p.GetProperty("iss").GetString() != "https://auth.openai.com" || p.GetProperty("exp").GetInt64() <= now || (p.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > now + 30) || (p.TryGetProperty("iat", out var iat) && iat.GetInt64() > now + 30)) throw new CryptographicException();
            if ((p.TryGetProperty("azp", out var azp) && azp.GetString() != clientId) || (audience.ValueKind == JsonValueKind.Array && audience.GetArrayLength() > 1 && !p.TryGetProperty("azp", out _))) throw new CryptographicException();
            if (nonce != null && (!p.TryGetProperty("nonce", out var n) || !FixedEquals(n.GetString() ?? "", nonce))) throw new CryptographicException();
            string subject = p.GetProperty("sub").GetString() ?? ""; if (subject.Length == 0) throw new CryptographicException();
            string email = p.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";
            return (subject, email);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException) { throw new InvalidDataException("ChatGPT 身份校验未通过，未保存新的登录凭证。"); }
    }
    internal async Task SeedForTestAsync(ChatGptRegistration account)
    { await Gate.WaitAsync(); try { using var fileLock = await LockAsync(CancellationToken.None); var vault = ReadVault(); vault.Accounts.Add(account); SaveVault(vault); } finally { Gate.Release(); } }
    public void Dispose() => client.Dispose();
}
