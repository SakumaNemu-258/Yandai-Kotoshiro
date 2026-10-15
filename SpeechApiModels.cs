namespace Kotoshiro;

/// <summary>Lists account-visible, documented Japanese transcription models using metadata only.</summary>
public static class SpeechApiModels
{
    // /models does not standardize languages or inference endpoints. Never infer Japanese
    // support from names such as "asr", "audio", "transcribe", or from a provider display label.
    // These exact IDs need a documentation review when additions or new snapshots appear.
    // Whisper's Japanese support: https://developers.openai.com/api/docs/guides/text-to-speech#supported-languages
    // Mini snapshot + alias: https://developers.openai.com/blog/updates-audio-models
    // /audio/transcriptions: https://developers.openai.com/api/docs/guides/speech-to-text
    private static readonly HashSet<string> KnownJapaneseTranscriptions = new(StringComparer.Ordinal)
    {
        "whisper-1", "gpt-4o-mini-transcribe", "gpt-4o-mini-transcribe-2025-12-15"
    };

    // SiliconFlow's filtered metadata supplies the speech-to-text endpoint classification.
    // Japanese language coverage is independently documented in the model authors' cards:
    // https://github.com/QwenLM/Qwen3-ASR#released-models-description-and-download
    // https://github.com/QwenAudio/SenseVoice
    // SenseVoice endpoint: https://api-docs.siliconflow.cn/docs/api/audio-transcriptions-post
    // Do not accept arbitrary renamed/fine-tuned variants or the forced aligner as ASR.
    private static readonly HashSet<string> KnownJapaneseSiliconFlowSpeech = new(StringComparer.Ordinal)
    {
        "Qwen/Qwen3-ASR-1.7B", "Qwen/Qwen3-ASR-0.6B", "FunAudioLLM/SenseVoiceSmall"
    };

    public static IReadOnlyList<ApiModelInfo> Defaults() => KnownJapaneseTranscriptions
        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).Select(id => new ApiModelInfo(id, id)).ToArray();

    public static Uri Endpoint(string address)
    {
        Uri inference = SpeechApiRecognition.Endpoint(address);
        const string suffix = "/audio/transcriptions";
        string list = inference.AbsoluteUri[..^suffix.Length] + "/models";
        // Official filters: https://api-docs.siliconflow.cn/docs/api/models-get
        if (IsSiliconFlow(inference)) list += "?type=audio&sub_type=speech-to-text";
        return new Uri(list);
    }

    public static async Task<IReadOnlyList<ApiModelInfo>> FetchAsync(AppSettings settings, CancellationToken token, HttpMessageHandler? handler = null)
    {
        token.ThrowIfCancellationRequested();
        // Intentionally do not normalize translation settings or use ApiBase/GetKey.
        Uri endpoint = Endpoint(settings.SpeechApiBase);
        string key = settings.GetSpeechKey().Trim();
        if (key.Length == 0) throw new InvalidOperationException("请先填写识别 API 密钥，再获取识别模型。");
        if (key.Any(char.IsControl)) throw new ArgumentException("识别 API 密钥包含无效字符，请重新填写。");
        IReadOnlyList<ApiModelInfo> available;
        try { available = await ApiModels.FetchFromEndpointAsync(endpoint, key, false, token, handler); }
        catch (HttpRequestException ex) when (ex.StatusCode == null)
        {
            // Omit raw network exception messages/inner exceptions; a custom service may echo secrets.
            throw new HttpRequestException("无法连接识别 API，请检查网络和识别 API 地址。", null);
        }
        bool siliconFlow = IsSiliconFlow(endpoint);
        var supported = available.Where(model => siliconFlow
            ? KnownJapaneseSiliconFlowSpeech.Contains(model.Id)
            : KnownJapaneseTranscriptions.Contains(model.Id)).ToArray();
        if (supported.Length == 0)
            throw new InvalidDataException("未找到已确认支持日语且兼容当前转写接口的模型。模型列表通常不提供语言能力信息；新模型或自定义别名可继续手动填写并用日语录音测试。");
        return supported;
    }

    // Match the host whose multipart schema is also handled by SpeechApiRecognition.
    private static bool IsSiliconFlow(Uri endpoint) => endpoint.Host.Equals("api.siliconflow.cn", StringComparison.OrdinalIgnoreCase);
}
