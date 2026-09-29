using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ArcaneRushSync.Services;

public sealed record UpdateRelease(
    Version Version,
    string Tag,
    string DownloadUrl,
    string? Sha256Digest,
    string ReleaseUrl);

public sealed class UpdateService
{
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ArcaneRushSync/" + AppConfig.Version);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        return http;
    }

    public async Task<UpdateRelease?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var repository = AppConfig.GitHubRepository;
        if (!TrySplitRepository(repository, out var owner, out var repo))
            return null;

        var apiUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/latest";
        using var response = await Http.GetAsync(apiUrl, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString()?.Trim() ?? string.Empty;
        if (!TryParseReleaseVersion(tag, out var latestVersion))
            return null;

        if (!Version.TryParse(AppConfig.Version, out var currentVersion))
            currentVersion = new Version(0, 0, 0);

        if (latestVersion.CompareTo(currentVersion) <= 0)
            return null;

        string? downloadUrl = null;
        string? digest = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!string.Equals(name, AppConfig.UpdateAssetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            AppLog.Warn($"Release {tag} found but asset {AppConfig.UpdateAssetName} is missing.");
            return null;
        }

        var releaseUrl = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? string.Empty : string.Empty;
        return new UpdateRelease(latestVersion, tag, downloadUrl!, NormalizeSha256Digest(digest), releaseUrl);
    }

    public async Task<string> DownloadAndPrepareAsync(
        UpdateRelease update,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureDirectories();
        var updateRoot = Path.Combine(
            AppPaths.Updates,
            $"{update.Version}-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(updateRoot, AppConfig.UpdateAssetName);
        var payload = Path.Combine(updateRoot, "payload");
        Directory.CreateDirectory(updateRoot);
        Directory.CreateDirectory(payload);

        try
        {
            using var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                var buffer = new byte[128 * 1024];
                long readTotal = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    readTotal += read;
                    if (total is > 0)
                        progress?.Report(Math.Clamp((int)(readTotal * 100L / total.Value), 0, 100));
                }
            }

            if (!string.IsNullOrWhiteSpace(update.Sha256Digest))
            {
                var actual = await ComputeSha256Async(zipPath, cancellationToken);
                if (!string.Equals(actual, update.Sha256Digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("La vérification SHA-256 de la mise à jour GitHub a échoué. La mise à jour a été annulée.");
            }
            else
            {
                AppLog.Warn("GitHub release asset has no SHA-256 digest; update downloaded over HTTPS but digest verification was unavailable.");
            }

            ZipFile.ExtractToDirectory(zipPath, payload, overwriteFiles: true);
            var stagedExe = Path.Combine(payload, "ArcaneRushSync.exe");
            if (!File.Exists(stagedExe))
                throw new InvalidDataException($"L'archive de mise à jour ne contient pas ArcaneRushSync.exe à sa racine ({AppConfig.UpdateAssetName}).");

            progress?.Report(100);
            return payload;
        }
        catch
        {
            TryDeleteDirectory(updateRoot);
            throw;
        }
    }

    public static void StartApplyProcess(string payloadDirectory)
    {
        var stagedExe = Path.Combine(payloadDirectory, "ArcaneRushSync.exe");
        if (!File.Exists(stagedExe))
            throw new FileNotFoundException("L'exécutable de mise à jour est introuvable.", stagedExe);

        var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var updateRoot = Directory.GetParent(payloadDirectory)?.FullName ?? payloadDirectory;
        var psi = new ProcessStartInfo(stagedExe)
        {
            UseShellExecute = false,
            WorkingDirectory = payloadDirectory
        };
        psi.ArgumentList.Add("--apply-update");
        psi.ArgumentList.Add(payloadDirectory);
        psi.ArgumentList.Add(installDirectory);
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(updateRoot);

        Process.Start(psi) ?? throw new InvalidOperationException("Impossible de démarrer l'installation de la mise à jour.");
    }

    public static int ApplyUpdateAndRestart(string payloadDirectory, string installDirectory, int oldProcessId, string updateRoot)
    {
        try
        {
            WaitForProcessExit(oldProcessId, TimeSpan.FromSeconds(90));
            CopyDirectory(payloadDirectory, installDirectory);

            var targetExe = Path.Combine(installDirectory, "ArcaneRushSync.exe");
            if (!File.Exists(targetExe))
                throw new FileNotFoundException("ArcaneRushSync.exe est absent après la mise à jour.", targetExe);

            var psi = new ProcessStartInfo(targetExe)
            {
                UseShellExecute = false,
                WorkingDirectory = installDirectory
            };
            psi.ArgumentList.Add("--cleanup-update");
            psi.ArgumentList.Add(updateRoot);
            psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                AppPaths.EnsureDirectories();
                File.AppendAllText(Path.Combine(AppPaths.Logs, "update-error.log"),
                    $"[{DateTimeOffset.Now:O}] {ex}\n");
            }
            catch { }
            return 2;
        }
    }

    public static void CleanupStagingInBackground(string updateRoot, int updaterProcessId)
    {
        if (string.IsNullOrWhiteSpace(updateRoot)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200);
                WaitForProcessExit(updaterProcessId, TimeSpan.FromSeconds(30));
                for (var i = 0; i < 8; i++)
                {
                    if (TryDeleteDirectory(updateRoot)) return;
                    await Task.Delay(750);
                }
            }
            catch { }
        });
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyWithRetry(file, target);
        }
    }

    private static void CopyWithRetry(string source, string destination)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(350);
            }
        }
        throw new IOException($"Impossible de remplacer {Path.GetFileName(destination)} pendant la mise à jour.", last);
    }

    private static void WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                throw new TimeoutException("L'ancienne version ne s'est pas fermée à temps.");
        }
        catch (ArgumentException)
        {
            // Process is already gone.
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static bool TrySplitRepository(string repository, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(repository)) return false;
        var parts = repository.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        owner = parts[0];
        repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return owner.Length > 0 && repo.Length > 0;
    }

    private static bool TryParseReleaseVersion(string tag, out Version version)
    {
        var value = tag.Trim();
        if (value.StartsWith("sync-v", StringComparison.OrdinalIgnoreCase)) value = value[6..];
        else if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value[1..];
        var ok = Version.TryParse(value, out var parsed);
        version = parsed ?? new Version(0, 0, 0);
        return ok;
    }

    private static string? NormalizeSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var value = digest.Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value : null;
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return !Directory.Exists(path);
        }
        catch { return false; }
    }
}
