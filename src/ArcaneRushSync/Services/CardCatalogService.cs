using System.Text.Json;
using ArcaneRushSync.Models;

namespace ArcaneRushSync.Services;

public sealed class CardCatalogService
{
    private readonly Dictionary<string, CardDisplayInfo> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FactionDisplayInfo> _factions = new(StringComparer.OrdinalIgnoreCase);

    public CardCatalogService(string dataDirectory)
    {
        LoadMerchants(Path.Combine(dataDirectory, "merchant_map.json"));
        LoadCards(Path.Combine(dataDirectory, "card_catalog.json"));
    }

    public CardDisplayInfo? TryGetCard(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _cards.TryGetValue(id.Trim(), out var card) ? card : null;
    }

    public IReadOnlyList<CardDisplayInfo> GetCards(IEnumerable<string> ids) =>
        ids.Select(TryGetCard).OfType<CardDisplayInfo>().ToArray();

    public FactionDisplayInfo? TryGetFaction(string? dealerId)
    {
        if (string.IsNullOrWhiteSpace(dealerId)) return null;
        return _factions.TryGetValue(dealerId.Trim(), out var faction) ? faction : null;
    }

    public IReadOnlyList<FactionDisplayInfo> GetFactions(IEnumerable<string> dealerIds) =>
        dealerIds.Select(TryGetFaction).OfType<FactionDisplayInfo>().ToArray();

    public IReadOnlyList<TavernCardGroup> GroupByTavernTier(IEnumerable<string> ids)
    {
        var cards = GetCards(ids)
            .GroupBy(card => card.TavernTier)
            .OrderBy(group => group.Key)
            .Select(group => new TavernCardGroup(
                group.Key,
                group
                    .OrderBy(card => card.DealerId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(card => FamilyNumber(card.Id))
                    .ThenBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .ToArray();

        return cards;
    }

    public static int TavernTierForCardId(string cardId)
    {
        var family = FamilyNumber(cardId);
        if (family is >= 1 and <= 3) return 1;
        if (family is >= 4 and <= 7) return 2;
        if (family is >= 8 and <= 11) return 3;
        if (family is >= 12 and <= 15) return 4;
        if (family is >= 16 and <= 20) return 5;
        return 0;
    }

    private void LoadMerchants(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var dealerId = property.Name;
            var value = property.Value;
            var name = ReadString(value, "name", dealerId);
            var faction = ReadString(value, "faction", dealerId);
            _factions[dealerId] = new FactionDisplayInfo(dealerId, name, faction);
        }
    }

    private void LoadCards(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("dealers", out var dealers)
            || dealers.ValueKind != JsonValueKind.Array)
            return;

        foreach (var dealer in dealers.EnumerateArray())
        {
            var dealerId = ReadString(dealer, "dealerId", "");
            if (string.IsNullOrWhiteSpace(dealerId)) continue;

            var faction = TryGetFaction(dealerId)
                ?? new FactionDisplayInfo(dealerId, dealerId, dealerId);

            if (!dealer.TryGetProperty("families", out var families)
                || families.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var family in families.EnumerateArray())
            {
                if (!family.TryGetProperty("variants", out var variants)
                    || variants.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var variant in variants.EnumerateArray())
                {
                    var id = ReadString(variant, "id", "");
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    var name = ReadString(variant, "name", id);
                    var rarity = ReadString(variant, "rarity", "");
                    var cost = ReadInt32(variant, "cost", 0);
                    var effect = ReadString(variant, "effect", "");

                    var imageUrl = $"{AppConfig.SiteUrl.TrimEnd('/')}/cards/{Uri.EscapeDataString(id)}.png";
                    _cards[id] = new CardDisplayInfo(
                        id,
                        name,
                        rarity,
                        cost,
                        effect,
                        dealerId,
                        faction.DealerName,
                        faction.Faction,
                        TavernTierForCardId(id),
                        imageUrl);
                }
            }
        }
    }

    private static string ReadString(JsonElement owner, string property, string fallback)
    {
        if (!owner.TryGetProperty(property, out var node))
            return fallback;

        return node.ValueKind switch
        {
            JsonValueKind.String => node.GetString() ?? fallback,
            JsonValueKind.Null or JsonValueKind.Undefined => fallback,
            _ => node.ToString()
        };
    }

    private static int ReadInt32(JsonElement owner, string property, int fallback)
    {
        if (!owner.TryGetProperty(property, out var node))
            return fallback;

        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var number))
            return number;

        if (node.ValueKind == JsonValueKind.String
            && int.TryParse(node.GetString(), out var parsed))
            return parsed;

        // Some catalogue entries legitimately have a null cost. That is display
        // metadata only and must never be able to crash Arcane Rush Sync.
        return fallback;
    }

    private static int FamilyNumber(string cardId)
    {
        var parts = (cardId ?? "").Split('_');
        return parts.Length >= 4 && int.TryParse(parts[2], out var family)
            ? family
            : 0;
    }
}
