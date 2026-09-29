using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ArcaneRushSync.Services;

public static class ProxyStateGuard
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private static readonly string[] Names = { "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL", "AutoDetect" };

    private sealed class Backup
    {
        public Dictionary<string, JsonElement?> Values { get; set; } = new();
    }

    public static bool Capture()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            var map = new Dictionary<string, object?>();
            foreach (var name in Names)
                map[name] = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var tmp = AppPaths.ProxyBackup + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, AppPaths.ProxyBackup, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Proxy backup failed: " + ex.Message);
            return false;
        }
    }

    public static void RestoreIfPending()
    {
        if (!File.Exists(AppPaths.ProxyBackup)) return;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(AppPaths.ProxyBackup))
                      ?? new Dictionary<string, JsonElement>();
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key is null) return;
            foreach (var name in Names)
            {
                if (!raw.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
                {
                    try { key.DeleteValue(name, throwOnMissingValue: false); } catch { }
                    continue;
                }
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                    key.SetValue(name, number, RegistryValueKind.DWord);
                else if (value.ValueKind == JsonValueKind.String)
                    key.SetValue(name, value.GetString() ?? "", RegistryValueKind.String);
            }
            RefreshInternetSettings();
            File.Delete(AppPaths.ProxyBackup);
            AppLog.Info("Windows proxy settings restored from safety backup.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Proxy restore failed", ex);
        }
    }

    public static void ClearBackup()
    {
        try { if (File.Exists(AppPaths.ProxyBackup)) File.Delete(AppPaths.ProxyBackup); } catch { }
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private static void RefreshInternetSettings()
    {
        try
        {
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // SETTINGS_CHANGED
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // REFRESH
        }
        catch { }
    }
}
