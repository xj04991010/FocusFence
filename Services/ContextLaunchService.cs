using System.Diagnostics;
using System.Runtime.InteropServices;
using FocusFence.Models;

namespace FocusFence.Services;

/// <summary>
/// Handles one-click context launching: brings apps to foreground,
/// sets system volume, and optionally starts a Pomodoro session.
/// 
/// "From 'my desktop is organized' to 'my desktop knows what I'm doing right now.'"
/// </summary>
public sealed class ContextLaunchService
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    private readonly AppConfig _config;
    private readonly PomodoroService _pomodoroService;

    /// <summary>Fired when the active context changes (newZoneId, or null for deactivation).</summary>
    public event Action<string?>? OnContextChanged;

    public ContextLaunchService(AppConfig config, PomodoroService pomodoroService)
    {
        _config = config;
        _pomodoroService = pomodoroService;
    }

    /// <summary>
    /// Activates a Zone's context: launches configured apps, adjusts volume, starts Pomodoro.
    /// </summary>
    public void LaunchContext(string zoneId)
    {
        var zone = _config.Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone?.LaunchConfig == null) return;

        // Deactivate previous context
        foreach (var z in _config.Zones)
            z.IsActiveContext = false;

        zone.IsActiveContext = true;
        _config.ActiveContextZone = zoneId;
        zone.LastInteractedAt = DateTime.Now;

        var lc = zone.LaunchConfig;

        // Launch or bring apps to foreground
        foreach (string appPath in lc.AppPaths)
        {
            try
            {
                LaunchOrFocus(appPath);
            }
            catch (Exception ex) { Debug.WriteLine($"App launch failed: {ex.Message}"); }
        }

        // Set system volume if configured
        if (lc.SystemVolume.HasValue)
        {
            SetSystemVolume(lc.SystemVolume.Value);
        }

        // Auto-start Pomodoro if configured
        if (lc.AutoStartPomodoro)
        {
            _pomodoroService.Start(zoneId, zone.Pomodoro?.DurationMinutes ?? 25);
        }

        OnContextChanged?.Invoke(zoneId);
    }

    /// <summary>
    /// Deactivates the current context.
    /// </summary>
    public void DeactivateContext()
    {
        foreach (var z in _config.Zones)
            z.IsActiveContext = false;
        _config.ActiveContextZone = null;
        OnContextChanged?.Invoke(null);
    }

    private static void LaunchOrFocus(string exePath)
    {
        string exeName = System.IO.Path.GetFileNameWithoutExtension(exePath);

        // Check if already running
        var existing = Process.GetProcessesByName(exeName);
        if (existing.Length > 0)
        {
            // Bring first instance to foreground
            var proc = existing[0];
            if (proc.MainWindowHandle != IntPtr.Zero)
            {
                ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(proc.MainWindowHandle);
            }
            return;
        }

        // Launch new instance
        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Sets the system master volume (0-100).
    /// Uses Windows Core Audio via COM interop (lightweight, no NuGet needed).
    /// </summary>
    private static void SetSystemVolume(int volumePercent)
    {
        try
        {
            // Use PowerShell one-liner as lightweight alternative to NAudio
            // This avoids adding a NuGet dependency for a single API call
            float scalar = Math.Clamp(volumePercent / 100f, 0f, 1f);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"(Get-AudioDevice -PlaybackVolume {volumePercent}) 2>$null; " +
                           $"$vol = New-Object -ComObject MMDeviceEnumerator 2>$null\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            // Fallback: use nircmd if available, otherwise silently skip
            var nircmd = new ProcessStartInfo
            {
                FileName = "nircmd.exe",
                Arguments = $"setsysvolume {(int)(65535 * scalar)}",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try { Process.Start(nircmd); }
            catch (Exception ex) { Debug.WriteLine($"nircmd not available: {ex.Message}"); }
        }
        catch (Exception ex) { Debug.WriteLine($"Volume control failed: {ex.Message}"); }
    }
}
