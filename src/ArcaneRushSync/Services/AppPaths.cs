using System.IO;

namespace ArcaneRushSync.Services;

public static class AppPaths
{
    public static readonly string Base = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArcaneRushSync");

    public static readonly string Logs = Path.Combine(Base, "logs");
    public static readonly string Reports = Path.Combine(Base, "reports");
    public static readonly string Updates = Path.Combine(Base, "updates");
    public static readonly string State = Path.Combine(Base, "state.json");
    public static readonly string ProxyBackup = Path.Combine(Base, "proxy-backup.json");
    public static readonly string ProxyCertificate = Path.Combine(Base, "proxy", "rootCert.pfx");
    public static readonly string ScannerConsent = Path.Combine(Base, "scanner-consent.txt");
    public static readonly string NumericReferenceMap = Path.Combine(Base, "numeric-reference-map.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Base);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Reports);
        Directory.CreateDirectory(Updates);
        Directory.CreateDirectory(Path.GetDirectoryName(ProxyCertificate)!);
    }
}
