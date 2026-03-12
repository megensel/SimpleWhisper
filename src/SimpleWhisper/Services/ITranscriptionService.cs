using SimpleWhisper.Models;

namespace SimpleWhisper.Services;

/// <summary>
/// Defines a contract for speech-to-text transcription services. Implementations may use
/// cloud APIs (e.g., OpenAI Whisper) or local inference engines (e.g., Whisper.net).
/// </summary>
public interface ITranscriptionService
{
    /// <summary>
    /// Transcribes the provided audio data to text asynchronously.
    /// </summary>
    /// <param name="audioData">
    /// A byte array containing a complete WAV file (including headers).
    /// Expected format: 16 kHz, 16-bit, mono PCM.
    /// </param>
    /// <param name="language">
    /// An optional ISO 639-1 language code (e.g., "en", "es", "ja") to hint the transcription engine.
    /// When <c>null</c> or empty, the engine attempts automatic language detection.
    /// </param>
    /// <param name="prompt">
    /// An optional prompt string providing context or custom vocabulary to improve transcription accuracy.
    /// Useful for domain-specific terminology, proper nouns, or abbreviations.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the transcription operation.</param>
    /// <returns>
    /// A <see cref="TranscriptionResult"/> containing the transcribed text and metadata,
    /// or a failure result if the operation did not succeed.
    /// </returns>
    Task<TranscriptionResult> TranscribeAsync(
        byte[] audioData,
        string? language = null,
        string? prompt = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a value indicating whether the transcription service is configured and ready to accept requests.
    /// For cloud services, this typically means an API key is present.
    /// For local services, this means a model is loaded or available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets a human-readable name identifying this transcription engine, suitable for display in the UI
    /// and status messages (e.g., "OpenAI Whisper (Cloud)", "Whisper.net (Local)").
    /// </summary>
    string EngineName { get; }
}
