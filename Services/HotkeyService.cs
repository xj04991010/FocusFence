using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FocusFence.Services;

/// <summary>
/// Manages global hotkeys via RegisterHotKey / UnregisterHotKey.
/// 
/// v3.0: Adds conflict detection — if a hotkey is already registered by
/// another application, automatically tries a fallback (adding Shift modifier)
/// and fires an event so the UI can notify the user.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Modifier keys
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CTRL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    // Virtual key codes
    private const uint VK_F = 0x46;          // 'F'

    // Hotkey IDs
    public const int HOTKEY_TOGGLE_ZONES = 9001;
    public const int HOTKEY_SHOW_DASHBOARD = 9002;
    public const int HOTKEY_PREV_DESKTOP = 9003;
    public const int HOTKEY_NEXT_DESKTOP = 9004;

    private IntPtr _hwnd;
    private HwndSource? _source;
    private readonly Dictionary<int, Action> _handlers = [];

    /// <summary>
    /// Fired when a hotkey could not be registered (conflict with another app).
    /// Parameters: (hotkeyId, virtualKey)
    /// </summary>
    public event Action<int, uint>? OnHotkeyConflict;

    /// <summary>
    /// Fired when a fallback hotkey was successfully registered.
    /// Parameters: (hotkeyId, virtualKey, fallbackModifiers)
    /// </summary>
    public event Action<int, uint, uint>? OnHotkeyFallback;

    /// <summary>
    /// Attaches to a WPF window and starts listening.
    /// Must be called after the window is loaded (has an HWND).
    /// </summary>
    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>
    /// Registers the default FocusFence hotkeys:
    ///   Ctrl+Alt+F  → Toggle all zones visibility
    ///   Ctrl+Alt+D  → Show dashboard
    ///   Ctrl+Alt+←/→ → Switch desktop pages
    /// </summary>
    public void RegisterDefaults(Action onToggleZones, Action onShowDashboard, Action onPrevDesktop, Action onNextDesktop)
    {
        Register(HOTKEY_TOGGLE_ZONES, MOD_CTRL | MOD_ALT | MOD_NOREPEAT, VK_F, onToggleZones);
        Register(HOTKEY_SHOW_DASHBOARD, MOD_CTRL | MOD_ALT | MOD_NOREPEAT, 0x44 /* VK_D */, onShowDashboard);
        Register(HOTKEY_PREV_DESKTOP, MOD_CTRL | MOD_ALT | MOD_NOREPEAT, 0x25 /* VK_LEFT */, onPrevDesktop);
        Register(HOTKEY_NEXT_DESKTOP, MOD_CTRL | MOD_ALT | MOD_NOREPEAT, 0x27 /* VK_RIGHT */, onNextDesktop);
    }

    private void Register(int id, uint modifiers, uint vk, Action handler)
    {
        bool success = RegisterHotKey(_hwnd, id, modifiers, vk);

        if (!success)
        {
            int error = Marshal.GetLastWin32Error();

            // ERROR_HOTKEY_ALREADY_REGISTERED = 1409
            if (error == 1409)
            {
                OnHotkeyConflict?.Invoke(id, vk);

                // Try fallback: add Shift modifier
                uint fallbackMods = modifiers | MOD_SHIFT;
                bool fallback = RegisterHotKey(_hwnd, id, fallbackMods, vk);

                if (fallback)
                {
                    _handlers[id] = handler;
                    OnHotkeyFallback?.Invoke(id, vk, fallbackMods);
                    LogWarning($"Hotkey {id} (VK=0x{vk:X2}): conflict detected, registered fallback with Shift");
                    return;
                }
            }

            LogWarning($"Hotkey {id} (VK=0x{vk:X2}): registration failed (error={error})");
            return;
        }

        _handlers[id] = handler;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_handlers.TryGetValue(id, out var action))
            {
                action();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private static void LogWarning(string message)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FocusFence");
            System.IO.Directory.CreateDirectory(dir);
            string logPath = System.IO.Path.Combine(dir, "hotkey.log");
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n";
            System.IO.File.AppendAllText(logPath, entry);
        }
        catch { }
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys)
            UnregisterHotKey(_hwnd, id);
        _handlers.Clear();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
