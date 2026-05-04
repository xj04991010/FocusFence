using System.IO;
using System.Text.Json;
using FocusFence.Models;

namespace FocusFence.Services;

/// <summary>
/// Handles loading and saving of zone configuration (config.json).
/// Creates sensible defaults on first run.
/// v3.0: Added schema versioning and migration support.
/// </summary>
public static class ConfigService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FocusFence");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
    private static readonly string SnapshotDir = Path.Combine(ConfigDir, "snapshot_backup");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return CreateDefault();

        try
        {
            string json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? CreateDefault();

            // Schema migration
            if (config.SchemaVersion != "3.0")
            {
                MigrateToV3(config);
            }

            return config;
        }
        catch (Exception ex)
        {
            // Backup the corrupted config so user data isn't silently lost
            try
            {
                string corruptPath = ConfigPath + $".corrupt_{DateTime.Now:yyyyMMdd_HHmmss}";
                File.Copy(ConfigPath, corruptPath, overwrite: true);
                System.Diagnostics.Debug.WriteLine($"Config corrupted, backed up to: {corruptPath}. Error: {ex.Message}");
            }
            catch { /* backup failed, proceed anyway */ }

            return CreateDefault();
        }
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDir);

        // Backup previous config before overwriting
        try
        {
            if (File.Exists(ConfigPath))
            {
                File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);

                // Also rotate into snapshot_backup (keep last 7)
                SaveRotatingSnapshot();
            }
        }
        catch (Exception ex)
        { 
            System.Diagnostics.Debug.WriteLine($"Backup failed: {ex.Message}");
        }

        string json = JsonSerializer.Serialize(config, JsonOpts);
        
        // Atomic write to prevent corruption
        string tmpPath = ConfigPath + ".tmp";
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, ConfigPath, overwrite: true);
    }

    /// <summary>
    /// Saves an auto-snapshot into the rotating backup directory.
    /// Keeps the last 7 snapshots.
    /// </summary>
    private static void SaveRotatingSnapshot()
    {
        try
        {
            Directory.CreateDirectory(SnapshotDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dest = Path.Combine(SnapshotDir, $"snapshot_{stamp}.json");
            File.Copy(ConfigPath, dest, overwrite: true);

            // Keep only last 7 (sort by parsed date from filename, not alphabetical string sort)
            var files = Directory.GetFiles(SnapshotDir, "snapshot_*.json")
                .Select(f => new 
                { 
                    Path = f, 
                    Date = ParseSnapshotDate(Path.GetFileNameWithoutExtension(f)) 
                })
                .OrderByDescending(x => x.Date)
                .Skip(7)
                .Select(x => x.Path)
                .ToList();

            foreach (var old in files)
            {
                try { File.Delete(old); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Snapshot delete failed: {ex.Message}"); }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Snapshot rotation failed: {ex.Message}"); }
    }

    private static DateTime ParseSnapshotDate(string filename)
    {
        // filename format: snapshot_yyyyMMdd_HHmmss
        try
        {
            if (filename.StartsWith("snapshot_") && filename.Length >= 24)
            {
                string datePart = filename.Substring(9, 15);
                if (DateTime.TryParseExact(datePart, "yyyyMMdd_HHmmss", null, System.Globalization.DateTimeStyles.None, out DateTime dt))
                    return dt;
            }
        }
        catch { }
        return DateTime.MinValue;
    }

    /// <summary>
    /// Migrates older config schemas to v3.0.
    /// Adds missing properties with sensible defaults.
    /// </summary>
    private static void MigrateToV3(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDir); // Ensure config directory exists before saving

        config.SchemaVersion = "3.0";

        foreach (var zone in config.Zones)
        {
            zone.TaskCards ??= [];
            zone.LaunchConfig ??= null;
            zone.Pomodoro ??= null;
            if (zone.LastInteractedAt == default)
                zone.LastInteractedAt = DateTime.Now;
        }

        // Save the migrated config
        Save(config);
    }

    /// <summary>
    /// Creates the three default zones matching the PRD workflow:
    /// Inbox (media capture), Active (project work), Arsenal (shortcuts).
    /// Also pre-creates the zone folders on the user's Desktop.
    /// </summary>
    private static AppConfig CreateDefault()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

        var zones = new List<ZoneConfig>
        {
            new()
            {
                Title    = "\U0001F4E5 Inbox",
                X        = 40,
                Y        = 40,
                Width    = 340,
                Height   = 300,
                FolderPath = Path.Combine(desktop, "FocusFence_Inbox"),
                ShowMemo = true,
                Memo     = $"👋 歡迎使用 FocusFence (v{version})！\n\n📌 快速上手指南：\n1. 【收納檔案】：直接將桌面的檔案或資料夾拖曳進此框框中。\n2. 【功能選單】：點擊框框底部標題可以設定顏色、大小以及開啟「番茄鐘」。\n3. 【解壓縮】：對著壓縮檔按「右鍵」並選擇「📦 解壓縮」。\n4. 【進入資料夾】：對著資料夾「左鍵點兩下」即可直接進入下一層。\n5. 【控制台】：到 Windows 系統列 (右下角) 點擊圖示，或使用全域快捷鍵開啟控制台。\n\n🌟 祝您專注愉快！(此便利貼可隨時在設定中關閉)"
            },
            new()
            {
                Title    = "\u2694\uFE0F Active",
                X        = 420,
                Y        = 40,
                Width    = 340,
                Height   = 300,
                FolderPath = Path.Combine(desktop, "FocusFence_Active")
            },
            new()
            {
                Title    = "\U0001F527 Arsenal",
                X        = 800,
                Y        = 40,
                Width    = 340,
                Height   = 300,
                FolderPath = Path.Combine(desktop, "FocusFence_Arsenal")
            }
        };

        // Pre-create zone folders
        foreach (var z in zones)
        {
            if (!string.IsNullOrEmpty(z.FolderPath))
                Directory.CreateDirectory(z.FolderPath);
        }

        var config = new AppConfig { Zones = zones };
        Save(config);
        return config;
    }
}
