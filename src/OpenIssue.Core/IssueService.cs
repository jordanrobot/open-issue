namespace OpenIssue.Core;

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

        if (filter?.Draft is { } draft)
        {
            issues = issues.Where(issue => issue.Draft == draft);
        }

        return new IssueListResult(issues.OrderBy(issue => issue.Status)
            .ThenByDescending(issue => issue.Priority)
            .ThenBy(issue => issue.CreatedAt)
            .ThenBy(issue => issue.Id).ToArray(), result.Diagnostics);
    }

    public Issue Resolve(string idOrPrefix)
    {
        if (!long.TryParse(idOrPrefix, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var id) || id < 1)
        {
            throw new IssueException($"Issue ID must be a positive integer: '{idOrPrefix}'.");
        }

        return Repository.Read(id);
    }

    public Issue Show(string idOrPrefix) => Resolve(idOrPrefix);

    public Issue Create(string title, string body, IssuePriority priority = IssuePriority.Normal, IEnumerable<string>? tags = null)
    {
        var now = DateTimeOffset.UtcNow;
        return Repository.Create(new Issue
        {
            Id = 0,
            Title = title,
            Body = body,
            Draft = true,
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
        IEnumerable<long>? parents = null,
        IEnumerable<long>? children = null,
        IEnumerable<long>? related = null,
        IEnumerable<long>? blocks = null,
        IssueStatus? status = null,
        bool? draft = null,
        string? expectedRevision = null)
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

        if (parents is not null)
        {
            current.Parents = parents.ToList();
        }

        if (children is not null)
        {
            current.Children = children.ToList();
        }

        if (related is not null)
        {
            current.Related = related.ToList();
        }

        if (blocks is not null)
        {
            current.Blocks = blocks.ToList();
        }

        if (status is not null)
        {
            current.Status = status.Value;
        }

        if (draft is not null)
        {
            current.Draft = draft.Value;
        }

        if (current.Status != IssueStatus.Open)
        {
            current.Draft = true;
        }

        current.UpdatedAt = DateTimeOffset.UtcNow;
        return Repository.Update(current, expectedRevision ?? current.Revision);
    }

    public Issue SetStatus(string idOrPrefix, IssueStatus status)
    {
        var current = Resolve(idOrPrefix);
        current.Status = status;
        if (status != IssueStatus.Open)
        {
            current.Draft = true;
        }

        current.UpdatedAt = DateTimeOffset.UtcNow;
        return Repository.Update(current, current.Revision);
    }

    public Issue SetDraft(string idOrPrefix, bool draft)
    {
        var current = Resolve(idOrPrefix);
        current.Draft = draft;
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
        var blocked = issues.SelectMany(issue => issue.Blocks).ToHashSet();
        return issues.Where(issue => issue.Status == IssueStatus.Open && !issue.Draft && !blocked.Contains(issue.Id))
            .OrderByDescending(issue => issue.Priority)
            .ThenBy(issue => issue.CreatedAt)
            .ThenBy(issue => issue.Id)
            .FirstOrDefault();
    }
}
