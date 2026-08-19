namespace SimpleWhisper.Models;

/// <summary>
/// Represents the current visual state of the floating overlay window.
/// </summary>
public enum OverlayState
{
    /// <summary>The overlay is idle (hidden).</summary>
    Idle,

    /// <summary>Audio is being captured from the microphone.</summary>
    Recording,

    /// <summary>Recorded audio is being transcribed.</summary>
    Processing,

    /// <summary>Transcription completed successfully.</summary>
    Done,

    /// <summary>An error occurred during recording or transcription.</summary>
    Error,

    /// <summary>The user cancelled the recording or transcription.</summary>
    Cancelled
}
