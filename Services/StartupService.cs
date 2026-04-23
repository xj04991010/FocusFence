using Microsoft.Win32;

namespace FocusFence.Services;

/// <summary>
/// Manages adding/removing FocusFence from Windows startup via the registry.
/// Key: HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run
/// </summary>
public static class StartupService
{
    private const string AppName = "FocusFence";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Returns true if FocusFence is configured to run at Windows startup.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(AppName) != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// Enables or disables auto-start at Windows login.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;

            if (enabled)
            {
                string exePath = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(AppName, $"\"{exePath}\" --startup");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch { /* registry access failed, ignore */ }
    }
}
