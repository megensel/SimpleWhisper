using System.Collections.ObjectModel;
using System.Windows.Input;
using SimpleWhisper.Helpers;
using SimpleWhisper.Models;
using SimpleWhisper.Services;

namespace SimpleWhisper.ViewModels;

/// <summary>
/// View model for the Settings window. Exposes all user-configurable options
/// grouped by tab (General, Audio, Transcription, Text Processing).
/// Every property setter saves settings immediately via <see cref="SettingsService"/>.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;

    // ──────────────────────────────────────────────────────────────────
    //  Backing fields
    // ──────────────────────────────────────────────────────────────────

    private System.Windows.Threading.DispatcherTimer? _saveDebounceTimer;
    private static readonly TimeSpan SaveDebounceInterval = TimeSpan.FromMilliseconds(300);

    // General
    private string _triggerDisplayName = string.Empty;
    private bool _isRecordingHoldMode;
    private bool _isRecordingToggleMode;
    private bool _startWithWindows;
    private bool _playSoundFeedback;
    private bool _showOverlay;
    private bool _autoCheckForUpdates;

    // Audio
    private List<string> _availableDevices = [];
    private int _selectedDeviceIndex;
    private string _selectedDeviceName = string.Empty;
    private bool _reduceOutputVolumeWhileRecording;
    private bool _muteOutputWhileRecording;
    private int _outputVolumeReductionPercent;

    // Transcription
    private bool _isCloudEngine;
    private bool _isLocalEngine;
    private string _apiKey = string.Empty;
    private string _selectedLanguage = string.Empty;
    private bool _autoDetectLanguage;
    private string _localModelSize = string.Empty;
    private GpuAcceleration _gpuAcceleration;
    private bool _trimSilence;
    private bool _realtimeTranscription;

    // Text Processing
    private bool _spokenPunctuationEnabled;
    private bool _emojiInsertionEnabled;
    private string _customVocabularyText = string.Empty;

    // ──────────────────────────────────────────────────────────────────
    //  Constructor
    // ──────────────────────────────────────────────────────────────────

    public SettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

        // Initialize commands
        AddReplacementCommand = new RelayCommand(AddReplacement);
        RemoveReplacementCommand = new RelayCommand(RemoveReplacement);

        // Load current settings into VM properties
        LoadFromSettings();
    }

    // ──────────────────────────────────────────────────────────────────
    //  General Tab Properties
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read-only display name of the current trigger (e.g. "Ctrl+F8").
    /// </summary>
    public string TriggerDisplayName
    {
        get => _triggerDisplayName;
        private set => SetProperty(ref _triggerDisplayName, value);
    }

    /// <summary>
    /// True when Recording Mode is Hold-to-Record.
    /// </summary>
    public bool IsRecordingHoldMode
    {
        get => _isRecordingHoldMode;
        set
        {
            if (SetProperty(ref _isRecordingHoldMode, value))
            {
                if (value)
                {
                    _isRecordingToggleMode = false;
                    OnPropertyChanged(nameof(IsRecordingToggleMode));
                    _settingsService.Settings.RecordingMode = RecordingMode.HoldToRecord;
                    SaveSettings();
                }
            }
        }
    }

    /// <summary>
    /// True when Recording Mode is Toggle.
    /// </summary>
    public bool IsRecordingToggleMode
    {
        get => _isRecordingToggleMode;
        set
        {
            if (SetProperty(ref _isRecordingToggleMode, value))
            {
                if (value)
                {
                    _isRecordingHoldMode = false;
                    OnPropertyChanged(nameof(IsRecordingHoldMode));
                    _settingsService.Settings.RecordingMode = RecordingMode.Toggle;
                    SaveSettings();
                }
            }
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (SetProperty(ref _startWithWindows, value))
            {
                _settingsService.Settings.StartWithWindows = value;
                StartupService.SetStartWithWindows(value);
                SaveSettings();
            }
        }
    }

    public bool PlaySoundFeedback
    {
        get => _playSoundFeedback;
        set
        {
            if (SetProperty(ref _playSoundFeedback, value))
            {
                _settingsService.Settings.PlaySoundFeedback = value;
                SaveSettings();
            }
        }
    }

    public bool ShowOverlay
    {
        get => _showOverlay;
        set
        {
            if (SetProperty(ref _showOverlay, value))
            {
                _settingsService.Settings.ShowOverlay = value;
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// When true, the app automatically checks GitHub for new releases.
    /// </summary>
    public bool AutoCheckForUpdates
    {
        get => _autoCheckForUpdates;
        set
        {
            if (SetProperty(ref _autoCheckForUpdates, value))
            {
                _settingsService.Settings.AutoCheckForUpdates = value;
                SaveSettings();
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Audio Tab Properties
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// List of microphone device names for the ComboBox.
    /// </summary>
    public List<string> AvailableDevices
    {
        get => _availableDevices;
        private set => SetProperty(ref _availableDevices, value);
    }

    /// <summary>
    /// Zero-based index of the selected microphone device.
    /// </summary>
    public int SelectedDeviceIndex
    {
        get => _selectedDeviceIndex;
        set
        {
            if (SetProperty(ref _selectedDeviceIndex, value))
            {
                _settingsService.Settings.MicrophoneDeviceIndex = value;

                // Update display name
                if (value >= 0 && value < _availableDevices.Count)
                    SelectedDeviceName = _availableDevices[value];

                SaveSettings();
            }
        }
    }

    /// <summary>
    /// Friendly name of the selected microphone for display.
    /// </summary>
    public string SelectedDeviceName
    {
        get => _selectedDeviceName;
        private set => SetProperty(ref _selectedDeviceName, value);
    }

    /// <summary>
    /// When true, system output volume is reduced while recording.
    /// </summary>
    public bool ReduceOutputVolumeWhileRecording
    {
        get => _reduceOutputVolumeWhileRecording;
        set
        {
            if (SetProperty(ref _reduceOutputVolumeWhileRecording, value))
            {
                _settingsService.Settings.ReduceOutputVolumeWhileRecording = value;
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// When true, system output is fully muted while recording.
    /// </summary>
    public bool MuteOutputWhileRecording
    {
        get => _muteOutputWhileRecording;
        set
        {
            if (SetProperty(ref _muteOutputWhileRecording, value))
            {
                _settingsService.Settings.MuteOutputWhileRecording = value;
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// Percentage (0-100) to reduce system output volume while recording.
    /// </summary>
    public int OutputVolumeReductionPercent
    {
        get => _outputVolumeReductionPercent;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _outputVolumeReductionPercent, value))
            {
                _settingsService.Settings.OutputVolumeReductionPercent = value;
                SaveSettings();
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Transcription Tab Properties
    // ──────────────────────────────────────────────────────────────────

    public bool IsCloudEngine
    {
        get => _isCloudEngine;
        set
        {
            if (SetProperty(ref _isCloudEngine, value))
            {
                if (value)
                {
                    _isLocalEngine = false;
                    OnPropertyChanged(nameof(IsLocalEngine));
                    _settingsService.Settings.Engine = TranscriptionEngine.Cloud;
                    SaveSettings();
                }
            }
        }
    }

    public bool IsLocalEngine
    {
        get => _isLocalEngine;
        set
        {
            if (SetProperty(ref _isLocalEngine, value))
            {
                if (value)
                {
                    _isCloudEngine = false;
                    OnPropertyChanged(nameof(IsCloudEngine));
                    _settingsService.Settings.Engine = TranscriptionEngine.Local;
                    SaveSettings();
                }
            }
        }
    }

    /// <summary>
    /// OpenAI API key for cloud transcription.
    /// </summary>
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (SetProperty(ref _apiKey, value))
            {
                _settingsService.Settings.OpenAIApiKey = value;
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// Currently selected language code (e.g. "en").
    /// </summary>
    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (SetProperty(ref _selectedLanguage, value))
            {
                _settingsService.Settings.Language = value;
                SaveSettings();
            }
        }
    }

    public bool AutoDetectLanguage
    {
        get => _autoDetectLanguage;
        set
        {
            if (SetProperty(ref _autoDetectLanguage, value))
            {
                _settingsService.Settings.AutoDetectLanguage = value;
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// Whisper model size: tiny, base, small, medium, large.
    /// </summary>
    public string LocalModelSize
    {
        get => _localModelSize;
        set
        {
            if (SetProperty(ref _localModelSize, value))
            {
                _settingsService.Settings.LocalModelSize = value;
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// Available Whisper-supported languages for the language ComboBox.
    /// Each entry is "code|Display Name" so we can bind display and value separately.
    /// </summary>
    public List<LanguageItem> AvailableLanguages { get; } = BuildLanguageList();

    /// <summary>
    /// Available model sizes with size hints for the model size ComboBox.
    /// Includes English-only (.en) variants that are faster for English transcription.
    /// </summary>
    public List<string> AvailableModelSizes { get; } =
    [
        "tiny",
        "tiny.en",
        "base",
        "base.en",
        "small",
        "small.en",
        "medium",
        "medium.en",
        "large"
    ];

    /// <summary>
    /// Hardware acceleration mode for local Whisper inference.
    /// </summary>
    public GpuAcceleration GpuAcceleration
    {
        get => _gpuAcceleration;
        set
        {
            if (SetProperty(ref _gpuAcceleration, value))
            {
                _settingsService.Settings.GpuAcceleration = value;
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// Whether to trim leading/trailing silence from audio before transcription.
    /// Reduces inference time by skipping silent portions of the recording.
    /// </summary>
    public bool TrimSilence
    {
        get => _trimSilence;
        set
        {
            if (SetProperty(ref _trimSilence, value))
            {
                _settingsService.Settings.TrimSilence = value;
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// Whether to show live transcription text in the overlay during recording.
    /// Only applies to the local Whisper engine, which supports chunked processing.
    /// </summary>
    public bool RealtimeTranscription
    {
        get => _realtimeTranscription;
        set
        {
            if (SetProperty(ref _realtimeTranscription, value))
            {
                _settingsService.Settings.RealtimeTranscription = value;
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// Available GPU acceleration options for the ComboBox.
    /// </summary>
    public List<GpuAccelerationItem> AvailableGpuOptions { get; } =
    [
        new(GpuAcceleration.Auto, "Auto-detect"),
        new(GpuAcceleration.Cuda, "NVIDIA CUDA"),
        new(GpuAcceleration.Vulkan, "Vulkan"),
        new(GpuAcceleration.CpuOnly, "CPU Only"),
    ];

    // ──────────────────────────────────────────────────────────────────
    //  Text Processing Tab Properties
    // ──────────────────────────────────────────────────────────────────

    public bool SpokenPunctuationEnabled
    {
        get => _spokenPunctuationEnabled;
        set
        {
            if (SetProperty(ref _spokenPunctuationEnabled, value))
            {
                _settingsService.Settings.SpokenPunctuationEnabled = value;
                SaveSettings();
            }
        }
    }

    public bool EmojiInsertionEnabled
    {
        get => _emojiInsertionEnabled;
        set
        {
            if (SetProperty(ref _emojiInsertionEnabled, value))
            {
                _settingsService.Settings.EmojiInsertionEnabled = value;
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// Observable collection of text replacement rules for the DataGrid.
    /// </summary>
    public ObservableCollection<TextReplacementRule> TextReplacements { get; } = [];

    /// <summary>
    /// Custom vocabulary as a multiline string (one word per line).
    /// </summary>
    public string CustomVocabularyText
    {
        get => _customVocabularyText;
        set
        {
            if (SetProperty(ref _customVocabularyText, value))
            {
                // Parse lines into the settings list
                _settingsService.Settings.CustomVocabulary = value
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .ToList();
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// Available replacement types for the DataGrid ComboBox column.
    /// </summary>
    public List<ReplacementType> AvailableReplacementTypes { get; } =
        [ReplacementType.Exact, ReplacementType.CaseInsensitive, ReplacementType.Regex];

    // ──────────────────────────────────────────────────────────────────
    //  Commands
    // ──────────────────────────────────────────────────────────────────

    public ICommand AddReplacementCommand { get; }
    public ICommand RemoveReplacementCommand { get; }

    // ──────────────────────────────────────────────────────────────────
    //  Public Methods
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates the trigger from a <see cref="TriggerRecorderControl"/> capture.
    /// </summary>
    public void ApplyTrigger(InputTrigger trigger)
    {
        _settingsService.Settings.Trigger = trigger;
        TriggerDisplayName = trigger.DisplayName;
        SaveSettings();
    }

    /// <summary>
    /// Placeholder for recording a new trigger. Will be invoked by the
    /// TriggerRecorderControl dialog once implemented.
    /// </summary>
    public void RecordTrigger()
    {
        // The SettingsWindow code-behind opens the TriggerRecorderControl dialog
        // and calls ApplyTrigger with the result.
    }

    /// <summary>
    /// Synchronizes the TextReplacements collection back to settings and saves.
    /// Call this when the DataGrid finishes editing a row.
    /// </summary>
    public void SaveTextReplacements()
    {
        _settingsService.Settings.TextReplacements = [.. TextReplacements];
        SaveSettings();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Private Helpers
    // ──────────────────────────────────────────────────────────────────

    private void LoadFromSettings()
    {
        var s = _settingsService.Settings;

        // General
        TriggerDisplayName = s.Trigger.DisplayName;
        _isRecordingHoldMode = s.RecordingMode == RecordingMode.HoldToRecord;
        _isRecordingToggleMode = s.RecordingMode == RecordingMode.Toggle;
        _startWithWindows = s.StartWithWindows;
        _playSoundFeedback = s.PlaySoundFeedback;
        _showOverlay = s.ShowOverlay;
        _autoCheckForUpdates = s.AutoCheckForUpdates;

        // Audio
        RefreshDevices();
        _reduceOutputVolumeWhileRecording = s.ReduceOutputVolumeWhileRecording;
        _muteOutputWhileRecording = s.MuteOutputWhileRecording;
        _outputVolumeReductionPercent = s.OutputVolumeReductionPercent;

        // Transcription
        _isCloudEngine = s.Engine == TranscriptionEngine.Cloud;
        _isLocalEngine = s.Engine == TranscriptionEngine.Local;
        _apiKey = s.OpenAIApiKey;
        _selectedLanguage = s.Language;
        _autoDetectLanguage = s.AutoDetectLanguage;
        _localModelSize = s.LocalModelSize;
        _gpuAcceleration = s.GpuAcceleration;
        _trimSilence = s.TrimSilence;
        _realtimeTranscription = s.RealtimeTranscription;

        // Text Processing
        _spokenPunctuationEnabled = s.SpokenPunctuationEnabled;
        _emojiInsertionEnabled = s.EmojiInsertionEnabled;

        TextReplacements.Clear();
        foreach (var rule in s.TextReplacements)
            TextReplacements.Add(rule);

        _customVocabularyText = string.Join("\n", s.CustomVocabulary);
    }

    private void RefreshDevices()
    {
        try
        {
            var devices = AudioCaptureService.GetAvailableDevices();
            AvailableDevices = devices.Select(d => d.Name).ToList();

            var savedIndex = _settingsService.Settings.MicrophoneDeviceIndex;
            if (savedIndex >= 0 && savedIndex < AvailableDevices.Count)
            {
                _selectedDeviceIndex = savedIndex;
                _selectedDeviceName = AvailableDevices[savedIndex];
            }
            else if (AvailableDevices.Count > 0)
            {
                _selectedDeviceIndex = 0;
                _selectedDeviceName = AvailableDevices[0];
            }
            else
            {
                _selectedDeviceIndex = -1;
                _selectedDeviceName = "(No devices found)";
            }
        }
        catch
        {
            AvailableDevices = [];
            _selectedDeviceIndex = -1;
            _selectedDeviceName = "(Error detecting devices)";
        }
    }

    private void AddReplacement()
    {
        var rule = new TextReplacementRule();
        TextReplacements.Add(rule);
        _settingsService.Settings.TextReplacements.Add(rule);
        SaveSettings();
    }

    private void RemoveReplacement(object? parameter)
    {
        if (parameter is TextReplacementRule rule)
        {
            TextReplacements.Remove(rule);
            _settingsService.Settings.TextReplacements.Remove(rule);
            SaveSettings();
        }
    }

    private void SaveSettings()
    {
        if (_saveDebounceTimer is null)
        {
            _saveDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = SaveDebounceInterval
            };
            _saveDebounceTimer.Tick += (_, _) =>
            {
                _saveDebounceTimer.Stop();
                try
                {
                    _settingsService.Save();
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[Settings] Failed to save: {ex.Message}");
                }
            };
        }

        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    /// <summary>
    /// Builds the list of Whisper-supported languages.
    /// </summary>
    private static List<LanguageItem> BuildLanguageList() =>
    [
        new("en", "English"),
        new("zh", "Chinese"),
        new("de", "German"),
        new("es", "Spanish"),
        new("ru", "Russian"),
        new("ko", "Korean"),
        new("fr", "French"),
        new("ja", "Japanese"),
        new("pt", "Portuguese"),
        new("tr", "Turkish"),
        new("pl", "Polish"),
        new("ca", "Catalan"),
        new("nl", "Dutch"),
        new("ar", "Arabic"),
        new("sv", "Swedish"),
        new("it", "Italian"),
        new("id", "Indonesian"),
        new("hi", "Hindi"),
        new("fi", "Finnish"),
        new("vi", "Vietnamese"),
        new("he", "Hebrew"),
        new("uk", "Ukrainian"),
        new("el", "Greek"),
        new("ms", "Malay"),
        new("cs", "Czech"),
        new("ro", "Romanian"),
        new("da", "Danish"),
        new("hu", "Hungarian"),
        new("ta", "Tamil"),
        new("no", "Norwegian"),
        new("th", "Thai"),
        new("ur", "Urdu"),
        new("hr", "Croatian"),
        new("bg", "Bulgarian"),
        new("lt", "Lithuanian"),
        new("la", "Latin"),
        new("mi", "Maori"),
        new("ml", "Malayalam"),
        new("cy", "Welsh"),
        new("sk", "Slovak"),
        new("te", "Telugu"),
        new("fa", "Persian"),
        new("lv", "Latvian"),
        new("bn", "Bengali"),
        new("sr", "Serbian"),
        new("az", "Azerbaijani"),
        new("sl", "Slovenian"),
        new("kn", "Kannada"),
        new("et", "Estonian"),
        new("mk", "Macedonian"),
        new("br", "Breton"),
        new("eu", "Basque"),
        new("is", "Icelandic"),
        new("hy", "Armenian"),
        new("ne", "Nepali"),
        new("mn", "Mongolian"),
        new("bs", "Bosnian"),
        new("kk", "Kazakh"),
        new("sq", "Albanian"),
        new("sw", "Swahili"),
        new("gl", "Galician"),
        new("mr", "Marathi"),
        new("pa", "Punjabi"),
        new("si", "Sinhala"),
        new("km", "Khmer"),
        new("sn", "Shona"),
        new("yo", "Yoruba"),
        new("so", "Somali"),
        new("af", "Afrikaans"),
        new("oc", "Occitan"),
        new("ka", "Georgian"),
        new("be", "Belarusian"),
        new("tg", "Tajik"),
        new("sd", "Sindhi"),
        new("gu", "Gujarati"),
        new("am", "Amharic"),
        new("yi", "Yiddish"),
        new("lo", "Lao"),
        new("uz", "Uzbek"),
        new("fo", "Faroese"),
        new("ht", "Haitian Creole"),
        new("ps", "Pashto"),
        new("tk", "Turkmen"),
        new("nn", "Nynorsk"),
        new("mt", "Maltese"),
        new("sa", "Sanskrit"),
        new("lb", "Luxembourgish"),
        new("my", "Myanmar"),
        new("bo", "Tibetan"),
        new("tl", "Tagalog"),
        new("mg", "Malagasy"),
        new("as", "Assamese"),
        new("tt", "Tatar"),
        new("haw", "Hawaiian"),
        new("ln", "Lingala"),
        new("ha", "Hausa"),
        new("ba", "Bashkir"),
        new("jw", "Javanese"),
        new("su", "Sundanese"),
    ];
}

/// <summary>
/// Represents a language option for the language ComboBox,
/// with a code for persistence and a display name for the UI.
/// </summary>
public sealed class LanguageItem(string code, string displayName)
{
    /// <summary>ISO 639-1 language code (e.g. "en").</summary>
    public string Code { get; } = code;

    /// <summary>Human-readable language name (e.g. "English").</summary>
    public string DisplayName { get; } = displayName;

    public override string ToString() => DisplayName;
}

/// <summary>
/// Represents a GPU acceleration option for the ComboBox,
/// with an enum value for persistence and a display name for the UI.
/// </summary>
public sealed class GpuAccelerationItem(GpuAcceleration value, string displayName)
{
    /// <summary>The <see cref="GpuAcceleration"/> enum value.</summary>
    public GpuAcceleration Value { get; } = value;

    /// <summary>Human-readable name (e.g. "NVIDIA CUDA").</summary>
    public string DisplayName { get; } = displayName;

    public override string ToString() => DisplayName;
}
