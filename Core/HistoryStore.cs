using System.IO;

namespace OverByte;

/// <summary>Most-recently-used list of files/folders, persisted as one path per line.</summary>
internal sealed class HistoryStore
{
    public const int MaxHistory = 5;

    readonly string _file;
    readonly StringComparison _cmp;
    readonly object _lock = new();

    public HistoryStore(string file)
    {
        _file = file;
        _cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    public List<string> Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_file))
                    return File.ReadAllLines(_file).Where(l => l.Length > 0).Take(MaxHistory).ToList();
            }
            catch { }
            return new();
        }
    }

    public void Add(string path)
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_file);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var history = Load();
                history.RemoveAll(h => h.Equals(path, _cmp));
                history.Insert(0, path);
                if (history.Count > MaxHistory) history = history.Take(MaxHistory).ToList();
                // write-then-rename so a crash can't leave a truncated history file
                var tmp = _file + ".tmp";
                File.WriteAllLines(tmp, history);
                File.Move(tmp, _file, overwrite: true);
            }
            catch { }
        }
    }
}
