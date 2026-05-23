using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FocusFence.Models;

namespace FocusFence.Services;

public sealed class DownloadCatcherService : IDisposable
{
    private readonly AppConfig _config;
    private FileSystemWatcher? _watcher;
    private string? _downloadsPath;
    private readonly HashSet<string> _mediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".svg",
        ".mp4", ".mov", ".avi", ".mkv", ".webm", ".ts", ".flv", ".wmv",
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma",
        ".srt", ".ass", ".vtt", ".sub", ".ssa",
        ".zip", ".rar", ".7z",
        ".pdf", ".psd", ".ai",
    };

    // Keep track of recent moves to avoid multiple triggers (thread-safe)
    private readonly HashSet<string> _recentProcessed = new(StringComparer.OrdinalIgnoreCase);

    // Auto-recovery timer
    private System.Threading.Timer? _healthCheckTimer;
    private int _consecutiveErrors;

    // ── Public feedback for UI ──────────────────────────────────
    /// <summary>Fired on UI thread after a file is successfully caught. Args: (fileName, targetZoneTitle)</summary>
    public event Action<string, string>? OnFileCaught;

    /// <summary>Total files caught since app start.</summary>
    public int TotalCaughtCount { get; private set; }

    /// <summary>Timestamp of last successful catch.</summary>
    public DateTime? LastCaughtAt { get; private set; }
    public DownloadCatcherService(AppConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        try
        {
            _downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(_downloadsPath)) return;

            CreateWatcher();

            // Health-check every 60 seconds — restart if watcher silently died
            _healthCheckTimer = new System.Threading.Timer(HealthCheck, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DownloadCatcher] Failed to start: {ex.Message}");
        }
    }

    private void CreateWatcher()
    {
        // Dispose old watcher if any
        DisposeWatcher();

        if (string.IsNullOrEmpty(_downloadsPath) || !Directory.Exists(_downloadsPath)) return;

        _watcher = new FileSystemWatcher(_downloadsPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            InternalBufferSize = 65536, // 64KB buffer to reduce overflow risk
            EnableRaisingEvents = true
        };

        // Chrome creates .crdownload and renames to the final name when done
        _watcher.Created += OnFileDetected;
        _watcher.Renamed += OnFileDetected;

        // Critical: handle the Error event to auto-recover from buffer overflows
        _watcher.Error += OnWatcherError;

        _consecutiveErrors = 0;
        Debug.WriteLine("[DownloadCatcher] Watcher started successfully.");
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        Debug.WriteLine($"[DownloadCatcher] Watcher error: {ex.Message}");
        _consecutiveErrors++;

        // Restart the watcher after a short delay
        if (_consecutiveErrors <= 10)
        {
            Task.Run(async () =>
            {
                int delayMs = Math.Min(1000 * _consecutiveErrors, 30000); // back-off up to 30s
                await Task.Delay(delayMs);
                try
                {
                    Application.Current?.Dispatcher.Invoke(() => { }); // check app alive
                    CreateWatcher();
                    Debug.WriteLine($"[DownloadCatcher] Watcher restarted after error (attempt {_consecutiveErrors}).");
                }
                catch
                {
                    Debug.WriteLine("[DownloadCatcher] App is shutting down, not restarting watcher.");
                }
            });
        }
        else
        {
            Debug.WriteLine("[DownloadCatcher] Too many consecutive errors, giving up auto-recovery.");
        }
    }

    private void HealthCheck(object? state)
    {
        try
        {
            if (_watcher == null || !_watcher.EnableRaisingEvents)
            {
                Debug.WriteLine("[DownloadCatcher] Health check: watcher is dead, restarting...");
                CreateWatcher();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DownloadCatcher] Health check error: {ex.Message}");
        }
    }

    private void OnFileDetected(object sender, FileSystemEventArgs e)
    {
        if (!_config.AutoRouteDownloadsToActiveZone || string.IsNullOrEmpty(_config.DownloadTargetZoneId)) return;

        // Ignore temp download files (.crdownload, .part, .tmp)
        string currentExt = Path.GetExtension(e.FullPath);
        if (currentExt.Equals(".crdownload", StringComparison.OrdinalIgnoreCase) ||
            currentExt.Equals(".part", StringComparison.OrdinalIgnoreCase) ||
            currentExt.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
            return;

        string targetZoneId = _config.DownloadTargetZoneId;
        var zone = _config.Zones.FirstOrDefault(z => z.Id == targetZoneId);
        if (zone == null || string.IsNullOrEmpty(zone.FolderPath)) return;

        string ext = Path.GetExtension(e.FullPath);
        if (!_mediaExtensions.Contains(ext)) return;

        // Debounce
        lock (_recentProcessed)
        {
            if (_recentProcessed.Contains(e.FullPath)) return;
            _recentProcessed.Add(e.FullPath);
        }

        Debug.WriteLine($"[DownloadCatcher] Detected: {Path.GetFileName(e.FullPath)}");

        // Fire and forget a task to handle the file moving
        // We delay slightly and poll to ensure the file is completely downloaded and unlocked
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000); // initial wait for file to settle

                int maxRetries = 7200; // Up to 2 hours limit (7200 seconds)

                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        var fi = new FileInfo(e.FullPath);
                        fi.Refresh();

                        // If file no longer exists, it might have been deleted or renamed by the browser
                        if (!fi.Exists)
                        {
                            Debug.WriteLine($"[DownloadCatcher] File disappeared: {Path.GetFileName(e.FullPath)}");
                            break;
                        }

                        // Skip 0-byte placeholders
                        if (fi.Length == 0)
                        {
                            await Task.Delay(1000);
                            continue;
                        }

                        // Check if a companion .crdownload file exists (Chrome still downloading)
                        string crdownloadPath = e.FullPath + ".crdownload";
                        if (File.Exists(crdownloadPath))
                        {
                            await Task.Delay(2000);
                            continue;
                        }

                        // Test if file is fully unlocked by attempting to open it exclusively
                        using (var stream = fi.Open(FileMode.Open, FileAccess.Read, FileShare.None))
                        {
                            stream.Close();
                        }

                        // Double-check size stability: wait 500ms, then confirm size unchanged
                        long sizeBefore = fi.Length;
                        await Task.Delay(500);
                        fi.Refresh();
                        if (!fi.Exists) break;
                        if (fi.Length != sizeBefore)
                        {
                            // File is still being written
                            await Task.Delay(1000);
                            continue;
                        }

                        // Re-verify the target zone in case config changed
                        var currentZone = _config.Zones.FirstOrDefault(z => z.Id == _config.DownloadTargetZoneId);
                        if (currentZone == null || string.IsNullOrEmpty(currentZone.FolderPath))
                        {
                            Debug.WriteLine("[DownloadCatcher] Target zone no longer valid, aborting move.");
                            break;
                        }

                        // Ensure target directory exists
                        if (!Directory.Exists(currentZone.FolderPath))
                        {
                            Directory.CreateDirectory(currentZone.FolderPath);
                        }

                        // If we successfully opened it exclusively, it is ready to move.
                        string fileName = Path.GetFileName(e.FullPath);
                        string destPath = Path.Combine(currentZone.FolderPath, fileName);

                        int counter = 2;
                        string bn = Path.GetFileNameWithoutExtension(fileName);
                        string destExt = Path.GetExtension(fileName);
                        while (File.Exists(destPath))
                        {
                            destPath = Path.Combine(currentZone.FolderPath, $"{bn} ({counter++}){destExt}");
                        }

                        File.Move(e.FullPath, destPath);

                        TotalCaughtCount++;
                        LastCaughtAt = DateTime.Now;
                        Debug.WriteLine($"[DownloadCatcher] ✔ Moved \"{fileName}\" → {currentZone.Title}");

                        // Notify UI on dispatcher thread
                        try
                        {
                            Application.Current?.Dispatcher.BeginInvoke(() =>
                            {
                                OnFileCaught?.Invoke(fileName, currentZone.Title);
                            });
                        }
                        catch { /* app shutting down */ }

                        break; // Success
                    }
                    catch (IOException)
                    {
                        // File still locked (downloading/writing), wait and try again
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // File locked by another process
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[DownloadCatcher] Error routing {Path.GetFileName(e.FullPath)}: {ex.Message}");
                        break;
                    }

                    await Task.Delay(1000);
                }
            }
            finally
            {
                // Always clean up _recentProcessed after a delay
                await Task.Delay(5000);
                lock (_recentProcessed)
                {
                    _recentProcessed.Remove(e.FullPath);
                }
            }
        });
    }

    private void DisposeWatcher()
    {
        if (_watcher != null)
        {
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFileDetected;
                _watcher.Renamed -= OnFileDetected;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
            }
            catch { /* swallow during cleanup */ }
            _watcher = null;
        }
    }

    public void Stop()
    {
        _healthCheckTimer?.Dispose();
        _healthCheckTimer = null;
        DisposeWatcher();
    }

    public void Dispose()
    {
        Stop();
    }
}
