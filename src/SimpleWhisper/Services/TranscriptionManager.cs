using SimpleWhisper.Models;

namespace SimpleWhisper.Services;

/// <summary>
/// Orchestrates transcription requests by routing audio data to the appropriate transcription
/// engine (cloud or local) based on the current application settings. Lazily initializes
/// service instances and keeps them in sync with settings changes.
/// </summary>
public sealed class TranscriptionManager
{
    private readonly SettingsService _settingsService;
    private readonly object _serviceLock = new();

    private CloudTranscriptionService? _cloudService;
    private LocalTranscriptionService? _localService;

    /// <summary>
    /// Fired when the manager's status changes, providing human-readable messages
    /// suitable for display in the UI (e.g., "Transcribing with OpenAI Whisper...").
    /// </summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscriptionManager"/> class.
    /// </summary>
    /// <param name="settingsService">
    /// The settings service used to read the current engine selection, API key,
    /// language preference, and custom vocabulary.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="settingsService"/> is <c>null</c>.
    /// </exception>
    public TranscriptionManager(SettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    /// <summary>
    /// Transcribes the provided audio data using the engine specified in the current application settings.
    /// The language, custom vocabulary (as a prompt), and engine selection are all read from settings
    /// at the time of each call.
    /// </summary>
    /// <param name="audioData">
    /// A byte array containing a complete WAV file (including headers).
    /// Expected format: 16 kHz, 16-bit, mono PCM.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the transcription operation.</param>
    /// <returns>
    /// A <see cref="TranscriptionResult"/> containing the transcribed text and metadata,
    /// or a failure result if the operation did not succeed.
    /// </returns>
    public async Task<TranscriptionResult> TranscribeAsync(
        byte[] audioData,
        CancellationToken cancellationToken = default)
    {
        var settings = _settingsService.Settings;
        var engine = settings.Engine;

        // Determine the language hint (null means auto-detect).
        string? language = settings.AutoDetectLanguage ? null : settings.Language;

        // Build a prompt from custom vocabulary for improved recognition accuracy.
        string? prompt = BuildVocabularyPrompt(settings.CustomVocabulary);

        ITranscriptionService service = engine switch
        {
            TranscriptionEngine.Cloud => GetOrCreateCloudService(settings.OpenAIApiKey),
            TranscriptionEngine.Local => GetOrCreateLocalService(),
            _ => throw new InvalidOperationException($"Unknown transcription engine: {engine}")
        };

        if (!service.IsAvailable)
        {
            string reason = engine == TranscriptionEngine.Cloud
                ? "OpenAI API key is not configured. Please add your key in Settings."
                : "Local transcription engine is not available.";

            OnStatusChanged(reason);
            return TranscriptionResult.Failure(reason);
        }

        OnStatusChanged($"Transcribing with {service.EngineName}...");

        var result = await service.TranscribeAsync(audioData, language, prompt, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            OnStatusChanged($"Transcription complete ({result.Duration.TotalSeconds:F1}s)");
        }
        else
        {
            OnStatusChanged($"Transcription failed: {result.ErrorMessage}");
        }

        return result;
    }

    /// <summary>
    /// Gets the current cloud transcription service, or creates one lazily.
    /// If the API key has changed since the last call, the existing service's key is updated.
    /// </summary>
    private CloudTranscriptionService GetOrCreateCloudService(string apiKey)
    {
        lock (_serviceLock)
        {
            if (_cloudService is null)
            {
                _cloudService = new CloudTranscriptionService(apiKey);
            }
            else
            {
                // Keep the service's API key in sync with current settings.
                _cloudService.UpdateApiKey(apiKey);
            }

            return _cloudService;
        }
    }

    /// <summary>
    /// Gets the current local transcription service stub, or creates one lazily.
    /// The local service is a placeholder until full Whisper.net integration is implemented.
    /// </summary>
    private LocalTranscriptionService GetOrCreateLocalService()
    {
        lock (_serviceLock)
        {
            _localService ??= new LocalTranscriptionService();
            return _localService;
        }
    }

    /// <summary>
    /// Builds a prompt string from the user's custom vocabulary list.
    /// The Whisper API uses the prompt to bias recognition toward these terms.
    /// </summary>
    /// <param name="vocabulary">The list of custom vocabulary words or phrases.</param>
    /// <returns>
    /// A comma-separated string of vocabulary terms, or <c>null</c> if the list is empty.
    /// </returns>
    private static string? BuildVocabularyPrompt(List<string> vocabulary)
    {
        if (vocabulary is null || vocabulary.Count == 0)
            return null;

        var terms = vocabulary
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        return terms.Count > 0
            ? string.Join(", ", terms)
            : null;
    }

    /// <summary>
    /// Raises the <see cref="StatusChanged"/> event with the specified message.
    /// </summary>
    /// <param name="message">The status message to broadcast.</param>
    private void OnStatusChanged(string message)
    {
        StatusChanged?.Invoke(message);
    }
}
