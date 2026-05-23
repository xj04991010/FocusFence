using System.IO;

namespace FocusFence.Helpers;

/// <summary>
/// File name validation helper — inspired by Files App (files-community/Files)
/// and Microsoft's official naming conventions.
/// https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file
/// </summary>
public static class FileNameValidator
{
    // Windows reserved device names (from DOS era, still enforced)
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT0","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
    };

    // Characters forbidden in Windows file names
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Validates a file name. Returns null if valid, or a localized error message.
    /// </summary>
    public static string? Validate(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "檔名不能為空白。";

        // Check for invalid characters
        int idx = fileName.IndexOfAny(InvalidChars);
        if (idx >= 0)
            return $"檔名包含不合法字元「{fileName[idx]}」。\n不可使用 < > : \" / \\ | ? * 等字元。";

        // Check Windows reserved names (without extension)
        string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        if (ReservedNames.Contains(nameWithoutExt))
            return $"「{nameWithoutExt}」是 Windows 保留字，不能當作檔名。";

        // Cannot end with space or period
        if (fileName.EndsWith(' ') || fileName.EndsWith('.'))
            return "檔名不能以空格或句號結尾。";

        // File name length check (NTFS max = 255 characters)
        if (fileName.Length > 255)
            return $"檔名超過 255 字元上限（目前 {fileName.Length} 字元）。";

        return null; // Valid
    }

    /// <summary>
    /// Validates the full path length (MAX_PATH = 260 on classic Windows).
    /// </summary>
    public static string? ValidateFullPath(string directory, string fileName)
    {
        string fullPath = Path.Combine(directory, fileName);
        if (fullPath.Length >= 260)
            return $"完整路徑超過 260 字元上限（目前 {fullPath.Length} 字元）。\n路徑: {fullPath}";

        return null;
    }

    /// <summary>
    /// Checks whether the file extension has changed between old and new name.
    /// </summary>
    public static bool HasExtensionChanged(string oldName, string newName)
    {
        string oldExt = Path.GetExtension(oldName);
        string newExt = Path.GetExtension(newName);
        return !oldExt.Equals(newExt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Generates a unique file name by appending " (2)", " (3)", etc.
    /// Inspired by Files App's GenerateUniqueName pattern.
    /// </summary>
    public static string GenerateUniqueName(string directory, string desiredName, bool isDirectory = false)
    {
        string path = Path.Combine(directory, desiredName);
        if (!File.Exists(path) && !Directory.Exists(path))
            return desiredName;

        string baseName = Path.GetFileNameWithoutExtension(desiredName);
        string ext = isDirectory ? "" : Path.GetExtension(desiredName);
        int counter = 2;

        do
        {
            desiredName = $"{baseName} ({counter++}){ext}";
            path = Path.Combine(directory, desiredName);
        }
        while (File.Exists(path) || Directory.Exists(path));

        return desiredName;
    }

    /// <summary>
    /// Smart truncation that preserves the file extension for display.
    /// "很長的檔案名稱_001.mp4" → "很長的檔...mp4"
    /// "短名.txt" → "短名.txt" (no truncation)
    /// </summary>
    public static string SmartTruncate(string name, int maxLen = 18)
    {
        if (name.Length <= maxLen) return name;

        string ext = Path.GetExtension(name);
        string baseName = Path.GetFileNameWithoutExtension(name);

        if (string.IsNullOrEmpty(ext))
        {
            // Directory or extensionless file
            return name[..(maxLen - 3)] + "...";
        }

        int baseMaxLen = maxLen - ext.Length - 3; // Reserve space for "..." and extension
        if (baseMaxLen < 3) baseMaxLen = 3;
        if (baseMaxLen > baseName.Length) baseMaxLen = baseName.Length;

        return baseName[..baseMaxLen] + "..." + ext;
    }
}
