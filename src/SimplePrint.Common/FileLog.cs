using System.Text;

namespace SimplePrint.Common;

public sealed class FileLog
{
    private readonly string _path;
    private readonly object _sync = new();
    public FileLog(string path) => _path = path;

    public void Info(string message) => Write("INFO", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private void Write(string level, string message)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                RotateIfNeeded();

                // Mehrere Komponenten des Dienstes schreiben in dieselbe Logdatei.
                // Mit geteiltem Zugriff gehen dabei keine Zeilen verloren.
                using var stream = new FileStream(
                    _path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < 5 * 1024 * 1024) return;
        for (var i = 3; i >= 1; i--)
        {
            var src = _path + "." + i;
            var dst = _path + "." + (i + 1);
            if (File.Exists(src)) File.Move(src, dst, true);
        }
        File.Move(_path, _path + ".1", true);
    }
}
