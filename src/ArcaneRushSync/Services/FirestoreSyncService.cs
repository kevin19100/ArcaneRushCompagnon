using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArcaneRushSync.Models;

namespace ArcaneRushSync.Services;

public sealed record FirestoreSyncDiagnostic(
    DateTimeOffset? LastAttemptAt,
    bool Attempted,
    bool Success,
    int StatusCode,
    string Message,
    string ResponsePreview,
    string[] Fields,
    int OwnedIdsSent = 0,
    int OwnedIdsReturned = 0,
    bool Verified = false,
    string ReturnedPlayerName = "");

public sealed class FirestoreSyncService : IDisposable
{
    private readonly FirebaseAuthService _auth;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(18) };
    private readonly object _diagGate = new();
    private FirestoreSyncDiagnostic _diagnostic = new(null, false, false, 0, "Aucune tentative de synchronisation.", "", Array.Empty<string>());

    public FirestoreSyncService(FirebaseAuthService auth) => _auth = auth;

    public FirestoreSyncDiagnostic GetDiagnostic()
    {
        lock (_diagGate) return _diagnostic;
    }

    public async Task SyncAsync(GameSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var validDecks = snapshot.ValidDecks
            .Where(deck => System.Text.RegularExpressions.Regex.IsMatch(deck.MerchantId, "^SK_[0-9]+$", RegexOptions.IgnoreCase))
            .OrderBy(deck => deck.MerchantId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sendName = snapshot.HasVerifiedPlayerName;

        var fieldsAttempted = new List<string>
        {
            "ownedIds", "collectionSchemaVersion", "collectionUpdatedAt", "collectionSyncSource",
            "collectionSyncClient", "collectionSyncClientVersion"
        };
        if (sendName) fieldsAttempted.Insert(0, "gamePlayerName");
        if (validDecks.Length > 0)
        {
            fieldsAttempted.AddRange(new[] { "deckSyncSchemaVersion", "deckSyncSource", "deckSyncClientVersion", "deckSyncUpdatedAt" });
            foreach (var deck in validDecks)
            {
                fieldsAttempted.Add("equippedDecks." + deck.MerchantId.ToUpperInvariant());
                fieldsAttempted.Add("equippedCardIdsByDealer." + deck.MerchantId.ToUpperInvariant());
            }
        }
        var fieldsArray = fieldsAttempted.ToArray();

        if (!snapshot.IsCollectionReady)
        {
            var blocker = SyncBlocker(snapshot);
            SetDiagnostic(new(DateTimeOffset.UtcNow, false, false, 0, "Synchronisation bloquée : " + blocker, "", fieldsArray,
                snapshot.OwnedCardIds.Count, 0, false, ""));
            throw new InvalidOperationException("La collection exacte du jeu n'a pas encore été lue. Relance Arcane Rush depuis Arcane Rush Sync pour réessayer.");
        }

        var session = await _auth.EnsureSessionAsync(cancellationToken);
        var projectId = _auth.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            SetDiagnostic(new(DateTimeOffset.UtcNow, false, false, 0, "Projet Firebase introuvable.", "", fieldsArray,
                snapshot.OwnedCardIds.Count, 0, false, ""));
            throw new InvalidOperationException("Projet Firebase introuvable.");
        }

        var owned = snapshot.OwnedCardIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.StartsWith("SK_", StringComparison.OrdinalIgnoreCase))
            .Select(id => id.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var deckFields = new Dictionary<string, object?>();
        foreach (var deck in validDecks)
        {
            deckFields[deck.MerchantId.ToUpperInvariant()] = new
            {
                arrayValue = new
                {
                    values = deck.CardIds.Select(id => new { stringValue = id.ToUpperInvariant() }).ToArray()
                }
            };
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var fields = new Dictionary<string, object?>
        {
            ["ownedIds"] = new
            {
                arrayValue = new
                {
                    values = owned.Select(id => new { stringValue = id }).ToArray()
                }
            },
            ["collectionSchemaVersion"] = new { integerValue = "1" },
            ["collectionUpdatedAt"] = new { timestampValue = now },
            // The website (src/cloud.jsx) treats exactly this value as "confirmed by the game":
            // it then replaces the local collection instead of merging an old browser cache.
            ["collectionSyncSource"] = new { stringValue = "arcane-rush-addon" },
            ["collectionSyncClient"] = new { stringValue = "arcane-rush-sync" },
            ["collectionSyncClientVersion"] = new { stringValue = AppConfig.Version }
        };
        if (sendName)
            fields["gamePlayerName"] = new { stringValue = snapshot.PlayerName };
        if (validDecks.Length > 0)
        {
            // Two shapes: equippedDecks (historical) and equippedCardIdsByDealer, the one the
            // website's Collection page actually reads. Each merchant is masked individually so a
            // partial scan never erases a deck that was synced earlier.
            fields["equippedDecks"] = new { mapValue = new { fields = deckFields } };
            fields["equippedCardIdsByDealer"] = new { mapValue = new { fields = deckFields } };
            fields["deckSyncSchemaVersion"] = new { integerValue = "1" };
            fields["deckSyncSource"] = new { stringValue = "arcane-rush-addon" };
            fields["deckSyncClientVersion"] = new { stringValue = AppConfig.Version };
            fields["deckSyncUpdatedAt"] = new { timestampValue = now };
        }

        var masks = string.Join("&", fieldsArray.Select(k => "updateMask.fieldPaths=" + Uri.EscapeDataString(k)));
        var url = $"https://firestore.googleapis.com/v1/projects/{Uri.EscapeDataString(projectId)}/databases/(default)/documents/users/{Uri.EscapeDataString(session.Uid)}?{masks}";
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { fields }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.IdToken);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var preview = Sanitize(body);
            if (!response.IsSuccessStatusCode)
            {
                SetDiagnostic(new(DateTimeOffset.UtcNow, true, false, (int)response.StatusCode,
                    $"Firebase a refusé la synchronisation ({(int)response.StatusCode}).", preview, fieldsArray,
                    owned.Length, 0, false, ""));
                AppLog.Warn($"Firestore sync failed {(int)response.StatusCode}: {preview[..Math.Min(preview.Length, 300)]}");
                throw new InvalidOperationException($"Synchronisation refusée par Firebase ({(int)response.StatusCode}). Vérifie les règles Firestore du site.");
            }

            var returnedOwned = ReadReturnedOwnedIds(body);
            var returnedName = ReadReturnedStringField(body, "gamePlayerName");
            var sentSet = owned.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var returnedSet = returnedOwned.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var collectionVerified = sentSet.SetEquals(returnedSet);
            var nameVerified = !sendName || string.Equals(returnedName, snapshot.PlayerName, StringComparison.Ordinal);
            var verified = collectionVerified && nameVerified;

            if (!verified)
            {
                var why = !collectionVerified
                    ? $"Firebase a répondu 200 mais ownedIds ne correspond pas à la collection envoyée ({returnedOwned.Count}/{owned.Length})."
                    : $"Firebase a répondu 200 mais le pseudo retourné est '{returnedName}' au lieu de '{snapshot.PlayerName}'.";
                SetDiagnostic(new(DateTimeOffset.UtcNow, true, false, (int)response.StatusCode,
                    why, preview, fieldsArray, owned.Length, returnedOwned.Count, false, returnedName));
                AppLog.Warn("Firestore post-write verification failed: " + why);
                throw new InvalidOperationException("Firebase a accepté la requête, mais la vérification après écriture a échoué. Aucun faux message « synchronisé » n'est affiché.");
            }

            SetDiagnostic(new(DateTimeOffset.UtcNow, true, true, (int)response.StatusCode,
                $"Synchronisation Firestore vérifiée : {owned.Length} cartes + {validDecks.Length} decks" + (sendName ? " + pseudo." : "."), preview, fieldsArray,
                owned.Length, returnedOwned.Count, true, returnedName));
            AppLog.Info($"Firestore sync verified. player={returnedName}, ownedIds={returnedOwned.Count}, decks={snapshot.Decks.Count}.");
        }
        catch (OperationCanceledException)
        {
            SetDiagnostic(new(DateTimeOffset.UtcNow, true, false, 0, "Synchronisation annulée ou délai dépassé.", "", fieldsArray,
                owned.Length, 0, false, ""));
            throw;
        }
        catch (HttpRequestException ex)
        {
            SetDiagnostic(new(DateTimeOffset.UtcNow, true, false, 0, "Erreur réseau Firestore : " + ex.Message, "", fieldsArray,
                owned.Length, 0, false, ""));
            throw;
        }
    }

    private static string SyncBlocker(GameSnapshot snapshot)
    {
        if (!snapshot.CollectionSyncSafe || snapshot.OwnedCardIds.Count == 0
            || !snapshot.CollectionMethod.StartsWith("bootstrap-exact-ascii", StringComparison.Ordinal))
            return "collection Arcane Rush exacte non validée";
        return "scan incomplet";
    }

    private static IReadOnlyList<string> ReadReturnedOwnedIds(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("fields", out var fields)
                || !fields.TryGetProperty("ownedIds", out var owned)
                || !owned.TryGetProperty("arrayValue", out var array)
                || !array.TryGetProperty("values", out var values)
                || values.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return values.EnumerateArray()
                .Select(row => row.TryGetProperty("stringValue", out var value) ? value.GetString() ?? "" : "")
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static string ReadReturnedStringField(string body, string fieldName)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("fields", out var fields)
                && fields.TryGetProperty(fieldName, out var field)
                && field.TryGetProperty("stringValue", out var value))
                return value.GetString() ?? "";
        }
        catch { }
        return "";
    }

    private void SetDiagnostic(FirestoreSyncDiagnostic diagnostic)
    {
        lock (_diagGate) _diagnostic = diagnostic;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Length > 1800 ? value[..1800] : value;
        text = Regex.Replace(text,
            "(?i)(idToken|refreshToken|access_token|authorization|password)\\s*[\"':=]+\\s*[\"']?[^\"'\\s,}]+",
            "$1=<redacted>");
        return text;
    }

    public void Dispose() => _http.Dispose();
}
