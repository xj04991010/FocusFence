using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusFence.Windows;

public partial class PomodoroTimerWindow : Window
{
    private bool _isPinned = true;
    private bool _isPaused = false;
    private System.Media.SoundPlayer? _audioPlayer;
    private System.IO.MemoryStream? _audioStream;
    private double _currentVolume = 0.5;
    private bool _isBrownActive = false;
    private bool _isNoisePlaying = false;

    public event Action? RequestStop;
    public event Action? RequestPause;
    public event Action? RequestResume;
    public PomodoroTimerWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Place at top-center of screen by default
        Left = (SystemParameters.WorkArea.Width - Width) / 2;
        Top = 20;
    }

    public void SetVolume(double vol)
    {
        _currentVolume = Math.Clamp(vol, 0, 1);
        // If noise is playing, we need to re-generate to apply volume
        if (_isNoisePlaying)
        {
            GenerateAndPlayNoise(_isBrownActive);
        }
    }

    public void UpdateDisplay(string label, int remainingSeconds, int totalSeconds)
    {
        TaskLabelText.Text = label;
        int m = remainingSeconds / 60;
        int s = remainingSeconds % 60;
        TimerText.Text = $"{m:D2}:{s:D2}";
        
        double progressRatio = totalSeconds > 0 ? (double)remainingSeconds / totalSeconds : 0;
        double progressPercent = 100 - (progressRatio * 100);
        ProgressArc.Value = progressPercent;

        // --- Premium Dynamic Color ---
        var color = GetInterpolatedColor(progressRatio);
        var brush = new SolidColorBrush(color);
        TimerText.Foreground = brush;
        ProgressArc.Foreground = brush;

        // --- Pulse Animation for last 60s ---
        if (remainingSeconds <= 60 && remainingSeconds > 0)
        {
            if (TimerText.RenderTransform is not ScaleTransform)
                TimerText.RenderTransform = new ScaleTransform(1, 1);
            
            // Subtle pulse
            double scale = 1.0 + (Math.Sin(DateTime.Now.Millisecond * 0.01) * 0.02);
            ((ScaleTransform)TimerText.RenderTransform).ScaleX = scale;
            ((ScaleTransform)TimerText.RenderTransform).ScaleY = scale;
        }
    }

    private Color GetInterpolatedColor(double ratio)
    {
        // Define key colors for the gradient
        // 1.0 (Start) = Teal (#5EEAD4)
        // 0.4 (Mid)   = Yellow (#FDE047)
        // 0.0 (End)   = OrangeRed (#F87171)

        if (ratio > 0.4)
        {
            // Teal to Yellow
            double t = (ratio - 0.4) / 0.6; // 0 to 1
            return Color.FromRgb(
                (byte)Lerp(253, 94, t),
                (byte)Lerp(224, 234, t),
                (byte)Lerp(71, 212, t));
        }
        else
        {
            // Yellow to OrangeRed
            double t = ratio / 0.4; // 0 to 1
            return Color.FromRgb(
                (byte)Lerp(248, 253, t),
                (byte)Lerp(113, 224, t),
                (byte)Lerp(113, 71, t));
        }
    }

    private double Lerp(double start, double end, double t)
    {
        return start + (end - start) * t;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void PinBtn_Click(object sender, MouseButtonEventArgs e)
    {
        _isPinned = !_isPinned;
        Topmost = _isPinned;
        PinIcon.Foreground = _isPinned 
            ? new SolidColorBrush(Colors.White) 
            : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        e.Handled = true;
    }

    private void StopBtn_Click(object sender, MouseButtonEventArgs e)
    {
        RequestStop?.Invoke();
        e.Handled = true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                PauseBtn_Click(this, null!);
                e.Handled = true;
                break;
            case Key.Escape:
                RequestStop?.Invoke();
                e.Handled = true;
                break;
            case Key.W:
                WhiteNoiseBtn_Click(this, null!);
                e.Handled = true;
                break;
            case Key.B:
                BrownNoiseBtn_Click(this, null!);
                e.Handled = true;
                break;
            case Key.P:
                _isPinned = !_isPinned;
                Topmost = _isPinned;
                PinIcon.Foreground = _isPinned
                    ? new SolidColorBrush(Colors.White)
                    : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
                e.Handled = true;
                break;
            case Key.M:
                StopNoise();
                e.Handled = true;
                break;
        }
    }

    private void VolumeBtn_Click(object sender, MouseButtonEventArgs e)
    {
        VolumePopup.IsOpen = !VolumePopup.IsOpen;
        e.Handled = true;
    }

    private void LocalVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SetVolume(e.NewValue / 100.0);
    }

    private void PauseBtn_Click(object sender, MouseButtonEventArgs e)
    {
        _isPaused = !_isPaused;
        if (_isPaused) 
        {
            PauseIcon.Text = "▶";
            PauseIcon.Foreground = new SolidColorBrush(Colors.LightGreen);
            RequestPause?.Invoke();
        }
        else 
        {
            PauseIcon.Text = "⏸";
            PauseIcon.Foreground = new SolidColorBrush(Colors.White);
            RequestResume?.Invoke();
        }
        e.Handled = true;
    }

    private void GenerateAndPlayNoise(bool isBrown)
    {
        StopNoise();
        
        int sampleRate = 44100;
        int seconds = 8; // 8 seconds allows for a sweeping wave cycle
        short[] samples = new short[sampleRate * seconds];
        var r = new Random();
        _isNoisePlaying = true;
        _isBrownActive = isBrown;
        double lastOut = 0;

        for(int i=0; i<samples.Length; i++) {
            double white = (r.NextDouble() * 2.0 - 1.0);
            
            if (isBrown) {
                // Deep brown noise/waterfall
                lastOut = (lastOut * 0.98) + (white * 0.05);
                
                double lfo = Math.Sin(i * 2.0 * Math.PI / (sampleRate * seconds)); 
                lfo = (lfo + 1.0) * 0.5;
                lfo = 0.5 + (lfo * 0.5);
                
                double val = lastOut * lfo * 3.5 * _currentVolume; 
                samples[i] = (short)(Math.Clamp(val, -1.0, 1.0) * 8000); 
            }
            else {
                // Pink-ish noise (gentle rain/static)
                lastOut = (lastOut * 0.88) + (white * 0.12);
                
                double val = lastOut * 1.2 * _currentVolume;
                samples[i] = (short)(Math.Clamp(val, -1.0, 1.0) * 4000); 
            }
        }
        
        _audioStream = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(_audioStream, System.Text.Encoding.ASCII, true);
        bw.Write("RIFF".ToCharArray());
        bw.Write(36 + samples.Length * 2);
        bw.Write("WAVE".ToCharArray());
        bw.Write("fmt ".ToCharArray());
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data".ToCharArray());
        bw.Write(samples.Length * 2);
        foreach(short s in samples) bw.Write(s);
        
        _audioStream.Position = 0;
        _audioPlayer = new System.Media.SoundPlayer(_audioStream);
        _audioPlayer.PlayLooping();
        
        WhiteNoiseIcon.Opacity = isBrown ? 0.4 : 1.0;
        BrownNoiseIcon.Opacity = isBrown ? 1.0 : 0.4;
    }

    private void StopNoise()
    {
        _isNoisePlaying = false;
        _audioPlayer?.Stop();
        _audioPlayer?.Dispose();
        _audioStream?.Dispose();
        _audioPlayer = null;
        _audioStream = null;
        
        WhiteNoiseIcon.Opacity = 0.4;
        BrownNoiseIcon.Opacity = 0.4;
    }

    private void WhiteNoiseBtn_Click(object sender, MouseButtonEventArgs e)
    {
        if (WhiteNoiseIcon.Opacity == 1.0) StopNoise();
        else GenerateAndPlayNoise(false);
        e.Handled = true;
    }

    private void BrownNoiseBtn_Click(object sender, MouseButtonEventArgs e)
    {
        if (BrownNoiseIcon.Opacity == 1.0) StopNoise();
        else GenerateAndPlayNoise(true);
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopNoise();
        base.OnClosed(e);
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_isPinned)
        {
            // If not pinned, wait, they said "可以丁選的時鐘" 
            // It should probably stay above if Pinned as Topmost=true.
            // If not pinned, Topmost=false handles it naturally falling behind.
        }
    }

    private void Window_LocationChanged(object sender, EventArgs e)
    {
        // Optional boundary snapping
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            double snapRadius = 20.0;
            double screenW = SystemParameters.WorkArea.Width;
            double screenH = SystemParameters.WorkArea.Height;

            if (Math.Abs(Left) < snapRadius) Left = 0;
            if (Math.Abs(Left + Width - screenW) < snapRadius) Left = screenW - Width;
            if (Math.Abs(Top) < snapRadius) Top = 0;
            if (Math.Abs(Top + Height - screenH) < snapRadius) Top = screenH - Height;
        }
    }
}
