using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Whisper.net;
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
    /// </summary>
    private static readonly HashSet<string> ValidModelSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tiny", "base", "small", "medium", "large"
    };

    #endregion

    #region Fields

    private readonly object _processorLock = new();
    private readonly SemaphoreSlim _transcriptionSemaphore = new(1, 1);

    private WhisperProcessor? _processor;
    private string? _loadedModelPath;
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
            Debug.WriteLine($"Local transcription error: {ex}");
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

        // Skip reloading if the same model is already loaded.
        lock (_processorLock)
        {
            if (_loadedModelPath is not null &&
                string.Equals(_loadedModelPath, resolvedPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        // Build a new processor on a background thread (model loading can be slow).
        var newProcessor = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var builder = WhisperFactory.FromPath(resolvedPath)
                .CreateBuilder()
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

            // Dispose the old processor after releasing it.
            oldProcessor?.Dispose();
        }

        Debug.WriteLine($"Loaded Whisper model from: {resolvedPath}");
    }

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
            Debug.WriteLine($"Model already exists at: {filePath}");
            progressCallback?.Invoke(1.0);
            return filePath;
        }

        string downloadUrl = string.Format(ModelDownloadBaseUrl, modelSize.ToLowerInvariant());
        string tempFilePath = filePath + ".downloading";

        Debug.WriteLine($"Downloading Whisper model from: {downloadUrl}");

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromHours(2); // Large models can take a long time.

        using var response = await httpClient
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

        Debug.WriteLine($"Model downloaded successfully to: {filePath}");
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
