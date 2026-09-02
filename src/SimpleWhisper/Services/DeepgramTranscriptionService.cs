using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SimpleWhisper.Helpers;
using SimpleWhisper.Models;

namespace SimpleWhisper.Services;

/// <summary>
/// Transcription service for Deepgram's pre-recorded <c>/v1/listen</c> endpoint (Nova-3).
/// Sends raw WAV bytes; custom vocabulary is passed as repeated <c>keyterm</c> query parameters.
/// </summary>
public sealed class DeepgramTranscriptionService : ITranscriptionService
{
    private const string ListenUrl = "https://api.deepgram.com/v1/listen";

    /// <summary>Deepgram caps key terms at 500 tokens per request; 50 terms is a safe practical limit.</summary>
    private const int MaxKeyTerms = 50;

    private readonly object _configLock = new();
    private string _apiKey;
    private string _model;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeepgramTranscriptionService"/> class.
    /// </summary>
    /// <param name="apiKey">Deepgram API key. May be empty initially.</param>
    /// <param name="model">Model id, normally "nova-3".</param>
    public DeepgramTranscriptionService(string apiKey, string model)
    {
        _apiKey = apiKey ?? string.Empty;
        _model = string.IsNullOrWhiteSpace(model) ? "nova-3" : model;
    }

    /// <inheritdoc />
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);

    /// <inheritdoc />
    public string EngineName => $"Deepgram {_model} (Cloud)";

    /// <summary>
    /// Updates the API key and model at runtime.
    /// </summary>
    public void UpdateConfiguration(string apiKey, string model)
    {
        lock (_configLock)
        {
            _apiKey = apiKey ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(model))
                _model = model;
        }
    }

    /// <inheritdoc />
    public async Task<TranscriptionResult> TranscribeAsync(
        byte[] audioData,
        string? language = null,
        string? prompt = null,
        CancellationToken cancellationToken = default)
    {
        string apiKey;
        string model;
        lock (_configLock)
        {
            apiKey = _apiKey;
            model = _model;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return TranscriptionResult.Failure("Deepgram API key is not configured.");
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            string url = BuildUrl(model, language, RestTranscriptionHttp.SplitVocabularyPrompt(prompt));

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", apiKey);
            request.Content = new ByteArrayContent(audioData);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

            using var response = await RestTranscriptionHttp.Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                string detail = ExtractErrorMessage(json);
                AppLogger.Log($"Deepgram API error: {(int)response.StatusCode} - {detail}");
                return TranscriptionResult.Failure(
                    $"Deepgram API error (HTTP {(int)response.StatusCode}): {detail}",
                    stopwatch.Elapsed);
            }

            var (text, confidence, detectedLanguage) = ExtractTranscript(json);

            if (string.IsNullOrWhiteSpace(text))
            {
                return TranscriptionResult.Failure("Transcription returned empty text.", stopwatch.Elapsed);
            }

            return TranscriptionResult.Success(
                text: text.Trim(),
                language: detectedLanguage ?? language ?? string.Empty,
                duration: stopwatch.Elapsed,
                confidence: confidence);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return TranscriptionResult.Failure("Transcription was cancelled.", stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            AppLogger.Log($"Network error during Deepgram transcription: {ex.Message}");
            return TranscriptionResult.Failure($"Network error: {ex.Message}", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppLogger.Log($"Unexpected error during Deepgram transcription: {ex}");
            return TranscriptionResult.Failure($"Unexpected error: {ex.Message}", stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Builds the listen URL with model, smart formatting, language (or detection), and key terms.
    /// </summary>
    private static string BuildUrl(string model, string? language, IReadOnlyList<string> keyTerms)
    {
        var sb = new StringBuilder(ListenUrl);
        sb.Append("?model=").Append(Uri.EscapeDataString(model));
        sb.Append("&smart_format=true");

        if (string.IsNullOrWhiteSpace(language))
        {
            sb.Append("&detect_language=true");
        }
        else
        {
            sb.Append("&language=").Append(Uri.EscapeDataString(language));
        }

        foreach (var term in keyTerms.Take(MaxKeyTerms))
        {
            sb.Append("&keyterm=").Append(Uri.EscapeDataString(term));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads transcript, confidence, and detected language from the first channel's best alternative.
    /// </summary>
    private static (string Text, double Confidence, string? DetectedLanguage) ExtractTranscript(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            !results.TryGetProperty("channels", out var channels) ||
            channels.ValueKind != JsonValueKind.Array ||
            channels.GetArrayLength() == 0)
        {
            return (string.Empty, 0.0, null);
        }

        var channel = channels[0];
        string? detected = channel.TryGetProperty("detected_language", out var dl) && dl.ValueKind == JsonValueKind.String
            ? dl.GetString()
            : null;

        if (!channel.TryGetProperty("alternatives", out var alternatives) ||
            alternatives.ValueKind != JsonValueKind.Array ||
            alternatives.GetArrayLength() == 0)
        {
            return (string.Empty, 0.0, detected);
        }

        var best = alternatives[0];
        string text = best.TryGetProperty("transcript", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? string.Empty
            : string.Empty;
        double confidence = best.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetDouble()
            : 0.0;

        return (text, confidence, detected);
    }

    /// <summary>
    /// Extracts <c>err_msg</c> from a Deepgram error body, or returns the raw body (truncated).
    /// </summary>
    private static string ExtractErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("err_msg", out var msg) && msg.ValueKind == JsonValueKind.String)
                return msg.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            // Fall through to raw body.
        }

        return json.Length > 300 ? json[..300] : json;
    }
}
