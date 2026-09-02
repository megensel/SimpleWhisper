# Transcription API Landscape — Research Report

**Date:** 2026-09-02
**Purpose:** Decide which cloud speech-to-text providers to add to SimpleWhisper alongside OpenAI `whisper-1` and local Whisper.net.
**Use case:** Push-to-talk dictation. Clips are 2–60 s of 16 kHz mono 16-bit WAV. The user pastes an API key in Settings. Priorities: accuracy, latency on short clips, cost, and integration simplicity (plain API key, no OAuth or service accounts).

Research was carried out by three parallel agents (Google, OpenAI, third-party) and the key API shapes were then verified directly against vendor documentation.

---

## 1. Google (the newest option)

Google's newest transcription model is **Gemini 3.5 Transcribe**, announced 2026-08-26 and served from the **Gemini API** (AI Studio), not from Cloud Speech-to-Text.

| Item | Value | Source |
|---|---|---|
| Model ids | `gemini-3.5-transcribe` (unary), `gemini-3.5-transcribe-live` (WebSocket) | https://ai.google.dev/gemini-api/docs/models/gemini-3.5-transcribe |
| Endpoint | `POST https://generativelanguage.googleapis.com/v1beta/interactions` | https://ai.google.dev/gemini-api/docs/transcribe |
| Auth | Header `x-goog-api-key: <key>` (plain API key) | same |
| Audio input | Inline base64 in `input[].data` with `mime_type` (`audio/wav` supported), or a Files API `uri` | https://ai.google.dev/api/interactions-api |
| Options | `generation_config.transcription_config.{language_codes[], custom_vocabulary[], mode}`; `mode: "smart"` removes disfluencies | https://ai.google.dev/gemini-api/docs/transcribe |
| Limits | 1 hour per request (30 min with diarization/timestamps); vocabulary up to 1,000 terms, best ≤100 | same |
| Price | ~$0.005 per audio minute blended ($2/M audio-input tokens, $12/M output tokens) | https://ai.google.dev/gemini-api/docs/pricing |
| Accuracy | ~2.6% WER reported on benchmark sets | third-party write-ups |

Cloud Speech-to-Text v2 (`chirp_3`, released May 2026) was rejected: it costs ~$0.016/min and requires a service account / Application Default Credentials, which does not fit a paste-a-key desktop app.

**Decision:** integrate `gemini-3.5-transcribe` via raw `HttpClient` REST. No official Google .NET SDK for the Gemini API exists; REST with one JSON body is the simplest path.

---

## 2. OpenAI (upgrade the existing path)

| Model | $/min | Prompt | Language | Notes |
|---|---|---|---|---|
| `whisper-1` | $0.006 | yes | yes | Legacy. Only model with `verbose_json`, srt/vtt, word timestamps. Weakest accuracy. |
| `gpt-4o-transcribe` | $0.006 | yes | yes | OpenAI's recommended default; markedly better WER than whisper-1, especially non-English. |
| `gpt-4o-mini-transcribe` | ~$0.003 | yes | yes | Cheapest OpenAI model; still beats whisper-1 in most reports. |
| `gpt-4o-transcribe-diarize` | $0.006 | **no** | yes | Speaker labels only. Not useful for single-speaker dictation. |
| Realtime API transcription | ~$0.017+ | n/a | n/a | WebSocket streaming. More cost and complexity than posting the finished clip. Rejected. |

Sources: https://developers.openai.com/api/docs/guides/speech-to-text, https://github.com/openai/openai-dotnet/blob/main/CHANGELOG.md, https://www.nuget.org/packages/OpenAI

**SDK:** The repo pins `OpenAI` NuGet **2.9.1**. Current release is **2.13.0** (verified against the NuGet flat container index on 2026-09-02). Newer models are drop-in with `GetAudioClient("<model>")` and the same `TranscribeAudioAsync` call; `Prompt` and `Language` are still honoured. Custom endpoints are set with `OpenAIClientOptions.Endpoint`.

**Decision:** upgrade to 2.13.0, make the model selectable, default new installs to `gpt-4o-mini-transcribe`, keep `whisper-1` as an option.

---

## 3. Third-party providers

| Provider | Model | Price | Accuracy signal | Key terms | Auth | OpenAI-compatible endpoint |
|---|---|---|---|---|---|---|
| **Groq** | `whisper-large-v3-turbo` | $0.04/hr (~$0.0007/min) | Whisper large-v3-turbo weights, extremely fast | prompt only | Bearer key | **Yes** — `https://api.groq.com/openai/v1` |
| **Deepgram** | `nova-3` | ~$0.0043/min | Strong, widely benchmarked | `keyterm` query param (Nova-3) | `Authorization: Token <key>` | No (native REST is a single POST) |
| ElevenLabs | Scribe v2 | ~$0.0037/min | Best hosted WER on Artificial Analysis (2.2%) | keyterms (+$0.05/hr) | key | No |
| AssemblyAI | Universal-3 Pro | $0.15/hr | Top-3 on Artificial Analysis | `keyterms_prompt` | key | No |
| Mistral | Voxtral Mini Transcribe V2 | $0.003/min | Vendor claims beat 4o-mini/Deepgram | prompt-style | key | Partial |
| Azure Speech | batch | ~$0.006/min | enterprise-grade | phrase lists | Azure resource key | No |
| Amazon Transcribe | standard | $0.024/min | solid | vocabulary | SigV4 | No |

Sources: https://console.groq.com/docs/speech-to-text, https://developers.deepgram.com/docs/pre-recorded-audio, https://developers.deepgram.com/docs/keyterm, https://elevenlabs.io/pricing/api, https://artificialanalysis.ai/speech-to-text/non-streaming

Verified Deepgram request:

```
POST https://api.deepgram.com/v1/listen?model=nova-3&smart_format=true&language=en&keyterm=Term1&keyterm=Term+Two
Authorization: Token <key>
Content-Type: audio/wav
<raw WAV bytes>
→ results.channels[0].alternatives[0].transcript
```

**Decision:** add **Groq** (near-zero cost, reuses the OpenAI SDK with a custom endpoint) and **Deepgram Nova-3** (best accuracy-per-dollar with real key-term boosting, one-call REST). ElevenLabs Scribe v2 and AssemblyAI are deferred to a follow-up; both need a vendor-specific multipart client and add little beyond Deepgram for this use case.

---

## 4. Final provider matrix for v1.5.0

| Engine | Model(s) exposed | Approx $/min | Integration |
|---|---|---|---|
| OpenAI | `gpt-4o-mini-transcribe` (default), `gpt-4o-transcribe`, `whisper-1` | 0.003–0.006 | existing SDK, upgraded |
| Google Gemini | `gemini-3.5-transcribe` | 0.005 | new REST service |
| Groq | `whisper-large-v3-turbo`, `whisper-large-v3` | 0.0007–0.002 | OpenAI SDK + custom endpoint |
| Deepgram | `nova-3` | 0.0043 | new REST service |
| Local | Whisper.net (unchanged) | 0 | unchanged |

A 30-second dictation costs well under a cent on every cloud option; the practical differentiators are accuracy and latency, which the user can now compare in-app.
