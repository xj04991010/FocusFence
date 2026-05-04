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

    private TrayService _trayService = null!;
    private ZoneManagerService _zoneManager = null!;
    private AppConfig _config = null!;
    private DashboardWindow _dashboard = null!;
    private DispatcherTimer _autoSaveTimer = null!;
    private HotkeyService? _hotkeyService;
    private Window? _hotkeyWindow;
    private PomodoroService? _pomodoroService;
    private DormancyService? _dormancyService;
    private ContextLaunchService? _contextLaunchService;
    private DownloadCatcherService? _downloadCatcherService;
    private IntPtr _mouseHookId = IntPtr.Zero;
    private LowLevelMouseProc? _mouseHookProc; // prevent GC collection
    private long _lastDoubleClickTicks = 0; // debounce (Thread-safe)

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
            
            var r = FocusFenceDialog.ShowConfirm(
                $"發生未預期的錯誤：\n{args.Exception.Message}\n\n為避免資料損毀，建議重新啟動應用程式。是否立即關閉？", 
                "FocusFence 嚴重錯誤", destructive: true);
                
            if (r) 
            {
                Environment.Exit(1);
            }
            args.Handled = true; // Attempt to continue if user chose not to close
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

        _zoneManager = new ZoneManagerService(_config);
        _zoneManager.RequestSave += ScheduleSave;
        _zoneManager.RequestDashboardRefresh += () => _dashboard.RefreshData();
        _zoneManager.RequestStartPomodoro += StartGlobalPomodoro;
        _zoneManager.RequestContextLaunch += (zoneId) => _contextLaunchService?.LaunchContext(zoneId);

        // Create Dashboard
        _dashboard = new DashboardWindow(_config);
        _dashboard.ConfigurationChanged += OnConfigurationChanged;
        _dashboard.RequestCreateZone += _zoneManager.OnCreateZoneRequest;
        _dashboard.RequestDeleteZone += _zoneManager.OnDeleteZoneRequest;
        _dashboard.RequestToggleZone += _zoneManager.OnToggleZoneRequest;
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
        _dashboard.RequestSummonZone += _zoneManager.SummonZone;
        _dashboard.RequestAutoArrange += () => _zoneManager.AutoArrangeZones(_dashboard);

        // Create initial zones
        _zoneManager.InitializeInitialZones();

        _trayService = new TrayService();
        _trayService.RequestDashboard += () => { _dashboard.Show(); _dashboard.Activate(); };
        _trayService.RequestToggleZones += _zoneManager.ToggleAllZones;
        _trayService.RequestPomodoro += () => StartGlobalPomodoro(null);
        _trayService.RequestGlobalMemo += ShowGlobalMemo;
        _trayService.RequestSave += () =>
        {
            _zoneManager.SnapshotAllPositions();
            ConfigService.Save(_config);
            _autoSaveTimer.Stop();
        };
        _trayService.RequestExit += ExitApp;
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
            var zone = _zoneManager.GetZone(zoneId);
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
            var zone = _zoneManager.GetZone(zoneId);
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
            foreach (var zone in _zoneManager.Zones)
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
            foreach (var zone in _zoneManager.Zones)
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
            foreach (var zone in _zoneManager.Zones)
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
                long nowTicks = DateTime.UtcNow.Ticks;
                long lastTicks = Interlocked.Read(ref _lastDoubleClickTicks);
                if (TimeSpan.FromTicks(nowTicks - lastTicks).TotalMilliseconds < 300) return;

                IntPtr hwndUnder = WindowFromPoint(px, py);
                if (IsDesktopWindow(hwndUnder))
                {
                    Interlocked.Exchange(ref _lastDoubleClickTicks, nowTicks);
                    Application.Current?.Dispatcher.BeginInvoke(_zoneManager.ToggleAllZones);
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
            onToggleZones: _zoneManager.ToggleAllZones,
            onShowDashboard: () => { _dashboard.Show(); _dashboard.Activate(); },
            onPrevDesktop: () => SwitchDesktopPage(-1),
            onNextDesktop: () => SwitchDesktopPage(1)
        );
    }

    private void SwitchDesktopPage(int delta)
    {
        _config.ActiveDesktopPage += delta;
        if (_config.ActiveDesktopPage < 0) _config.ActiveDesktopPage = 0;
        
        _zoneManager.UpdateZoneVisibilityForPage();
        ScheduleSave();
    }

    private void OnConfigurationChanged()
    {
        ScheduleSave();
        _zoneManager.SyncFromConfig();
    }

    private void ScheduleSave()
    {
        _zoneManager.SnapshotAllPositions();
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

        _zoneManager.SnapshotAllPositions();
        ConfigService.Save(_config);

        _trayService?.Dispose();

        _zoneManager.CloseAll();
        _dashboard.Close();
        _hotkeyWindow?.Close();
        _showDashboardEvent?.Dispose();
        _appCts?.Dispose();

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayService?.Dispose();
        base.OnExit(e);
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
