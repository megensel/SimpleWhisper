using System.IO;
using NAudio.Wave;

namespace SimpleWhisper.Services;

/// <summary>
/// Captures audio from a system microphone using NAudio, buffering it as a WAV file
/// in memory. Designed to produce audio in Whisper's native format: 16 kHz, 16-bit, mono.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    /// <summary>
    /// Whisper's expected sample rate.
    /// </summary>
    private const int SampleRate = 16000;

    /// <summary>
    /// Whisper's expected bit depth.
    /// </summary>
    private const int BitsPerSample = 16;

    /// <summary>
    /// Whisper's expected channel count.
    /// </summary>
    private const int Channels = 1;

    private WaveInEvent? _waveIn;
    private MemoryStream? _memoryStream;
    private WaveFileWriter? _waveFileWriter;
    private bool _disposed;

    /// <summary>
    /// Fired on each audio buffer with the calculated RMS audio level, normalized to a 0.0–1.0 range.
    /// Useful for driving a real-time audio level visualizer.
    /// </summary>
    public event Action<float>? AudioLevelChanged;

    /// <summary>
    /// Gets a value indicating whether the service is currently recording audio.
    /// </summary>
    public bool IsRecording { get; private set; }

    /// <summary>
    /// Enumerates the available audio input (microphone) devices on the system.
    /// </summary>
    /// <returns>
    /// A list of tuples containing the zero-based device index and the device's friendly name.
    /// Returns an empty list if no recording devices are available.
    /// </returns>
    public static List<(int Index, string Name)> GetAvailableDevices()
    {
        var devices = new List<(int Index, string Name)>();
        int deviceCount = WaveInEvent.DeviceCount;

        for (int i = 0; i < deviceCount; i++)
        {
            var capabilities = WaveInEvent.GetCapabilities(i);
            devices.Add((i, capabilities.ProductName));
        }

        return devices;
    }

    /// <summary>
    /// Begins capturing audio from the specified microphone device. Audio data is buffered
    /// internally as a WAV file in memory. Call <see cref="StopRecording"/> to finalize
    /// and retrieve the recorded data.
    /// </summary>
    /// <param name="deviceIndex">
    /// The zero-based index of the recording device, as returned by <see cref="GetAvailableDevices"/>.
    /// </param>
    /// <exception cref="ObjectDisposedException">Thrown if the service has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown if recording is already in progress.</exception>
    public void StartRecording(int deviceIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRecording)
            throw new InvalidOperationException("Recording is already in progress.");

        _memoryStream = new MemoryStream();

        var waveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels);
        _waveFileWriter = new WaveFileWriter(_memoryStream, waveFormat);

        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceIndex,
            WaveFormat = waveFormat,
            BufferMilliseconds = 50
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        _waveIn.StartRecording();
        IsRecording = true;
    }

    /// <summary>
    /// Stops the current recording and returns the captured audio as a complete WAV file byte array.
    /// The WAV data includes proper headers and can be sent directly to a transcription service.
    /// </summary>
    /// <returns>
    /// A byte array containing a valid WAV file, or an empty array if no recording was in progress.
    /// </returns>
    public byte[] StopRecording()
    {
        if (!IsRecording || _waveIn is null)
            return [];

        _waveIn.StopRecording();
        IsRecording = false;

        return FinalizeRecording();
    }

    /// <summary>
    /// Releases all resources held by this service, including any active recording session.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (IsRecording)
        {
            _waveIn?.StopRecording();
            IsRecording = false;
        }

        CleanupResources();
    }

    /// <summary>
    /// Handles incoming audio data from the microphone. Writes the raw PCM data to the
    /// in-memory WAV file and calculates the RMS level for the visualizer.
    /// </summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_waveFileWriter is not null && e.BytesRecorded > 0)
        {
            _waveFileWriter.Write(e.Buffer, 0, e.BytesRecorded);

            float rmsLevel = CalculateRmsLevel(e.Buffer, e.BytesRecorded);
            AudioLevelChanged?.Invoke(rmsLevel);
        }
    }

    /// <summary>
    /// Handles the recording-stopped event from NAudio. Used for cleanup on unexpected stops.
    /// </summary>
    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Recording stopped due to error: {e.Exception.Message}");
        }
    }

    /// <summary>
    /// Flushes the WAV writer and extracts the completed WAV file bytes from the memory stream.
    /// </summary>
    private byte[] FinalizeRecording()
    {
        byte[] audioData;

        try
        {
            // Flush the WAV writer to finalize headers (length fields in the WAV header).
            // We must flush before reading the stream, but we cannot dispose the writer
            // because it would close the underlying MemoryStream.
            _waveFileWriter?.Flush();

            audioData = _memoryStream?.ToArray() ?? [];
        }
        finally
        {
            CleanupResources();
        }

        return audioData;
    }

    /// <summary>
    /// Disposes of the NAudio recording device, WAV writer, and memory stream.
    /// </summary>
    private void CleanupResources()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }

        // Dispose writer before stream; WaveFileWriter does NOT own the stream
        // when constructed with a MemoryStream, but we manage both explicitly.
        _waveFileWriter?.Dispose();
        _waveFileWriter = null;

        _memoryStream?.Dispose();
        _memoryStream = null;
    }

    /// <summary>
    /// Calculates the RMS (root mean square) audio level from a buffer of 16-bit PCM samples,
    /// normalized to a 0.0–1.0 range.
    /// </summary>
    /// <param name="buffer">The raw byte buffer containing 16-bit PCM samples.</param>
    /// <param name="bytesRecorded">The number of valid bytes in the buffer.</param>
    /// <returns>The RMS level as a float between 0.0 and 1.0.</returns>
    private static float CalculateRmsLevel(byte[] buffer, int bytesRecorded)
    {
        // Each sample is 2 bytes (16-bit).
        int sampleCount = bytesRecorded / 2;
        if (sampleCount == 0)
            return 0f;

        double sumOfSquares = 0;

        for (int i = 0; i < bytesRecorded; i += 2)
        {
            // Convert two bytes to a 16-bit signed sample (little-endian).
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));

            // Normalize to -1.0..1.0 range.
            double normalized = sample / 32768.0;
            sumOfSquares += normalized * normalized;
        }

        double rms = Math.Sqrt(sumOfSquares / sampleCount);

        // Clamp to 0.0..1.0 (RMS of a full-scale sine wave is ~0.707, so values near 1.0
        // are theoretically possible but rare with real audio).
        return (float)Math.Min(rms, 1.0);
    }
}
