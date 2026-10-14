using System.Globalization;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kotoshiro;

public sealed class BilibiliAccount
{
    public long UserId { get; }
    public string DisplayName { get; }
    internal string CookieHeader { get; }
    internal string Csrf { get; }
    internal DateTimeOffset ExpiresAt { get; }
    internal BilibiliAccount(long userId, string displayName, string cookies, string csrf, DateTimeOffset expiresAt)
    { UserId = userId; DisplayName = displayName; CookieHeader = cookies; Csrf = csrf; ExpiresAt = expiresAt; }
    public override string ToString() => DisplayName;
}

internal sealed record BilibiliQrTicket(string Url, string Key, DateTimeOffset ExpiresAt, long Version = 0);
internal enum BilibiliQrStatus { Waiting, Scanned, Expired, LoggedIn }
internal sealed record BilibiliQrResult(BilibiliQrStatus Status, BilibiliAccount? Account = null);
internal sealed class BilibiliAccountVault
{
    public long UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public Dictionary<string, string> Cookies { get; set; } = [];
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Own QR-login session only. Never reads cookies from browsers or other apps.</summary>
public sealed class BilibiliAccountStore : IDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<string, long> Versions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] CookieNames = ["DedeUserID", "DedeUserID__ckMd5", "SESSDATA", "bili_jct"];
    private readonly string root;
    private readonly HttpClient client;
    private string VaultPath => Path.Combine(root, "bilibili.dat");
    private long Version => Versions.GetOrAdd(VaultPath, 0);
    public BilibiliAccount? Current { get; private set; }
    public bool IsLoggedIn => Current != null && Current.ExpiresAt > DateTimeOffset.UtcNow;

    public BilibiliAccountStore(string? root = null, HttpMessageHandler? handler = null)
    {
        this.root = Path.GetFullPath(root ?? SettingsStore.Root);
        client = handler == null ? new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) : new(handler);
        client.Timeout = TimeSpan.FromSeconds(20); client.MaxResponseContentBufferSize = 1048576;
    }

    public async Task<BilibiliAccount?> GetAsync(CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            if (!File.Exists(VaultPath)) { Current = null; return null; }
            BilibiliAccountVault vault;
            try
            {
                byte[] clear = ProtectedData.Unprotect(await File.ReadAllBytesAsync(VaultPath, token), null, DataProtectionScope.CurrentUser);
                try { vault = JsonSerializer.Deserialize<BilibiliAccountVault>(clear) ?? throw new InvalidDataException(); }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException)
            { throw new InvalidDataException("无法读取本机 B 站登录信息，请退出账号后重新扫码。"); }
            if (vault.ExpiresAt <= DateTimeOffset.UtcNow) { DeleteVault(); return null; }
            Current = AccountFrom(vault); return Current;
        }
        finally { Gate.Release(); }
    }

    public async Task<BilibiliAccount?> CheckAsync(CancellationToken token = default)
    {
        long version = Version;
        var current = await GetAsync(token);
        if (current == null) return null;
        var verified = await VerifyAsync(current, token);
        if (verified == null) { await ClearAsync(token, version); return await GetAsync(token); }
        if (!await SaveAsync(ToVault(verified), token, version, false)) return await GetAsync(token);
        return Current;
    }

    /// <summary>Remove this app's saved login. It does not log out browsers or revoke other sessions.</summary>
    public async Task LogoutAsync(CancellationToken token = default)
        => await ClearAsync(token, null);

    private async Task ClearAsync(CancellationToken token, long? expectedVersion)
    {
        await Gate.WaitAsync(token);
        try { if (!expectedVersion.HasValue || expectedVersion.Value == Version) DeleteVault(); }
        finally { Gate.Release(); }
    }

    internal async Task<BilibiliQrTicket> BeginQrAsync(CancellationToken token)
    {
        long version = Version;
        using var doc = await GetJsonAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate?source=main_web", null, token);
        var rootJson = doc.RootElement;
        if (!ApiOk(rootJson) || !rootJson.TryGetProperty("data", out var data)) throw new InvalidOperationException("B 站暂时无法生成登录二维码，请稍后重试。");
        string url = String(data, "url"), key = String(data, "qrcode_key");
        if (!Regex.IsMatch(key, "^[0-9a-fA-F]{32}$") || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !OfficialQrPath(uri) || !uri.Query.Contains("qrcode_key=" + key, StringComparison.Ordinal))
            throw new InvalidDataException("B 站返回的登录二维码格式异常，请重试。");
        return new(url, key, DateTimeOffset.UtcNow.AddSeconds(180), version);
    }

    private static bool OfficialQrPath(Uri uri) =>
        (uri.Host == "account.bilibili.com" && uri.AbsolutePath == "/h5/account-h5/auth/scan-web") ||
        (uri.Host == "passport.bilibili.com" && uri.AbsolutePath == "/h5-app/passport/login/scan");

    internal async Task<BilibiliQrResult> PollQrAsync(BilibiliQrTicket ticket, CancellationToken token)
    {
        if (ticket.ExpiresAt <= DateTimeOffset.UtcNow || ticket.Version != Version) return new(BilibiliQrStatus.Expired);
        if (!Regex.IsMatch(ticket.Key, "^[0-9a-fA-F]{32}$")) throw new InvalidDataException("登录二维码已失效，请刷新。");
        using var request = Request("https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + Uri.EscapeDataString(ticket.Key));
        using var response = await SendAsync(request, token);
        using var doc = await ParseAsync(response, token);
        var json = doc.RootElement;
        if (!ApiOk(json) || !json.TryGetProperty("data", out var data) || !data.TryGetProperty("code", out var status) || !status.TryGetInt32(out int code)) throw new InvalidOperationException("B 站登录状态未返回，请刷新二维码重试。");
        if (code == 86101) return new(BilibiliQrStatus.Waiting);
        if (code == 86090) return new(BilibiliQrStatus.Scanned);
        if (code == 86038) return new(BilibiliQrStatus.Expired);
        if (code != 0) throw new InvalidOperationException("B 站未接受这次扫码登录，请刷新二维码重试。");
        var vault = ParseCookies(response);
        var candidate = AccountFrom(vault);
        var verified = await VerifyAsync(candidate, token) ?? throw new InvalidOperationException("B 站登录信息未生效，请重新扫码。");
        if (!await SaveAsync(ToVault(verified), token, ticket.Version, true)) return new(BilibiliQrStatus.Expired);
        return new(BilibiliQrStatus.LoggedIn, Current);
    }

    private async Task<BilibiliAccount?> VerifyAsync(BilibiliAccount account, CancellationToken token)
    {
        using var doc = await GetJsonAsync("https://api.bilibili.com/x/web-interface/nav", account.CookieHeader, token);
        var json = doc.RootElement;
        if (json.TryGetProperty("code", out var code) && code.TryGetInt32(out int apiCode) && apiCode == -101) return null;
        if (!ApiOk(json) || !json.TryGetProperty("data", out var data)) throw new InvalidOperationException("B 站暂时无法验证账号，已保存的登录信息会保留。");
        if (!data.TryGetProperty("isLogin", out var login) || login.ValueKind != JsonValueKind.True) return null;
        if (!data.TryGetProperty("mid", out var mid) || !mid.TryGetInt64(out long uid) || uid != account.UserId) throw new InvalidDataException("B 站返回的账号信息不一致，请重新扫码。");
        string name = CleanName(String(data, "uname"));
        return new(account.UserId, name.Length > 0 ? name : "B 站用户 " + uid, account.CookieHeader, account.Csrf, account.ExpiresAt);
    }

    private async Task<bool> SaveAsync(BilibiliAccountVault vault, CancellationToken token, long expectedVersion, bool advanceVersion)
    {
        var account = AccountFrom(vault);
        await Gate.WaitAsync(token);
        try
        {
            if (Version != expectedVersion) return false;
            Directory.CreateDirectory(root);
            byte[] clear = JsonSerializer.SerializeToUtf8Bytes(vault);
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(clear); }
            string temp = VaultPath + ".tmp";
            try { await File.WriteAllBytesAsync(temp, encrypted, token); token.ThrowIfCancellationRequested(); File.Move(temp, VaultPath, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            Current = account;
            if (advanceVersion) Versions.AddOrUpdate(VaultPath, 1, (_, value) => value + 1);
            return true;
        }
        finally { Gate.Release(); }
    }

    private void DeleteVault() { if (File.Exists(VaultPath)) File.Delete(VaultPath); Current = null; Versions.AddOrUpdate(VaultPath, 1, (_, value) => value + 1); }
    private static BilibiliAccountVault ToVault(BilibiliAccount account)
    {
        var cookies = account.CookieHeader.Split("; ", StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x[1]);
        return new() { UserId = account.UserId, DisplayName = account.DisplayName, Cookies = cookies, ExpiresAt = account.ExpiresAt };
    }
    private static BilibiliAccount AccountFrom(BilibiliAccountVault vault)
    {
        if (vault.Cookies == null || vault.UserId <= 0 || vault.ExpiresAt <= DateTimeOffset.UtcNow || vault.ExpiresAt > DateTimeOffset.UtcNow.AddDays(181) || !CookieNames.All(name => vault.Cookies.TryGetValue(name, out string? value) && ValidCookie(value)) || !long.TryParse(vault.Cookies["DedeUserID"], NumberStyles.None, CultureInfo.InvariantCulture, out long uid) || uid != vault.UserId)
            throw new InvalidDataException("B 站登录信息不完整，请退出账号后重新扫码。");
        string cookies = string.Join("; ", CookieNames.Select(name => name + "=" + vault.Cookies[name]));
        return new(uid, CleanName(vault.DisplayName), cookies, vault.Cookies["bili_jct"], vault.ExpiresAt);
    }
    private static bool ValidCookie(string value) => value.Length is > 0 and <= 4096 && value.All(c => c is >= '!' and <= '~' && c != ';' && c != ',');
    private static BilibiliAccountVault ParseCookies(HttpResponseMessage response)
    {
        var vault = new BilibiliAccountVault { ExpiresAt = DateTimeOffset.UtcNow.AddDays(180) };
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers)) throw new InvalidDataException("B 站没有返回登录凭证，请重新扫码。");
        foreach (string header in headers)
        {
            string[] parts = header.Split(';', StringSplitOptions.TrimEntries);
            string[] pair = parts[0].Split('=', 2);
            if (pair.Length != 2 || !CookieNames.Contains(pair[0]) || !ValidCookie(pair[1])) continue;
            string? domain = parts.FirstOrDefault(x => x.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1].TrimStart('.');
            if (domain != null && domain != "bilibili.com" && domain != "passport.bilibili.com") continue;
            if (pair[0] == "SESSDATA")
            {
                string? maxAge = parts.FirstOrDefault(x => x.StartsWith("Max-Age=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1];
                string? expires = parts.FirstOrDefault(x => x.StartsWith("Expires=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1];
                if (long.TryParse(maxAge, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)) vault.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, -1, 180L * 86400));
                else if (DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) && date < vault.ExpiresAt) vault.ExpiresAt = date;
            }
            vault.Cookies[pair[0]] = pair[1];
        }
        if (!vault.Cookies.TryGetValue("DedeUserID", out string? id) || !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long uid)) throw new InvalidDataException("B 站没有返回完整账号信息，请重新扫码。");
        vault.UserId = uid; vault.DisplayName = "B 站用户 " + uid; return vault;
    }
    private static string CleanName(string text) => new(text.Where(c => !char.IsControl(c)).Take(80).ToArray());
    private static string String(JsonElement json, string name) => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool ApiOk(JsonElement json) => json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var code) && code.TryGetInt32(out int number) && number == 0;
    private static HttpRequestMessage Request(string url, string? cookies = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        request.Headers.Referrer = new("https://www.bilibili.com/");
        if (!string.IsNullOrEmpty(cookies)) request.Headers.TryAddWithoutValidation("Cookie", cookies);
        return request;
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token); }
        catch (HttpRequestException) { throw new HttpRequestException("无法连接 B 站登录服务，请检查网络后重试。"); }
        if (!response.IsSuccessStatusCode)
        { int status = (int)response.StatusCode; response.Dispose(); throw new HttpRequestException($"B 站登录服务暂时不可用（HTTP {status}）。请稍后重试。"); }
        return response;
    }
    private async Task<JsonDocument> GetJsonAsync(string url, string? cookies, CancellationToken token)
    {
        using var request = Request(url, cookies); using var response = await SendAsync(request, token); return await ParseAsync(response, token);
    }
    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            if (response.Content.Headers.ContentLength is > 1048576) throw new InvalidDataException("B 站登录服务返回异常，请重试。");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var memory = new MemoryStream(); byte[] chunk = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(chunk, token)) > 0)
            { if (memory.Length + count > 1048576) throw new InvalidDataException("B 站登录服务返回异常，请重试。"); await memory.WriteAsync(chunk.AsMemory(0, count), token); }
            return JsonDocument.Parse(memory.ToArray());
        }
        catch (JsonException) { throw new InvalidDataException("B 站登录服务返回格式异常，请稍后重试。"); }
    }
    public void Dispose() => client.Dispose();
}
