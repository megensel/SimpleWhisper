# Multi-Provider Cloud Transcription Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user pick OpenAI (any current model), Google Gemini 3.5 Transcribe, Groq, or Deepgram Nova-3 as the cloud transcription engine, each with its own stored API key.

**Architecture:** `ITranscriptionService` stays as-is. The existing OpenAI service is generalized to any OpenAI-compatible endpoint (serving OpenAI and Groq), and two small `HttpClient` REST services are added for Gemini and Deepgram. `TranscriptionManager` routes on an extended `TranscriptionEngine` enum. Settings gain per-provider keys and model ids; the Settings window swaps two radio buttons for an engine `ComboBox` plus per-provider panels.

**Tech Stack:** .NET 9 / C# / WPF, OpenAI NuGet 2.13.0, `System.Net.Http`, `System.Text.Json`, DPAPI via existing `ApiKeyProtection`.

**Spec:** `docs/superpowers/specs/2026-09-02-multi-provider-transcription-design.md`
**Research:** `docs/superpowers/specs/2026-09-02-transcription-providers-research.md`

## Global Constraints

- Worktree: `C:\Users\Mike\SimpleWhisper\.claude\worktrees\transcription-api-research-dfb640`, branch `claude/transcription-api-research-dfb640`. Every implementer runs `git rev-parse --abbrev-ref HEAD` in that directory and verifies the branch before committing. No git commands anywhere else.
- **No automated test project exists in this repo.** Do not add one. Verification per task = `dotnet build SimpleWhisper.sln -c Release` from the worktree root with **zero errors and no new warnings**, plus the manual matrix in Task 9.
- Existing settings JSON must keep loading: `"engine": "Cloud"` still means OpenAI; every new property has a default.
- API keys are stored only through the `Encrypted*` DPAPI properties. Never log a key.
- Match existing style: file-scoped namespaces, XML doc comments on public members, `AppLogger.Log` for diagnostics, section-divider comments in the view model.
- Commit after every task with the repo's short imperative style ("Add X", "Rename Y").
- Target framework is `net9.0-windows`; C# 13 features are allowed.

## Model / Agent Assignment (cost-effective routing)

Orchestrator (this session, Fable) dispatches one fresh implementer per task and one reviewer per task, following `superpowers:subagent-driven-development`.

| Task | Nature | Implementer model | Reviewer model |
|---|---|---|---|
| 1 Settings model | Mechanical property additions, fully specified | **haiku** | sonnet |
| 2 OpenAI service generalization + NuGet bump | SDK API change, refactor | **sonnet** | sonnet |
| 3 Gemini REST service | New HTTP client, JSON parsing | **sonnet** | sonnet |
| 4 Deepgram REST service | New HTTP client, JSON parsing | **sonnet** | sonnet |
| 5 Manager routing | Multi-branch logic touching all services | **sonnet** | sonnet |
| 6 SettingsViewModel | Many bindings, easy to mis-name | **sonnet** | sonnet |
| 7 Settings XAML + code-behind | WPF bindings, visibility wiring | **sonnet** | sonnet |
| 8 Version bump + CHANGELOG | Text-only | **haiku** | none (orchestrator eyeballs diff) |
| 9 Manual verification | Needs real keys and a microphone | **orchestrator + user** | — |

Tasks 3 and 4 are independent of each other and of Task 2; dispatch them in parallel after Task 1. Task 5 needs 2, 3, 4. Task 6 needs 1 and 5. Task 7 needs 6.

---

### Task 1: Settings model — engine enum, per-provider keys and models, model catalog

**Files:**
- Modify: `src/SimpleWhisper/Models/AppSettings.cs`
- Create: `src/SimpleWhisper/Models/TranscriptionModelCatalog.cs`

**Interfaces:**
- Consumes: `Helpers.ApiKeyProtection.Protect/Unprotect` (existing).
- Produces: `TranscriptionEngine.{Cloud,Local,Gemini,Groq,Deepgram}`; `AppSettings.OpenAIModel`, `GeminiModel`, `GroqModel`, `DeepgramModel` (string), `GeminiApiKey`, `GroqApiKey`, `DeepgramApiKey` (string, plaintext accessors); `TranscriptionModelCatalog.ModelsFor(TranscriptionEngine)` returning `IReadOnlyList<TranscriptionModelOption>`; `record TranscriptionModelOption(string Id, string DisplayName)`.

- [ ] **Step 1: Extend the enum**

In `AppSettings.cs` replace the `TranscriptionEngine` enum with:

```csharp
/// <summary>
/// Selects which speech-to-text engine processes the audio.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranscriptionEngine
{
    /// <summary>OpenAI cloud API (requires API key and internet connection).
    /// The member is named <c>Cloud</c> for backward compatibility with existing settings files.</summary>
    Cloud,

    /// <summary>Local Whisper.net model (runs entirely on the local machine).</summary>
    Local,

    /// <summary>Google Gemini API (gemini-3.5-transcribe). Requires a Gemini API key.</summary>
    Gemini,

    /// <summary>Groq cloud API (hosted Whisper, OpenAI-compatible endpoint). Requires a Groq API key.</summary>
    Groq,

    /// <summary>Deepgram cloud API (Nova-3). Requires a Deepgram API key.</summary>
    Deepgram
}
```

- [ ] **Step 2: Add model and key properties**

Directly after the existing `OpenAIApiKey` property in `AppSettings`, insert:

```csharp
    /// <summary>
    /// OpenAI transcription model id (e.g. "gpt-4o-mini-transcribe", "gpt-4o-transcribe", "whisper-1").
    /// </summary>
    [JsonPropertyName("openAIModel")]
    public string OpenAIModel { get; set; } = "gpt-4o-mini-transcribe";

    /// <summary>
    /// Gemini transcription model id. Currently only "gemini-3.5-transcribe" is supported.
    /// </summary>
    [JsonPropertyName("geminiModel")]
    public string GeminiModel { get; set; } = "gemini-3.5-transcribe";

    /// <summary>
    /// DPAPI-encrypted Gemini API key stored as Base64.
    /// </summary>
    [JsonPropertyName("geminiApiKeyProtected")]
    public string EncryptedGeminiApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Plaintext Gemini API key for runtime use. Not serialized to JSON.
    /// </summary>
    [JsonIgnore]
    public string GeminiApiKey
    {
        get => Helpers.ApiKeyProtection.Unprotect(EncryptedGeminiApiKey);
        set => EncryptedGeminiApiKey = Helpers.ApiKeyProtection.Protect(value);
    }

    /// <summary>
    /// Groq transcription model id (e.g. "whisper-large-v3-turbo", "whisper-large-v3").
    /// </summary>
    [JsonPropertyName("groqModel")]
    public string GroqModel { get; set; } = "whisper-large-v3-turbo";

    /// <summary>
    /// DPAPI-encrypted Groq API key stored as Base64.
    /// </summary>
    [JsonPropertyName("groqApiKeyProtected")]
    public string EncryptedGroqApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Plaintext Groq API key for runtime use. Not serialized to JSON.
    /// </summary>
    [JsonIgnore]
    public string GroqApiKey
    {
        get => Helpers.ApiKeyProtection.Unprotect(EncryptedGroqApiKey);
        set => EncryptedGroqApiKey = Helpers.ApiKeyProtection.Protect(value);
    }

    /// <summary>
    /// Deepgram transcription model id. Currently only "nova-3" is supported.
    /// </summary>
    [JsonPropertyName("deepgramModel")]
    public string DeepgramModel { get; set; } = "nova-3";

    /// <summary>
    /// DPAPI-encrypted Deepgram API key stored as Base64.
    /// </summary>
    [JsonPropertyName("deepgramApiKeyProtected")]
    public string EncryptedDeepgramApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Plaintext Deepgram API key for runtime use. Not serialized to JSON.
    /// </summary>
    [JsonIgnore]
    public string DeepgramApiKey
    {
        get => Helpers.ApiKeyProtection.Unprotect(EncryptedDeepgramApiKey);
        set => EncryptedDeepgramApiKey = Helpers.ApiKeyProtection.Protect(value);
    }
```

Also update the XML comment on `Engine` to read: `Selects which transcription engine is used: OpenAI (Cloud), Local (Whisper.net), Gemini, Groq, or Deepgram.`

- [ ] **Step 3: Create the model catalog**

Create `src/SimpleWhisper/Models/TranscriptionModelCatalog.cs`:

```csharp
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
```

- [ ] **Step 4: Build**

Run from the worktree root: `dotnet build SimpleWhisper.sln -c Release`
Expected: succeeds with 0 errors. (The manager still compiles because it only references `Cloud` and `Local`.)

- [ ] **Step 5: Commit**

```bash
git add src/SimpleWhisper/Models/AppSettings.cs src/SimpleWhisper/Models/TranscriptionModelCatalog.cs
git commit -m "Add Gemini, Groq, Deepgram engines and per-provider settings"
```

---

### Task 2: Generalize the OpenAI service to any OpenAI-compatible endpoint; upgrade the SDK

**Files:**
- Modify: `src/SimpleWhisper/SimpleWhisper.csproj:24` (OpenAI package version)
- Rename + modify: `src/SimpleWhisper/Services/CloudTranscriptionService.cs` → `src/SimpleWhisper/Services/OpenAICompatibleTranscriptionService.cs`
- Modify: `src/SimpleWhisper/Services/TranscriptionManager.cs` (only enough to compile; full routing is Task 5)

**Interfaces:**
- Consumes: `OpenAI.OpenAIClient(ApiKeyCredential, OpenAIClientOptions)`, `OpenAIClientOptions.Endpoint`, `AudioClient.TranscribeAudioAsync(Stream, string, AudioTranscriptionOptions, CancellationToken)`.
- Produces: `public sealed class OpenAICompatibleTranscriptionService : ITranscriptionService` with constructor `(string apiKey, string model, Uri? endpoint, string providerDisplayName)`, method `UpdateConfiguration(string apiKey, string model)`, and `public static readonly Uri GroqEndpoint = new("https://api.groq.com/openai/v1")`.

- [ ] **Step 1: Bump the NuGet package**

In `SimpleWhisper.csproj` change `<PackageReference Include="OpenAI" Version="2.9.1" />` to `<PackageReference Include="OpenAI" Version="2.13.0" />`. Run `dotnet restore SimpleWhisper.sln` and confirm it resolves 2.13.0.

- [ ] **Step 2: Rename the file**

```bash
git mv src/SimpleWhisper/Services/CloudTranscriptionService.cs src/SimpleWhisper/Services/OpenAICompatibleTranscriptionService.cs
```

- [ ] **Step 3: Rewrite the class**

Replace the whole file content with:

```csharp
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
```

- [ ] **Step 4: Patch the manager so it compiles**

In `TranscriptionManager.cs`:
- Change the field `private CloudTranscriptionService? _cloudService;` to `private OpenAICompatibleTranscriptionService? _openAIService;`.
- Replace `GetOrCreateCloudService(string apiKey)` with:

```csharp
    /// <summary>
    /// Gets the OpenAI transcription service, creating it lazily and keeping its key and model in sync with settings.
    /// </summary>
    private OpenAICompatibleTranscriptionService GetOrCreateOpenAIService(AppSettings settings)
    {
        lock (_serviceLock)
        {
            if (_openAIService is null)
            {
                _openAIService = new OpenAICompatibleTranscriptionService(
                    settings.OpenAIApiKey, settings.OpenAIModel, endpoint: null, providerDisplayName: "OpenAI");
            }
            else
            {
                _openAIService.UpdateConfiguration(settings.OpenAIApiKey, settings.OpenAIModel);
            }

            return _openAIService;
        }
    }
```

- In `TranscribeAsync`, change `TranscriptionEngine.Cloud => GetOrCreateCloudService(settings.OpenAIApiKey),` to `TranscriptionEngine.Cloud => GetOrCreateOpenAIService(settings),`.
- In `Dispose`, change `_cloudService = null;` to `_openAIService = null;`.

- [ ] **Step 5: Build**

Run: `dotnet build SimpleWhisper.sln -c Release`
Expected: 0 errors. If `OpenAIClientOptions.Endpoint` or the `OpenAIClient(ApiKeyCredential, OpenAIClientOptions)` constructor is reported missing, the restore did not pick up 2.13.0; run `dotnet restore --force` and rebuild.

- [ ] **Step 6: Commit**

```bash
git add -A src/SimpleWhisper/SimpleWhisper.csproj src/SimpleWhisper/Services
git commit -m "Generalize OpenAI service to OpenAI-compatible endpoints and upgrade SDK to 2.13.0"
```

---

### Task 3: Gemini transcription service

**Files:**
- Create: `src/SimpleWhisper/Services/GeminiTranscriptionService.cs`
- Create: `src/SimpleWhisper/Services/RestTranscriptionHttp.cs`

**Interfaces:**
- Consumes: `TranscriptionResult.Success/Failure`, `AppLogger.Log`.
- Produces: `public sealed class GeminiTranscriptionService : ITranscriptionService` with constructor `(string apiKey, string model)` and `UpdateConfiguration(string apiKey, string model)`; `internal static class RestTranscriptionHttp` exposing `public static HttpClient Client` and `public static IReadOnlyList<string> SplitVocabularyPrompt(string? prompt)`.

Verified API shape (https://ai.google.dev/gemini-api/docs/transcribe, https://ai.google.dev/api/interactions-api):

```
POST https://generativelanguage.googleapis.com/v1beta/interactions
x-goog-api-key: <key>
Content-Type: application/json
{
  "model": "gemini-3.5-transcribe",
  "input": [ { "type": "audio", "data": "<base64 wav>", "mime_type": "audio/wav" } ],
  "generation_config": {
    "transcription_config": {
      "language_codes": ["en"],
      "custom_vocabulary": ["Kubernetes", "SimpleWhisper"],
      "mode": "smart"
    }
  }
}
```

Response: top-level `output_text` when present; otherwise `steps[]` items with `"type": "model_output"` whose `content[]` has `"type": "text"` and a `"text"` field. `status` is one of `completed`, `failed`, etc.

- [ ] **Step 1: Create the shared HTTP helper**

Create `src/SimpleWhisper/Services/RestTranscriptionHttp.cs`:

```csharp
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
```

- [ ] **Step 2: Create the Gemini service**

Create `src/SimpleWhisper/Services/GeminiTranscriptionService.cs`:

```csharp
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
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return TranscriptionResult.Failure("Transcription was cancelled.", stopwatch.Elapsed);
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

        if (root.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.String &&
            status.GetString() == "failed")
        {
            return string.Empty;
        }

        if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var parts = new List<string>();
        foreach (var step in steps.EnumerateArray())
        {
            if (!step.TryGetProperty("type", out var stepType) || stepType.GetString() != "model_output")
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

        return string.Join(" ", parts);
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
```

- [ ] **Step 3: Build**

Run: `dotnet build SimpleWhisper.sln -c Release`
Expected: 0 errors. `System.Net.Http.Json` is part of the shared framework in .NET 9; no package is needed.

- [ ] **Step 4: Commit**

```bash
git add src/SimpleWhisper/Services/GeminiTranscriptionService.cs src/SimpleWhisper/Services/RestTranscriptionHttp.cs
git commit -m "Add Gemini 3.5 Transcribe service"
```

---

### Task 4: Deepgram transcription service

**Files:**
- Create: `src/SimpleWhisper/Services/DeepgramTranscriptionService.cs`

**Interfaces:**
- Consumes: `RestTranscriptionHttp.Client`, `RestTranscriptionHttp.SplitVocabularyPrompt` (Task 3). If Task 3 has not landed yet in this branch, create `RestTranscriptionHttp.cs` exactly as shown in Task 3 Step 1 as part of this task.
- Produces: `public sealed class DeepgramTranscriptionService : ITranscriptionService` with constructor `(string apiKey, string model)` and `UpdateConfiguration(string apiKey, string model)`.

Verified API shape (https://developers.deepgram.com/docs/pre-recorded-audio, https://developers.deepgram.com/docs/keyterm):

```
POST https://api.deepgram.com/v1/listen?model=nova-3&smart_format=true&language=en&keyterm=Kubernetes&keyterm=Simple+Whisper
Authorization: Token <key>
Content-Type: audio/wav
<raw WAV bytes>
→ results.channels[0].alternatives[0].transcript  (also .confidence)
→ results.channels[0].detected_language when detect_language=true
```

- [ ] **Step 1: Create the service**

Create `src/SimpleWhisper/Services/DeepgramTranscriptionService.cs`:

```csharp
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
```

- [ ] **Step 2: Build**

Run: `dotnet build SimpleWhisper.sln -c Release`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/SimpleWhisper/Services/DeepgramTranscriptionService.cs
git commit -m "Add Deepgram Nova-3 transcription service"
```

---

### Task 5: Route every engine through TranscriptionManager

**Files:**
- Modify: `src/SimpleWhisper/Services/TranscriptionManager.cs`

**Interfaces:**
- Consumes: `OpenAICompatibleTranscriptionService` (Task 2), `GeminiTranscriptionService` (Task 3), `DeepgramTranscriptionService` (Task 4), `AppSettings` provider properties (Task 1).
- Produces: `TranscriptionManager.TranscribeAsync` handling all five `TranscriptionEngine` values. `TranscribeChunkAsync` is unchanged (local only).

- [ ] **Step 1: Add service fields**

Replace the two service fields at the top of the class with:

```csharp
    private OpenAICompatibleTranscriptionService? _openAIService;
    private OpenAICompatibleTranscriptionService? _groqService;
    private GeminiTranscriptionService? _geminiService;
    private DeepgramTranscriptionService? _deepgramService;
    private LocalTranscriptionService? _localService;
```

- [ ] **Step 2: Replace the engine switch and the not-available message in `TranscribeAsync`**

Replace the block from `ITranscriptionService service = engine switch` through the `return TranscriptionResult.Failure(reason);` closing brace with:

```csharp
        ITranscriptionService service = engine switch
        {
            TranscriptionEngine.Cloud => GetOrCreateOpenAIService(settings),
            TranscriptionEngine.Groq => GetOrCreateGroqService(settings),
            TranscriptionEngine.Gemini => GetOrCreateGeminiService(settings),
            TranscriptionEngine.Deepgram => GetOrCreateDeepgramService(settings),
            TranscriptionEngine.Local => await GetOrCreateLocalServiceAsync(settings, language, prompt, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown transcription engine: {engine}")
        };

        if (!service.IsAvailable)
        {
            string reason = engine switch
            {
                TranscriptionEngine.Cloud => "OpenAI API key is not configured. Please add your key in Settings.",
                TranscriptionEngine.Groq => "Groq API key is not configured. Please add your key in Settings.",
                TranscriptionEngine.Gemini => "Gemini API key is not configured. Please add your key in Settings.",
                TranscriptionEngine.Deepgram => "Deepgram API key is not configured. Please add your key in Settings.",
                _ => "Local transcription engine is not available."
            };

            OnStatusChanged(reason);
            return TranscriptionResult.Failure(reason);
        }
```

- [ ] **Step 3: Add the factory methods**

After `GetOrCreateOpenAIService` (from Task 2) add:

```csharp
    /// <summary>
    /// Gets the Groq transcription service (OpenAI-compatible endpoint), creating it lazily
    /// and keeping its key and model in sync with settings.
    /// </summary>
    private OpenAICompatibleTranscriptionService GetOrCreateGroqService(AppSettings settings)
    {
        lock (_serviceLock)
        {
            if (_groqService is null)
            {
                _groqService = new OpenAICompatibleTranscriptionService(
                    settings.GroqApiKey,
                    settings.GroqModel,
                    OpenAICompatibleTranscriptionService.GroqEndpoint,
                    providerDisplayName: "Groq");
            }
            else
            {
                _groqService.UpdateConfiguration(settings.GroqApiKey, settings.GroqModel);
            }

            return _groqService;
        }
    }

    /// <summary>
    /// Gets the Gemini transcription service, creating it lazily and syncing its configuration.
    /// </summary>
    private GeminiTranscriptionService GetOrCreateGeminiService(AppSettings settings)
    {
        lock (_serviceLock)
        {
            if (_geminiService is null)
            {
                _geminiService = new GeminiTranscriptionService(settings.GeminiApiKey, settings.GeminiModel);
            }
            else
            {
                _geminiService.UpdateConfiguration(settings.GeminiApiKey, settings.GeminiModel);
            }

            return _geminiService;
        }
    }

    /// <summary>
    /// Gets the Deepgram transcription service, creating it lazily and syncing its configuration.
    /// </summary>
    private DeepgramTranscriptionService GetOrCreateDeepgramService(AppSettings settings)
    {
        lock (_serviceLock)
        {
            if (_deepgramService is null)
            {
                _deepgramService = new DeepgramTranscriptionService(settings.DeepgramApiKey, settings.DeepgramModel);
            }
            else
            {
                _deepgramService.UpdateConfiguration(settings.DeepgramApiKey, settings.DeepgramModel);
            }

            return _deepgramService;
        }
    }
```

- [ ] **Step 4: Update Dispose**

```csharp
    public void Dispose()
    {
        lock (_serviceLock)
        {
            _localService?.Dispose();
            _localService = null;
            _openAIService = null;
            _groqService = null;
            _geminiService = null;
            _deepgramService = null;
        }
    }
```

- [ ] **Step 5: Update the class-level and `TranscribeAsync` XML comments**

Change "(cloud or local)" in the class summary to "(OpenAI, Gemini, Groq, Deepgram, or local)". Change the `BuildVocabularyPrompt` remark "The Whisper API uses the prompt…" to "OpenAI-compatible engines receive this as a prompt; Gemini and Deepgram split it back into terms via <see cref=\"RestTranscriptionHttp.SplitVocabularyPrompt\"/>."

- [ ] **Step 6: Build**

Run: `dotnet build SimpleWhisper.sln -c Release`
Expected: 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/SimpleWhisper/Services/TranscriptionManager.cs
git commit -m "Route transcription to OpenAI, Groq, Gemini, Deepgram, or local engine"
```

---

### Task 6: SettingsViewModel — engine picker, per-provider keys and models

**Files:**
- Modify: `src/SimpleWhisper/ViewModels/SettingsViewModel.cs` (fields at ~lines 43–45, properties at ~281–330, load block at ~589–591)

**Interfaces:**
- Consumes: `TranscriptionEngine`, `TranscriptionModelCatalog`, `TranscriptionModelOption`, `AppSettings` provider properties (Task 1).
- Produces (bound by Task 7's XAML): `IReadOnlyList<EngineOption> EngineOptions`; `TranscriptionEngine SelectedEngine`; `bool IsOpenAIEngine`, `IsGeminiEngine`, `IsGroqEngine`, `IsDeepgramEngine`, `IsLocalEngine`; `string OpenAIApiKey`, `GeminiApiKey`, `GroqApiKey`, `DeepgramApiKey`; `IReadOnlyList<TranscriptionModelOption> OpenAIModels`, `GroqModels`; `string OpenAIModel`, `GroqModel`. `IsCloudEngine` and `ApiKey` are **removed**.

- [ ] **Step 1: Add the engine option record**

At the bottom of `SettingsViewModel.cs`, outside the class but inside the namespace, add:

```csharp
/// <summary>
/// One entry in the engine picker.
/// </summary>
/// <param name="Engine">The engine value stored in settings.</param>
/// <param name="DisplayName">Label shown in the ComboBox.</param>
public sealed record EngineOption(TranscriptionEngine Engine, string DisplayName);
```

- [ ] **Step 2: Replace the fields**

Replace

```csharp
    private bool _isCloudEngine;
    private bool _isLocalEngine;
    private string _apiKey = string.Empty;
```

with

```csharp
    private TranscriptionEngine _selectedEngine;
    private string _openAIApiKey = string.Empty;
    private string _geminiApiKey = string.Empty;
    private string _groqApiKey = string.Empty;
    private string _deepgramApiKey = string.Empty;
    private string _openAIModel = string.Empty;
    private string _groqModel = string.Empty;
```

- [ ] **Step 3: Replace `IsCloudEngine`, `IsLocalEngine`, and `ApiKey` properties**

Replace those three properties (from `public bool IsCloudEngine` through the end of `ApiKey`) with:

```csharp
    /// <summary>
    /// All engines the user can pick, in display order.
    /// </summary>
    public IReadOnlyList<EngineOption> EngineOptions { get; } =
    [
        new(TranscriptionEngine.Cloud, "OpenAI (Cloud)"),
        new(TranscriptionEngine.Gemini, "Google Gemini (Cloud)"),
        new(TranscriptionEngine.Groq, "Groq (Cloud)"),
        new(TranscriptionEngine.Deepgram, "Deepgram (Cloud)"),
        new(TranscriptionEngine.Local, "Local (Whisper.net, runs on device)")
    ];

    /// <summary>
    /// The currently selected transcription engine. Setting it persists immediately and
    /// refreshes every per-engine visibility flag.
    /// </summary>
    public TranscriptionEngine SelectedEngine
    {
        get => _selectedEngine;
        set
        {
            if (SetProperty(ref _selectedEngine, value))
            {
                _settingsService.Settings.Engine = value;
                SaveSettings();
                OnPropertyChanged(nameof(IsOpenAIEngine));
                OnPropertyChanged(nameof(IsGeminiEngine));
                OnPropertyChanged(nameof(IsGroqEngine));
                OnPropertyChanged(nameof(IsDeepgramEngine));
                OnPropertyChanged(nameof(IsLocalEngine));
            }
        }
    }

    /// <summary>True when the OpenAI engine is selected. Drives the OpenAI settings panel.</summary>
    public bool IsOpenAIEngine => _selectedEngine == TranscriptionEngine.Cloud;

    /// <summary>True when the Gemini engine is selected. Drives the Gemini settings panel.</summary>
    public bool IsGeminiEngine => _selectedEngine == TranscriptionEngine.Gemini;

    /// <summary>True when the Groq engine is selected. Drives the Groq settings panel.</summary>
    public bool IsGroqEngine => _selectedEngine == TranscriptionEngine.Groq;

    /// <summary>True when the Deepgram engine is selected. Drives the Deepgram settings panel.</summary>
    public bool IsDeepgramEngine => _selectedEngine == TranscriptionEngine.Deepgram;

    /// <summary>True when the local engine is selected. Drives the Local settings panel.</summary>
    public bool IsLocalEngine => _selectedEngine == TranscriptionEngine.Local;

    /// <summary>OpenAI API key.</summary>
    public string OpenAIApiKey
    {
        get => _openAIApiKey;
        set
        {
            if (SetProperty(ref _openAIApiKey, value))
            {
                _settingsService.Settings.OpenAIApiKey = value;
                SaveSettings();
            }
        }
    }

    /// <summary>Google Gemini API key.</summary>
    public string GeminiApiKey
    {
        get => _geminiApiKey;
        set
        {
            if (SetProperty(ref _geminiApiKey, value))
            {
                _settingsService.Settings.GeminiApiKey = value;
                SaveSettings();
            }
        }
    }

    /// <summary>Groq API key.</summary>
    public string GroqApiKey
    {
        get => _groqApiKey;
        set
        {
            if (SetProperty(ref _groqApiKey, value))
            {
                _settingsService.Settings.GroqApiKey = value;
                SaveSettings();
            }
        }
    }

    /// <summary>Deepgram API key.</summary>
    public string DeepgramApiKey
    {
        get => _deepgramApiKey;
        set
        {
            if (SetProperty(ref _deepgramApiKey, value))
            {
                _settingsService.Settings.DeepgramApiKey = value;
                SaveSettings();
            }
        }
    }

    /// <summary>Selectable OpenAI models.</summary>
    public IReadOnlyList<TranscriptionModelOption> OpenAIModels { get; } =
        TranscriptionModelCatalog.ModelsFor(TranscriptionEngine.Cloud);

    /// <summary>Selectable Groq models.</summary>
    public IReadOnlyList<TranscriptionModelOption> GroqModels { get; } =
        TranscriptionModelCatalog.ModelsFor(TranscriptionEngine.Groq);

    /// <summary>Selected OpenAI model id.</summary>
    public string OpenAIModel
    {
        get => _openAIModel;
        set
        {
            if (SetProperty(ref _openAIModel, value))
            {
                _settingsService.Settings.OpenAIModel = value;
                SaveSettings();
            }
        }
    }

    /// <summary>Selected Groq model id.</summary>
    public string GroqModel
    {
        get => _groqModel;
        set
        {
            if (SetProperty(ref _groqModel, value))
            {
                _settingsService.Settings.GroqModel = value;
                SaveSettings();
            }
        }
    }
```

- [ ] **Step 4: Update the load block**

Replace

```csharp
        _isCloudEngine = s.Engine == TranscriptionEngine.Cloud;
        _isLocalEngine = s.Engine == TranscriptionEngine.Local;
        _apiKey = s.OpenAIApiKey;
```

with

```csharp
        _selectedEngine = s.Engine;
        _openAIApiKey = s.OpenAIApiKey;
        _geminiApiKey = s.GeminiApiKey;
        _groqApiKey = s.GroqApiKey;
        _deepgramApiKey = s.DeepgramApiKey;
        _openAIModel = OpenAIModels.Any(m => m.Id == s.OpenAIModel) ? s.OpenAIModel : OpenAIModels[0].Id;
        _groqModel = GroqModels.Any(m => m.Id == s.GroqModel) ? s.GroqModel : GroqModels[0].Id;
```

- [ ] **Step 5: Search for leftovers**

Run from the worktree root: `grep -rn "IsCloudEngine\|ViewModel.ApiKey\|\.ApiKey\b" src/SimpleWhisper --include=*.cs --include=*.xaml`
Expected: hits only in `SettingsWindow.xaml` and `SettingsWindow.xaml.cs` (Task 7 fixes those). The build will fail until Task 7 lands, which is expected; do **not** try to fix the XAML in this task.

- [ ] **Step 6: Commit**

```bash
git add src/SimpleWhisper/ViewModels/SettingsViewModel.cs
git commit -m "Add engine picker and per-provider key/model properties to SettingsViewModel"
```

---

### Task 7: Settings window — engine ComboBox and per-provider panels

**Files:**
- Modify: `src/SimpleWhisper/Views/SettingsWindow.xaml` (Engine section and Cloud Settings border, ~lines 269–303)
- Modify: `src/SimpleWhisper/Views/SettingsWindow.xaml.cs` (~lines 31 and 58–66)

**Interfaces:**
- Consumes: every property listed under Task 6 "Produces".
- Produces: a building, working Settings window.

- [ ] **Step 1: Replace the Engine section**

Replace the `<!-- Engine Section -->` border (the one containing two `RadioButton`s) with:

```xml
                        <!-- Engine Section -->
                        <TextBlock Text="Engine" Style="{StaticResource SectionHeader}"/>
                        <Border Background="{StaticResource PanelBg}" CornerRadius="6" Padding="12,10" Margin="0,0,0,10">
                            <StackPanel>
                                <TextBlock Text="Transcription engine" Style="{StaticResource FieldLabel}"/>
                                <ComboBox ItemsSource="{Binding EngineOptions}"
                                          SelectedValue="{Binding SelectedEngine, Mode=TwoWay}"
                                          SelectedValuePath="Engine"
                                          DisplayMemberPath="DisplayName"
                                          Style="{StaticResource DarkComboBox}"/>
                            </StackPanel>
                        </Border>
```

`DarkComboBox` already exists in this file (used by the Local-settings ComboBoxes at ~line 309).

- [ ] **Step 2: Replace the Cloud Settings border with four provider panels**

Replace the `<!-- Cloud Settings -->` border with:

```xml
                        <!-- OpenAI Settings -->
                        <Border Background="{StaticResource PanelBg}" CornerRadius="6" Padding="12,10" Margin="0,0,0,10"
                                Visibility="{Binding IsOpenAIEngine, Converter={StaticResource BoolToVis}}">
                            <StackPanel>
                                <TextBlock Text="OpenAI Settings" Style="{StaticResource SectionHeader}" Margin="0,0,0,6"/>
                                <TextBlock Text="OpenAI API Key" Style="{StaticResource FieldLabel}"/>
                                <PasswordBox x:Name="OpenAIApiKeyBox"
                                             Style="{StaticResource DarkPasswordBox}"
                                             PasswordChanged="OnOpenAIApiKeyChanged"
                                             MaxLength="200"
                                             Margin="0,0,0,8"/>
                                <TextBlock Text="Model" Style="{StaticResource FieldLabel}"/>
                                <ComboBox ItemsSource="{Binding OpenAIModels}"
                                          SelectedValue="{Binding OpenAIModel, Mode=TwoWay}"
                                          SelectedValuePath="Id"
                                          DisplayMemberPath="DisplayName"
                                          Style="{StaticResource DarkComboBox}"
                                          Margin="0,0,0,8"/>
                                <TextBlock>
                                    <Hyperlink NavigateUri="https://platform.openai.com/api-keys"
                                               RequestNavigate="OnHyperlinkRequestNavigate"
                                               Foreground="{StaticResource AccentBrush}">
                                        Get an API key from OpenAI
                                    </Hyperlink>
                                </TextBlock>
                            </StackPanel>
                        </Border>

                        <!-- Gemini Settings -->
                        <Border Background="{StaticResource PanelBg}" CornerRadius="6" Padding="12,10" Margin="0,0,0,10"
                                Visibility="{Binding IsGeminiEngine, Converter={StaticResource BoolToVis}}">
                            <StackPanel>
                                <TextBlock Text="Google Gemini Settings" Style="{StaticResource SectionHeader}" Margin="0,0,0,6"/>
                                <TextBlock Text="Gemini API Key" Style="{StaticResource FieldLabel}"/>
                                <PasswordBox x:Name="GeminiApiKeyBox"
                                             Style="{StaticResource DarkPasswordBox}"
                                             PasswordChanged="OnGeminiApiKeyChanged"
                                             MaxLength="200"
                                             Margin="0,0,0,8"/>
                                <TextBlock Text="Model: gemini-3.5-transcribe" Style="{StaticResource FieldLabel}" Margin="0,0,0,8"/>
                                <TextBlock>
                                    <Hyperlink NavigateUri="https://aistudio.google.com/apikey"
                                               RequestNavigate="OnHyperlinkRequestNavigate"
                                               Foreground="{StaticResource AccentBrush}">
                                        Get an API key from Google AI Studio
                                    </Hyperlink>
                                </TextBlock>
                            </StackPanel>
                        </Border>

                        <!-- Groq Settings -->
                        <Border Background="{StaticResource PanelBg}" CornerRadius="6" Padding="12,10" Margin="0,0,0,10"
                                Visibility="{Binding IsGroqEngine, Converter={StaticResource BoolToVis}}">
                            <StackPanel>
                                <TextBlock Text="Groq Settings" Style="{StaticResource SectionHeader}" Margin="0,0,0,6"/>
                                <TextBlock Text="Groq API Key" Style="{StaticResource FieldLabel}"/>
                                <PasswordBox x:Name="GroqApiKeyBox"
                                             Style="{StaticResource DarkPasswordBox}"
                                             PasswordChanged="OnGroqApiKeyChanged"
                                             MaxLength="200"
                                             Margin="0,0,0,8"/>
                                <TextBlock Text="Model" Style="{StaticResource FieldLabel}"/>
                                <ComboBox ItemsSource="{Binding GroqModels}"
                                          SelectedValue="{Binding GroqModel, Mode=TwoWay}"
                                          SelectedValuePath="Id"
                                          DisplayMemberPath="DisplayName"
                                          Style="{StaticResource DarkComboBox}"
                                          Margin="0,0,0,8"/>
                                <TextBlock>
                                    <Hyperlink NavigateUri="https://console.groq.com/keys"
                                               RequestNavigate="OnHyperlinkRequestNavigate"
                                               Foreground="{StaticResource AccentBrush}">
                                        Get an API key from Groq
                                    </Hyperlink>
                                </TextBlock>
                            </StackPanel>
                        </Border>

                        <!-- Deepgram Settings -->
                        <Border Background="{StaticResource PanelBg}" CornerRadius="6" Padding="12,10" Margin="0,0,0,10"
                                Visibility="{Binding IsDeepgramEngine, Converter={StaticResource BoolToVis}}">
                            <StackPanel>
                                <TextBlock Text="Deepgram Settings" Style="{StaticResource SectionHeader}" Margin="0,0,0,6"/>
                                <TextBlock Text="Deepgram API Key" Style="{StaticResource FieldLabel}"/>
                                <PasswordBox x:Name="DeepgramApiKeyBox"
                                             Style="{StaticResource DarkPasswordBox}"
                                             PasswordChanged="OnDeepgramApiKeyChanged"
                                             MaxLength="200"
                                             Margin="0,0,0,8"/>
                                <TextBlock Text="Model: nova-3" Style="{StaticResource FieldLabel}" Margin="0,0,0,8"/>
                                <TextBlock>
                                    <Hyperlink NavigateUri="https://console.deepgram.com/"
                                               RequestNavigate="OnHyperlinkRequestNavigate"
                                               Foreground="{StaticResource AccentBrush}">
                                        Get an API key from Deepgram
                                    </Hyperlink>
                                </TextBlock>
                            </StackPanel>
                        </Border>
```

- [ ] **Step 3: Update the code-behind**

In `SettingsWindow.xaml.cs` replace `ApiKeyBox.Password = ViewModel.ApiKey;` with:

```csharp
        // Pre-populate the PasswordBoxes (PasswordBox does not support binding)
        OpenAIApiKeyBox.Password = ViewModel.OpenAIApiKey;
        GeminiApiKeyBox.Password = ViewModel.GeminiApiKey;
        GroqApiKeyBox.Password = ViewModel.GroqApiKey;
        DeepgramApiKeyBox.Password = ViewModel.DeepgramApiKey;
```

Replace the `OnApiKeyChanged` handler with:

```csharp
    /// <summary>
    /// PasswordBox doesn't support binding, so each provider's key is synced manually on change.
    /// </summary>
    private void OnOpenAIApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
            ViewModel.OpenAIApiKey = pb.Password;
    }

    private void OnGeminiApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
            ViewModel.GeminiApiKey = pb.Password;
    }

    private void OnGroqApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
            ViewModel.GroqApiKey = pb.Password;
    }

    private void OnDeepgramApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
            ViewModel.DeepgramApiKey = pb.Password;
    }
```

- [ ] **Step 4: Build and smoke-run**

Run: `dotnet build SimpleWhisper.sln -c Release`
Expected: 0 errors, no XAML binding errors at build.

Then run: `dotnet run --project src/SimpleWhisper/SimpleWhisper.csproj -c Release`
Open Settings → Transcription. Expected: the engine ComboBox lists five entries; switching engines shows exactly one provider panel (or the Local panel); closing and reopening Settings restores the selection. Close the app.

- [ ] **Step 5: Commit**

```bash
git add src/SimpleWhisper/Views/SettingsWindow.xaml src/SimpleWhisper/Views/SettingsWindow.xaml.cs
git commit -m "Add engine picker and provider panels to Settings window"
```

---

### Task 8: Version bump and changelog

**Files:**
- Modify: `src/SimpleWhisper/SimpleWhisper.csproj:12` (`<Version>`)
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Bump the version**

Change `<Version>1.4.0</Version>` to `<Version>1.5.0</Version>`. If the installer directory contains a version string (run `grep -rn "1\.4\.0" installer build.ps1`), update those too.

- [ ] **Step 2: Add the changelog entry**

Add at the top of `CHANGELOG.md`, following the existing entry format in that file:

```markdown
## 1.5.0 — 2026-09-02

### Added
- Engine picker in Settings with four cloud providers plus Local.
- Google Gemini 3.5 Transcribe (`gemini-3.5-transcribe`) via the Gemini API.
- Groq hosted Whisper (`whisper-large-v3-turbo`, `whisper-large-v3`).
- Deepgram Nova-3 with key-term boosting from the custom vocabulary list.
- OpenAI model selection: `gpt-4o-mini-transcribe` (new default), `gpt-4o-transcribe`, `whisper-1`.
- One DPAPI-encrypted API key per provider.

### Changed
- OpenAI NuGet package upgraded from 2.9.1 to 2.13.0.
- New installs and existing installs default to `gpt-4o-mini-transcribe` instead of `whisper-1`.
```

- [ ] **Step 3: Build and commit**

Run: `dotnet build SimpleWhisper.sln -c Release` → 0 errors.

```bash
git add src/SimpleWhisper/SimpleWhisper.csproj CHANGELOG.md installer build.ps1
git commit -m "Bump version to 1.5.0 for multi-provider transcription"
```

---

### Task 9: Manual verification matrix (orchestrator + user)

This needs real API keys and a microphone, so it is run by the user with the orchestrator watching logs. Record results in the PR description.

**Setup:** `dotnet run --project src/SimpleWhisper/SimpleWhisper.csproj -c Release`. Add a custom vocabulary term that is unusual (e.g. "SimpleWhisper", "Kubernetes"). Open a text editor as the paste target.

| # | Engine / model | Steps | Expected |
|---|---|---|---|
| 1 | Backward compat | Before first launch, confirm the existing settings JSON has `"engine": "Cloud"` and an `openAIApiKeyProtected` value. Launch. | Settings shows OpenAI selected, key box populated, model = gpt-4o-mini-transcribe. |
| 2 | OpenAI gpt-4o-mini-transcribe | Record a 10 s sentence including a vocabulary term. | Text pasted within ~3 s; vocabulary term spelled correctly; overlay says "Transcribing with OpenAI gpt-4o-mini-transcribe (Cloud)...". |
| 3 | OpenAI whisper-1 | Switch model, record again. | Works; status shows whisper-1. |
| 4 | OpenAI, empty key | Clear the key, record. | Balloon "OpenAI API key is not configured…"; no crash. |
| 5 | Gemini | Paste Gemini key, select Gemini, record 10 s. | Text pasted; status shows "Gemini gemini-3.5-transcribe (Cloud)". |
| 6 | Gemini, bad key | Paste "invalid", record. | Failure message contains "Gemini API error (HTTP 400" or 403; no crash. |
| 7 | Groq turbo | Paste Groq key, record. | Text pasted, typically under 1.5 s. |
| 8 | Deepgram | Paste Deepgram key, record with the vocabulary term. | Text pasted; term spelled correctly (keyterm boost). |
| 9 | Auto-detect language | Enable auto-detect, say a Spanish sentence on Deepgram and on Gemini. | Spanish text returned on both. |
| 10 | Cancel mid-flight | On any cloud engine, press Esc during "Processing…". | Returns to idle within a second; log shows "Transcription was cancelled." |
| 11 | Local | Select Local, record. | Unchanged behaviour, streaming preview still works. |
| 12 | Restart persistence | Restart the app. | Engine, all four keys, and both model selections survive. |

Any failure goes back to the owning task's implementer model with the log excerpt.

---

## Self-review notes

- **Spec coverage:** engine enum + compat (T1), per-provider keys (T1, T6, T7), model selection (T1, T6, T7), OpenAI SDK upgrade and generalization (T2), Gemini (T3), Deepgram (T4), Groq via endpoint (T2, T5), routing and per-engine messages (T5), vocabulary array handling (T3, T4 via `SplitVocabularyPrompt`), UI (T7), App.xaml.cs untouched (spec), version/changelog (T8), manual matrix (T9).
- **Type consistency:** `UpdateConfiguration(string apiKey, string model)` on all three cloud service classes; `TranscriptionModelOption(Id, DisplayName)` used by catalog, VM, and XAML `SelectedValuePath="Id"`; `EngineOption(Engine, DisplayName)` matches `SelectedValuePath="Engine"`; `RestTranscriptionHttp.SplitVocabularyPrompt` referenced by T3, T4, T5 comment.
- **Ordering:** T6 intentionally leaves the build red until T7; the orchestrator should dispatch T6 and T7 back-to-back and only run the reviewer gate after T7 builds, or merge T6+T7 into one implementer if preferred.
