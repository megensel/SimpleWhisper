using H.Hooks;
using SimpleWhisper.Helpers;
using SimpleWhisper.Models;
using HKey = H.Hooks.Key;

namespace SimpleWhisper.Services;

/// <summary>
/// Global input trigger system that listens for a configurable keyboard key or mouse button
/// (with optional Ctrl/Alt/Shift modifiers) and fires activation/deactivation events.
/// Supports both hold-to-record and toggle recording modes.
///
/// Uses low-level Windows hooks via H.Hooks. Keyboard hooks are observational only and never
/// block input. Mouse hooks suppress trigger-matching button events to prevent the
/// configured mouse button (e.g. middle-click) from reaching other applications.
/// </summary>
public sealed class InputTriggerService : IDisposable
{
    // ── Hook instances ───────────────────────────────────────────────────
    private LowLevelKeyboardHook? _keyboardHook;
    private LowLevelMouseHook? _mouseHook;

    // ── Configuration (guarded by _configLock) ───────────────────────────
    private readonly object _configLock = new();
    private InputTrigger _trigger;
    private RecordingMode _mode;

    // ── Modifier key state tracked from the keyboard hook ────────────────
    // Written on the hook callback thread, read when evaluating mouse triggers.
    private volatile bool _ctrlHeld;
    private volatile bool _altHeld;
    private volatile bool _shiftHeld;

    // ── Trigger state ────────────────────────────────────────────────────
    // _isPressed: true while the trigger key/button is physically held (used to filter repeats and for hold mode).
    // _isActive:  true while recording is logically active (relevant for toggle mode).
    private volatile bool _isPressed;
    private volatile bool _isActive;

    private bool _disposed;

    // ── Events ───────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when the configured trigger is activated (recording should start).
    /// This event fires on a thread-pool thread; consumers must marshal to the UI thread if needed.
    /// </summary>
    public event Action? TriggerActivated;

    /// <summary>
    /// Raised when the configured trigger is deactivated (recording should stop).
    /// This event fires on a thread-pool thread; consumers must marshal to the UI thread if needed.
    /// </summary>
    public event Action? TriggerDeactivated;

    /// <summary>
    /// Creates a new <see cref="InputTriggerService"/> with the specified trigger configuration and recording mode.
    /// The service is created in the stopped state; call <see cref="Start"/> to install hooks.
    /// </summary>
    /// <param name="trigger">The input trigger to listen for.</param>
    /// <param name="mode">The recording mode (hold-to-record or toggle).</param>
    public InputTriggerService(InputTrigger trigger, RecordingMode mode)
    {
        _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        _mode = mode;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Public API
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Installs low-level keyboard and mouse hooks and begins listening for the configured trigger.
    /// Safe to call multiple times; subsequent calls are no-ops if hooks are already running.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_keyboardHook is not null)
            return; // Already started.

        // ── Keyboard hook ────────────────────────────────────────────
        _keyboardHook = new LowLevelKeyboardHook
        {
            // OneUpEvent = true (default) prevents flooding us with repeated keydown while held.
            // We still do our own guard via _isPressed for extra safety.
            HandleModifierKeys = true,  // We need Ctrl/Alt/Shift events for modifier tracking.
            IsExtendedMode = false,
            Handling = false            // Observational only — never block input.
        };

        _keyboardHook.Down += OnKeyboardDown;
        _keyboardHook.Up += OnKeyboardUp;
        _keyboardHook.ExceptionOccurred += OnHookException;
        _keyboardHook.Start();

        // ── Mouse hook ───────────────────────────────────────────────
        _mouseHook = new LowLevelMouseHook
        {
            GenerateMouseMoveEvents = false, // We don't need move events.
            AddKeyboardKeys = false,         // We track modifiers ourselves via the keyboard hook.
            Handling = true                  // Allows suppressing trigger button events via e.IsHandled.
        };

        _mouseHook.Down += OnMouseDown;
        _mouseHook.Up += OnMouseUp;
        _mouseHook.ExceptionOccurred += OnHookException;
        _mouseHook.Start();
    }

    /// <summary>
    /// Uninstalls all hooks and resets trigger state.
    /// Safe to call multiple times or when already stopped.
    /// </summary>
    public void Stop()
    {
        DisposeHooks();
        ResetState();
    }

    /// <summary>
    /// Changes the trigger configuration at runtime. If the service is running, the new trigger
    /// takes effect immediately. Any in-progress activation is cleanly deactivated first.
    /// </summary>
    /// <param name="trigger">The new input trigger configuration.</param>
    public void UpdateTrigger(InputTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        lock (_configLock)
        {
            _trigger = trigger;
        }

        // Deactivate gracefully if the old trigger was active, since the new trigger hasn't been pressed yet.
        if (_isActive)
        {
            _isActive = false;
            _isPressed = false;
            RaiseTriggerDeactivated();
        }
        else
        {
            _isPressed = false;
        }
    }

    /// <summary>
    /// Changes the recording mode at runtime (hold-to-record or toggle).
    /// If recording is currently active, it is deactivated cleanly before switching modes.
    /// </summary>
    /// <param name="mode">The new recording mode.</param>
    public void UpdateMode(RecordingMode mode)
    {
        lock (_configLock)
        {
            _mode = mode;
        }

        // Deactivate to avoid ambiguous state when switching modes mid-recording.
        if (_isActive)
        {
            _isActive = false;
            _isPressed = false;
            RaiseTriggerDeactivated();
        }
        else
        {
            _isPressed = false;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Keyboard hook handlers
    // ════════════════════════════════════════════════════════════════════

    private void OnKeyboardDown(object? sender, KeyboardEventArgs e)
    {
        UpdateModifierState(e.CurrentKey, pressed: true);

        // Read current config snapshot under lock.
        InputTrigger trigger;
        RecordingMode mode;
        lock (_configLock)
        {
            trigger = _trigger;
            mode = _mode;
        }

        // Only process keyboard triggers.
        if (trigger.Type != TriggerType.Keyboard)
            return;

        // Check if this is the configured trigger key.
        if (!IsMatchingKeyboardKey(e.CurrentKey, trigger.Key))
            return;

        // Verify modifier requirements.
        if (!AreModifiersSatisfied(trigger))
            return;

        // Filter key repeats — ignore if already pressed.
        if (_isPressed)
            return;

        _isPressed = true;

        switch (mode)
        {
            case RecordingMode.HoldToRecord:
                _isActive = true;
                RaiseTriggerActivated();
                break;

            case RecordingMode.Toggle:
                if (_isActive)
                {
                    _isActive = false;
                    RaiseTriggerDeactivated();
                }
                else
                {
                    _isActive = true;
                    RaiseTriggerActivated();
                }
                break;
        }
    }

    private void OnKeyboardUp(object? sender, KeyboardEventArgs e)
    {
        UpdateModifierState(e.CurrentKey, pressed: false);

        // Read current config snapshot under lock.
        InputTrigger trigger;
        RecordingMode mode;
        lock (_configLock)
        {
            trigger = _trigger;
            mode = _mode;
        }

        if (trigger.Type != TriggerType.Keyboard)
            return;

        if (!IsMatchingKeyboardKey(e.CurrentKey, trigger.Key))
            return;

        _isPressed = false;

        // In hold mode, releasing the key deactivates the trigger.
        if (mode == RecordingMode.HoldToRecord && _isActive)
        {
            _isActive = false;
            RaiseTriggerDeactivated();
        }
        // In toggle mode, key-up is ignored.
    }

    // ════════════════════════════════════════════════════════════════════
    //  Mouse hook handlers
    // ════════════════════════════════════════════════════════════════════

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        InputTrigger trigger;
        RecordingMode mode;
        lock (_configLock)
        {
            trigger = _trigger;
            mode = _mode;
        }

        if (trigger.Type != TriggerType.Mouse)
            return;

        if (!IsMatchingMouseButton(e.CurrentKey, trigger.MouseButton))
            return;

        // Verify modifier requirements (tracked via the keyboard hook).
        if (!AreModifiersSatisfied(trigger))
            return;

        // Suppress the mouse event so it doesn't reach other applications
        // (e.g. prevent middle-click from triggering auto-scroll or stealing focus).
        e.IsHandled = true;

        // Filter repeats (shouldn't normally happen for mouse, but guard anyway).
        if (_isPressed)
            return;

        _isPressed = true;

        switch (mode)
        {
            case RecordingMode.HoldToRecord:
                _isActive = true;
                RaiseTriggerActivated();
                break;

            case RecordingMode.Toggle:
                if (_isActive)
                {
                    _isActive = false;
                    RaiseTriggerDeactivated();
                }
                else
                {
                    _isActive = true;
                    RaiseTriggerActivated();
                }
                break;
        }
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        InputTrigger trigger;
        RecordingMode mode;
        lock (_configLock)
        {
            trigger = _trigger;
            mode = _mode;
        }

        if (trigger.Type != TriggerType.Mouse)
            return;

        if (!IsMatchingMouseButton(e.CurrentKey, trigger.MouseButton))
            return;

        // Suppress the release event if we tracked the corresponding press,
        // so the full click cycle is consumed and never reaches other apps.
        if (_isPressed)
            e.IsHandled = true;

        _isPressed = false;

        if (mode == RecordingMode.HoldToRecord && _isActive)
        {
            _isActive = false;
            RaiseTriggerDeactivated();
        }
        // In toggle mode, mouse-up is ignored.
    }

    // ════════════════════════════════════════════════════════════════════
    //  Modifier tracking
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Updates the tracked modifier state when a key is pressed or released.
    /// Called for every keyboard event, regardless of whether the key is the configured trigger.
    /// </summary>
    private void UpdateModifierState(HKey key, bool pressed)
    {
        switch (key)
        {
            case HKey.Ctrl:
            case HKey.LCtrl:
            case HKey.RCtrl:
                _ctrlHeld = pressed;
                break;

            case HKey.Alt:
            case HKey.LAlt:
            case HKey.RAlt:
                _altHeld = pressed;
                break;

            case HKey.Shift:
            case HKey.LShift:
            case HKey.RShift:
                _shiftHeld = pressed;
                break;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if the currently held modifier keys match the trigger's requirements.
    /// Each modifier in the trigger must be held, and modifiers NOT in the trigger must NOT be held.
    /// This prevents "Ctrl+F8" from firing when only "F8" is configured (and vice versa).
    /// </summary>
    private bool AreModifiersSatisfied(InputTrigger trigger)
    {
        return _ctrlHeld == trigger.Ctrl
            && _altHeld == trigger.Alt
            && _shiftHeld == trigger.Shift;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Key / button matching
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks whether a <see cref="HKey"/> from the keyboard hook matches the configured key name string.
    /// The comparison uses the <see cref="HKey"/> enum name (case-insensitive).
    /// </summary>
    private static bool IsMatchingKeyboardKey(HKey currentKey, string configuredKeyName)
    {
        // The InputTrigger.Key stores the key name as a string matching H.Hooks.Key enum names
        // (e.g., "F8", "Space", "A"). We parse to compare.
        if (string.IsNullOrEmpty(configuredKeyName))
            return false;

        if (!Enum.TryParse<HKey>(configuredKeyName, ignoreCase: true, out var expectedKey))
            return false;

        return currentKey == expectedKey;
    }

    /// <summary>
    /// Checks whether a <see cref="HKey"/> from the mouse hook matches the configured <see cref="MouseTriggerButton"/>.
    /// </summary>
    private static bool IsMatchingMouseButton(HKey currentKey, MouseTriggerButton configuredButton)
    {
        return configuredButton switch
        {
            MouseTriggerButton.MiddleButton => currentKey is HKey.MButton or HKey.MouseMiddle,
            MouseTriggerButton.XButton1 => currentKey is HKey.XButton1 or HKey.MouseXButton1,
            MouseTriggerButton.XButton2 => currentKey is HKey.XButton2 or HKey.MouseXButton2,
            _ => false
        };
    }

    // ════════════════════════════════════════════════════════════════════
    //  Event raising helpers
    // ════════════════════════════════════════════════════════════════════

    private void RaiseTriggerActivated()
    {
        try
        {
            TriggerActivated?.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[InputTriggerService] TriggerActivated handler threw: {ex}");
        }
    }

    private void RaiseTriggerDeactivated()
    {
        try
        {
            TriggerDeactivated?.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[InputTriggerService] TriggerDeactivated handler threw: {ex}");
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Hook exception handler
    // ════════════════════════════════════════════════════════════════════

    private static void OnHookException(object? sender, Exception e)
    {
        AppLogger.Log($"[InputTriggerService] Hook exception: {e}");
    }

    // ════════════════════════════════════════════════════════════════════
    //  Cleanup
    // ════════════════════════════════════════════════════════════════════

    private void ResetState()
    {
        _isPressed = false;
        _isActive = false;
        _ctrlHeld = false;
        _altHeld = false;
        _shiftHeld = false;
    }

    private void DisposeHooks()
    {
        if (_keyboardHook is not null)
        {
            _keyboardHook.Down -= OnKeyboardDown;
            _keyboardHook.Up -= OnKeyboardUp;
            _keyboardHook.ExceptionOccurred -= OnHookException;

            try { _keyboardHook.Dispose(); }
            catch (Exception ex) { AppLogger.Log($"[InputTriggerService] Keyboard hook dispose error: {ex}"); }

            _keyboardHook = null;
        }

        if (_mouseHook is not null)
        {
            _mouseHook.Down -= OnMouseDown;
            _mouseHook.Up -= OnMouseUp;
            _mouseHook.ExceptionOccurred -= OnHookException;

            try { _mouseHook.Dispose(); }
            catch (Exception ex) { AppLogger.Log($"[InputTriggerService] Mouse hook dispose error: {ex}"); }

            _mouseHook = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeHooks();
        ResetState();
    }
}
