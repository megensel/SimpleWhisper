using NAudio.CoreAudioApi;
using SimpleWhisper.Helpers;

namespace SimpleWhisper.Services;

/// <summary>
/// Controls the system's default audio output volume. Used to reduce or mute
/// playback while recording to prevent audio bleed into the microphone.
/// Thread-safe — all operations are idempotent and guard against double-apply/restore.
/// </summary>
public sealed class OutputVolumeService : IDisposable
{
    private readonly object _lock = new();
    private MMDeviceEnumerator? _enumerator;

    // Saved state before volume reduction
    private float _savedVolume;
    private bool _savedMute;
    private bool _isReduced;
    private bool _disposed;

    /// <summary>
    /// Reduces the system output volume according to the specified parameters.
    /// Saves the current volume/mute state for later restoration.
    /// No-op if already reduced.
    /// </summary>
    /// <param name="mute">If true, fully mute the output.</param>
    /// <param name="reductionPercent">
    /// Percentage to reduce (0-100). 80 means reduce volume to 20% of current.
    /// Ignored if <paramref name="mute"/> is true.
    /// </param>
    public void ReduceVolume(bool mute, int reductionPercent)
    {
        lock (_lock)
        {
            if (_isReduced || _disposed)
                return;

            try
            {
                var device = GetDefaultOutputDevice();
                if (device is null)
                    return;

                var volume = device.AudioEndpointVolume;

                // Save current state
                _savedVolume = volume.MasterVolumeLevelScalar;
                _savedMute = volume.Mute;

                if (mute)
                {
                    volume.Mute = true;
                    AppLogger.Log("[OutputVolume] Muted system output.");
                }
                else
                {
                    int clamped = Math.Clamp(reductionPercent, 0, 100);
                    float factor = (100 - clamped) / 100f;
                    float newVolume = _savedVolume * factor;
                    volume.MasterVolumeLevelScalar = newVolume;
                    AppLogger.Log($"[OutputVolume] Reduced volume from {_savedVolume:P0} to {newVolume:P0} ({clamped}% reduction).");
                }

                _isReduced = true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[OutputVolume] Failed to reduce volume: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Restores the system output volume to the state saved before
    /// <see cref="ReduceVolume"/> was called. No-op if not currently reduced.
    /// </summary>
    public void RestoreVolume()
    {
        lock (_lock)
        {
            if (!_isReduced || _disposed)
                return;

            try
            {
                var device = GetDefaultOutputDevice();
                if (device is null)
                {
                    _isReduced = false;
                    return;
                }

                var volume = device.AudioEndpointVolume;
                volume.MasterVolumeLevelScalar = _savedVolume;
                volume.Mute = _savedMute;

                AppLogger.Log($"[OutputVolume] Restored volume to {_savedVolume:P0}, mute={_savedMute}.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[OutputVolume] Failed to restore volume: {ex.Message}");
            }
            finally
            {
                _isReduced = false;
            }
        }
    }

    /// <summary>
    /// Gets the default audio output endpoint device, or null if none is available.
    /// </summary>
    private MMDevice? GetDefaultOutputDevice()
    {
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[OutputVolume] No default output device: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        RestoreVolume();
        _disposed = true;

        try
        {
            _enumerator?.Dispose();
        }
        catch
        {
            // Ignore COM cleanup errors during shutdown.
        }

        _enumerator = null;
    }
}
