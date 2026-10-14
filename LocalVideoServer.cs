using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Kotoshiro;

internal sealed class LocalVideoServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly string folder, secret = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim connections = new(8, 8);
    public string PlaylistUrl { get; }
    public LocalVideoServer(string folder)
    {
        this.folder = folder; listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        PlaylistUrl = $"http://127.0.0.1:{port}/{secret}/live.m3u8";
        _ = Task.Run(ListenAsync);
    }
    private async Task ListenAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await connections.WaitAsync(lifetime.Token);
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(lifetime.Token); }
                catch { connections.Release(); throw; }
                _ = Task.Run(async () => { try { await ServeAsync(client); } finally { client.Dispose(); connections.Release(); } });
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15)); var token = timeout.Token;
        try
        {
            using var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            string? first = await reader.ReadLineAsync(token); var request = (first ?? "").Split(' '); int bytes = first?.Length ?? 0; string? line, range = null;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(token))) { bytes += line.Length; if (bytes > 16000) return; if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line[6..].Trim(); }
            if (request.Length != 3 || request[0] is not ("GET" or "HEAD") || !request[1].StartsWith('/' + secret + '/', StringComparison.Ordinal)) { await ErrorAsync(stream, 404, token); return; }
            string name = request[1][(secret.Length + 2)..];
            if (name != "live.m3u8" && !Regex.IsMatch(name, "^part[0-9]{8}\\.ts$")) { await ErrorAsync(stream, 404, token); return; }
            string path = Path.Combine(folder, name); if (!File.Exists(path)) { await ErrorAsync(stream, 404, token); return; }
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, useAsync: true);
            long size = file.Length, start = 0, end = size - 1; bool partial = false;
            if (range != null)
            {
                var match = Regex.Match(range, "^bytes=([0-9]+)-([0-9]*)$");
                if (!match.Success || !long.TryParse(match.Groups[1].Value, out start) || start >= size || (match.Groups[2].Length > 0 && !long.TryParse(match.Groups[2].Value, out end))) { await ErrorAsync(stream, 416, token); return; }
                end = Math.Min(end, size - 1); if (end < start) { await ErrorAsync(stream, 416, token); return; } partial = true;
            }
            string type = name.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t";
            long count = Math.Max(0, end - start + 1);
            string headers = $"HTTP/1.1 {(partial ? "206 Partial Content" : "200 OK")}\r\nContent-Type: {type}\r\nContent-Length: {count}\r\nCache-Control: no-store\r\nAccept-Ranges: bytes\r\nConnection: close\r\n" + (partial ? $"Content-Range: bytes {start}-{end}/{size}\r\n" : "") + "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), token);
            if (request[0] == "HEAD") return;
            file.Position = start; byte[] block = new byte[65536];
            while (count > 0) { int read = await file.ReadAsync(block.AsMemory(0, (int)Math.Min(block.Length, count)), token); if (read == 0) break; await stream.WriteAsync(block.AsMemory(0, read), token); count -= read; }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private static Task ErrorAsync(Stream stream, int code, CancellationToken token) => stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token).AsTask();
    public void Dispose() { lifetime.Cancel(); listener.Stop(); }
}
