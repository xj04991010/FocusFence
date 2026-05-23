using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualBasic.FileIO;
using FocusFence.Models;
using FocusFence.Helpers;
using FocusFence.Windows;

namespace FocusFence.Services;

/// <summary>
/// Service handling all core file operations, detached from the UI code-behind.
/// Following MVVM and Service-layer best practices.
/// </summary>
public static class FileOperationService
{
    public static bool RenameItem(FileItem file, string newName, string currentPath)
    {
        string oldName = Path.GetFileName(file.FullPath);

        if (string.IsNullOrEmpty(newName) || newName == oldName)
            return false;

        string? validationError = FileNameValidator.Validate(newName);
        if (validationError != null)
        {
            FocusFenceDialog.ShowMessage($"無法重新命名：\n{validationError}", "FocusFence", true);
            return false;
        }

        string? pathError = FileNameValidator.ValidateFullPath(currentPath, newName);
        if (pathError != null)
        {
            FocusFenceDialog.ShowMessage($"無法重新命名：\n{pathError}", "FocusFence", true);
            return false;
        }

        if (!file.IsDirectory && FileNameValidator.HasExtensionChanged(oldName, newName))
        {
            if (!FocusFenceDialog.ShowConfirm("變更副檔名可能導致檔案無法使用。\n確定要更改嗎？", "FocusFence"))
                return false;
        }

        string newPath = Path.Combine(currentPath, newName);

        if (!newPath.Equals(file.FullPath, StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(newPath) || Directory.Exists(newPath)))
        {
            if (FocusFenceDialog.ShowConfirm($"「{newName}」已經存在。\n要自動編號嗎？", "FocusFence"))
            {
                newName = FileNameValidator.GenerateUniqueName(currentPath, newName, file.IsDirectory);
                newPath = Path.Combine(currentPath, newName);
            }
            else
            {
                return false;
            }
        }

        try
        {
            string oldPath = file.FullPath;

            if (!newPath.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
            {
                if (file.IsDirectory) Directory.Move(oldPath, newPath);
                else File.Move(oldPath, newPath);
            }
            else
            {
                // Case-only rename
                string tempPath = oldPath + ".tmp_rename";
                if (file.IsDirectory) { Directory.Move(oldPath, tempPath); Directory.Move(tempPath, newPath); }
                else { File.Move(oldPath, tempPath); File.Move(tempPath, newPath); }
            }

            UndoService.RecordMove(new List<UndoRecord> { new() { OriginalSource = oldPath, NewDestination = newPath } });
            
            file.FullPath = newPath;
            file.FileName = newName;
            file.DisplayName = FileNameValidator.SmartTruncate(newName);
            return true;
        }
        catch (Exception ex)
        {
            FocusFenceDialog.ShowMessage($"重新命名失敗: {ex.Message}", "FocusFence", true);
            return false;
        }
    }

    public static bool BatchRenameItems(List<FileItem> targets, string baseName, string currentPath)
    {
        string? validationError = FileNameValidator.Validate(baseName + "_001.tmp");
        if (validationError != null)
        {
            FocusFenceDialog.ShowMessage($"基本名稱不合法：\n{validationError}", "FocusFence", true);
            return false;
        }

        int renamed = 0;
        int failed = 0;
        var errors = new List<string>();
        var undoRecords = new List<UndoRecord>();

        var sorted = targets.OrderBy(f => f.FileName).ToList();
        int pad = Math.Max(3, sorted.Count.ToString().Length);

        for (int i = 0; i < sorted.Count; i++)
        {
            var file = sorted[i];
            try
            {
                string ext = Path.GetExtension(file.FullPath);
                string newName = $"{baseName}_{(i + 1).ToString().PadLeft(pad, '0')}{ext}";
                string newPath = Path.Combine(currentPath, newName);

                if (newPath.Equals(file.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    renamed++;
                    continue;
                }

                string oldPath = file.FullPath;

                if (File.Exists(newPath))
                {
                    string tempPath = newPath + ".tmp_rename";
                    File.Move(oldPath, tempPath);
                    file.FullPath = tempPath;
                }

                File.Move(file.FullPath, newPath);
                undoRecords.Add(new UndoRecord { OriginalSource = oldPath, NewDestination = newPath });
                
                file.FullPath = newPath;
                file.FileName = newName;
                file.DisplayName = FileNameValidator.SmartTruncate(newName);
                renamed++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{file.FileName}: {ex.Message}");
            }
        }

        if (undoRecords.Count > 0)
            UndoService.RecordMove(undoRecords);

        if (failed > 0)
            FocusFenceDialog.ShowMessage($"已重新命名 {renamed} 個，失敗 {failed} 個：\n" + string.Join("\n", errors.Take(5)), "FocusFence", true);

        return renamed > 0;
    }

    public static string? CreateFolder(string currentPath)
    {
        try
        {
            string newName = FileNameValidator.GenerateUniqueName(currentPath, "新增資料夾", true);
            string newPath = Path.Combine(currentPath, newName);
            Directory.CreateDirectory(newPath);
            return newPath;
        }
        catch (Exception ex)
        {
            FocusFenceDialog.ShowMessage(ex.Message, "FocusFence", true);
            return null;
        }
    }

    public static string? CreateTextFile(string currentPath)
    {
        try
        {
            string newName = FileNameValidator.GenerateUniqueName(currentPath, "新增文字檔.txt", false);
            string newPath = Path.Combine(currentPath, newName);
            File.WriteAllText(newPath, "");
            return newPath;
        }
        catch (Exception ex)
        {
            FocusFenceDialog.ShowMessage(ex.Message, "FocusFence", true);
            return null;
        }
    }

    public static bool PasteItems(List<string> clipboardFiles, bool isCut, string currentPath)
    {
        var undoRecords = new List<UndoRecord>();
        int pastedCount = 0;

        foreach (string src in clipboardFiles)
        {
            try
            {
                if (!File.Exists(src) && !Directory.Exists(src)) continue;

                string name = Path.GetFileName(src);
                string dest = Path.Combine(currentPath, name);

                if (src.Equals(dest, StringComparison.OrdinalIgnoreCase)) continue;

                bool isDir = Directory.Exists(src);
                string uniqueName = FileNameValidator.GenerateUniqueName(currentPath, name, isDir);
                dest = Path.Combine(currentPath, uniqueName);

                if (isCut)
                {
                    if (isDir) FileSystem.MoveDirectory(src, dest, UIOption.AllDialogs);
                    else FileSystem.MoveFile(src, dest, UIOption.AllDialogs);
                }
                else
                {
                    if (isDir) FileSystem.CopyDirectory(src, dest, UIOption.AllDialogs);
                    else FileSystem.CopyFile(src, dest, UIOption.AllDialogs);
                }

                undoRecords.Add(new UndoRecord { OriginalSource = src, NewDestination = dest });
                pastedCount++;
            }
            catch (Exception ex)
            {
                FocusFenceDialog.ShowMessage($"無法貼上：{ex.Message}", "FocusFence", true);
            }
        }

        if (undoRecords.Count > 0)
            UndoService.RecordMove(undoRecords);

        return pastedCount > 0;
    }

    public static bool DeleteItems(List<FileItem> items)
    {
        if (items.Count == 0) return false;

        if (!FocusFenceDialog.ShowConfirm($"確定要將 {items.Count} 個項目移至回收筒？", "FocusFence", destructive: true))
            return false;

        bool changed = false;
        foreach (var f in items)
        {
            try
            {
                if (f.IsDirectory)
                    FileSystem.DeleteDirectory(f.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else
                    FileSystem.DeleteFile(f.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                changed = true;
            }
            catch { }
        }

        return changed;
    }
}
