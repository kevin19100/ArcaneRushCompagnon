using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace ArcaneRushSync.Services;

/// <summary>
/// Built-in replacement for the old REPARER_RESEAU_WINDOWS.bat + PowerShell script.
/// Shipping a .bat that runs PowerShell with "-ExecutionPolicy Bypass" is a classic
/// antivirus heuristic, so the repair now lives inside the signed/installed executable:
///   ArcaneRushSync.exe --repair-network
/// or the "Réparer le réseau" button.
/// </summary>
public static class NetworkRepairService
{
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string LocalCaSubject = "Arcane Rush Sync Local CA";

    public sealed record RepairResult(bool ProxyRestored, bool ProxyDisabled, int CertificatesRemoved);

    public static RepairResult Repair(bool removeCertificates)
    {
        var restored = false;
        var disabled = false;

        // 1. Exact restore from the backup written before each scan.
        if (File.Exists(AppPaths.ProxyBackup))
        {
            ProxyStateGuard.RestoreIfPending();
            restored = !File.Exists(AppPaths.ProxyBackup);
        }

        // 2. No backup but Windows still points at a proxy written in the exact format
        //    Titanium uses ("http=127.0.0.1:N;https=127.0.0.1:N"): leftover of a crashed scan.
        //    Other local proxies (Clash, v2ray, Fiddler, VPN clients…) use other formats and are left alone.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key is not null
                && key.GetValue("ProxyEnable") is int enabled && enabled == 1
                && key.GetValue("ProxyServer") is string server
                && LooksLikeOurProxy(server)
                && !GameScannerIsRunningInThisProcess)
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                ProxyStateGuard.NotifySettingsChanged();
                disabled = true;
                AppLog.Info("Network repair: leftover loopback proxy disabled (" + server + ").");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Network repair proxy check failed: " + ex.Message);
        }

        var removed = 0;
        if (removeCertificates)
        {
            foreach (var storeName in new[] { StoreName.Root, StoreName.My })
            {
                try
                {
                    using var store = new X509Store(storeName, StoreLocation.CurrentUser);
                    store.Open(OpenFlags.ReadWrite);
                    var matches = store.Certificates
                        .Where(c => c.Subject.Contains("CN=" + LocalCaSubject, StringComparison.OrdinalIgnoreCase)
                                    && c.Issuer.Contains("Arcane Rush Sync", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    foreach (var cert in matches)
                    {
                        try
                        {
                            // Removing from Root shows a Windows confirmation dialog; that is expected.
                            store.Remove(cert);
                            removed++;
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn("Certificate removal refused/failed: " + ex.Message);
                        }
                        finally
                        {
                            cert.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Certificate store {storeName} not accessible: " + ex.Message);
                }
            }

            try
            {
                if (File.Exists(AppPaths.ProxyCertificate)) File.Delete(AppPaths.ProxyCertificate);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Local certificate file deletion failed: " + ex.Message);
            }

            ScannerConsentStore.Revoke();
        }

        AppLog.Info($"Network repair done. restored={restored}, disabled={disabled}, certificatesRemoved={removed}.");
        return new RepairResult(restored, disabled, removed);
    }

    private static bool LooksLikeOurProxy(string server)
    {
        // Titanium writes "http=HOST:PORT;https=HOST:PORT" (keeping any other protocol entries).
        string? http = null, https = null;
        foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var scheme = part[..eq].Trim().ToLowerInvariant();
            var target = part[(eq + 1)..].Trim().ToLowerInvariant();
            if (scheme == "http") http = target;
            else if (scheme == "https") https = target;
        }
        if (http is null || https is null || http != https) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(http, @"^(127\.0\.0\.1|localhost):\d{2,5}$");
    }

    /// <summary>Set by the scanner while its proxy is live, so a repair never cuts an active scan.</summary>
    public static volatile bool GameScannerIsRunningInThisProcess;
}
