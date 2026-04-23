using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FocusFence.Models;

namespace FocusFence.Windows;

public partial class DashboardWindow : Window
{
    private readonly AppConfig _config;
    private readonly ObservableCollection<DashboardZoneItem> _zoneItems = [];

    public event Action? ConfigurationChanged;
    public event Action<ZoneConfig>? RequestCreateZone;
    public event Action<ZoneConfig>? RequestDeleteZone;
    public event Action<ZoneConfig, bool>? RequestToggleZone;
    public event Action<ZoneConfig>? RequestSummonZone;
    public event Action? RequestAutoArrange;
    public event Action<string, string, int>? RequestStartPomodoro;

    public DashboardWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        ZoneListBox.ItemsSource = _zoneItems;
        GlobalNotesList.ItemsSource = _config.Zones;
        Loaded += OnLoaded;
        InitDashboardColors();
    }

    private static readonly string[] Palette =
        ["#E8A87C","#5B8DEF","#41D68B","#EF5350","#AB6FE8",
         "#FBC02D","#26C6DA","#F06292","#78909C","#66BB6A",
         "#FF7043","#29B6F6"];

    private void InitDashboardColors()
    {
        foreach (var hex in Palette)
        {
            var el = new Ellipse
            {
                Width = 20, Height = 20, Margin = new Thickness(4),
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                Cursor = Cursors.Hand
            };
            string c = hex;
            el.MouseLeftButtonDown += (_, ev) => 
            { 
                if (_currentEditingZone != null)
                {
                    _currentEditingZone.Config.AccentColor = c;
                    _currentEditingZone.Notify("AccentColorBrush"); // force binding update
                    ConfigurationChanged?.Invoke();
                }
                DashColorPopup.IsOpen = false;
                ev.Handled = true; 
            };
            DashColorPalette.Children.Add(el);
        }
    }

    private DashboardZoneItem? _currentEditingZone;

    private void DashColorDot_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse el && el.Tag is DashboardZoneItem item)
        {
            _currentEditingZone = item;
            DashColorPopup.PlacementTarget = el;
            DashColorPopup.IsOpen = true;
            e.Handled = true;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        if (DashboardTitleText != null)
        {
            DashboardTitleText.Text = $"📊 FocusFence 控制台 (v{version})";
        }
        RefreshData();
    }

    public void RefreshData()
    {
        // Refresh Zones
        _zoneItems.Clear();

        foreach (var zc in _config.Zones)
        {
            int count = 0;
            if (!string.IsNullOrEmpty(zc.FolderPath) && Directory.Exists(zc.FolderPath))
                count = Directory.GetFileSystemEntries(zc.FolderPath).Length;

            _zoneItems.Add(new DashboardZoneItem(zc, count, () => ConfigurationChanged?.Invoke()));
        }

        // Refresh Pomodoro stats & combobox
        RefreshPomodoroData();

        // Refresh Global Notes
        GlobalNotesList.ItemsSource = null;
        GlobalNotesList.ItemsSource = _config.Zones.Where(z => z.ShowMemo).ToList();
    }

    private void RefreshPomodoroData()
    {

        // Today's tomatoes (kept variable if needed for logic)
        var today = DateTime.Now.Date;
        var todaysTomatoes = _config.PomodoroHistory.Where(x => x.IsCompleted && x.CompletedAt.HasValue && x.CompletedAt.Value.Date == today).Sum(x => x.DurationMinutes) / 25; // 25 min = 1 tomato

        // Week's stamps (Assuming week starts on Monday or just tracking last 7 days total)
        var weekStart = today.AddDays(-6);
        int weeklyTomatoes = _config.PomodoroHistory.Where(x => x.IsCompleted && x.CompletedAt.HasValue && x.CompletedAt.Value.Date >= weekStart).Sum(x => x.DurationMinutes) / 25;
        
        WeekTomatoCountText.Text = $"{weeklyTomatoes} / 21";
        WeekTomatoProgressBar.Value = Math.Min(weeklyTomatoes, 21);

        // History list (last 10)
        var recent = _config.PomodoroHistory.Where(x => x.IsCompleted && x.CompletedAt.HasValue).OrderByDescending(x => x.CompletedAt ?? DateTime.MinValue).Take(10).ToList();
        PomoHistoryList.ItemsSource = recent;
    }

    // ── Toggle Switch ──────────────────────────────────────────────

    private void ToggleSwitch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is DashboardZoneItem item)
        {
            item.IsVisible = !item.IsVisible;
            RequestToggleZone?.Invoke(item.Config, item.Config.IsVisible);
            ConfigurationChanged?.Invoke();
        }
        e.Handled = true;
    }

    // ── Title TextBox Focus (ensure IME works) ────────────────────

    private void TitleTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // Ensure the TextBox captures keyboard properly within the ListBox
        if (sender is System.Windows.Controls.TextBox tb)
        {
            tb.SelectAll();
            System.Windows.Input.Keyboard.Focus(tb);
        }
    }

    private void TitleTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ConfigurationChanged?.Invoke();
    }

    private void TitleTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            System.Windows.Input.Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    // ── Drag Reorder ──────────────────────────────────────────────

    private Point _dragStartPoint;
    private DashboardZoneItem? _dragItem;
    private bool _isDragging;
    private const double DragThreshold = 8;

    private void ZoneList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(ZoneListBox);
        _isDragging = false;

        // Find the DashboardZoneItem from the clicked element
        var element = e.OriginalSource as DependencyObject;
        // Don't start drag on TextBox (let user edit title)
        if (element is System.Windows.Controls.TextBox) { _dragItem = null; return; }
        // Don't start drag on toggle switch area
        while (element != null)
        {
            if (element is Border b && b.Tag is DashboardZoneItem item)
            {
                // This is the toggle — skip
                _dragItem = null; return;
            }
            if (element is FrameworkElement fe && fe.DataContext is DashboardZoneItem foundItem)
            {
                _dragItem = foundItem;
                return;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        _dragItem = null;
    }

    private void ZoneList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(ZoneListBox);
        var diff = pos - _dragStartPoint;

        if (Math.Abs(diff.Y) > DragThreshold && !_isDragging)
        {
            _isDragging = true;
            var data = new System.Windows.DataObject("DashboardZoneItem", _dragItem);
            DragDrop.DoDragDrop(ZoneListBox, data, DragDropEffects.Move);
            _isDragging = false;
            _dragItem = null;
            DragIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void ZoneList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        _isDragging = false;
        DragIndicator.Visibility = Visibility.Collapsed;
    }

    private void ZoneList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("DashboardZoneItem")) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;

        // Show drop indicator
        var pos = e.GetPosition(ZoneListBox);
        DragIndicator.Visibility = Visibility.Visible;
        DragIndicator.Margin = new Thickness(20, pos.Y, 20, 0);
        e.Handled = true;
    }

    private void ZoneList_Drop(object sender, DragEventArgs e)
    {
        DragIndicator.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent("DashboardZoneItem")) return;

        var draggedItem = e.Data.GetData("DashboardZoneItem") as DashboardZoneItem;
        if (draggedItem == null) return;

        // Find drop target index
        var pos = e.GetPosition(ZoneListBox);
        int targetIndex = _config.Zones.Count - 1; // default: end

        // Walk visible items to find drop position
        for (int i = 0; i < _zoneItems.Count; i++)
        {
            var container = ZoneListBox.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
            if (container == null) continue;

            var itemPos = container.TransformToAncestor(ZoneListBox).Transform(new Point(0, 0));
            double itemMid = itemPos.Y + container.ActualHeight / 2;

            if (pos.Y < itemMid)
            {
                targetIndex = _config.Zones.IndexOf(_zoneItems[i].Config);
                break;
            }
        }

        int sourceIndex = _config.Zones.IndexOf(draggedItem.Config);
        if (sourceIndex < 0 || sourceIndex == targetIndex) return;

        _config.Zones.RemoveAt(sourceIndex);
        if (targetIndex > sourceIndex) targetIndex--;
        if (targetIndex < 0) targetIndex = 0;
        if (targetIndex >= _config.Zones.Count) _config.Zones.Add(draggedItem.Config);
        else _config.Zones.Insert(targetIndex, draggedItem.Config);

        RefreshData();
        ConfigurationChanged?.Invoke();
    }

    // ── Double-click to Summon ────────────────────────────────────

    private void ZoneRow_DoubleClickSummon(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is Border border && border.DataContext is DashboardZoneItem item)
        {
            SummonItem(item);
            e.Handled = true;
        }
    }

    // ── Context Menu Handlers ─────────────────────────────────────

    private void ContextSummon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is DashboardZoneItem item) SummonItem(item);
    }

    private void ContextOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is DashboardZoneItem item)
        {
            if (!string.IsNullOrEmpty(item.Config.FolderPath) && Directory.Exists(item.Config.FolderPath))
            {
                Process.Start(new ProcessStartInfo { FileName = item.Config.FolderPath, UseShellExecute = true });
            }
        }
    }

    private void ContextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is DashboardZoneItem item) DeleteItem(item);
    }

    private void SummonItem(DashboardZoneItem item)
    {
        if (!item.IsVisible)
        {
            item.IsVisible = true;
            RequestToggleZone?.Invoke(item.Config, true);
            ConfigurationChanged?.Invoke();
        }
        RequestSummonZone?.Invoke(item.Config);
    }

    // ── Delete ────────────────────────────────────────────────────

    private void RowDeleteX_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is DashboardZoneItem item) DeleteItem(item);
        e.Handled = true;
    }

    private void DeleteItem(DashboardZoneItem item)
    {
        var r = MessageBox.Show($"確定要刪除「{item.Config.Title}」?\n資料夾與內部檔案會保留在桌面上不被刪除。", 
            "刪除確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            
        if (r == MessageBoxResult.Yes)
        {
            RequestDeleteZone?.Invoke(item.Config);
            RefreshData();
            ConfigurationChanged?.Invoke();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void NewZone_Click(object sender, RoutedEventArgs e)
    {
        string baseDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FocusFence", "Zones");
        string newZoneName = "點我可以改名的框框 " + (_config.Zones.Count + 1);
        string newPath = System.IO.Path.Combine(baseDir, "FocusFence_" + Guid.NewGuid().ToString("N")[..8]);
        
        string newColor = Palette[_config.Zones.Count % Palette.Length];

        var newConfig = new ZoneConfig
        {
            Title = newZoneName,
            X = 100,
            Y = 100,
            Width = 320,
            Height = 280,
            FolderPath = newPath,
            AccentColor = newColor
        };

        RequestCreateZone?.Invoke(newConfig);
        RefreshData();
        ConfigurationChanged?.Invoke();
    }

    private void AutoArrange_Click(object sender, RoutedEventArgs e)
    {
        RequestAutoArrange?.Invoke();
    }

    // ── Tabs ────────────────────────────────────────────────────────

    private void ResetTabs()
    {
        TabZones.Background = Brushes.Transparent;
        TabZonesText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#AAFFFFFF"));
        
        TabPomodoro.Background = Brushes.Transparent;
        TabPomodoroText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#AAFFFFFF"));

        TabNotes.Background = Brushes.Transparent;
        TabNotesText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#AAFFFFFF"));

        ZonesContainer.Visibility = Visibility.Collapsed;
        PomodoroContainer.Visibility = Visibility.Collapsed;
        NotesContainer.Visibility = Visibility.Collapsed;
    }

    private void TabZones_Click(object sender, MouseButtonEventArgs e)
    {
        ResetTabs();
        TabZones.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#33C69C6D"));
        TabZonesText.Foreground = Brushes.White;
        ZonesContainer.Visibility = Visibility.Visible;
    }


    private void TabPomodoro_Click(object sender, MouseButtonEventArgs e)
    {
        ResetTabs();
        TabPomodoro.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#33C69C6D"));
        TabPomodoroText.Foreground = Brushes.White;
        PomodoroContainer.Visibility = Visibility.Visible;
    }

    private void TabNotes_Click(object sender, MouseButtonEventArgs e)
    {
        ResetTabs();
        TabNotes.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#33C69C6D"));
        TabNotesText.Foreground = Brushes.White;
        NotesContainer.Visibility = Visibility.Visible;
    }

    public void ShowPomodoroTab(string? zoneTitle = null)
    {
        TabPomodoro_Click(this, null!);
        PomoLabelInput.Focus();
    }

    private void StartPomodoro_Click(object sender, RoutedEventArgs e)
    {
        string label = PomoLabelInput.Text.Trim();
        if (string.IsNullOrEmpty(label)) label = "專注時間";
        
        int duration = (int)PomoDurationSlider.Value;
        
        RequestStartPomodoro?.Invoke("", label, duration);
        
        PomoLabelInput.Text = ""; // clear for next time
    }



    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    // ── Pause/Resume Button ───────────────────────────────────────
    private void DashPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Implement real pause/resume logic via PomodoroService.
        // Placeholder: toggle button icon and show message.
        if (DashPauseIcon != null)
        {
            if (DashPauseIcon.Text == "⏸")
            {
                // Currently paused, resume
                DashPauseIcon.Text = "▶"; // change to play icon (optional)
                // Insert resume logic here
                MessageBox.Show("Pomodoro resumed.");
            }
            else
            {
                // Currently running, pause
                DashPauseIcon.Text = "⏸"; // keep pause icon
                // Insert pause logic here
                MessageBox.Show("Pomodoro paused.");
            }
        }
    }

    // ── Stop Button ───────────────────────────────────────────────
    private void DashStopBtn_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Implement stop logic via PomodoroService.
        // Placeholder: stop the current Pomodoro session and reset UI.
        MessageBox.Show("Pomodoro stopped.");
        // Reset UI elements (e.g., timer text, progress bar) if needed.
    }

    // ── Clear History Button ───────────────────────────────────────
    private void ClearHistoryBtn_Click(object sender, RoutedEventArgs e)
    {
        // Clear pomodoro history and refresh UI.
        _config.PomodoroHistory.Clear();
        RefreshPomodoroData();
        MessageBox.Show("Pomodoro history cleared.");
    }

    // ── Global Notes ──────────────────────────────────────────────────

    private void GlobalNoteText_Changed(object sender, TextChangedEventArgs e)
    {
        ConfigurationChanged?.Invoke();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true; // Always hide, never truly close until app exit
        Hide();
    }
}

public class DashboardZoneItem : INotifyPropertyChanged
{
    public ZoneConfig Config { get; }
    public int FileCountValue { get; }

    public Action NotifyConfigChange { get; }
    
    public DashboardZoneItem(ZoneConfig config, int count, Action notifyChange)
    {
        Config = config;
        FileCountValue = count;
        NotifyConfigChange = notifyChange;
    }

    public bool IsVisible 
    { 
        get => Config.IsVisible; 
        set 
        { 
            Config.IsVisible = value; 
            Notify(nameof(IsVisible)); 
            Notify(nameof(ToggleBackground)); 
            Notify(nameof(ToggleKnobMargin)); 
        }
    }
    
    public string Title 
    { 
        get => Config.Title; 
        set { Config.Title = value; Notify(nameof(Title)); }
    }

    public string AccentColor => Config.AccentColor;
    public System.Windows.Media.Brush AccentColorBrush => Config.AccentColorBrush;
    public string FileCount => $"{FileCountValue} 個項目";

    // ── Toggle Switch visual properties ──────────────────────────
    public SolidColorBrush ToggleBackground => IsVisible 
        ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C69C6D"))
        : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#44FFFFFF"));

    public Thickness ToggleKnobMargin => IsVisible 
        ? new Thickness(20, 0, 2, 0)  // Right side (ON)
        : new Thickness(2, 0, 0, 0);  // Left side (OFF)

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}



