using System.Net.Http;

namespace SimpleWhisper.Services;

/// <summary>
/// Shared plumbing for the hand-written REST transcription services (Gemini, Deepgram).
/// </summary>
internal static class RestTranscriptionHttp
{
    /// <summary>
    /// A single long-lived <see cref="HttpClient"/> shared by REST transcription services.
    /// The 60-second timeout is a safety net; callers also pass a cancellation token.
    /// </summary>
    public static HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Splits the comma-separated vocabulary prompt built by <c>TranscriptionManager</c>
    /// back into individual terms for providers that take a term array instead of free text.
    /// </summary>
    /// <param name="prompt">The prompt, e.g. "Kubernetes, SimpleWhisper". May be null.</param>
    /// <returns>Trimmed, non-empty terms in original order. Empty when the prompt is blank.</returns>
    public static IReadOnlyList<string> SplitVocabularyPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return [];

        return prompt
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .ToList();
    }
}
