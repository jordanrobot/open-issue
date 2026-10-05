using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenIssue.Core;

/// <summary>Parses issue references and dependency markers from issue bodies.</summary>
public static class IssueBodyLinkParser
{
    private static readonly Regex ReferencePattern = new(
        @"(?<![\p{L}\p{N}_#])#(?<id>[0-9]+)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DependencyPattern = new(
        @"(?<![\p{L}\p{N}_])Depends\s+on\s+#(?<id>[0-9]+)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Gets unique positive issue IDs mentioned as <c>#number</c> references in a body.</summary>
    /// <param name="body">The issue body to parse.</param>
    /// <returns>Referenced issue IDs in their order of appearance.</returns>
    public static IReadOnlyList<long> GetReferences(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ReadIssueIds(ReferencePattern.Matches(body));
    }

    /// <summary>Gets unique positive issue IDs mentioned with the <c>Depends on #number</c> syntax.</summary>
    /// <param name="body">The issue body to parse.</param>
    /// <returns>Dependency issue IDs in their order of appearance.</returns>
    public static IReadOnlyList<long> GetDependencies(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ReadIssueIds(DependencyPattern.Matches(body));
    }

    /// <summary>Finds the issue-reference token at the caret in a body being edited.</summary>
    /// <param name="text">The current body text.</param>
    /// <param name="caretIndex">The caret position in <paramref name="text"/>.</param>
    /// <param name="start">Receives the token's start position when found.</param>
    /// <param name="length">Receives the token's length when found.</param>
    /// <param name="idPrefix">Receives the numeric prefix typed before the caret.</param>
    /// <returns><see langword="true"/> when the caret is within a valid <c>#number</c> token or immediately after a trailing <c>#</c>.</returns>
    public static bool TryGetReferenceAtCaret(
        string text,
        int caretIndex,
        out int start,
        out int length,
        out string idPrefix)
    {
        ArgumentNullException.ThrowIfNull(text);
        if ((uint)caretIndex > (uint)text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(caretIndex));
        }

        start = 0;
        length = 0;
        idPrefix = string.Empty;

        var digitStart = caretIndex;
        while (digitStart > 0 && char.IsAsciiDigit(text[digitStart - 1]))
        {
            digitStart--;
        }

        if (digitStart == 0 || text[digitStart - 1] != '#')
        {
            return false;
        }

        var hashIndex = digitStart - 1;
        if (hashIndex > 0 &&
            (char.IsLetterOrDigit(text[hashIndex - 1]) || text[hashIndex - 1] is '_' or '#'))
        {
            return false;
        }

        var tokenEnd = caretIndex;
        while (tokenEnd < text.Length && char.IsAsciiDigit(text[tokenEnd]))
        {
            tokenEnd++;
        }

        if (tokenEnd < text.Length && (char.IsLetterOrDigit(text[tokenEnd]) || text[tokenEnd] == '_'))
        {
            return false;
        }

        if (tokenEnd == hashIndex + 1 && tokenEnd < text.Length)
        {
            return false;
        }

        start = hashIndex;
        length = tokenEnd - hashIndex;
        idPrefix = text[(hashIndex + 1)..caretIndex];
        return true;
    }

    /// <summary>Replaces a parsed issue-reference token and returns the new caret position.</summary>
    /// <param name="text">The current body text.</param>
    /// <param name="start">The token's start position.</param>
    /// <param name="length">The token's length.</param>
    /// <param name="issueId">The selected positive issue ID.</param>
    /// <param name="updatedText">Receives the body after replacement.</param>
    /// <param name="caretIndex">Receives the caret position immediately after the inserted reference.</param>
    /// <returns><see langword="true"/> when the requested range still contains an issue-reference token.</returns>
    public static bool TryReplaceReference(
        string text,
        int start,
        int length,
        long issueId,
        out string updatedText,
        out int caretIndex)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (issueId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(issueId));
        }

        updatedText = text;
        caretIndex = start;
        if (start < 0 ||
            length <= 0 ||
            start > text.Length - length ||
            !TryGetReferenceAtCaret(text, start + length, out var tokenStart, out var tokenLength, out _) ||
            tokenStart != start ||
            tokenLength != length)
        {
            return false;
        }

        var replacement = $"#{issueId.ToString(CultureInfo.InvariantCulture)}";
        updatedText = text[..start] + replacement + text[(start + length)..];
        caretIndex = start + replacement.Length;
        return true;
    }

    /// <summary>Suggests issues whose IDs match or are numerically closest to a typed issue-number prefix.</summary>
    /// <param name="issues">The available issues to suggest.</param>
    /// <param name="idPrefix">Digits typed after the <c>#</c> character.</param>
    /// <param name="maximumCount">The maximum number of suggestions to return.</param>
    /// <returns>Suggestions ordered with ID-prefix matches first, followed by nearby issue IDs.</returns>
    public static IReadOnlyList<Issue> SuggestIssues(
        IEnumerable<Issue> issues,
        string idPrefix,
        int maximumCount = 6)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(idPrefix);
        if (idPrefix.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new ArgumentException("An issue ID prefix can contain only digits.", nameof(idPrefix));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);

        var available = issues.OrderBy(issue => issue.Id).ToArray();
        if (idPrefix.Length == 0)
        {
            return available.Take(maximumCount).ToArray();
        }

        var prefixMatches = available
            .Where(issue => issue.Id.ToString(CultureInfo.InvariantCulture)
                .StartsWith(idPrefix, StringComparison.Ordinal))
            .OrderBy(issue => string.Equals(
                issue.Id.ToString(CultureInfo.InvariantCulture),
                idPrefix,
                StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(issue => issue.Id)
            .ToArray();
        if (prefixMatches.Length >= maximumCount ||
            !long.TryParse(idPrefix, NumberStyles.None, CultureInfo.InvariantCulture, out var target))
        {
            return prefixMatches.Take(maximumCount).ToArray();
        }

        var matchedIds = prefixMatches.Select(issue => issue.Id).ToHashSet();
        var nearest = available
            .Where(issue => !matchedIds.Contains(issue.Id))
            .OrderBy(issue => Math.Abs((decimal)issue.Id - target))
            .ThenBy(issue => issue.Id);
        return prefixMatches.Concat(nearest).Take(maximumCount).ToArray();
    }

    private static IReadOnlyList<long> ReadIssueIds(MatchCollection matches)
    {
        var ids = new List<long>();
        var seen = new HashSet<long>();
        foreach (Match match in matches)
        {
            if (long.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                id > 0 &&
                seen.Add(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
