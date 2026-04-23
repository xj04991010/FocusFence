using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;

namespace FocusFence.Models;

public class FileItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string FullPath { get; set; } = "";
    
    private string _fileName = "";
    public string FileName 
    { 
        get => _fileName; 
        set { _fileName = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileName))); }
    }
    
    private string _displayName = "";
    public string DisplayName 
    { 
        get => _displayName; 
        set { _displayName = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName))); }
    }
    public string IconEmoji { get; set; } = "📄";
    public bool IsDirectory { get; set; }
    public DateTime LastModified { get; set; } = DateTime.MinValue;
    public long FileSize { get; set; } = 0;

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set { _isEditing = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing))); }
    }

    // ── Thumbnail support ────────────────────────────────────────────

    private System.Windows.Media.Imaging.BitmapSource? _thumbnail;
    public System.Windows.Media.Imaging.BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThumbnail)));
        }
    }

    /// <summary>True when a real thumbnail image is loaded.</summary>
    public bool HasThumbnail => _thumbnail != null;

    /// <summary>Whether this file type supports thumbnail preview.</summary>
    public bool IsThumbnailable
    {
        get
        {
            if (IsDirectory) return false;
            string ext = Path.GetExtension(FullPath).ToLowerInvariant();
            // Expanded to include common video formats, so they can trigger the shell thumbnail load
            return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp"
                       or ".ico" or ".tiff" or ".tif" or ".mp4" or ".mov" or ".avi" 
                       or ".mkv" or ".webm" or ".pdf" or ".ts" or ".flv";
        }
    }

    /// <summary>
    /// Asynchronously loads a thumbnail for image files.
    /// Called after construction to avoid blocking the UI thread.
    /// </summary>
    public void LoadThumbnailAsync()
    {
        if (!IsThumbnailable || !File.Exists(FullPath)) return;

        // Use ThreadPool to fetch the thumbnail off the UI thread
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                // Attempt to retrieve a 96x96 native Windows Explorer shell thumbnail
                // This correctly handles video frame extraction, PDFs, etc.
                var nativeThumb = FocusFence.Helpers.NativeThumbnailProvider.GetThumbnail(FullPath, 96, 96);
                
                if (nativeThumb != null)
                {
                    // Dispatch back to UI thread to set the property
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        Thumbnail = nativeThumb;
                    });
                }
            }
            catch
            {
                // Silently fail — icon fallback will be used
            }
        });
    }

    public static FileItem FromPath(string path)
    {
        string name = Path.GetFileName(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        bool isDir = Directory.Exists(path);

        var item = new FileItem
        {
            FullPath    = path,
            FileName    = name,
            DisplayName = name.Length > 16 ? name[..13] + "..." : name,
            IconEmoji   = GetEmoji(ext, isDir),
            IsDirectory = isDir
        };

        // Populate sort metadata
        try
        {
            if (isDir)
            {
                var di = new DirectoryInfo(path);
                item.LastModified = di.LastWriteTime;
            }
            else
            {
                var fi = new FileInfo(path);
                item.LastModified = fi.LastWriteTime;
                item.FileSize = fi.Length;
            }
        }
        catch { /* metadata is optional */ }

        // Kick off async thumbnail loading
        item.LoadThumbnailAsync();

        return item;
    }

    private static string GetEmoji(string ext, bool isDirectory)
    {
        // Using Segoe Fluent Icons / Segoe MDL2 Assets for a premium, native Windows look
        if (isDirectory) return "\uE8B7"; // Folder icon

        return ext switch
        {
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" or ".ts" or ".flv" => "\uE714", // Video
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" => "\uEB9F", // Image
            ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".m4a" => "\uE189", // Audio
            ".pdf" => "\uEA90", // PDF/Doc
            ".doc" or ".docx" or ".rtf" => "\uE8A5", // Document
            ".txt" or ".md" => "\uE8C4", // Text
            ".xls" or ".xlsx" or ".csv" => "\uE81C", // SpreadSheet (using Table icon)
            ".ppt" or ".pptx" => "\uEA80", // Presentation
            ".cs" or ".py" or ".js" or ".html" or ".css" or ".json" or ".xml" => "\uE9F5", // Script/Code (E9F5 is document with code)
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "\uE7B8", // Archive
            ".lnk" or ".url" => "\uE71B", // Link
            ".exe" or ".msi" => "\uE713", // Settings/Exe
            _ => "\uE7C3" // Default Document
        };
    }
}
