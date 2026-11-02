using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

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

    /// <summary>
    /// Asks the running game to close normally (same as clicking the window's X).
    /// The process is never killed: if the game refuses, the player closes it himself.
    /// </summary>
    public static async Task<bool> TryCloseRunningGameAsync(TimeSpan timeout)
    {
        var processes = Process.GetProcessesByName(AppConfig.GameProcessName);
        try
        {
            foreach (var process in processes)
            {
                try { process.CloseMainWindow(); } catch { }
            }
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning()) return true;
            await Task.Delay(400);
        }
        return !IsRunning();
    }

    /// <summary>
    /// Starts Arcane Rush. Steam is preferred (the game is a Steam title and Steam may refuse
    /// a direct launch); the executable found in the Steam library is the fallback.
    /// </summary>
    public static (bool Started, string Method) Launch()
    {
        if (IsSteamProtocolRegistered())
        {
            try
            {
                Process.Start(new ProcessStartInfo($"steam://rungameid/{AppConfig.SteamAppId}") { UseShellExecute = true })?.Dispose();
                AppLog.Info("Arcane Rush launch requested through Steam.");
                return (true, "steam");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Steam launch failed: " + ex.Message);
            }
        }

        var exe = FindGameExecutable();
        if (exe is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
                })?.Dispose();
                AppLog.Info("Arcane Rush launched directly: " + exe);
                return (true, "exe");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Direct game launch failed: " + ex.Message);
            }
        }

        return (false, "");
    }

    private static bool IsSteamProtocolRegistered()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"steam\shell\open\command");
            return key?.GetValue(null) is string command && !string.IsNullOrWhiteSpace(command);
        }
        catch { return false; }
    }

    public static string? FindGameExecutable()
    {
        try
        {
            foreach (var library in SteamLibraries())
            {
                var manifest = Path.Combine(library, "steamapps", $"appmanifest_{AppConfig.SteamAppId}.acf");
                if (!File.Exists(manifest)) continue;
                var match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
                if (!match.Success) continue;
                var dir = Path.Combine(library, "steamapps", "common", match.Groups[1].Value);
                var exe = Path.Combine(dir, AppConfig.GameProcessName + ".exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Steam library lookup failed: " + ex.Message);
        }
        return null;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string path && Directory.Exists(path))
                roots.Add(Path.GetFullPath(path));
        }
        catch { }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            roots.Add(Path.Combine(programFilesX86, "Steam"));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root) || !seen.Add(root)) continue;
            yield return root;

            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            string text;
            try { text = File.ReadAllText(vdf); } catch { continue; }
            foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            {
                var library = m.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(library) && seen.Add(library))
                    yield return library;
            }
        }
    }
}
