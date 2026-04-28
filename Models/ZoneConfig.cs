namespace FocusFence.Models;

public class ZoneConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New Zone";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 280;
    public string? FolderPath { get; set; }
    private string _accentColor = "#C69C6D";
    private System.Windows.Media.Brush? _accentBrush;

    public string AccentColor 
    { 
        get => _accentColor; 
        set { _accentColor = value; _accentBrush = null; } // Invalidate cache
    }
    
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Windows.Media.Brush AccentColorBrush 
    {
        get 
        {
            if (_accentBrush != null) return _accentBrush;
            try { _accentBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(AccentColor)!; }
            catch { _accentBrush = System.Windows.Media.Brushes.Gray; }
            return _accentBrush;
        }
    }

    public bool IsCollapsed { get; set; }
    public bool IsVisible { get; set; } = true;
    public string Group { get; set; } = "";
    public bool IsPinned { get; set; } = false;
    public double Opacity { get; set; } = 0.90;
    public bool HoverExpand { get; set; } = true;
    public int DesktopPage { get; set; } = 0;
    public double IconSize { get; set; } = 48;
    public bool ShowMemo { get; set; } = false;

    // ── v3.0 additions ──────────────────────────────────────────────
    /// <summary>Task cards stored inside this Zone.</summary>
    public List<TaskCard> TaskCards { get; set; } = [];

    /// <summary>Context launcher configuration for one-click workflow switching.</summary>
    public ContextLaunchConfig? LaunchConfig { get; set; }

    /// <summary>Pomodoro timer state for this Zone.</summary>
    public PomodoroState? Pomodoro { get; set; }

    /// <summary>Last time a file in this Zone was opened or moved in (for Smart Dormancy).</summary>
    public DateTime LastInteractedAt { get; set; } = DateTime.Now;

    /// <summary>Whether this Zone is in dormant (auto-collapsed) mode.</summary>
    public bool IsDormant { get; set; }

    /// <summary>Whether this Zone is the currently active context.</summary>
    public bool IsActiveContext { get; set; }

    /// <summary>Sticky note / memo text for this Zone (便利貼).</summary>
    public string Memo { get; set; } = "";
}

public class AppConfig
{
    /// <summary>Schema version for forward-compat migration.</summary>
    public string SchemaVersion { get; set; } = "3.0";

    public List<ZoneConfig> Zones { get; set; } = [];
    public int ActiveDesktopPage { get; set; } = 0;

    /// <summary>Title of the currently active context Zone (for Zen Mode integration).</summary>
    public string? ActiveContextZone { get; set; }

    /// <summary>Global sticky note / memo (總便利貼).</summary>
    public string GlobalMemo { get; set; } = "";

    /// <summary>Pomodoro session history with named tasks.</summary>
    public List<PomodoroLogEntry> PomodoroHistory { get; set; } = [];

    /// <summary>Global checklist for dashboard (便利貼/Tasks).</summary>
    public List<TaskCard> GlobalTasks { get; set; } = [];

    /// <summary>Whether to automatically route downloaded media to the active context zone.</summary>
    public bool AutoRouteDownloadsToActiveZone { get; set; } = false;

    /// <summary>The ID of the Zone that should receive downloaded media.</summary>
    public string? DownloadTargetZoneId { get; set; }
}

/// <summary>
/// A named Pomodoro session entry for history tracking.
/// </summary>
public class PomodoroLogEntry
{
    public string Label { get; set; } = "";
    public string ZoneTitle { get; set; } = "";
    public int DurationMinutes { get; set; } = 25;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool IsCompleted { get; set; }
    public string DurationStr => $"{DurationMinutes} 分鐘";

    /// <summary>Friendly display string, e.g. "今天 14:30" or "昨天 09:00".</summary>
    public string CompletedAtStr
    {
        get
        {
            if (!CompletedAt.HasValue) return "";
            var dt = CompletedAt.Value;
            var today = DateTime.Now.Date;
            if (dt.Date == today)          return $"今天 {dt:HH:mm}";
            if (dt.Date == today.AddDays(-1)) return $"昨天 {dt:HH:mm}";
            return dt.ToString("MM/dd HH:mm");
        }
    }
}
