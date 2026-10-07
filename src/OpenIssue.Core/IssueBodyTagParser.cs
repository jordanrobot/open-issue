using System.Text.RegularExpressions;

namespace OpenIssue.Core;

/// <summary>Parses inline tags from issue bodies.</summary>
public static class IssueBodyTagParser
{
    private static readonly Regex TagPattern = new(
        @"(?<![\p{L}\p{N}_#])#(?<tag>[\p{L}_][\p{L}\p{N}_-]*)(?![\p{L}\p{N}_-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Gets unique inline tags in their order of appearance.</summary>
    /// <param name="body">The issue body to parse.</param>
    /// <returns>Tag names without their leading <c>#</c> character.</returns>
    public static IReadOnlyList<string> GetTags(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TagPattern.Matches(body))
        {
            var tag = match.Groups["tag"].Value;
            if (seen.Add(tag))
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    /// <summary>Gets the ranges of every inline tag marker in the body, including repeated tags.</summary>
    /// <param name="body">The issue body to parse.</param>
    /// <returns>Start indices and lengths that include each tag's leading <c>#</c> character.</returns>
    public static IReadOnlyList<(int Start, int Length)> GetTagRanges(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        return TagPattern.Matches(body)
            .Cast<Match>()
            .Select(match => (Start: match.Index, Length: match.Length))
            .ToArray();
    }

    /// <summary>Removes all matching inline tag markers while preserving surrounding body text.</summary>
    /// <param name="body">The issue body to update.</param>
    /// <param name="tag">The tag name without its leading <c>#</c> character.</param>
    /// <returns>The body with exact, case-insensitive matches removed.</returns>
    public static string RemoveInlineTag(string body, string tag)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        var matches = TagPattern.Matches(body)
            .Cast<Match>()
            .Where(match => string.Equals(
                match.Groups["tag"].Value,
                tag,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var updatedBody = body;
        foreach (var match in matches.Reverse())
        {
            updatedBody = updatedBody.Remove(match.Index, match.Length);
        }

        return updatedBody;
    }
}
