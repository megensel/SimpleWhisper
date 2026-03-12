using System.ClientModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using OpenAI;
using OpenAI.Audio;
using SimpleWhisper.Models;

namespace SimpleWhisper.Services;

/// <summary>
/// Transcription service that uses the OpenAI Whisper cloud API via the official OpenAI NuGet package.
/// Sends recorded WAV audio to the <c>whisper-1</c> model and returns the transcribed text.
/// </summary>
public sealed class CloudTranscriptionService : ITranscriptionService
{
    private readonly object _clientLock = new();
    private string _apiKey;
    private OpenAIClient? _openAIClient;
    private AudioClient? _audioClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="CloudTranscriptionService"/> class
    /// with the specified OpenAI API key.
    /// </summary>
    /// <param name="apiKey">
    /// The OpenAI API key used to authenticate requests to the Whisper API.
    /// Can be empty initially and updated later via <see cref="UpdateApiKey"/>.
    /// </param>
    public CloudTranscriptionService(string apiKey)
    {
        _apiKey = apiKey ?? string.Empty;
        RebuildClients();
    }

    /// <inheritdoc />
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);

    /// <inheritdoc />
    public string EngineName => "OpenAI Whisper (Cloud)";

    /// <summary>
    /// Updates the API key at runtime and rebuilds the underlying OpenAI clients.
    /// This is useful when the user changes the key in application settings without restarting.
    /// </summary>
    /// <param name="key">The new OpenAI API key.</param>
    public void UpdateApiKey(string key)
    {
        lock (_clientLock)
        {
            _apiKey = key ?? string.Empty;
            RebuildClients();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sends the WAV audio data to the OpenAI Whisper API (<c>whisper-1</c> model).
    /// The response includes the transcribed text. Elapsed wall-clock time is measured
    /// and reported in the result's <see cref="TranscriptionResult.Duration"/> field.
    /// </remarks>
    public async Task<TranscriptionResult> TranscribeAsync(
        byte[] audioData,
        string? language = null,
        string? prompt = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return TranscriptionResult.Failure("OpenAI API key is not configured.");
        }

        AudioClient? client;
        lock (_clientLock)
        {
            client = _audioClient;
        }

        if (client is null)
        {
            return TranscriptionResult.Failure("OpenAI client could not be initialized.");
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
            Debug.WriteLine($"OpenAI API error: {ex.Status} - {ex.Message}");
            return TranscriptionResult.Failure(
                $"OpenAI API error (HTTP {ex.Status}): {ex.Message}",
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            Debug.WriteLine($"Network error during transcription: {ex.Message}");
            return TranscriptionResult.Failure(
                $"Network error: {ex.Message}",
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Debug.WriteLine($"Unexpected error during transcription: {ex}");
            return TranscriptionResult.Failure(
                $"Unexpected error: {ex.Message}",
                stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Creates or recreates the OpenAI client instances using the current API key.
    /// Called on construction and whenever the API key is updated.
    /// </summary>
    private void RebuildClients()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _openAIClient = null;
            _audioClient = null;
            return;
        }

        _openAIClient = new OpenAIClient(_apiKey);
        _audioClient = _openAIClient.GetAudioClient("whisper-1");
    }
}
