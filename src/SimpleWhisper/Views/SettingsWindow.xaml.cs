using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using SimpleWhisper.Services;
using SimpleWhisper.ViewModels;
using SimpleWhisper.Views.Controls;

namespace SimpleWhisper.Views;

/// <summary>
/// Settings window with tabbed interface for General, Audio, Transcription,
/// and Text Processing settings. All changes are saved immediately.
/// </summary>
public partial class SettingsWindow : Window
{
    private AudioCaptureService? _testCapture;

    /// <summary>
    /// The settings view model driving this window.
    /// </summary>
    public SettingsViewModel ViewModel { get; }

    public SettingsWindow()
    {
        ViewModel = new SettingsViewModel(App.Settings);
        DataContext = ViewModel;

        InitializeComponent();

        // Pre-populate the PasswordBox (PasswordBox does not support binding)
        ApiKeyBox.Password = ViewModel.ApiKey;

        // Set up the replacement type combo column with enum values
        var typeColumn = (DataGridComboBoxColumn)ReplacementsGrid.Columns[2];
        typeColumn.ItemsSource = ViewModel.AvailableReplacementTypes;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Event Handlers
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the trigger recorder dialog to capture a new keyboard/mouse shortcut.
    /// </summary>
    private void OnChangeTriggerClick(object sender, RoutedEventArgs e)
    {
        var recorder = new TriggerRecorderControl();
        recorder.Owner = this;

        if (recorder.ShowDialog() == true && recorder.CapturedTrigger is not null)
        {
            ViewModel.ApplyTrigger(recorder.CapturedTrigger);
        }
    }

    /// <summary>
    /// PasswordBox doesn't support binding, so we sync manually on change.
    /// </summary>
    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
        {
            ViewModel.ApiKey = pb.Password;
        }
    }

    /// <summary>
    /// Opens the OpenAI API key page in the default browser.
    /// </summary>
    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    /// <summary>
    /// Tests the selected microphone by recording briefly and showing the audio level.
    /// </summary>
    private void OnTestMicrophoneClick(object sender, RoutedEventArgs e)
    {
        StopTestCapture();

        if (ViewModel.SelectedDeviceIndex < 0)
        {
            MessageBox.Show(
                "No microphone device is selected.",
                "Test Microphone",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            _testCapture = new AudioCaptureService();
            _testCapture.AudioLevelChanged += OnTestAudioLevel;
            _testCapture.StartRecording(ViewModel.SelectedDeviceIndex);

            // Auto-stop after 5 seconds
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                StopTestCapture();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to start microphone test:\n{ex.Message}",
                "Test Microphone",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnTestAudioLevel(float level)
    {
        Dispatcher.Invoke(() => AudioLevel.Level = level);
    }

    private void StopTestCapture()
    {
        if (_testCapture is not null)
        {
            _testCapture.AudioLevelChanged -= OnTestAudioLevel;
            _testCapture.StopRecording();
            _testCapture.Dispose();
            _testCapture = null;
        }
    }

    /// <summary>
    /// Downloads the selected Whisper model using LocalTranscriptionService.
    /// Shows progress in the progress bar and loads the model when complete.
    /// </summary>
    private async void OnDownloadModelClick(object sender, RoutedEventArgs e)
    {
        var modelSize = ViewModel.LocalModelSize;
        if (string.IsNullOrWhiteSpace(modelSize))
        {
            MessageBox.Show("Please select a model size first.", "Download Model",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var button = (Button)sender;
        button.IsEnabled = false;
        button.Content = "Downloading...";
        ModelDownloadProgress.Visibility = Visibility.Visible;
        ModelDownloadProgress.Value = 0;

        try
        {
            var localService = new LocalTranscriptionService();
            string modelPath = await localService.DownloadModelAsync(
                modelSize,
                progress =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        ModelDownloadProgress.Value = progress * 100;
                    });
                });

            ModelDownloadProgress.Value = 100;

            MessageBox.Show(
                $"Model \"{modelSize}\" downloaded successfully!\nPath: {modelPath}",
                "Download Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            localService.Dispose();
        }
        catch (OperationCanceledException)
        {
            // User cancelled; no action needed.
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to download model:\n{ex.Message}",
                "Download Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = "Download Model";
            ModelDownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Saves text replacements after a cell edit completes.
    /// </summary>
    private void OnReplacementCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        // Use Dispatcher to save after the edit is committed
        Dispatcher.BeginInvoke(new Action(() => ViewModel.SaveTextReplacements()),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Clean up when the window is closing.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        StopTestCapture();
        ViewModel.SaveTextReplacements();
        base.OnClosing(e);
    }
}
