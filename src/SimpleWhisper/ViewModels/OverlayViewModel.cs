using System.Windows.Threading;
using SimpleWhisper.Models;

namespace SimpleWhisper.ViewModels;

/// <summary>
/// View model for the floating overlay window that displays recording state,
/// audio levels, transcription status, and brief result previews.
/// </summary>
public class OverlayViewModel : ViewModelBase
{
    // ──────────────────────────────────────────────────────────────────
    //  Backing fields
    // ──────────────────────────────────────────────────────────────────

    private OverlayState _state = OverlayState.Idle;
    private string _statusText = "Ready";
    private double _audioLevel;
    private bool _isVisible;
    private string _transcribedText = string.Empty;
    private double _animationOpacity;

    /// <summary>
    /// Timer used to auto-hide the overlay after Done or Error states.
    /// </summary>
    private readonly DispatcherTimer _autoHideTimer;

    // ──────────────────────────────────────────────────────────────────
    //  Constructor
    // ──────────────────────────────────────────────────────────────────

    public OverlayViewModel()
    {
        _autoHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _autoHideTimer.Tick += OnAutoHideTimerTick;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Bindable properties
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The current visual state of the overlay (Idle, Recording, Processing, Done, Error).
    /// </summary>
    public OverlayState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    /// <summary>
    /// Human-readable status text displayed in the overlay center
    /// (e.g. "Recording...", "Processing...", "Done!").
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>
    /// Current audio input level, normalized to 0.0 .. 1.0.
    /// Only meaningful while <see cref="State"/> is <see cref="OverlayState.Recording"/>.
    /// </summary>
    public double AudioLevel
    {
        get => _audioLevel;
        private set => SetProperty(ref _audioLevel, value);
    }

    /// <summary>
    /// Controls whether the overlay window is visible on screen.
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    /// <summary>
    /// The most recently transcribed text, shown briefly when the state is Done.
    /// </summary>
    public string TranscribedText
    {
        get => _transcribedText;
        private set => SetProperty(ref _transcribedText, value);
    }

    /// <summary>
    /// Opacity value (0.0 .. 1.0) used by XAML animations for fade-in / fade-out effects.
    /// </summary>
    public double AnimationOpacity
    {
        get => _animationOpacity;
        private set => SetProperty(ref _animationOpacity, value);
    }

    // ──────────────────────────────────────────────────────────────────
    //  State transition methods
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Transitions the overlay to the Recording state.
    /// </summary>
    public void SetRecording()
    {
        StopAutoHideTimer();
        State = OverlayState.Recording;
        StatusText = "Recording\u2026";
        AudioLevel = 0.0;
        TranscribedText = string.Empty;
        AnimationOpacity = 1.0;
        IsVisible = true;
    }

    /// <summary>
    /// Transitions the overlay to the Processing state.
    /// </summary>
    public void SetProcessing()
    {
        StopAutoHideTimer();
        State = OverlayState.Processing;
        StatusText = "Processing\u2026";
        AudioLevel = 0.0;
        AnimationOpacity = 1.0;
        IsVisible = true;
    }

    /// <summary>
    /// Transitions the overlay to the Done state. The overlay auto-hides after 2 seconds.
    /// </summary>
    /// <param name="text">The transcribed text to display briefly.</param>
    public void SetDone(string text)
    {
        StopAutoHideTimer();
        State = OverlayState.Done;
        StatusText = "Done!";
        TranscribedText = text;
        AudioLevel = 0.0;
        AnimationOpacity = 1.0;
        IsVisible = true;
        StartAutoHideTimer(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Transitions the overlay to the Error state. The overlay auto-hides after 3 seconds.
    /// </summary>
    /// <param name="message">The error message to display.</param>
    public void SetError(string message)
    {
        StopAutoHideTimer();
        State = OverlayState.Error;
        StatusText = message;
        TranscribedText = string.Empty;
        AudioLevel = 0.0;
        AnimationOpacity = 1.0;
        IsVisible = true;
        StartAutoHideTimer(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Transitions the overlay to the Idle (hidden) state.
    /// </summary>
    public void SetIdle()
    {
        StopAutoHideTimer();
        State = OverlayState.Idle;
        StatusText = "Ready";
        AudioLevel = 0.0;
        AnimationOpacity = 0.0;
        IsVisible = false;
    }

    /// <summary>
    /// Updates the audio level indicator. Clamped to 0.0 .. 1.0.
    /// </summary>
    /// <param name="level">Normalized audio level.</param>
    public void UpdateAudioLevel(double level)
    {
        AudioLevel = Math.Clamp(level, 0.0, 1.0);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Auto-hide timer helpers
    // ──────────────────────────────────────────────────────────────────

    private void StartAutoHideTimer(TimeSpan interval)
    {
        _autoHideTimer.Interval = interval;
        _autoHideTimer.Start();
    }

    private void StopAutoHideTimer()
    {
        _autoHideTimer.Stop();
    }

    private void OnAutoHideTimerTick(object? sender, EventArgs e)
    {
        _autoHideTimer.Stop();
        SetIdle();
    }
}
