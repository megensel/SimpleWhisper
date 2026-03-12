using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SimpleWhisper.Helpers;

/// <summary>
/// Saves and restores the current Windows clipboard contents so the application
/// can temporarily use the clipboard for text insertion without losing the user's data.
/// </summary>
public sealed class ClipboardHelper
{
    /// <summary>
    /// Maximum number of retry attempts when the clipboard is locked by another application.
    /// </summary>
    private const int MaxRetries = 3;

    /// <summary>
    /// Delay in milliseconds between clipboard retry attempts.
    /// </summary>
    private const int RetryDelayMs = 100;

    private string? _savedText;
    private BitmapSource? _savedImage;
    private StringCollection? _savedFileDropList;
    private bool _hasSavedState;

    /// <summary>
    /// Gets a value indicating whether clipboard state has been saved and is available for restoration.
    /// </summary>
    public bool HasSavedState => _hasSavedState;

    /// <summary>
    /// Captures the current clipboard contents (text, image, and file drop list).
    /// Must be called on an STA thread. If the clipboard is locked by another application,
    /// the save is silently skipped.
    /// </summary>
    public void Save()
    {
        _savedText = null;
        _savedImage = null;
        _savedFileDropList = null;
        _hasSavedState = false;

        try
        {
            if (Clipboard.ContainsText())
            {
                _savedText = Clipboard.GetText();
            }

            if (Clipboard.ContainsImage())
            {
                _savedImage = Clipboard.GetImage();

                // Freeze the BitmapSource so it can be accessed across threads.
                if (_savedImage is { IsFrozen: false, CanFreeze: true })
                {
                    _savedImage.Freeze();
                }
            }

            if (Clipboard.ContainsFileDropList())
            {
                _savedFileDropList = Clipboard.GetFileDropList();
            }

            _hasSavedState = true;

            AppLogger.Log($"Clipboard saved — text: {(_savedText is not null ? $"{_savedText.Length} chars" : "none")}, " +
                         $"image: {(_savedImage is not null ? "yes" : "none")}, " +
                         $"files: {(_savedFileDropList?.Count ?? 0)}");
        }
        catch (ExternalException ex)
        {
            AppLogger.Log($"Clipboard save failed (locked by another app): {ex.Message}");
        }
    }

    /// <summary>
    /// Restores the previously saved clipboard contents immediately.
    /// Must be called on an STA thread.
    /// If the clipboard is locked, retries up to <see cref="MaxRetries"/> times.
    /// </summary>
    public void Restore()
    {
        if (!_hasSavedState)
        {
            AppLogger.Log("Clipboard restore skipped — no saved state.");
            return;
        }

        RestoreClipboardContents();
    }

    /// <summary>
    /// Restores the previously saved clipboard contents with retry logic for locked clipboards.
    /// Tries up to <see cref="MaxRetries"/> times with a short delay between attempts.
    /// </summary>
    private void RestoreClipboardContents()
    {
        try
        {
            // Build a DataObject containing all saved formats.
            var dataObject = new DataObject();
            bool hasContent = false;

            if (_savedText is not null)
            {
                dataObject.SetText(_savedText);
                hasContent = true;
            }

            if (_savedImage is not null)
            {
                dataObject.SetImage(_savedImage);
                hasContent = true;
            }

            if (_savedFileDropList is { Count: > 0 })
            {
                dataObject.SetFileDropList(_savedFileDropList);
                hasContent = true;
            }

            // Retry loop — the clipboard may be briefly locked by the target app processing the paste.
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    if (hasContent)
                    {
                        Clipboard.SetDataObject(dataObject, copy: true);
                    }
                    else
                    {
                        // The clipboard was empty before; clear it.
                        Clipboard.Clear();
                    }

                    AppLogger.Log($"Clipboard restored successfully on attempt {attempt}.");
                    return; // Success — exit the retry loop.
                }
                catch (ExternalException) when (attempt < MaxRetries)
                {
                    AppLogger.Log($"Clipboard restore attempt {attempt}/{MaxRetries} failed (locked). Retrying in {RetryDelayMs}ms...");
                    Thread.Sleep(RetryDelayMs);
                }
            }

            // Final attempt already threw — this line is unreachable, but for safety:
            AppLogger.Log("Clipboard restore failed after all retry attempts.");
        }
        catch (ExternalException ex)
        {
            AppLogger.Log($"Clipboard restore failed after {MaxRetries} attempts: {ex.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Clipboard restore unexpected error: {ex.Message}");
        }
        finally
        {
            // Release references to allow garbage collection.
            _savedText = null;
            _savedImage = null;
            _savedFileDropList = null;
            _hasSavedState = false;
        }
    }
}
