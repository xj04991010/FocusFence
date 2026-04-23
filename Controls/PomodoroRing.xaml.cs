using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using FocusFence.Helpers;

namespace FocusFence.Controls;

/// <summary>
/// SVG Arc Progress ring for Pomodoro timer display.
/// Per PART 7C: IDLE → RUNNING → WARNING → COMPLETED states.
/// 
/// Arc is drawn with StreamGeometry for crisp rendering.
/// </summary>
public partial class PomodoroRing : System.Windows.Controls.UserControl
{
    public PomodoroRing()
    {
        InitializeComponent();
        SetIdle();
    }

    private double _ringSize = 100; // Fixed internal coordinate size
    private bool _isWarning;

    /// <summary>
    /// Updates the ring to show progress. ratio = elapsed/total (0→1).
    /// </summary>
    public void SetProgress(double ratio, int remainingSeconds)
    {
        _ringSize = 100;

        // Update arc
        double angle = ratio * 360;
        DrawArc(angle);

        // Update center text
        int m = remainingSeconds / 60;
        int s = remainingSeconds % 60;
        TimeText.Text = m > 0 ? $"{m}:{s:D2}" : $"{s}";

        // Warning state: last 2 minutes
        if (remainingSeconds <= 120 && remainingSeconds > 0 && !_isWarning)
        {
            _isWarning = true;
            StartWarningAnimation();
        }
        else if (remainingSeconds > 120 && _isWarning)
        {
            _isWarning = false;
            StopWarningAnimation();
        }
    }

    /// <summary>
    /// IDLE state: faint dot, tooltip "開始專注".
    /// </summary>
    public void SetIdle()
    {
        ProgressArc.Data = null;
        TimeText.Text = "";
        _isWarning = false;
        StopWarningAnimation();

        // Set track to a faint subtle color
        TrackEllipse.Stroke = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));

        ToolTip = "開始專注";
    }

    /// <summary>
    /// RUNNING state: show arc + time.
    /// </summary>
    public void SetRunning()
    {
        ToolTip = "暫停番茄鐘";

        if (TryFindResource("AccentTealBrush") is SolidColorBrush ab)
            ProgressArc.Stroke = ab;
    }

    /// <summary>
    /// COMPLETED state: bounce scale 1.0 → 1.3 → 1.0, then reset to IDLE.
    /// Per PART 7C.
    /// </summary>
    public void SetCompleted()
    {
        if (AnimationHelper.AnimationsDisabled)
        {
            SetIdle();
            return;
        }

        var bounceX = AnimationHelper.PomodoroRingBounce();
        var bounceY = AnimationHelper.PomodoroRingBounce();

        bounceX.Completed += (_, _) => Dispatcher.BeginInvoke(SetIdle);

        RingScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        RingScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
    }

    // ── Arc Drawing ─────────────────────────────────────────────

    private void DrawArc(double angleDegrees)
    {
        if (angleDegrees <= 0)
        {
            ProgressArc.Data = null;
            return;
        }

        double radius = (_ringSize / 2) - 3.5; // inset for stroke
        double cx = _ringSize / 2;
        double cy = _ringSize / 2;

        // SVG arc: start from top (12 o'clock)
        double startAngle = -90;
        double endAngle = startAngle + Math.Min(angleDegrees, 359.9);

        double startRad = startAngle * Math.PI / 180;
        double endRad = endAngle * Math.PI / 180;

        double x1 = cx + radius * Math.Cos(startRad);
        double y1 = cy + radius * Math.Sin(startRad);
        double x2 = cx + radius * Math.Cos(endRad);
        double y2 = cy + radius * Math.Sin(endRad);

        bool isLargeArc = angleDegrees > 180;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x1, y1), false, false);
            ctx.ArcTo(new Point(x2, y2),
                new System.Windows.Size(radius, radius),
                0,
                isLargeArc,
                SweepDirection.Clockwise,
                true, false);
        }
        geometry.Freeze();
        ProgressArc.Data = geometry;
    }

    // ── Warning Color Animation ─────────────────────────────────


    private void StartWarningAnimation()
    {
        // Per PART 4C: Teal → Orange → Red, 4000ms cycle, SineEase, infinite
        if (AnimationHelper.AnimationsDisabled) return;

        var colorAnim = new ColorAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(4000),
            RepeatBehavior = RepeatBehavior.Forever
        };

        Color teal = (Color)FindResource("AccentTeal");
        Color orange = (Color)FindResource("WarningOrange");
        Color red = (Color)FindResource("WarningRed");

        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(teal, KeyTime.FromPercent(0)));
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(orange, KeyTime.FromPercent(0.4)));
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(red, KeyTime.FromPercent(0.8)));
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(teal, KeyTime.FromPercent(1.0)));

        ProgressArc.Stroke = new SolidColorBrush(teal);
        ((SolidColorBrush)ProgressArc.Stroke).BeginAnimation(
            SolidColorBrush.ColorProperty, colorAnim);
            
        ArcGlow.BeginAnimation(DropShadowEffect.ColorProperty, colorAnim);
    }

    private void StopWarningAnimation()
    {
        if (ProgressArc.Stroke is SolidColorBrush brush && !brush.IsFrozen)
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            
        ArcGlow.BeginAnimation(DropShadowEffect.ColorProperty, null);

        if (TryFindResource("AccentTealBrush") is SolidColorBrush ab)
        {
            ProgressArc.Stroke = ab;
            ArcGlow.Color = (Color)FindResource("AccentTeal");
        }
    }
}
