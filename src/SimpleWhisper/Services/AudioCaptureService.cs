using System.IO;
using NAudio.Wave;
using SimpleWhisper.Helpers;

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

    /// <summary>
    /// Size of a standard RIFF/WAVE header in bytes.
    /// </summary>
    private const int WavHeaderSize = 44;

    /// <summary>
    /// Minimum PCM bytes required for a useful audio chunk (~0.5 s at 16 kHz / 16-bit / mono).
    /// </summary>
    private const int MinChunkPcmBytes = 16_000;

    private WaveInEvent? _waveIn;
    private MemoryStream? _memoryStream;
    private WaveFileWriter? _waveFileWriter;
    private bool _disposed;

    /// <summary>
    /// Synchronizes concurrent access to the memory stream between the NAudio callback
    /// thread (writes) and the chunked-extraction caller thread (reads).
    /// </summary>
    private readonly object _bufferLock = new();

    /// <summary>
    /// Synchronizes recording state transitions (start/stop/dispose) so concurrent
    /// callers (e.g. a cancel on the Esc-hook thread racing a trigger release on a
    /// thread-pool thread) cannot both pass the IsRecording check and finalize the
    /// same recording twice.
    /// </summary>
    private readonly object _stateLock = new();

    /// <summary>
    /// Byte offset into the memory stream marking the end of the last extracted chunk.
    /// Reset to <see cref="WavHeaderSize"/> when a new recording starts.
    /// </summary>
    private long _lastChunkByteOffset;

    /// <summary>
    /// Tracks the highest RMS audio level observed during the current recording session.
    /// Reset to zero when a new recording starts. Used to detect muted/silent recordings.
    /// </summary>
    private float _peakAudioLevel;

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
    /// Gets the peak (maximum) RMS audio level observed during the most recent recording session.
    /// A value near zero after recording indicates the microphone was muted or disconnected.
    /// </summary>
    public float PeakAudioLevel => _peakAudioLevel;

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
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (IsRecording)
                throw new InvalidOperationException("Recording is already in progress.");

            _memoryStream = new MemoryStream();

            var waveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels);
            _waveFileWriter = new WaveFileWriter(_memoryStream, waveFormat);

            // The WAV header occupies the first 44 bytes; PCM data starts after that.
            _lastChunkByteOffset = WavHeaderSize;
            _peakAudioLevel = 0f;

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
        lock (_stateLock)
        {
            if (!IsRecording || _waveIn is null)
                return [];

            _waveIn.StopRecording();
            IsRecording = false;

            return FinalizeRecording();
        }
    }

    /// <summary>
    /// Releases all resources held by this service, including any active recording session.
    /// </summary>
    public void Dispose()
    {
        lock (_stateLock)
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
    }

    /// <summary>
    /// Handles incoming audio data from the microphone. Writes the raw PCM data to the
    /// in-memory WAV file and calculates the RMS level for the visualizer.
    /// </summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;

        lock (_bufferLock)
        {
            _waveFileWriter?.Write(e.Buffer, 0, e.BytesRecorded);
        }

        // RMS calculation is read-only on e.Buffer — safe outside the lock.
        float rmsLevel = CalculateRmsLevel(e.Buffer, e.BytesRecorded);

        // Track peak level to detect muted/silent recordings.
        if (rmsLevel > _peakAudioLevel)
            _peakAudioLevel = rmsLevel;

        AudioLevelChanged?.Invoke(rmsLevel);
    }

    /// <summary>
    /// Handles the recording-stopped event from NAudio. Used for cleanup on unexpected stops.
    /// </summary>
    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            AppLogger.Log(
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

    // ──────────────────────────────────────────────────────────────────
    //  Chunked audio extraction (for real-time transcription)
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the audio recorded since the last call to this method (or since recording
    /// started) as a standalone WAV byte array. The recording continues uninterrupted.
    /// Returns an empty array if there is not enough new audio (less than ~0.5 s).
    /// </summary>
    /// <remarks>Thread-safe — may be called from any thread while recording is in progress.</remarks>
    public byte[] GetAudioChunk()
    {
        lock (_bufferLock)
        {
            if (_memoryStream is null || _waveFileWriter is null || !IsRecording)
                return [];

            _waveFileWriter.Flush();

            long currentPosition = _memoryStream.Position;
            long chunkStart = _lastChunkByteOffset;

            if (currentPosition - chunkStart < MinChunkPcmBytes)
                return [];

            int pcmLength = (int)(currentPosition - chunkStart);
            byte[] pcmData = new byte[pcmLength];
            Buffer.BlockCopy(_memoryStream.GetBuffer(), (int)chunkStart, pcmData, 0, pcmLength);

            _lastChunkByteOffset = currentPosition;

            return BuildWavFromPcm(pcmData);
        }
    }

    /// <summary>
    /// After <see cref="StopRecording"/> has been called, extracts any audio that was
    /// recorded after the last <see cref="GetAudioChunk"/> call. Returns an empty array
    /// if all audio was already consumed by previous chunk calls, or if the remaining
    /// audio is too short to be useful.
    /// </summary>
    /// <param name="fullWav">
    /// The complete WAV byte array returned by <see cref="StopRecording"/>.
    /// </param>
    public byte[] GetRemainingAudio(byte[] fullWav)
    {
        if (fullWav.Length <= WavHeaderSize)
            return [];

        long chunkStart = _lastChunkByteOffset;

        // All audio was already consumed by chunked calls.
        if (chunkStart >= fullWav.Length)
            return [];

        int pcmLength = fullWav.Length - (int)chunkStart;

        if (pcmLength < MinChunkPcmBytes)
            return [];

        byte[] pcmData = new byte[pcmLength];
        Buffer.BlockCopy(fullWav, (int)chunkStart, pcmData, 0, pcmLength);

        return BuildWavFromPcm(pcmData);
    }

    /// <summary>
    /// Builds a complete WAV file from raw PCM data using the standard audio format
    /// (16 kHz, 16-bit, mono).
    /// </summary>
    private static byte[] BuildWavFromPcm(byte[] pcmData)
    {
        int dataSize = pcmData.Length;
        int fileSize = WavHeaderSize + dataSize;
        byte[] wav = new byte[fileSize];

        int byteRate = SampleRate * Channels * (BitsPerSample / 8);
        int blockAlign = Channels * (BitsPerSample / 8);

        // RIFF header
        wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
        BitConverter.TryWriteBytes(wav.AsSpan(4), fileSize - 8);       // ChunkSize
        wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';

        // fmt sub-chunk
        wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
        BitConverter.TryWriteBytes(wav.AsSpan(16), 16);                 // Subchunk1Size (PCM)
        BitConverter.TryWriteBytes(wav.AsSpan(20), (short)1);           // AudioFormat (PCM)
        BitConverter.TryWriteBytes(wav.AsSpan(22), (short)Channels);
        BitConverter.TryWriteBytes(wav.AsSpan(24), SampleRate);
        BitConverter.TryWriteBytes(wav.AsSpan(28), byteRate);
        BitConverter.TryWriteBytes(wav.AsSpan(32), (short)blockAlign);
        BitConverter.TryWriteBytes(wav.AsSpan(34), (short)BitsPerSample);

        // data sub-chunk
        wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
        BitConverter.TryWriteBytes(wav.AsSpan(40), dataSize);           // Subchunk2Size

        // PCM payload
        Buffer.BlockCopy(pcmData, 0, wav, WavHeaderSize, dataSize);

        return wav;
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
