namespace ArcaneRushSync.Models;

public sealed class LiveGameState
{
    public IReadOnlyList<string> ActiveFactionDealers { get; init; } = Array.Empty<string>();
    public string NeutralDealer { get; init; } = "SK_3";
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RunPools { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    public int TavernTier { get; init; }
    public IReadOnlyList<string> Shop { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Hand { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Board { get; init; } = Array.Empty<string>();
    public string SourcePath { get; init; } = "";
    public string SourceKind { get; init; } = "";
    public int Confidence { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool HasRunPools => RunPools.Count >= 4;
    public bool HasShop => Shop.Count > 0;

    public IReadOnlyList<string> AllRunCards =>
        RunPools.Values
            .SelectMany(cards => cards)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

public sealed record CardDisplayInfo(
    string Id,
    string Name,
    string Rarity,
    int Cost,
    string Effect,
    string DealerId,
    string DealerName,
    string Faction,
    int TavernTier,
    string ImageUrl);

public sealed record FactionDisplayInfo(
    string DealerId,
    string DealerName,
    string Faction);

public sealed record TavernCardGroup(
    int Tier,
    IReadOnlyList<CardDisplayInfo> Cards);
