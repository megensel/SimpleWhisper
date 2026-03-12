using System.Windows;
using System.Windows.Input;
using SimpleWhisper.Models;

namespace SimpleWhisper.Views.Controls;

/// <summary>
/// A dialog window that captures a keyboard key or mouse button to create an <see cref="InputTrigger"/>.
/// The user presses a key or clicks a mouse button while modifier checkboxes are selected.
/// </summary>
public partial class TriggerRecorderControl : Window
{
    private string? _capturedKey;
    private MouseTriggerButton _capturedMouseButton = MouseTriggerButton.None;
    private bool _hasCaptured;

    /// <summary>
    /// The resulting trigger after the user captures a shortcut and clicks OK.
    /// Null if the dialog was cancelled.
    /// </summary>
    public InputTrigger? CapturedTrigger { get; private set; }

    public TriggerRecorderControl()
    {
        InitializeComponent();
        Focusable = true;
        Focus();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Key / Mouse Capture
    // ──────────────────────────────────────────────────────────────────

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // Ignore modifier-only keys so they can be used as checkboxes
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or
            Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or
            Key.System or Key.LWin or Key.RWin)
        {
            return;
        }

        // Only capture if keyboard mode is selected
        if (KeyboardRadio.IsChecked != true)
            return;

        _capturedKey = e.Key.ToString();
        _capturedMouseButton = MouseTriggerButton.None;
        _hasCaptured = true;

        UpdateDisplay();
        e.Handled = true;
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Only capture if mouse mode is selected
        if (MouseRadio.IsChecked != true)
            return;

        MouseTriggerButton? button = e.ChangedButton switch
        {
            MouseButton.Middle => MouseTriggerButton.MiddleButton,
            MouseButton.XButton1 => MouseTriggerButton.XButton1,
            MouseButton.XButton2 => MouseTriggerButton.XButton2,
            _ => null // Ignore left/right click
        };

        if (button is null)
            return;

        _capturedMouseButton = button.Value;
        _capturedKey = null;
        _hasCaptured = true;

        UpdateDisplay();
        e.Handled = true;
    }

    // ──────────────────────────────────────────────────────────────────
    //  UI Helpers
    // ──────────────────────────────────────────────────────────────────

    private void OnTriggerTypeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        // Reset capture when switching modes
        _capturedKey = null;
        _capturedMouseButton = MouseTriggerButton.None;
        _hasCaptured = false;

        if (KeyboardRadio.IsChecked == true)
            InstructionText.Text = "Press a key to set the trigger...";
        else
            InstructionText.Text = "Click a mouse button (Middle, XButton1, XButton2)...";

        UpdateDisplay();
    }

    private void OnModifierChanged(object sender, RoutedEventArgs e)
    {
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (!_hasCaptured)
        {
            CapturedDisplay.Text = "(none)";
            return;
        }

        // Build the display using the same logic as InputTrigger.DisplayName
        var parts = new List<string>(4);

        if (CtrlCheck.IsChecked == true) parts.Add("Ctrl");
        if (AltCheck.IsChecked == true) parts.Add("Alt");
        if (ShiftCheck.IsChecked == true) parts.Add("Shift");

        if (_capturedKey is not null)
        {
            parts.Add(_capturedKey);
        }
        else
        {
            parts.Add(_capturedMouseButton switch
            {
                MouseTriggerButton.MiddleButton => "Middle Click",
                MouseTriggerButton.XButton1 => "XButton1",
                MouseTriggerButton.XButton2 => "XButton2",
                _ => "None"
            });
        }

        CapturedDisplay.Text = string.Join("+", parts);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Buttons
    // ──────────────────────────────────────────────────────────────────

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!_hasCaptured)
        {
            MessageBox.Show(
                "Please capture a key or mouse button first.",
                "Record Shortcut",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        CapturedTrigger = new InputTrigger
        {
            Type = _capturedKey is not null ? TriggerType.Keyboard : TriggerType.Mouse,
            Key = _capturedKey ?? "F8",
            MouseButton = _capturedMouseButton,
            Ctrl = CtrlCheck.IsChecked == true,
            Alt = AltCheck.IsChecked == true,
            Shift = ShiftCheck.IsChecked == true
        };

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
