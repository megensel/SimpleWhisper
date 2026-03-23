using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Whisper.net;
using Whisper.net.LibraryLoader;
using SimpleWhisper.Helpers;
using SimpleWhisper.Models;

namespace SimpleWhisper.Services;

/// <summary>
/// Transcription service that runs Whisper inference locally using the Whisper.net library
/// and GGML model files. Supports automatic model downloading, multiple model sizes, and
/// thread-safe processor access.
/// </summary>
public sealed class LocalTranscriptionService : ITranscriptionService, IDisposable
{
    #region Constants

    /// <summary>
    /// Base URL for downloading pre-converted GGML Whisper models from Hugging Face.
    /// </summary>
    private const string ModelDownloadBaseUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{0}.bin";

    /// <summary>
    /// Subfolder name within <c>%AppData%</c> where downloaded models are cached.
    /// </summary>
    private const string AppDataFolderName = "SimpleWhisper";

    /// <summary>
    /// Subfolder within the application data directory for model files.
    /// </summary>
    private const string ModelsFolderName = "models";

    /// <summary>
    /// Valid Whisper model sizes in ascending order of accuracy (and resource usage).
    /// Includes English-only variants (.en) which are faster for English transcription.
    /// </summary>
    private static readonly HashSet<string> ValidModelSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tiny", "base", "small", "medium", "large",
        "tiny.en", "base.en", "small.en", "medium.en"
    };

    /// <summary>
    /// WAV file header size in bytes (standard RIFF/WAVE PCM header).
    /// </summary>
    private const int WavHeaderSize = 44;

    /// <summary>
    /// Number of 16-bit mono samples in a 20ms analysis window at 16 kHz.
    /// </summary>
    private const int SilenceWindowSamples = 320; // 16000 * 0.020

    /// <summary>
    /// RMS threshold below which a window is considered silence.
    /// Conservative value to avoid trimming actual speech.
    /// </summary>
    private const double SilenceRmsThreshold = AudioConstants.SilenceRmsThreshold;

    /// <summary>
    /// Safety margin in samples to preserve on each side when trimming silence.
    /// 100ms at 16 kHz = 1600 samples.
    /// </summary>
    private const int SilenceMarginSamples = 1600;

    #endregion

    #region Fields

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromHours(2)
    };

    private readonly object _processorLock = new();
    private readonly SemaphoreSlim _transcriptionSemaphore = new(1, 1);

    private WhisperProcessor? _processor;
    private string? _loadedModelPath;
    private GpuAcceleration _loadedAcceleration;
    private bool _disposed;

    #endregion

    #region ITranscriptionService

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            lock (_processorLock)
            {
                return _processor is not null;
            }
        }
    }

    /// <inheritdoc />
    public string EngineName => "Whisper.net (Local)";

    /// <inheritdoc />
    /// <remarks>
    /// Processes the WAV audio data through the local Whisper model. The processor must
    /// be initialized via <see cref="LoadModelAsync"/> before calling this method.
    /// All segments returned by the processor are concatenated into a single result string.
    /// </remarks>
    public async Task<TranscriptionResult> TranscribeAsync(
        byte[] audioData,
        string? language = null,
        string? prompt = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        WhisperProcessor? processor;
        lock (_processorLock)
        {
            processor = _processor;
        }

        if (processor is null)
        {
            return TranscriptionResult.Failure(
                "Local Whisper model is not loaded. Please download or select a model in Settings.");
        }

        var stopwatch = Stopwatch.StartNew();

        // Serialize access to the processor; Whisper.net processors are not reentrant.
        await _transcriptionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Convert WAV byte array to a seekable MemoryStream for the processor.
            using var audioStream = new MemoryStream(audioData);

            var segments = new List<string>();

            await foreach (var segment in processor.ProcessAsync(audioStream, cancellationToken)
                               .ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    segments.Add(segment.Text.Trim());
                }
            }

            stopwatch.Stop();

            string text = string.Join(" ", segments).Trim();

            if (string.IsNullOrWhiteSpace(text))
            {
                return TranscriptionResult.Failure(
                    "Transcription returned empty text.", stopwatch.Elapsed);
            }

            return TranscriptionResult.Success(
                text: text,
                language: language ?? "auto",
                duration: stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return TranscriptionResult.Failure("Transcription was cancelled.", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppLogger.Log($"Local transcription error: {ex}");
            return TranscriptionResult.Failure(
                $"Local transcription error: {ex.Message}", stopwatch.Elapsed);
        }
        finally
        {
            _transcriptionSemaphore.Release();
        }
    }

    #endregion

    #region Model Management

    /// <summary>
    /// Loads a Whisper GGML model from the specified file path, or resolves the path
    /// automatically based on the given model size. If the processor is already loaded
    /// with the same model, this method is a no-op.
    /// </summary>
    /// <param name="modelPath">
    /// An explicit path to a GGML model file. When empty, the path is resolved
    /// from <paramref name="modelSize"/> using the default models directory.
    /// </param>
    /// <param name="modelSize">
    /// The Whisper model size (tiny, base, small, medium, large). Used to resolve
    /// the default model path when <paramref name="modelPath"/> is empty.
    /// </param>
    /// <param name="language">
    /// An optional ISO 639-1 language code to configure the processor with.
    /// When <c>null</c>, the processor uses automatic language detection.
    /// </param>
    /// <param name="prompt">
    /// An optional initial prompt string providing context or custom vocabulary
    /// to bias the transcription engine.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the loading operation.</param>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the resolved model file does not exist on disk.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when an invalid model size is specified and no explicit path is provided.
    /// </exception>
    public async Task LoadModelAsync(
        string modelPath,
        string modelSize,
        string? language = null,
        string? prompt = null,
        GpuAcceleration acceleration = GpuAcceleration.Auto,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        string resolvedPath = ResolveModelPath(modelPath, modelSize);

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException(
                $"Whisper model file not found at: {resolvedPath}. " +
                "Please download the model first using DownloadModelAsync().",
                resolvedPath);
        }

        // Skip reloading if the same model and acceleration mode are already loaded.
        lock (_processorLock)
        {
            if (_loadedModelPath is not null &&
                string.Equals(_loadedModelPath, resolvedPath, StringComparison.OrdinalIgnoreCase) &&
                _loadedAcceleration == acceleration)
            {
                return;
            }
        }

        // Configure the runtime probe order before loading the native library.
        ConfigureRuntimeOrder(acceleration);

        // Build a new processor on a background thread (model loading can be slow).
        var newProcessor = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool useGpu = acceleration != GpuAcceleration.CpuOnly;
            var options = new WhisperFactoryOptions { UseGpu = useGpu };

            var builder = WhisperFactory.FromPath(resolvedPath, options)
                .CreateBuilder()
                .WithGreedySamplingStrategy()
                .ParentBuilder
                .WithThreads(Math.Max(1, Environment.ProcessorCount / 2));

            if (!string.IsNullOrWhiteSpace(language))
            {
                builder.WithLanguage(language);
            }
            else
            {
                builder.WithLanguageDetection();
            }

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                builder.WithPrompt(prompt);
            }

            return builder.Build();
        }, cancellationToken).ConfigureAwait(false);

        // Swap the processor under the lock.
        lock (_processorLock)
        {
            var oldProcessor = _processor;
            _processor = newProcessor;
            _loadedModelPath = resolvedPath;
            _loadedAcceleration = acceleration;

            // Dispose the old processor after releasing it.
            oldProcessor?.Dispose();
        }

        string runtimeInfo = RuntimeOptions.LoadedLibrary.HasValue
            ? RuntimeOptions.LoadedLibrary.Value.ToString()
            : "unknown";
        AppLogger.Log($"Loaded Whisper model from: {resolvedPath} (runtime: {runtimeInfo})");
    }

    /// <summary>
    /// Configures the Whisper.net native library probe order based on the user's
    /// GPU acceleration preference. Must be called before <see cref="WhisperFactory.FromPath"/>.
    /// </summary>
    private static void ConfigureRuntimeOrder(GpuAcceleration acceleration)
    {
        RuntimeOptions.RuntimeLibraryOrder = acceleration switch
        {
            GpuAcceleration.Cuda =>
                [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            GpuAcceleration.Vulkan =>
                [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            GpuAcceleration.CpuOnly =>
                [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            // Auto: use Whisper.net's default probe order
            _ => [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.OpenVino,
                  RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
        };
    }

    /// <summary>
    /// Gets the name of the currently loaded native runtime, or <c>null</c> if no model is loaded.
    /// </summary>
    public string? LoadedRuntimeName =>
        RuntimeOptions.LoadedLibrary?.ToString();

    /// <summary>
    /// Downloads a Whisper GGML model of the specified size from Hugging Face to the
    /// default models directory. Reports download progress via the optional callback.
    /// If the model file already exists, the download is skipped.
    /// </summary>
    /// <param name="modelSize">
    /// The model size to download: tiny, base, small, medium, or large.
    /// </param>
    /// <param name="progressCallback">
    /// An optional callback that receives the download progress as a value between 0.0 and 1.0.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the download operation.</param>
    /// <returns>The full file path to the downloaded (or already existing) model file.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="modelSize"/> is not a valid Whisper model size.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// Thrown when the download fails due to network or server errors.
    /// </exception>
    public async Task<string> DownloadModelAsync(
        string modelSize,
        Action<double>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateModelSize(modelSize);

        string modelsDir = GetDefaultModelsDirectory();
        Directory.CreateDirectory(modelsDir);

        string fileName = $"ggml-{modelSize.ToLowerInvariant()}.bin";
        string filePath = Path.Combine(modelsDir, fileName);

        // If the model file already exists, skip the download.
        if (File.Exists(filePath))
        {
            AppLogger.Log($"Model already exists at: {filePath}");
            progressCallback?.Invoke(1.0);
            return filePath;
        }

        string downloadUrl = string.Format(ModelDownloadBaseUrl, modelSize.ToLowerInvariant());
        string tempFilePath = filePath + ".downloading";

        AppLogger.Log($"Downloading Whisper model from: {downloadUrl}");

        using var response = await SharedHttpClient
            .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        long bytesRead = 0;

        await using var contentStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var fileStream = new FileStream(
            tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        int read;

        while ((read = await contentStream
                   .ReadAsync(buffer, cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);

            bytesRead += read;

            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                progressCallback?.Invoke((double)bytesRead / totalBytes.Value);
            }
        }

        await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Close the file stream before moving the file.
        await fileStream.DisposeAsync().ConfigureAwait(false);

        // Atomically move the temp file to the final path.
        File.Move(tempFilePath, filePath, overwrite: true);

        AppLogger.Log($"Model downloaded successfully to: {filePath}");
        progressCallback?.Invoke(1.0);

        return filePath;
    }

    /// <summary>
    /// Checks whether a model file exists for the given size and optional custom path.
    /// </summary>
    /// <param name="modelPath">
    /// An explicit model file path. When empty, the default path for the given size is checked.
    /// </param>
    /// <param name="modelSize">
    /// The model size used to resolve the default path when <paramref name="modelPath"/> is empty.
    /// </param>
    /// <returns><c>true</c> if the model file exists on disk; otherwise <c>false</c>.</returns>
    public bool IsModelDownloaded(string modelPath, string modelSize)
    {
        string resolvedPath = ResolveModelPath(modelPath, modelSize);
        return File.Exists(resolvedPath);
    }

    /// <summary>
    /// Unloads the currently loaded Whisper model and releases its resources.
    /// </summary>
    public void UnloadModel()
    {
        lock (_processorLock)
        {
            _processor?.Dispose();
            _processor = null;
            _loadedModelPath = null;
        }
    }

    #endregion

    #region Path Resolution

    /// <summary>
    /// Resolves the full file path for a Whisper model, using an explicit path if provided
    /// or falling back to the default models directory based on model size.
    /// </summary>
    /// <param name="modelPath">An explicit model file path, or empty/null for the default.</param>
    /// <param name="modelSize">The model size used to compute the default file name.</param>
    /// <returns>The resolved absolute file path to the model.</returns>
    private static string ResolveModelPath(string modelPath, string modelSize)
    {
        if (!string.IsNullOrWhiteSpace(modelPath))
        {
            return modelPath;
        }

        ValidateModelSize(modelSize);

        string modelsDir = GetDefaultModelsDirectory();
        string fileName = $"ggml-{modelSize.ToLowerInvariant()}.bin";
        return Path.Combine(modelsDir, fileName);
    }

    /// <summary>
    /// Gets the default directory where downloaded Whisper models are stored.
    /// Typically <c>%AppData%/SimpleWhisper/models/</c>.
    /// </summary>
    /// <returns>The full path to the default models directory.</returns>
    private static string GetDefaultModelsDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, AppDataFolderName, ModelsFolderName);
    }

    /// <summary>
    /// Validates that the given model size string is one of the supported Whisper model sizes.
    /// </summary>
    /// <param name="modelSize">The model size to validate.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="modelSize"/> is null, empty, or not a recognized size.
    /// </exception>
    private static void ValidateModelSize(string modelSize)
    {
        if (string.IsNullOrWhiteSpace(modelSize) || !ValidModelSizes.Contains(modelSize))
        {
            throw new ArgumentException(
                $"Invalid model size '{modelSize}'. " +
                $"Supported sizes are: {string.Join(", ", ValidModelSizes)}.",
                nameof(modelSize));
        }
    }

    #endregion

    #region Audio Processing

    /// <summary>
    /// Removes leading and trailing silence from a WAV byte array to reduce
    /// Whisper inference time. Preserves a small margin on each side to avoid
    /// clipping speech onset/offset. Returns the original data if it is too
    /// short or if no meaningful silence is detected.
    /// </summary>
    /// <param name="wavData">A complete WAV file (16 kHz, 16-bit, mono PCM).</param>
    /// <returns>A trimmed WAV byte array, or the original if trimming is not beneficial.</returns>
    internal static byte[] TrimSilenceFromWav(byte[] wavData)
    {
        // Need at least a WAV header plus some samples to work with.
        if (wavData.Length <= WavHeaderSize + SilenceWindowSamples * 2)
            return wavData;

        int pcmLength = wavData.Length - WavHeaderSize;
        int totalSamples = pcmLength / 2; // 16-bit = 2 bytes per sample

        // Find the first non-silent window from the start.
        int firstVoiceSample = 0;
        for (int i = 0; i <= totalSamples - SilenceWindowSamples; i += SilenceWindowSamples)
        {
            if (CalculateWindowRms(wavData, i) > SilenceRmsThreshold)
            {
                firstVoiceSample = i;
                break;
            }

            firstVoiceSample = i + SilenceWindowSamples;
        }

        // Find the first non-silent window from the end.
        int lastVoiceSample = totalSamples;
        for (int i = totalSamples - SilenceWindowSamples; i >= 0; i -= SilenceWindowSamples)
        {
            if (CalculateWindowRms(wavData, i) > SilenceRmsThreshold)
            {
                lastVoiceSample = Math.Min(i + SilenceWindowSamples, totalSamples);
                break;
            }

            lastVoiceSample = i;
        }

        // Apply safety margin (keep some silence on each side).
        firstVoiceSample = Math.Max(0, firstVoiceSample - SilenceMarginSamples);
        lastVoiceSample = Math.Min(totalSamples, lastVoiceSample + SilenceMarginSamples);

        // If trimming wouldn't remove much, return the original.
        int trimmedSamples = lastVoiceSample - firstVoiceSample;
        if (trimmedSamples >= totalSamples - SilenceWindowSamples)
            return wavData;

        // Ensure we have at least some audio to transcribe.
        if (trimmedSamples < SilenceWindowSamples)
            return wavData;

        int trimmedPcmBytes = trimmedSamples * 2;
        int trimmedFileSize = WavHeaderSize + trimmedPcmBytes;
        byte[] result = new byte[trimmedFileSize];

        // Copy the original WAV header.
        Array.Copy(wavData, 0, result, 0, WavHeaderSize);

        // Update RIFF chunk size (bytes 4-7): total file size minus 8.
        int riffChunkSize = trimmedFileSize - 8;
        result[4] = (byte)(riffChunkSize & 0xFF);
        result[5] = (byte)((riffChunkSize >> 8) & 0xFF);
        result[6] = (byte)((riffChunkSize >> 16) & 0xFF);
        result[7] = (byte)((riffChunkSize >> 24) & 0xFF);

        // Update data subchunk size (bytes 40-43): trimmed PCM byte count.
        result[40] = (byte)(trimmedPcmBytes & 0xFF);
        result[41] = (byte)((trimmedPcmBytes >> 8) & 0xFF);
        result[42] = (byte)((trimmedPcmBytes >> 16) & 0xFF);
        result[43] = (byte)((trimmedPcmBytes >> 24) & 0xFF);

        // Copy the trimmed PCM data.
        int sourceOffset = WavHeaderSize + firstVoiceSample * 2;
        Array.Copy(wavData, sourceOffset, result, WavHeaderSize, trimmedPcmBytes);

        int trimmedMs = (totalSamples - trimmedSamples) * 1000 / 16000;
        AppLogger.Log($"Trimmed {trimmedMs}ms of silence from audio ({wavData.Length} -> {result.Length} bytes)");

        return result;
    }

    /// <summary>
    /// Calculates the RMS (Root Mean Square) energy of a window of PCM samples
    /// starting at the given sample index within a WAV byte array.
    /// </summary>
    private static double CalculateWindowRms(byte[] wavData, int sampleStart)
    {
        double sumSquares = 0;
        int count = Math.Min(SilenceWindowSamples, (wavData.Length - WavHeaderSize) / 2 - sampleStart);

        if (count <= 0)
            return 0;

        for (int i = 0; i < count; i++)
        {
            int byteOffset = WavHeaderSize + (sampleStart + i) * 2;
            if (byteOffset + 1 >= wavData.Length)
                break;

            short sample = (short)(wavData[byteOffset] | (wavData[byteOffset + 1] << 8));
            double normalized = sample / 32768.0;
            sumSquares += normalized * normalized;
        }

        return Math.Sqrt(sumSquares / count);
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Releases all resources used by this instance, including the Whisper processor
    /// and synchronization primitives.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_processorLock)
        {
            _processor?.Dispose();
            _processor = null;
            _loadedModelPath = null;
        }

        _transcriptionSemaphore.Dispose();
    }

    /// <summary>
    /// Throws an <see cref="ObjectDisposedException"/> if this instance has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    #endregion
}
