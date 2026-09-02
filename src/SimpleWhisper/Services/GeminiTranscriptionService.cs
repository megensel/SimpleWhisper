using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleWhisper.Helpers;
using SimpleWhisper.Models;

namespace SimpleWhisper.Services;

/// <summary>
/// Transcription service for Google's Gemini API using the dedicated
/// <c>gemini-3.5-transcribe</c> model via the Interactions endpoint.
/// Audio is sent inline as base64 WAV; custom vocabulary and language hints
/// are passed through <c>transcription_config</c>.
/// </summary>
public sealed class GeminiTranscriptionService : ITranscriptionService
{
    private const string InteractionsUrl = "https://generativelanguage.googleapis.com/v1beta/interactions";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _configLock = new();
    private string _apiKey;
    private string _model;

    /// <summary>
    /// Initializes a new instance of the <see cref="GeminiTranscriptionService"/> class.
    /// </summary>
    /// <param name="apiKey">Gemini API key from AI Studio. May be empty initially.</param>
    /// <param name="model">Model id, normally "gemini-3.5-transcribe".</param>
    public GeminiTranscriptionService(string apiKey, string model)
    {
        _apiKey = apiKey ?? string.Empty;
        _model = string.IsNullOrWhiteSpace(model) ? "gemini-3.5-transcribe" : model;
    }

    /// <inheritdoc />
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);

    /// <inheritdoc />
    public string EngineName => $"Gemini {_model} (Cloud)";

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
            return TranscriptionResult.Failure("Gemini API key is not configured.");
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var vocabulary = RestTranscriptionHttp.SplitVocabularyPrompt(prompt);

            var body = new InteractionRequest
            {
                Model = model,
                Input =
                [
                    new AudioInput
                    {
                        Data = Convert.ToBase64String(audioData),
                        MimeType = "audio/wav"
                    }
                ],
                GenerationConfig = new GenerationConfig
                {
                    TranscriptionConfig = new TranscriptionConfig
                    {
                        LanguageCodes = string.IsNullOrWhiteSpace(language) ? null : [language],
                        CustomVocabulary = vocabulary.Count > 0 ? vocabulary : null,
                        Mode = "smart"
                    }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, InteractionsUrl);
            request.Headers.Add("x-goog-api-key", apiKey);
            request.Content = JsonContent.Create(body, options: JsonOptions);

            using var response = await RestTranscriptionHttp.Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                string detail = ExtractErrorMessage(json);
                AppLogger.Log($"Gemini API error: {(int)response.StatusCode} - {detail}");
                return TranscriptionResult.Failure(
                    $"Gemini API error (HTTP {(int)response.StatusCode}): {detail}",
                    stopwatch.Elapsed);
            }

            if (TryExtractFailure(json, out string failureReason))
            {
                AppLogger.Log($"Gemini interaction failed: {failureReason}");
                return TranscriptionResult.Failure($"Gemini API error: {failureReason}", stopwatch.Elapsed);
            }

            string text = ExtractTranscript(json);

            if (string.IsNullOrWhiteSpace(text))
            {
                return TranscriptionResult.Failure("Transcription returned empty text.", stopwatch.Elapsed);
            }

            return TranscriptionResult.Success(
                text: text.Trim(),
                language: language ?? string.Empty,
                duration: stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return TranscriptionResult.Failure("Transcription was cancelled.", stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            AppLogger.Log($"Gemini transcription timed out after {RestTranscriptionHttp.TimeoutSeconds} seconds.");
            return TranscriptionResult.Failure(
                $"Request timed out after {RestTranscriptionHttp.TimeoutSeconds} seconds.",
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            AppLogger.Log($"Network error during Gemini transcription: {ex.Message}");
            return TranscriptionResult.Failure($"Network error: {ex.Message}", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppLogger.Log($"Unexpected error during Gemini transcription: {ex}");
            return TranscriptionResult.Failure($"Unexpected error: {ex.Message}", stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Pulls the transcript out of an Interactions response. Prefers the convenience
    /// <c>output_text</c> field; falls back to concatenating text blocks of <c>model_output</c> steps.
    /// </summary>
    private static string ExtractTranscript(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString() ?? string.Empty;
        }

        if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            return string.Empty;

        // Prefer text blocks from model_output steps; fall back to any step with a content array
        // if no model_output steps yielded text (some responses use a different step type).
        var parts = CollectTextBlocks(steps, step =>
            step.TryGetProperty("type", out var stepType) && stepType.GetString() == "model_output");

        if (parts.Count == 0)
        {
            parts = CollectTextBlocks(steps, step => step.TryGetProperty("content", out _));
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Collects <c>text</c> blocks from steps that satisfy <paramref name="stepPredicate"/> and
    /// have a <c>content</c> array.
    /// </summary>
    private static List<string> CollectTextBlocks(JsonElement steps, Func<JsonElement, bool> stepPredicate)
    {
        var parts = new List<string>();
        foreach (var step in steps.EnumerateArray())
        {
            if (!stepPredicate(step))
                continue;

            if (!step.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var blockType) && blockType.GetString() == "text" &&
                    block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    parts.Add(text.GetString() ?? string.Empty);
                }
            }
        }

        return parts;
    }

    /// <summary>
    /// Checks whether the interaction reported <c>status: "failed"</c> and, if so, extracts a
    /// human-readable reason from <c>error.message</c>, <c>error</c>, or <c>status_details</c>/
    /// <c>incomplete_details</c>, falling back to a generic message.
    /// </summary>
    private static bool TryExtractFailure(string json, out string reason)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String ||
            status.GetString() != "failed")
        {
            reason = string.Empty;
            return false;
        }

        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(message.GetString()))
            {
                reason = message.GetString()!;
                return true;
            }

            if (error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()))
            {
                reason = error.GetString()!;
                return true;
            }
        }

        foreach (string property in new[] { "status_details", "incomplete_details" })
        {
            if (root.TryGetProperty(property, out var details))
            {
                if (details.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(details.GetString()))
                {
                    reason = details.GetString()!;
                    return true;
                }

                if (details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("reason", out var detailsReason) &&
                    detailsReason.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(detailsReason.GetString()))
                {
                    reason = detailsReason.GetString()!;
                    return true;
                }
            }
        }

        reason = "Gemini reported the interaction failed.";
        return true;
    }

    /// <summary>
    /// Extracts <c>error.message</c> from a Gemini error body, or returns the raw body (truncated).
    /// </summary>
    private static string ExtractErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Fall through to raw body.
        }

        return json.Length > 300 ? json[..300] : json;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Request DTOs (snake_case per the Interactions API)
    // ──────────────────────────────────────────────────────────────────

    private sealed class InteractionRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("input")] public List<AudioInput> Input { get; set; } = [];
        [JsonPropertyName("generation_config")] public GenerationConfig? GenerationConfig { get; set; }
    }

    private sealed class AudioInput
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "audio";
        [JsonPropertyName("data")] public string Data { get; set; } = string.Empty;
        [JsonPropertyName("mime_type")] public string MimeType { get; set; } = "audio/wav";
    }

    private sealed class GenerationConfig
    {
        [JsonPropertyName("transcription_config")] public TranscriptionConfig? TranscriptionConfig { get; set; }
    }

    private sealed class TranscriptionConfig
    {
        [JsonPropertyName("language_codes")] public IReadOnlyList<string>? LanguageCodes { get; set; }
        [JsonPropertyName("custom_vocabulary")] public IReadOnlyList<string>? CustomVocabulary { get; set; }
        [JsonPropertyName("mode")] public string? Mode { get; set; }
    }
}
