using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using FocusFence.Controls;
using FocusFence.Helpers;
using FocusFence.Interop;
using FocusFence.Models;
using FocusFence.Services;
using Microsoft.VisualBasic.FileIO;

namespace FocusFence.Windows;

public partial class ZoneWindow : Window, INotifyPropertyChanged
{
    private ZoneConfig _config;
    private IntPtr _hwnd;

    // Files
    private readonly ObservableCollection<FileItem> _files = [];
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounceTimer;

    // Navigation
    private string _currentPath = "";
    private readonly Stack<string> _navStack = new();
    private string _searchQuery = "";
    private SortMode _currentSortMode = SortMode.Name;
    private bool _sortDescending = false;

    // Resize
    private bool _isResizing;
    private Point _resizeStart;
    private double _resizeStartW, _resizeStartH;

    // Drag-out
    private Point _dragStartPoint;
    private bool _isDragSource;
    private static ZoneWindow? _currentDragSourceZone;

    // Collapse
    private double _expandedHeight;

    // Hover expand
    private bool _isHoverExpanded;
    private DispatcherTimer? _hoverCollapseTimer;

    // Suppress ZoneChanged during SyncFromConfig to avoid feedback loops
    private bool _suppressZoneChanged;



    // Color palette
    private static readonly string[] Palette =
        ["#E8A87C","#5B8DEF","#41D68B","#EF5350","#AB6FE8",
         "#FBC02D","#26C6DA","#F06292","#78909C","#66BB6A",
         "#FF7043","#29B6F6"];

    public ZoneConfig Config => _config;
    public event Action? ZoneChanged;
    public event Action<ZoneWindow>? ZoneCloseRequested;
    public event Action<string>? PomodoroStartRequested;
    public event Action<string>? ContextLaunchRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ZoneWindow(ZoneConfig config)
    {
        InitializeComponent();
        _config = config;
        UpdateSizingProperties();
        Left = config.X; Top = config.Y;
        Width = config.Width; Height = config.Height;
        _expandedHeight = config.Height;
        TitleText.Text = config.Title;
        ZoneLabelText.Text = config.Title;
        _currentPath = config.FolderPath ?? "";

        FileListBox.ItemsSource = _files;

        AllowDrop = true;
        Drop += OnFileDrop;
        DragEnter += OnDragEnter;
        DragLeave += OnDragLeave;
        DragOver += OnDragOver;
        Loaded += OnLoaded;
        LocationChanged += OnLocationChanged;
        KeyDown += OnWindowKeyDown;
        PreviewMouseWheel += OnCtrlScrollWheel;
        MouseEnter += OnZoneMouseEnter;
        MouseLeave += OnZoneMouseLeave;
        Deactivated += (_, _) => { if (!_isDragSource && !_config.IsPinned) SendToBottom(); };

        // Per PART 2B: set the base background to dark glass
        MainBorder.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x1E, 0x1E));
    }

    // ── Lifecycle ────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = Win32Api.GetWindowLong(_hwnd, Win32Api.GWL_EXSTYLE);
        Win32Api.SetWindowLong(_hwnd, Win32Api.GWL_EXSTYLE, ex | Win32Api.WS_EX_TOOLWINDOW);

        ApplyAcrylicBlur();
        ApplyAccentColor(_config.AccentColor);
        ApplyOpacity();
        ApplyNoiseTexture();
        InitColorPalette();

        UpdatePinState();
        if (!_config.IsPinned) SendToBottom();

        if (_config.IsCollapsed) ApplyCollapsed();

        RefreshFiles();
        StartWatcher();
        UpdateDormancyBadge();

        // Restore Pomodoro duration slider
        PomodoroDurationSlider.Value = _config.Pomodoro?.DurationMinutes ?? 25;

        FocusFence.Services.UndoService.UndoExecuted += UndoService_UndoExecuted;

        // PART 4C: Zone appear animation (Spring curve)
        PlayAppearAnimation();
    }

    private void UndoService_UndoExecuted()
    {
        Dispatcher.InvokeAsync(RefreshFiles);
    }

    /// <summary>
    /// PART 2A Layer 3: Applies the film-grain noise texture overlay.
    /// </summary>
    private void ApplyNoiseTexture()
    {
        NoiseOverlay.Background = NoiseTextureHelper.CreateNoiseBrush(0.025);
    }

    /// <summary>
    /// PART 4C: Zone appear — Opacity + ScaleXY + TranslateY with Spring curve.
    /// </summary>
    private void PlayAppearAnimation()
    {
        if (AnimationHelper.AnimationsDisabled)
        {
            MainBorder.Opacity = 1;
            return;
        }

        MainBorder.Opacity = 0;
        ZoneScale.ScaleX = 0.96;
        ZoneScale.ScaleY = 0.96;
        ZoneTranslate.Y = 6;

        MainBorder.BeginAnimation(OpacityProperty, AnimationHelper.FadeInSpring());
        ZoneScale.BeginAnimation(ScaleTransform.ScaleXProperty, AnimationHelper.ScaleInSpring());
        ZoneScale.BeginAnimation(ScaleTransform.ScaleYProperty, AnimationHelper.ScaleInSpring());
        ZoneTranslate.BeginAnimation(TranslateTransform.YProperty, AnimationHelper.TranslateYInSpring());
    }

    private void ApplyOpacity()
    {
        // Apply user-configured opacity to the main background
        if (MainBorder.Background is SolidColorBrush bg)
        {
            MainBorder.Background = new SolidColorBrush(bg.Color) { Opacity = _config.Opacity };
        }
    }

    private void SendToBottom()
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32Api.SetWindowPos(_hwnd, Win32Api.HWND_BOTTOM, 0, 0, 0, 0,
            Win32Api.SWP_NOMOVE | Win32Api.SWP_NOSIZE | Win32Api.SWP_NOACTIVATE);
    }

    private void ApplyAcrylicBlur()
    {
        try
        {
            var version = Environment.OSVersion.Version;

            if (version.Build >= 22621)
            {
                int backdropType = 3;
                Win32Api.DwmSetWindowAttribute(_hwnd, 38, ref backdropType, sizeof(int));
                int useDarkMode = 1;
                Win32Api.DwmSetWindowAttribute(_hwnd, 20, ref useDarkMode, sizeof(int));
            }
            else
            {
                ApplyLegacyAcrylicBlur();
            }
        }
        catch { /* Acrylic is cosmetic, don't crash */ }
    }

    private void ApplyLegacyAcrylicBlur()
    {
        var accent = new Win32Api.AccentPolicy
        {
            AccentState = Win32Api.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            GradientColor = unchecked((int)0xCC2E1A1A)
        };
        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new Win32Api.WindowCompositionAttributeData
            {
                Attribute = Win32Api.WindowCompositionAttribute.WCA_ACCENT_POLICY,
                SizeOfData = size, Data = ptr
            };
            Win32Api.SetWindowCompositionAttribute(_hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    // ── DP & Sizing ──────────────────────────────────────────────────

    public double IconSize => _config.IconSize;
    public double ItemFontSize => Math.Max(10, _config.IconSize * 0.25);
    public double ItemBoxWidth => _config.IconSize * 1.6;
    public double ItemBoxHeight => _config.IconSize * 1.5 + 24;
    public double ItemTextWidth => _config.IconSize * 1.5;

    private void UpdateSizingProperties()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconSize)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemFontSize)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemBoxWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemBoxHeight)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemTextWidth)));
    }

    // ── Color & Popup ────────────────────────────────────────────────

    private void InitColorPalette()
    {
        foreach (var hex in Palette)
        {
            var el = new Ellipse
            {
                Width = 24, Height = 24, Margin = new Thickness(3),
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                Cursor = Cursors.Hand
            };
            string c = hex;
            el.MouseLeftButtonDown += (_, ev) => { ApplyAccentColor(c); ev.Handled = true; };
            ColorPalette.Children.Add(el);
        }
    }

    private void ColorPopup_Opened(object sender, EventArgs e)
    {
        OpacitySlider.Value = _config.Opacity;
        IconSizeSlider.Value = _config.IconSize;
        HoverExpandCheck.IsChecked = _config.HoverExpand;
        ShowMemoCheck.IsChecked = _config.ShowMemo;
        PomodoroDurationSlider.Value = _config.Pomodoro?.DurationMinutes ?? 25;
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_config == null) return;
        _config.Opacity = e.NewValue;
        ApplyOpacity();
        ZoneChanged?.Invoke();
    }

    private void IconSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_config == null) return;
        _config.IconSize = e.NewValue;
        UpdateSizingProperties();
        ZoneChanged?.Invoke();
    }

    private void HoverExpand_Checked(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        _config.HoverExpand = HoverExpandCheck.IsChecked ?? true;
        ZoneChanged?.Invoke();
    }

    private void ShowMemo_Checked(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        _config.ShowMemo = ShowMemoCheck.IsChecked ?? true;
        StickyNoteArea.Visibility = _config.ShowMemo ? Visibility.Visible : Visibility.Collapsed;
        ZoneChanged?.Invoke();
    }

    private void PomodoroDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_config == null) return;
        _config.Pomodoro ??= new PomodoroState();
        _config.Pomodoro.DurationMinutes = (int)e.NewValue;
        ZoneChanged?.Invoke();
    }

    private void ApplyAccentColor(string hex)
    {
        _config.AccentColor = hex;
        var color = (Color)ColorConverter.ConvertFromString(hex);

        // === Layer 1: Strong tint overlay on the entire zone background ===
        TintOverlay.Background = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B));

        // === Layer 2: Title bar gets a subtle accent gradient (left to right) ===
        // This creates a colored header stripe effect

        // === Layer 3: Bottom label text uses the accent color ===
        ZoneLabelText.Foreground = new SolidColorBrush(Color.FromArgb(0x88, color.R, color.G, color.B));

        // === Layer 4: Bottom label gradient uses accent color ===
        LabelGradient1.Color = Color.FromArgb(0x00, color.R, color.G, color.B);
        LabelGradient2.Color = Color.FromArgb(0x22, color.R, color.G, color.B);

        // === Layer 5: Main border gets an accent-colored top highlight ===
        MainBorder.BorderBrush = new LinearGradientBrush(
            Color.FromArgb(0x55, color.R, color.G, color.B),
            Color.FromArgb(0x18, color.R, color.G, color.B),
            new Point(0, 0), new Point(0, 1));

        if (!_suppressZoneChanged)
            ZoneChanged?.Invoke();
    }

    private void ZoneLabel_Click(object s, MouseButtonEventArgs e)
    {
        ColorPopup.IsOpen = true;
        e.Handled = true;
    }

    // ── Title Editing ────────────────────────────────────────────────

    private void TitleText_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            TitleEdit.Text = _config.Title;
            TitleText.Visibility = Visibility.Collapsed;
            TitleEdit.Visibility = Visibility.Visible;
            TitleEdit.Focus();
            TitleEdit.SelectAll();
            e.Handled = true;
        }
    }

    private void CommitTitleEdit()
    {
        string newTitle = TitleEdit.Text.Trim();
        if (!string.IsNullOrEmpty(newTitle))
        {
            _config.Title = newTitle;
            TitleText.Text = newTitle;
            ZoneLabelText.Text = newTitle;
        }
        TitleEdit.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        ZoneChanged?.Invoke();
    }

    private void TitleEdit_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitTitleEdit(); e.Handled = true; }
        if (e.Key == Key.Escape) { TitleEdit.Visibility = Visibility.Collapsed; TitleText.Visibility = Visibility.Visible; e.Handled = true; }
    }

    private void TitleEdit_LostFocus(object s, RoutedEventArgs e) => CommitTitleEdit();

    // ── Collapse ─────────────────────────────────────────────────────

    private void CollapseBtn_Click(object s, MouseButtonEventArgs e)
    {
        _config.IsCollapsed = !_config.IsCollapsed;
        if (_config.IsCollapsed) ApplyCollapsed(); else ApplyExpanded();
        ZoneChanged?.Invoke();
        e.Handled = true;
    }

    private void ApplyCollapsed()
    {
        _expandedHeight = _config.IsCollapsed ? _expandedHeight : Height;
        ContentArea.Visibility = Visibility.Collapsed;
        SearchBar.Visibility = Visibility.Collapsed;
        StickyNoteArea.Visibility = Visibility.Collapsed;
        DormancyBadge.Visibility = Visibility.Collapsed;
        
        // Show small timer when collapsed if running
        if (_config.Pomodoro != null && _config.Pomodoro.IsRunning) PomodoroRing.Visibility = Visibility.Visible;
        Height = 56;
        CollapseIcon.Text = "▶";
    }

    private void ApplyExpanded()
    {
        ContentArea.Visibility = Visibility.Visible;
        Height = _expandedHeight > 100 ? _expandedHeight : 280;
        CollapseIcon.Text = "▼";
        StickyNoteArea.Visibility = Visibility.Visible;
        UpdateDormancyBadge();

        if (_config.Pomodoro != null && _config.Pomodoro.IsRunning)
        {
            PomodoroRing.Visibility = Visibility.Collapsed;
        }
    }

    // ── Hover Expand (Fences-style) ──────────────────────────────────

    private void OnZoneMouseEnter(object sender, MouseEventArgs e)
    {
        _hoverCollapseTimer?.Stop();

        // PART 4C: Hover lift — TranslateY 0 → -2px, Shadow Layer1 Opacity +0.07
        if (!AnimationHelper.AnimationsDisabled)
        {
            ZoneTranslate.BeginAnimation(TranslateTransform.YProperty, AnimationHelper.HoverLift(true));
            ShadowLayer1.BeginAnimation(OpacityProperty, AnimationHelper.ShadowHover(true));
        }

        if (_config.IsCollapsed && _config.HoverExpand && !_isHoverExpanded)
        {
            _isHoverExpanded = true;
            ApplyExpanded();
        }
    }

    private void OnZoneMouseLeave(object sender, MouseEventArgs e)
    {
        // PART 4C: Hover exit — TranslateY → 0, Shadow Layer1 → 0.15
        if (!AnimationHelper.AnimationsDisabled)
        {
            ZoneTranslate.BeginAnimation(TranslateTransform.YProperty, AnimationHelper.HoverLift(false));
            ShadowLayer1.BeginAnimation(OpacityProperty, AnimationHelper.ShadowHover(false));
        }

        if (_isHoverExpanded)
        {
            _hoverCollapseTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _hoverCollapseTimer.Tick += (_, _) =>
            {
                _hoverCollapseTimer.Stop();
                if (_isHoverExpanded && !IsMouseOver)
                {
                    _isHoverExpanded = false;
                    ApplyCollapsed();
                }
            };
            _hoverCollapseTimer.Start();
        }
    }

    // ── Pin ──────────────────────────────────────────────────────────

    private void PinBtn_Click(object s, MouseButtonEventArgs e)
    {
        _config.IsPinned = !_config.IsPinned;
        UpdatePinState();
        ZoneChanged?.Invoke();
        e.Handled = true;
    }

    private void UpdatePinState()
    {
        Topmost = _config.IsPinned;
        PinIcon.Foreground = _config.IsPinned 
            ? new SolidColorBrush(Colors.White) 
            : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            
        if (_config.IsPinned)
        {
            // Pinned: always on top
            Win32Api.SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 
                Win32Api.SWP_NOMOVE | Win32Api.SWP_NOSIZE | Win32Api.SWP_NOACTIVATE);
        }
        else
        {
            // Unpinned: send to bottom
            SendToBottom();
        }
    }

    // ── Close/Hide ───────────────────────────────────────────────────

    private void CloseBtn_Click(object s, MouseButtonEventArgs e)
    {
        _config.IsVisible = false;
        Visibility = Visibility.Hidden;
        ZoneCloseRequested?.Invoke(this);
        e.Handled = true;
    }

    public void ShowZone()
    {
        _config.IsVisible = true;
        Visibility = Visibility.Visible;
        SendToBottom();
    }

    public void HideZone()
    {
        _config.IsVisible = false;
        Visibility = Visibility.Hidden;
    }

    // ── Pomodoro Timer ───────────────────────────────────────────────

    private void PomodoroBtn_Click(object s, MouseButtonEventArgs e)
    {
        PomodoroStartRequested?.Invoke(_config.Id);
        e.Handled = true;
    }

    private void PomodoroIndicator_Click(object s, MouseButtonEventArgs e)
    {
        // Clicking the running indicator also requests start/stop
        PomodoroStartRequested?.Invoke(_config.Id);
        e.Handled = true;
    }

    /// <summary>
    /// Called from App.xaml.cs when the PomodoroService fires OnTick.
    /// </summary>
    public void UpdatePomodoroDisplay(int remainingSeconds, int totalSeconds)
    {
        Dispatcher.BeginInvoke(() =>
        {
            PomodoroRing.Visibility = _config.Pomodoro != null && _config.Pomodoro.IsRunning ? Visibility.Visible : Visibility.Collapsed;

            double elapsed = totalSeconds - remainingSeconds;
            double ratio = totalSeconds > 0 ? elapsed / totalSeconds : 0;
            
            PomodoroRing.SetProgress(ratio, remainingSeconds);

            if (PomodoroRing.ToolTip is string st && st == "開始專注")
                PomodoroRing.SetRunning();

            // Warning phase: last 2 minutes — shift border color
            if (remainingSeconds <= 120 && remainingSeconds > 0)
            {
                double progress = 1.0 - (remainingSeconds / 120.0);
                byte r = (byte)(249 + (239 - 249) * progress);
                byte g = (byte)(115 + (68 - 115) * progress);
                byte b = (byte)(22 + (68 - 22) * progress);

                var borderBrush = new LinearGradientBrush(
                    Color.FromArgb(0xCC, r, g, b),
                    Color.FromArgb(0x66, r, g, b),
                    new Point(0, 0), new Point(1, 1));
                MainBorder.BorderBrush = borderBrush;
            }
        });
    }

    /// <summary>
    /// Called when Pomodoro session completes — reset display.
    /// </summary>
    public void OnPomodoroComplete()
    {
        Dispatcher.BeginInvoke(() =>
        {
            // PART 7C: Completion bounce, then reset to idle
            PomodoroRing.SetCompleted();

            // Delay hiding + reset border after bounce completes
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                PomodoroRing.Visibility = Visibility.Collapsed;
                // Restore original border brush
                MainBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("ZoneBorderBrush");
                ApplyAccentColor(_config.AccentColor);
            };
            timer.Start();
        });
    }

    /// <summary>
    /// Sets the focus dim/undim visual state.
    /// </summary>
    public void SetFocusDim(bool dimmed)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // PART 4C: Curve C (EaseInOut) — Context dim transition
            var anim = AnimationHelper.OpacityTransition(dimmed ? 0.4 : 1.0);
            MainBorder.BeginAnimation(OpacityProperty, anim);

            // PART 7A: Dormant-style desaturation for dimmed zones
            if (dimmed)
            {
                ShadowLayer1.BeginAnimation(OpacityProperty,
                    AnimationHelper.OpacityTransition(0.06));
                ShadowLayer2.BeginAnimation(OpacityProperty,
                    AnimationHelper.OpacityTransition(0.04));
                ShadowLayer3.BeginAnimation(OpacityProperty,
                    AnimationHelper.OpacityTransition(0.02));
            }
            else
            {
                ShadowLayer1.BeginAnimation(OpacityProperty,
                    AnimationHelper.OpacityTransition(0.15));
                ShadowLayer2.BeginAnimation(OpacityProperty,
                    AnimationHelper.OpacityTransition(0.10));
                ShadowLayer3.BeginAnimation(OpacityProperty,
                    AnimationHelper.OpacityTransition(0.06));
            }
        });
    }

    /// <summary>
    /// Shows or hides the context-active glow border.
    /// </summary>
    public void SetContextActive(bool active)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // PART 4C: Curve C (EaseInOut) — Context glow transition 280ms
            var anim = AnimationHelper.OpacityTransition(active ? 1.0 : 0.0);

            if (active)
            {
                ContextGlowBorder.BorderThickness = new Thickness(2);
                // Switch main border to teal gradient
                MainBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("ZoneBorderActiveBrush");
            }

            ContextGlowBorder.BeginAnimation(OpacityProperty, anim);

            if (!active)
            {
                // Restore default highlight border after fade out
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    ContextGlowBorder.BorderThickness = new Thickness(0);
                    MainBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("ZoneBorderBrush");
                };
                timer.Start();
            }
        });
    }
    private void ZoneMemo_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_config != null && _config.Memo != ZoneMemo.Text)
        {
            _config.Memo = ZoneMemo.Text;
            ZoneChanged?.Invoke();
        }
    }

    // ── Dormancy ─────────────────────────────────────────────────────

    public void UpdateDormancyBadge()
    {
        if (_config.IsDormant)
        {
            DormancyBadge.Visibility = _config.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            DormancyText.Text = $"🌙 {DormancyService.GetDormancyTooltip(_config)}";
        }
        else
        {
            DormancyBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void DormancyBadge_Click(object s, MouseButtonEventArgs e)
    {
        // Reactivate from dormancy
        _config.IsDormant = false;
        _config.LastInteractedAt = DateTime.Now;
        UpdateDormancyBadge();
        ZoneChanged?.Invoke();
        e.Handled = true;
    }

    // ── File List ────────────────────────────────────────────────────

    public void SyncFromConfig()
    {
        _suppressZoneChanged = true;
        try
        {
            TitleText.Text = _config.Title;
            ZoneLabelText.Text = _config.Title;
            if (TitleEdit.Visibility == Visibility.Visible) { TitleText.Visibility = Visibility.Visible; TitleEdit.Visibility = Visibility.Collapsed; }

            ApplyAccentColor(_config.AccentColor);
            StickyNoteArea.Visibility = _config.ShowMemo ? Visibility.Visible : Visibility.Collapsed;

            // Only reset navigation if the zone's root folder actually changed.
            // Don't reset when user is just browsing inside a subfolder.
            string configRoot = _config.FolderPath ?? "";
            bool rootChanged = !string.Equals(_currentPath, configRoot, StringComparison.OrdinalIgnoreCase)
                && !_currentPath.StartsWith(configRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (rootChanged)
            {
                _currentPath = configRoot;
                _navStack.Clear();
                StopWatcher();
                RefreshFiles();
                StartWatcher();
                UpdateBreadcrumb();
            }

            if (ZoneMemo.Text != _config.Memo)
                ZoneMemo.Text = _config.Memo;
            UpdateDormancyBadge();
        }
        finally
        {
            _suppressZoneChanged = false;
        }
    }

    public void RefreshFiles()
    {
        if (_files.Any(f => f.IsEditing)) return;

        _files.Clear();
        if (string.IsNullOrEmpty(_currentPath) || !Directory.Exists(_currentPath))
        {
            PlaceholderText.Visibility = Visibility.Visible;
            FileCountText.Text = "";
            return;
        }

        var entries = Directory.GetFileSystemEntries(_currentPath);
        var items = new List<FileItem>();
        foreach (string p in entries)
        {
            var item = FileItem.FromPath(p);
            // Apply search filter
            if (!string.IsNullOrEmpty(_searchQuery) &&
                !item.FileName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
                continue;
            items.Add(item);
        }

        // Apply sorting (directories always first)
        var dirs = items.Where(i => i.IsDirectory);
        var files = items.Where(i => !i.IsDirectory);

        dirs = _currentSortMode switch
        {
            SortMode.Name => _sortDescending ? dirs.OrderByDescending(f => f.FileName) : dirs.OrderBy(f => f.FileName),
            SortMode.Date => _sortDescending ? dirs.OrderByDescending(f => f.LastModified) : dirs.OrderBy(f => f.LastModified),
            SortMode.Type => _sortDescending ? dirs.OrderByDescending(f => f.FileName) : dirs.OrderBy(f => f.FileName),
            SortMode.Size => _sortDescending ? dirs.OrderByDescending(f => f.FileName) : dirs.OrderBy(f => f.FileName),
            _ => dirs.OrderBy(f => f.FileName)
        };

        files = _currentSortMode switch
        {
            SortMode.Name => _sortDescending ? files.OrderByDescending(f => f.FileName) : files.OrderBy(f => f.FileName),
            SortMode.Date => _sortDescending ? files.OrderByDescending(f => f.LastModified) : files.OrderBy(f => f.LastModified),
            SortMode.Type => _sortDescending ? files.OrderByDescending(f => System.IO.Path.GetExtension(f.FullPath)) : files.OrderBy(f => System.IO.Path.GetExtension(f.FullPath)),
            SortMode.Size => _sortDescending ? files.OrderByDescending(f => f.FileSize) : files.OrderBy(f => f.FileSize),
            _ => files.OrderBy(f => f.FileName)
        };

        foreach (var item in dirs.Concat(files))
            _files.Add(item);

        PlaceholderText.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderText.Text = !string.IsNullOrEmpty(_searchQuery) && _files.Count == 0
            ? $"找不到「{_searchQuery}」"
            : "拖曳檔案到此處...";
        FileCountText.Text = $"{_files.Count} 個";
    }

    // ── Search ───────────────────────────────────────────────────────

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+F → open search
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_config.IsCollapsed) return;
            SearchBar.Visibility = Visibility.Visible;
            SearchBox.Focus();
            e.Handled = true;
        }

        // Escape → close search if open
        if (e.Key == Key.Escape && SearchBar.Visibility == Visibility.Visible)
        {
            CloseSearch();
            e.Handled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchBox.Text.Trim();
        RefreshFiles();
    }

    private void SearchClear_Click(object sender, MouseButtonEventArgs e)
    {
        CloseSearch();
        e.Handled = true;
    }

    private void CloseSearch()
    {
        _searchQuery = "";
        SearchBox.Text = "";
        SearchBar.Visibility = Visibility.Collapsed;
        RefreshFiles();
    }

    // ── Ctrl+Scroll Icon Sizing ──────────────────────────────────────

    private void OnCtrlScrollWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        double step = 4;
        double newSize = _config.IconSize + (e.Delta > 0 ? step : -step);
        newSize = Math.Clamp(newSize, 20, 96);

        if (Math.Abs(newSize - _config.IconSize) > 0.1)
        {
            _config.IconSize = newSize;
            UpdateSizingProperties();
            ZoneChanged?.Invoke();
        }

        e.Handled = true;
    }

    // ── Sort Logic ───────────────────────────────────────────────

    private void SetSortMode(SortMode mode)
    {
        if (_currentSortMode == mode)
            _sortDescending = !_sortDescending;
        else
        {
            _currentSortMode = mode;
            _sortDescending = false;
        }
        UpdateSortBarVisuals();
        RefreshFiles();
    }

    private void UpdateSortBarVisuals()
    {
        var buttons = new[] { (SortByName, SortMode.Name), (SortByDate, SortMode.Date), (SortByType, SortMode.Type), (SortBySize, SortMode.Size) };
        foreach (var (border, mode) in buttons)
        {
            bool isActive = _currentSortMode == mode;
            border.Background = isActive
                ? new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))
                : Brushes.Transparent;
            if (border.Child is TextBlock tb)
            {
                string arrow = isActive ? (_sortDescending ? " ↓" : " ↑") : "";
                string baseName = mode switch
                {
                    SortMode.Name => "名稱",
                    SortMode.Date => "日期",
                    SortMode.Type => "類型",
                    SortMode.Size => "大小",
                    _ => ""
                };
                tb.Text = baseName + arrow;
                tb.Foreground = isActive
                    ? new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF))
                    : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            }
        }
    }

    private void SortByName_Click(object s, MouseButtonEventArgs e) { SetSortMode(SortMode.Name); e.Handled = true; }
    private void SortByDate_Click(object s, MouseButtonEventArgs e) { SetSortMode(SortMode.Date); e.Handled = true; }
    private void SortByType_Click(object s, MouseButtonEventArgs e) { SetSortMode(SortMode.Type); e.Handled = true; }
    private void SortBySize_Click(object s, MouseButtonEventArgs e) { SetSortMode(SortMode.Size); e.Handled = true; }

    private void StartWatcher()
    {
        StopWatcher();
        if (string.IsNullOrEmpty(_currentPath) || !Directory.Exists(_currentPath)) return;

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _debounceTimer.Tick += (_, _) => { _debounceTimer.Stop(); RefreshFiles(); };

        _watcher = new FileSystemWatcher(_currentPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };
        _watcher.Created += FsEvent;
        _watcher.Deleted += FsEvent;
        _watcher.Renamed += (_, _) => Dispatcher.Invoke(() => { _debounceTimer?.Stop(); _debounceTimer?.Start(); });
    }

    private void FsEvent(object s, FileSystemEventArgs e) =>
        Dispatcher.Invoke(() => { _debounceTimer?.Stop(); _debounceTimer?.Start(); });

    private void StopWatcher()
    {
        _watcher?.Dispose(); _watcher = null;
        _debounceTimer?.Stop(); _debounceTimer = null;
    }

    // ── Folder Navigation ────────────────────────────────────────────

    private void FileList_DoubleClick(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src)
        {
            while (src != null)
            {
                if (src is System.Windows.Controls.TextBox) return;
                src = System.Windows.Media.VisualTreeHelper.GetParent(src);
            }
        }

        if (FileListBox.SelectedItem is not FileItem file) return;
        if (file.IsEditing) return; // Block opening file/folder when renaming
        
        if (file.IsDirectory)
        {
            _navStack.Push(_currentPath);
            _currentPath = file.FullPath;
            StopWatcher(); RefreshFiles(); StartWatcher();
            UpdateBreadcrumb();
        }
        else
        {
            // Track interaction for dormancy
            _config.LastInteractedAt = DateTime.Now;
            try { Process.Start(new ProcessStartInfo { FileName = file.FullPath, UseShellExecute = true }); }
            catch (Exception ex) { FocusFenceDialog.ShowMessage($"無法開啟：{ex.Message}", "FocusFence", true); }
        }
    }

    /// <summary>
    /// Navigates the zone view into the specified folder path.
    /// </summary>
    private void NavigateIntoFolder(string folderPath)
    {
        _navStack.Push(_currentPath);
        _currentPath = folderPath;
        StopWatcher(); RefreshFiles(); StartWatcher();
        UpdateBreadcrumb();
    }

    private void NavigateBack_Click(object s, MouseButtonEventArgs e)
    {
        if (_navStack.Count == 0) return;
        _currentPath = _navStack.Pop();
        StopWatcher(); RefreshFiles(); StartWatcher();
        UpdateBreadcrumb();
        e.Handled = true;
    }

    private void UpdateBreadcrumb()
    {
        string root = _config.FolderPath ?? "";
        if (_currentPath == root || _navStack.Count == 0)
        {
            BreadcrumbBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            BreadcrumbBar.Visibility = _config.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            string rel = System.IO.Path.GetRelativePath(root, _currentPath);
            BreadcrumbText.Text = rel;
        }
    }

    // ── Drag OUT & Rubber Band Selection (multi-select) ──────────────

    private bool _isRubberBanding = false;
    private Point _rubberBandStartPoint;
    private bool _suppressSelectionUp = false;

    private void FileList_PreviewMouseDown(object s, MouseButtonEventArgs e)
    {
        FileListBox.Focus(); // Fix Delete key issue by ensuring ListBox gets focus

        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is FileItem fi && fi.IsEditing) return;

        // Handle double-click directly here, because setting e.Handled=true
        // on the first click prevents MouseDoubleClick from firing reliably.
        if (e.ClickCount == 2)
        {
            // Find which item was double-clicked
            var dblClickTarget = e.OriginalSource as DependencyObject;
            FileItem? dblClickFile = null;
            while (dblClickTarget != null && dblClickTarget != FileListBox)
            {
                if (dblClickTarget is System.Windows.Controls.TextBox) { e.Handled = true; return; }
                if (dblClickTarget is ListBoxItem lbiDbl && lbiDbl.DataContext is FileItem fiDbl)
                {
                    dblClickFile = fiDbl;
                    break;
                }
                dblClickTarget = VisualTreeHelper.GetParent(dblClickTarget);
            }

            if (dblClickFile != null && !dblClickFile.IsEditing)
            {
                if (dblClickFile.IsDirectory)
                {
                    NavigateIntoFolder(dblClickFile.FullPath);
                }
                else
                {
                    _config.LastInteractedAt = DateTime.Now;
                    try { Process.Start(new ProcessStartInfo { FileName = dblClickFile.FullPath, UseShellExecute = true }); }
                    catch (Exception ex) { FocusFenceDialog.ShowMessage($"無法開啟：{ex.Message}", "FocusFence", true); }
                }
            }
            e.Handled = true;
            return;
        }

        bool clickedOnItem = false;
        object? clickedItemData = null;
        var current = e.OriginalSource as DependencyObject;
        while (current != null && current != FileListBox)
        {
            if (current is ListBoxItem lbi) 
            { 
                clickedOnItem = true; 
                clickedItemData = lbi.DataContext;
                break; 
            }
            current = VisualTreeHelper.GetParent(current);
        }

        if (clickedOnItem)
        {
            _dragStartPoint = e.GetPosition(null);
            _suppressSelectionUp = false;

            if (clickedItemData != null && FileListBox.SelectedItems.Contains(clickedItemData))
            {
                // If it is already selected, PREVENT ListBox from clearing the selection.
                // We handle single-click selection clearing in MouseUp instead if it wasn't a drag.
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                {
                    e.Handled = true;
                    _suppressSelectionUp = true;
                }
            }
        }
        else
        {
            _isRubberBanding = true;
            _rubberBandStartPoint = e.GetPosition(FileListBox);
            Canvas.SetLeft(SelectionRect, _rubberBandStartPoint.X);
            Canvas.SetTop(SelectionRect, _rubberBandStartPoint.Y);
            SelectionRect.Width = 0;
            SelectionRect.Height = 0;
            SelectionRect.Visibility = Visibility.Visible;
            FileListBox.CaptureMouse();
            
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                FileListBox.SelectedItems.Clear();
                
            e.Handled = true;
        }
    }

    private void FileList_PreviewMouseMove(object s, MouseEventArgs e)
    {
        if (_isRubberBanding)
        {
            var pos = e.GetPosition(FileListBox);
            double x = Math.Min(pos.X, _rubberBandStartPoint.X);
            double y = Math.Min(pos.Y, _rubberBandStartPoint.Y);
            double w = Math.Abs(pos.X - _rubberBandStartPoint.X);
            double h = Math.Abs(pos.Y - _rubberBandStartPoint.Y);

            Canvas.SetLeft(SelectionRect, x);
            Canvas.SetTop(SelectionRect, y);
            SelectionRect.Width = w;
            SelectionRect.Height = h;

            Rect selectionBounds = new Rect(x, y, w, h);
            
            foreach (var item in FileListBox.Items)
            {
                var container = FileListBox.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
                if (container != null)
                {
                    Point topLeft = container.TranslatePoint(new Point(0, 0), FileListBox);
                    Rect itemBounds = new Rect(topLeft, new System.Windows.Size(container.ActualWidth, container.ActualHeight));
                    
                    if (selectionBounds.IntersectsWith(itemBounds))
                    {
                        if (!FileListBox.SelectedItems.Contains(item))
                            FileListBox.SelectedItems.Add(item);
                    }
                    else if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                    {
                        if (FileListBox.SelectedItems.Contains(item))
                            FileListBox.SelectedItems.Remove(item);
                    }
                }
            }
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;
        
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is FileItem fi && fi.IsEditing) 
            return;
        var mpos = e.GetPosition(null);
        var diff = _dragStartPoint - mpos;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var selected = FileListBox.SelectedItems.Cast<FileItem>().Select(f => f.FullPath).ToArray();
        
        // Ensure the item being dragged is selected if selection was empty
        if (selected.Length == 0) 
        {
            var current = e.OriginalSource as DependencyObject;
            while (current != null && current != FileListBox)
            {
                if (current is ListBoxItem lbi && lbi.DataContext is FileItem fileItem)
                {
                    selected = new[] { fileItem.FullPath };
                    break;
                }
                current = VisualTreeHelper.GetParent(current);
            }
        }

        if (selected.Length == 0) return;

        _isDragSource = true;
        _currentDragSourceZone = this;
        var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, selected);
        System.Windows.DragDrop.DoDragDrop(FileListBox, data, System.Windows.DragDropEffects.Move | System.Windows.DragDropEffects.Copy);
        _isDragSource = false;
        _currentDragSourceZone = null;
    }

    private void FileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isRubberBanding)
        {
            _isRubberBanding = false;
            SelectionRect.Visibility = Visibility.Collapsed;
            FileListBox.ReleaseMouseCapture();
        }
        else if (_suppressSelectionUp)
        {
            _suppressSelectionUp = false;
            var mpos = e.GetPosition(null);
            var diff = _dragStartPoint - mpos;
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                var current = e.OriginalSource as DependencyObject;
                while (current != null && current != FileListBox)
                {
                    if (current is ListBoxItem lbi && lbi.DataContext is FileItem fi)
                    {
                        FileListBox.SelectedItems.Clear();
                        FileListBox.SelectedItems.Add(fi);
                        break;
                    }
                    current = VisualTreeHelper.GetParent(current);
                }
            }
        }
    }

    // ── Drag IN ──────────────────────────────────────────────────────

    private void OnDragOver(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragEnter(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
            MainBorder.BorderThickness = new Thickness(2);
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object s, DragEventArgs e) => MainBorder.BorderThickness = new Thickness(1);

    private void OnFileDrop(object s, DragEventArgs e)
    {
        MainBorder.BorderThickness = new Thickness(1);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        string target = _currentPath;
        var fe = e.OriginalSource as FrameworkElement;
        
        // If dropping onto a specific folder inside the list, use that folder as target
        if (fe != null && fe.DataContext is FileItem fi && fi.IsDirectory)
        {
            target = fi.FullPath;
        }
        else if (_currentDragSourceZone == this)
        {
            // Block self-drop onto the background of the same zone
            return;
        }

        if (string.IsNullOrEmpty(target)) return;
        Directory.CreateDirectory(target);

        // Track interaction for dormancy
        _config.LastInteractedAt = DateTime.Now;

        var undoRecords = new List<FocusFence.Services.UndoRecord>();
        foreach (string src in (string[])e.Data.GetData(DataFormats.FileDrop)!)
        {
            try
            {
                string name = System.IO.Path.GetFileName(src);
                string dest = System.IO.Path.Combine(target, name);
                
                if (src.Equals(dest, StringComparison.OrdinalIgnoreCase)) continue;

                int c = 2;
                string bn = System.IO.Path.GetFileNameWithoutExtension(name);
                string ext = System.IO.Path.GetExtension(name);
                while (File.Exists(dest) || Directory.Exists(dest))
                    dest = System.IO.Path.Combine(target, $"{bn} ({c++}){ext}");

                bool isCopy = (e.AllowedEffects & DragDropEffects.Move) == 0 || (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
                
                if (File.Exists(src)) 
                {
                    if (isCopy) File.Copy(src, dest);
                    else File.Move(src, dest);
                }
                else if (Directory.Exists(src)) 
                {
                    if (isCopy) FileSystem.CopyDirectory(src, dest);
                    else Directory.Move(src, dest);
                }
                
                undoRecords.Add(new FocusFence.Services.UndoRecord { OriginalSource = src, NewDestination = dest });
            }
            catch (Exception ex) { FocusFenceDialog.ShowMessage($"無法移動：{ex.Message}", "FocusFence", true); }
        }
        FocusFence.Services.UndoService.RecordMove(undoRecords);
        RefreshFiles();
    }

    // ── Context Menu ─────────────────────────────────────────────────

    private void FileList_PreviewRightClick(object s, MouseButtonEventArgs e)
    {
        // Auto-select the right-clicked item so context menu operations apply to it
        var current = e.OriginalSource as DependencyObject;
        while (current != null && current != FileListBox)
        {
            if (current is ListBoxItem lbi && lbi.DataContext is FileItem fi)
            {
                if (!FileListBox.SelectedItems.Contains(fi))
                {
                    FileListBox.SelectedItems.Clear();
                    FileListBox.SelectedItem = fi;
                }
                break;
            }
            current = VisualTreeHelper.GetParent(current);
        }
    }

    private void Context_Open(object s, RoutedEventArgs e)
    {
        _config.LastInteractedAt = DateTime.Now;
        var items = FileListBox.SelectedItems.Cast<FileItem>().ToList();
        foreach (FileItem f in items)
        {
            if (f.IsDirectory)
            {
                // Navigate into the folder within the zone instead of opening Explorer
                NavigateIntoFolder(f.FullPath);
                return; // Only navigate into the first selected folder
            }
            else
            {
                try { Process.Start(new ProcessStartInfo { FileName = f.FullPath, UseShellExecute = true }); } catch { }
            }
        }
    }

    private void Context_CopyPath(object s, RoutedEventArgs e)
    {
        var paths = FileListBox.SelectedItems.Cast<FileItem>().Select(f => f.FullPath);
        System.Windows.Clipboard.SetText(string.Join("\n", paths));
    }

    private void Context_ShowInExplorer(object s, RoutedEventArgs e)
    {
        if (FileListBox.SelectedItem is FileItem f)
            Process.Start("explorer.exe", $"/select,\"{f.FullPath}\"");
    }

    private void Context_Delete(object s, RoutedEventArgs e)
    {
        var items = FileListBox.SelectedItems.Cast<FileItem>().ToList();
        if (items.Count == 0) return;
        if (!FocusFenceDialog.ShowConfirm($"確定要將 {items.Count} 個項目移至回收筒？", "FocusFence", destructive: true)) return;
        foreach (var f in items)
        {
            try
            {
                if (f.IsDirectory)
                    FileSystem.DeleteDirectory(f.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else
                    FileSystem.DeleteFile(f.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            catch { }
        }
        RefreshFiles();
    }

    private void Context_NewFolder(object s, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_currentPath)) return;
            string newPath = System.IO.Path.Combine(_currentPath, "新增資料夾");
            int c = 2;
            while (Directory.Exists(newPath))
                newPath = System.IO.Path.Combine(_currentPath, $"新增資料夾 ({c++})");
            Directory.CreateDirectory(newPath);
            RefreshFiles();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var newFolderItem = _files.FirstOrDefault(f => f.FullPath == newPath);
                if (newFolderItem != null)
                {
                    FileListBox.SelectedItem = newFolderItem;
                    FileListBox.ScrollIntoView(newFolderItem);
                    newFolderItem.IsEditing = true;
                }
            }), DispatcherPriority.Loaded);
        }
        catch (Exception ex) { FocusFenceDialog.ShowMessage(ex.Message, "FocusFence", true); }
    }

    private void Context_NewTextFile(object s, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_currentPath)) return;
            string newPath = System.IO.Path.Combine(_currentPath, "新增文字檔.txt");
            int c = 2;
            while (File.Exists(newPath))
                newPath = System.IO.Path.Combine(_currentPath, $"新增文字檔 ({c++}).txt");
            File.WriteAllText(newPath, "");
            RefreshFiles();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var newFileItem = _files.FirstOrDefault(f => f.FullPath == newPath);
                if (newFileItem != null)
                {
                    FileListBox.SelectedItem = newFileItem;
                    FileListBox.ScrollIntoView(newFileItem);
                    newFileItem.IsEditing = true;
                }
            }), DispatcherPriority.Loaded);
        }
        catch (Exception ex) { FocusFenceDialog.ShowMessage(ex.Message, "FocusFence", true); }
    }

    // ── Video Consolidation ─────────────────────────────────────────────

    private static readonly HashSet<string> VideoExtensions =
        [".mp4", ".mov", ".avi", ".mkv", ".webm", ".ts", ".flv", ".wmv", ".m4v", ".mpg", ".mpeg"];

    private async void Context_ConsolidateVideos(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentPath) || !Directory.Exists(_currentPath))
        {
            FocusFenceDialog.ShowMessage("目前沒有有效的資料夾路徑。");
            return;
        }

        // Recursively find all video files in subdirectories (NOT the current directory itself)
        var subDirs = Directory.GetDirectories(_currentPath, "*", System.IO.SearchOption.AllDirectories);
        var videoFiles = new List<string>();
        foreach (var dir in subDirs)
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (VideoExtensions.Contains(ext))
                    videoFiles.Add(file);
            }
        }

        if (videoFiles.Count == 0)
        {
            FocusFenceDialog.ShowMessage("子資料夾中沒有找到任何影片檔案。");
            return;
        }

        if (!FocusFenceDialog.ShowConfirm(
            $"在子資料夾中找到 {videoFiles.Count} 個影片檔案。\n確定要全部移動到「{System.IO.Path.GetFileName(_currentPath)}」嗎？",
            "FocusFence — 整理影片")) return;

        int moved = 0;
        int failed = 0;
        var errors = new List<string>();

        await Task.Run(() =>
        {
            foreach (var srcPath in videoFiles)
            {
                try
                {
                    string fileName = System.IO.Path.GetFileName(srcPath);
                    string destPath = System.IO.Path.Combine(_currentPath, fileName);

                    // Handle name collisions
                    int c = 2;
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(fileName);
                    string ext = System.IO.Path.GetExtension(fileName);
                    while (File.Exists(destPath))
                        destPath = System.IO.Path.Combine(_currentPath, $"{baseName} ({c++}){ext}");

                    File.Move(srcPath, destPath);
                    moved++;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"{System.IO.Path.GetFileName(srcPath)}: {ex.Message}");
                }
            }
        });

        RefreshFiles();

        string resultMsg = $"已移動 {moved} 個影片到此資料夾。";
        if (failed > 0)
            resultMsg += $"\n失敗 {failed} 個：\n" + string.Join("\n", errors.Take(5));
        FocusFenceDialog.ShowMessage(resultMsg, "FocusFence — 整理完成", failed > 0);
    }

    // ── File Extraction (Async) ──────────────────────────────────────

    private static readonly HashSet<string> ArchiveExtensions = 
        [".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".tar.gz", ".bz2", ".xz"];

    private bool IsArchiveFile(FileItem file)
    {
        if (file.IsDirectory) return false;
        string ext = System.IO.Path.GetExtension(file.FullPath).ToLowerInvariant();
        return ArchiveExtensions.Contains(ext);
    }

    private async void Context_Extract(object s, RoutedEventArgs e)
    {
        var items = FileListBox.SelectedItems.Cast<FileItem>().Where(IsArchiveFile).ToList();
        if (items.Count == 0)
        {
            FocusFenceDialog.ShowMessage("請選擇壓縮檔案（.zip, .rar, .7z 等）");
            return;
        }

        // Pre-resolve tool paths on the UI thread
        string? externalToolPath = null;
        bool needsExternalTool = items.Any(f =>
        {
            string ext = System.IO.Path.GetExtension(f.FullPath).ToLowerInvariant();
            return ext is ".7z" or ".rar";
        });
        if (needsExternalTool)
        {
            externalToolPath = Find7zOrWinRAR();
        }

        // Prepare extraction jobs (capture paths before going async)
        var jobs = new List<(string FilePath, string FileName, string Ext, string ExtractDir)>();
        foreach (var file in items)
        {
            string ext = System.IO.Path.GetExtension(file.FullPath).ToLowerInvariant();
            string baseName = System.IO.Path.GetFileNameWithoutExtension(file.FullPath);

            // Handle .tar.gz / .tgz naming
            if (ext == ".gz" && file.FullPath.ToLowerInvariant().EndsWith(".tar.gz"))
                baseName = System.IO.Path.GetFileNameWithoutExtension(baseName);

            string extractDir = System.IO.Path.Combine(_currentPath, baseName);
            int counter = 2;
            while (Directory.Exists(extractDir))
                extractDir = System.IO.Path.Combine(_currentPath, $"{baseName} ({counter++})");

            jobs.Add((file.FullPath, file.FileName, ext, extractDir));
        }

        var errors = new List<string>();

        // Run all extraction on a background thread
        await Task.Run(() =>
        {
            foreach (var (filePath, fileName, ext, extractDir) in jobs)
            {
                try
                {
                    if (ext == ".zip")
                    {
                        ZipFile.ExtractToDirectory(filePath, extractDir);
                    }
                    else if (ext is ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz")
                    {
                        Directory.CreateDirectory(extractDir);
                        var psi = new ProcessStartInfo
                        {
                            FileName = "tar",
                            Arguments = $"xf \"{filePath}\" -C \"{extractDir}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardError = true
                        };
                        var proc = Process.Start(psi);
                        if (proc != null)
                        {
                            string stderr = proc.StandardError.ReadToEnd();
                            proc.WaitForExit();
                            if (proc.ExitCode != 0)
                                throw new Exception($"tar 解壓失敗: {stderr}");
                        }
                    }
                    else if (ext is ".7z" or ".rar")
                    {
                        if (externalToolPath == null)
                        {
                            errors.Add($"「{fileName}」: 無法解壓 {ext} 檔案，請安裝 7-Zip 或 WinRAR。");
                            continue;
                        }

                        Directory.CreateDirectory(extractDir);
                        string args;
                        if (externalToolPath.Contains("7z", StringComparison.OrdinalIgnoreCase))
                            args = $"x \"{filePath}\" -o\"{extractDir}\" -y";
                        else // WinRAR
                            args = $"x -y \"{filePath}\" \"{extractDir}\\\"";

                        var psi = new ProcessStartInfo
                        {
                            FileName = externalToolPath,
                            Arguments = args,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardError = true
                        };
                        var proc = Process.Start(psi);
                        if (proc != null)
                        {
                            proc.WaitForExit();
                            if (proc.ExitCode != 0)
                                throw new Exception($"解壓失敗 (exit code {proc.ExitCode})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"「{fileName}」: {ex.Message}");
                }
            }
        });

        RefreshFiles();

        if (errors.Count > 0)
        {
            FocusFenceDialog.ShowMessage(
                "部分檔案解壓縮失敗：\n" + string.Join("\n", errors),
                "FocusFence", true);
        }
    }

    /// <summary>
    /// Searches for 7z.exe or WinRAR (UnRAR.exe) on the system.
    /// </summary>
    private static string? Find7zOrWinRAR()
    {
        // Common installation paths for 7-Zip
        string[] sevenZipPaths =
        [
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe"
        ];

        foreach (var p in sevenZipPaths)
            if (File.Exists(p)) return p;

        // Common installation paths for WinRAR
        string[] winrarPaths =
        [
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinRAR", "UnRAR.exe"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinRAR", "UnRAR.exe"),
            @"C:\Program Files\WinRAR\UnRAR.exe",
            @"C:\Program Files (x86)\WinRAR\UnRAR.exe"
        ];

        foreach (var p in winrarPaths)
            if (File.Exists(p)) return p;

        return null;
    }

    // ── Renaming ──────────────────────────────────────────────────────

    private void Context_Rename(object s, RoutedEventArgs e)
    {
        StartRename();
    }

    private void Context_BatchRename(object s, RoutedEventArgs e)
    {
        // Get target items: selected non-directory files, or all files if none selected
        var selected = FileListBox.SelectedItems.Cast<FileItem>()
            .Where(f => !f.IsDirectory).ToList();
        
        var targets = selected.Count > 0 
            ? selected 
            : _files.Where(f => !f.IsDirectory).ToList();

        if (targets.Count == 0)
        {
            FocusFenceDialog.ShowMessage("沒有可以重新命名的檔案。");
            return;
        }

        string defaultBase = System.IO.Path.GetFileNameWithoutExtension(
            targets.FirstOrDefault()?.FullPath ?? "file");

        var baseName = FocusFenceDialog.ShowInput(
            $"將 {targets.Count} 個檔案批次重新命名。\n格式: 名稱_001, 名稱_002, ...\n\n請輸入基本名稱：",
            "FocusFence — 批次重新命名",
            defaultBase);

        if (string.IsNullOrWhiteSpace(baseName)) return;

        int renamed = 0;
        int failed = 0;
        var errors = new List<string>();

        // Sort by current name for consistent ordering
        var sorted = targets.OrderBy(f => f.FileName).ToList();
        int pad = sorted.Count.ToString().Length;
        if (pad < 3) pad = 3; // Minimum 3 digits

        for (int i = 0; i < sorted.Count; i++)
        {
            var file = sorted[i];
            try
            {
                string ext = System.IO.Path.GetExtension(file.FullPath);
                string newName = $"{baseName}_{(i + 1).ToString().PadLeft(pad, '0')}{ext}";
                string newPath = System.IO.Path.Combine(_currentPath, newName);

                // Skip if same name
                if (newPath.Equals(file.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    renamed++;
                    continue;
                }

                // Handle collision with a temp name first
                if (File.Exists(newPath))
                {
                    string tempPath = newPath + ".tmp_rename";
                    File.Move(file.FullPath, tempPath);
                    file.FullPath = tempPath;
                }

                File.Move(file.FullPath, newPath);
                file.FullPath = newPath;
                file.FileName = newName;
                file.DisplayName = newName.Length > 16 ? newName[..13] + "..." : newName;
                renamed++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{file.FileName}: {ex.Message}");
            }
        }

        RefreshFiles();

        if (failed > 0)
            FocusFenceDialog.ShowMessage(
                $"已重新命名 {renamed} 個，失敗 {failed} 個：\n" + string.Join("\n", errors.Take(5)),
                "FocusFence", true);
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FileListBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            StartRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            Context_Delete(null!, null!);
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (FocusFence.Services.UndoService.CanUndo)
            {
                FocusFence.Services.UndoService.Undo();
            }
            e.Handled = true;
        }
    }

    private void StartRename()
    {
        if (FileListBox.SelectedItem is FileItem file)
        {
            file.IsEditing = true;
        }
    }

    private void RenameTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb && tb.Visibility == Visibility.Visible)
        {
            Dispatcher.BeginInvoke(() =>
            {
                tb.Focus();
                string txt = tb.Text;
                if (!string.IsNullOrEmpty(txt) && tb.DataContext is FileItem fi && !fi.IsDirectory)
                {
                    int extIdx = txt.LastIndexOf('.');
                    if (extIdx > 0)
                        tb.Select(0, extIdx);
                    else
                        tb.SelectAll();
                }
                else
                {
                    tb.SelectAll();
                }
                System.Windows.Input.Keyboard.Focus(tb);
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void RenameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename(sender as System.Windows.Controls.TextBox);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRename(sender as System.Windows.Controls.TextBox);
            e.Handled = true;
        }
    }

    private void RenameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitRename(sender as System.Windows.Controls.TextBox);
    }

    private void CommitRename(System.Windows.Controls.TextBox? tb)
    {
        if (tb == null || tb.DataContext is not FileItem file || !file.IsEditing) return;

        string newName = tb.Text.Trim();
        string newPath = System.IO.Path.Combine(_currentPath, newName);

        if (!string.IsNullOrEmpty(newName) && newPath != file.FullPath)
        {
            try
            {
                if (file.IsDirectory) Directory.Move(file.FullPath, newPath);
                else File.Move(file.FullPath, newPath);
                
                file.FullPath = newPath;
                file.DisplayName = newName.Length > 16 ? newName[..13] + "..." : newName;
            }
            catch (Exception ex)
            {
                FocusFenceDialog.ShowMessage($"重新命名失敗: {ex.Message}", "FocusFence", true);
                file.FileName = System.IO.Path.GetFileName(file.FullPath); // revert text
            }
        }
        else
        {
            file.FileName = System.IO.Path.GetFileName(file.FullPath); // revert if empty or same
        }

        file.IsEditing = false;
    }

    private void CancelRename(System.Windows.Controls.TextBox? tb)
    {
        if (tb == null || tb.DataContext is not FileItem file || !file.IsEditing) return;
        file.FileName = System.IO.Path.GetFileName(file.FullPath); // revert
        file.IsEditing = false;
    }

    // ── Drag & Snap ───────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) { DragMove(); SnapshotPosition(); ZoneChanged?.Invoke(); }
    }

    private void TitleBar_RightClick(object s, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();

        var launchItem = new MenuItem { Header = "🚀 啟動情境 (Launch Context)" };
        launchItem.Click += (_, _) => ContextLaunchRequested?.Invoke(_config.Id);
        menu.Items.Add(launchItem);


        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;

        double snapRadius = 20.0;
        double screenW = SystemParameters.WorkArea.Width;
        double screenH = SystemParameters.WorkArea.Height;

        // Snap to Screen Edges
        if (Math.Abs(Left) < snapRadius) Left = 0;
        if (Math.Abs(Left + Width - screenW) < snapRadius) Left = screenW - Width;
        
        if (Math.Abs(Top) < snapRadius) Top = 0;
        if (Math.Abs(Top + Height - screenH) < snapRadius) Top = screenH - Height;
    }

    // ── Resize ───────────────────────────────────────────────────────

    private void ResizeGrip_MouseDown(object s, MouseButtonEventArgs e)
    {
        _isResizing = true;
        _resizeStart = PointToScreen(e.GetPosition(this));
        _resizeStartW = Width; _resizeStartH = Height;
        ((UIElement)s).CaptureMouse();
    }

    private void ResizeGrip_MouseMove(object s, MouseEventArgs e)
    {
        if (!_isResizing) return;
        var c = PointToScreen(e.GetPosition(this));
        Width = Math.Max(220, _resizeStartW + (c.X - _resizeStart.X));
        Height = Math.Max(120, _resizeStartH + (c.Y - _resizeStart.Y));
    }

    private void ResizeGrip_MouseUp(object s, MouseButtonEventArgs e)
    {
        if (!_isResizing) return;
        _isResizing = false;
        ((UIElement)s).ReleaseMouseCapture();
        _expandedHeight = Height;
        SnapshotPosition();
        ZoneChanged?.Invoke();
    }

    // ── Persistence ──────────────────────────────────────────────────

    public void SnapshotPosition()
    {
        _config.X = Left; _config.Y = Top;
        _config.Width = Width;
        if (!_config.IsCollapsed) _config.Height = Height;
    }

    /// <summary>
    /// Animates the zone out (used by Zen Mode)
    /// </summary>
    public void AnimateOut()
    {
        // PART 4C: Zone disappear — Opacity + Scale → 0.97 with Curve B
        var fadeOut = AnimationHelper.FadeOutEaseOut();
        fadeOut.Completed += (_, _) => Visibility = Visibility.Hidden;
        MainBorder.BeginAnimation(OpacityProperty, fadeOut);
        ZoneScale.BeginAnimation(ScaleTransform.ScaleXProperty, AnimationHelper.ScaleOutEaseOut());
        ZoneScale.BeginAnimation(ScaleTransform.ScaleYProperty, AnimationHelper.ScaleOutEaseOut());
    }

    /// <summary>
    /// Animates the zone in (used by Zen Mode un-hide)
    /// PART 4C: Zone appear — Opacity + Scale 0.96→1 + TranslateY +6→0 with Spring
    /// </summary>
    public void AnimateIn()
    {
        Visibility = Visibility.Visible;
        PlayAppearAnimation();
    }

    protected override void OnClosed(EventArgs e) 
    { 
        StopWatcher(); 
        FocusFence.Services.UndoService.UndoExecuted -= UndoService_UndoExecuted;
        base.OnClosed(e); 
    }
}

/// <summary>
/// ViewModel wrapper for TaskCard to support data binding.
/// </summary>
public class TaskCardViewModel : INotifyPropertyChanged
{
    public TaskCard Card { get; }
    private readonly Action _onChanged;

    public TaskCardViewModel(TaskCard card, Action onChanged)
    {
        Card = card;
        _onChanged = onChanged;
    }

    public string Text
    {
        get => Card.Text;
        set { Card.Text = value; Notify(nameof(Text)); _onChanged(); }
    }

    public bool IsCompleted
    {
        get => Card.IsCompleted;
        set
        {
            Card.IsCompleted = value;
            Notify(nameof(IsCompleted));
            Notify(nameof(TextColor));
            _onChanged();
        }
    }

    /// <summary>
    /// Completed = strikethrough gray, Overdue = red, Normal = white.
    /// </summary>
    public SolidColorBrush TextColor
    {
        get
        {
            if (IsCompleted)
                return new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            if (Card.Deadline.HasValue && Card.Deadline.Value < DateTime.Now)
                return new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)); // #EF4444
            return new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
