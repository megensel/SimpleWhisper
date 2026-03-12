using System.Text.Json.Serialization;

namespace SimpleWhisper.Models;

/// <summary>
/// Controls whether the user must hold the trigger to record or toggle it on/off.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecordingMode
{
    /// <summary>Recording starts when the trigger is pressed and stops when released.</summary>
    HoldToRecord,

    /// <summary>First press starts recording, second press stops it.</summary>
    Toggle
}

/// <summary>
/// Selects which speech-to-text engine processes the audio.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranscriptionEngine
{
    /// <summary>OpenAI Whisper cloud API (requires API key and internet connection).</summary>
    Cloud,

    /// <summary>Local Whisper.net model (runs entirely on the local machine).</summary>
    Local
}

/// <summary>
/// Controls which hardware acceleration runtime is used for local Whisper inference.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GpuAcceleration
{
    /// <summary>Auto-detect: probes CUDA → Vulkan → OpenVINO → CPU in order.</summary>
    Auto,

    /// <summary>Force NVIDIA CUDA runtime (falls back to CPU if unavailable).</summary>
    Cuda,

    /// <summary>Force Vulkan runtime (falls back to CPU if unavailable).</summary>
    Vulkan,

    /// <summary>Skip all GPU runtimes and use CPU only.</summary>
    CpuOnly
}

/// <summary>
/// Determines where the recording/transcription overlay is positioned on screen.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverlayPosition
{
    /// <summary>Centered at the top of the screen.</summary>
    TopCenter,

    /// <summary>Top-left corner of the screen.</summary>
    TopLeft,

    /// <summary>Top-right corner of the screen.</summary>
    TopRight,

    /// <summary>Centered at the bottom of the screen.</summary>
    BottomCenter
}

/// <summary>
/// Application-wide settings POCO. Serialized to/from JSON for persistence.
/// All properties have sensible defaults so the app works out of the box.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// The keyboard key or mouse button that starts/stops recording.
    /// </summary>
    [JsonPropertyName("trigger")]
    public InputTrigger Trigger { get; set; } = new();

    /// <summary>
    /// Whether the user must hold the trigger (push-to-talk) or toggle it.
    /// </summary>
    [JsonPropertyName("recordingMode")]
    public RecordingMode RecordingMode { get; set; } = RecordingMode.HoldToRecord;

    /// <summary>
    /// ISO 639-1 language code used as a hint for the transcription engine (e.g. "en", "es", "ja").
    /// Ignored when <see cref="AutoDetectLanguage"/> is <c>true</c>.
    /// </summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    /// <summary>
    /// When <c>true</c>, the transcription engine will attempt to detect the spoken language automatically.
    /// </summary>
    [JsonPropertyName("autoDetectLanguage")]
    public bool AutoDetectLanguage { get; set; }

    /// <summary>
    /// Selects cloud (OpenAI API) or local (Whisper.net) transcription.
    /// </summary>
    [JsonPropertyName("engine")]
    public TranscriptionEngine Engine { get; set; } = TranscriptionEngine.Cloud;

    /// <summary>
    /// OpenAI API key for cloud transcription. Required when <see cref="Engine"/> is <see cref="TranscriptionEngine.Cloud"/>.
    /// </summary>
    [JsonPropertyName("openAIApiKey")]
    public string OpenAIApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Whisper model size for local transcription: tiny, base, small, medium, or large.
    /// Larger models are slower but more accurate.
    /// </summary>
    [JsonPropertyName("localModelSize")]
    public string LocalModelSize { get; set; } = "base";

    /// <summary>
    /// Custom file path to a local Whisper GGML model. When empty, the model is
    /// downloaded and cached automatically based on <see cref="LocalModelSize"/>.
    /// </summary>
    [JsonPropertyName("localModelPath")]
    public string LocalModelPath { get; set; } = string.Empty;

    /// <summary>
    /// Which hardware acceleration runtime to use for local Whisper inference.
    /// <see cref="GpuAcceleration.Auto"/> lets Whisper.net probe available runtimes automatically.
    /// </summary>
    [JsonPropertyName("gpuAcceleration")]
    public GpuAcceleration GpuAcceleration { get; set; } = GpuAcceleration.Auto;

    /// <summary>
    /// When <c>true</c>, leading and trailing silence is trimmed from recorded audio
    /// before transcription to reduce inference time. Enabled by default.
    /// </summary>
    [JsonPropertyName("trimSilence")]
    public bool TrimSilence { get; set; } = true;

    /// <summary>
    /// When <c>true</c> and the local engine is selected, audio is transcribed in chunks
    /// during recording, providing real-time visual feedback in the overlay. The chunked
    /// results are concatenated as the final output, reducing perceived latency.
    /// Has no effect when the cloud engine is selected.
    /// </summary>
    [JsonPropertyName("realtimeTranscription")]
    public bool RealtimeTranscription { get; set; } = true;

    /// <summary>
    /// Zero-based index of the recording device from the system microphone list.
    /// </summary>
    [JsonPropertyName("microphoneDeviceIndex")]
    public int MicrophoneDeviceIndex { get; set; }

    /// <summary>
    /// Register the application to start automatically when Windows starts.
    /// </summary>
    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Play an audible beep/chime when recording starts and stops.
    /// </summary>
    [JsonPropertyName("playSoundFeedback")]
    public bool PlaySoundFeedback { get; set; } = true;

    /// <summary>
    /// Show a floating overlay indicating recording state and transcription progress.
    /// </summary>
    [JsonPropertyName("showOverlay")]
    public bool ShowOverlay { get; set; } = true;

    /// <summary>
    /// Screen position for the recording overlay window.
    /// </summary>
    [JsonPropertyName("overlayPosition")]
    public OverlayPosition OverlayPosition { get; set; } = OverlayPosition.TopCenter;

    /// <summary>
    /// Words or phrases that are frequently used and may be uncommon. Provided as a hint
    /// to the transcription engine to improve recognition accuracy.
    /// </summary>
    [JsonPropertyName("customVocabulary")]
    public List<string> CustomVocabulary { get; set; } = [];

    /// <summary>
    /// Ordered list of find-and-replace rules applied to transcription output before text insertion.
    /// </summary>
    [JsonPropertyName("textReplacements")]
    public List<TextReplacementRule> TextReplacements { get; set; } = [];

    /// <summary>
    /// When <c>true</c>, spoken punctuation words (e.g. "period", "comma", "question mark")
    /// are converted to their corresponding punctuation characters.
    /// </summary>
    [JsonPropertyName("spokenPunctuationEnabled")]
    public bool SpokenPunctuationEnabled { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, spoken emoji names (e.g. "smiley face", "thumbs up") are
    /// converted to their Unicode emoji characters.
    /// </summary>
    [JsonPropertyName("emojiInsertionEnabled")]
    public bool EmojiInsertionEnabled { get; set; } = true;
}
