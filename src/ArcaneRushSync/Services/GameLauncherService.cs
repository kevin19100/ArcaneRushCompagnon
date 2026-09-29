using System.Diagnostics;

namespace ArcaneRushSync.Services;

public static class GameLauncherService
{
    public static bool IsRunning() => Process.GetProcessesByName(AppConfig.GameProcessName).Length > 0;

    public static void Launch()
    {
        Process.Start(new ProcessStartInfo($"steam://rungameid/{AppConfig.SteamAppId}")
        {
            UseShellExecute = true
        });
    }
}
