using System.Diagnostics;

namespace ArcaneRushSync.Services;

public static class GameLauncherService
{
    public static bool IsRunning() =>
        Process.GetProcessesByName(AppConfig.GameProcessName).Length > 0;
}
