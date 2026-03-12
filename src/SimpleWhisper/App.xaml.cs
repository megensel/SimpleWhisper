using System.Windows;
using System.Windows.Threading;
using SimpleWhisper.Helpers;
using SimpleWhisper.Models;
using SimpleWhisper.Services;
using SimpleWhisper.Views;

namespace SimpleWhisper;

/// <summary>
/// Application entry point. The app starts minimized to the system tray — there is no main window.
/// This class also acts as the orchestration layer, wiring together all services for the
/// hotkey → record → transcribe → process → paste pipeline.
/// </summary>
public partial class App : Application
{
    // ──────────────────────────────────────────────────────────────────
    //  Static singletons accessible throughout the application
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Application-wide settings service. Initialized during <see cref="OnStartup"/>.
    /// </summary>
    public static SettingsService Settings { get; private set; } = null!;

    /// <summary>
    /// System tray icon manager. Initialized during <see cref="OnStartup"/>.
    /// </summary>
    public static TrayManager Tray { get; private set; } = null!;

    // ──────────────────────────────────────────────────────────────────
    //  Private service instances
    // ──────────────────────────────────────────────────────────────────

    private InputTriggerService? _inputTriggerService;
    private AudioCaptureService? _audioCaptureService;
    private TranscriptionManager? _transcriptionManager;
    private TextInsertionService? _textInsertionService;
    private OverlayWindow? _overlayWindow;

    /// <summary>
    /// Guard to prevent concurrent recording pipeline executions.
    /// </summary>
    private volatile bool _isProcessing;

    /// <summary>
    /// Cancellation source for the real-time transcription loop that runs during recording.
    /// Created in <see cref="OnTriggerActivated"/> and cancelled in <see cref="OnTriggerDeactivated"/>.
    /// </summary>
    private CancellationTokenSource? _streamingCts;

    /// <summary>
    /// Background task running the chunked transcription loop during recording.
    /// </summary>
    private Task? _streamingTask;

    /// <summary>
    /// Text accumulated from real-time transcription chunks during the current recording.
    /// Reset at the start of each recording session.
    /// </summary>
    private string _accumulatedStreamingText = string.Empty;

    /// <summary>
    /// Interval between real-time transcription chunk extractions.
    /// </summary>
    private static readonly TimeSpan StreamingChunkInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Minimum WAV file size to bother sending to transcription.
    /// At 16 kHz / 16-bit / mono = 32,000 bytes/sec, this is ~0.3 seconds of audio
    /// plus the 44-byte WAV header. Below this threshold the audio is too short
    /// for meaningful transcription and will be rejected by the API.
    /// </summary>
    private const int MinimumWavBytes = 10_000;

    /// <summary>
    /// RMS threshold below which audio is considered silence (mic muted/disconnected).
    /// </summary>
    private const float SilenceThreshold = 0.005f;

    /// <summary>
    /// How long to wait (in seconds) after recording starts before checking for silence.
    /// </summary>
    private const double SilenceCheckDelaySeconds = 2.5;

    /// <summary>
    /// One-shot timer that fires after <see cref="SilenceCheckDelaySeconds"/> to warn the
    /// user if no audio has been detected (mic may be muted).
    /// </summary>
    private DispatcherTimer? _silenceCheckTimer;

    /// <summary>
    /// Tracks whether the "mic may be muted" warning is currently displayed on the overlay
    /// during a recording session. Reset at the start of each recording.
    /// </summary>
    private bool _mutedWarningShown;

    // ──────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ──────────────────────────────────────────────────────────────────

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Single-instance check
        if (SingleInstanceHelper.IsAlreadyRunning())
        {
            MessageBox.Show(
                "SimpleWhisper is already running.\nCheck the system tray.",
                "SimpleWhisper",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        // 2. Global exception handlers
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 3. Initialize settings
        Settings = new SettingsService();

        // 3b. Sync "Start with Windows" registry entry with saved setting.
        // This ensures the registry stays correct even if the app was moved or reinstalled.
        StartupService.SetStartWithWindows(Settings.Settings.StartWithWindows);

        // 4. Create core services
        _audioCaptureService = new AudioCaptureService();
        _transcriptionManager = new TranscriptionManager(Settings);
        _textInsertionService = new TextInsertionService();

        // 5. Create the overlay window
        _overlayWindow = new OverlayWindow();
        _overlayWindow.Show();

        // 6. Set up input trigger (hotkey/mouse hook)
        _inputTriggerService = new InputTriggerService(
            Settings.Settings.Trigger,
            Settings.Settings.RecordingMode);

        _inputTriggerService.TriggerActivated += OnTriggerActivated;
        _inputTriggerService.TriggerDeactivated += OnTriggerDeactivated;

        // 7. Wire audio level to overlay
        _audioCaptureService.AudioLevelChanged += OnAudioLevelChanged;

        // 8. Set up system tray
        Tray = new TrayManager(initialListeningState: true);
        Tray.ListeningToggled += OnListeningToggled;

        // 9. Listen for settings changes to reconfigure services at runtime
        Settings.SettingsChanged += OnSettingsChanged;

        // 10. Start listening immediately (hotkey hooks active)
        _inputTriggerService.Start();

        // 11. Startup validation — warn user about missing configuration
        ValidateStartupConfiguration();

        AppLogger.Log("Application started. Listening for trigger...");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _inputTriggerService?.Dispose();
        _audioCaptureService?.Dispose();
        _textInsertionService?.Dispose();
        Tray?.Dispose();
        SingleInstanceHelper.Release();
        base.OnExit(e);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Pipeline: Trigger → Record → Transcribe → Process → Paste
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called when the input trigger is activated (key/mouse pressed).
    /// Starts recording audio from the microphone.
    /// </summary>
    private void OnTriggerActivated()
    {
        AppLogger.Log(">>> Trigger ACTIVATED (key/mouse pressed)");

        if (_isProcessing || _audioCaptureService is null || _overlayWindow is null)
            return;

        try
        {
            int deviceIndex = Settings.Settings.MicrophoneDeviceIndex;
            _audioCaptureService.StartRecording(deviceIndex);

            // Update UI state
            Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow.ViewModel.SetRecording();
                Tray?.SetState(TrayState.Recording);
            });

            // Start real-time transcription loop if enabled and using local engine.
            var settings = Settings.Settings;
            if (settings.RealtimeTranscription && settings.Engine == TranscriptionEngine.Local)
            {
                _accumulatedStreamingText = string.Empty;
                _streamingCts = new CancellationTokenSource();
                _streamingTask = RunStreamingTranscriptionLoopAsync(_streamingCts.Token);
                AppLogger.Log("Real-time transcription loop started.");
            }

            AppLogger.Log("Recording started.");

            // Start a one-shot timer to check for silence after a few seconds.
            // If the mic is muted, peak audio level will still be near-zero by then.
            _mutedWarningShown = false;
            StartSilenceCheckTimer();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Failed to start recording: {ex.Message}");
            Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow?.ViewModel.SetError($"Mic error: {ex.Message}");
            });
        }
    }

    /// <summary>
    /// Called when the input trigger is deactivated (key/mouse released).
    /// Stops recording, transcribes, processes, and pastes the result.
    /// </summary>
    private void OnTriggerDeactivated()
    {
        AppLogger.Log(">>> Trigger DEACTIVATED (key/mouse released)");

        // Stop the silence-check timer — we'll do a final check in HandleDeactivationAsync.
        StopSilenceCheckTimer();

        if (_audioCaptureService is null || !_audioCaptureService.IsRecording)
            return;

        if (_isProcessing)
            return;

        _isProcessing = true;

        // Determine if we were running in real-time streaming mode.
        bool wasStreaming = _streamingCts is not null;

        // Cancel the streaming loop signal immediately (synchronous).
        if (wasStreaming)
        {
            _streamingCts!.Cancel();
        }

        // The rest involves async work (waiting for streaming loop, transcribing the tail).
        _ = HandleDeactivationAsync(wasStreaming);
    }

    /// <summary>
    /// Runs the full transcription pipeline: transcribe → text process → insert.
    /// Executes on a background thread to keep the UI responsive.
    /// </summary>
    private async Task RunTranscriptionPipelineAsync(byte[] wavData)
    {
        try
        {
            // Step 1: Transcribe
            var result = await _transcriptionManager!.TranscribeAsync(wavData);

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
                return;
            }

            string text = result.Text;
            AppLogger.Log($"Raw transcription: \"{text}\"");

            // Step 2: Text processing (punctuation, replacements, emoji)
            text = TextProcessingService.Process(text, Settings.Settings);
            AppLogger.Log($"Processed text: \"{text}\"");

            if (string.IsNullOrWhiteSpace(text))
            {
                await Dispatcher.BeginInvoke(() =>
                {
                    _overlayWindow?.ViewModel.SetIdle();
                    Tray?.SetState(TrayState.Idle);
                });
                return;
            }

            // Step 3: Insert text via clipboard paste
            await _textInsertionService!.InsertTextAsync(text);

            AppLogger.Log("Text inserted successfully.");

            // Step 4: Show success in overlay
            await Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow?.ViewModel.SetDone(text);
                Tray?.SetState(TrayState.Idle);
            });
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Pipeline error", ex);
            await Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow?.ViewModel.SetError($"Error: {ex.Message}");
                Tray?.SetState(TrayState.Idle);
            });
        }
        finally
        {
            _isProcessing = false;
        }
    }

    /// <summary>
    /// Async continuation of <see cref="OnTriggerDeactivated"/>. Waits for the streaming
    /// loop to finish (if active), stops recording, and runs the appropriate pipeline.
    /// </summary>
    private async Task HandleDeactivationAsync(bool wasStreaming)
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

            // Stop recording and get complete WAV data.
            byte[] wavData = _audioCaptureService!.StopRecording();

            AppLogger.Log($"Recording stopped. WAV size: {wavData.Length} bytes.");

            if (wavData.Length < MinimumWavBytes)
            {
                AppLogger.Log($"Audio too short ({wavData.Length} bytes < {MinimumWavBytes} minimum). Skipping transcription.");
                _isProcessing = false;
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
                _isProcessing = false;
                await Dispatcher.BeginInvoke(() =>
                {
                    _overlayWindow?.ViewModel.SetError("No audio detected \u2014 is your mic muted?");
                    Tray?.SetState(TrayState.Idle);
                    Tray?.ShowBalloonTip("No Audio Detected",
                        "Your recording was silent. Check that your microphone is unmuted and working.",
                        H.NotifyIcon.Core.NotificationIcon.Warning);
                });
                return;
            }

            // Update UI to processing state.
            await Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow?.ViewModel.SetProcessing();
                Tray?.SetState(TrayState.Processing);
            });

            if (wasStreaming)
            {
                // Streaming path: transcribe only the remaining un-chunked audio tail,
                // then combine with the text accumulated during recording.
                await RunStreamingFinalizePipelineAsync(wavData);
            }
            else
            {
                // Standard path: transcribe the full recording at once.
                await RunTranscriptionPipelineAsync(wavData);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log("Error in deactivation handler", ex);
            await Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow?.ViewModel.SetError($"Error: {ex.Message}");
                Tray?.SetState(TrayState.Idle);
            });
            _isProcessing = false;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Real-time streaming transcription
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Background loop that extracts audio chunks during recording and transcribes them
    /// in real-time, updating the overlay with partial results.
    /// </summary>
    private async Task RunStreamingTranscriptionLoopAsync(CancellationToken cancellationToken)
    {
        string? lastContext = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Wait for the next chunk interval.
                await Task.Delay(StreamingChunkInterval, cancellationToken).ConfigureAwait(false);

                byte[] chunk = _audioCaptureService!.GetAudioChunk();
                if (chunk.Length == 0)
                    continue;

                AppLogger.Log($"Streaming chunk extracted: {chunk.Length} bytes.");

                var result = await _transcriptionManager!.TranscribeChunkAsync(
                    chunk, lastContext, cancellationToken).ConfigureAwait(false);

                if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Text))
                {
                    // Append this chunk's text to the running accumulator.
                    _accumulatedStreamingText = string.IsNullOrEmpty(_accumulatedStreamingText)
                        ? result.Text
                        : $"{_accumulatedStreamingText} {result.Text}";

                    // Keep the last ~30 words as context for the next chunk.
                    lastContext = GetTrailingWords(result.Text, 30);

                    AppLogger.Log($"Streaming chunk transcribed: \"{result.Text}\"");

                    // Update overlay with accumulated text (must run on UI thread).
                    string displayText = _accumulatedStreamingText;
                    Dispatcher.BeginInvoke(() =>
                    {
                        _overlayWindow?.ViewModel.UpdateLiveText(displayText);
                    });
                }
                else if (!result.IsSuccess)
                {
                    AppLogger.Log($"Streaming chunk failed: {result.ErrorMessage}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation when recording stops.
        }
        catch (Exception ex)
        {
            AppLogger.Log("Streaming transcription loop error", ex);
        }
    }

    /// <summary>
    /// Finalizes a real-time streaming session: transcribes any remaining audio that
    /// wasn't covered by the chunked loop, concatenates with accumulated text,
    /// then processes and pastes.
    /// </summary>
    private async Task RunStreamingFinalizePipelineAsync(byte[] fullWavData)
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

                var tailResult = await _transcriptionManager!.TranscribeChunkAsync(remaining, lastContext);

                if (tailResult.IsSuccess && !string.IsNullOrWhiteSpace(tailResult.Text))
                {
                    _accumulatedStreamingText = string.IsNullOrEmpty(_accumulatedStreamingText)
                        ? tailResult.Text
                        : $"{_accumulatedStreamingText} {tailResult.Text}";

                    AppLogger.Log($"Tail transcribed: \"{tailResult.Text}\"");
                }
            }

            string text = _accumulatedStreamingText.Trim();
            AppLogger.Log($"Full streaming transcription: \"{text}\"");

            if (string.IsNullOrWhiteSpace(text))
            {
                await Dispatcher.BeginInvoke(() =>
                {
                    _overlayWindow?.ViewModel.SetIdle();
                    Tray?.SetState(TrayState.Idle);
                });
                return;
            }

            // Text processing (punctuation, replacements, emoji)
            text = TextProcessingService.Process(text, Settings.Settings);
            AppLogger.Log($"Processed text: \"{text}\"");

            if (string.IsNullOrWhiteSpace(text))
            {
                await Dispatcher.BeginInvoke(() =>
                {
                    _overlayWindow?.ViewModel.SetIdle();
                    Tray?.SetState(TrayState.Idle);
                });
                return;
            }

            // Insert text via clipboard paste
            await _textInsertionService!.InsertTextAsync(text);

            AppLogger.Log("Streaming text inserted successfully.");

            // Show success in overlay
            await Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow?.ViewModel.SetDone(text);
                Tray?.SetState(TrayState.Idle);
            });
        }
        catch (Exception ex)
        {
            AppLogger.Log("Streaming finalize error", ex);
            await Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow?.ViewModel.SetError($"Error: {ex.Message}");
                Tray?.SetState(TrayState.Idle);
            });
        }
        finally
        {
            _isProcessing = false;
        }
    }

    /// <summary>
    /// Returns the last <paramref name="wordCount"/> words from the given text,
    /// used as context for prompt chaining between transcription chunks.
    /// </summary>
    private static string? GetTrailingWords(string text, int wordCount)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= wordCount)
            return text;

        return string.Join(' ', words[^wordCount..]);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Audio level forwarding
    // ──────────────────────────────────────────────────────────────────

    private void OnAudioLevelChanged(float level)
    {
        // Forward audio level to overlay (throttled by the overlay's binding system)
        _overlayWindow?.Dispatcher.BeginInvoke(() =>
        {
            _overlayWindow?.ViewModel.UpdateAudioLevel(level);
        });

        // If the muted warning is showing and we detect real audio, clear it.
        if (_mutedWarningShown && _audioCaptureService is not null
            && _audioCaptureService.PeakAudioLevel >= SilenceThreshold)
        {
            _mutedWarningShown = false;
            AppLogger.Log("Audio detected after muted warning — clearing warning.");
            _overlayWindow?.Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow?.ViewModel.UpdateLiveText(string.Empty);
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Real-time silence detection (muted-mic warning during recording)
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a one-shot dispatcher timer that checks for silence after
    /// <see cref="SilenceCheckDelaySeconds"/>. If no meaningful audio has been
    /// detected by then, a warning is shown on the overlay while recording continues.
    /// </summary>
    private void StartSilenceCheckTimer()
    {
        Dispatcher.BeginInvoke(() =>
        {
            StopSilenceCheckTimer();
            _silenceCheckTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(SilenceCheckDelaySeconds)
            };
            _silenceCheckTimer.Tick += OnSilenceCheckTimerTick;
            _silenceCheckTimer.Start();
        });
    }

    /// <summary>
    /// Stops and disposes the silence-check timer if it is running.
    /// </summary>
    private void StopSilenceCheckTimer()
    {
        if (_silenceCheckTimer is not null)
        {
            _silenceCheckTimer.Stop();
            _silenceCheckTimer.Tick -= OnSilenceCheckTimerTick;
            _silenceCheckTimer = null;
        }
    }

    /// <summary>
    /// Fires once after <see cref="SilenceCheckDelaySeconds"/>. If the peak audio
    /// level is still below the silence threshold, shows a muted-mic warning on
    /// the overlay without interrupting the recording.
    /// </summary>
    private void OnSilenceCheckTimerTick(object? sender, EventArgs e)
    {
        // One-shot: stop immediately so this doesn't repeat.
        StopSilenceCheckTimer();

        if (_audioCaptureService is null || !_audioCaptureService.IsRecording)
            return;

        if (_audioCaptureService.PeakAudioLevel < SilenceThreshold)
        {
            _mutedWarningShown = true;
            AppLogger.Log($"Silence detected during recording (peak: {_audioCaptureService.PeakAudioLevel:F4}). Showing muted warning.");
            _overlayWindow?.ViewModel.UpdateLiveText("⚠ No audio detected — is your mic muted?");
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Tray listening toggle
    // ──────────────────────────────────────────────────────────────────

    private void OnListeningToggled(object? sender, bool isListening)
    {
        if (isListening)
        {
            _inputTriggerService?.Start();
            AppLogger.Log("Listening started via tray.");
        }
        else
        {
            _inputTriggerService?.Stop();
            AppLogger.Log("Listening stopped via tray.");
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Settings change handler
    // ──────────────────────────────────────────────────────────────────

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var settings = Settings.Settings;

        // Reconfigure the input trigger if it changed
        _inputTriggerService?.UpdateTrigger(settings.Trigger);
        _inputTriggerService?.UpdateMode(settings.RecordingMode);

        AppLogger.Log("Settings changed — services reconfigured.");
    }

    // ──────────────────────────────────────────────────────────────────
    //  Error handling
    // ──────────────────────────────────────────────────────────────────

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        e.Handled = true;

        MessageBox.Show(
            $"An unexpected error occurred:\n{e.Exception.Message}",
            "SimpleWhisper Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException(e.Exception);
        e.SetObserved();
    }

    private static void LogException(Exception ex)
    {
        AppLogger.Log($"Unhandled exception", ex);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Error message helpers
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reduces verbose API error messages to short, user-friendly strings for the overlay.
    /// The full error is always written to the log file for diagnostics.
    /// </summary>
    private static string SimplifyErrorMessage(string error)
    {
        if (error.Contains("audio_too_short", StringComparison.OrdinalIgnoreCase))
            return "Recording too short — hold the key longer.";

        if (error.Contains("API key", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            return "Invalid API key — check Settings.";

        if (error.Contains("Network error", StringComparison.OrdinalIgnoreCase))
            return "Network error — check your connection.";

        if (error.Contains("empty text", StringComparison.OrdinalIgnoreCase))
            return "No speech detected.";

        // Truncate long errors to fit the overlay.
        return error.Length > 60 ? error[..57] + "..." : error;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Startup validation
    // ──────────────────────────────────────────────────────────────────

    private void ValidateStartupConfiguration()
    {
        var settings = Settings.Settings;

        // Check if cloud engine is selected but API key is missing
        if (settings.Engine == TranscriptionEngine.Cloud && string.IsNullOrWhiteSpace(settings.OpenAIApiKey))
        {
            AppLogger.Log("WARNING: Cloud engine selected but no API key configured.");

            Dispatcher.BeginInvoke(() =>
            {
                var result = MessageBox.Show(
                    "SimpleWhisper is set to use the OpenAI cloud engine, but no API key is configured.\n\n" +
                    "Would you like to open Settings to add your API key?\n\n" +
                    "(You can also switch to the local Whisper engine in Settings.)",
                    "Configuration Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    var settingsWindow = new Views.SettingsWindow();
                    settingsWindow.Show();
                }
            });
        }

        // Check available microphones
        var devices = AudioCaptureService.GetAvailableDevices();
        if (devices.Count == 0)
        {
            AppLogger.Log("WARNING: No microphone devices found.");
            Dispatcher.BeginInvoke(() =>
            {
                MessageBox.Show(
                    "No microphone devices were found.\n\nPlease connect a microphone and restart the application.",
                    "No Microphone",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            });
        }
        else
        {
            AppLogger.Log($"Found {devices.Count} microphone(s). Using device {settings.MicrophoneDeviceIndex}: {(settings.MicrophoneDeviceIndex < devices.Count ? devices[settings.MicrophoneDeviceIndex].Name : "INVALID INDEX")}");
        }
    }
}
