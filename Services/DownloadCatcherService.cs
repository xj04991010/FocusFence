using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using FocusFence.Models;

namespace FocusFence.Services;

public sealed class DownloadCatcherService : IDisposable
{
    private readonly AppConfig _config;
    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _mediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".svg",
        ".mp4", ".mov", ".avi", ".mkv", ".webm", ".ts", ".flv", ".wmv"
    };

    // Keep track of recent moves to avoid multiple triggers
    private readonly HashSet<string> _recentProcessed = new();

    public DownloadCatcherService(AppConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        try
        {
            string downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(downloadsPath)) return;

            _watcher = new FileSystemWatcher(downloadsPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            
            // Chrome creates .crdownload and renames to the final name when done
            _watcher.Created += OnFileDetected;
            _watcher.Renamed += OnFileDetected;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start DownloadCatcherService: {ex.Message}");
        }
    }

    private void OnFileDetected(object sender, FileSystemEventArgs e)
    {
        if (!_config.AutoRouteDownloadsToActiveZone || string.IsNullOrEmpty(_config.DownloadTargetZoneId)) return;

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

        // Fire and forget a task to handle the file moving
        // We delay slightly and poll to ensure the file is completely downloaded and unlocked
        Task.Run(async () =>
        {
            await Task.Delay(500);

            int maxRetries = 7200; // Up to 2 hours limit (7200 seconds)
            bool moved = false;

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    var fi = new FileInfo(e.FullPath);
                    fi.Refresh();
                    
                    // If file no longer exists, it might have been deleted or renamed by the browser
                    if (!fi.Exists) 
                        break; 

                    // Skip 0-byte placeholders
                    if (fi.Length == 0)
                    {
                        await Task.Delay(1000);
                        continue;
                    }

                    // Test if file is fully unlocked by attempting to open it exclusively
                    using (var stream = fi.Open(FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        stream.Close();
                    }

                    // If we successfully opened it exclusively, it is ready to move.
                    string fileName = Path.GetFileName(e.FullPath);
                    string destPath = Path.Combine(zone.FolderPath, fileName);

                    int counter = 2;
                    string bn = Path.GetFileNameWithoutExtension(fileName);
                    while (File.Exists(destPath))
                    {
                        destPath = Path.Combine(zone.FolderPath, $"{bn} ({counter++}){ext}");
                    }

                    File.Move(e.FullPath, destPath);
                    moved = true;
                    
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        Debug.WriteLine($"Auto-routed {fileName} to active zone {zone.Title}");
                    });
                    break; // Success
                }
                catch (IOException)
                {
                    // File still locked (downloading/writing), wait and try again
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error routing download {e.FullPath}: {ex.Message}");
                    break;
                }

                await Task.Delay(1000);
            }

            await Task.Delay(5000);
            lock (_recentProcessed)
            {
                _recentProcessed.Remove(e.FullPath);
            }
        });
    }

    public void Stop()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
