using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FocusFence.Models;
using FocusFence.Services;
using FocusFence.Windows;
using Forms = System.Windows.Forms;

namespace FocusFence;

public partial class App : Application
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // Desktop double-click hook
    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr hInstance, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(int x, int y);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    private Forms.NotifyIcon _trayIcon = null!;
    private readonly List<ZoneWindow> _zones = [];
    private AppConfig _config = null!;
    private DashboardWindow _dashboard = null!;
    private DispatcherTimer _autoSaveTimer = null!;
    private HotkeyService? _hotkeyService;
    private Window? _hotkeyWindow;
    private bool _zonesVisible = true;
    private PomodoroService? _pomodoroService;
    private DormancyService? _dormancyService;
    private ContextLaunchService? _contextLaunchService;
    private DownloadCatcherService? _downloadCatcherService;
    private IntPtr _mouseHookId = IntPtr.Zero;
    private LowLevelMouseProc? _mouseHookProc; // prevent GC collection
    private DateTime _lastDoubleClickTime = DateTime.MinValue; // debounce

    private EventWaitHandle? _showDashboardEvent;
    private CancellationTokenSource? _appCts;

    private PomodoroTimerWindow? _pomoTimerWin;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ── Single Instance Check using EventWaitHandle ──
        if (EventWaitHandle.TryOpenExisting("FocusFence_ShowDashboard_Event", out EventWaitHandle? existingEvent))
        {
            existingEvent.Set(); // Signal first instance to show Dashboard
            Shutdown();
            return;
        }

        _showDashboardEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "FocusFence_ShowDashboard_Event");
        _appCts = new CancellationTokenSource();
        var ct = _appCts.Token;
        _ = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Wait with cancellation support
                    WaitHandle.WaitAny([_showDashboardEvent, ct.WaitHandle]);
                    if (ct.IsCancellationRequested) break;
                    Dispatcher.BeginInvoke(() =>
                    {
                        _dashboard.Show();
                        if (_dashboard.WindowState == WindowState.Minimized) _dashboard.WindowState = WindowState.Normal;
                        _dashboard.Activate();
                        _dashboard.Topmost = true;
                        _dashboard.Topmost = false;
                    });
                }
                catch (OperationCanceledException) { break; }
            }
        }, ct);

        base.OnStartup(e);

        // Global crash handler
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            LogCrash("AppDomain.UnhandledException", ex);
        };
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true; // prevent crash, keep running
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        _config = ConfigService.Load();

        // Init Auto-save timer (debounce 2 seconds)
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            ConfigService.Save(_config);
        };

        // Create services
        _pomodoroService = new PomodoroService(_config);
        _pomodoroService.OnTick += OnPomodoroTick;
        _pomodoroService.OnSessionComplete += OnPomodoroComplete;
        _pomodoroService.OnFocusChanged += OnPomodoroFocusChanged;

        _contextLaunchService = new ContextLaunchService(_config, _pomodoroService);
        _contextLaunchService.OnContextChanged += OnContextChanged;

        _dormancyService = new DormancyService(_config);
        _dormancyService.OnDormancyChanged += OnDormancyChanged;

        _downloadCatcherService = new DownloadCatcherService(_config);
        _downloadCatcherService.Start();

        // Create Dashboard
        _dashboard = new DashboardWindow(_config);
        _dashboard.ConfigurationChanged += OnConfigurationChanged;
        _dashboard.RequestCreateZone += OnCreateZoneRequest;
        _dashboard.RequestDeleteZone += OnDeleteZoneRequest;
        _dashboard.RequestToggleZone += OnToggleZoneRequest;
        _dashboard.RequestStartPomodoro += (zoneId, label, duration, volume) =>
        {
            _pomodoroService!.Start(zoneId, duration, label);
            
            if (_pomoTimerWin == null)
            {
                _pomoTimerWin = new PomodoroTimerWindow();
                _pomoTimerWin.RequestStop += () => { _pomodoroService.Stop(); ClosePomodoroTimer(); };
                _pomoTimerWin.RequestPause += () => _pomodoroService.Pause();
                _pomoTimerWin.RequestResume += () => _pomodoroService.Resume();
                _pomoTimerWin.Show();
            }
            
            _dashboard.ResetPauseState();
            _pomoTimerWin.SetVolume(volume);
            _dashboard.RefreshData();
        };

        _dashboard.RequestVolumeChange += (volume) =>
        {
            _pomoTimerWin?.SetVolume(volume);
        };
        _dashboard.RequestPause += () => _pomodoroService?.Pause();
        _dashboard.RequestResume += () => _pomodoroService?.Resume();
        _dashboard.RequestStop += () =>
        {
            _pomodoroService?.Stop();
            ClosePomodoroTimer();
            _dashboard.RefreshData();
        };
        _dashboard.RequestSummonZone += (config) =>
        {
            var zone = _zones.FirstOrDefault(z => z.Config == config);
            if (zone != null)
            {
                zone.ShowZone();
                zone.Topmost = true;
                zone.Activate();
                // Reset topmost after a short delay (unless pinned)
                if (!config.IsPinned)
                {
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    timer.Tick += (_, _) => { timer.Stop(); zone.Topmost = false; };
                    timer.Start();
                }
            }
        };
        _dashboard.RequestAutoArrange += AutoArrangeZones;

        // Create initial zones
        foreach (var zc in _config.Zones.ToList())
        {
            CreateZoneWindow(zc);
        }

        InitTrayIcon();
        InitGlobalHotkeys();
        InitDesktopDoubleClickHook();

        // Start dormancy scanning
        _dormancyService.Start();

        // Show dashboard if not auto-started by Windows
        if (!e.Args.Contains("--startup"))
        {
            _dashboard.Show();
            _dashboard.Activate();
        }
    }

    // ── Pomodoro Event Handlers ─────────────────────────────────────

    private void OnPomodoroTick(string zoneId, int remaining, int total)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var zone = _zones.FirstOrDefault(z => z.Config.Id == zoneId);
            zone?.UpdatePomodoroDisplay(remaining, total);

            if (_pomoTimerWin != null && _pomodoroService != null)
            {
                _pomoTimerWin.UpdateDisplay(_pomodoroService.ActiveLabel ?? "專注中...", remaining, total);
            }
        });
    }

    private void OnPomodoroComplete(string zoneId, int completedCount)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var zone = _zones.FirstOrDefault(z => z.Config.Id == zoneId);
            zone?.OnPomodoroComplete();

            ClosePomodoroTimer();

            // Full-desktop pulse flash effect
            PlayCompletionPulse();
        });
    }

    private void ClosePomodoroTimer()
    {
        if (_pomoTimerWin != null)
        {
            _pomoTimerWin.Close();
            _pomoTimerWin = null;
        }
    }

    private void OnPomodoroFocusChanged(string zoneId, bool isActive)
    {
        if (string.IsNullOrEmpty(zoneId)) return;
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var zone in _zones)
            {
                if (zone.Config.Id == zoneId)
                {
                    zone.SetFocusDim(false); // Active zone always full opacity
                }
                else
                {
                    zone.SetFocusDim(isActive); // Other zones dim when a pomodoro is active
                }
            }
        });
    }

    /// <summary>
    /// Non-intrusive full-desktop pulse: brief white flash to signal session end.
    /// </summary>
    private void PlayCompletionPulse()
    {
        var overlay = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.White,
            Topmost = true,
            ShowInTaskbar = false,
            Opacity = 0,
            Left = 0, Top = 0,
            Width = SystemParameters.PrimaryScreenWidth,
            Height = SystemParameters.PrimaryScreenHeight
        };
        overlay.Show();

        var anim = new DoubleAnimation
        {
            From = 0, To = 0.15,
            Duration = TimeSpan.FromMilliseconds(250),
            AutoReverse = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) => overlay.Close();
        overlay.BeginAnimation(Window.OpacityProperty, anim);
    }

    // ── Context Launch Event Handler ────────────────────────────────

    private void OnContextChanged(string? zoneId)
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var zone in _zones)
            {
                bool isActive = zone.Config.Id == zoneId;
                zone.SetContextActive(isActive);

                if (zoneId != null && !isActive)
                    zone.SetFocusDim(true);
                else
                    zone.SetFocusDim(false);
            }
            ScheduleSave();
        });
    }

    // ── Dormancy Event Handler ──────────────────────────────────────

    private void OnDormancyChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var zone in _zones)
            {
                zone.UpdateDormancyBadge();

                // Auto-collapse dormant zones
                if (zone.Config.IsDormant && !zone.Config.IsCollapsed)
                {
                    zone.Config.IsCollapsed = true;
                    // Let the zone handle its own collapse visual
                }
            }
            ScheduleSave();
        });
    }



    // ── Desktop Double-Click Hide ────────────────────────────────

    private void InitDesktopDoubleClickHook()
    {
        _mouseHookProc = MouseHookCallback;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc,
            GetModuleHandle(module.ModuleName), 0);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Only intercept WM_LBUTTONDBLCLK; let all other events pass through immediately
        if (nCode >= 0 && wParam == (IntPtr)WM_LBUTTONDBLCLK)
        {
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int px = hookStruct.pt.x, py = hookStruct.pt.y;

            // Offload the Win32 desktop-check to ThreadPool so we never
            // block the system-wide mouse pipeline inside this callback.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // Debounce: ignore if < 300 ms since last accepted click
                if ((DateTime.UtcNow - _lastDoubleClickTime).TotalMilliseconds < 300) return;

                IntPtr hwndUnder = WindowFromPoint(px, py);
                if (IsDesktopWindow(hwndUnder))
                {
                    _lastDoubleClickTime = DateTime.UtcNow;
                    Application.Current?.Dispatcher.BeginInvoke(ToggleAllZones);
                }
            });
        }
        // Always return immediately — never let the hook stall the mouse
        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private static bool IsDesktopWindow(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();
        // Desktop icons live in SysListView32 under SHELLDLL_DefView,
        // or directly on Progman / WorkerW
        return cls is "SysListView32" or "SHELLDLL_DefView" or "Progman" or "WorkerW";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    // ── Global Hotkeys ──────────────────────────────────────────────

    private void InitGlobalHotkeys()
    {
        // Create a hidden window to receive WM_HOTKEY messages
        _hotkeyWindow = new Window
        {
            Width = 0, Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Visibility = Visibility.Hidden
        };
        _hotkeyWindow.Show();
        _hotkeyWindow.Hide();

        _hotkeyService = new HotkeyService();
        _hotkeyService.Attach(_hotkeyWindow);
        _hotkeyService.RegisterDefaults(
            onToggleZones: ToggleAllZones,
            onShowDashboard: () => { _dashboard.Show(); _dashboard.Activate(); },
            onPrevDesktop: () => SwitchDesktopPage(-1),
            onNextDesktop: () => SwitchDesktopPage(1)
        );
    }

    private void SwitchDesktopPage(int delta)
    {
        _config.ActiveDesktopPage += delta;
        if (_config.ActiveDesktopPage < 0) _config.ActiveDesktopPage = 0;
        
        UpdateZoneVisibilityForPage();
        ScheduleSave();
    }

    private void UpdateZoneVisibilityForPage()
    {
        foreach (var zone in _zones)
        {
            if (!_zonesVisible)
            {
                zone.Visibility = Visibility.Hidden;
                continue;
            }

            // Always show pinned zones, otherwise check page match
            bool isOnPage = zone.Config.IsPinned || zone.Config.DesktopPage == _config.ActiveDesktopPage;
            
            if (isOnPage && zone.Config.IsVisible)
                zone.ShowZone();
            else
                zone.Visibility = Visibility.Hidden;
        }
    }

    /// <summary>
    /// v3.0 Zen Mode upgrade: If there's an active context,
    /// only hide non-active zones. Otherwise hide all.
    /// </summary>
    private void ToggleAllZones()
    {
        _zonesVisible = !_zonesVisible;

        if (!_zonesVisible && HasActiveContext())
        {
            // Selective hide: only hide non-active context zones
            foreach (var zone in _zones)
            {
                if (zone.Config.IsActiveContext)
                {
                    // Keep active context visible
                    bool isOnPage = zone.Config.IsPinned || zone.Config.DesktopPage == _config.ActiveDesktopPage;
                    if (isOnPage && zone.Config.IsVisible)
                        zone.AnimateIn();
                }
                else
                {
                    zone.AnimateOut();
                }
            }
        }
        else
        {
            UpdateZoneVisibilityForPage();
        }
    }

    private bool HasActiveContext()
    {
        return _config.Zones.Any(z => z.IsActiveContext);
    }

    private void CreateZoneWindow(ZoneConfig zc)
    {
        if (zc.Width < 100) zc.Width = 320;
        if (zc.Height < 50) zc.Height = 280;

        var zone = new ZoneWindow(zc);
        _zones.Add(zone);
        
        zone.ZoneChanged += () =>
        {
            ScheduleSave();
            _dashboard.RefreshData();
        };
        zone.ZoneCloseRequested += (z) => 
        {
            z.HideZone();
            _dashboard.RefreshData();
            ScheduleSave();
        };

        // Wire up Pomodoro request
        zone.PomodoroStartRequested += (zoneId) => StartGlobalPomodoro(zoneId);

        // Wire up Context Launch request
        zone.ContextLaunchRequested += (zoneId) =>
        {
            _contextLaunchService?.LaunchContext(zoneId);
        };

        bool isOnPage = zc.IsPinned || zc.DesktopPage == _config.ActiveDesktopPage;
        if (zc.IsVisible && isOnPage)
        {
            zone.Show();
        }
    }

    private void OnCreateZoneRequest(ZoneConfig newConfig)
    {
        _config.Zones.Add(newConfig);
        
        // Ensure folder exists
        if (!string.IsNullOrEmpty(newConfig.FolderPath))
            Directory.CreateDirectory(newConfig.FolderPath);

        CreateZoneWindow(newConfig);
        
        // Bring the newly created zone to the front
        var newZone = _zones.LastOrDefault(z => z.Config == newConfig);
        if (newZone != null)
        {
            newZone.Activate();
            newZone.Topmost = true;
            newZone.Topmost = false;
        }

        ScheduleSave();
    }

    private void OnDeleteZoneRequest(ZoneConfig config)
    {
        _config.Zones.Remove(config);
        var zone = _zones.FirstOrDefault(z => z.Config == config);
        if (zone != null)
        {
            _zones.Remove(zone);
            zone.Close();
        }
        ScheduleSave();
    }

    private void OnToggleZoneRequest(ZoneConfig config, bool isVisible)
    {
        var zone = _zones.FirstOrDefault(z => z.Config == config);
        if (zone != null)
        {
            if (isVisible) zone.ShowZone();
            else zone.HideZone();
        }
    }

    private void OnConfigurationChanged()
    {
        ScheduleSave();
        foreach (var zone in _zones)
        {
            zone.SyncFromConfig();
        }
    }

    private void ScheduleSave()
    {
        foreach (var zone in _zones) zone.SnapshotPosition();
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }
    // ── Global Pomodoro ──────────────────────────────────────────────

    private void StartGlobalPomodoro(string? zoneId)
    {
        if (_pomodoroService!.IsRunning)
        {
            var r = MessageBox.Show(
                $"番茄鐘正在進行中（{_pomodoroService.ActiveZoneTitle}）。\n要停止嗎？",
                "FocusFence 番茄鐘", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
            {
                _pomodoroService.Stop();
                ClosePomodoroTimer();
            }
            return;
        }

        _dashboard.Show();
        if (_dashboard.WindowState == WindowState.Minimized) _dashboard.WindowState = WindowState.Normal;
        _dashboard.Activate();
        _dashboard.RefreshData();
        _dashboard.ShowPomodoroTab(zoneId);
    }

    // ── Global Memo ─────────────────────────────────────────────────

    private Window? _globalMemoWindow;

    private void ShowGlobalMemo()
    {
        if (_globalMemoWindow != null && _globalMemoWindow.IsVisible)
        {
            _globalMemoWindow.Activate();
            return;
        }

        _globalMemoWindow = new Window
        {
            Title = "📝 總便利貼",
            Width = 340, Height = 400,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.ToolWindow,
            Topmost = true,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x2A, 0x2A)),
        };

        var tb = new System.Windows.Controls.TextBox
        {
            Text = _config.GlobalMemo,
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = System.Windows.Media.Brushes.White,
            CaretBrush = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            FontSize = 14,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Display, Microsoft JhengHei UI"),
            Padding = new Thickness(16),
        };

        tb.TextChanged += (_, _) =>
        {
            _config.GlobalMemo = tb.Text;
            ScheduleSave();
        };

        _globalMemoWindow.Content = tb;
        _globalMemoWindow.Show();
    }

    // ── System Tray ──────────────────────────────────────────────────

    private void InitTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateCoolIcon(),
            Text = "FocusFence",
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => _dashboard.Show();

        InitWpfTrayMenu();
    }

    private System.Windows.Controls.ContextMenu _wpfTrayMenu = null!;

    private void InitWpfTrayMenu()
    {
        _wpfTrayMenu = new System.Windows.Controls.ContextMenu();

        var dashItem = new System.Windows.Controls.MenuItem { Header = "控制台 (Dashboard)" };
        dashItem.Click += (_, _) => _dashboard.Show();

        var toggleItem = new System.Windows.Controls.MenuItem { Header = "顯示/隱藏所有框框 (Ctrl+Alt+F)" };
        toggleItem.Click += (_, _) => ToggleAllZones();

        var pomoItem = new System.Windows.Controls.MenuItem { Header = "🍅 番茄鐘 (Pomodoro)" };
        pomoItem.Click += (_, _) => Dispatcher.BeginInvoke(() => StartGlobalPomodoro(null));

        var memoItem = new System.Windows.Controls.MenuItem { Header = "📝 總便利貼 (Global Memo)" };
        memoItem.Click += (_, _) => Dispatcher.BeginInvoke(ShowGlobalMemo);

        var saveItem = new System.Windows.Controls.MenuItem { Header = "強制儲存 (Force Save)" };
        saveItem.Click += (_, _) =>
        {
            foreach (var zone in _zones) zone.SnapshotPosition();
            ConfigService.Save(_config);
            _autoSaveTimer.Stop();
        };

        var startupItem = new System.Windows.Controls.MenuItem 
        { 
            Header = "開機自動啟動 (Startup)",
            IsCheckable = true,
            IsChecked = StartupService.IsEnabled()
        };
        startupItem.Click += (_, _) => StartupService.SetEnabled(startupItem.IsChecked);

        var publishItem = new System.Windows.Controls.MenuItem { Header = "🛠️ 打包並固定到工具列 (Build & Pin)" };
        publishItem.Click += async (_, _) => 
        {
            MessageBox.Show("正在進行正式打包發佈...\n這將產出一個「單一檔案、免安裝」的高效能版本。\n\n完成後會自動開啟資料夾，請將 FocusFence.exe 拖曳至您的工具列固定即可。", "FocusFence Build", MessageBoxButton.OK, MessageBoxImage.Information);
            try {
                string publishCmd = "dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishReadyToRun=true /p:IncludeNativeLibrariesForSelfContained=true";
                await Task.Run(() => {
                    var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                        FileName = "cmd.exe",
                        Arguments = $"/c {publishCmd} & explorer bin\\Release\\net9.0-windows\\win-x64\\publish",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    process?.WaitForExit();
                });
            } catch (Exception ex) {
                MessageBox.Show($"打包失敗：{ex.Message}");
            }
        };

        var exitItem = new System.Windows.Controls.MenuItem { Header = "結束 (Exit)", Foreground = System.Windows.Media.Brushes.IndianRed };
        exitItem.Click += (_, _) => ExitApp();

        _wpfTrayMenu.Items.Add(dashItem);
        _wpfTrayMenu.Items.Add(toggleItem);
        _wpfTrayMenu.Items.Add(new System.Windows.Controls.Separator());
        _wpfTrayMenu.Items.Add(pomoItem);
        _wpfTrayMenu.Items.Add(memoItem);
        _wpfTrayMenu.Items.Add(new System.Windows.Controls.Separator());
        _wpfTrayMenu.Items.Add(saveItem);
        _wpfTrayMenu.Items.Add(startupItem);
        _wpfTrayMenu.Items.Add(publishItem);
        _wpfTrayMenu.Items.Add(new System.Windows.Controls.Separator());
        _wpfTrayMenu.Items.Add(exitItem);

        _trayIcon.MouseUp += (s, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right)
            {
                var w = new Window 
                { 
                    WindowStyle = WindowStyle.None, AllowsTransparency = true, 
                    Background = System.Windows.Media.Brushes.Transparent, 
                    Width = 0, Height = 0, ShowInTaskbar = false, Topmost = true,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = System.Windows.Forms.Cursor.Position.X,
                    Top = System.Windows.Forms.Cursor.Position.Y
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
        };
    }

    private Icon CreateCoolIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Draw outer thick frame (Gold)
        using var outerPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(200, 198, 156, 109), 4);
        g.DrawRectangle(outerPen, 4, 4, 24, 24);

        // Draw inner dark block
        using var innerBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, 30, 30, 40));
        g.FillRectangle(innerBrush, 8, 8, 16, 16);

        // Draw a neat diagonal slash to represent "focus / fencing"
        using var slashPen = new System.Drawing.Pen(System.Drawing.Color.White, 3);
        g.DrawLine(slashPen, 8, 24, 24, 8);

        IntPtr hIcon = bmp.GetHicon();
        Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    private void ExitApp()
    {
        _autoSaveTimer.Stop();

        // Cancel background listener thread first
        _appCts?.Cancel();

        // Stop services before disposing (correct order)
        _dormancyService?.Stop();
        _downloadCatcherService?.Stop();
        _pomodoroService?.Stop();

        // Now dispose
        _hotkeyService?.Dispose();
        _pomodoroService?.Dispose();
        _dormancyService?.Dispose();
        if (_mouseHookId != IntPtr.Zero) UnhookWindowsHookEx(_mouseHookId);

        foreach (var zone in _zones) zone.SnapshotPosition();
        ConfigService.Save(_config);

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        foreach (var zone in _zones) zone.Close();
        _dashboard.Close();
        _hotkeyWindow?.Close();
        _showDashboardEvent?.Dispose();
        _appCts?.Dispose();

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    private void AutoArrangeZones()
    {
        var visibleZones = _zones
            .Where(z => z.IsVisible && z.Config.IsVisible)
            .OrderBy(z => _config.Zones.IndexOf(z.Config))
            .ToList();
        if (visibleZones.Count == 0) return;

        var handle = new System.Windows.Interop.WindowInteropHelper(_dashboard).Handle;
        var screen = System.Windows.Forms.Screen.FromHandle(handle);

        // WPF uses device-independent pixels, so we might need DPI scaling for perfection,
        // but since we are just putting them on screen without heavy exactness, Form bounds are close enough.
        // For a more accurate approach using WPF visual presentation source:
        var source = System.Windows.PresentationSource.FromVisual(_dashboard);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        double screenWidth = screen.WorkingArea.Width / scaleX;
        double screenHeight = screen.WorkingArea.Height / scaleY;
        
        double gap = 24;
        double startX = (screen.WorkingArea.Left / scaleX) + gap;
        double startY = (screen.WorkingArea.Top / scaleY) + gap;
        
        double currentX = startX;
        double currentY = startY;
        double rowMaxHeight = 0;

        foreach (var zone in visibleZones)
        {
            // Simple flow layout
            if (currentX + zone.Width > screenWidth && currentX > startX)
            {
                currentX = startX;
                currentY += rowMaxHeight + gap;
                rowMaxHeight = 0;
            }

            var leftAnim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = currentX,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new System.Windows.Media.Animation.QuinticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            var topAnim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = currentY,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new System.Windows.Media.Animation.QuinticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };

            zone.BeginAnimation(Window.LeftProperty, leftAnim);
            zone.BeginAnimation(Window.TopProperty, topAnim);

            zone.Config.X = currentX;
            zone.Config.Y = currentY;

            currentX += zone.Width + gap;
            if (zone.Height > rowMaxHeight) rowMaxHeight = zone.Height;
        }
        
        ConfigService.Save(_config);
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FocusFence");
            Directory.CreateDirectory(dir);
            string logPath = Path.Combine(dir, "crash.log");
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]\n{ex}\n{new string('─', 60)}\n";
            File.AppendAllText(logPath, entry);
        }
        catch { /* cannot log, swallow */ }
    }
}
