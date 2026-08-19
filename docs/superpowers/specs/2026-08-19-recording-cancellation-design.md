# Recording / Processing Cancellation — Design

**Date:** 2026-08-19
**Status:** Approved

## Problem

An accidental trigger press (especially in toggle mode) silently starts recording. The
next trigger press — intended to *start* recording — actually stops the accidental one
and sends a long ambient-audio recording to transcription. While "Processing…" is shown,
the `_isProcessing` guard in `App.xaml.cs` ignores all trigger presses, and the
transcription call receives no cancellation token and has no timeout. A slow or hung
transcription leaves the app stuck on "Processing…" forever with no way out.

## Goals

1. Let the user cancel a recording or in-flight processing at any time.
2. Guarantee "Processing…" can never hang indefinitely.
3. A cancelled session fully resets state so the next trigger press works immediately.

## Non-goals

- No pipeline state-machine refactor.
- No change to trigger/recording-mode behavior.

## Design

### Cancel triggers (three, all routed to one code path)

1. **Esc key** — detected by the existing global keyboard hook in
   `InputTriggerService`. A new `CancelRequested` event fires on Esc key-down **only
   while a session is active** (recording or processing); otherwise Esc is ignored and
   never blocked from reaching other applications (observational hook, `Handling = false`).
   The app informs the service of session state via a new `IsSessionActive` boolean
   property (set true on trigger activation, false when the pipeline ends or is cancelled).
2. **Overlay ✕ button** — a small close button on the floating overlay, visible in the
   Recording and Processing states only. Click → same cancel path. The overlay window
   must accept mouse input for this button (it is currently click-through/no-activate;
   the button area must be hit-testable without stealing focus from the user's target app).
3. **90-second timeout** — `CancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(90))`
   armed when the UI enters the Processing state. Generous enough for long local-engine
   transcriptions.

### Session cancellation token (`App.xaml.cs`)

- A `CancellationTokenSource _pipelineCts` is created in `OnTriggerActivated` and
  disposed when the session ends (done, error, or cancelled).
- Its token is threaded through `HandleDeactivationAsync`,
  `RunTranscriptionPipelineAsync`, `RunStreamingFinalizePipelineAsync`, and into
  `TranscriptionManager.TranscribeAsync` / `TranscribeChunkAsync` (which already accept
  tokens down to the HTTP request and local Whisper run — cancellation genuinely aborts
  the work, not just the wait).
- `CancelPipeline(reason)` method:
  - Cancels `_pipelineCts` (streaming loop CTS is also cancelled if active).
  - Stops recording if in progress, **discarding** the audio.
  - Restores output volume, stops the silence-check timer.
  - Resets `_isProcessing` to 0 and `IsSessionActive` to false.
  - Overlay: brief "Cancelled" state (manual cancel) or "Timed out — try again"
    (timeout), auto-hiding like the existing Error state. Tray → Idle.
- Cancellation surfaces as `OperationCanceledException` in the pipeline; handlers catch
  it and show the appropriate message instead of the generic error path. Distinguishing
  manual vs. timeout: a `_cancelReason` field set before `Cancel()` is called; the
  timeout path is the default when the token fired without a manual reason.

### Overlay changes

- `OverlayViewModel`: new `SetCancelled(string message)` transition (auto-hide ~2s), and
  a `CancelCommand` (RelayCommand) invoked by the ✕ button; the command raises an event
  the app subscribes to.
- `OverlayWindow.xaml`: ✕ button styled to match, shown via trigger on State
  Recording/Processing.

### Edge cases

- **Cancel during recording:** audio discarded, processing never starts, overlay hides.
- **Cancel during text insertion:** not interrupted — paste is fast; the token is not
  checked after transcription succeeds.
- **Late results:** because the token aborts the underlying work, no cancelled
  transcription can paste text afterwards.
- **Esc while idle:** no-op, event not fired, key never suppressed.
- **Double cancel / cancel racing completion:** `CancelPipeline` and pipeline
  completion both funnel through `Interlocked` on `_isProcessing`; whichever wins,
  state resets exactly once and the loser observes the cancelled token or reset flag.

## Testing

Manual test matrix (no existing automated test project):

1. Toggle mode: accidental press → press again → cancel with Esc during Processing → next press records normally.
2. Esc during recording discards audio; nothing pasted.
3. ✕ click during recording and during processing.
4. Timeout: simulate a hang (e.g., no network with cloud engine) → error at ~90s.
5. Esc while idle does nothing and still works in other apps.
6. Normal happy-path record → paste unaffected; streaming (real-time) mode cancel mid-recording and mid-finalize.
