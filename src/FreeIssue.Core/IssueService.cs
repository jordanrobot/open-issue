namespace FreeIssue.Core;

public sealed class IssueService(IssueRepository repository)
{
    public IssueRepository Repository { get; } = repository;

    public IssueListResult List(IssueFilter? filter = null)
    {
        var result = Repository.List();
        IEnumerable<Issue> issues = result.Issues;
        if (filter?.Status is { } status)
        {
            issues = issues.Where(issue => issue.Status == status);
        }

        if (filter?.Tag is { Length: > 0 } tag)
        {
            issues = issues.Where(issue => issue.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        }

        if (filter?.Priority is { } priority)
        {
            issues = issues.Where(issue => issue.Priority == priority);
        }

        if (filter?.Ready is { } ready)
        {
            issues = issues.Where(issue => issue.Ready == ready);
        }

        return new IssueListResult(issues.OrderBy(issue => issue.Status)
            .ThenByDescending(issue => issue.Priority)
            .ThenBy(issue => issue.CreatedAt)
            .ThenBy(issue => issue.Id, StringComparer.Ordinal).ToArray(), result.Diagnostics);
    }

    public Issue Resolve(string idOrPrefix)
    {
        var candidates = Repository.List().Issues
            .Where(issue => issue.Id.StartsWith(idOrPrefix, StringComparison.Ordinal))
            .ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new IssueException($"No issue matches ID or prefix '{idOrPrefix}'."),
            _ => throw new IssueException($"Issue ID prefix '{idOrPrefix}' is ambiguous; use a longer prefix.")
        };
    }

    public Issue Show(string idOrPrefix) => Resolve(idOrPrefix);

    public Issue Create(string title, string body, IssuePriority priority = IssuePriority.Normal, IEnumerable<string>? tags = null)
    {
        var now = DateTimeOffset.UtcNow;
        return Repository.Create(new Issue
        {
            Id = Guid.NewGuid().ToString("D"),
            Title = title,
            Body = body,
            Priority = priority,
            Tags = tags?.ToList() ?? [],
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    public Issue Edit(
        string idOrPrefix,
        string? title = null,
        string? body = null,
        IssuePriority? priority = null,
        IEnumerable<string>? tags = null,
        string? parent = null,
        IEnumerable<string>? related = null,
        IEnumerable<string>? blocks = null)
    {
        var current = Resolve(idOrPrefix);
        if (title is not null)
        {
            current.Title = title;
        }

        if (body is not null)
        {
            current.Body = body;
        }

        if (priority is not null)
        {
            current.Priority = priority.Value;
        }

        if (tags is not null)
        {
            current.Tags = tags.ToList();
        }

        if (parent is not null)
        {
            current.Parent = parent.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : Resolve(parent).Id;
        }

        if (related is not null)
        {
            current.Related = related.Select(reference => Resolve(reference).Id).ToList();
        }

        if (blocks is not null)
        {
            current.Blocks = blocks.Select(reference => Resolve(reference).Id).ToList();
        }

        current.UpdatedAt = DateTimeOffset.UtcNow;
        return Repository.Update(current, current.Revision);
    }

    public Issue SetStatus(string idOrPrefix, IssueStatus status)
    {
        var current = Resolve(idOrPrefix);
        current.Status = status;
        if (status != IssueStatus.Open)
        {
            current.Ready = false;
        }

        current.UpdatedAt = DateTimeOffset.UtcNow;
        return Repository.Update(current, current.Revision);
    }

    public Issue SetReady(string idOrPrefix, bool ready)
    {
        var current = Resolve(idOrPrefix);
        current.Ready = ready;
        current.UpdatedAt = DateTimeOffset.UtcNow;
        return Repository.Update(current, current.Revision);
    }

    public IReadOnlyList<Issue> Search(string query, IssueFilter? filter = null)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return List(filter).Issues.Where(issue =>
                terms.All(term => issue.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                  issue.Body.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                  issue.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
    }

    public Issue? Next()
    {
        var issues = Repository.List().Issues;
        var blocked = issues.SelectMany(issue => issue.Blocks).ToHashSet(StringComparer.Ordinal);
        return issues.Where(issue => issue.Status == IssueStatus.Open && issue.Ready && !blocked.Contains(issue.Id))
            .OrderByDescending(issue => issue.Priority)
            .ThenBy(issue => issue.CreatedAt)
            .ThenBy(issue => issue.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
