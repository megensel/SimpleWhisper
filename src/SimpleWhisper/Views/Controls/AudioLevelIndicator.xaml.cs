using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace SimpleWhisper.Views.Controls;

/// <summary>
/// A horizontal audio level indicator that displays input level as a colored bar.
/// Green for low levels, yellow for medium, red for high. Animates smoothly between values.
/// </summary>
public partial class AudioLevelIndicator : UserControl
{
    /// <summary>
    /// Identifies the <see cref="Level"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(double),
        typeof(AudioLevelIndicator),
        new PropertyMetadata(0.0, OnLevelChanged));

    /// <summary>
    /// Gets or sets the audio level, normalized between 0.0 (silence) and 1.0 (maximum).
    /// </summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public AudioLevelIndicator()
    {
        InitializeComponent();

        // Make the level bar span the full width so ScaleTransform works correctly
        LevelBar.Width = double.NaN;
        SizeChanged += (_, _) => UpdateBarWidth();
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AudioLevelIndicator indicator)
        {
            indicator.AnimateLevel((double)e.NewValue);
        }
    }

    private void AnimateLevel(double newLevel)
    {
        // Clamp to 0.0 - 1.0
        newLevel = Math.Clamp(newLevel, 0.0, 1.0);

        var animation = new DoubleAnimation
        {
            To = newLevel,
            Duration = TimeSpan.FromMilliseconds(80),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        LevelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, animation);
    }

    private void UpdateBarWidth()
    {
        // Ensure the bar spans the full control width so scaling works
        LevelBar.Width = ActualWidth;
    }
}
