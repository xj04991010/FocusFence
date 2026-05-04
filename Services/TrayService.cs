using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using FocusFence.Models;
using Forms = System.Windows.Forms;

namespace FocusFence.Services;

public class TrayService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly Forms.NotifyIcon _trayIcon;
    private readonly ContextMenu _wpfTrayMenu;

    public event Action? RequestDashboard;
    public event Action? RequestToggleZones;
    public event Action? RequestPomodoro;
    public event Action? RequestGlobalMemo;
    public event Action? RequestSave;
    public event Action? RequestExit;

    public TrayService()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateCoolIcon(),
            Text = "FocusFence",
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => RequestDashboard?.Invoke();

        _wpfTrayMenu = new ContextMenu();
        BuildMenu();

        _trayIcon.MouseUp += OnTrayMouseUp;
    }

    private void BuildMenu()
    {
        var dashItem = new MenuItem { Header = "控制台 (Dashboard)" };
        dashItem.Click += (_, _) => RequestDashboard?.Invoke();

        var toggleItem = new MenuItem { Header = "顯示/隱藏所有框框 (Ctrl+Alt+F)" };
        toggleItem.Click += (_, _) => RequestToggleZones?.Invoke();

        var pomoItem = new MenuItem { Header = "🍅 番茄鐘 (Pomodoro)" };
        pomoItem.Click += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => RequestPomodoro?.Invoke());

        var memoItem = new MenuItem { Header = "📝 總便利貼 (Global Memo)" };
        memoItem.Click += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => RequestGlobalMemo?.Invoke());

        var saveItem = new MenuItem { Header = "強制儲存 (Force Save)" };
        saveItem.Click += (_, _) => RequestSave?.Invoke();

        var startupItem = new MenuItem 
        { 
            Header = "開機自動啟動 (Startup)",
            IsCheckable = true,
            IsChecked = StartupService.IsEnabled()
        };
        startupItem.Click += (_, _) => StartupService.SetEnabled(startupItem.IsChecked);

        var exitItem = new MenuItem { Header = "結束 (Exit)", Foreground = System.Windows.Media.Brushes.IndianRed };
        exitItem.Click += (_, _) => RequestExit?.Invoke();

        _wpfTrayMenu.Items.Add(dashItem);
        _wpfTrayMenu.Items.Add(toggleItem);
        _wpfTrayMenu.Items.Add(new Separator());
        _wpfTrayMenu.Items.Add(pomoItem);
        _wpfTrayMenu.Items.Add(memoItem);
        _wpfTrayMenu.Items.Add(new Separator());
        _wpfTrayMenu.Items.Add(saveItem);
        _wpfTrayMenu.Items.Add(startupItem);
        _wpfTrayMenu.Items.Add(new Separator());
        _wpfTrayMenu.Items.Add(exitItem);
    }

    private void OnTrayMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Right)
        {
            var w = new Window 
            { 
                WindowStyle = WindowStyle.None, AllowsTransparency = true, 
                Background = System.Windows.Media.Brushes.Transparent, 
                Width = 0, Height = 0, ShowInTaskbar = false, Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = Forms.Cursor.Position.X,
                Top = Forms.Cursor.Position.Y
            };
            w.Show();
            w.Activate();
            
            _wpfTrayMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            _wpfTrayMenu.IsOpen = true;
            
            RoutedEventHandler? closedHandler = null;
            closedHandler = (s2, e2) => 
            {
                _wpfTrayMenu.Closed -= closedHandler;
                w.Close();
            };
            _wpfTrayMenu.Closed += closedHandler;
        }
    }

    private Icon CreateCoolIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var outerPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(200, 198, 156, 109), 4);
        g.DrawRectangle(outerPen, 4, 4, 24, 24);

        using var innerBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, 30, 30, 40));
        g.FillRectangle(innerBrush, 8, 8, 16, 16);

        using var slashPen = new System.Drawing.Pen(System.Drawing.Color.White, 3);
        g.DrawLine(slashPen, 8, 24, 24, 8);

        IntPtr hIcon = bmp.GetHicon();
        Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    public void Dispose()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }
}
