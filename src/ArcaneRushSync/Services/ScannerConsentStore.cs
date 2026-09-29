namespace ArcaneRushSync.Services;

public static class ScannerConsentStore
{
    public static bool HasConsent
    {
        get
        {
            try { return File.Exists(AppPaths.ScannerConsent) && File.ReadAllText(AppPaths.ScannerConsent).Trim() == "1"; }
            catch { return false; }
        }
    }

    public static void Grant()
    {
        AppPaths.EnsureDirectories();
        File.WriteAllText(AppPaths.ScannerConsent, "1");
    }

    public static void Revoke()
    {
        try { if (File.Exists(AppPaths.ScannerConsent)) File.Delete(AppPaths.ScannerConsent); } catch { }
    }
}
