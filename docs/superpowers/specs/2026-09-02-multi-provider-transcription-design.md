# Multi-Provider Cloud Transcription — Design

**Date:** 2026-09-02
**Status:** Proposed
**Research:** `docs/superpowers/specs/2026-09-02-transcription-providers-research.md`

## Problem

SimpleWhisper offers exactly two engines: OpenAI `whisper-1` (hard-coded model) and local Whisper.net. Since whisper-1 shipped, OpenAI has released cheaper and more accurate models, Google has released a dedicated transcription model (Gemini 3.5 Transcribe), and several vendors offer faster or cheaper options. The user cannot pick any of them.

## Goals

1. Let the user choose among OpenAI, Google Gemini, Groq, Deepgram, and Local engines.
2. Let the user choose the model within OpenAI and Groq (Gemini and Deepgram expose one model each).
3. Store one API key per provider, DPAPI-encrypted like the existing OpenAI key.
4. Existing settings files keep working: an existing `"engine": "Cloud"` still means OpenAI.
5. Custom vocabulary and language hint are passed to every provider in its native form.
6. No new OAuth, service-account, or SDK-download flows. Every provider works from a pasted API key.

## Non-goals

- Streaming / realtime cloud transcription (local streaming is untouched).
- Speaker diarization, timestamps, subtitles.
- ElevenLabs, AssemblyAI, Mistral, Azure, Amazon (deferred; the provider abstraction makes them additive later).
- A test project. The repo has none; verification is `dotnet build` plus a manual matrix.

## Design

### Settings model

`TranscriptionEngine` gains three members. `Cloud` is kept as the OpenAI value so existing JSON deserializes unchanged.

```csharp
public enum TranscriptionEngine { Cloud, Local, Gemini, Groq, Deepgram }
```

New `AppSettings` properties (all with defaults so old files load):

| Property | JSON name | Default |
|---|---|---|
| `OpenAIModel` | `openAIModel` | `gpt-4o-mini-transcribe` |
| `GeminiModel` | `geminiModel` | `gemini-3.5-transcribe` |
| `GroqModel` | `groqModel` | `whisper-large-v3-turbo` |
| `DeepgramModel` | `deepgramModel` | `nova-3` |
| `EncryptedGeminiApiKey` / `GeminiApiKey` | `geminiApiKeyProtected` | empty |
| `EncryptedGroqApiKey` / `GroqApiKey` | `groqApiKeyProtected` | empty |
| `EncryptedDeepgramApiKey` / `DeepgramApiKey` | `deepgramApiKeyProtected` | empty |

Existing installs that had `whisper-1` implicitly will now default to `gpt-4o-mini-transcribe`. This is intentional: it is cheaper and more accurate, and the user can switch back in Settings.

A static `TranscriptionModelCatalog` lists the selectable models per engine (id, display label) so the view model and UI share one source of truth.

### Services

`ITranscriptionService` is unchanged.

- **`OpenAICompatibleTranscriptionService`** (renamed from `CloudTranscriptionService`). Constructor takes `(apiKey, model, endpoint?, engineName)`. Uses `OpenAIClientOptions.Endpoint` when an endpoint is supplied. Serves both OpenAI (no endpoint) and Groq (`https://api.groq.com/openai/v1`). `UpdateConfiguration(apiKey, model)` replaces `UpdateApiKey`.
- **`GeminiTranscriptionService`**. `HttpClient` POST to `https://generativelanguage.googleapis.com/v1beta/interactions` with inline base64 WAV, `generation_config.transcription_config` carrying `language_codes`, `custom_vocabulary`, and `mode: "smart"`. Parses `output_text` if present, otherwise the first `model_output` step's text content.
- **`DeepgramTranscriptionService`**. `HttpClient` POST of raw WAV bytes to `https://api.deepgram.com/v1/listen` with query parameters `model`, `smart_format=true`, `language` or `detect_language=true`, and repeated `keyterm`. Parses `results.channels[0].alternatives[0].transcript`.

Both REST services share one static `HttpClient` with a 60 s timeout, honour the cancellation token, and map failures to `TranscriptionResult.Failure` with the HTTP status in the message, mirroring the OpenAI service's error style.

### Vocabulary handling

`TranscriptionManager` keeps building the comma-separated prompt for OpenAI and Groq. For Gemini and Deepgram it passes the vocabulary list itself, because both take an array of terms rather than a free-text prompt. To avoid changing `ITranscriptionService`, the manager parses the prompt back into terms with a shared helper `RestTranscriptionHttp.SplitVocabularyPrompt(string?)`; the REST services call it. (The prompt is always built from the list with `", "` so the round-trip is lossless for terms without commas.)

### Routing

`TranscriptionManager.TranscribeAsync` switches on `settings.Engine` and lazily creates one service per engine. Per-engine "key not configured" messages replace the current OpenAI-only text. `EngineName` values follow the pattern `"OpenAI gpt-4o-mini-transcribe (Cloud)"`, `"Gemini gemini-3.5-transcribe (Cloud)"`, `"Groq whisper-large-v3-turbo (Cloud)"`, `"Deepgram nova-3 (Cloud)"`.

### Settings UI

The two radio buttons become a `ComboBox` of engines. Below it, one panel per cloud provider (visible only for the selected engine) with a `PasswordBox` for the key, a model `ComboBox` where the provider has more than one model, and a "Get an API key" hyperlink. The Local panel is unchanged. `SettingsViewModel` exposes `SelectedEngine`, `EngineOptions`, `IsOpenAIEngine`, `IsGeminiEngine`, `IsGroqEngine`, `IsDeepgramEngine`, `IsLocalEngine` (kept), per-provider key properties, and per-provider model properties. `IsCloudEngine` is removed; nothing outside the view model and XAML used it.

### App.xaml.cs

Unchanged: it already compares `settings.Engine == TranscriptionEngine.Local` for streaming and otherwise treats every other engine as non-streaming.

## Risks

- Gemini 3.5 Transcribe is in public preview; the Interactions API shape could change. The service isolates this in one file.
- Groq's OpenAI-compatible endpoint may reject unsupported optional fields. The service only sends `prompt` and `language`, both documented as supported.
- `gpt-4o-*` models return no `language` in the default JSON response; the service falls back to the requested language.
