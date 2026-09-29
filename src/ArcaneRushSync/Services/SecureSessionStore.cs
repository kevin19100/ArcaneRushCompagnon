using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArcaneRushSync.Services;

public sealed class SecureSessionStore
{
    private sealed class StoredState
    {
        public int Version { get; set; } = 1;
        public string SiteUrl { get; set; } = AppConfig.SiteUrl;
        public string Uid { get; set; } = "";
        public string Email { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string RefreshTokenProtected { get; set; } = "";
    }

    public sealed record RestoredSessionInfo(string SiteUrl, string Uid, string Email, string DisplayName, string RefreshToken);

    public void Save(string siteUrl, Models.FirebaseSession session)
    {
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(session.RefreshToken),
            optionalEntropy: Encoding.UTF8.GetBytes("ArcaneRushSync-v1"),
            scope: DataProtectionScope.CurrentUser);

        var state = new StoredState
        {
            SiteUrl = siteUrl,
            Uid = session.Uid,
            Email = session.Email,
            DisplayName = session.DisplayName,
            RefreshTokenProtected = Convert.ToBase64String(protectedBytes)
        };

        var tmp = AppPaths.State + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, AppPaths.State, overwrite: true);
    }

    public RestoredSessionInfo? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.State)) return null;
            var state = JsonSerializer.Deserialize<StoredState>(File.ReadAllText(AppPaths.State));
            if (state is null || string.IsNullOrWhiteSpace(state.RefreshTokenProtected)) return null;

            var raw = ProtectedData.Unprotect(
                Convert.FromBase64String(state.RefreshTokenProtected),
                optionalEntropy: Encoding.UTF8.GetBytes("ArcaneRushSync-v1"),
                scope: DataProtectionScope.CurrentUser);

            return new RestoredSessionInfo(
                string.IsNullOrWhiteSpace(state.SiteUrl) ? AppConfig.SiteUrl : state.SiteUrl,
                state.Uid,
                state.Email,
                state.DisplayName,
                Encoding.UTF8.GetString(raw));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Stored session could not be restored: " + ex.Message);
            return null;
        }
    }

    public void Clear()
    {
        try { if (File.Exists(AppPaths.State)) File.Delete(AppPaths.State); } catch { }
    }
}
