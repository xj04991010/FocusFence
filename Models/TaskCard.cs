using System.Text.Json.Serialization;

namespace FocusFence.Models;

/// <summary>
/// A lightweight task card that lives within a specific Zone/Fence.
/// Designed for small-scope, Fence-specific task tracking — NOT a full PM system.
/// </summary>
public class TaskCard
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public bool IsCompleted { get; set; }
    public DateTime? Deadline { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Configuration for one-click context launching from a Zone.
/// </summary>
public class ContextLaunchConfig
{
    public List<string> AppPaths { get; set; } = [];
    public int? SystemVolume { get; set; }    // 0-100, null = don't change
    public bool AutoStartPomodoro { get; set; } = false;
    public int HotkeyNumber { get; set; }     // 0 = unset, 1-9 = Win+number
}

/// <summary>
/// State of a Pomodoro session for a specific Zone.
/// </summary>
public class PomodoroState
{
    public int DurationMinutes { get; set; } = 25;
    public int RemainingSeconds { get; set; }
    public bool IsRunning { get; set; }
    public DateTime? StartedAt { get; set; }
    public int CompletedCount { get; set; }
    public string ActiveLabel { get; set; } = "";
}

/// <summary>
/// Sort mode for file listing in ZoneWindow.
/// </summary>
public enum SortMode
{
    Name,
    Date,
    Type,
    Size
}
