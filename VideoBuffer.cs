using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography.X509Certificates;

namespace Kotoshiro;

public record VideoPiece(string Name, double Start, double Duration) { public double End => Start + Duration; }
public record SubtitleCue(double Start, double End, string Japanese, string Chinese);
public static class MediaTools
{
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "player");
    public static string Ffmpeg => Path.Combine(DirectoryPath, "ffmpeg.exe");
    public static string Mpv => Path.Combine(DirectoryPath, "mpv.exe");
    public static void Check() { if (!File.Exists(Ffmpeg) || !File.Exists(Mpv)) throw new FileNotFoundException("播放器组件缺失，请完整解压新版软件，包括 player 文件夹。"); }
    internal static Process Launch(string path, IEnumerable<string> args, Action<string>? onError = null)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(path)! };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        var process = new Process { StartInfo = start }; process.ErrorDataReceived += (_, e) => { if (e.Data != null) onError?.Invoke(e.Data); }; process.OutputDataReceived += (_, _) => { };
        if (!process.Start()) throw new InvalidOperationException("无法启动播放器组件。");
        process.BeginErrorReadLine(); process.BeginOutputReadLine(); return process;
    }
    internal static void Stop(Process? process)
    {
        if (process == null) return;
        try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(2000); } } catch (InvalidOperationException) { }
        process.Dispose();
    }
}
public sealed class VideoBuffer : IDisposable
{
    private Process? recorder;
    private readonly LocalVideoServer server;
    public string Folder { get; }
    public string Playlist => Path.Combine(Folder, "live.m3u8");
    public string PlaybackUrl => server.PlaylistUrl;
    public double Edge { get; private set; }
    private long nextIndex;
    private string failureDetail = "网络连接或直播源异常";
    public string FailureMessage => $"直播取流失败：{Volatile.Read(ref failureDetail)}。请停止后重新打开直播。";
    public VideoBuffer(string modelFolder)
    {
        if (string.IsNullOrWhiteSpace(modelFolder)) throw new InvalidOperationException("请先选择模型存放文件夹，直播缓存也会放在那里。");
        Folder = Path.Combine(Branding.ModelRuntimeDirectory(Path.GetFullPath(modelFolder)), "player-cache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        server = new LocalVideoServer(Folder);
    }
    internal IEnumerable<string> RecorderArgs(string input, string? referer, bool realtimeFile = false)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
        if (realtimeFile) args.Add("-re");
        else args.AddRange(["-rw_timeout", "20000000", "-tls_verify", "1", "-ca_file", Path.Combine(Folder, "windows-roots.pem"), "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_on_network_error", "1", "-reconnect_on_http_error", "429,5xx", "-reconnect_max_retries", "3", "-reconnect_delay_max", "5", "-reconnect_delay_total_max", "20", "-user_agent", "Mozilla/5.0", "-headers", $"Referer: {referer}\r\n"]);
        args.AddRange(["-i", input, "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-f", "hls", "-hls_time", "2", "-hls_list_size", "90", "-hls_delete_threshold", "30", "-hls_flags", "delete_segments+temp_file+independent_segments", "-hls_segment_filename", Path.Combine(Folder, "part%08d.ts"), Playlist]);
        return args;
    }
    public void Start(BilibiliStream stream)
    {
        MediaTools.Check(); WindowsMediaTrust.WriteRoots(Path.Combine(Folder, "windows-roots.pem"));
        recorder = MediaTools.Launch(MediaTools.Ffmpeg, RecorderArgs(stream.Source.AbsoluteUri, stream.RoomPage), line =>
        {
            // Keep only known categories; never persist source URLs, query signatures or raw stderr.
            string? detail = ClassifyFailure(line); if (detail != null) Volatile.Write(ref failureDetail, detail);
        });
    }
    internal static string? ClassifyFailure(string line)
    {
        if (line.Contains("[tls", StringComparison.OrdinalIgnoreCase) || line.Contains("certificate", StringComparison.OrdinalIgnoreCase)) return "HTTPS 证书校验或握手未完成";
        var http = Regex.Match(line, @"(?:HTTP error|Server returned)\s+(\d{3})", RegexOptions.IgnoreCase);
        if (http.Success) return "直播源返回 HTTP " + http.Groups[1].Value;
        if (line.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return "直播源连接超时";
        if (line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return "直播缓存文件无法写入";
        if (line.Contains("No space left", StringComparison.OrdinalIgnoreCase)) return "缓存所在磁盘空间不足";
        return null;
    }
    internal void StartFile(string input) { MediaTools.Check(); recorder = MediaTools.Launch(MediaTools.Ffmpeg, RecorderArgs(input, null, true)); }
    public bool Finished => recorder != null && recorder.HasExited;
    public bool Failed => Finished && recorder!.ExitCode != 0;
    public List<VideoPiece> TakeNewPieces()
    {
        if (!File.Exists(Playlist)) return [];
        string text;
        try { using var stream = new FileStream(Playlist, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var reader = new StreamReader(stream); text = reader.ReadToEnd(); }
        catch (IOException) { return []; }
        var parsed = ParsePlaylist(text); var result = new List<VideoPiece>();
        foreach (var part in parsed)
        {
            if (part.Index < nextIndex) continue;
            if (part.Index != nextIndex) throw new InvalidOperationException("直播缓存发生缺段，请停止后重新打开直播。");
            result.Add(new(part.Name, Edge, part.Duration)); Edge += part.Duration; nextIndex++;
        }
        return result;
    }
    internal static List<(long Index, string Name, double Duration)> ParsePlaylist(string text)
    {
        var result = new List<(long, string, double)>(); double duration = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("#EXTINF:"))
            {
                if (!double.TryParse(line[8..].TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out duration) || !double.IsFinite(duration) || duration <= 0 || duration > 60) throw new InvalidDataException("直播缓存时长无效。");
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                var name = Regex.Match(line, "^part([0-9]{8})\\.ts$");
                if (!name.Success || duration == 0) throw new InvalidDataException("直播缓存清单无效。");
                result.Add((long.Parse(name.Groups[1].Value, CultureInfo.InvariantCulture), line, duration)); duration = 0;
            }
        }
        return result;
    }
    public async Task<byte[]> DecodeAsync(IReadOnlyList<VideoPiece> pieces, CancellationToken token)
    {
        string list = Path.Combine(Folder, "decode-" + Guid.NewGuid().ToString("N") + ".txt"), wave = Path.ChangeExtension(list, ".wav");
        await File.WriteAllLinesAsync(list, pieces.Select(x => "file '" + x.Name + "'"), new UTF8Encoding(false), token);
        using var process = MediaTools.Launch(MediaTools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "concat", "-safe", "1", "-i", list, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav", wave]);
        try
        {
            using var kill = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0 || !File.Exists(wave)) throw new InvalidDataException("无法读取直播音频，请重新打开直播。");
            if (new FileInfo(wave).Length > 8_000_000) throw new InvalidDataException("直播音频分段过长。");
            return await File.ReadAllBytesAsync(wave, token);
        }
        finally
        {
            try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(2000); } } catch (InvalidOperationException) { }
            try { if (File.Exists(wave)) File.Delete(wave); if (File.Exists(list)) File.Delete(list); } catch (IOException) { }
        }
    }
    public void Dispose()
    {
        MediaTools.Stop(recorder); recorder = null;
        server.Dispose();
        string parent = Path.GetDirectoryName(Folder)!;
        string resolved = Path.GetFullPath(Folder);
        // Delete only this instance's GUID directory; never the model folder or shared cache parent.
        if (Path.GetDirectoryName(resolved) == parent && Regex.IsMatch(Path.GetFileName(resolved), "^[a-f0-9]{32}$"))
        { try { Directory.Delete(resolved, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
internal static class WindowsMediaTrust
{
    internal static void WriteRoots(string path)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var certificates = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var denied = new X509Store(StoreName.Disallowed, location); denied.Open(OpenFlags.ReadOnly);
            foreach (var cert in denied.Certificates) { using (cert) blocked.Add(cert.Thumbprint); }
            using var roots = new X509Store(StoreName.Root, location); roots.Open(OpenFlags.ReadOnly);
            foreach (var cert in roots.Certificates) { using (cert) certificates.TryAdd(cert.Thumbprint, cert.RawData); }
        }
        var pem = new StringBuilder();
        foreach (var cert in certificates.Where(x => !blocked.Contains(x.Key)))
        { pem.AppendLine("-----BEGIN CERTIFICATE-----").AppendLine(Convert.ToBase64String(cert.Value, Base64FormattingOptions.InsertLineBreaks)).AppendLine("-----END CERTIFICATE-----"); }
        if (pem.Length == 0) throw new InvalidOperationException("Windows 没有可用的受信任根证书，请更新系统证书后重试。");
        File.WriteAllText(path, pem.ToString(), Encoding.ASCII);
    }
}
public sealed class VideoPlayer : IDisposable
{
    private Process? process;
    private NamedPipeClientStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private long requestId;
    private readonly SemaphoreSlim gate = new(1, 1);
    public event Action<string>? KeyCommand;
    public bool Finished => process != null && process.HasExited;
    public async Task StartAsync(string playlist, IntPtr window, CancellationToken token, bool headless = false, string? inputConfig = null, int fontSize = 30)
    {
        string name = "KotoshiroPlayer-" + Guid.NewGuid().ToString("N");
        var args = new List<string> { "--no-config", "--idle=yes", "--pause=yes", "--start=0", "--keep-open=yes", "--osc=no", "--input-default-bindings=no", "--input-terminal=no", "--demuxer-lavf-o=live_start_index=0", "--cache=yes", "--cache-pause=yes", "--cache-pause-wait=2", "--demuxer-max-bytes=64MiB", "--demuxer-max-back-bytes=16MiB", "--osd-align-y=bottom", "--osd-align-x=center", "--osd-font-size=" + Math.Clamp(fontSize, 14, 48).ToString(CultureInfo.InvariantCulture), "--osd-border-size=2", "--osd-margin-y=45", "--title=言代Kotoshiro · B站延迟播放器", "--input-ipc-server=\\\\.\\pipe\\" + name };
        if (inputConfig != null)
        {
            await File.WriteAllTextAsync(inputConfig, "ESC script-message kotoshiro-exit-fullscreen\nSPACE script-message kotoshiro-toggle-pause\nf script-message kotoshiro-toggle-fullscreen\nF5 script-message kotoshiro-refresh\nCtrl+r script-message kotoshiro-refresh\n", token); args.Add("--input-conf=" + inputConfig);
        }
        if (headless) args.AddRange(["--vo=null", "--ao=null"]);
        else { args.Add("--hwdec=auto-safe"); args.Add("--wid=" + window.ToInt64().ToString(CultureInfo.InvariantCulture)); }
        args.Add(playlist); process = MediaTools.Launch(MediaTools.Mpv, args);
        pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000, token); reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true); writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        for (int i = 0; i < 100; i++) { if (await PositionAsync(token) != null) return; await Task.Delay(100, token); }
        throw new TimeoutException("播放器未能打开缓存，请重新打开直播。");
    }
    private async Task<JsonElement?> CommandAsync(object[] command, CancellationToken token)
    {
        if (writer == null || reader == null) throw new InvalidOperationException("播放器尚未就绪。");
        await gate.WaitAsync(token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(3000); long id = ++requestId;
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { command, request_id = id }).AsMemory(), deadline.Token);
            while (true)
            {
                string? line = await reader.ReadLineAsync(deadline.Token); if (line == null) throw new IOException("播放器连接已关闭。");
                using var json = JsonDocument.Parse(line); var root = json.RootElement;
                if (root.TryGetProperty("event", out var ev) && ev.GetString() == "client-message" && root.TryGetProperty("args", out var values) && values.GetArrayLength() > 0)
                { KeyCommand?.Invoke(values[0].GetString() ?? ""); continue; }
                if (!root.TryGetProperty("request_id", out var rid) || rid.GetInt64() != id) continue;
                if (root.GetProperty("error").GetString() != "success") return null;
                return root.TryGetProperty("data", out var data) ? data.Clone() : null;
            }
        }
        finally { gate.Release(); }
    }
    public async Task<double?> PositionAsync(CancellationToken token)
    { var value = await CommandAsync(["get_property", "time-pos"], token); return value is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : null; }
    public async Task PauseAsync(bool value, CancellationToken token) => await CommandAsync(["set_property", "pause", value], token);
    public async Task VolumeAsync(int value, CancellationToken token) => await CommandAsync(["set_property", "volume", Math.Clamp(value, 0, 100)], token);
    public async Task CaptionAsync(string text, CancellationToken token) => await CommandAsync(["show-text", text, 300000], token);
    internal async Task<JsonElement?> PropertyAsync(string name, CancellationToken token) => await CommandAsync(["get_property", name], token);
    internal async Task ScreenshotAsync(string path, CancellationToken token, string mode = "video") => await CommandAsync(["screenshot-to-file", path, mode], token);
    internal async Task TestKeyAsync(string message, CancellationToken token) => await CommandAsync(["script-message", message], token);
    public void Dispose()
    {
        MediaTools.Stop(process); process = null; writer?.Dispose(); reader?.Dispose(); pipe?.Dispose(); writer = null; reader = null; pipe = null;
    }
}
internal static class SubtitleTimeline
{
    internal static SubtitleCue? At(IEnumerable<SubtitleCue> cues, double position) => cues.FirstOrDefault(x => position >= x.Start && position < x.End);
    internal static bool ShouldHold(double position, double edge, double resolvedThrough, int delay, bool wait, bool inputFinished)
        => (!inputFinished && edge - position < 0.5) || (wait && position + 0.15 >= resolvedThrough && resolvedThrough < edge - 0.1);
}
