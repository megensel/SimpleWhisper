using System.ClientModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using OpenAI;
using OpenAI.Audio;
using SimpleWhisper.Helpers;
using SimpleWhisper.Models;

namespace SimpleWhisper.Services;

/// <summary>
/// Transcription service for any provider that exposes the OpenAI <c>/audio/transcriptions</c>
/// contract, using the official OpenAI NuGet package. Serves OpenAI itself (default endpoint)
/// and Groq (custom endpoint). The model id is configurable at runtime.
/// </summary>
public sealed class OpenAICompatibleTranscriptionService : ITranscriptionService
{
    /// <summary>Base endpoint for Groq's OpenAI-compatible API.</summary>
    public static readonly Uri GroqEndpoint = new("https://api.groq.com/openai/v1");

    private readonly object _clientLock = new();
    private readonly Uri? _endpoint;
    private readonly string _providerDisplayName;
    private string _apiKey;
    private string _model;
    private AudioClient? _audioClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAICompatibleTranscriptionService"/> class.
    /// </summary>
    /// <param name="apiKey">Provider API key. May be empty and set later via <see cref="UpdateConfiguration"/>.</param>
    /// <param name="model">Model id to request (e.g. "gpt-4o-mini-transcribe", "whisper-large-v3-turbo").</param>
    /// <param name="endpoint">Custom base endpoint, or <c>null</c> for api.openai.com.</param>
    /// <param name="providerDisplayName">Provider name used in <see cref="EngineName"/> (e.g. "OpenAI", "Groq").</param>
    public OpenAICompatibleTranscriptionService(string apiKey, string model, Uri? endpoint, string providerDisplayName)
    {
        _apiKey = apiKey ?? string.Empty;
        _model = string.IsNullOrWhiteSpace(model) ? "whisper-1" : model;
        _endpoint = endpoint;
        _providerDisplayName = providerDisplayName;
        RebuildClients();
    }

    /// <inheritdoc />
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);

    /// <inheritdoc />
    public string EngineName => $"{_providerDisplayName} {_model} (Cloud)";

    /// <summary>
    /// Updates the API key and/or model at runtime and rebuilds the client when either changed.
    /// </summary>
    /// <param name="apiKey">The new API key.</param>
    /// <param name="model">The new model id.</param>
    public void UpdateConfiguration(string apiKey, string model)
    {
        lock (_clientLock)
        {
            string newKey = apiKey ?? string.Empty;
            string newModel = string.IsNullOrWhiteSpace(model) ? _model : model;

            if (newKey == _apiKey && newModel == _model)
                return;

            _apiKey = newKey;
            _model = newModel;
            RebuildClients();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sends the WAV audio to the provider's <c>/audio/transcriptions</c> endpoint with the configured model.
    /// Elapsed wall-clock time is reported in <see cref="TranscriptionResult.Duration"/>.
    /// </remarks>
    public async Task<TranscriptionResult> TranscribeAsync(
        byte[] audioData,
        string? language = null,
        string? prompt = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return TranscriptionResult.Failure($"{_providerDisplayName} API key is not configured.");
        }

        AudioClient? client;
        lock (_clientLock)
        {
            client = _audioClient;
        }

        if (client is null)
        {
            return TranscriptionResult.Failure($"{_providerDisplayName} client could not be initialized.");
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var options = new AudioTranscriptionOptions();

            if (!string.IsNullOrWhiteSpace(language))
            {
                options.Language = language;
            }

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                options.Prompt = prompt;
            }

            using var audioStream = new MemoryStream(audioData);

            var result = await client.TranscribeAudioAsync(
                audioStream,
                "audio.wav",
                options,
                cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            var transcription = result.Value;
            string text = transcription.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                return TranscriptionResult.Failure(
                    "Transcription returned empty text.",
                    stopwatch.Elapsed);
            }

            return TranscriptionResult.Success(
                text: text,
                language: transcription.Language ?? language ?? string.Empty,
                duration: stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return TranscriptionResult.Failure("Transcription was cancelled.", stopwatch.Elapsed);
        }
        catch (ClientResultException ex)
        {
            stopwatch.Stop();
            AppLogger.Log($"{_providerDisplayName} API error: {ex.Status} - {ex.Message}");
            return TranscriptionResult.Failure(
                $"{_providerDisplayName} API error (HTTP {ex.Status}): {ex.Message}",
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            AppLogger.Log($"Network error during transcription: {ex.Message}");
            return TranscriptionResult.Failure(
                $"Network error: {ex.Message}",
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppLogger.Log($"Unexpected error during transcription: {ex}");
            return TranscriptionResult.Failure(
                $"Unexpected error: {ex.Message}",
                stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Creates or recreates the audio client using the current key, model, and endpoint.
    /// </summary>
    private void RebuildClients()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _audioClient = null;
            return;
        }

        var options = new OpenAIClientOptions();
        if (_endpoint is not null)
        {
            options.Endpoint = _endpoint;
        }

        var openAIClient = new OpenAIClient(new ApiKeyCredential(_apiKey), options);
        _audioClient = openAIClient.GetAudioClient(_model);
    }
}
