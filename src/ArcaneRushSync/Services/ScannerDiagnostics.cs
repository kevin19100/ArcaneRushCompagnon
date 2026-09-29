using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ArcaneRushSync.Services;

public sealed record TrafficObservation(
    DateTimeOffset At,
    string Direction,
    string Method,
    string Path,
    int StatusCode,
    int BodyBytes,
    string ContentType,
    string BodySha256,
    IReadOnlyList<string> StrongNameCandidates,
    IReadOnlyList<string> NameLikeStrings);

public static class ScannerDiagnosticExtractor
{
    private static readonly Regex[] StrongPatterns =
    {
        new(@"""playerName""\s*:\s*""(?<v>[^""\r\n]{2,40})""", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"""displayName""\s*:\s*""(?<v>[^""\r\n]{2,40})""", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"""userName""\s*:\s*""(?<v>[^""\r\n]{2,40})""", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"""username""\s*:\s*""(?<v>[^""\r\n]{2,40})""", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"""nickname""\s*:\s*""(?<v>[^""\r\n]{2,40})""", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant),
    };

    private static readonly Regex QuotedString = new(
        "\\\"(?<v>[^\\\"\\r\\n]{2,40})\\\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Protobuf strings do not need to be JSON quoted. Keep only short human-looking
    // printable runs; IDs, URLs, card IDs and protocol vocabulary are filtered below.
    private static readonly Regex PrintableRun = new(
        @"(?<![A-Za-z0-9_])(?<v>[A-Za-zÀ-ÿ0-9][A-Za-zÀ-ÿ0-9 .’'\-]{1,39})(?![A-Za-z0-9_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "playerName", "displayName", "userName", "username", "nickname", "playerId",
        "shopkeeperId", "cardId", "cards", "collection", "profile", "account", "player",
        "request", "response", "success", "status", "version", "data", "true", "false",
        "Arcane Rush", "Windows", "Firebase", "Google", "Steam"
    };

    public static TrafficObservation Build(
        string direction,
        string method,
        string path,
        int statusCode,
        string? contentType,
        byte[]? body)
    {
        body ??= Array.Empty<byte>();
        var strong = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (body.Length > 0)
        {
            // UTF-8 is enough for JSON/text responses. Invalid binary bytes are replaced,
            // while printable protobuf strings such as a player name remain visible.
            var text = Encoding.UTF8.GetString(body);
            foreach (var regex in StrongPatterns)
            {
                foreach (Match match in regex.Matches(text))
                {
                    var value = DeckDetector.CleanPlayerName(match.Groups["v"].Value);
                    if (!string.IsNullOrWhiteSpace(value)) strong.Add(value);
                }
            }

            foreach (var value in ExtractNameLikeStrings(body, 80))
                names.Add(value);

            foreach (var value in strong)
                names.Add(value);
        }

        return new TrafficObservation(
            DateTimeOffset.UtcNow,
            direction,
            method,
            path,
            statusCode,
            body.Length,
            contentType ?? "",
            body.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(),
            strong.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(80).ToArray());
    }


    /// <summary>
    /// Extracts short printable strings from a protobuf/JSON payload without retaining the body.
    /// The player-name detector reuses this exact extractor because it is the path that exposed
    /// the real nickname in /001003 during field diagnostics.
    /// </summary>
    public static IReadOnlyList<string> ExtractNameLikeStrings(byte[] body, int maxItems = 512)
    {
        if (body is null || body.Length == 0 || maxItems <= 0)
            return Array.Empty<string>();

        var text = Encoding.UTF8.GetString(body);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Quoted strings first: in the account bootstrap they isolate values such as the
        // nickname much more reliably than one broad regex over the whole binary buffer.
        foreach (Match match in QuotedString.Matches(text))
        {
            var value = CleanPotentialName(match.Groups["v"].Value);
            if (!string.IsNullOrWhiteSpace(value)) names.Add(value);
            if (names.Count >= maxItems) break;
        }

        if (names.Count < maxItems)
        {
            foreach (Match match in PrintableRun.Matches(text))
            {
                var value = CleanPotentialName(match.Groups["v"].Value);
                if (!string.IsNullOrWhiteSpace(value)) names.Add(value);
                if (names.Count >= maxItems) break;
            }
        }

        return names.ToArray();
    }

    private static string CleanPotentialName(string value)
    {
        var text = value.Trim().Trim('"', '\'', ' ');
        if (text.Length is < 2 or > 40) return "";
        if (Noise.Contains(text)) return "";
        if (!text.Any(char.IsLetter)) return "";
        if (text.Contains('@') || text.Contains('/') || text.Contains('\\') || text.Contains(':')) return "";
        if (text.Contains('_')) return "";
        if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("SK", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Relic", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("RANDOM", StringComparison.OrdinalIgnoreCase)) return "";
        // Avoid dumping long prose from localisation/ability text into a support report.
        if (text.Count(char.IsWhiteSpace) > 3) return "";
        return text;
    }
}
