using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Animation;
using SimpleWhisper.Helpers;
using SimpleWhisper.Models;
using SimpleWhisper.ViewModels;

namespace SimpleWhisper.Views;

/// <summary>
/// Floating overlay window that shows recording state, audio level, and transcription feedback.
/// The window is always-on-top, click-through, and positioned at the top-center of the primary screen.
/// </summary>
public partial class OverlayWindow : Window
{
    // ──────────────────────────────────────────────────────────────────
    //  Fields
    // ──────────────────────────────────────────────────────────────────

    private Storyboard? _pulseStoryboard;
    private Storyboard? _spinStoryboard;
    private Storyboard? _fadeInStoryboard;
    private Storyboard? _fadeOutStoryboard;

    private OverlayState _previousState = OverlayState.Idle;

    // ──────────────────────────────────────────────────────────────────
    //  Public API
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The view model driving this overlay. Exposed publicly so that
    /// <c>App.xaml.cs</c> and services can drive state transitions.
    /// </summary>
    public OverlayViewModel ViewModel { get; }

    // ──────────────────────────────────────────────────────────────────
    //  Constructor
    // ──────────────────────────────────────────────────────────────────

    public OverlayWindow()
    {
        ViewModel = new OverlayViewModel();
        DataContext = ViewModel;

        InitializeComponent();

        // Listen for property changes to manage animations and repositioning.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ──────────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Cache storyboard references.
        _pulseStoryboard = (Storyboard)FindResource("PulseAnimation");
        _spinStoryboard = (Storyboard)FindResource("SpinAnimation");
        _fadeInStoryboard = (Storyboard)FindResource("FadeInAnimation");
        _fadeOutStoryboard = (Storyboard)FindResource("FadeOutAnimation");

        // Make the window click-through so mouse events pass to applications below.
        Win32Helper.MakeWindowClickThrough(this);

        // Position at top-center of the primary screen.
        RepositionWindow();

        // React to display setting changes.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnClosed(e);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Positioning
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Centers the overlay horizontally at the top of the primary working area,
    /// taking DPI scaling into account via WPF logical units.
    /// </summary>
    public void RepositionWindow()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;

        // Measure desired size if not yet laid out.
        if (ActualWidth == 0)
        {
            Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Arrange(new Rect(DesiredSize));
        }

        var windowWidth = ActualWidth > 0 ? ActualWidth : 416; // 400 min + margin
        Left = (screenWidth - windowWidth) / 2.0;
        Top = 0;
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(RepositionWindow);
    }

    // ──────────────────────────────────────────────────────────────────
    //  ViewModel property-change handler (drives animations)
    // ──────────────────────────────────────────────────────────────────

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlayViewModel.IsVisible))
        {
            HandleVisibilityChange();
        }
        else if (e.PropertyName == nameof(OverlayViewModel.State))
        {
            HandleStateChange();
        }
    }

    private void HandleVisibilityChange()
    {
        if (ViewModel.IsVisible)
        {
            // Ensure the window is shown before animating.
            Visibility = Visibility.Visible;
            RepositionWindow();
            _fadeInStoryboard?.Begin(this, true);
        }
        else
        {
            // Fade out; actual Visibility.Collapsed is set in OnFadeOutCompleted.
            _fadeOutStoryboard?.Begin(this, true);
        }
    }

    private void HandleStateChange()
    {
        var newState = ViewModel.State;

        // Stop animations from the previous state.
        StopAnimationsForState(_previousState);

        // Start animations for the new state.
        StartAnimationsForState(newState);

        _previousState = newState;
        RepositionWindow();
    }

    private void StartAnimationsForState(OverlayState state)
    {
        switch (state)
        {
            case OverlayState.Recording:
                _pulseStoryboard?.Begin(this, true);
                break;

            case OverlayState.Processing:
                _spinStoryboard?.Begin(this, true);
                break;
        }
    }

    private void StopAnimationsForState(OverlayState state)
    {
        switch (state)
        {
            case OverlayState.Recording:
                _pulseStoryboard?.Stop(this);
                break;

            case OverlayState.Processing:
                _spinStoryboard?.Stop(this);
                break;
        }
    }

    /// <summary>
    /// Called when the fade-out animation completes so the window can be fully collapsed.
    /// </summary>
    private void OnFadeOutCompleted(object? sender, EventArgs e)
    {
        if (!ViewModel.IsVisible)
        {
            Visibility = Visibility.Collapsed;
        }
    }
}

// ══════════════════════════════════════════════════════════════════════
//  Value converter: AudioLevel (0..1) -> pixel Width for the level bar
// ══════════════════════════════════════════════════════════════════════

/// <summary>
/// Converts a normalized audio level (0.0 .. 1.0) to a pixel width
/// for the audio-level progress bar. The maximum width (100 px) matches
/// the container declared in OverlayWindow.xaml.
/// </summary>
public class AudioLevelToWidthConverter : IValueConverter
{
    /// <summary>Maximum width of the audio level bar in device-independent pixels.</summary>
    private const double MaxWidth = 100.0;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double level)
        {
            return Math.Clamp(level, 0.0, 1.0) * MaxWidth;
        }

        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
