using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MergeMansionWikiTools.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Fade in 1s → call onFadedIn(startFadeOut) → when startFadeOut is called → fade out 0.2s → onComplete.
    /// </summary>
    public void RunFadeSequence(Action<Action> onFadedIn, Action onComplete)
    {
        var duration = TimeSpan.FromSeconds(2.5);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };
        var scaleX = new DoubleAnimation(0.85, 1, duration) { EasingFunction = easing };
        var scaleY = new DoubleAnimation(0.85, 1, duration) { EasingFunction = easing };

        fadeIn.Completed += (_, _) =>
        {
            onFadedIn(() =>
            {
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5));
                fadeOut.Completed += (_, _) => onComplete();
                BeginAnimation(OpacityProperty, fadeOut);
            });
        };

        BeginAnimation(OpacityProperty, fadeIn);
        LogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        LogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }
}
