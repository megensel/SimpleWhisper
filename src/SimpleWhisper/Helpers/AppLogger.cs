using System.Diagnostics;
using System.IO;

namespace SimpleWhisper.Helpers;

/// <summary>
/// Simple file-based logger that writes to %AppData%/SimpleWhisper/simplewhisper.log.
/// Thread-safe, auto-creates directory, keeps last 500KB of logs.
/// </summary>
public static class AppLogger
{
    private static readonly object _lock = new();
    private static readonly string _logPath;
    private const long MaxLogSize = 512 * 1024; // 500KB

    static AppLogger()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string logDir = Path.Combine(appData, "SimpleWhisper");
        Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, "simplewhisper.log");
    }

    public static void Log(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        Debug.WriteLine(line);

        try
        {
            lock (_lock)
            {
                // Rotate if too large
                if (File.Exists(_logPath))
                {
                    var info = new FileInfo(_logPath);
                    if (info.Length > MaxLogSize)
                    {
                        string backup = _logPath + ".old";
                        File.Copy(_logPath, backup, overwrite: true);
                        File.WriteAllText(_logPath, string.Empty);
                    }
                }

                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            // Logging must never crash the app. Write to Trace (not Debug) for Release-build
            // visibility, and avoid calling AppLogger again to prevent recursion.
            System.Diagnostics.Trace.WriteLine($"[AppLogger] Failed to write log: {ex.Message}");
        }
    }

    public static void Log(string message, Exception ex)
    {
        Log($"{message}: {ex.GetType().Name}: {ex.Message}");
    }
}
