using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcaneRushSync.Models;

namespace ArcaneRushSync.Services;

public sealed class FirebaseAuthService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly SecureSessionStore _store;
    private string _siteUrl = AppConfig.SiteUrl;
    private string _apiKey = "";
    private string _projectId = "";

    public FirebaseSession? Session { get; private set; }
    public string ProjectId => _projectId;
    public string SiteUrl => _siteUrl;

    public FirebaseAuthService(SecureSessionStore store) => _store = store;

    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        var saved = _store.Load();
        if (saved is null) return false;
        try
        {
            await DiscoverAsync(saved.SiteUrl, cancellationToken);
            Session = await RefreshAsync(saved.RefreshToken, saved.Email, saved.Uid, saved.DisplayName, cancellationToken);
            var profile = await LookupProfileAsync(cancellationToken);
            Session = Session with
            {
                Uid = profile.Uid,
                Email = profile.Email,
                DisplayName = profile.DisplayName
            };
            _store.Save(_siteUrl, Session);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Session restore failed: " + ex.Message);
            Session = null;
            return false;
        }
    }

    public async Task<FirebaseSession> LoginEmailAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Renseigne ton adresse e-mail et ton mot de passe.");

        await DiscoverAsync(AppConfig.SiteUrl, cancellationToken);
        var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={Uri.EscapeDataString(_apiKey)}";
        var json = await PostJsonAsync(url, new
        {
            email = email.Trim(),
            password,
            returnSecureToken = true
        }, cancellationToken);

        Session = new FirebaseSession(
            json.GetProperty("localId").GetString() ?? "",
            json.TryGetProperty("email", out var em) ? em.GetString() ?? email.Trim() : email.Trim(),
            json.GetProperty("idToken").GetString() ?? "",
            json.GetProperty("refreshToken").GetString() ?? "",
            DateTimeOffset.UtcNow.AddSeconds(ParseInt(json, "expiresIn", 3600) - 90),
            json.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "");

        _store.Save(_siteUrl, Session);
        return Session;
    }

    public async Task<FirebaseSession> LoginGoogleAsync(Action<string>? status = null, CancellationToken cancellationToken = default)
    {
        await DiscoverAsync(AppConfig.SiteUrl, cancellationToken);
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

        // TcpListener is intentionally used instead of HttpListener/HTTP.sys:
        // it stays on 127.0.0.1, needs no URL ACL and no administrator rights.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var url = $"{_siteUrl}/addon-connect?port={port}&state={Uri.EscapeDataString(state)}&v={Uri.EscapeDataString(AppConfig.Version)}&client=sync";
        status?.Invoke("Navigateur ouvert · connexion Google en attente…");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        LoopbackRequest callback;
        using (var client = await listener.AcceptTcpClientAsync(timeoutCts.Token))
        using (var stream = client.GetStream())
        {
            callback = await ReadLoopbackRequestAsync(stream, timeoutCts.Token);

            if (!string.Equals(callback.Method, "POST", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(callback.Path, "/callback", StringComparison.Ordinal))
            {
                await SendCallbackResponse(stream, 405, "Connexion refusée", timeoutCts.Token);
                throw new InvalidOperationException("Callback Google invalide.");
            }

            if (!callback.Form.TryGetValue("state", out var gotState)
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(gotState),
                    Encoding.UTF8.GetBytes(state)))
            {
                await SendCallbackResponse(stream, 403, "Code de sécurité invalide", timeoutCts.Token);
                throw new InvalidOperationException("Code de sécurité Google invalide.");
            }

            if (!callback.Form.TryGetValue("refreshToken", out var refreshToken)
                || string.IsNullOrWhiteSpace(refreshToken))
            {
                await SendCallbackResponse(stream, 400, "Connexion incomplète", timeoutCts.Token);
                throw new InvalidOperationException("Aucun jeton Firebase reçu.");
            }

            await SendCallbackResponse(stream, 200, "Connexion réussie. Tu peux revenir dans Arcane Rush Sync.", timeoutCts.Token);
            status?.Invoke("Validation du compte Google…");

            Session = await RefreshAsync(
                refreshToken,
                callback.Form.GetValueOrDefault("email", ""),
                callback.Form.GetValueOrDefault("uid", ""),
                callback.Form.GetValueOrDefault("displayName", ""),
                cancellationToken);
        }

        var profile = await LookupProfileAsync(cancellationToken);
        if (profile.Providers.Count > 0 && !profile.Providers.Contains("google.com"))
            throw new InvalidOperationException("Ce compte Firebase n'est pas relié à Google.");

        Session = Session with { Uid = profile.Uid, Email = profile.Email, DisplayName = profile.DisplayName };
        _store.Save(_siteUrl, Session);
        return Session;
    }

    public async Task<FirebaseSession> EnsureSessionAsync(CancellationToken cancellationToken = default)
    {
        if (Session is null) throw new InvalidOperationException("Connecte-toi d'abord au site.");
        if (DateTimeOffset.UtcNow >= Session.ExpiresAt)
        {
            Session = await RefreshAsync(Session.RefreshToken, Session.Email, Session.Uid, Session.DisplayName, cancellationToken);
            _store.Save(_siteUrl, Session);
        }
        return Session;
    }

    public void Logout()
    {
        Session = null;
        _store.Clear();
    }

    private async Task DiscoverAsync(string siteUrl, CancellationToken cancellationToken)
    {
        _siteUrl = new Uri(siteUrl).GetLeftPart(UriPartial.Authority).TrimEnd('/');
        using var response = await _http.GetAsync(_siteUrl + "/__/firebase/init.json", cancellationToken);
        await EnsureSuccessAsync(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        _apiKey = doc.RootElement.GetProperty("apiKey").GetString() ?? "";
        _projectId = doc.RootElement.GetProperty("projectId").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_projectId))
            throw new InvalidOperationException("Configuration Firebase du site introuvable.");
    }

    private async Task<FirebaseSession> RefreshAsync(string refreshToken, string email, string uid, string displayName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey)) await DiscoverAsync(_siteUrl, cancellationToken);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });
        using var response = await _http.PostAsync(
            $"https://securetoken.googleapis.com/v1/token?key={Uri.EscapeDataString(_apiKey)}",
            content,
            cancellationToken);
        await EnsureSuccessAsync(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        return new FirebaseSession(
            root.TryGetProperty("user_id", out var u) ? u.GetString() ?? uid : uid,
            email,
            root.GetProperty("id_token").GetString() ?? "",
            root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? refreshToken : refreshToken,
            DateTimeOffset.UtcNow.AddSeconds(ParseInt(root, "expires_in", 3600) - 90),
            displayName);
    }

    private async Task<(string Uid, string Email, string DisplayName, HashSet<string> Providers)> LookupProfileAsync(CancellationToken cancellationToken)
    {
        var session = Session ?? throw new InvalidOperationException("Session Firebase absente.");
        var json = await PostJsonAsync(
            $"https://identitytoolkit.googleapis.com/v1/accounts:lookup?key={Uri.EscapeDataString(_apiKey)}",
            new { idToken = session.IdToken },
            cancellationToken);
        var users = json.GetProperty("users");
        if (users.GetArrayLength() == 0) throw new InvalidOperationException("Compte Firebase introuvable.");
        var user = users[0];
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (user.TryGetProperty("providerUserInfo", out var rows))
        {
            foreach (var row in rows.EnumerateArray())
                if (row.TryGetProperty("providerId", out var p) && !string.IsNullOrWhiteSpace(p.GetString()))
                    providers.Add(p.GetString()!);
        }
        return (
            user.TryGetProperty("localId", out var id) ? id.GetString() ?? session.Uid : session.Uid,
            user.TryGetProperty("email", out var em) ? em.GetString() ?? session.Email : session.Email,
            user.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? session.DisplayName : session.DisplayName,
            providers);
    }

    private async Task<JsonElement> PostJsonAsync(string url, object payload, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content, cancellationToken);
        await EnsureSuccessAsync(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return doc.RootElement.Clone();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        var message = response.StatusCode switch
        {
            HttpStatusCode.BadRequest when body.Contains("INVALID_LOGIN_CREDENTIALS", StringComparison.OrdinalIgnoreCase) => "E-mail ou mot de passe incorrect.",
            HttpStatusCode.BadRequest when body.Contains("EMAIL_NOT_FOUND", StringComparison.OrdinalIgnoreCase) => "Compte introuvable.",
            HttpStatusCode.BadRequest when body.Contains("INVALID_PASSWORD", StringComparison.OrdinalIgnoreCase) => "Mot de passe incorrect.",
            HttpStatusCode.TooManyRequests => "Trop de tentatives. Réessaie plus tard.",
            _ => $"Erreur Firebase ({(int)response.StatusCode})."
        };
        throw new InvalidOperationException(message);
    }

    private static int ParseInt(JsonElement root, string property, int fallback) =>
        root.TryGetProperty(property, out var v) && int.TryParse(v.GetString(), out var n) ? n : fallback;

    private sealed record LoopbackRequest(string Method, string Path, Dictionary<string, string> Form);

    private static async Task<LoopbackRequest> ReadLoopbackRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken) ?? "";
        var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return new LoopbackRequest("", "", new Dictionary<string, string>());

        var contentLength = 0;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken)))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                _ = int.TryParse(value, out contentLength);
        }

        if (contentLength < 0 || contentLength > 64 * 1024)
            throw new InvalidOperationException("Callback Google trop volumineux.");

        var body = "";
        if (contentLength > 0)
        {
            var chars = new char[contentLength];
            var read = 0;
            while (read < chars.Length)
            {
                var n = await reader.ReadAsync(chars.AsMemory(read, chars.Length - read), cancellationToken);
                if (n == 0) break;
                read += n;
            }
            body = new string(chars, 0, read);
        }

        return new LoopbackRequest(parts[0], new Uri("http://127.0.0.1" + parts[1]).AbsolutePath, ParseForm(body));
    }

    private static Dictionary<string, string> ParseForm(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split('=', 2);
            var key = Uri.UnescapeDataString(bits[0].Replace('+', ' '));
            var value = bits.Length > 1 ? Uri.UnescapeDataString(bits[1].Replace('+', ' ')) : "";
            result[key] = value;
        }
        return result;
    }

    private static async Task SendCallbackResponse(NetworkStream stream, int statusCode, string message, CancellationToken cancellationToken)
    {
        var body = $"<!doctype html><html lang='fr'><meta charset='utf-8'><title>Arcane Rush Sync</title><body style='font-family:Segoe UI;background:#0c0d0f;color:#f5f1e8;padding:48px'><h1>{WebUtility.HtmlEncode(message)}</h1><p>Tu peux fermer cet onglet.</p></body></html>";
        var payload = Encoding.UTF8.GetBytes(body);
        var reason = statusCode switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 405 => "Method Not Allowed", _ => "Error" };
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n" +
            $"Content-Length: {payload.Length}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public void Dispose() => _http.Dispose();
}
