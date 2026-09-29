using System.Text;
using System.Text.RegularExpressions;

namespace ArcaneRushSync.Services;

public static class PlayerLogReader
{
    private static readonly Regex[] Patterns =
    {
        new(@"""playerName""\s*:\s*""([^""\r\n]{2,40})""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"""userName""\s*:\s*""([^""\r\n]{2,40})""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"""username""\s*:\s*""([^""\r\n]{2,40})""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"player\s*name\s*[:=]\s*[""']?([^""'\r\n|]{2,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"display\s*name\s*[:=]\s*[""']?([^""'\r\n|]{2,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"user\s*name\s*[:=]\s*[""']?([^""'\r\n|]{2,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"nickname\s*[:=]\s*[""']?([^""'\r\n|]{2,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    };

    public static string TryRead() => TryReadWithSource().Name;

    public static (string Name, string Source) TryReadWithSource()
    {
        // V1.0.6 deliberately does NOT import the old Companion's player_profile.json.
        // A previous heuristic could persist a false positive for weeks (for example a
        // random protocol token) and then prevent the fresh /001003 identity from ever
        // being considered. Only current Arcane Rush logs are accepted as a weak fallback;
        // a network identity discovered during this scan has higher confidence and wins.
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "AlleyLabs", "Arcane Rush");

        foreach (var file in new[] { "Player.log", "Player-prev.log" })
        {
            var path = Path.Combine(root, file);
            try
            {
                if (!File.Exists(path)) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var take = (int)Math.Min(stream.Length, 768 * 1024);
                if (take <= 0) continue;
                stream.Seek(-take, SeekOrigin.End);
                var bytes = new byte[take];
                _ = stream.Read(bytes, 0, take);
                var text = Encoding.UTF8.GetString(bytes);

                for (var p = Patterns.Length - 1; p >= 0; p--)
                {
                    var matches = Patterns[p].Matches(text);
                    for (var i = matches.Count - 1; i >= 0; i--)
                    {
                        var name = DeckDetector.CleanPlayerName(matches[i].Groups[1].Value);
                        if (!string.IsNullOrWhiteSpace(name)) return (name, file);
                    }
                }
            }
            catch { }
        }

        return ("", "");
    }
}
