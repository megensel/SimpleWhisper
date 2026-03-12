using System.Text.RegularExpressions;
using SimpleWhisper.Helpers;
using SimpleWhisper.Models;

namespace SimpleWhisper.Services;

/// <summary>
/// Applies a multi-stage text processing pipeline to raw transcription output before
/// it is inserted into the target application. The pipeline includes spoken punctuation
/// conversion, user-defined text replacements, emoji insertion, and final cleanup.
/// This service is stateless and all methods are static.
/// </summary>
public static class TextProcessingService
{
    #region Spoken Punctuation Mappings

    /// <summary>
    /// Ordered list of spoken punctuation phrases and their corresponding punctuation marks.
    /// Longer phrases are listed first to prevent partial matches (e.g., "exclamation point"
    /// must be matched before "exclamation").
    /// </summary>
    private static readonly (string Spoken, string Punctuation, bool IsSentenceEnding)[] SpokenPunctuationMap =
    [
        // Multi-word phrases first (longest match wins).
        ("full stop", ".", true),
        ("question mark", "?", true),
        ("exclamation mark", "!", true),
        ("exclamation point", "!", true),
        ("new paragraph", "\n", false),
        ("new line", "\n", false),
        ("open parenthesis", "(", false),
        ("close parenthesis", ")", false),
        ("open paren", "(", false),
        ("close paren", ")", false),
        ("open quote", "\"", false),
        ("close quote", "\"", false),

        // Single-word phrases.
        ("period", ".", true),
        ("comma", ",", false),
        ("colon", ":", false),
        ("semicolon", ";", false),
        ("ellipsis", "...", false),
        ("dash", "-", false),
        ("hyphen", "-", false),
    ];

    #endregion

    #region Emoji Mappings

    /// <summary>
    /// Dictionary of spoken emoji names mapped to their Unicode emoji characters.
    /// Used when emoji insertion is enabled. Keys are lowercase for case-insensitive matching.
    /// </summary>
    private static readonly Dictionary<string, string> EmojiMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Smileys & People
        ["smile"] = "\U0001F642",
        ["smiley"] = "\U0001F603",
        ["grin"] = "\U0001F601",
        ["laugh"] = "\U0001F602",
        ["joy"] = "\U0001F602",
        ["rofl"] = "\U0001F923",
        ["wink"] = "\U0001F609",
        ["blush"] = "\U0001F60A",
        ["innocent"] = "\U0001F607",
        ["love"] = "\U0001F60D",
        ["heart eyes"] = "\U0001F60D",
        ["kiss"] = "\U0001F618",
        ["tongue"] = "\U0001F61B",
        ["thinking"] = "\U0001F914",
        ["shush"] = "\U0001F92B",
        ["hug"] = "\U0001F917",
        ["cool"] = "\U0001F60E",
        ["nerd"] = "\U0001F913",
        ["confused"] = "\U0001F615",
        ["worried"] = "\U0001F61F",
        ["cry"] = "\U0001F622",
        ["sob"] = "\U0001F62D",
        ["angry"] = "\U0001F620",
        ["rage"] = "\U0001F621",
        ["scream"] = "\U0001F631",
        ["fear"] = "\U0001F628",
        ["shock"] = "\U0001F632",
        ["surprise"] = "\U0001F62E",
        ["yawn"] = "\U0001F971",
        ["sick"] = "\U0001F912",
        ["vomit"] = "\U0001F92E",
        ["devil"] = "\U0001F608",
        ["skull"] = "\U0001F480",
        ["ghost"] = "\U0001F47B",
        ["alien"] = "\U0001F47D",
        ["robot"] = "\U0001F916",
        ["poop"] = "\U0001F4A9",
        ["clown"] = "\U0001F921",

        // Gestures & Body
        ["thumbs up"] = "\U0001F44D",
        ["thumbs down"] = "\U0001F44E",
        ["clap"] = "\U0001F44F",
        ["wave"] = "\U0001F44B",
        ["handshake"] = "\U0001F91D",
        ["pray"] = "\U0001F64F",
        ["muscle"] = "\U0001F4AA",
        ["fist"] = "\u270A",
        ["peace"] = "\u270C\uFE0F",
        ["ok"] = "\U0001F44C",
        ["point up"] = "\U0001F446",
        ["point down"] = "\U0001F447",
        ["point left"] = "\U0001F448",
        ["point right"] = "\U0001F449",
        ["raised hand"] = "\u270B",
        ["middle finger"] = "\U0001F595",
        ["eyes"] = "\U0001F440",
        ["brain"] = "\U0001F9E0",

        // Hearts & Symbols
        ["heart"] = "\u2764\uFE0F",
        ["red heart"] = "\u2764\uFE0F",
        ["blue heart"] = "\U0001F499",
        ["green heart"] = "\U0001F49A",
        ["yellow heart"] = "\U0001F49B",
        ["purple heart"] = "\U0001F49C",
        ["black heart"] = "\U0001F5A4",
        ["broken heart"] = "\U0001F494",
        ["sparkling heart"] = "\U0001F496",
        ["star"] = "\u2B50",
        ["sparkles"] = "\u2728",
        ["lightning"] = "\u26A1",
        ["fire"] = "\U0001F525",
        ["explosion"] = "\U0001F4A5",
        ["rainbow"] = "\U0001F308",
        ["sun"] = "\u2600\uFE0F",
        ["moon"] = "\U0001F319",
        ["cloud"] = "\u2601\uFE0F",
        ["snowflake"] = "\u2744\uFE0F",
        ["umbrella"] = "\u2602\uFE0F",
        ["water"] = "\U0001F4A7",

        // Animals
        ["dog"] = "\U0001F436",
        ["cat"] = "\U0001F431",
        ["mouse"] = "\U0001F42D",
        ["rabbit"] = "\U0001F430",
        ["fox"] = "\U0001F98A",
        ["bear"] = "\U0001F43B",
        ["panda"] = "\U0001F43C",
        ["koala"] = "\U0001F428",
        ["lion"] = "\U0001F981",
        ["unicorn"] = "\U0001F984",
        ["bee"] = "\U0001F41D",
        ["butterfly"] = "\U0001F98B",
        ["snake"] = "\U0001F40D",
        ["turtle"] = "\U0001F422",
        ["octopus"] = "\U0001F419",
        ["penguin"] = "\U0001F427",
        ["bird"] = "\U0001F426",
        ["eagle"] = "\U0001F985",
        ["whale"] = "\U0001F433",
        ["dolphin"] = "\U0001F42C",
        ["fish"] = "\U0001F41F",

        // Food & Drink
        ["pizza"] = "\U0001F355",
        ["hamburger"] = "\U0001F354",
        ["fries"] = "\U0001F35F",
        ["hotdog"] = "\U0001F32D",
        ["taco"] = "\U0001F32E",
        ["sushi"] = "\U0001F363",
        ["cookie"] = "\U0001F36A",
        ["cake"] = "\U0001F382",
        ["ice cream"] = "\U0001F368",
        ["donut"] = "\U0001F369",
        ["chocolate"] = "\U0001F36B",
        ["coffee"] = "\u2615",
        ["tea"] = "\U0001F375",
        ["beer"] = "\U0001F37A",
        ["wine"] = "\U0001F377",
        ["apple"] = "\U0001F34E",
        ["banana"] = "\U0001F34C",
        ["cherry"] = "\U0001F352",
        ["grapes"] = "\U0001F347",
        ["watermelon"] = "\U0001F349",
        ["avocado"] = "\U0001F951",
        ["eggplant"] = "\U0001F346",
        ["corn"] = "\U0001F33D",

        // Objects & Activities
        ["trophy"] = "\U0001F3C6",
        ["medal"] = "\U0001F3C5",
        ["crown"] = "\U0001F451",
        ["gem"] = "\U0001F48E",
        ["ring"] = "\U0001F48D",
        ["gift"] = "\U0001F381",
        ["balloon"] = "\U0001F388",
        ["party"] = "\U0001F389",
        ["confetti"] = "\U0001F38A",
        ["music"] = "\U0001F3B5",
        ["guitar"] = "\U0001F3B8",
        ["microphone"] = "\U0001F3A4",
        ["movie"] = "\U0001F3AC",
        ["camera"] = "\U0001F4F7",
        ["phone"] = "\U0001F4F1",
        ["computer"] = "\U0001F4BB",
        ["keyboard"] = "\u2328\uFE0F",
        ["light bulb"] = "\U0001F4A1",
        ["money"] = "\U0001F4B0",
        ["dollar"] = "\U0001F4B5",
        ["email"] = "\U0001F4E7",
        ["mailbox"] = "\U0001F4EB",
        ["lock"] = "\U0001F512",
        ["key"] = "\U0001F511",
        ["magnifying glass"] = "\U0001F50D",
        ["hammer"] = "\U0001F528",
        ["wrench"] = "\U0001F527",
        ["bomb"] = "\U0001F4A3",
        ["pill"] = "\U0001F48A",
        ["rocket"] = "\U0001F680",
        ["airplane"] = "\u2708\uFE0F",
        ["car"] = "\U0001F697",
        ["bike"] = "\U0001F6B2",

        // Flags & Miscellaneous
        ["check"] = "\u2705",
        ["checkmark"] = "\u2705",
        ["cross"] = "\u274C",
        ["x mark"] = "\u274C",
        ["warning"] = "\u26A0\uFE0F",
        ["question"] = "\u2753",
        ["exclamation"] = "\u2757",
        ["hundred"] = "\U0001F4AF",
        ["plus"] = "\u2795",
        ["minus"] = "\u2796",
        ["infinity"] = "\u267E\uFE0F",
        ["recycle"] = "\u267B\uFE0F",
        ["flag"] = "\U0001F3F4",
        ["white flag"] = "\U0001F3F3\uFE0F",
    };

    /// <summary>
    /// Pre-compiled regex that matches the pattern "[name] emoji" (case-insensitive)
    /// where [name] is one or more word characters or spaces.
    /// </summary>
    private static readonly Regex EmojiPatternRegex = new(
        @"([\w ]+?)\s+emoji\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    #endregion

    #region Public API

    /// <summary>
    /// Processes raw transcription text through the full post-processing pipeline.
    /// </summary>
    /// <param name="rawText">The raw transcription text from the speech-to-text engine.</param>
    /// <param name="settings">
    /// The current application settings, which control which processing stages are enabled
    /// and provide the text replacement rules.
    /// </param>
    /// <returns>
    /// The processed text with punctuation, replacements, emojis, and cleanup applied.
    /// Returns an empty string if the input is null or whitespace.
    /// </returns>
    public static string Process(string rawText, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return string.Empty;

        ArgumentNullException.ThrowIfNull(settings);

        string text = rawText;

        // Stage 1: Spoken punctuation conversion.
        if (settings.SpokenPunctuationEnabled)
        {
            text = ApplySpokenPunctuation(text);
        }

        // Stage 2: User-defined text replacement rules.
        if (settings.TextReplacements is { Count: > 0 })
        {
            text = ApplyTextReplacements(text, settings.TextReplacements);
        }

        // Stage 3: Emoji insertion.
        if (settings.EmojiInsertionEnabled)
        {
            text = ApplyEmojiInsertion(text);
        }

        // Stage 4: Final cleanup.
        text = ApplyFinalCleanup(text);

        return text;
    }

    #endregion

    #region Stage 1: Spoken Punctuation

    /// <summary>
    /// Replaces spoken punctuation words (e.g., "period", "comma", "question mark") with
    /// their corresponding punctuation characters. After sentence-ending punctuation,
    /// the next word is automatically capitalized.
    /// </summary>
    /// <param name="text">The input text potentially containing spoken punctuation words.</param>
    /// <returns>The text with spoken punctuation words replaced by punctuation characters.</returns>
    private static string ApplySpokenPunctuation(string text)
    {
        foreach (var (spoken, punctuation, isSentenceEnding) in SpokenPunctuationMap)
        {
            // Build a pattern that matches the spoken phrase as a whole word,
            // optionally preceded by a space. Case-insensitive.
            // We consume the leading space (if any) so that "hello period world" becomes "hello. World".
            string pattern = @"(?<=\s|^)" + Regex.Escape(spoken) + @"(?=\s|$)";

            text = Regex.Replace(text, pattern, match =>
            {
                // Determine whether there's a preceding space in the original text.
                // The lookbehind doesn't consume it, so we check the character before the match.
                int matchStart = match.Index;
                bool hasLeadingSpace = matchStart > 0 && text[matchStart - 1] == ' ';

                // For punctuation that attaches to the previous word (.,!?;:...),
                // we want to remove the leading space.
                return punctuation;
            }, RegexOptions.IgnoreCase);
        }

        // Remove spaces before punctuation marks that attach to the previous word.
        text = Regex.Replace(text, @"\s+([.,!?;:)\]""'}\-])", "$1");

        // Add a space after punctuation if followed directly by a letter (but not after newlines).
        text = Regex.Replace(text, @"([.,!?;:])([A-Za-z])", "$1 $2");

        // Remove space after opening brackets/quotes.
        text = Regex.Replace(text, @"([\[(""'{])\s+", "$1");

        // Capitalize the first letter after sentence-ending punctuation (.!?).
        text = Regex.Replace(text, @"([.!?])\s+(\w)", match =>
            match.Groups[1].Value + " " + match.Groups[2].Value.ToUpperInvariant());

        // Capitalize the first letter after a newline.
        text = Regex.Replace(text, @"\n\s*(\w)", match =>
            "\n" + match.Groups[1].Value.ToUpperInvariant());

        return text;
    }

    #endregion

    #region Stage 2: Text Replacements

    /// <summary>
    /// Applies user-defined text replacement rules in order. Each enabled rule's
    /// <see cref="TextReplacementRule.Find"/> pattern is matched against the text
    /// according to its <see cref="TextReplacementRule.Type"/> and replaced with
    /// <see cref="TextReplacementRule.Replace"/>.
    /// </summary>
    /// <param name="text">The input text to apply replacements to.</param>
    /// <param name="rules">The ordered list of replacement rules.</param>
    /// <returns>The text with all applicable replacements applied.</returns>
    private static string ApplyTextReplacements(string text, List<TextReplacementRule> rules)
    {
        foreach (var rule in rules)
        {
            if (!rule.IsEnabled || string.IsNullOrEmpty(rule.Find))
                continue;

            try
            {
                text = rule.Type switch
                {
                    ReplacementType.Exact =>
                        text.Replace(rule.Find, rule.Replace, StringComparison.Ordinal),

                    ReplacementType.CaseInsensitive =>
                        text.Replace(rule.Find, rule.Replace, StringComparison.OrdinalIgnoreCase),

                    ReplacementType.Regex =>
                        Regex.Replace(text, rule.Find, rule.Replace),

                    _ => text
                };
            }
            catch (RegexParseException ex)
            {
                // Invalid regex pattern in user rule; skip it and log the error.
                AppLogger.Log(
                    $"Skipping invalid regex replacement rule '{rule.Find}': {ex.Message}");
            }
        }

        return text;
    }

    #endregion

    #region Stage 3: Emoji Insertion

    /// <summary>
    /// Replaces patterns matching "[name] emoji" with the corresponding Unicode emoji
    /// character, if the name is found in the emoji dictionary. Unrecognized names are
    /// left unchanged.
    /// </summary>
    /// <param name="text">The input text potentially containing emoji patterns.</param>
    /// <returns>The text with recognized emoji patterns replaced by Unicode emoji characters.</returns>
    private static string ApplyEmojiInsertion(string text)
    {
        return EmojiPatternRegex.Replace(text, match =>
        {
            string name = match.Groups[1].Value.Trim();

            if (EmojiMap.TryGetValue(name, out string? emoji))
            {
                return emoji;
            }

            // Unrecognized emoji name; leave the original text unchanged.
            return match.Value;
        });
    }

    #endregion

    #region Stage 4: Final Cleanup

    /// <summary>
    /// Performs final cleanup on the processed text: trims leading/trailing whitespace,
    /// collapses multiple consecutive spaces into a single space, and removes trailing
    /// whitespace from each line.
    /// </summary>
    /// <param name="text">The input text to clean up.</param>
    /// <returns>The cleaned-up text.</returns>
    private static string ApplyFinalCleanup(string text)
    {
        // Collapse multiple spaces into a single space.
        text = Regex.Replace(text, @" {2,}", " ");

        // Remove trailing whitespace from each line.
        text = Regex.Replace(text, @"[ \t]+(?=\n|$)", "");

        // Trim the entire string.
        text = text.Trim();

        return text;
    }

    #endregion
}
