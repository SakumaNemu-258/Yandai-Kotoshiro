using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Threading.Channels;

namespace Kotoshiro;

public record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}
public record AudioChunk(byte[] Wave, DateTime CapturedAt, double Duration, long ReadyTimestamp = 0);

public static class AudioFiles
{
    public static (byte[] Bytes, double Duration) Read(string path, int maxSeconds)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider source = reader;
        if (source.WaveFormat.Channels == 2) source = new StereoToMonoSampleProvider(source);
        else if (source.WaveFormat.Channels > 2) source = new DownmixSampleProvider(source);
        source = new WdlResamplingSampleProvider(source, 16000);
        var values = new float[maxSeconds * 16000]; int total = 0, count;
        while (total < values.Length && (count = source.Read(values, total, values.Length - total)) > 0) total += count;
        if (total < 1600) throw new InvalidDataException("音频过短或格式无法读取，请尝试 WAV 格式。");
        return (AudioSegmenter.WaveBytes(values.Take(total).ToArray()), total / 16000.0);
    }
}

public sealed class AudioSegmenter
{
    private readonly int maxSamples;
    private readonly List<float> samples = [];
    private int silence;
    private bool hasSignal;
    private const float SignalThreshold = 0.007f;
    public event Action<float[], double>? Ready;
    public AudioSegmenter(int seconds) => maxSamples = Math.Clamp(seconds, 3, 12) * 16000;
    public void Push(ReadOnlySpan<float> block)
    {
        double energy = 0;
        foreach (float value in block) energy += value * value;
        bool signal = block.Length > 0 && Math.Sqrt(energy / block.Length) >= SignalThreshold;
        if (signal) { hasSignal = true; silence = 0; }
        else silence += block.Length;
        // Keep a short lead-in so syllables before the level threshold are retained.
        if (!hasSignal && samples.Count > 4000) samples.RemoveRange(0, samples.Count - 4000);
        foreach (float value in block) samples.Add(value);
        if (samples.Count >= maxSamples || (hasSignal && silence >= 9600 && samples.Count >= 24000)) Emit();
    }
    private void Emit()
    {
        if (hasSignal && samples.Count >= 8000) Ready?.Invoke(samples.ToArray(), samples.Count / 16000.0);
        samples.Clear(); silence = 0; hasSignal = false;
    }
    public void Flush() => Emit();
    public static byte[] WaveBytes(float[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(stream), new WaveFormat(16000, 16, 1)))
        {
            byte[] pcm = new byte[values.Length * 2];
            for (int i = 0; i < values.Length; i++)
            {
                short value = (short)(Math.Clamp(values[i], -1, 1) * short.MaxValue);
                pcm[i * 2] = (byte)value; pcm[i * 2 + 1] = (byte)(value >> 8);
            }
            writer.Write(pcm, 0, pcm.Length);
        }
        return stream.ToArray();
    }
}

public sealed class LiveAudioCapture : IDisposable
{
    private WasapiLoopbackCapture? capture;
    private MMDevice? device;
    private BufferedWaveProvider? buffer;
    private ISampleProvider? converted;
    private readonly AudioSegmenter segmenter;
    private readonly float[] readBuffer = new float[1600];
    private readonly System.Threading.Timer silenceTimer;
    private readonly object gate = new();
    private long lastData = Environment.TickCount64;
    private bool stopping;
    public Channel<AudioChunk> Chunks { get; } = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    public event Action<float>? Level;
    public event Action<string>? Failed;
    public static AudioDevice[] Devices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(x => { using (x) return new AudioDevice(x.ID, x.FriendlyName); }).ToArray();
    }
    public static string DefaultDeviceId()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.ID;
    }
    public LiveAudioCapture(int seconds)
    {
        segmenter = new AudioSegmenter(seconds);
        segmenter.Ready += (values, duration) => Chunks.Writer.TryWrite(new(AudioSegmenter.WaveBytes(values), DateTime.UtcNow, duration, System.Diagnostics.Stopwatch.GetTimestamp()));
        silenceTimer = new System.Threading.Timer(_ =>
        {
            lock (gate)
            {
                if (!stopping && capture != null && Environment.TickCount64 - lastData > 250)
                { segmenter.Push(new float[1600]); Level?.Invoke(0); }
            }
        }, null, 100, 100);
    }
    public void Start(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        device = enumerator.GetDevice(deviceId);
        capture = new WasapiLoopbackCapture(device);
        buffer = new BufferedWaveProvider(capture.WaveFormat) { ReadFully = false, BufferDuration = TimeSpan.FromSeconds(3), DiscardOnBufferOverflow = true };
        ISampleProvider source = buffer.ToSampleProvider();
        if (source.WaveFormat.Channels == 2) source = new StereoToMonoSampleProvider(source);
        else if (source.WaveFormat.Channels > 2) source = new DownmixSampleProvider(source);
        converted = new WdlResamplingSampleProvider(source, 16000);
        capture.DataAvailable += (_, e) =>
        {
            lock (gate)
            {
                if (stopping) return;
                lastData = Environment.TickCount64;
                buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                int count;
                while ((count = converted.Read(readBuffer, 0, readBuffer.Length)) > 0)
                {
                    float peak = 0; for (int i = 0; i < count; i++) peak = Math.Max(peak, Math.Abs(readBuffer[i]));
                    Level?.Invoke(peak); segmenter.Push(readBuffer.AsSpan(0, count));
                }
            }
        };
        capture.RecordingStopped += (_, e) => { if (!stopping) Failed?.Invoke(e.Exception == null ? "声音设备已停止，请重新选择设备。" : "无法读取声音设备，请重新选择设备。"); };
        capture.StartRecording();
    }
    public void Dispose()
    {
        lock (gate) stopping = true;
        silenceTimer.Dispose();
        capture?.StopRecording(); capture?.Dispose(); device?.Dispose(); Chunks.Writer.TryComplete();
    }
}

internal sealed class DownmixSampleProvider(ISampleProvider source) : ISampleProvider
{
    private float[] scratch = [];
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    public int Read(float[] buffer, int offset, int count)
    {
        int channels = source.WaveFormat.Channels;
        if (scratch.Length < count * channels) scratch = new float[count * channels];
        int frames = source.Read(scratch, 0, count * channels) / channels;
        for (int i = 0; i < frames; i++) { float sum = 0; for (int j = 0; j < channels; j++) sum += scratch[i * channels + j]; buffer[offset + i] = sum / channels; }
        return frames;
    }
}
