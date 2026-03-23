namespace SimpleWhisper.Models;

/// <summary>
/// Shared audio processing constants used across services.
/// </summary>
internal static class AudioConstants
{
    /// <summary>
    /// RMS threshold below which audio is considered silence (mic muted or disconnected).
    /// </summary>
    public const double SilenceRmsThreshold = 0.005;
}
