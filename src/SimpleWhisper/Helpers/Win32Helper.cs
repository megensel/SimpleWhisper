using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SimpleWhisper.Helpers;

/// <summary>
/// Provides P/Invoke wrappers and helper methods for Win32 window management.
/// Used primarily to configure overlay windows as click-through and tool windows.
/// </summary>
public static partial class Win32Helper
{
    // ──────────────────────────────────────────────────────────────────
    //  Constants
    // ──────────────────────────────────────────────────────────────────

    /// <summary>Retrieves/sets the extended window styles (index for Get/SetWindowLong).</summary>
    public const int GWL_EXSTYLE = -20;

    /// <summary>
    /// Extended style: the window should not receive mouse/pen input.
    /// Input passes through to the window behind it.
    /// </summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>
    /// Extended style: the window is a tool window (thin title bar, does not appear in the taskbar
    /// or Alt+Tab dialog).
    /// </summary>
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>
    /// Extended style: the window is a layered window, enabling per-pixel alpha,
    /// transparency color keys, and alpha blending.
    /// </summary>
    public const int WS_EX_LAYERED = 0x00080000;

    /// <summary>
    /// Extended style: the window does not become the foreground window when the user clicks it.
    /// </summary>
    public const int WS_EX_NOACTIVATE = 0x08000000;

    // ──────────────────────────────────────────────────────────────────
    //  P/Invoke declarations (source-generated)
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Retrieves the handle to the foreground (currently active) window.
    /// </summary>
    /// <returns>The handle to the foreground window, or <see cref="IntPtr.Zero"/> if no window is in the foreground.</returns>
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    /// <summary>
    /// Retrieves information about the specified window. The function also retrieves
    /// the value at the specified offset into the extra window memory.
    /// </summary>
    /// <param name="hWnd">A handle to the window.</param>
    /// <param name="nIndex">The zero-based offset or a constant such as <see cref="GWL_EXSTYLE"/>.</param>
    /// <returns>The requested value.</returns>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    /// <summary>
    /// Changes an attribute of the specified window. The function also sets a value
    /// at the specified offset in the extra window memory.
    /// </summary>
    /// <param name="hWnd">A handle to the window.</param>
    /// <param name="nIndex">The zero-based offset or a constant such as <see cref="GWL_EXSTYLE"/>.</param>
    /// <param name="dwNewLong">The replacement value.</param>
    /// <returns>The previous value of the specified offset.</returns>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ──────────────────────────────────────────────────────────────────
    //  Helper methods
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gets the Win32 window handle (HWND) for a WPF <see cref="Window"/>.
    /// The window must be initialized (shown at least once or have its source initialized).
    /// </summary>
    /// <param name="window">The WPF window to retrieve the handle for.</param>
    /// <returns>The native window handle.</returns>
    public static IntPtr GetWindowHandle(Window window)
    {
        return new WindowInteropHelper(window).Handle;
    }

    /// <summary>
    /// Configures a window to be click-through, preventing it from receiving any mouse input.
    /// Also applies the tool-window, layered, and no-activate extended styles so the window
    /// does not appear in the taskbar or Alt+Tab switcher and never steals focus.
    /// </summary>
    /// <param name="hwnd">The native window handle to modify.</param>
    public static void MakeWindowClickThrough(IntPtr hwnd)
    {
        int currentStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

        SetWindowLong(hwnd, GWL_EXSTYLE,
            currentStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
    }

    /// <summary>
    /// Convenience overload that accepts a WPF <see cref="Window"/> directly.
    /// Retrieves its handle via <see cref="WindowInteropHelper"/> and applies click-through styles.
    /// </summary>
    /// <param name="window">The WPF window to make click-through.</param>
    public static void MakeWindowClickThrough(Window window)
    {
        var hwnd = GetWindowHandle(window);
        MakeWindowClickThrough(hwnd);
    }

    /// <summary>
    /// Enables or disables the click-through (<see cref="WS_EX_TRANSPARENT"/>) style on a window
    /// at runtime, leaving the tool-window, layered, and no-activate styles in place. Used by the
    /// overlay to accept clicks on its cancel button only while a session is active.
    /// </summary>
    /// <param name="window">The WPF window to modify. No-op if its handle is not yet created.</param>
    /// <param name="enabled"><c>true</c> to make the window click-through; <c>false</c> to let it receive mouse input.</param>
    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
            return;

        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        style = enabled ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;

        SetWindowLong(hwnd, GWL_EXSTYLE,
            style | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
    }
}
