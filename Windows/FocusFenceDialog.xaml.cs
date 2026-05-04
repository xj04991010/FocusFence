using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FocusFence.Windows;

/// <summary>
/// Custom dark-themed dialog to replace ugly native Windows MessageBox.
/// Supports: Message, Confirm (Yes/No), and Input modes.
/// </summary>
public partial class FocusFenceDialog : Window
{
    public enum DialogMode { Message, Confirm, Input, Destructive }

    public bool DialogConfirmed { get; private set; }
    public string InputResult { get; private set; } = "";

    public FocusFenceDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PlayAppearAnimation();
    }

    // ── Static helpers (drop-in replacements for MessageBox) ─────────

    /// <summary>
    /// Shows an info/error message with a single OK button.
    /// </summary>
    public static void ShowMessage(string message, string title = "FocusFence", bool isError = false)
    {
        var dlg = new FocusFenceDialog();
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.CancelBtn.Visibility = Visibility.Collapsed;
        dlg.ConfirmBtnText.Text = "確定";

        if (isError)
        {
            dlg.IconText.Text = "\uE7BA"; // Error icon
            dlg.IconText.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        }
        else
        {
            dlg.IconText.Text = "\uE946"; // Info icon
        }

        dlg.ShowDialog();
    }

    /// <summary>
    /// Shows a Yes/No confirmation dialog. Returns true if confirmed.
    /// </summary>
    public static bool ShowConfirm(string message, string title = "FocusFence", bool destructive = false)
    {
        var dlg = new FocusFenceDialog();
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.CancelBtn.Visibility = Visibility.Visible;
        dlg.CancelBtnText.Text = "取消";
        dlg.ConfirmBtnText.Text = "確定";

        if (destructive)
        {
            dlg.IconText.Text = "\uE7BA"; // Warning
            dlg.IconText.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            dlg.ConfirmBtn.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));
            dlg.ConfirmBtnText.Foreground = new SolidColorBrush(Colors.White);
            dlg.ConfirmBtnText.Text = "刪除";
        }
        else
        {
            dlg.IconText.Text = "\uE783"; // Question
        }

        dlg.ShowDialog();
        return dlg.DialogConfirmed;
    }

    /// <summary>
    /// Shows an input dialog. Returns null if cancelled.
    /// </summary>
    public static string? ShowInput(string message, string title = "FocusFence", string defaultValue = "", string placeholder = "")
    {
        var dlg = new FocusFenceDialog();
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.InputArea.Visibility = Visibility.Visible;
        dlg.InputBox.Text = defaultValue;
        dlg.CancelBtn.Visibility = Visibility.Visible;
        dlg.CancelBtnText.Text = "取消";
        dlg.ConfirmBtnText.Text = "確定";
        dlg.IconText.Text = "\uE70F"; // Edit icon

        dlg.Loaded += (_, _) =>
        {
            dlg.InputBox.Focus();
            dlg.InputBox.SelectAll();
        };

        dlg.ShowDialog();
        return dlg.DialogConfirmed ? dlg.InputResult : null;
    }

    // ── Event handlers ──────────────────────────────────────────────

    private void ConfirmBtn_Click(object sender, MouseButtonEventArgs e)
    {
        DialogConfirmed = true;
        InputResult = InputBox.Text;
        Close();
    }

    private void CancelBtn_Click(object sender, MouseButtonEventArgs e)
    {
        DialogConfirmed = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogConfirmed = false;
            Close();
        }
        else if (e.Key == Key.Enter && InputArea.Visibility != Visibility.Visible)
        {
            DialogConfirmed = true;
            Close();
        }
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogConfirmed = true;
            InputResult = InputBox.Text;
            Close();
            e.Handled = true;
        }
    }

    // ── Appear animation ────────────────────────────────────────────

    private void PlayAppearAnimation()
    {
        MainBorder.Opacity = 0;
        MainBorder.RenderTransform = new ScaleTransform(0.95, 0.95);
        MainBorder.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var scaleX = new DoubleAnimation(0.95, 1.0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var scaleY = new DoubleAnimation(0.95, 1.0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        MainBorder.BeginAnimation(OpacityProperty, fadeIn);
        ((ScaleTransform)MainBorder.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        ((ScaleTransform)MainBorder.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }
}
