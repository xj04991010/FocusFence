using System.Windows;
using System.Windows.Media.Animation;

namespace FocusFence.Helpers;

/// <summary>
/// Central animation library implementing PART 4 of the Design Brief.
/// 
/// Three standard curves:
///   A — Spring  (appear, expand, activate)
///   B — EaseOut (disappear, collapse, leave)
///   C — EaseInOut (state transitions, color changes)
/// 
/// RULES:
///   - No linear animations (PART 4A Rule 1)
///   - Duration: 150ms–400ms (PART 4A Rule 3)
///   - Displacement max 8px (PART 4A Rule 4)
///   - Opacity animations must pair with Scale or TranslateY (PART 4A Rule 5)
///   - Respect SystemParameters.ClientAreaAnimation (PART 9 Rule 2)
/// </summary>
public static class AnimationHelper
{
    // ── Curve Definitions ─────────────────────────────────────────

    /// <summary>Curve A — Spring: overshoot + settle (350ms)</summary>
    public static IEasingFunction SpringEase => new ElasticEase
    {
        Oscillations = 1,
        Springiness = 8,
        EasingMode = EasingMode.EaseOut
    };

    /// <summary>Curve B — EaseOut: fast start, slow finish (220ms)</summary>
    public static CubicEase EaseOut => new() { EasingMode = EasingMode.EaseOut };

    /// <summary>Curve C — EaseInOut: state transitions (280ms)</summary>
    public static CubicEase EaseInOut => new() { EasingMode = EasingMode.EaseInOut };

    // ── Standard Durations ────────────────────────────────────────

    public static readonly Duration DurationSpring   = TimeSpan.FromMilliseconds(350);
    public static readonly Duration DurationEaseOut  = TimeSpan.FromMilliseconds(220);
    public static readonly Duration DurationTransition = TimeSpan.FromMilliseconds(280);
    public static readonly Duration DurationHover    = TimeSpan.FromMilliseconds(180);

    // ── Animation Accessibility Check ─────────────────────────────

    /// <summary>
    /// Returns true if the user has disabled window animations.
    /// When true, skip all animations and apply states immediately.
    /// </summary>
    public static bool AnimationsDisabled =>
        !SystemParameters.ClientAreaAnimation;

    // ── Factory Methods ───────────────────────────────────────────

    /// <summary>
    /// Creates a Curve A (Spring) opacity animation: 0 → 1.
    /// Per PART 4C: Zone appear must pair with Scale and TranslateY.
    /// </summary>
    public static DoubleAnimation FadeInSpring()
    {
        if (AnimationsDisabled)
            return new DoubleAnimation { To = 1, Duration = TimeSpan.Zero };

        return new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = EaseOut
        };
    }

    /// <summary>
    /// Creates a Curve B (EaseOut) opacity animation: 1 → 0.
    /// Per PART 4C: Zone disappear with Scale 1.0 → 0.97.
    /// </summary>
    public static DoubleAnimation FadeOutEaseOut()
    {
        if (AnimationsDisabled)
            return new DoubleAnimation { To = 0, Duration = TimeSpan.Zero };

        return new DoubleAnimation
        {
            To = 0,
            Duration = DurationEaseOut,
            EasingFunction = EaseOut
        };
    }

    /// <summary>
    /// Creates a Curve A (Spring) scale animation: 0.96 → 1.0.
    /// Per PART 4C: Zone appear scale.
    /// </summary>
    public static DoubleAnimation ScaleInSpring()
    {
        if (AnimationsDisabled)
            return new DoubleAnimation { To = 1, Duration = TimeSpan.Zero };

        return new DoubleAnimation
        {
            From = 0.96, To = 1.0,
            Duration = DurationSpring,
            EasingFunction = SpringEase
        };
    }

    /// <summary>
    /// Creates a Curve B scale-out animation: 1.0 → 0.97.
    /// Per PART 4C: Zone disappear scale.
    /// </summary>
    public static DoubleAnimation ScaleOutEaseOut()
    {
        if (AnimationsDisabled)
            return new DoubleAnimation { To = 0.97, Duration = TimeSpan.Zero };

        return new DoubleAnimation
        {
            To = 0.97,
            Duration = DurationEaseOut,
            EasingFunction = EaseOut
        };
    }

    /// <summary>
    /// Creates a Curve A TranslateY animation: +6 → 0 (appear, float up).
    /// Per PART 4C: Zone appear translate.
    /// </summary>
    public static DoubleAnimation TranslateYInSpring()
    {
        if (AnimationsDisabled)
            return new DoubleAnimation { To = 0, Duration = TimeSpan.Zero };

        return new DoubleAnimation
        {
            From = 6, To = 0,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = SpringEase
        };
    }

    /// <summary>
    /// Creates a Curve B hover TranslateY: 0 → -2px (float up on hover).
    /// Per PART 4C: Zone Hover.
    /// </summary>
    public static DoubleAnimation HoverLift(bool entering)
    {
        if (AnimationsDisabled)
            return new DoubleAnimation { To = entering ? -2 : 0, Duration = TimeSpan.Zero };

        return new DoubleAnimation
        {
            To = entering ? -2 : 0,
            Duration = DurationHover,
            EasingFunction = EaseOut
        };
    }

    /// <summary>
    /// Creates a Curve C (EaseInOut) opacity animation for state transitions.
    /// Per PART 4C: Context launch dim (1.0 → 0.4) or restore (→ 1.0).
    /// </summary>
    public static DoubleAnimation OpacityTransition(double to)
    {
        if (AnimationsDisabled)
            return new DoubleAnimation { To = to, Duration = TimeSpan.Zero };

        return new DoubleAnimation
        {
            To = to,
            Duration = DurationTransition,
            EasingFunction = EaseInOut
        };
    }

    /// <summary>
    /// Creates a Curve C color animation for border brush transitions.
    /// Per PART 4C: Context launch border color change.
    /// </summary>
    public static ColorAnimation ColorTransition(System.Windows.Media.Color to)
    {
        if (AnimationsDisabled)
            return new ColorAnimation { To = to, Duration = TimeSpan.Zero };

        return new ColorAnimation
        {
            To = to,
            Duration = DurationTransition,
            EasingFunction = EaseInOut
        };
    }

    /// <summary>
    /// Creates the Pomodoro completion pulse: 0 → 0.12 → 0 (500ms).
    /// Per PART 4C: full-screen white overlay.
    /// </summary>
    public static DoubleAnimationUsingKeyFrames PomodoroCompletionPulse()
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(0.12,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)),
            new KeySpline(0.25, 0.1, 0.25, 1.0)));
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)),
            new KeySpline(0.25, 0.1, 0.25, 1.0)));

        if (AnimationsDisabled)
        {
            anim.KeyFrames.Clear();
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        }

        return anim;
    }

    /// <summary>
    /// Creates a shadow opacity animation for hover state.
    /// Per PART 3B: Layer1 Opacity 0.15 → 0.22 on hover.
    /// </summary>
    public static DoubleAnimation ShadowHover(bool entering)
    {
        if (AnimationsDisabled)
            return new DoubleAnimation { To = entering ? 0.22 : 0.15, Duration = TimeSpan.Zero };

        return new DoubleAnimation
        {
            To = entering ? 0.22 : 0.15,
            Duration = DurationHover,
            EasingFunction = EaseOut
        };
    }

    /// <summary>
    /// Creates the Pomodoro ring completion bounce: Scale 1.0 → 1.3 → 1.0 (400ms Spring).
    /// Per PART 7C.
    /// </summary>
    public static DoubleAnimationUsingKeyFrames PomodoroRingBounce()
    {
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(400) };
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(1.3,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)),
            new KeySpline(0.34, 1.56, 0.64, 1.0)));
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)),
            new KeySpline(0.25, 0.1, 0.25, 1.0)));

        if (AnimationsDisabled)
        {
            anim.KeyFrames.Clear();
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        }

        return anim;
    }
}
