namespace WebCrawler.Domain.Robots;

public sealed class RobotsRules
{
    private static readonly RobotsRules AllowAllInstance = new([], []);

    private readonly IReadOnlyList<string> _allowRules;
    private readonly IReadOnlyList<string> _disallowRules;

    private RobotsRules(IReadOnlyList<string> allowRules, IReadOnlyList<string> disallowRules)
    {
        _allowRules = allowRules;
        _disallowRules = disallowRules;
    }

    public static RobotsRules AllowAll() => AllowAllInstance;

    public static RobotsRules Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return AllowAll();
        }

        var allow = new List<string>();
        var disallow = new List<string>();
        var applies = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();

            if (line.Length == 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':');

            if (separatorIndex <= 0)
            {
                continue;
            }

            var directive = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (directive.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
            {
                applies = value == "*";
                continue;
            }

            if (!applies)
            {
                continue;
            }

            if (directive.Equals("Allow", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                allow.Add(value);
            }
            else if (directive.Equals("Disallow", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                disallow.Add(value);
            }
        }

        return new RobotsRules(allow, disallow);
    }

    public bool IsAllowed(Uri url)
    {
        var candidate = url.PathAndQuery;
        var longestAllow = MatchLength(_allowRules, candidate);
        var longestDisallow = MatchLength(_disallowRules, candidate);
        return longestAllow >= longestDisallow;
    }

    private static int MatchLength(IReadOnlyList<string> rules, string candidate)
    {
        var longest = 0;

        foreach (var rule in rules)
        {
            if (candidate.StartsWith(rule, StringComparison.Ordinal) && rule.Length > longest)
            {
                longest = rule.Length;
            }
        }

        return longest;
    }

    private static string StripComment(string line)
    {
        var commentIndex = line.IndexOf('#');
        return commentIndex >= 0 ? line[..commentIndex] : line;
    }
}
