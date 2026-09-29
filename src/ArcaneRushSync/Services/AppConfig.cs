namespace ArcaneRushSync.Services;

public static class AppConfig
{
    public static string Version
    {
        get
        {
            var v = typeof(AppConfig).Assembly.GetName().Version;
            return v is null ? "1.1.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    public static string GitHubRepository
    {
        get
        {
            return typeof(AppConfig).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .OfType<System.Reflection.AssemblyMetadataAttribute>()
                .FirstOrDefault(x => string.Equals(x.Key, "GitHubRepository", StringComparison.Ordinal))
                ?.Value?.Trim() ?? string.Empty;
        }
    }

    public const string UpdateAssetName = "ArcaneRushSync-win-x64.zip";
    public const string SiteUrl = "https://arcane-rush-compagnon-2026.web.app";
    public const string GameProcessName = "ArcaneRush";
    public const string ApiHost = "api-overhaul.cbg.alleylabs.com";

    // Endpoints manually validated against equipped decks in the existing Companion.
    // /001003 is special: it also carries the collection, so it is accepted ONLY by
    // DeckDetector's strict nested-deck extractor (20 cards / 20 families / one merchant).
    public static readonly HashSet<string> ValidatedDeckEndpoints = new(StringComparer.Ordinal)
    {
        "/001003",
        "/113021",
        "/112003"
    };

    // The old working Companion captured complete merchant loadouts on /001004 too.
    // Keep it under generic scoring because it can carry other card lists as well.
    public static readonly HashSet<string> ObservedDeckEndpoints = new(StringComparer.Ordinal)
    {
        "/001004",
        "/002001"
    };

    // /001003 contains both collection and deck submessages. It is NOT a generic deck
    // endpoint: only its strict nested 20-family records may become decks.
    public static readonly HashSet<string> StructuredDeckEndpoints = new(StringComparer.Ordinal)
    {
        "/001003"
    };

    // These payloads can contain game/run pools or GDC state shaped exactly like a
    // 20-card deck. They must never be accepted as equipped decks.
    public static readonly HashSet<string> NonDeckEndpoints = new(StringComparer.Ordinal)
    {
        "/112010",
        "/113010",
        "/113011",
        "/119008",
        "/119007",
        "/119022",
        "/119023"
    };

    public static bool IsDeckEligibleEndpoint(string path) =>
        StructuredDeckEndpoints.Contains(path) || !NonDeckEndpoints.Contains(path);

    public static readonly string[] ExpectedMerchantIds =
    {
        "SK_1", "SK_2", "SK_3", "SK_4", "SK_5", "SK_7", "SK_8",
        "SK_9", "SK_10", "SK_12", "SK_14", "SK_15", "SK_16"
    };

    public const int ExpectedDeckCount = 13;
    public const int ExpectedCardsPerDeck = 20;
    public const int MaxCapturedBodyBytes = 4 * 1024 * 1024;
    public const int GenericDeckConfidence = 90;
}
