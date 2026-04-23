namespace FocusFence.Interop;

/// <summary>
/// Finds the WorkerW window behind the desktop icons and provides
/// methods to embed/unembed child windows into the desktop layer.
/// 
/// Desktop window hierarchy (after 0x052C message):
///   Desktop (root)
///     ├── Progman
///     │     └── SHELLDLL_DefView  (desktop icons)
///     │           └── SysListView32
///     ├── WorkerW  ← this one HAS SHELLDLL_DefView (we skip it)
///     └── WorkerW  ← this one is EMPTY (our target, behind icons)
/// </summary>
internal static class DesktopBridge
{
    private static IntPtr _workerW = IntPtr.Zero;

    /// <summary>
    /// Locates the WorkerW window that sits behind the desktop icons.
    /// Sends message 0x052C to Progman to force its creation.
    /// </summary>
    public static IntPtr GetWorkerW()
    {
        if (_workerW != IntPtr.Zero)
            return _workerW;

        // Step 1: Find Progman (the desktop shell container)
        IntPtr progman = Win32Api.FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            return IntPtr.Zero;

        // Step 2: Send undocumented message 0x052C to Progman.
        // This forces Windows to create a separate WorkerW window
        // between the wallpaper and the desktop icons.
        Win32Api.SendMessageTimeout(
            progman, 0x052C,
            IntPtr.Zero, IntPtr.Zero,
            Win32Api.SMTO_NORMAL, 1000, out _);

        // Step 3: Enumerate all top-level windows.
        // Find the WorkerW that does NOT contain SHELLDLL_DefView.
        // The one AFTER the WorkerW with SHELLDLL_DefView is our target.
        _workerW = IntPtr.Zero;
        Win32Api.EnumWindows((topHandle, _) =>
        {
            IntPtr shellDefView = Win32Api.FindWindowEx(
                topHandle, IntPtr.Zero, "SHELLDLL_DefView", null);

            if (shellDefView != IntPtr.Zero)
            {
                // Found the WorkerW (or Progman) that has the desktop icons.
                // The NEXT WorkerW in z-order is our empty target.
                _workerW = Win32Api.FindWindowEx(
                    IntPtr.Zero, topHandle, "WorkerW", null);
            }
            return true; // continue enumeration
        }, IntPtr.Zero);

        return _workerW;
    }

    /// <summary>
    /// Makes the given window a child of the desktop WorkerW layer.
    /// </summary>
    public static bool EmbedWindow(IntPtr childHwnd)
    {
        IntPtr workerW = GetWorkerW();
        if (workerW == IntPtr.Zero)
            return false;

        Win32Api.SetParent(childHwnd, workerW);
        return true;
    }

    /// <summary>
    /// Restores the window back to a top-level window.
    /// </summary>
    public static void UnembedWindow(IntPtr childHwnd)
    {
        Win32Api.SetParent(childHwnd, IntPtr.Zero);
    }

    /// <summary>
    /// Invalidates the cached WorkerW handle (e.g., after Explorer restart).
    /// </summary>
    public static void Reset()
    {
        _workerW = IntPtr.Zero;
    }
}
