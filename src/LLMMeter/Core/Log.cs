using System.IO;
using System.Text;

namespace LLMMeter.Core;

/// <summary>
/// Minimal diagnostic logging, OFF by default. When enabled writes
/// LLMMeter.log beside the EXE with size-capped rotation.
/// Never log prompt/completion text or secrets — callers pass short messages.
/// </summary>
public static class Log
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Lock = new();
    private static string? _path;
    private static bool _enabled;

    public static bool Enabled => _enabled;

    public static void Enable(string? path = null)
    {
        lock (Lock)
        {
            _path = path ?? System.IO.Path.Combine(AppContext.BaseDirectory, "LLMMeter.log");
            _enabled = true;
            try
            {
                File.AppendAllText(_path, $"--- LLMMeter started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} ---\r\n");
                RotateIfNeeded();
            }
            catch
            {
                _enabled = false; // unwritable dir: stay silent rather than crash
            }
        }
    }

    public static void Disable()
    {
        lock (Lock) _enabled = false;
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        if (!_enabled) return;
        try
        {
            lock (Lock)
            {
                var sb = new StringBuilder(64)
                    .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" ").Append(level).Append(" ").AppendLine(message);
                File.AppendAllText(_path!, sb.ToString());
                RotateIfNeeded();
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_path!);
            if (fi.Exists && fi.Length > MaxBytes)
            {
                var old = _path + ".old";
                File.Copy(_path!, old, overwrite: true);
                File.Delete(_path!);
            }
        }
        catch { }
    }
}
