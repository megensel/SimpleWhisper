namespace SimpleWhisper.Models;

/// <summary>
/// A selectable transcription model for a given engine.
/// </summary>
/// <param name="Id">The model id sent to the provider API.</param>
/// <param name="DisplayName">Human-readable label shown in Settings.</param>
public sealed record TranscriptionModelOption(string Id, string DisplayName);

/// <summary>
/// Single source of truth for the models each cloud engine can use.
/// Used by the settings view model to populate model pickers.
/// </summary>
public static class TranscriptionModelCatalog
{
    private static readonly IReadOnlyList<TranscriptionModelOption> OpenAIModels =
    [
        new("gpt-4o-mini-transcribe", "gpt-4o-mini-transcribe (cheapest, recommended)"),
        new("gpt-4o-transcribe", "gpt-4o-transcribe (highest accuracy)"),
        new("whisper-1", "whisper-1 (legacy)")
    ];

    private static readonly IReadOnlyList<TranscriptionModelOption> GeminiModels =
    [
        new("gemini-3.5-transcribe", "gemini-3.5-transcribe")
    ];

    private static readonly IReadOnlyList<TranscriptionModelOption> GroqModels =
    [
        new("whisper-large-v3-turbo", "whisper-large-v3-turbo (fastest, cheapest)"),
        new("whisper-large-v3", "whisper-large-v3 (more accurate)")
    ];

    private static readonly IReadOnlyList<TranscriptionModelOption> DeepgramModels =
    [
        new("nova-3", "nova-3")
    ];

    /// <summary>
    /// Returns the selectable models for the given engine. Local returns an empty list.
    /// </summary>
    public static IReadOnlyList<TranscriptionModelOption> ModelsFor(TranscriptionEngine engine) => engine switch
    {
        TranscriptionEngine.Cloud => OpenAIModels,
        TranscriptionEngine.Gemini => GeminiModels,
        TranscriptionEngine.Groq => GroqModels,
        TranscriptionEngine.Deepgram => DeepgramModels,
        _ => []
    };
}
