using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using SimpleWhisper.Models;

namespace SimpleWhisper.Services;

/// <summary>
/// Manages the system tray icon, context menu, and state indicators using H.NotifyIcon.
/// Implements <see cref="IDisposable"/> to ensure the tray icon is removed on shutdown.
/// </summary>
public sealed class TrayManager : IDisposable
{
    // ──────────────────────────────────────────────────────────────────
    //  Constants
    // ──────────────────────────────────────────────────────────────────

    private const int IconSize = 32;
    private const string AppTitle = "SimpleWhisper";

    // ──────────────────────────────────────────────────────────────────
    //  Fields
    // ──────────────────────────────────────────────────────────────────

    private readonly TaskbarIcon _taskbarIcon;
    private readonly MenuItem _toggleListeningItem;
    private TrayState _currentState = TrayState.Idle;
    private bool _isListening;
    private bool _disposed;

    // Icon data stored as byte arrays so we can create fresh Icon instances
    // each time. H.NotifyIcon disposes the old Icon when a new one is assigned,
    // so we cannot reuse pre-cached Icon objects.
    private readonly byte[] _idleIconData;
    private readonly byte[] _recordingIconData;
    private readonly byte[] _processingIconData;

    // ──────────────────────────────────────────────────────────────────
    //  Events
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when the user toggles listening on or off via the tray context menu.
    /// The <see cref="bool"/> argument is <c>true</c> when listening was started.
    /// </summary>
    public event EventHandler<bool>? ListeningToggled;

    // ──────────────────────────────────────────────────────────────────
    //  Constructor
    // ──────────────────────────────────────────────────────────────────

    public TrayManager(bool initialListeningState = true)
    {
        // Pre-render icon data as byte arrays (ICO format).
        _idleIconData = RenderCircleIconData(Color.Gray);
        _recordingIconData = RenderCircleIconData(Color.Red);
        _processingIconData = RenderCircleIconData(Color.FromArgb(255, 200, 80)); // orange-yellow

        // Sync initial listening state with what the app is actually doing.
        _isListening = initialListeningState;

        // Build context menu
        _toggleListeningItem = new MenuItem
        {
            Header = _isListening ? "Stop Listening" : "Start Listening"
        };
        _toggleListeningItem.Click += OnToggleListeningClick;

        var settingsItem = new MenuItem { Header = "Settings" };
        settingsItem.Click += OnSettingsClick;

        var aboutItem = new MenuItem { Header = "About" };
        aboutItem.Click += OnAboutClick;

        var quitItem = new MenuItem { Header = "Quit" };
        quitItem.Click += OnQuitClick;

        var contextMenu = new ContextMenu
        {
            Items =
            {
                settingsItem,
                new Separator(),
                _toggleListeningItem,
                new Separator(),
                aboutItem,
                quitItem
            }
        };

        // Create the taskbar icon programmatically
        _taskbarIcon = new TaskbarIcon
        {
            Icon = IconFromData(_idleIconData),
            ToolTipText = $"{AppTitle} - Ready",
            ContextMenu = contextMenu,
            NoLeftClickDelay = true
        };

        _taskbarIcon.TrayMouseDoubleClick += OnTrayDoubleClick;

        // Force the icon to appear in the system tray immediately.
        _taskbarIcon.ForceCreate();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Public API
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gets the current tray state.
    /// </summary>
    public TrayState CurrentState => _currentState;

    /// <summary>
    /// Shows a balloon tip notification from the tray icon.
    /// </summary>
    public void ShowBalloonTip(string title, string message, H.NotifyIcon.Core.NotificationIcon icon = H.NotifyIcon.Core.NotificationIcon.Info)
    {
        _taskbarIcon.ShowNotification(title, message, icon);
    }

    /// <summary>
    /// Updates the tray icon and tooltip to reflect the given application state.
    /// </summary>
    public void SetState(TrayState state)
    {
        _currentState = state;

        // Create a fresh Icon each time — H.NotifyIcon disposes the previous one.
        _taskbarIcon.Icon = state switch
        {
            TrayState.Recording => IconFromData(_recordingIconData),
            TrayState.Processing => IconFromData(_processingIconData),
            _ => IconFromData(_idleIconData)
        };

        _taskbarIcon.ToolTipText = state switch
        {
            TrayState.Recording => $"{AppTitle} - Recording...",
            TrayState.Processing => $"{AppTitle} - Processing...",
            _ => $"{AppTitle} - Ready"
        };
    }

    // ──────────────────────────────────────────────────────────────────
    //  Menu handlers
    // ──────────────────────────────────────────────────────────────────

    private void OnToggleListeningClick(object sender, RoutedEventArgs e)
    {
        _isListening = !_isListening;
        _toggleListeningItem.Header = _isListening ? "Stop Listening" : "Start Listening";
        ListeningToggled?.Invoke(this, _isListening);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        ShowSettingsWindow();
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            $"{AppTitle}\nVersion 1.0.0\n\nSpeech-to-text anywhere with a hotkey.",
            $"About {AppTitle}",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static void OnQuitClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void OnTrayDoubleClick(object sender, RoutedEventArgs e)
    {
        ShowSettingsWindow();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Settings window
    // ──────────────────────────────────────────────────────────────────

    private static void ShowSettingsWindow()
    {
        // Look for an already-open SettingsWindow; if found, bring it to front.
        foreach (Window window in Application.Current.Windows)
        {
            if (window is Views.SettingsWindow existingWindow)
            {
                existingWindow.Activate();
                return;
            }
        }

        var settingsWindow = new Views.SettingsWindow();
        settingsWindow.Show();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Icon generation
    // ──────────────────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// Renders a 32x32 circle icon of the given color and returns the raw ICO
    /// bytes. The byte array can be used to create fresh <see cref="Icon"/>
    /// instances via <see cref="IconFromData"/> any number of times.
    /// </summary>
    private static byte[] RenderCircleIconData(Color fillColor)
    {
        using var bitmap = new Bitmap(IconSize, IconSize);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            const int padding = 2;
            var rect = new Rectangle(padding, padding, IconSize - padding * 2, IconSize - padding * 2);

            using var brush = new SolidBrush(fillColor);
            graphics.FillEllipse(brush, rect);

            // Subtle border for better visibility on both light and dark taskbars.
            using var pen = new Pen(Color.FromArgb(180, 60, 60, 60), 1.2f);
            graphics.DrawEllipse(pen, rect);
        }

        var hIcon = bitmap.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            using var ms = new MemoryStream();
            tempIcon.Save(ms);
            return ms.ToArray();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// Creates a new <see cref="Icon"/> from pre-rendered ICO byte data.
    /// Each call returns a fresh, independently-owned Icon instance that
    /// H.NotifyIcon can safely dispose without affecting future calls.
    /// </summary>
    private static Icon IconFromData(byte[] data)
    {
        return new Icon(new MemoryStream(data));
    }

    // ──────────────────────────────────────────────────────────────────
    //  IDisposable
    // ──────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _taskbarIcon.Dispose();
    }
}
