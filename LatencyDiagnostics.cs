using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace Kotoshiro;

public record LatencyRun(int Number, double RecognitionSeconds, double? TranslationSeconds, TranslationTiming? ApiTiming, string Japanese, string? Chinese, string? Error);
public record LatencyReport(string Model, string Mode, string ApiModel, double AudioSeconds, int SegmentLimitSeconds, double ModelLoadSeconds, double InputConversionSeconds, double SampleSegmentSeconds, double SamplePauseWaitSeconds, List<LatencyRun> Runs)
{
    public string ToText()
    {
        var text = new StringBuilder($"运行方式：{Mode}\n识别模型：{Model}；翻译模型：{ApiModel}\n测试录音：{AudioSeconds:F2} 秒\n识别服务准备：{ModelLoadSeconds:F3} 秒（仅启动时）\n读取与转换音频：{InputConversionSeconds:F3} 秒（离线文件测试）\n当前最长分段：{SegmentLimitSeconds} 秒\n样本经过现有分段器：从首个样本到提交 {SampleSegmentSeconds:F2} 秒，录音结尾后约 {SamplePauseWaitSeconds:F2} 秒\n（分段时间按音频样本时钟计算，模拟正常播放；未播放系统声音）\n\n");
        foreach (var run in Runs)
        {
            text.AppendLine($"第 {run.Number} 次：识别 {run.RecognitionSeconds:F3} 秒；翻译 {(run.TranslationSeconds.HasValue ? $"{run.TranslationSeconds:F3} 秒" : "未完成")}。");
            if (run.ApiTiming != null) text.AppendLine($"  收到接口响应头前 {run.ApiTiming.ResponseWaitSeconds:F3} 秒；等待并读取完整正文 {run.ApiTiming.BodyReadSeconds:F3} 秒。");
            if (run.Error != null) text.AppendLine("  " + run.Error);
            text.AppendLine("  日语：" + run.Japanese); text.AppendLine("  中文：" + run.Chinese);
        }
        text.AppendLine("\n识别测试包括服务往返与结果解析；API 模式含音频上传、网络等待和服务生成，本地首轮可能包含 GPU 首次运算开销。");
        text.AppendLine("翻译测试为真实付费接口调用；请求到完整正文包含联网、服务排队和生成，无法仅凭客户端计时分别拆开。");
        text.AppendLine("当前使用非流式翻译：得到完整译文后显示。以上不包含实时排队或屏幕实际绘制耗时。");
        text.AppendLine("直播主界面另外显示每段实际排队、识别、翻译和字幕更新；字幕更新时间为 UI 调度与控件更新，不等于屏幕呈现时间。");
        return text.ToString();
    }
}

public static class LatencyDiagnostics
{
    public static async Task<LatencyReport> MeasureAsync(AppSettings settings, string samplePath, int repetitions, bool translate, IProgress<string>? progress, CancellationToken token)
    {
        RecognitionEngines.Validate(settings);
        if (translate && !ApiTranslator.IsConfigured(settings)) throw new InvalidOperationException("请先设置翻译服务：登录 ChatGPT 并选择模型，或填写 API 密钥。");
        var timer = Stopwatch.StartNew(); var sample = AudioFiles.Read(samplePath, 30); double conversion = timer.Elapsed.TotalSeconds;
        double sampleSegment = SimulateSegmentation(sample.Bytes, settings.SegmentSeconds);
        progress?.Report("正在准备识别服务…");
        using var engine = RecognitionEngines.Create(settings); timer.Restart(); await engine.StartAsync(settings, token); double load = timer.Elapsed.TotalSeconds;
        using var translator = new ApiTranslator(settings);
        var runs = new List<LatencyRun>();
        for (int i = 1; i <= Math.Clamp(repetitions, 1, 5); i++)
        {
            progress?.Report($"正在测试第 {i} 次识别…");
            timer.Restart(); string japanese = await engine.TranscribeAsync(sample.Bytes, token); double recognition = timer.Elapsed.TotalSeconds;
            double? translationSeconds = null; string? chinese = null, error = null;
            if (translate && !string.IsNullOrWhiteSpace(japanese))
            {
                progress?.Report($"第 {i} 次识别 {recognition:F3} 秒，正在测试翻译…");
                timer.Restart();
                try { chinese = await translator.TranslateAsync(japanese, token); translationSeconds = timer.Elapsed.TotalSeconds; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException)
                {
                    error = ex is OperationCanceledException ? $"接口请求超时（约 {timer.Elapsed.TotalSeconds:F1} 秒）。" : ex.Message;
                }
            }
            runs.Add(new(i, recognition, translationSeconds, translate ? translator.LastTiming : null, japanese, chinese, error));
            progress?.Report($"第 {i} 次完成：识别 {recognition:F3} 秒；翻译 {(translationSeconds.HasValue ? $"{translationSeconds:F3} 秒" : error ?? "未测试")}。");
            if (error != null) break;
        }
        return new(RecognitionEngines.ModelLabel(settings), engine.Mode, ApiTranslator.ServiceLabel(settings), sample.Duration, settings.SegmentSeconds, load, conversion, sampleSegment, Math.Max(0, sampleSegment - sample.Duration), runs);
    }
    private static double SimulateSegmentation(byte[] wave, int limit)
    {
        using var reader = new WaveFileReader(new MemoryStream(wave));
        var source = reader.ToSampleProvider(); var segmenter = new AudioSegmenter(limit); float[] buffer = new float[1600];
        int samples = 0; double first = -1;
        segmenter.Ready += (_, _) => { if (first < 0) first = samples / 16000.0; };
        int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) > 0) { samples += count; segmenter.Push(buffer.AsSpan(0, count)); }
        for (int i = 0; i < 20 && first < 0; i++) { samples += buffer.Length; segmenter.Push(new float[buffer.Length]); }
        return first < 0 ? samples / 16000.0 : first;
    }
    internal static async Task RunSavedAsync(string samplePath, string destination)
    {
        Directory.CreateDirectory(destination);
        var settings = SettingsStore.Load();
        var report = await MeasureAsync(settings, samplePath, 3, true, new FileProgress(Path.Combine(destination, "progress.txt")), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(destination, "latency.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(destination, "latency.txt"), report.ToText());
    }
    private sealed class FileProgress(string path) : IProgress<string>
    {
        public void Report(string value) => File.AppendAllText(path, value + Environment.NewLine);
    }
}
