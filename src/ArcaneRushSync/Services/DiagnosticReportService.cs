using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ArcaneRushSync.Models;

namespace ArcaneRushSync.Services;

public static class DiagnosticReportService
{
    public static string Generate(
        GameScannerService scanner,
        FirestoreSyncService sync,
        FirebaseSession? session)
    {
        AppPaths.EnsureDirectories();
        Directory.CreateDirectory(AppPaths.Reports);

        var now = DateTimeOffset.Now;
        var stamp = now.ToString("yyyyMMdd-HHmmss");
        var temp = Path.Combine(AppPaths.Reports, $"report-{stamp}");
        Directory.CreateDirectory(temp);

        var snapshot = scanner.Snapshot;
        var traffic = scanner.GetTrafficDiagnostics();
        var identityEvidence = scanner.GetIdentityDiagnostics();
        var endpointSummary = traffic
            .GroupBy(x => new { x.Direction, x.Method, x.Path })
            .Select(g => new
            {
                g.Key.Direction,
                g.Key.Method,
                g.Key.Path,
                Count = g.Count(),
                BodyBytes = g.Sum(x => (long)x.BodyBytes),
                Statuses = g.Select(x => x.StatusCode).Where(x => x != 0).Distinct().OrderBy(x => x).ToArray(),
                StrongNames = g.SelectMany(x => x.StrongNameCandidates).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray()
            })
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Direction, StringComparer.Ordinal)
            .ToArray();

        var possibleNames = traffic
            .SelectMany(row => row.NameLikeStrings.Select(name => new { name, row.Direction, row.Path }))
            .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Name = g.Key,
                Count = g.Count(),
                RequestHits = g.Count(x => string.Equals(x.Direction, "request", StringComparison.OrdinalIgnoreCase)),
                ResponseHits = g.Count(x => string.Equals(x.Direction, "response", StringComparison.OrdinalIgnoreCase)),
                Paths = g.Select(x => x.Path).Distinct(StringComparer.Ordinal).OrderBy(x => x).Take(12).ToArray(),
                Strong = traffic.Any(row => row.StrongNameCandidates.Contains(g.Key, StringComparer.OrdinalIgnoreCase))
            })
            .OrderByDescending(x => x.Strong)
            .ThenByDescending(x => x.RequestHits)
            .ThenByDescending(x => x.Count)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(250)
            .ToArray();

        var report = new
        {
            reportVersion = 6,
            clientVersion = AppConfig.Version,
            generatedAt = now,
            privacy = new
            {
                rawBodiesSaved = false,
                authTokensSaved = false,
                passwordsSaved = false,
                note = "Le rapport contient seulement des métadonnées réseau, des candidats pseudo/chaînes courtes, des IDs de cartes Arcane Rush et l'état de détection. Aucun corps réseau brut n'est conservé."
            },
            account = new
            {
                connected = session is not null,
                email = session?.Email ?? "",
                displayName = session?.DisplayName ?? ""
            },
            scan = new
            {
                scannerRunning = scanner.IsRunning,
                playerName = snapshot.PlayerName,
                playerNameConfidence = snapshot.PlayerNameConfidence,
                playerNameSource = snapshot.PlayerNameSource,
                playerNameMissing = string.IsNullOrWhiteSpace(snapshot.PlayerName) || snapshot.PlayerNameConfidence < 75,
                collectionCount = snapshot.OwnedCardIds.Count,
                collectionSyncSafe = snapshot.CollectionSyncSafe,
                collectionConfidence = snapshot.CollectionConfidence,
                collectionMethod = snapshot.CollectionMethod,
                collectionAudit = new
                {
                    asciiCount = snapshot.CollectionAsciiCount,
                    noiseCount = snapshot.CollectionNoiseCount,
                    numericOnlyCount = snapshot.CollectionNumericOnlyCount,
                    unresolvedRecords = snapshot.CollectionUnresolvedRecords,
                    candidateRecords = snapshot.CollectionCandidateRecords,
                    numericOnlyIds = snapshot.CollectionNumericOnlyIds,
                    inferredIds = snapshot.CollectionInferredIds,
                    noiseIds = snapshot.CollectionNoiseIds,
                    rejectedNumericIds = snapshot.CollectionRejectedNumericIds,
                    deckRecoveredIds = snapshot.CollectionDeckRecoveredIds,
                    ownedCardIds = snapshot.OwnedCardIds
                },
                detectedDecks = snapshot.Decks.Count,
                expectedDecks = AppConfig.ExpectedDeckCount,
                complete = snapshot.IsComplete(AppConfig.ExpectedDeckCount),
                syncBlocker = snapshot.PlayerNameConfidence < 75 || string.IsNullOrWhiteSpace(snapshot.PlayerName)
                    ? "pseudo Arcane Rush non vérifié"
                    : !snapshot.CollectionSyncSafe || snapshot.OwnedCardIds.Count == 0
                        ? "collection Arcane Rush non validée"
                        : snapshot.Decks.Count < AppConfig.ExpectedDeckCount
                            ? $"{AppConfig.ExpectedDeckCount - snapshot.Decks.Count} deck(s) manquant(s)"
                            : session is null ? "compte site non connecté" : "aucun",
                decks = snapshot.Decks.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => new
                {
                    merchantId = x.Key,
                    cards = x.Value.CardIds.Count,
                    confidence = x.Value.Confidence,
                    sourcePath = x.Value.SourcePath,
                    capturedAt = x.Value.CapturedAt
                }).ToArray(),
                numericReferences = scanner.NumericReferenceCount
            },
            sync = sync.GetDiagnostic(),
            identityEvidence,
            endpointSummary,
            possibleNames,
            traffic = traffic.TakeLast(300).ToArray()
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(temp, "rapport.json"), JsonSerializer.Serialize(report, options), Encoding.UTF8);

        var summary = new StringBuilder();
        summary.AppendLine($"ARCANE RUSH SYNC {AppConfig.Version} — RAPPORT DIAGNOSTIC");
        summary.AppendLine(new string('=', 66));
        summary.AppendLine($"Généré : {now:yyyy-MM-dd HH:mm:ss zzz}");
        summary.AppendLine($"Compte site : {(session is null ? "NON CONNECTÉ" : session.Email)}");
        summary.AppendLine($"Pseudo jeu : {(string.IsNullOrWhiteSpace(snapshot.PlayerName) ? "NON DÉTECTÉ" : snapshot.PlayerName)} (confiance {snapshot.PlayerNameConfidence}, source {snapshot.PlayerNameSource})");
        summary.AppendLine($"Collection : {(snapshot.CollectionSyncSafe ? $"{snapshot.OwnedCardIds.Count} cartes · sûre · confiance {snapshot.CollectionConfidence} · {snapshot.CollectionMethod}" : $"NON VALIDÉE · {snapshot.CollectionMethod}")}");
        summary.AppendLine($"Audit collection : ascii={snapshot.CollectionAsciiCount}, bruit={snapshot.CollectionNoiseCount}, récupérées-numériques={snapshot.CollectionNumericOnlyCount}, inférées={snapshot.CollectionInferredIds.Count}, rejetées-numériques={snapshot.CollectionRejectedNumericIds.Count}, records={snapshot.CollectionCandidateRecords}, non-résolus={snapshot.CollectionUnresolvedRecords}");
        if (snapshot.CollectionNumericOnlyIds.Count > 0)
            summary.AppendLine($"- Cartes récupérées sans SK_* clair : {string.Join(", ", snapshot.CollectionNumericOnlyIds)}");
        if (snapshot.CollectionNoiseIds.Count > 0)
            summary.AppendLine($"- Références SK_* exclues de la collection : {string.Join(", ", snapshot.CollectionNoiseIds.Take(40))}{(snapshot.CollectionNoiseIds.Count > 40 ? " …" : "")}");
        if (snapshot.CollectionRejectedNumericIds.Count > 0)
            summary.AppendLine($"- Références numériques refusées : {string.Join(", ", snapshot.CollectionRejectedNumericIds.Take(40))}{(snapshot.CollectionRejectedNumericIds.Count > 40 ? " …" : "")}");
        summary.AppendLine($"Decks : {snapshot.Decks.Count}/{AppConfig.ExpectedDeckCount}");
        summary.AppendLine($"Synchronisation possible : {(snapshot.IsComplete(AppConfig.ExpectedDeckCount) && session is not null ? "OUI" : "NON")}");
        summary.AppendLine();
        summary.AppendLine("RÉSOLUTION IDENTITÉ LOCALE");
        foreach (var item in identityEvidence.Take(20))
            summary.AppendLine($"- {item.Name} | score={item.Score} | réponses={string.Join(",", item.ResponsePaths)} | requêtes={string.Join(",", item.RequestPaths)} | hits={item.ResponseHits}/{item.RequestHits} | json={item.StrongJsonWeight} | éligible={(item.Eligible ? "oui" : "non")}");
        summary.AppendLine();
        summary.AppendLine("CANDIDATS PSEUDO LES PLUS UTILES");
        foreach (var item in possibleNames.Take(80))
            summary.AppendLine($"- {item.Name} | total={item.Count}, request={item.RequestHits}, response={item.ResponseHits}, strong={(item.Strong ? "oui" : "non")} | {string.Join(",", item.Paths)}");
        summary.AppendLine();
        summary.AppendLine("ENDPOINTS OBSERVÉS");
        foreach (var item in endpointSummary)
            summary.AppendLine($"- {item.Direction} {item.Method} {item.Path} | x{item.Count} | {item.BodyBytes} octets | noms forts: {string.Join(", ", item.StrongNames)}");
        summary.AppendLine();
        var syncDiag = sync.GetDiagnostic();
        summary.AppendLine();
        summary.AppendLine("DERNIÈRE SYNCHRONISATION");
        summary.AppendLine($"- Tentée : {(syncDiag.Attempted ? "oui" : "non")} · succès : {(syncDiag.Success ? "oui" : "non")} · HTTP {syncDiag.StatusCode}");
        summary.AppendLine($"- ownedIds envoyés/retournés : {syncDiag.OwnedIdsSent}/{syncDiag.OwnedIdsReturned} · vérifié : {(syncDiag.Verified ? "oui" : "non")}");
        summary.AppendLine($"- Pseudo retourné : {syncDiag.ReturnedPlayerName}");
        summary.AppendLine($"- Message : {syncDiag.Message}");
        summary.AppendLine();
        summary.AppendLine("Aucun corps réseau brut, mot de passe ou token Firebase n'est inclus dans ce rapport.");
        File.WriteAllText(Path.Combine(temp, "RESUME.txt"), summary.ToString(), Encoding.UTF8);

        var appLog = Path.Combine(AppPaths.Logs, "app.log");
        if (File.Exists(appLog))
        {
            try
            {
                var lines = File.ReadLines(appLog).TakeLast(1200);
                File.WriteAllLines(Path.Combine(temp, "app.log"), lines, Encoding.UTF8);
            }
            catch { }
        }

        var zip = Path.Combine(AppPaths.Reports, $"ArcaneRushSync-Rapport-{stamp}.zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(temp, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
        try { Directory.Delete(temp, recursive: true); } catch { }

        AppLog.Info($"Diagnostic report generated: {zip}");
        return zip;
    }

    public static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo(AppPaths.Reports) { UseShellExecute = true }); } catch { }
        }
    }
}
