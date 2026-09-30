using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArcaneRushSync.Models;

namespace ArcaneRushSync.Services;

public sealed record PlayerNameProbe(
    string AcceptedName,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> StrongCandidates,
    int Confidence = 0,
    string Evidence = "");

public sealed record IdentityCandidateDiagnostic(
    string Name,
    int Score,
    IReadOnlyList<string> ResponsePaths,
    IReadOnlyList<string> RequestPaths,
    int ResponseHits,
    int RequestHits,
    int StrongJsonWeight,
    bool Eligible);

public sealed record OwnedCollectionProbe(
    bool SyncSafe,
    int Confidence,
    string Method,
    IReadOnlyList<string> OwnedCardIds,
    int AsciiCount,
    int NoiseCount,
    int NumericOnlyCount,
    IReadOnlyList<string>? NumericOnlyIds = null,
    IReadOnlyList<string>? InferredIds = null,
    IReadOnlyList<string>? NoiseIds = null,
    IReadOnlyList<string>? RejectedNumericIds = null,
    int UnresolvedRecords = 0,
    int CandidateRecords = 0)
{
    public IReadOnlyList<string> SafeNumericOnlyIds => NumericOnlyIds ?? Array.Empty<string>();
    public IReadOnlyList<string> SafeInferredIds => InferredIds ?? Array.Empty<string>();
    public IReadOnlyList<string> SafeNoiseIds => NoiseIds ?? Array.Empty<string>();
    public IReadOnlyList<string> SafeRejectedNumericIds => RejectedNumericIds ?? Array.Empty<string>();
}


public sealed class DeckDetector
{
    private static readonly Regex CardRegex = new(
        @"(?<![A-Za-z0-9_])(SK_\d+_\d+_[1-9])(?:_Gold)?(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (string Key, Regex Pattern, int Weight)[] PlayerNamePatterns =
    {
        ("playerName", new Regex(@"""playerName""\s*:\s*""(?<v>[^""\r\n]{2,40})""", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), 100),
        ("displayName", new Regex(@"""displayName""\s*:\s*""(?<v>[^""\r\n]{2,40})""", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), 85),
        ("userName", new Regex(@"""userName""\s*:\s*""(?<v>[^""\r\n]{2,40})""", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), 80),
        ("username", new Regex(@"""username""\s*:\s*""(?<v>[^""\r\n]{2,40})""", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), 80),
        ("nickname", new Regex(@"""nickname""\s*:\s*""(?<v>[^""\r\n]{2,40})""", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), 75)
    };

    private static readonly string[] PlayerProfileHints = { "profile", "account", "player", "user", "me" };
    private static readonly HashSet<string> LocalIdentityEndpoints = new(StringComparer.Ordinal)
    {
        "/001003", "/001004", "/112003", "/113021"
    };

    // Binary account/profile payloads that repeatedly carry the local player's identity.
    // /001003 is the bootstrap, /002001 is the account state and /113022 is a small
    // follow-up profile payload. Cross-checking them is much safer than guessing from one
    // printable string in a protobuf response.
    private static readonly HashSet<string> IdentityResponseEndpoints = new(StringComparer.Ordinal)
    {
        "/001003", "/001004", "/002001", "/113022"
    };

    // These startup requests carry opaque account/clan identifiers on real captures. A value
    // seen here is therefore penalised as an ID, rather than being mistaken for a nickname.
    private static readonly HashSet<string> IdentityIdentifierRequestEndpoints = new(StringComparer.Ordinal)
    {
        "/002001", "/007001", "/008002", "/008005", "/014001"
    };

    private readonly Dictionary<string, MerchantInfo> _merchants;
    private readonly HashSet<string> _validCards;
    private readonly HashSet<string> _starterCards;
    private readonly HashSet<string> _knownIdentityNoise;
    private readonly Dictionary<string, int> _playerNameVotes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IdentityEvidence> _identityEvidence = new(StringComparer.OrdinalIgnoreCase);

    // Arcane Rush sometimes serializes equipped cards as compact numeric protobuf
    // references instead of literal SK_* strings.  The mature Companion learns a
    // conservative numeric-id -> card-id map from sibling records that contain both.
    // Keep the exact same idea here so the lightweight Sync can recover all 13 decks.
    private readonly Dictionary<ulong, Dictionary<string, int>> _refVotes = new();
    private readonly Dictionary<ulong, string> _stableRefs = new();
    private readonly List<RecentPayload> _recentPayloads = new();
    private long _recentPayloadBytes;
    private const int MaxRecentPayloads = 96;
    private const long MaxRecentPayloadBytes = 20L * 1024 * 1024;

    public int NumericReferenceCount => _stableRefs.Count;
    public bool IsStarterCard(string cardId) => _starterCards.Contains(cardId);

    public DeckDetector(string dataDirectory)
    {
        var merchantPath = Path.Combine(dataDirectory, "merchant_map.json");
        var catalogPath = Path.Combine(dataDirectory, "card_catalog.json");
        _merchants = LoadMerchants(merchantPath);
        _validCards = LoadValidCards(catalogPath);
        _starterCards = LoadStarterCards(catalogPath);
        _knownIdentityNoise = LoadKnownIdentityNoise(dataDirectory, catalogPath, merchantPath);
        LoadPersistedNumericRefs();
    }

    public void ResetTransientScanState()
    {
        _playerNameVotes.Clear();
        _identityEvidence.Clear();
        _recentPayloads.Clear();
        _recentPayloadBytes = 0;
    }

    public IReadOnlyList<IdentityCandidateDiagnostic> GetIdentityDiagnostics()
    {
        return _identityEvidence
            .Select(pair => BuildIdentityDiagnostic(pair.Key, pair.Value))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string? TryPlayerName(byte[] data, string path = "", string source = "response") =>
        InspectPlayerName(data, path, source).AcceptedName is { Length: > 0 } name ? name : null;

    public PlayerNameProbe InspectPlayerName(byte[] data, string path = "", string source = "response")
    {
        if (data.Length == 0 || data.Length > 2 * 1024 * 1024)
            return new PlayerNameProbe("", Array.Empty<string>(), Array.Empty<string>());

        var text = Encoding.UTF8.GetString(data);
        var rows = new List<(string Name, string Key, int Weight)>();
        foreach (var (key, pattern, weight) in PlayerNamePatterns)
        {
            foreach (Match match in pattern.Matches(text))
            {
                var clean = CleanPlayerName(match.Groups["v"].Value);
                if (!string.IsNullOrWhiteSpace(clean)) rows.Add((clean, key, weight));
            }
        }

        var isRequest = string.Equals(source, "request", StringComparison.OrdinalIgnoreCase);
        var isResponse = string.Equals(source, "response", StringComparison.OrdinalIgnoreCase);
        var payloadCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var strongNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // First keep the strongest signal available: a literal playerName/displayName key.
        // We still record all names as evidence because clan/lobby payloads can contain several
        // players and must never be resolved by "first name wins".
        if (rows.Count > 0)
        {
            foreach (var group in rows.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var candidate = group.Key;
                var weight = group.Max(x => x.Weight);
                if (!LooksLikeIdentityCandidate(candidate)) continue;
                payloadCandidates.Add(candidate);
                if (weight >= 80) strongNames.Add(candidate);
                ObserveIdentityCandidate(candidate, path, source, weight);

                var vote = PlayerProfileHints.Any(token => path.Contains(token, StringComparison.OrdinalIgnoreCase)) ? 2 : 1;
                if (isRequest && weight >= 80) vote += 2;
                if (LocalIdentityEndpoints.Contains(path) && weight >= 100) vote += 2;
                _playerNameVotes[candidate] = _playerNameVotes.GetValueOrDefault(candidate) + vote;
            }

            var uniqueRows = rows.Select(x => x.Name)
                .Where(LooksLikeIdentityCandidate)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (uniqueRows.Length == 1)
            {
                var candidate = uniqueRows[0];
                var best = rows.Where(x => string.Equals(x.Name, candidate, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Weight).First();
                var pathHint = PlayerProfileHints.Any(token => path.Contains(token, StringComparison.OrdinalIgnoreCase));
                var localIdentityEndpoint = LocalIdentityEndpoints.Contains(path);
                if (pathHint
                    || (isRequest && best.Weight >= 80 && localIdentityEndpoint)
                    || (isResponse && localIdentityEndpoint && best.Weight >= 100))
                {
                    var confidence = best.Weight >= 100 ? 100 : Math.Min(98, best.Weight + 8);
                    return new PlayerNameProbe(candidate, payloadCandidates.ToArray(), strongNames.ToArray(), confidence, $"json-{best.Key}:{source}:{path}");
                }
            }
        }

        // The account bootstrap/profile payloads are protobuf, not JSON. Record printable
        // human-looking values from several independent local-account responses, then resolve
        // the nickname by intersection. This is the field-tested fix for responses where the
        // bootstrap contains e.g. social labels + the nickname, while /002001 repeats only the
        // real local identity and technical account identifiers.
        var inspectPrintableIdentity =
            (isResponse && IdentityResponseEndpoints.Contains(path))
            || (isRequest && IdentityIdentifierRequestEndpoints.Contains(path));

        if (inspectPrintableIdentity)
        {
            foreach (var raw in ScannerDiagnosticExtractor.ExtractNameLikeStrings(data, 512))
            {
                var candidate = CleanPlayerName(raw);
                if (!LooksLikeIdentityCandidate(candidate)) continue;
                payloadCandidates.Add(candidate);
                ObserveIdentityCandidate(candidate, path, source, strongJsonWeight: 0);
            }
        }

        var resolved = ResolveIdentityCandidate();
        if (resolved is not null)
        {
            return new PlayerNameProbe(
                resolved.Value.Name,
                payloadCandidates.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                strongNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                resolved.Value.Confidence,
                resolved.Value.Evidence);
        }

        return new PlayerNameProbe(
            "",
            payloadCandidates.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            strongNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private void ObserveIdentityCandidate(string candidate, string path, string source, int strongJsonWeight)
    {
        if (!_identityEvidence.TryGetValue(candidate, out var evidence))
        {
            evidence = new IdentityEvidence();
            _identityEvidence[candidate] = evidence;
        }

        if (string.Equals(source, "response", StringComparison.OrdinalIgnoreCase))
        {
            evidence.ResponseHits++;
            evidence.ResponsePaths.Add(path);
        }
        else if (string.Equals(source, "request", StringComparison.OrdinalIgnoreCase))
        {
            evidence.RequestHits++;
            evidence.RequestPaths.Add(path);
        }

        evidence.StrongJsonWeight = Math.Max(evidence.StrongJsonWeight, strongJsonWeight);
    }

    private (string Name, int Confidence, string Evidence)? ResolveIdentityCandidate()
    {
        if (_identityEvidence.Count == 0) return null;

        var ranked = _identityEvidence
            .Select(pair => BuildIdentityDiagnostic(pair.Key, pair.Value))
            .Where(x => x.Eligible)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.ResponsePaths.Count)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ranked.Length == 0) return null;
        var best = ranked[0];
        var runnerUpScore = ranked.Length > 1 ? ranked[1].Score : int.MinValue;
        var hasBootstrapAndAccount = best.ResponsePaths.Contains("/001003", StringComparer.Ordinal)
            && best.ResponsePaths.Contains("/002001", StringComparer.Ordinal);
        var hasBootstrapAndProfile = best.ResponsePaths.Contains("/001003", StringComparer.Ordinal)
            && best.ResponsePaths.Contains("/113022", StringComparer.Ordinal);
        var hasIdentifierRequest = best.RequestPaths.Any(IdentityIdentifierRequestEndpoints.Contains);

        // Two independent account responses with no matching identifier request are decisive.
        // Otherwise require a clear score margin so a clan/lobby packet cannot select another player.
        var decisivePair = hasBootstrapAndAccount && !hasIdentifierRequest;
        var clearMargin = best.Score >= 24 && (ranked.Length == 1 || best.Score >= runnerUpScore + 7);
        if (!decisivePair && !clearMargin) return null;

        var confidence = decisivePair ? 100
            : hasBootstrapAndProfile && !hasIdentifierRequest ? 97
            : best.StrongJsonWeight >= 100 ? 98
            : 92;

        var evidence = $"identity-evidence:{string.Join("+", best.ResponsePaths.OrderBy(x => x, StringComparer.Ordinal))}";
        return (best.Name, confidence, evidence);
    }

    private IdentityCandidateDiagnostic BuildIdentityDiagnostic(string name, IdentityEvidence evidence)
    {
        var score = 0;
        foreach (var path in evidence.ResponsePaths)
        {
            score += path switch
            {
                "/001003" => 12,
                "/001004" => 9,
                "/002001" => 12,
                "/113022" => 6,
                _ => 1
            };
        }

        if (evidence.ResponsePaths.Contains("/001003") && evidence.ResponsePaths.Contains("/002001")) score += 20;
        if (evidence.ResponsePaths.Contains("/001003") && evidence.ResponsePaths.Contains("/113022")) score += 6;
        score += Math.Min(5, evidence.ResponseHits);
        score += evidence.StrongJsonWeight >= 100 ? 12 : evidence.StrongJsonWeight >= 80 ? 6 : 0;

        var identifierRequestHits = evidence.RequestPaths.Count(IdentityIdentifierRequestEndpoints.Contains);
        score -= identifierRequestHits * 15;
        if (identifierRequestHits > 0 && LooksLikeOpaqueIdentifier(name)) score -= 12;

        var eligible = evidence.StrongJsonWeight >= 80
            || evidence.ResponsePaths.Contains("/001003")
            || evidence.ResponsePaths.Contains("/002001");

        return new IdentityCandidateDiagnostic(
            name,
            score,
            evidence.ResponsePaths.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            evidence.RequestPaths.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            evidence.ResponseHits,
            evidence.RequestHits,
            evidence.StrongJsonWeight,
            eligible);
    }

    private bool LooksLikeIdentityCandidate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (_knownIdentityNoise.Contains(candidate)) return false;
        if (candidate.Length is < 2 or > 32) return false;
        if (!candidate.Any(char.IsLetter)) return false;
        if (candidate.Contains('/') || candidate.Contains('\\') || candidate.Contains(':') || candidate.Contains('@')) return false;
        if (candidate.Contains('_')) return false;
        if (candidate.Count(char.IsWhiteSpace) > 2) return false;

        var lower = candidate.ToLowerInvariant();
        if (lower.StartsWith("http") || lower.StartsWith("www.")) return false;
        var protocolPrefixes = new[]
        {
            "Quest", "Contextual", "ForceClaim", "BattlePass", "SpellPass", "WildCard",
            "RemoveAds", "Dialogue", "Dealer", "League", "RankPoint", "PassPoint", "dailyOffer",
            "avatar", "Commander", "Relic", "RANDOM", "SK", "OVTraining", "Ads-"
        };
        if (protocolPrefixes.Any(prefix => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    private static bool LooksLikeOpaqueIdentifier(string candidate)
    {
        if (candidate.Length is < 6 or > 24 || candidate.Any(char.IsWhiteSpace)) return false;
        var lettersOrDigits = candidate.All(char.IsLetterOrDigit);
        if (!lettersOrDigits) return false;
        var hasLower = candidate.Any(char.IsLower);
        var hasDigit = candidate.Any(char.IsDigit);
        return !hasLower || (candidate.Length >= 16 && hasDigit);
    }

    private sealed class IdentityEvidence
    {
        public HashSet<string> ResponsePaths { get; } = new(StringComparer.Ordinal);
        public HashSet<string> RequestPaths { get; } = new(StringComparer.Ordinal);
        public int ResponseHits { get; set; }
        public int RequestHits { get; set; }
        public int StrongJsonWeight { get; set; }
    }

    public OwnedCollectionProbe DetectOwnedCollection(byte[] data, string path)
    {
        if (!string.Equals(path, "/001003", StringComparison.Ordinal)
            || data.Length == 0
            || data.Length > AppConfig.MaxCapturedBodyBytes)
            return new OwnedCollectionProbe(false, 0, "not-collection-endpoint", Array.Empty<string>(), 0, 0, 0);

        // /001003 is the confirmed startup account payload used by the original Companion.
        // Real captures from 1.0.8/1.0.9 established an important distinction:
        //   * exact textual SK_* IDs are the reliable ownership signal;
        //   * keeping only the largest dense byte cluster can drop a genuinely owned card;
        //   * root-wide numeric reference recovery can add cards the player does NOT own.
        //
        // Public synchronization therefore uses EVERY exact catalog-valid SK_* ID present
        // in /001003 and NEVER invents ownership from a numeric reference. Protobuf/density
        // analysis remains a safety check and diagnostic only.
        var text = Encoding.Latin1.GetString(data);
        var hits = CardRegex.Matches(text)
            .Select(m => new CollectionCardHit(m.Index, m.Index + m.Length, m.Groups[1].Value.ToUpperInvariant()))
            .Where(hit => _validCards.Contains(hit.CardId))
            .ToList();

        var allAscii = UniquePreserve(hits.Select(x => x.CardId));
        var allAsciiSet = allAscii.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (hits.Count < 6 || allAscii.Count < 6)
            return new OwnedCollectionProbe(false, 0, "bootstrap-exact-ascii-too-small", allAscii, allAscii.Count, 0, 0);

        ProtoNode? root = null;
        OwnedCollectionProbe? structuredDiagnostic = null;
        if (TryParseProtobuf(data, out var parsedRoot))
        {
            root = parsedRoot;
            structuredDiagnostic = ExtractStructuredOwnedCollection(root, allAscii);
        }

        // Density is used only to recognize the expected inventory-shaped payload. Exact IDs
        // outside the dominant cluster are INCLUDED: a serialization gap is not evidence that
        // a valid card ID is unowned.
        var gaps = new List<int>();
        for (var i = 1; i < hits.Count; i++)
            gaps.Add(Math.Max(0, hits[i].Start - hits[i - 1].End));
        gaps.Sort();
        var medianGap = gaps.Count > 0 ? gaps[gaps.Count / 2] : 0;
        var clusterGap = Math.Max(1024, Math.Min(8192, Math.Max(1, medianGap) * 24));

        var clusters = new List<List<CollectionCardHit>>();
        var current = new List<CollectionCardHit>();
        CollectionCardHit? previous = null;
        foreach (var hit in hits)
        {
            if (previous is not null && hit.Start - previous.Value.End > clusterGap)
            {
                if (current.Count > 0) clusters.Add(current);
                current = new List<CollectionCardHit>();
            }
            current.Add(hit);
            previous = hit;
        }
        if (current.Count > 0) clusters.Add(current);

        var denseRows = clusters
            .Select(cluster => new DenseCollectionCandidate(
                UniquePreserve(cluster.Select(x => x.CardId)),
                cluster.Count,
                cluster.Count == 0 ? 0 : cluster[^1].End - cluster[0].Start))
            .Where(row => row.Ids.Count > 0)
            .OrderByDescending(row => row.Ids.Count)
            .ThenByDescending(row => row.Occurrences)
            .ToArray();

        if (denseRows.Length == 0)
            return new OwnedCollectionProbe(false, 0, "bootstrap-exact-ascii-no-shape", allAscii, allAscii.Count, 0, 0);

        var denseBest = denseRows[0];
        var denseSet = denseBest.Ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outOfDense = allAsciiSet
            .Where(id => !denseSet.Contains(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var coverage = denseSet.Count / (double)Math.Max(1, allAsciiSet.Count);

        // Numeric mappings are diagnostic only. They are explicitly rejected from ownership
        // unless the same card is independently proven later by an equipped deck.
        var rejectedNumeric = Array.Empty<string>();
        if (root is not null && _stableRefs.Count > 0)
        {
            rejectedNumeric = DescendantNumericCards(root)
                .Where(card => _validCards.Contains(card) && !allAsciiSet.Contains(card))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(card => card, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var count = allAscii.Count;

        // IMPORTANT: density is a diagnostic, not an ownership gate.
        //
        // Field captures from the mature Companion and the 1.1.0 validation showed that
        // /001003 can serialize the same legitimate collection with very different byte
        // spacing between accounts. Requiring one dominant dense cluster therefore rejects
        // valid players even though every retained ID is an exact, catalogue-valid SK_* value.
        //
        // The false positives seen in older builds came from NUMERIC reference recovery,
        // not from these explicit SK_* strings. Numeric-only cards remain excluded here and
        // may only be added later when an equipped deck independently proves ownership.
        bool shapeLooksDense;
        if (count >= 80)
        {
            var outlierLimit = Math.Max(12, (int)Math.Ceiling(count * 0.05));
            shapeLooksDense = coverage >= 0.95 && outOfDense.Length <= outlierLimit;
        }
        else if (count >= 20)
        {
            var outlierLimit = Math.Max(6, (int)Math.Ceiling(count * 0.10));
            shapeLooksDense = coverage >= 0.90 && outOfDense.Length <= outlierLimit;
        }
        else
        {
            shapeLooksDense = coverage >= 0.85 && outOfDense.Length <= 3;
        }

        var structuredCorroborated = structuredDiagnostic?.SyncSafe == true;
        var confidence = shapeLooksDense
            ? coverage >= 0.995 && outOfDense.Length <= 2
                ? 100
                : coverage >= 0.98
                    ? 99
                    : 98
            : structuredCorroborated
                ? 98
                : 96;

        var owned = allAscii
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var method = shapeLooksDense
            ? "bootstrap-exact-ascii"
            : structuredCorroborated
                ? "bootstrap-exact-ascii+structured-proof"
                : "bootstrap-exact-ascii+fragmented";

        AppLog.Info(
            $"Collection /001003 exact-ASCII accepted: owned={owned.Length}, dense={denseSet.Count}, coverage={coverage:F4}, " +
            $"outOfDense={outOfDense.Length}, rejectedNumeric={rejectedNumeric.Length}, " +
            $"structuredCandidate={(structuredDiagnostic?.OwnedCardIds.Count ?? 0)}, structuredSafe={structuredCorroborated}, " +
            $"shapeDense={shapeLooksDense}, confidence={confidence}, method={method}.");

        return new OwnedCollectionProbe(
            true,
            confidence,
            method,
            owned,
            allAscii.Count,
            outOfDense.Length,
            0,
            Array.Empty<string>(),
            Array.Empty<string>(),
            outOfDense,
            rejectedNumeric,
            structuredDiagnostic?.UnresolvedRecords ?? 0,
            structuredDiagnostic?.CandidateRecords ?? 0);
    }

    private OwnedCollectionProbe? ExtractStructuredOwnedCollection(ProtoNode root, IReadOnlyList<string> allAscii)
    {
        var asciiSet = allAscii.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<StructuredCollectionCandidate>();

        foreach (var parent in Walk(root))
        {
            foreach (var group in parent.Children.GroupBy(child => child.FieldNumber))
            {
                var children = group.ToArray();
                if (children.Length < 4) continue;

                var inferredByIndex = InferComponentCardsForGroup(children);
                var rows = new List<string>();
                var inferredUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var mappedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var mappedConflicts = 0;
                var ambiguous = 0;
                var unresolved = 0;

                // A persisted numeric reference is never trusted globally for ownership.
                // It may be used for a numeric-only record ONLY after the same mapping table
                // proves itself on many exact SK_* sibling records from THIS inventory group.
                // This keeps the useful mature-Companion recovery path while preventing a deck
                // or stale reference elsewhere in /001003 from becoming an owned card.
                var mapAgreement = 0;
                var mapDisagreement = 0;
                for (var probeIndex = 0; probeIndex < children.Length; probeIndex++)
                {
                    var probeChild = children[probeIndex];
                    var probeExact = UniquePreserve(DescendantCards(probeChild).Where(_validCards.Contains));
                    if (probeExact.Count != 1) continue;
                    var probeMapped = UniquePreserve(DescendantNumericCards(probeChild).Where(_validCards.Contains));
                    if (probeMapped.Count == 0) continue;
                    if (probeMapped.Contains(probeExact[0], StringComparer.OrdinalIgnoreCase))
                        mapAgreement++;
                    else
                        mapDisagreement++;
                }
                var mapEvidence = mapAgreement + mapDisagreement;
                var mapAgreementRatio = mapAgreement / (double)Math.Max(1, mapEvidence);
                var allowCorroboratedMappedRecords = mapEvidence >= 12
                    && mapAgreement >= 10
                    && mapAgreementRatio >= 0.97
                    && mapDisagreement <= 1;
                mappedConflicts += mapDisagreement;

                for (var index = 0; index < children.Length; index++)
                {
                    var child = children[index];
                    var exactAscii = UniquePreserve(DescendantCards(child).Where(_validCards.Contains));

                    if (exactAscii.Count == 1)
                    {
                        rows.Add(exactAscii[0]);
                        continue;
                    }

                    if (exactAscii.Count > 1)
                    {
                        ambiguous++;
                        continue;
                    }

                    var mapped = UniquePreserve(DescendantNumericCards(child).Where(_validCards.Contains));
                    foreach (var mappedCard in mapped) mappedCandidates.Add(mappedCard);

                    if (inferredByIndex.TryGetValue(index, out var inferredCard))
                    {
                        // Component inference is local to THIS inventory group and is stronger
                        // than a persisted numeric map learned on older sessions. If they disagree,
                        // record the stale-map conflict but keep the structurally inferred card.
                        if (mapped.Count > 0 && !mapped.Contains(inferredCard, StringComparer.OrdinalIgnoreCase))
                            mappedConflicts++;

                        rows.Add(inferredCard);
                        inferredUsed.Add(inferredCard);
                        continue;
                    }

                    if (allowCorroboratedMappedRecords && mapped.Count == 1)
                    {
                        // Record-local fallback only: the numeric map has just been verified
                        // against exact sibling ownership records in this very group.
                        rows.Add(mapped[0]);
                        inferredUsed.Add(mapped[0]);
                        continue;
                    }

                    // An uncorroborated persisted mapping is diagnostic only. It can still help
                    // deck recovery, but it cannot mutate the cloud collection.
                    unresolved++;
                }

                var uniqueIds = UniquePreserve(rows);
                if (uniqueIds.Count < 4) continue;

                var singleRatio = rows.Count / (double)Math.Max(1, children.Length);
                var asciiHits = uniqueIds.Count(id => asciiSet.Contains(id));
                var asciiCoverage = asciiHits / (double)Math.Max(1, asciiSet.Count);
                var candidateAsciiRatio = asciiHits / (double)Math.Max(1, uniqueIds.Count);
                var score = uniqueIds.Count * 10
                    + (int)(singleRatio * 120)
                    + (int)(asciiCoverage * 100)
                    - ambiguous * 4
                    - mappedConflicts * 10;

                candidates.Add(new StructuredCollectionCandidate(
                    uniqueIds,
                    children.Length,
                    rows.Count,
                    singleRatio,
                    asciiCoverage,
                    candidateAsciiRatio,
                    inferredUsed.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                    mappedCandidates.Where(id => !asciiSet.Contains(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                    unresolved,
                    ambiguous,
                    mappedConflicts,
                    score));
            }
        }

        var best = candidates
            .OrderByDescending(row => row.Ids.Count)
            .ThenByDescending(row => row.SingleRatio)
            .ThenByDescending(row => row.AsciiCoverage)
            .ThenByDescending(row => row.Score)
            .FirstOrDefault();

        if (best is null) return null;

        var count = best.Ids.Count;
        var ratio = best.SingleRatio;
        var coverage = best.AsciiCoverage;
        var asciiExtraCount = Math.Max(0, asciiSet.Count - count);

        bool structurallySafe;
        if (count >= 40)
        {
            // Public sync is stricter than the old research collector: a large group
            // must still explain most printable collection IDs and may infer only a
            // small minority of records numerically. This blocks catalogue/deck-shaped
            // groups from ever becoming the cloud collection.
            var inferredLimit = Math.Max(12, (int)Math.Ceiling(count * 0.08));
            structurallySafe = ratio >= 0.70
                && coverage >= 0.80
                && best.CandidateAsciiRatio >= 0.90
                && best.InferredIds.Count <= inferredLimit;
        }
        else if (count >= 20)
        {
            structurallySafe = ratio >= 0.90
                && best.CandidateAsciiRatio >= 0.90
                && asciiExtraCount <= Math.Max(6, (int)(count * 0.35))
                && best.AmbiguousRecords <= 2;
        }
        else if (count >= 6)
        {
            structurallySafe = ratio >= 0.96
                && best.CandidateAsciiRatio >= 0.96
                && asciiExtraCount <= 2
                && best.AmbiguousRecords == 0;
        }
        else
        {
            structurallySafe = false;
        }

        var owned = best.Ids
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ownedSet = owned.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var noise = asciiSet.Where(id => !ownedSet.Contains(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        var inferredOnly = best.InferredIds.Where(id => !asciiSet.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        var rejectedNumeric = best.MappedCandidates.Where(id => !ownedSet.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();

        var confidence = Math.Min(100, (int)(
            55
            + Math.Min(20, count / 20.0)
            + ratio * 15
            + Math.Min(10, coverage * 10)));

        AppLog.Info(
            $"Structured collection candidate: owned={count}, records={best.RecordCount}, single={best.SingleCardRecords}, " +
            $"ratio={ratio:F4}, asciiCoverage={coverage:F4}, inferred={inferredOnly.Length}, noise={noise.Length}, " +
            $"unresolved={best.UnresolvedRecords}, ambiguous={best.AmbiguousRecords}, numericMapConflicts={best.MappedConflicts}, safe={structurallySafe}.");

        return new OwnedCollectionProbe(
            structurallySafe,
            structurallySafe ? confidence : Math.Min(50, confidence),
            structurallySafe ? "protobuf-repeated-owned-records" : "diagnostic-structured-inventory-unsafe",
            owned,
            allAscii.Count,
            noise.Length,
            inferredOnly.Length,
            inferredOnly,
            best.InferredIds,
            noise,
            rejectedNumeric,
            best.UnresolvedRecords,
            best.RecordCount);
    }

    private Dictionary<int, string> InferComponentCardsForGroup(IReadOnlyList<ProtoNode> children)
    {
        var featureVotes = new Dictionary<NumericFeature, ComponentVoteBucket>();

        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            var exact = UniquePreserve(DescendantCards(child).Where(_validCards.Contains));
            if (exact.Count != 1) continue;
            var parts = ParseCardComponents(exact[0]);
            if (parts is null) continue;

            foreach (var feature in RecordNumericFeatures(child).Distinct())
            {
                if (!featureVotes.TryGetValue(feature, out var votes))
                {
                    votes = new ComponentVoteBucket();
                    featureVotes[feature] = votes;
                }
                Increment(votes.Merchant, parts.Value.MerchantId);
                Increment(votes.Family, parts.Value.Family);
                Increment(votes.Variant, parts.Value.Variant);
            }
        }

        var inferred = new Dictionary<int, string>();
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            if (DescendantCards(child).Any(card => _validCards.Contains(card))) continue;

            var merchantScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var familyScores = new Dictionary<int, int>();
            var variantScores = new Dictionary<int, int>();

            foreach (var feature in RecordNumericFeatures(child).Distinct())
            {
                if (!featureVotes.TryGetValue(feature, out var votes)) continue;

                if (TryStableTop(votes.Merchant, 3, out var merchant, out var merchantScore))
                    Increment(merchantScores, merchant, merchantScore);
                if (TryStableTop(votes.Family, 3, out var family, out var familyScore))
                    Increment(familyScores, family, familyScore);
                if (TryStableTop(votes.Variant, 2, out var variant, out var variantScore))
                    Increment(variantScores, variant, variantScore);
            }

            if (!TryChooseComponent(merchantScores, 3, out var chosenMerchant)
                || !TryChooseComponent(familyScores, 3, out var chosenFamily)
                || !TryChooseComponent(variantScores, 2, out var chosenVariant))
                continue;

            var cardId = $"{chosenMerchant}_{chosenFamily}_{chosenVariant}".ToUpperInvariant();
            if (_validCards.Contains(cardId))
                inferred[index] = cardId;
        }

        return inferred;
    }

    private static IReadOnlyList<NumericFeature> RecordNumericFeatures(ProtoNode node)
    {
        var output = new List<NumericFeature>();
        CollectRecordNumericFeatures(node, output, "", 0);
        return output;
    }

    private static void CollectRecordNumericFeatures(
        ProtoNode node,
        List<NumericFeature> output,
        string path,
        int depth)
    {
        if (depth > 2) return;

        foreach (var pair in node.DirectVarints)
        {
            foreach (var value in pair.Value)
            {
                if (value <= 2_000_000)
                    output.Add(new NumericFeature(path, pair.Key, value));
            }
        }

        foreach (var child in node.Children)
        {
            var childPath = string.IsNullOrEmpty(path)
                ? child.FieldNumber.ToString()
                : path + "." + child.FieldNumber;
            CollectRecordNumericFeatures(child, output, childPath, depth + 1);
        }
    }

    private static bool TryStableTop<T>(
        IReadOnlyDictionary<T, int> counter,
        int minSupport,
        out T value,
        out int score)
        where T : notnull
    {
        value = default!;
        score = 0;
        if (counter.Count == 0) return false;

        var ordered = counter.OrderByDescending(pair => pair.Value).ToArray();
        var top = ordered[0];
        var second = ordered.Length > 1 ? ordered[1].Value : 0;
        var total = ordered.Sum(pair => pair.Value);
        if (top.Value < minSupport) return false;
        if (top.Value / (double)Math.Max(1, total) < 0.92) return false;
        if (second > 0 && top.Value < second * 4) return false;

        value = top.Key;
        score = top.Value;
        return true;
    }

    private static bool TryChooseComponent<T>(
        IReadOnlyDictionary<T, int> scores,
        int minTotal,
        out T value)
        where T : notnull
    {
        value = default!;
        if (scores.Count == 0) return false;
        var ordered = scores.OrderByDescending(pair => pair.Value).ToArray();
        var top = ordered[0];
        var second = ordered.Length > 1 ? ordered[1].Value : 0;
        if (top.Value < minTotal) return false;
        if (second > 0 && top.Value < second * 2) return false;
        value = top.Key;
        return true;
    }

    private static void Increment<T>(Dictionary<T, int> dictionary, T key, int amount = 1)
        where T : notnull => dictionary[key] = dictionary.GetValueOrDefault(key) + amount;

    private static (string MerchantId, int Family, int Variant)? ParseCardComponents(string cardId)
    {
        var parts = cardId.Split('_');
        if (parts.Length < 4
            || !int.TryParse(parts[1], out var merchant)
            || !int.TryParse(parts[2], out var family)
            || !int.TryParse(parts[3], out var variant))
            return null;
        return ($"SK_{merchant}", family, variant);
    }

    private sealed record DenseCollectionCandidate(
        IReadOnlyList<string> Ids,
        int Occurrences,
        int SpanBytes);

    private sealed record StructuredCollectionCandidate(
        IReadOnlyList<string> Ids,
        int RecordCount,
        int SingleCardRecords,
        double SingleRatio,
        double AsciiCoverage,
        double CandidateAsciiRatio,
        IReadOnlyList<string> InferredIds,
        IReadOnlyList<string> MappedCandidates,
        int UnresolvedRecords,
        int AmbiguousRecords,
        int MappedConflicts,
        int Score);

    private sealed class ComponentVoteBucket
    {
        public Dictionary<string, int> Merchant { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, int> Family { get; } = new();
        public Dictionary<int, int> Variant { get; } = new();
    }

    private readonly record struct NumericFeature(string Path, int Field, ulong Value);

    public IReadOnlyList<DetectedDeck> DetectDecks(byte[] data, string path)
    {
        if (data.Length == 0 || data.Length > AppConfig.MaxCapturedBodyBytes)
            return Array.Empty<DetectedDeck>();

        var found = new Dictionary<(string Merchant, string Signature), DetectedDeck>();
        var refsBefore = _stableRefs.Count;

        CollectDeckCandidates(data, path, learnNumericRefs: true, found);

        // A numeric-only deck may have arrived BEFORE another response taught us the
        // card references.  When new stable mappings appear, replay a small in-memory
        // ring buffer. Nothing from this buffer is ever written to disk.
        if (_stableRefs.Count > refsBefore)
        {
            SavePersistedNumericRefs();
            foreach (var recent in _recentPayloads.ToArray())
                CollectDeckCandidates(recent.Data, recent.Path, learnNumericRefs: false, found);
        }

        RememberRecentPayload(data, path);

        return found.Values
            .Where(x => x.CardIds.Count == AppConfig.ExpectedCardsPerDeck && x.Confidence >= 86)
            .OrderByDescending(x => x.Confidence)
            .ToArray();
    }

    private void CollectDeckCandidates(
        byte[] data,
        string path,
        bool learnNumericRefs,
        Dictionary<(string Merchant, string Signature), DetectedDeck> found)
    {
        void Consider(IEnumerable<string> ids, string origin, int bonus = 0)
        {
            var unique = UniquePreserve(ids);
            if (unique.Count is < 15 or > 25) return;
            var chosen = ChooseMerchant(unique);
            if (chosen is null || chosen.Value.Score < 72) return;
            var score = Math.Min(100, chosen.Value.Score + bonus);
            if (AppConfig.ValidatedDeckEndpoints.Contains(path)
                && origin.StartsWith("ascii", StringComparison.Ordinal))
                score = 100;

            var info = _merchants[chosen.Value.MerchantId];
            var deck = new DetectedDeck(
                chosen.Value.MerchantId,
                info.Name,
                info.Faction,
                unique,
                score,
                path,
                DateTimeOffset.UtcNow);
            var signature = string.Join("|", unique);
            found[(deck.MerchantId, signature)] = deck;
        }

        // /001003 is the important compatibility path from the mature Companion.
        // It contains both the player's collection AND nested equipped-deck records.
        // Never run broad sliding-window detection on this endpoint: parse protobuf first
        // and accept only structurally exact deck nodes (20 cards, families 1..20, one merchant).
        if (AppConfig.StructuredDeckEndpoints.Contains(path))
        {
            if (!TryParseProtobuf(data, out var structuredRoot)) return;
            if (learnNumericRefs)
                LearnNumericReferences(structuredRoot);

            var strictCount = InspectStrictStructuredDeckNodes(structuredRoot, Consider);
            AppLog.Info($"Structured deck scan {path}: {strictCount} strict deck candidate(s), numericRefs={_stableRefs.Count}.");
            return;
        }

        var text = Encoding.Latin1.GetString(data);
        var hits = CardRegex.Matches(text)
            .Select(m => new CardHit(m.Index, m.Groups[1].Value.ToUpperInvariant()))
            .Where(h => _validCards.Contains(h.CardId))
            .ToList();

        if (hits.Count >= 15)
        {
            // Entire unique body if it already resembles one deck.
            Consider(hits.Select(h => h.CardId), "ascii-flow");

            // Sliding windows over raw occurrences. This preserves repeated occurrences
            // in network order and is useful when a deck is serialized as one compact list.
            var maxWindows = Math.Min(Math.Max(0, hits.Count - 19), 5000);
            for (var i = 0; i < maxWindows; i++)
            {
                var chunk = hits.Skip(i).Take(20).ToArray();
                var ids = chunk.Select(x => x.CardId).ToArray();
                if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 20) continue;
                var span = chunk[^1].Offset - chunk[0].Offset;
                var bonus = span <= 900 ? 5 : span <= 1800 ? 2 : 0;
                Consider(ids, "ascii-raw-window20", bonus);
            }

            // Sliding windows over first unique occurrences.
            var uniq = UniquePreserve(hits.Select(h => h.CardId));
            if (uniq.Count is >= 20 and <= 500)
                for (var i = 0; i <= uniq.Count - 20; i++)
                    Consider(uniq.Skip(i).Take(20), "ascii-window20");
        }

        // Crucial path used by the validated Companion: equipped decks can be nested
        // protobuf structures, and some accounts use numeric-only card references.
        if (!TryParseProtobuf(data, out var root)) return;

        InspectProtobufTree(root, Consider);
        if (learnNumericRefs)
            LearnNumericReferences(root);
        InspectNumericProtobufTree(root, Consider);
    }

    private int InspectStrictStructuredDeckNodes(
        ProtoNode root,
        Action<IEnumerable<string>, string, int> consider)
    {
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in Walk(root))
        {
            // The previously validated /001003 payload stored each merchant deck as
            // one nested protobuf node containing exactly the 20 selected SK_* IDs.
            // Try direct strings first; then a conservative ASCII+numeric union for
            // accounts where one or more records are compact numeric references.
            var candidates = new List<List<string>>();

            var direct = UniquePreserve(node.DirectCards.Where(_validCards.Contains));
            if (direct.Count == AppConfig.ExpectedCardsPerDeck)
                candidates.Add(direct);

            var asciiDesc = UniquePreserve(DescendantCards(node).Where(_validCards.Contains));
            if (asciiDesc.Count == AppConfig.ExpectedCardsPerDeck)
                candidates.Add(asciiDesc);

            if (_stableRefs.Count > 0)
            {
                var hybrid = UniquePreserve(
                    DescendantCards(node).Where(_validCards.Contains)
                        .Concat(DescendantNumericCards(node)));
                if (hybrid.Count == AppConfig.ExpectedCardsPerDeck)
                    candidates.Add(hybrid);
            }

            foreach (var ids in candidates)
            {
                if (!TryStrictDeckSignature(ids, out var merchantId)) continue;
                var signature = merchantId + ":" + string.Join("|", ids);
                if (!accepted.Add(signature)) continue;

                // +12 raises Neutral's normal score (88) to 100 while remaining
                // harmless because TryStrictDeckSignature has already proven the
                // exact 20-family loadout shape.
                consider(ids, "ascii-structured-001003", 12);
            }
        }

        return accepted.Count;
    }

    private bool TryStrictDeckSignature(IReadOnlyList<string> ids, out string merchantId)
    {
        merchantId = "";
        if (ids.Count != AppConfig.ExpectedCardsPerDeck) return false;

        var parsed = ids
            .Select(ParseCard)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();
        if (parsed.Length != AppConfig.ExpectedCardsPerDeck) return false;

        var merchants = parsed
            .Select(x => x.MerchantId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (merchants.Length != 1 || !_merchants.ContainsKey(merchants[0])) return false;

        // The real equipped records previously captured from /001003 contain one
        // selected variant for each of the twenty deck families. This is the safety
        // property that distinguishes them from the surrounding collection records.
        var families = parsed.Select(x => x.Family).Distinct().OrderBy(x => x).ToArray();
        if (families.Length != AppConfig.ExpectedCardsPerDeck) return false;
        for (var family = 1; family <= AppConfig.ExpectedCardsPerDeck; family++)
            if (!families.Contains(family)) return false;

        merchantId = merchants[0];
        return true;
    }

    private void InspectProtobufTree(ProtoNode root, Action<IEnumerable<string>, string, int> consider)
    {
        foreach (var node in Walk(root))
        {
            var direct = UniquePreserve(node.DirectCards.Where(_validCards.Contains));
            if (direct.Count is >= 15 and <= 25)
                consider(direct, "ascii-direct-node", 0);

            var descendants = UniquePreserve(DescendantCards(node).Where(_validCards.Contains));
            if (descendants.Count is >= 15 and <= 30)
                consider(descendants, "ascii-protobuf-node", 0);

            foreach (var group in node.Children.GroupBy(x => x.FieldNumber))
            {
                var children = group.ToArray();
                if (children.Length is < 15 or > 30) continue;
                var oneEach = new List<string>();
                var usable = 0;
                foreach (var child in children)
                {
                    var cards = UniquePreserve(DescendantCards(child).Where(_validCards.Contains));
                    if (cards.Count != 1) continue;
                    usable++;
                    oneEach.Add(cards[0]);
                }
                oneEach = UniquePreserve(oneEach);
                if (usable >= 15 && oneEach.Count is >= 15 and <= 25)
                    consider(oneEach, "ascii-repeated-field", 6);
            }
        }
    }

    private void InspectNumericProtobufTree(ProtoNode root, Action<IEnumerable<string>, string, int> consider)
    {
        if (_stableRefs.Count == 0) return;

        foreach (var node in Walk(root))
        {
            var directMapped = UniquePreserve(NumericValues(node)
                .Select(v => _stableRefs.TryGetValue(v.Value, out var card) ? card : null)
                .OfType<string>());
            if (directMapped.Count is >= 15 and <= 25)
                consider(directMapped, "numeric-direct-node", 0);

            foreach (var packed in node.PackedVarints)
            {
                var mapped = UniquePreserve(packed.Value
                    .Select(value => _stableRefs.TryGetValue(value, out var card) ? card : null)
                    .OfType<string>());
                if (mapped.Count is >= 15 and <= 25)
                    consider(mapped, $"numeric-packed-field-{packed.Key}", 0);
            }

            foreach (var group in node.Children.GroupBy(x => x.FieldNumber))
            {
                var children = group.ToArray();
                if (children.Length is < 15 or > 30) continue;

                var oneEach = new List<string>();
                var usable = 0;
                foreach (var child in children)
                {
                    var mapped = UniquePreserve(DescendantNumericCards(child));
                    if (mapped.Count != 1) continue;
                    usable++;
                    oneEach.Add(mapped[0]);
                }

                oneEach = UniquePreserve(oneEach);
                if (usable >= 15 && oneEach.Count is >= 15 and <= 25)
                    consider(oneEach, $"numeric-repeated-field-{group.Key}", 6);
            }
        }

        // Compatibility with the mature Python collector: a protobuf node can
        // contain one deck plus a few unrelated numeric references. Project larger
        // mapped groups onto each merchant before trying root-wide windows.
        foreach (var node in Walk(root))
        {
            var mapped = UniquePreserve(DescendantNumericCards(node));
            if (mapped.Count is < 20 or > 80) continue;

            foreach (var merchantId in _merchants.Keys)
            {
                var projected = UniquePreserve(mapped.Where(card =>
                {
                    var parsed = ParseCard(card);
                    if (parsed is null) return false;
                    return parsed.Value.MerchantId.Equals(merchantId, StringComparison.OrdinalIgnoreCase)
                        || (!merchantId.Equals("SK_3", StringComparison.OrdinalIgnoreCase)
                            && parsed.Value.MerchantId.Equals("SK_3", StringComparison.OrdinalIgnoreCase));
                }));

                if (projected.Count < AppConfig.ExpectedCardsPerDeck) continue;
                for (var start = 0; start <= projected.Count - AppConfig.ExpectedCardsPerDeck; start++)
                    consider(projected.Skip(start).Take(AppConfig.ExpectedCardsPerDeck), "numeric-merchant-projection", 0);
            }
        }

        // A whole response can hold multiple deck records. Preserve traversal order
        // and try exact 20-card windows, just like the mature Companion collector.
        var ordered = UniquePreserve(DescendantNumericCards(root));
        if (ordered.Count is >= 20 and <= 520)
        {
            for (var i = 0; i <= ordered.Count - 20; i++)
                consider(ordered.Skip(i).Take(20), "numeric-root-window20", 0);
        }
    }

    private int LearnNumericReferences(ProtoNode root)
    {
        var before = _stableRefs.Count;

        foreach (var node in Walk(root))
        {
            var cards = UniquePreserve(DescendantCards(node).Where(_validCards.Contains));
            if (cards.Count != 1) continue;

            var values = NumericValues(node).ToArray();
            if (values.Length == 0 || values.Length > 40) continue;
            var card = cards[0];

            foreach (var (field, value) in values)
            {
                if (value < 9 || value > 2_000_000) continue;
                var weight = field is 1 or 4 ? 5 : value >= 32 ? 2 : 1;
                if (!_refVotes.TryGetValue(value, out var votes))
                {
                    votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    _refVotes[value] = votes;
                }
                votes[card] = votes.GetValueOrDefault(card) + weight;
            }
        }

        RebuildStableNumericRefs();
        return _stableRefs.Count - before;
    }

    private void RebuildStableNumericRefs()
    {
        _stableRefs.Clear();
        foreach (var (value, votes) in _refVotes)
        {
            if (votes.Count == 0) continue;
            var ordered = votes.OrderByDescending(x => x.Value).ToArray();
            var top = ordered[0];
            var second = ordered.Length > 1 ? ordered[1].Value : 0;
            var total = ordered.Sum(x => x.Value);
            var dominance = top.Value / (double)Math.Max(1, total);
            if (top.Value >= 3 && dominance >= 0.78 && top.Value >= second * 2)
                _stableRefs[value] = top.Key;
        }
    }

    private IEnumerable<(int Field, ulong Value)> NumericValues(ProtoNode node)
    {
        foreach (var pair in node.DirectVarints)
            foreach (var value in pair.Value)
                yield return (pair.Key, value);
        foreach (var pair in node.PackedVarints)
            foreach (var value in pair.Value)
                yield return (pair.Key, value);
    }

    private IEnumerable<string> DescendantNumericCards(ProtoNode node)
    {
        foreach (var (_, value) in NumericValues(node))
            if (_stableRefs.TryGetValue(value, out var card) && _validCards.Contains(card))
                yield return card;
        foreach (var child in node.Children)
            foreach (var card in DescendantNumericCards(child))
                yield return card;
    }

    private void RememberRecentPayload(byte[] data, string path)
    {
        var copy = data.ToArray();
        _recentPayloads.Add(new RecentPayload(copy, path));
        _recentPayloadBytes += copy.Length;
        while (_recentPayloads.Count > MaxRecentPayloads || _recentPayloadBytes > MaxRecentPayloadBytes)
        {
            _recentPayloadBytes -= _recentPayloads[0].Data.Length;
            _recentPayloads.RemoveAt(0);
        }
    }

    private void LoadPersistedNumericRefs()
    {
        var candidates = new[]
        {
            AppPaths.NumericReferenceMap,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ArcaneRushCompagnon", "output", "numeric_reference_map.json")
        };

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement refs;
                if (doc.RootElement.TryGetProperty("stableRefs", out var fromCompanion))
                    refs = fromCompanion;
                else if (doc.RootElement.TryGetProperty("stable_refs", out var alternate))
                    refs = alternate;
                else
                    continue;

                if (refs.ValueKind != JsonValueKind.Object) continue;
                foreach (var row in refs.EnumerateObject())
                {
                    if (!ulong.TryParse(row.Name, out var value) || value < 9 || value > 2_000_000)
                        continue;
                    var card = (row.Value.GetString() ?? "").ToUpperInvariant();
                    if (!_validCards.Contains(card)) continue;
                    if (!_refVotes.TryGetValue(value, out var votes))
                    {
                        votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        _refVotes[value] = votes;
                    }
                    votes[card] = votes.GetValueOrDefault(card) + 6;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Numeric reference map ignored: " + ex.Message);
            }
        }

        RebuildStableNumericRefs();
        if (_stableRefs.Count > 0)
            AppLog.Info($"Loaded {_stableRefs.Count} stable numeric card references.");
    }

    private void SavePersistedNumericRefs()
    {
        try
        {
            AppPaths.EnsureDirectories();
            var payload = new Dictionary<string, object?>
            {
                ["version"] = 1,
                ["count"] = _stableRefs.Count,
                ["stableRefs"] = _stableRefs.ToDictionary(
                    pair => pair.Key.ToString(),
                    pair => pair.Value,
                    StringComparer.Ordinal)
            };
            var temp = AppPaths.NumericReferenceMap + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, AppPaths.NumericReferenceMap, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Numeric reference map save failed: " + ex.Message);
        }
    }

    private static IEnumerable<ProtoNode> Walk(ProtoNode root)
    {
        var stack = new Stack<ProtoNode>();
        stack.Push(root);
        var emitted = 0;
        while (stack.Count > 0 && emitted++ < 20000)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }
    }

    private static IEnumerable<string> DescendantCards(ProtoNode node)
    {
        foreach (var card in node.DirectCards) yield return card;
        foreach (var child in node.Children)
            foreach (var card in DescendantCards(child))
                yield return card;
    }

    private bool TryParseProtobuf(byte[] data, out ProtoNode root)
    {
        var nodes = 0;
        var parsed = TryParseMessage(data.AsSpan(), 0, 0, ref nodes);
        root = parsed.Node ?? new ProtoNode(0);
        // The validated Companion accepts a partially framed/wrapped root when a
        // meaningful prefix is protobuf. Nested children remain much stricter.
        return parsed.Node is not null && parsed.Fraction >= 0.35;
    }

    private (ProtoNode? Node, double Fraction) TryParseMessage(
        ReadOnlySpan<byte> bytes,
        int fieldNumber,
        int depth,
        ref int nodes)
    {
        if (bytes.Length == 0 || depth > 13 || nodes >= 220000)
            return (null, 0.0);

        var node = new ProtoNode(fieldNumber);
        var pos = 0;
        var parsedBytes = 0;
        var fields = 0;
        nodes++;

        while (pos < bytes.Length && nodes < 220000)
        {
            var fieldStart = pos;
            if (!TryReadVarint(bytes, ref pos, out var tag) || tag == 0) break;

            var wire = (int)(tag & 7);
            var field = (int)(tag >> 3);
            if (field <= 0) break;

            var ok = true;
            switch (wire)
            {
                case 0:
                    ok = TryReadVarint(bytes, ref pos, out var scalar);
                    if (ok && scalar <= 10_000_000)
                    {
                        if (!node.DirectVarints.TryGetValue(field, out var scalarValues))
                        {
                            scalarValues = new List<ulong>();
                            node.DirectVarints[field] = scalarValues;
                        }
                        scalarValues.Add(scalar);
                    }
                    break;

                case 1:
                    if (pos + 8 > bytes.Length) ok = false;
                    else pos += 8;
                    break;

                case 5:
                    if (pos + 4 > bytes.Length) ok = false;
                    else pos += 4;
                    break;

                case 2:
                    if (!TryReadVarint(bytes, ref pos, out var len64) || len64 > int.MaxValue)
                    {
                        ok = false;
                        break;
                    }

                    var len = (int)len64;
                    if (len < 0 || pos + len > bytes.Length)
                    {
                        ok = false;
                        break;
                    }

                    var payload = bytes.Slice(pos, len);
                    pos += len;

                    // Match the proven Companion parser: a length-delimited value is
                    // considered a direct card only when the WHOLE field is one SK id.
                    // This avoids treating arbitrary text blobs as deck records.
                    var text = Encoding.UTF8.GetString(payload).Trim();
                    if (text.EndsWith("_Gold", StringComparison.OrdinalIgnoreCase))
                        text = text[..^5];
                    var upper = text.ToUpperInvariant();
                    if (_validCards.Contains(upper) && CardRegex.IsMatch(upper))
                        node.DirectCards.Add(upper);

                    // Length-delimited protobuf fields may also be packed varints.
                    if (TryParsePackedVarints(payload, out var packed) && packed.Count >= 2)
                    {
                        if (!node.PackedVarints.TryGetValue(field, out var packedValues))
                        {
                            packedValues = new List<ulong>();
                            node.PackedVarints[field] = packedValues;
                        }
                        packedValues.AddRange(packed);
                    }

                    // Child protobuf candidates must be at least 80% structurally
                    // consumed, exactly like the validated Python collector.
                    var beforeChild = nodes;
                    var child = TryParseMessage(payload, field, depth + 1, ref nodes);
                    if (child.Node is not null && child.Fraction >= 0.80)
                    {
                        node.Children.Add(child.Node);
                    }
                    else
                    {
                        // A failed speculative parse must not consume our global node budget.
                        nodes = beforeChild;
                    }
                    break;

                default:
                    // Deprecated protobuf groups and invalid wire types: stop at the
                    // valid prefix instead of rejecting the entire root message.
                    ok = false;
                    break;
            }

            if (!ok) break;
            fields++;
            parsedBytes = pos;
            if (pos <= fieldStart) break;
        }

        var fraction = parsedBytes / (double)Math.Max(1, bytes.Length);
        return fields > 0 ? (node, fraction) : (null, fraction);
    }

    private static bool TryParsePackedVarints(ReadOnlySpan<byte> bytes, out List<ulong> values)
    {
        values = new List<ulong>();
        if (bytes.Length == 0 || bytes.Length > 4096) return false;
        var pos = 0;
        while (pos < bytes.Length)
        {
            if (!TryReadVarint(bytes, ref pos, out var value) || value > 10_000_000)
            {
                values.Clear();
                return false;
            }
            values.Add(value);
            if (values.Count > 1024)
            {
                values.Clear();
                return false;
            }
        }
        return values.Count > 0;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> bytes, ref int position, out ulong value)
    {
        value = 0;
        var shift = 0;

        // A protobuf varint is at most 10 bytes for an unsigned 64-bit value.
        for (var i = 0; i < 10; i++)
        {
            if ((uint)position >= (uint)bytes.Length)
                return false;

            var current = bytes[position++];
            if (i == 9 && current > 1)
                return false;

            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
                return true;

            shift += 7;
        }

        return false;
    }

    private (int Score, string MerchantId)? ChooseMerchant(IReadOnlyList<string> ids)
    {
        (int Score, string MerchantId)? best = null;
        foreach (var merchantId in _merchants.Keys)
        {
            var score = MerchantScore(ids, merchantId);
            if (score <= 0) continue;
            if (best is null || score > best.Value.Score)
                best = (score, merchantId);
        }
        return best;
    }

    private int MerchantScore(IReadOnlyList<string> sourceIds, string merchantId)
    {
        var ids = UniquePreserve(sourceIds);
        if (ids.Count is < 15 or > 25) return 0;
        var parsed = ids.Select(ParseCard).Where(x => x is not null).Select(x => x!.Value).ToArray();
        var own = parsed.Count(p => p.MerchantId.Equals(merchantId, StringComparison.OrdinalIgnoreCase));
        var foreign = parsed.Count(p => !p.MerchantId.Equals(merchantId, StringComparison.OrdinalIgnoreCase)
                                     && !(merchantId != "SK_3" && p.MerchantId == "SK_3"));
        var allowedFamilies = parsed
            .Where(p => p.MerchantId == merchantId || (merchantId != "SK_3" && p.MerchantId == "SK_3"))
            .Select(p => p.Family)
            .Distinct()
            .Count();

        if (merchantId == "SK_3" && parsed.Any(p => p.MerchantId != "SK_3")) return 0;
        if (merchantId != "SK_3" && own < 2) return 0;

        var score = ids.Count == 20 ? 42 : ids.Count is >= 18 and <= 22 ? 26 : 10;
        score += foreign == 0 ? 30 : Math.Max(0, 24 - foreign * 8);
        score += allowedFamilies >= 18 ? 16 : allowedFamilies >= 14 ? 9 : allowedFamilies >= 10 ? 4 : 0;
        if (merchantId != "SK_3") score += own >= 10 ? 9 : own >= 5 ? 5 : 2;
        return Math.Min(100, score);
    }

    private static (string MerchantId, int Family)? ParseCard(string cardId)
    {
        var parts = cardId.Split('_');
        if (parts.Length < 4 || !int.TryParse(parts[1], out var merchant) || !int.TryParse(parts[2], out var family))
            return null;
        return ($"SK_{merchant}", family);
    }

    private static List<string> UniquePreserve(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var id in ids)
        {
            var upper = id.ToUpperInvariant();
            if (seen.Add(upper)) result.Add(upper);
        }
        return result;
    }

    public static string CleanPlayerName(string? value)
    {
        var name = (value ?? "").Trim().Trim('"', '\'', ' ');
        if (name.Length is < 2 or > 40) return "";
        var lower = name.ToLowerInvariant();
        if (name.Contains('@') || lower.StartsWith("http://") || lower.StartsWith("https://") || lower.StartsWith("sk_") || lower.StartsWith("relic_") || lower.StartsWith("random_"))
            return "";
        return name.Any(char.IsLetterOrDigit) ? name : "";
    }


    private static HashSet<string> LoadKnownIdentityNoise(string dataDirectory, string catalogPath, string merchantPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ENERGY", "GEM", "FR", "bundle", "asset", "Ltd", "PassPoint", "LeagueReport",
            "All", "Discord", "Facebook", "Wiki", "Google", "Steam", "Reddit", "Twitter", "YouTube",
            "Instagram", "TikTok", "Support", "Privacy", "Terms", "Website", "News"
        };

        static void AddHumanName(HashSet<string> target, string? value)
        {
            var text = (value ?? "").Trim();
            if (text.Length < 2) return;
            target.Add(text);
            foreach (var token in Regex.Split(text, @"[^A-Za-zÀ-ÿ0-9]+"))
                if (token.Length >= 3) target.Add(token);
        }

        try
        {
            var heroesPath = Path.Combine(dataDirectory, "heroes.json");
            if (File.Exists(heroesPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(heroesPath));
                if (doc.RootElement.TryGetProperty("heroes", out var heroes) && heroes.ValueKind == JsonValueKind.Array)
                    foreach (var hero in heroes.EnumerateArray())
                        if (hero.TryGetProperty("name", out var name)) AddHumanName(result, name.GetString());
            }
        }
        catch (Exception ex) { AppLog.Warn("Hero-name identity filter ignored: " + ex.Message); }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
            if (doc.RootElement.TryGetProperty("dealers", out var dealers) && dealers.ValueKind == JsonValueKind.Array)
            {
                foreach (var dealer in dealers.EnumerateArray())
                {
                    if (dealer.TryGetProperty("dealerName", out var dealerName)) AddHumanName(result, dealerName.GetString());
                    if (dealer.TryGetProperty("factionName", out var factionName)) AddHumanName(result, factionName.GetString());
                    if (!dealer.TryGetProperty("families", out var families) || families.ValueKind != JsonValueKind.Array) continue;
                    foreach (var family in families.EnumerateArray())
                    {
                        if (family.TryGetProperty("familyName", out var familyName)) AddHumanName(result, familyName.GetString());
                        if (!family.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Array) continue;
                        foreach (var variant in variants.EnumerateArray())
                            if (variant.TryGetProperty("name", out var cardName)) AddHumanName(result, cardName.GetString());
                    }
                }
            }
        }
        catch { }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(merchantPath));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.TryGetProperty("name", out var name)) AddHumanName(result, name.GetString());
                if (prop.Value.TryGetProperty("faction", out var faction)) AddHumanName(result, faction.GetString());
            }
        }
        catch { }

        return result;
    }

    private static Dictionary<string, MerchantInfo> LoadMerchants(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, MerchantInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var name = prop.Value.TryGetProperty("name", out var n) ? n.GetString() ?? prop.Name : prop.Name;
            var faction = prop.Value.TryGetProperty("faction", out var f) ? f.GetString() ?? "" : "";
            result[prop.Name.ToUpperInvariant()] = new MerchantInfo(name, faction);
        }
        return result;
    }

    private static HashSet<string> LoadValidCards(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dealer in doc.RootElement.GetProperty("dealers").EnumerateArray())
            foreach (var family in dealer.GetProperty("families").EnumerateArray())
                foreach (var variant in family.GetProperty("variants").EnumerateArray())
                    if (variant.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
                        result.Add(id.GetString()!.ToUpperInvariant());
        return result;
    }

    private static HashSet<string> LoadStarterCards(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dealer in doc.RootElement.GetProperty("dealers").EnumerateArray())
            foreach (var family in dealer.GetProperty("families").EnumerateArray())
                foreach (var variant in family.GetProperty("variants").EnumerateArray())
                {
                    var isStarter = variant.TryGetProperty("isStarter", out var starter)
                        && starter.ValueKind == JsonValueKind.True;
                    if (isStarter
                        && variant.TryGetProperty("id", out var id)
                        && !string.IsNullOrWhiteSpace(id.GetString()))
                        result.Add(id.GetString()!.ToUpperInvariant());
                }
        return result;
    }

    private sealed class ProtoNode
    {
        public ProtoNode(int fieldNumber) => FieldNumber = fieldNumber;
        public int FieldNumber { get; }
        public Dictionary<int, List<ulong>> DirectVarints { get; } = new();
        public Dictionary<int, List<ulong>> PackedVarints { get; } = new();
        public List<string> DirectCards { get; } = new();
        public List<ProtoNode> Children { get; } = new();
    }

    private readonly record struct CardHit(int Offset, string CardId);
    private readonly record struct CollectionCardHit(int Start, int End, string CardId);
    private sealed record RecentPayload(byte[] Data, string Path);
}
