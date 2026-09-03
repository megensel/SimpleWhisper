using System.Text.Json.Serialization;

namespace SimpleWhisper.Models;

/// <summary>
/// Specifies whether the trigger is a keyboard key or a mouse button.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TriggerType
{
    /// <summary>A keyboard key trigger.</summary>
    Keyboard,

    /// <summary>A mouse button trigger.</summary>
    Mouse
}

/// <summary>
/// Mouse buttons that can be used as recording triggers.
/// Standard left/right buttons are excluded to avoid interfering with normal usage.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MouseTriggerButton
{
    /// <summary>No mouse button assigned.</summary>
    None,

    /// <summary>Middle mouse button (scroll wheel click).</summary>
    MiddleButton,

    /// <summary>First extended mouse button (typically "Back").</summary>
    XButton1,

    /// <summary>Second extended mouse button (typically "Forward").</summary>
    XButton2
}

/// <summary>
/// Represents a keyboard key or mouse button trigger with optional modifier keys.
/// Used to configure the push-to-talk or toggle-to-record input binding.
/// </summary>
public class InputTrigger
{
    /// <summary>
    /// Whether this trigger uses a keyboard key or a mouse button.
    /// </summary>
    [JsonPropertyName("type")]
    public TriggerType Type { get; set; } = TriggerType.Keyboard;

    /// <summary>
    /// The keyboard key name (matches <see cref="System.Windows.Input.Key"/> enum names).
    /// Only used when <see cref="Type"/> is <see cref="TriggerType.Keyboard"/>.
    /// </summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "F8";

    /// <summary>
    /// The mouse button to use as a trigger.
    /// Only used when <see cref="Type"/> is <see cref="TriggerType.Mouse"/>.
    /// </summary>
    [JsonPropertyName("mouseButton")]
    public MouseTriggerButton MouseButton { get; set; } = MouseTriggerButton.None;

    /// <summary>Whether the Ctrl modifier must be held.</summary>
    [JsonPropertyName("ctrl")]
    public bool Ctrl { get; set; }

    /// <summary>Whether the Alt modifier must be held.</summary>
    [JsonPropertyName("alt")]
    public bool Alt { get; set; }

    /// <summary>Whether the Shift modifier must be held.</summary>
    [JsonPropertyName("shift")]
    public bool Shift { get; set; }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="other"/> describes the same binding
    /// (type, key, mouse button, and modifiers). Used to skip no-op reconfiguration.
    /// </summary>
    public bool IsSameBindingAs(InputTrigger? other) =>
        other is not null
        && Type == other.Type
        && string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase)
        && MouseButton == other.MouseButton
        && Ctrl == other.Ctrl
        && Alt == other.Alt
        && Shift == other.Shift;

    /// <summary>
    /// Human-readable display string for the trigger, e.g. "F8", "Ctrl+Middle Click", "Shift+XButton1".
    /// </summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var parts = new List<string>(4);

            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");

            parts.Add(Type switch
            {
                TriggerType.Keyboard => Key,
                TriggerType.Mouse => MouseButton switch
                {
                    MouseTriggerButton.MiddleButton => "Middle Click",
                    MouseTriggerButton.XButton1 => "XButton1",
                    MouseTriggerButton.XButton2 => "XButton2",
                    _ => "None"
                },
                _ => "Unknown"
            });

            return string.Join("+", parts);
        }
    }
}
