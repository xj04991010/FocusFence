using System.IO;
using System.Text.Json;
using FocusFence.Models;
using System.Windows.Threading;
using System.Threading;
using System.Threading.Tasks;

namespace FocusFence.Services;

/// <summary>
/// Manages Pomodoro timer sessions for individual Zones.
/// Uses System.Timers.Timer (not DispatcherTimer) to avoid UI thread blocking.
/// Logs completed sessions to a JSONL file for dashboard analytics.
/// </summary>
public sealed class PomodoroService : IDisposable
{
    private DispatcherTimer? _timer;
    private ZoneConfig? _activeZone;
    private readonly AppConfig _config;
    private string _sessionLabel = "";
    
    private DateTime? _targetEndTime;
    private int _pausedRemainingSeconds;
    
    // Limits concurrent file writes to 1 thread safely
    private static readonly SemaphoreSlim _logSemaphore = new(1, 1);

    /// <summary>Fired every second with (zoneTitle, remainingSeconds, totalSeconds).</summary>
    public event Action<string, int, int>? OnTick;

    /// <summary>Fired when the last 2 minutes begin (zoneTitle).</summary>
    public event Action<string>? OnWarningPhase;

    /// <summary>Fired when a session completes (zoneTitle, completedCount).</summary>
    public event Action<string, int>? OnSessionComplete;

    /// <summary>Fired when other zones should dim/undim (zoneTitle, isActive).</summary>
    public event Action<string, bool>? OnFocusChanged;

    private readonly PomodoroState _globalState = new();

    public PomodoroService(AppConfig config)
    {
        _config = config;
    }

    public bool IsRunning => _timer != null && _timer.IsEnabled;
    public string? ActiveZoneTitle => _activeZone?.Title ?? "";
    public string? ActiveLabel => _sessionLabel;

    public void Start(string zoneTitle, int durationMinutes = 25, string label = "")
    {
        // Stop any existing session
        if (IsRunning) Stop();

        _activeZone = _config.Zones.FirstOrDefault(z => z.Title == zoneTitle);

        _sessionLabel = label;
        var state = _activeZone?.Pomodoro;
        if (state == null)
        {
            if (_activeZone != null) { _activeZone.Pomodoro = new PomodoroState(); state = _activeZone.Pomodoro; }
            else { state = _globalState; }
        }
        
        state.DurationMinutes = durationMinutes;
        state.IsRunning = true;
        state.StartedAt = DateTime.Now;
        state.ActiveLabel = label;
        
        _targetEndTime = DateTime.UtcNow.AddMinutes(durationMinutes);
        state.RemainingSeconds = durationMinutes * 60;

        if (!string.IsNullOrEmpty(zoneTitle)) OnFocusChanged?.Invoke(zoneTitle, true);

        // Use DispatcherTimer — fires natively on UI thread, 0 context-switch overhead
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;

        if (_activeZone != null)
        {
            OnFocusChanged?.Invoke(_activeZone.Title, false);
            if (_activeZone.Pomodoro != null)
                _activeZone.Pomodoro.IsRunning = false;
        }
        _globalState.IsRunning = false;
        
        _activeZone = null;
        _targetEndTime = null;
    }

    public void Pause()
    {
        _timer?.Stop();
        var state = _activeZone?.Pomodoro ?? _globalState;
        state.IsRunning = false;
        if (_targetEndTime.HasValue)
        {
            _pausedRemainingSeconds = (int)(_targetEndTime.Value - DateTime.UtcNow).TotalSeconds;
        }
    }

    public void Resume()
    {
        var state = _activeZone?.Pomodoro ?? _globalState;
        if (_pausedRemainingSeconds <= 0) return;
        
        state.IsRunning = true;
        _targetEndTime = DateTime.UtcNow.AddSeconds(_pausedRemainingSeconds);
        _timer?.Start();
    }

    private void Tick()
    {
        var state = _activeZone?.Pomodoro ?? _globalState;
        if (!_targetEndTime.HasValue) return;

        int remaining = (int)(_targetEndTime.Value - DateTime.UtcNow).TotalSeconds;
        if (remaining < 0) remaining = 0;
        
        state.RemainingSeconds = remaining;
        int total = state.DurationMinutes * 60;

        OnTick?.Invoke(_activeZone?.Title ?? "", remaining, total);

        // Warning phase: last 2 minutes
        if (remaining == 120 && _activeZone != null)
            OnWarningPhase?.Invoke(_activeZone.Title);

        // Session complete
        if (remaining <= 0)
        {
            _timer?.Stop();
            state.IsRunning = false;
            state.CompletedCount++;
            state.RemainingSeconds = 0;

            _ = LogSessionAsync(_activeZone?.Title ?? "", state.DurationMinutes, _sessionLabel);
            OnSessionComplete?.Invoke(_activeZone?.Title ?? "", state.CompletedCount);
            if (_activeZone != null) OnFocusChanged?.Invoke(_activeZone.Title, false);

            _timer = null;
            _sessionLabel = "";
            _targetEndTime = null;
        }
    }

    /// <summary>
    /// Writes a completed Pomodoro record to the JSONL log asynchronously without blocking UI.
    /// </summary>
    private async Task LogSessionAsync(string zoneTitle, int durationMinutes, string label)
    {
        await _logSemaphore.WaitAsync();
        try
        {
            // Save to AppConfig.PomodoroHistory
            _config.PomodoroHistory.Add(new PomodoroLogEntry
            {
                Label = label,
                ZoneTitle = zoneTitle,
                DurationMinutes = durationMinutes,
                StartedAt = DateTime.Now.AddMinutes(-durationMinutes),
                CompletedAt = DateTime.Now,
                IsCompleted = true
            });

            // Keep only last 100 entries
            if (_config.PomodoroHistory.Count > 100)
                _config.PomodoroHistory.RemoveRange(0, _config.PomodoroHistory.Count - 100);

            // Also write to JSONL for analytics
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FocusFence");
            Directory.CreateDirectory(dir);
            string logPath = Path.Combine(dir, "pomodoro_log.jsonl");

            var record = new
            {
                zone = zoneTitle,
                label = label,
                date = DateTime.Now.ToString("yyyy-MM-dd"),
                completed_at = DateTime.Now.ToString("HH:mm"),
                duration = durationMinutes
            };

            string line = JsonSerializer.Serialize(record) + "\n";
            await File.AppendAllTextAsync(logPath, line);
        }
        catch { /* best-effort logging */ }
        finally
        {
            _logSemaphore.Release();
        }
    }

    /// <summary>
    /// Read the Pomodoro log and return weekly summary per zone.
    /// </summary>
    public static Dictionary<string, int> GetWeeklySummary()
    {
        var result = new Dictionary<string, int>();
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FocusFence");
            string logPath = Path.Combine(dir, "pomodoro_log.jsonl");
            if (!File.Exists(logPath)) return result;

            var weekAgo = DateTime.Now.AddDays(-7).ToString("yyyy-MM-dd");
            foreach (string line in File.ReadAllLines(logPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string date = root.GetProperty("date").GetString() ?? "";
                    if (string.Compare(date, weekAgo) < 0) continue;

                    string zone = root.GetProperty("zone").GetString() ?? "";
                    result[zone] = result.GetValueOrDefault(zone) + 1;
                }
                catch { /* skip malformed lines */ }
            }
        }
        catch { }
        return result;
    }

    public void Dispose()
    {
        Stop();
    }
}
