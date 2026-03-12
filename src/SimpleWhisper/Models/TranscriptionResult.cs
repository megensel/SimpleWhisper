namespace SimpleWhisper.Models;

/// <summary>
/// Represents the result of a single audio transcription operation,
/// whether performed via the cloud (OpenAI Whisper API) or a local Whisper model.
/// </summary>
public class TranscriptionResult
{
    /// <summary>
    /// The transcribed text. Empty string on failure.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// The detected or specified language as an ISO 639-1 code (e.g. "en", "es", "ja").
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// The duration of the audio that was transcribed.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// A confidence score between 0.0 and 1.0 indicating transcription quality.
    /// A value of 0.0 typically indicates that confidence was not reported.
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Whether the transcription completed successfully.
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// A human-readable error message when <see cref="IsSuccess"/> is <c>false</c>.
    /// Null when the transcription succeeded.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Creates a successful transcription result.
    /// </summary>
    /// <param name="text">The transcribed text.</param>
    /// <param name="language">The detected language code.</param>
    /// <param name="duration">The audio duration.</param>
    /// <param name="confidence">The confidence score (0.0 to 1.0).</param>
    /// <returns>A new <see cref="TranscriptionResult"/> marked as successful.</returns>
    public static TranscriptionResult Success(string text, string language, TimeSpan duration, double confidence = 1.0) =>
        new()
        {
            Text = text,
            Language = language,
            Duration = duration,
            Confidence = confidence,
            IsSuccess = true
        };

    /// <summary>
    /// Creates a failed transcription result.
    /// </summary>
    /// <param name="errorMessage">The error message describing what went wrong.</param>
    /// <param name="duration">The audio duration that was attempted.</param>
    /// <returns>A new <see cref="TranscriptionResult"/> marked as failed.</returns>
    public static TranscriptionResult Failure(string errorMessage, TimeSpan duration = default) =>
        new()
        {
            IsSuccess = false,
            ErrorMessage = errorMessage,
            Duration = duration
        };
}
