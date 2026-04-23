using FocusFence.Models;

namespace FocusFence.Services;

/// <summary>
/// Periodically scans Zones and marks any that haven't been interacted with
/// for 72+ hours as dormant. Dormant zones auto-collapse and move to screen edge.
/// </summary>
public sealed class DormancyService : IDisposable
{
    private System.Timers.Timer? _scanTimer;
    private readonly AppConfig _config;
    private readonly TimeSpan _dormancyThreshold = TimeSpan.FromHours(72);
    private readonly TimeSpan _scanInterval = TimeSpan.FromMinutes(30);

    /// <summary>Fired when a Zone becomes dormant (zoneTitle).</summary>
    public event Action<string>? OnZoneDormant;

    /// <summary>Fired when dormancy status changes (for UI refresh).</summary>
    public event Action? OnDormancyChanged;

    public DormancyService(AppConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        _scanTimer = new System.Timers.Timer(_scanInterval.TotalMilliseconds);
        _scanTimer.Elapsed += (_, _) => ScanForDormantZones();
        _scanTimer.Start();

        // Also run an initial scan
        ScanForDormantZones();
    }

    public void Stop()
    {
        _scanTimer?.Stop();
        _scanTimer?.Dispose();
        _scanTimer = null;
    }

    /// <summary>
    /// Updates LastInteractedAt for the given zone (call when files are opened/moved in).
    /// </summary>
    public void MarkInteracted(string zoneTitle)
    {
        var zone = _config.Zones.FirstOrDefault(z => z.Title == zoneTitle);
        if (zone == null) return;

        zone.LastInteractedAt = DateTime.Now;

        if (zone.IsDormant)
        {
            zone.IsDormant = false;
            OnDormancyChanged?.Invoke();
        }
    }

    public void ReactivateZone(string zoneTitle)
    {
        MarkInteracted(zoneTitle);
    }

    private void ScanForDormantZones()
    {
        bool changed = false;
        var now = DateTime.Now;

        foreach (var zone in _config.Zones)
        {
            bool shouldBeDormant = (now - zone.LastInteractedAt) > _dormancyThreshold;

            if (shouldBeDormant && !zone.IsDormant)
            {
                zone.IsDormant = true;
                OnZoneDormant?.Invoke(zone.Title);
                changed = true;
            }
        }

        if (changed)
            OnDormancyChanged?.Invoke();
    }

    /// <summary>Returns the count of currently dormant zones.</summary>
    public int DormantCount => _config.Zones.Count(z => z.IsDormant);

    /// <summary>Returns human-readable dormancy info for a zone.</summary>
    public static string GetDormancyTooltip(ZoneConfig zone)
    {
        var elapsed = DateTime.Now - zone.LastInteractedAt;
        if (elapsed.TotalDays >= 1)
            return $"上次使用：{(int)elapsed.TotalDays} 天前";
        if (elapsed.TotalHours >= 1)
            return $"上次使用：{(int)elapsed.TotalHours} 小時前";
        return "最近使用過";
    }

    public void Dispose()
    {
        Stop();
    }
}
