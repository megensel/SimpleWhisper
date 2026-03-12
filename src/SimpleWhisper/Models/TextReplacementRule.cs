using System.Text.Json.Serialization;

namespace SimpleWhisper.Models;

/// <summary>
/// Determines how the <see cref="TextReplacementRule.Find"/> pattern is matched against transcribed text.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReplacementType
{
    /// <summary>Case-sensitive exact string match.</summary>
    Exact,

    /// <summary>Case-insensitive string match.</summary>
    CaseInsensitive,

    /// <summary>Regular expression pattern match.</summary>
    Regex
}

/// <summary>
/// A single find-and-replace rule applied to transcription output before text insertion.
/// Supports exact, case-insensitive, and regex matching modes.
/// </summary>
public class TextReplacementRule
{
    /// <summary>
    /// The pattern to search for in the transcribed text.
    /// Interpreted according to <see cref="Type"/>.
    /// </summary>
    [JsonPropertyName("find")]
    public string Find { get; set; } = string.Empty;

    /// <summary>
    /// The replacement text to substitute when the pattern matches.
    /// For <see cref="ReplacementType.Regex"/>, supports group references like $1, $2.
    /// </summary>
    [JsonPropertyName("replace")]
    public string Replace { get; set; } = string.Empty;

    /// <summary>
    /// How the <see cref="Find"/> pattern should be matched.
    /// </summary>
    [JsonPropertyName("type")]
    public ReplacementType Type { get; set; } = ReplacementType.Exact;

    /// <summary>
    /// Whether this rule is active. Disabled rules are skipped during text processing.
    /// </summary>
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;
}
