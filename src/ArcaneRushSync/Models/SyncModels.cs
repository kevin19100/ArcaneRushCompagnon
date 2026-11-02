namespace ArcaneRushSync.Models;

public sealed record MerchantInfo(string Name, string Faction);

public sealed record DetectedDeck(
    string MerchantId,
    string Name,
    string Faction,
    IReadOnlyList<string> CardIds,
    int Confidence,
    string SourcePath,
    DateTimeOffset CapturedAt);

public sealed class GameSnapshot
{
    public string PlayerName { get; set; } = "";
    public int PlayerNameConfidence { get; set; }
    public string PlayerNameSource { get; set; } = "";

    public Dictionary<string, DetectedDeck> Decks { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Full owned-card inventory captured from the startup /001003 response.
    // This is deliberately distinct from the 13 equipped decks: the website's
    // "Ma collection" page reads users/{uid}.ownedIds and therefore needs the
    // complete inventory, not only the cards currently equipped.
    public IReadOnlyList<string> OwnedCardIds { get; set; } = Array.Empty<string>();
    public bool CollectionSyncSafe { get; set; }
    public int CollectionConfidence { get; set; }
    public string CollectionMethod { get; set; } = "";

    // Diagnostic-only ownership provenance. These card IDs are safe to include in
    // support reports and let us audit exact false positives / recovered numeric records
    // without retaining any raw network body.
    public int CollectionAsciiCount { get; set; }
    public int CollectionNoiseCount { get; set; }
    public int CollectionNumericOnlyCount { get; set; }
    public int CollectionUnresolvedRecords { get; set; }
    public int CollectionCandidateRecords { get; set; }
    public IReadOnlyList<string> CollectionNumericOnlyIds { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> CollectionInferredIds { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> CollectionNoiseIds { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> CollectionRejectedNumericIds { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> CollectionDeckRecoveredIds { get; set; } = Array.Empty<string>();

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Moment where the last deck / collection change was observed. The UI waits for
    // a short quiet period after this before syncing a partial deck set.
    public DateTimeOffset LastDetectionChangeAt { get; set; } = DateTimeOffset.MinValue;

    public bool HasVerifiedPlayerName =>
        !string.IsNullOrWhiteSpace(PlayerName) && PlayerNameConfidence >= 75;

    // The exact owned-card inventory from the /001003 bootstrap is the only thing
    // strictly required to update "Ma collection" on the website.
    public bool IsCollectionReady =>
        CollectionSyncSafe
        && CollectionMethod.StartsWith("bootstrap-exact-ascii", StringComparison.Ordinal)
        && OwnedCardIds.Count > 0;

    public IEnumerable<DetectedDeck> ValidDecks =>
        Decks.Values.Where(d => d.CardIds.Count == 20);

    // Everything the game can give us: verified name, exact collection and one deck
    // per merchant listed in merchant_map.json.
    public bool IsComplete(int expectedDecks) =>
        HasVerifiedPlayerName
        && IsCollectionReady
        && ValidDecks.Count() >= expectedDecks;

    public GameSnapshot Clone()
    {
        var copy = new GameSnapshot
        {
            PlayerName = PlayerName,
            PlayerNameConfidence = PlayerNameConfidence,
            PlayerNameSource = PlayerNameSource,
            OwnedCardIds = OwnedCardIds.ToArray(),
            CollectionSyncSafe = CollectionSyncSafe,
            CollectionConfidence = CollectionConfidence,
            CollectionMethod = CollectionMethod,
            CollectionAsciiCount = CollectionAsciiCount,
            CollectionNoiseCount = CollectionNoiseCount,
            CollectionNumericOnlyCount = CollectionNumericOnlyCount,
            CollectionUnresolvedRecords = CollectionUnresolvedRecords,
            CollectionCandidateRecords = CollectionCandidateRecords,
            CollectionNumericOnlyIds = CollectionNumericOnlyIds.ToArray(),
            CollectionInferredIds = CollectionInferredIds.ToArray(),
            CollectionNoiseIds = CollectionNoiseIds.ToArray(),
            CollectionRejectedNumericIds = CollectionRejectedNumericIds.ToArray(),
            CollectionDeckRecoveredIds = CollectionDeckRecoveredIds.ToArray(),
            LastDetectionChangeAt = LastDetectionChangeAt,
            UpdatedAt = UpdatedAt
        };
        foreach (var pair in Decks)
            copy.Decks[pair.Key] = pair.Value with { CardIds = pair.Value.CardIds.ToArray() };
        return copy;
    }
}

public sealed record FirebaseSession(
    string Uid,
    string Email,
    string IdToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string DisplayName = "");
