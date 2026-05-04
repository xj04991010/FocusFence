using System;
using System.Collections.Generic;
using System.IO;

namespace FocusFence.Services;

public class UndoRecord
{
    public string OriginalSource { get; set; } = "";
    public string NewDestination { get; set; } = "";
}

public static class UndoService
{
    private static readonly Stack<List<UndoRecord>> _history = new();
    private static readonly object _lock = new();

    public static event Action? UndoExecuted;

    public static void RecordMove(List<UndoRecord> records)
    {
        if (records.Count > 0)
        {
            lock (_lock)
            {
                _history.Push(records);
            }
        }
    }
    
    public static bool CanUndo 
    {
        get
        {
            lock (_lock) return _history.Count > 0;
        }
    }
    
    public static int Undo()
    {
        List<UndoRecord>? batch = null;
        lock (_lock)
        {
            if (_history.Count == 0) return 0;
            batch = _history.Pop();
        }
        
        int count = 0;
        foreach (var r in batch)
        {
            try
            {
                // To undo, we move from NewDestination back to OriginalSource
                if (File.Exists(r.NewDestination) && !File.Exists(r.OriginalSource))
                {
                    File.Move(r.NewDestination, r.OriginalSource);
                    count++;
                }
                else if (Directory.Exists(r.NewDestination) && !Directory.Exists(r.OriginalSource))
                {
                    Directory.Move(r.NewDestination, r.OriginalSource);
                    count++;
                }
            }
            catch { /* Ignore locks or files that were deleted since move */ }
        }
        
        if (count > 0)
        {
            UndoExecuted?.Invoke();
        }
        return count;
    }
}
