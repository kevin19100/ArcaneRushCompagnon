using System.Diagnostics;

namespace ArcaneRushSync.Services;

public static class GameLauncherService
{
    public static bool IsRunning() => TryGetRunningProcessId().HasValue;

    public static int? TryGetRunningProcessId()
    {
        var processes = Process.GetProcessesByName(AppConfig.GameProcessName);
        try
        {
            return processes
                .OrderBy(process => process.Id)
                .Select(process => (int?)process.Id)
                .FirstOrDefault();
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }
}
