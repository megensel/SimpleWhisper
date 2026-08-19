# Recording / Processing Cancellation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user cancel a recording or stuck "Processing…" state via Esc, an overlay ✕ button, or an automatic 90-second timeout — all funneled through one per-session `CancellationTokenSource` that genuinely aborts the transcription work.

**Architecture:** `App.xaml.cs` owns a per-session CTS created on trigger activation and threaded into `TranscriptionManager.TranscribeAsync`/`TranscribeChunkAsync` (which already accept tokens down to the HTTP request / local Whisper run). Esc is detected by the existing global keyboard hook in `InputTriggerService` (new `CancelRequested` event, gated by an `IsSessionActive` flag). The overlay gets a ✕ button; because the window is click-through window-wide (`WS_EX_TRANSPARENT`), click-through is toggled off while Recording/Processing and `WS_EX_NOACTIVATE` is added so clicks never steal focus.

**Tech Stack:** .NET / C# / WPF, H.Hooks (global hooks), Win32 P/Invoke.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-08-19-recording-cancellation-design.md`.
- **No automated test project exists in this repo** (WPF app, global-hook orchestration). Verification per task = `dotnet build SimpleWhisper.sln` with zero errors/warnings-as-before, plus the manual test matrix in the final task. Do not add a test project.
- Esc must never be blocked/suppressed from other applications (keyboard hook stays `Handling = false`).
- Timeout: exactly 90 seconds, armed when the UI enters Processing.
- Text insertion (paste) is never interrupted once transcription has succeeded.
- Match existing code style: file-scoped namespaces, XML doc comments on public members, `AppLogger.Log` for diagnostics, section-divider comments.
- Commit after every task. Message style follows the repo's existing short imperative style (e.g. "Add X", "Fix Y").

---

### Task 1: Win32Helper — toggleable click-through + WS_EX_NOACTIVATE

**Files:**
- Modify: `src/SimpleWhisper/Helpers/Win32Helper.cs:96-113`

**Interfaces:**
- Consumes: nothing new.
- Produces: `public static void SetClickThrough(Window window, bool enabled)` — used by Task 3. `MakeWindowClickThrough` now also applies `WS_EX_NOACTIVATE`.

- [ ] **Step 1: Replace the helper-methods section**

Replace the two `MakeWindowClickThrough` methods (lines 96–113) with:

```csharp
    /// <summary>
    /// Configures a window to be click-through, preventing it from receiving any mouse input.
    /// Also applies the tool-window, layered, and no-activate extended styles so the window
    /// does not appear in the taskbar or Alt+Tab switcher and never steals focus.
    /// </summary>
    /// <param name="hwnd">The native window handle to modify.</param>
    public static void MakeWindowClickThrough(IntPtr hwnd)
    {
        int currentStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

        SetWindowLong(hwnd, GWL_EXSTYLE,
            currentStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
    }

    /// <summary>
    /// Convenience overload that accepts a WPF <see cref="Window"/> directly.
    /// Retrieves its handle via <see cref="WindowInteropHelper"/> and applies click-through styles.
    /// </summary>
    /// <param name="window">The WPF window to make click-through.</param>
    public static void MakeWindowClickThrough(Window window)
    {
        var hwnd = GetWindowHandle(window);
        MakeWindowClickThrough(hwnd);
    }

    /// <summary>
    /// Enables or disables the click-through (<see cref="WS_EX_TRANSPARENT"/>) style on a window
    /// at runtime, leaving the tool-window, layered, and no-activate styles in place. Used by the
    /// overlay to accept clicks on its cancel button only while a session is active.
    /// </summary>
    /// <param name="window">The WPF window to modify. No-op if its handle is not yet created.</param>
    /// <param name="enabled"><c>true</c> to make the window click-through; <c>false</c> to let it receive mouse input.</param>
    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
            return;

        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        style = enabled ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;

        SetWindowLong(hwnd, GWL_EXSTYLE,
            style | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build SimpleWhisper.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/SimpleWhisper/Helpers/Win32Helper.cs
git commit -m "Add toggleable click-through and no-activate style to Win32Helper"
```

---

### Task 2: OverlayState.Cancelled + OverlayViewModel cancel support

**Files:**
- Modify: `src/SimpleWhisper/Models/OverlayState.cs`
- Modify: `src/SimpleWhisper/ViewModels/OverlayViewModel.cs`

**Interfaces:**
- Consumes: `RelayCommand` (`SimpleWhisper.Helpers`), existing `ViewModelBase`.
- Produces (used by Tasks 3 and 5):
  - `OverlayState.Cancelled` enum member.
  - `public ICommand CancelCommand { get; }` — bound by the ✕ button.
  - `public event Action? CancelRequested;` — raised when CancelCommand executes.
  - `public void SetCancelled(string message)` — shows message, auto-hides after 2s.

- [ ] **Step 1: Add the enum member**

In `src/SimpleWhisper/Models/OverlayState.cs`, add after the `Error` member:

```csharp
    /// <summary>The user cancelled the recording or transcription.</summary>
    Cancelled
```

(Add a comma after `Error`.)

- [ ] **Step 2: Add command, event, and transition to OverlayViewModel**

In `src/SimpleWhisper/ViewModels/OverlayViewModel.cs`:

Add to the usings:

```csharp
using System.Windows.Input;
using SimpleWhisper.Helpers;
```

In the constructor, after the `_autoHideTimer` setup, add:

```csharp
        CancelCommand = new RelayCommand(() => CancelRequested?.Invoke());
```

Add a new section after the constructor:

```csharp
    // ──────────────────────────────────────────────────────────────────
    //  Cancellation
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Command bound to the overlay's ✕ button. Raises <see cref="CancelRequested"/>.
    /// </summary>
    public ICommand CancelCommand { get; }

    /// <summary>
    /// Raised when the user clicks the overlay's ✕ button to cancel the current
    /// recording or transcription. The application layer subscribes and cancels the pipeline.
    /// </summary>
    public event Action? CancelRequested;
```

Add a new state-transition method after `SetError`:

```csharp
    /// <summary>
    /// Transitions the overlay to the Cancelled state. The overlay auto-hides after 2 seconds.
    /// </summary>
    /// <param name="message">The message to display (e.g. "Cancelled").</param>
    public void SetCancelled(string message)
    {
        StopAutoHideTimer();
        State = OverlayState.Cancelled;
        StatusText = message;
        TranscribedText = string.Empty;
        AudioLevel = 0.0;
        AnimationOpacity = 1.0;
        IsVisible = true;
        StartAutoHideTimer(TimeSpan.FromSeconds(2));
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build SimpleWhisper.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/SimpleWhisper/Models/OverlayState.cs src/SimpleWhisper/ViewModels/OverlayViewModel.cs
git commit -m "Add Cancelled overlay state and cancel command to OverlayViewModel"
```

---

### Task 3: Overlay ✕ button + click-through toggling

**Files:**
- Modify: `src/SimpleWhisper/Views/OverlayWindow.xaml`
- Modify: `src/SimpleWhisper/Views/OverlayWindow.xaml.cs`

**Interfaces:**
- Consumes: `OverlayViewModel.CancelCommand` (Task 2), `Win32Helper.SetClickThrough(Window, bool)` (Task 1), `OverlayState.Cancelled` (Task 2).
- Produces: ✕ button visible in Recording/Processing states; window accepts clicks only in those states.

- [ ] **Step 1: Add a column and the ✕ button in XAML**

In `src/SimpleWhisper/Views/OverlayWindow.xaml`, inside `MainBar`'s `Grid.ColumnDefinitions` (after the Col 3 definition), add:

```xml
                    <!-- Col 4: Cancel button -->
                    <ColumnDefinition Width="Auto" />
```

After the closing `</Grid>` of the Column-3 grid (the audio-level/text grid ending at line ~240) and before the closing `</Grid>` of the main content grid, add:

```xml
                <!-- ────────────────────────────────────────────────
                     Column 4: Cancel (✕) button
                     ──────────────────────────────────────────────── -->
                <Button Grid.Column="4"
                        Command="{Binding CancelCommand}"
                        Width="22" Height="22"
                        Margin="8,0,0,0"
                        VerticalAlignment="Center"
                        Cursor="Hand"
                        Focusable="False"
                        ToolTip="Cancel (Esc)">
                    <Button.Style>
                        <Style TargetType="Button">
                            <Setter Property="Visibility" Value="Collapsed" />
                            <Setter Property="Background" Value="Transparent" />
                            <Setter Property="BorderThickness" Value="0" />
                            <Setter Property="Foreground" Value="#CCFFFFFF" />
                            <Setter Property="Template">
                                <Setter.Value>
                                    <ControlTemplate TargetType="Button">
                                        <Border x:Name="ButtonBorder"
                                                Background="{TemplateBinding Background}"
                                                CornerRadius="4">
                                            <TextBlock Text="✕"
                                                       FontSize="12"
                                                       Foreground="{TemplateBinding Foreground}"
                                                       HorizontalAlignment="Center"
                                                       VerticalAlignment="Center" />
                                        </Border>
                                        <ControlTemplate.Triggers>
                                            <Trigger Property="IsMouseOver" Value="True">
                                                <Setter TargetName="ButtonBorder" Property="Background" Value="#33FFFFFF" />
                                            </Trigger>
                                        </ControlTemplate.Triggers>
                                    </ControlTemplate>
                                </Setter.Value>
                            </Setter>
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding State}" Value="{x:Static models:OverlayState.Recording}">
                                    <Setter Property="Visibility" Value="Visible" />
                                </DataTrigger>
                                <DataTrigger Binding="{Binding State}" Value="{x:Static models:OverlayState.Processing}">
                                    <Setter Property="Visibility" Value="Visible" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Button.Style>
                </Button>
```

- [ ] **Step 2: Toggle click-through on state changes in code-behind**

In `src/SimpleWhisper/Views/OverlayWindow.xaml.cs`, at the end of `HandleStateChange()` (after `RepositionWindow();`), add:

```csharp
        // Accept mouse input (for the ✕ cancel button) only while a session is active;
        // stay click-through in every other state so clicks pass to windows below.
        bool sessionActive = newState is OverlayState.Recording or OverlayState.Processing;
        Win32Helper.SetClickThrough(this, enabled: !sessionActive);
```

- [ ] **Step 3: Build**

Run: `dotnet build SimpleWhisper.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/SimpleWhisper/Views/OverlayWindow.xaml src/SimpleWhisper/Views/OverlayWindow.xaml.cs
git commit -m "Add cancel button to overlay with state-based click-through toggling"
```

---

### Task 4: InputTriggerService — Esc detection + session flag + activation reset

**Files:**
- Modify: `src/SimpleWhisper/Services/InputTriggerService.cs`

**Interfaces:**
- Consumes: existing keyboard hook plumbing.
- Produces (used by Task 5):
  - `public event Action? CancelRequested;` — fired on Esc key-down while `IsSessionActive`.
  - `public bool IsSessionActive { get; set; }` — set by the app on session start/end.
  - `public void ResetActivation()` — clears `_isPressed`/`_isActive` without raising events (so a cancelled toggle-mode session doesn't leave the service thinking it's still recording).

- [ ] **Step 1: Add state, event, and public members**

After the `_isPressed`/`_isActive` field declarations (near line 38), add:

```csharp
    // True while the application has an active recording/processing session.
    // Set by the app layer; gates Esc-to-cancel detection.
    private volatile bool _isSessionActive;
```

After the `TriggerDeactivated` event declaration, add:

```csharp
    /// <summary>
    /// Raised when the user presses Esc while a recording or processing session is active
    /// (see <see cref="IsSessionActive"/>). Fires on a thread-pool thread.
    /// The Esc key press is never suppressed and still reaches other applications.
    /// </summary>
    public event Action? CancelRequested;

    /// <summary>
    /// Gets or sets whether a recording/processing session is currently active.
    /// While <c>true</c>, pressing Esc raises <see cref="CancelRequested"/>.
    /// Set by the application layer at session start/end.
    /// </summary>
    public bool IsSessionActive
    {
        get => _isSessionActive;
        set => _isSessionActive = value;
    }
```

In the Public API section (after `UpdateMode`), add:

```csharp
    /// <summary>
    /// Clears the internal pressed/active trigger state without raising events.
    /// Called when the application cancels a session externally (e.g. Esc or the overlay ✕),
    /// so that in toggle mode the next trigger press starts a new recording instead of
    /// being interpreted as "stop".
    /// </summary>
    public void ResetActivation()
    {
        _isPressed = false;
        _isActive = false;
    }
```

- [ ] **Step 2: Detect Esc in OnKeyboardDown**

In `OnKeyboardDown`, immediately after the `UpdateModifierState(e.CurrentKey, pressed: true);` line, add:

```csharp
        // Esc cancels the active session (recording or processing). Observational only —
        // the hook never blocks the key, so Esc still works normally in other apps.
        if (e.CurrentKey == HKey.Escape && _isSessionActive)
        {
            RaiseCancelRequested();
            return;
        }
```

- [ ] **Step 3: Add the event-raising helper**

Next to `RaiseTriggerDeactivated()`, add:

```csharp
    private void RaiseCancelRequested()
    {
        try
        {
            CancelRequested?.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[InputTriggerService] CancelRequested handler threw: {ex}");
        }
    }
```

- [ ] **Step 4: Reset the session flag in ResetState**

In `ResetState()`, add `_isSessionActive = false;` alongside the existing resets.

- [ ] **Step 5: Build**

Run: `dotnet build SimpleWhisper.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/SimpleWhisper/Services/InputTriggerService.cs
git commit -m "Add Esc-to-cancel detection and session state to InputTriggerService"
```

---

### Task 5: App.xaml.cs — session CTS, CancelPipeline, and wiring

**Files:**
- Modify: `src/SimpleWhisper/App.xaml.cs`

**Interfaces:**
- Consumes: `InputTriggerService.CancelRequested` / `.IsSessionActive` / `.ResetActivation()` (Task 4), `OverlayViewModel.CancelRequested` / `.SetCancelled(string)` (Task 2).
- Produces (used by Task 6): fields `_pipelineCts` (`CancellationTokenSource?`), `_cancelReason` (`CancelReason` enum: `None`/`Manual`/`Timeout`); methods `RequestCancel()`, `EndSession()`; constant `ProcessingTimeoutSeconds = 90`.

- [ ] **Step 1: Add fields and constants**

In `App.xaml.cs`, after the `_streamingTask` field declaration (near line 61), add:

```csharp
    /// <summary>
    /// Why the current session's cancellation token fired. Set to <see cref="CancelReason.Manual"/>
    /// before a user-initiated cancel; if the token fires while this is still
    /// <see cref="CancelReason.None"/>, the 90-second processing timeout caused it.
    /// </summary>
    private enum CancelReason { None, Manual, Timeout }

    /// <summary>
    /// Cancellation source covering the whole recording→transcription session.
    /// Created in <see cref="OnTriggerActivated"/>; cancelled by Esc, the overlay ✕ button,
    /// or the processing timeout; disposed in <see cref="EndSession"/>.
    /// </summary>
    private CancellationTokenSource? _pipelineCts;

    /// <summary>
    /// See <see cref="CancelReason"/>. Reset to None at the start of each session.
    /// </summary>
    private volatile CancelReason _cancelReason;

    /// <summary>
    /// Safety-net timeout for the Processing state. If transcription hasn't finished
    /// this many seconds after processing starts, the session is cancelled.
    /// </summary>
    private const int ProcessingTimeoutSeconds = 90;
```

(`volatile` is valid on `CancelReason` because its underlying type is `int`; C# permits `volatile` on enum fields with integral underlying types.)

- [ ] **Step 2: Wire cancel sources in OnStartup**

In `OnStartup`, after the two existing `_inputTriggerService` event subscriptions (line ~152), add:

```csharp
        _inputTriggerService.CancelRequested += RequestCancel;
```

After `_overlayWindow.Show();` (line ~144) — note the overlay is created before the trigger service — add:

```csharp
        _overlayWindow.ViewModel.CancelRequested += RequestCancel;
```

- [ ] **Step 3: Start the session in OnTriggerActivated**

In `OnTriggerActivated`, inside the `try` block, immediately after `_audioCaptureService.StartRecording(deviceIndex);`, add:

```csharp
            // Begin a cancellable session covering recording + transcription.
            _cancelReason = CancelReason.None;
            _pipelineCts?.Dispose();
            _pipelineCts = new CancellationTokenSource();
            _inputTriggerService!.IsSessionActive = true;
```

- [ ] **Step 4: Add RequestCancel and EndSession**

Add a new section after `HandleDeactivationAsync` (before the Real-time streaming section):

```csharp
    // ──────────────────────────────────────────────────────────────────
    //  Session cancellation (Esc, overlay ✕, processing timeout)
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cancels the active session. Called from the Esc hook (thread-pool thread) and the
    /// overlay ✕ button (UI thread). If recording is in progress the audio is discarded;
    /// if processing is in progress the cancellation token aborts the transcription and
    /// the pipeline's cancellation handler finishes the UI reset.
    /// </summary>
    private void RequestCancel()
    {
        var cts = _pipelineCts;
        if (cts is null)
            return;

        AppLogger.Log(">>> Cancel requested (Esc or overlay button).");
        _cancelReason = CancelReason.Manual;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* Session ended between the null check and Cancel. */ }

        _streamingCts?.Cancel();

        // Clear the trigger service's internal state so (in toggle mode) the next
        // press starts a new recording instead of acting as "stop".
        _inputTriggerService?.ResetActivation();

        // If we're still recording (cancel happened before trigger deactivation),
        // stop the mic, discard the audio, and reset the UI here — no pipeline is
        // running yet to do it for us.
        if (_audioCaptureService is not null && _audioCaptureService.IsRecording)
        {
            try
            {
                _ = _audioCaptureService.StopRecording(); // Discard audio.
            }
            catch (Exception ex)
            {
                AppLogger.Log("Error stopping recording during cancel", ex);
            }

            _outputVolumeService?.RestoreVolume();

            Dispatcher.BeginInvoke(() =>
            {
                StopSilenceCheckTimer();
                _overlayWindow?.ViewModel.SetCancelled("Cancelled");
                Tray?.SetState(TrayState.Idle);
            });

            EndSession();
        }
        // Else: processing is in flight. The cancelled token surfaces as an
        // OperationCanceledException in the pipeline, whose handler resets the UI
        // and calls EndSession.
    }

    /// <summary>
    /// Ends the current session: clears the trigger service's session flag and disposes
    /// the session cancellation source. Safe to call multiple times.
    /// </summary>
    private void EndSession()
    {
        if (_inputTriggerService is not null)
            _inputTriggerService.IsSessionActive = false;

        _pipelineCts?.Dispose();
        _pipelineCts = null;
    }
```

- [ ] **Step 5: End the session on the OnTriggerActivated failure path**

In `OnTriggerActivated`'s `catch` block, after the `SetError` dispatch, add:

```csharp
            EndSession();
```

- [ ] **Step 6: Build**

Run: `dotnet build SimpleWhisper.sln`
Expected: Build succeeded, 0 errors. (The token isn't consumed by the pipeline yet — that's Task 6. `EndSession` is not yet called from the pipeline paths; also Task 6.)

- [ ] **Step 7: Commit**

```bash
git add src/SimpleWhisper/App.xaml.cs
git commit -m "Add session cancellation source and cancel wiring to App"
```

---

### Task 6: Thread the token through the pipeline + timeout + cancelled UI

**Files:**
- Modify: `src/SimpleWhisper/App.xaml.cs`

**Interfaces:**
- Consumes: everything from Task 5; `TranscriptionManager.TranscribeAsync(byte[], CancellationToken)` and `.TranscribeChunkAsync(byte[], string?, CancellationToken)` (existing signatures — no changes needed in TranscriptionManager).
- Produces: fully cancellable pipeline; 90s timeout; "Cancelled" / "Timed out — try again" overlay messages; session cleanup on every exit path.

- [ ] **Step 1: Capture the token at deactivation and pass it down**

In `OnTriggerDeactivated`, replace:

```csharp
        // The rest involves async work (waiting for streaming loop, transcribing the tail).
        _ = HandleDeactivationAsync(wasStreaming).ContinueWith(
            t => AppLogger.Log("HandleDeactivationAsync faulted", t.Exception!.InnerException ?? t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
```

with:

```csharp
        // The rest involves async work (waiting for streaming loop, transcribing the tail).
        CancellationToken sessionToken = _pipelineCts?.Token ?? CancellationToken.None;
        _ = HandleDeactivationAsync(wasStreaming, sessionToken).ContinueWith(
            t => AppLogger.Log("HandleDeactivationAsync faulted", t.Exception!.InnerException ?? t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
```

- [ ] **Step 2: Rework HandleDeactivationAsync**

Replace the entire `HandleDeactivationAsync` method with:

```csharp
    /// <summary>
    /// Async continuation of <see cref="OnTriggerDeactivated"/>. Waits for the streaming
    /// loop to finish (if active), stops recording, and runs the appropriate pipeline.
    /// The <paramref name="sessionToken"/> cancels transcription work when the user
    /// cancels (Esc / overlay ✕) or the processing timeout fires.
    /// </summary>
    private async Task HandleDeactivationAsync(bool wasStreaming, CancellationToken sessionToken)
    {
        try
        {
            // Wait for the streaming transcription loop to finish its current iteration.
            if (wasStreaming)
            {
                try
                {
                    if (_streamingTask is not null)
                        await _streamingTask.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (OperationCanceledException) { }
                catch (TimeoutException)
                {
                    AppLogger.Log("Streaming loop timed out during shutdown.");
                }
                finally
                {
                    _streamingCts?.Dispose();
                    _streamingCts = null;
                    _streamingTask = null;
                }
            }

            // If the user cancelled while we were shutting down the streaming loop,
            // the cancel path already stopped the mic and reset the UI — just bail.
            // (Also guards the race where Esc and trigger release arrive together.)
            if (sessionToken.IsCancellationRequested || !_audioCaptureService!.IsRecording)
            {
                AppLogger.Log("Session cancelled before processing started. Discarding audio.");
                Interlocked.Exchange(ref _isProcessing, 0);
                EndSession();
                return;
            }

            // Stop recording and get complete WAV data.
            byte[] wavData = _audioCaptureService.StopRecording();

            AppLogger.Log($"Recording stopped. WAV size: {wavData.Length} bytes.");

            if (wavData.Length < MinimumWavBytes)
            {
                AppLogger.Log($"Audio too short ({wavData.Length} bytes < {MinimumWavBytes} minimum). Skipping transcription.");
                Interlocked.Exchange(ref _isProcessing, 0);
                EndSession();
                await Dispatcher.BeginInvoke(() =>
                {
                    _overlayWindow?.ViewModel.SetIdle();
                    Tray?.SetState(TrayState.Idle);
                });
                return;
            }

            // Check if the recording was essentially silent (mic muted or disconnected).
            if (_audioCaptureService.PeakAudioLevel < SilenceThreshold)
            {
                AppLogger.Log($"Recording appears silent (peak level: {_audioCaptureService.PeakAudioLevel:F4}). Mic may be muted.");
                Interlocked.Exchange(ref _isProcessing, 0);
                EndSession();
                await Dispatcher.BeginInvoke(() =>
                {
                    _overlayWindow?.ViewModel.SetError("No audio detected — is your mic muted?");
                    Tray?.SetState(TrayState.Idle);
                    Tray?.ShowBalloonTip("No Audio Detected",
                        "Your recording was silent. Check that your microphone is unmuted and working.",
                        H.NotifyIcon.Core.NotificationIcon.Warning);
                });
                return;
            }

            // Update UI to processing state and arm the safety-net timeout.
            await Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow?.ViewModel.SetProcessing();
                Tray?.SetState(TrayState.Processing);
            });

            try
            {
                _pipelineCts?.CancelAfter(TimeSpan.FromSeconds(ProcessingTimeoutSeconds));
            }
            catch (ObjectDisposedException) { /* Session already ended. */ }

            if (wasStreaming)
            {
                // Streaming path: transcribe only the remaining un-chunked audio tail,
                // then combine with the text accumulated during recording.
                await RunStreamingFinalizePipelineAsync(wavData, sessionToken);
            }
            else
            {
                // Standard path: transcribe the full recording at once.
                await RunTranscriptionPipelineAsync(wavData, sessionToken);
            }
        }
        catch (OperationCanceledException)
        {
            HandlePipelineCancelled();
        }
        catch (Exception ex)
        {
            AppLogger.Log("Error in deactivation handler", ex);
            await Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow?.ViewModel.SetError($"Error: {ex.Message}");
                Tray?.SetState(TrayState.Idle);
            });
            Interlocked.Exchange(ref _isProcessing, 0);
            EndSession();
        }
    }

    /// <summary>
    /// Shared handler for a cancelled pipeline: resets state and shows either
    /// "Cancelled" (manual) or a timeout error on the overlay.
    /// </summary>
    private void HandlePipelineCancelled()
    {
        bool manual = _cancelReason == CancelReason.Manual;
        AppLogger.Log(manual
            ? "Pipeline cancelled by user."
            : $"Pipeline cancelled by {ProcessingTimeoutSeconds}s processing timeout.");

        Interlocked.Exchange(ref _isProcessing, 0);
        EndSession();

        Dispatcher.BeginInvoke(() =>
        {
            if (manual)
            {
                _overlayWindow?.ViewModel.SetCancelled("Cancelled");
            }
            else
            {
                _overlayWindow?.ViewModel.SetError("Timed out — try again.");
            }
            Tray?.SetState(TrayState.Idle);
        });
    }
```

- [ ] **Step 3: Rework RunTranscriptionPipelineAsync**

Replace the method with:

```csharp
    /// <summary>
    /// Runs the full transcription pipeline: transcribe → text process → insert.
    /// Executes on a background thread to keep the UI responsive.
    /// Cancellation (user or timeout) surfaces as <see cref="OperationCanceledException"/>
    /// and is handled by <see cref="HandlePipelineCancelled"/>.
    /// </summary>
    private async Task RunTranscriptionPipelineAsync(byte[] wavData, CancellationToken cancellationToken)
    {
        try
        {
            // Step 1: Transcribe (aborts the underlying HTTP request / local run on cancel).
            var result = await _transcriptionManager!.TranscribeAsync(wavData, cancellationToken);

            if (!result.IsSuccess)
            {
                AppLogger.Log($"Transcription failed: {result.ErrorMessage}");

                // Show a friendlier error in the overlay (full details go to log file).
                string shortError = SimplifyErrorMessage(result.ErrorMessage ?? "Transcription failed");
                await Dispatcher.BeginInvoke(() =>
                {
                    _overlayWindow?.ViewModel.SetError(shortError);
                    Tray?.SetState(TrayState.Idle);
                    Tray?.ShowBalloonTip("Transcription Failed", shortError,
                        H.NotifyIcon.Core.NotificationIcon.Error);
                });
                Interlocked.Exchange(ref _isProcessing, 0);
                EndSession();
                return;
            }

            string rawText = result.Text;
            AppLogger.Log($"Raw transcription: \"{rawText}\"");

            // Step 2-4: Process text, insert via clipboard, update UI.
            // Deliberately NOT cancellable — once transcription succeeded, the paste completes.
            await ProcessAndInsertTextAsync(rawText);
            Interlocked.Exchange(ref _isProcessing, 0);
            EndSession();
        }
        catch (OperationCanceledException)
        {
            HandlePipelineCancelled();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Pipeline error", ex);
            await Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow?.ViewModel.SetError($"Error: {ex.Message}");
                Tray?.SetState(TrayState.Idle);
            });
            Interlocked.Exchange(ref _isProcessing, 0);
            EndSession();
        }
    }
```

(Note: the previous `finally { Interlocked.Exchange(ref _isProcessing, 0); }` is replaced by explicit resets on each path so that `HandlePipelineCancelled` owns the reset on the cancel path — it must reset before `EndSession` disposes the CTS.)

- [ ] **Step 4: Rework RunStreamingFinalizePipelineAsync**

Replace the method with:

```csharp
    /// <summary>
    /// Finalizes a real-time streaming session: transcribes any remaining audio that
    /// wasn't covered by the chunked loop, concatenates with accumulated text,
    /// then processes and pastes. Cancellation surfaces as
    /// <see cref="OperationCanceledException"/> and is handled by <see cref="HandlePipelineCancelled"/>.
    /// </summary>
    private async Task RunStreamingFinalizePipelineAsync(byte[] fullWavData, CancellationToken cancellationToken)
    {
        try
        {
            // Transcribe the remaining audio tail (if any).
            byte[] remaining = _audioCaptureService!.GetRemainingAudio(fullWavData);

            if (remaining.Length > 0)
            {
                AppLogger.Log($"Transcribing remaining audio tail: {remaining.Length} bytes.");

                string? lastContext = string.IsNullOrEmpty(_accumulatedStreamingText)
                    ? null
                    : GetTrailingWords(_accumulatedStreamingText, 30);

                var tailResult = await _transcriptionManager!.TranscribeChunkAsync(
                    remaining, lastContext, cancellationToken);

                if (tailResult.IsSuccess && !string.IsNullOrWhiteSpace(tailResult.Text))
                {
                    _accumulatedStreamingText = string.IsNullOrEmpty(_accumulatedStreamingText)
                        ? tailResult.Text
                        : $"{_accumulatedStreamingText} {tailResult.Text}";

                    AppLogger.Log($"Tail transcribed: \"{tailResult.Text}\"");
                }
            }

            string rawText = _accumulatedStreamingText.Trim();
            AppLogger.Log($"Full streaming transcription: \"{rawText}\"");

            // Process text, insert via clipboard, update UI.
            // Deliberately NOT cancellable — once transcription succeeded, the paste completes.
            await ProcessAndInsertTextAsync(rawText);
            Interlocked.Exchange(ref _isProcessing, 0);
            EndSession();
        }
        catch (OperationCanceledException)
        {
            HandlePipelineCancelled();
        }
        catch (Exception ex)
        {
            AppLogger.Log("Streaming finalize error", ex);
            await Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow?.ViewModel.SetError($"Error: {ex.Message}");
                Tray?.SetState(TrayState.Idle);
            });
            Interlocked.Exchange(ref _isProcessing, 0);
            EndSession();
        }
    }
```

- [ ] **Step 5: Dispose _pipelineCts on exit**

In `OnExit`, after `_streamingCts?.Dispose();`, add:

```csharp
        _pipelineCts?.Dispose();
```

- [ ] **Step 6: Build**

Run: `dotnet build SimpleWhisper.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/SimpleWhisper/App.xaml.cs
git commit -m "Thread session cancellation through pipeline with 90s processing timeout"
```

---

### Task 7: Full build, manual test matrix, changelog

**Files:**
- Modify: `CHANGELOG.md` (add an Unreleased entry following the file's existing format)

**Interfaces:**
- Consumes: everything above.
- Produces: verified feature; changelog entry.

- [ ] **Step 1: Release build**

Run: `dotnet build SimpleWhisper.sln -c Release`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Manual test matrix**

Run the app (`dotnet run --project src/SimpleWhisper` or the built exe) and verify each row. These require a human at the machine — if executing as an agent, present this list to the user for sign-off rather than skipping it:

1. Toggle mode: press trigger (recording starts) → press again (Processing) → **Esc** → overlay shows "Cancelled", hides ~2s later → next trigger press records normally.
2. **Esc during recording**: recording stops, nothing pasted, overlay "Cancelled".
3. **✕ click during recording** and **✕ click during processing**: same as Esc. Confirm the click does not steal focus from the foreground app (type immediately after — caret still in your editor).
4. **Click-through when idle/done**: after the overlay hides, clicks in that screen region reach the window below.
5. **Timeout**: cloud engine + disconnect network (or firewall the app) → record → Processing shows → after ~90s overlay shows "Timed out — try again."
6. **Esc while idle**: nothing happens in SimpleWhisper; Esc still works in other apps (e.g. closes a dialog).
7. **Happy path regression**: normal record → transcribe → paste still works, in both standard and real-time (streaming) mode.
8. Streaming mode: Esc mid-recording and Esc during finalize both cancel cleanly.

- [ ] **Step 3: Update CHANGELOG.md**

Add at the top, following the existing entry format:

```markdown
## Unreleased

### Added
- Cancel a recording or in-flight transcription at any time with **Esc** or the new **✕ button** on the overlay.
- Automatic 90-second timeout so "Processing…" can never hang forever.

### Fixed
- An accidental recording could leave the app stuck on "Processing…" indefinitely, ignoring all further trigger presses.
```

(If the file uses version headers only, place this under an `## Unreleased` header above the newest version.)

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md
git commit -m "Add changelog entry for recording cancellation"
```
