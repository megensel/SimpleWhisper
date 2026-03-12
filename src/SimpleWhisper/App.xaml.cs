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
    /// Minimum WAV file size to bother sending to transcription.
    /// At 16 kHz / 16-bit / mono = 32,000 bytes/sec, this is ~0.3 seconds of audio
    /// plus the 44-byte WAV header. Below this threshold the audio is too short
    /// for meaningful transcription and will be rejected by the API.
    /// </summary>
    private const int MinimumWavBytes = 10_000;

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

            AppLogger.Log("Recording started.");
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

        if (_audioCaptureService is null || !_audioCaptureService.IsRecording)
            return;

        if (_isProcessing)
            return;

        _isProcessing = true;

        try
        {
            // Stop recording and get WAV data
            byte[] wavData = _audioCaptureService.StopRecording();

            AppLogger.Log($"Recording stopped. WAV size: {wavData.Length} bytes.");

            if (wavData.Length < MinimumWavBytes)
            {
                AppLogger.Log($"Audio too short ({wavData.Length} bytes < {MinimumWavBytes} minimum). Skipping transcription.");
                _isProcessing = false;
                Dispatcher.BeginInvoke(() =>
                {
                    _overlayWindow?.ViewModel.SetIdle();
                    Tray?.SetState(TrayState.Idle);
                });
                return;
            }

            // Update UI to processing state
            Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow?.ViewModel.SetProcessing();
                Tray?.SetState(TrayState.Processing);
            });

            // Run the transcription + processing + insertion pipeline on a background thread
            _ = RunTranscriptionPipelineAsync(wavData);
        }
        catch (Exception ex)
        {
            _isProcessing = false;
            AppLogger.Log("Error stopping recording", ex);
            Dispatcher.BeginInvoke(() =>
            {
                _overlayWindow?.ViewModel.SetError($"Error: {ex.Message}");
                Tray?.SetState(TrayState.Idle);
            });
        }
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
