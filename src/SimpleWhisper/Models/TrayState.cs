namespace SimpleWhisper.Models;

/// <summary>
/// Represents the current state of the application as displayed by the system tray icon.
/// </summary>
public enum TrayState
{
    /// <summary>The application is idle and ready to accept input.</summary>
    Idle,

    /// <summary>Audio is being recorded from the microphone.</summary>
    Recording,

    /// <summary>Recorded audio is being transcribed.</summary>
    Processing
}
