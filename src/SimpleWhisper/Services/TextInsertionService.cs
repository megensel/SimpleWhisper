using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using WindowsInput;
using SimpleWhisper.Helpers;

namespace SimpleWhisper.Services;

/// <summary>
/// Inserts transcribed text into the currently focused application by temporarily placing
/// the text on the clipboard, simulating a Ctrl+V paste, and then restoring the original
/// clipboard contents. All clipboard operations are marshaled to the WPF UI (STA) thread.
/// </summary>
public sealed class TextInsertionService : IDisposable
{
    #region Constants

    /// <summary>
    /// Delay in milliseconds to wait after the paste keystroke before restoring the clipboard.
    /// This gives the target application time to process the paste operation.
    /// </summary>
    private const int PasteDelayMs = 300;

    #endregion

    #region Fields

    private readonly InputSimulator _inputSimulator;
    private readonly ClipboardHelper _clipboardHelper;
    private bool _disposed;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="TextInsertionService"/> class
    /// with a new <see cref="InputSimulator"/> and <see cref="ClipboardHelper"/>.
    /// </summary>
    public TextInsertionService()
    {
        _inputSimulator = new InputSimulator();
        _clipboardHelper = new ClipboardHelper();
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Inserts the specified text into the currently focused application by performing
    /// a clipboard-based paste operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The method follows this workflow:
    /// <list type="number">
    ///   <item>Save the current clipboard contents via <see cref="ClipboardHelper"/>.</item>
    ///   <item>Set the clipboard text to the transcribed text (on the STA/UI thread).</item>
    ///   <item>Simulate a <c>Ctrl+V</c> keystroke via <see cref="InputSimulator"/>.</item>
    ///   <item>Wait a short delay for the target application to process the paste.</item>
    ///   <item>Restore the original clipboard contents.</item>
    /// </list>
    /// </para>
    /// <para>
    /// All clipboard operations must execute on an STA thread. This method marshals to the
    /// WPF application dispatcher when necessary.
    /// </para>
    /// </remarks>
    /// <param name="text">The text to insert. If null or empty, the method returns immediately.</param>
    /// <returns>A task that completes when the text insertion and clipboard restoration are finished.</returns>
    public async Task InsertTextAsync(string text)
    {
        ThrowIfDisposed();

        if (string.IsNullOrEmpty(text))
        {
            AppLogger.Log("InsertTextAsync called with empty text; skipping.");
            return;
        }

        try
        {
            AppLogger.Log($"InsertTextAsync starting for {text.Length} chars...");

            // Step 1: Save current clipboard on the UI thread.
            await RunOnUIThreadAsync(() =>
            {
                _clipboardHelper.Save();
            }).ConfigureAwait(false);

            // Step 2: Set clipboard to our text on the UI thread.
            await RunOnUIThreadAsync(() =>
            {
                try
                {
                    Clipboard.SetDataObject(text, copy: true);
                    AppLogger.Log("Clipboard set to transcribed text.");
                }
                catch (ExternalException ex)
                {
                    AppLogger.Log($"Failed to set clipboard text: {ex.Message}");
                    throw;
                }
            }).ConfigureAwait(false);

            // Step 3: Simulate Ctrl+V paste.
            _inputSimulator.Keyboard.ModifiedKeyStroke(
                VirtualKeyCode.CONTROL,
                VirtualKeyCode.VK_V);

            AppLogger.Log("Ctrl+V simulated.");

            // Step 4: Wait for the target application to process the paste.
            await Task.Delay(PasteDelayMs).ConfigureAwait(false);

            // Step 5: Restore the original clipboard contents on the UI thread.
            await RunOnUIThreadAsync(() => _clipboardHelper.RestoreAsync()).ConfigureAwait(false);

            AppLogger.Log("InsertTextAsync completed.");
        }
        catch (ExternalException ex)
        {
            // Clipboard was locked by another process; log and continue.
            AppLogger.Log($"Clipboard operation failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Text insertion failed: {ex}");
            throw;
        }
    }

    #endregion

    #region Thread Marshaling

    /// <summary>
    /// Executes the specified async function on the WPF application dispatcher (UI/STA thread).
    /// If already on the UI thread, the function is invoked directly.
    /// </summary>
    /// <param name="func">The async function to execute on the UI thread.</param>
    /// <returns>A task that completes when the async function has finished executing.</returns>
    private static async Task RunOnUIThreadAsync(Func<Task> func)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                await func();
                return;
            }

            throw new InvalidOperationException(
                "Cannot access the clipboard: no WPF dispatcher is available and " +
                "the current thread is not an STA thread.");
        }

        if (dispatcher.CheckAccess())
        {
            await func();
        }
        else
        {
            await dispatcher.InvokeAsync(func);
        }
    }

    /// <summary>
    /// Executes the specified action on the WPF application dispatcher (UI/STA thread).
    /// If already on the UI thread, the action is invoked directly.
    /// </summary>
    /// <param name="action">The action to execute on the UI thread.</param>
    /// <returns>A task that completes when the action has finished executing.</returns>
    private static async Task RunOnUIThreadAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            // Fallback: no WPF application running (e.g., unit test context).
            // Try the current dispatcher if we are on an STA thread.
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                action();
                return;
            }

            throw new InvalidOperationException(
                "Cannot access the clipboard: no WPF dispatcher is available and " +
                "the current thread is not an STA thread.");
        }

        if (dispatcher.CheckAccess())
        {
            // Already on the UI thread; invoke directly.
            action();
        }
        else
        {
            // Marshal to the UI thread and await completion.
            await dispatcher.InvokeAsync(action, DispatcherPriority.Normal);
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Releases resources used by this instance. The underlying <see cref="InputSimulator"/>
    /// does not hold unmanaged resources, but this method is provided for consistency.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // InputSimulator and ClipboardHelper do not implement IDisposable,
        // but we null them to help the GC and signal disposal.
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
