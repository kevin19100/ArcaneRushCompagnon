using System.IO;

namespace ArcaneRushSync.Services;

public static class AppLog
{
    private static readonly object Gate = new();
    private const long MaxLogBytes = 1_500_000;

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var path = Path.Combine(AppPaths.Logs, "app.log");
            lock (Gate)
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                {
                    var previous = Path.Combine(AppPaths.Logs, "app.previous.log");
                    try { File.Move(path, previous, overwrite: true); } catch { }
                }

                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
                if (ex is not null)
                    line += $" | {ex.GetType().Name}: {ex.Message}";
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never break the launcher. Logs never contain packet bodies.
        }
    }
}
