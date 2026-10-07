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
            issues = issues.Where(issue => issue.EffectiveTags.Contains(tag, StringComparer.OrdinalIgnoreCase));
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

    /// <summary>
    /// Permanently deletes an issue by its positive numeric ID.
    /// </summary>
    public void Delete(string idOrPrefix) => Repository.Delete(Resolve(idOrPrefix).Id);

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
            CreatedBy = Environment.UserName,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    /// <summary>Creates a new draft that copies an issue's title, body, priority, and tags.</summary>
    /// <param name="idOrPrefix">The positive numeric ID of the issue to duplicate.</param>
    /// <returns>The new draft issue with a newly allocated ID.</returns>
    /// <remarks>The duplicate does not inherit the source issue's status or relationships.</remarks>
    public Issue Duplicate(string idOrPrefix)
    {
        var source = Resolve(idOrPrefix);
        return Create(source.Title, source.Body, source.Priority, source.Tags);
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

        current.UpdatedAt = DateTimeOffset.UtcNow;
        return Repository.Update(current, expectedRevision ?? current.Revision);
    }

    /// <summary>Adds parent issue references without removing existing parents.</summary>
    /// <param name="idOrPrefix">The issue ID to update.</param>
    /// <param name="parentIds">The parent issue IDs to add.</param>
    /// <param name="expectedRevision">The revision the caller edited, when optimistic concurrency is required.</param>
    /// <returns>The updated issue, or the current issue when no new parent IDs were supplied.</returns>
    public Issue AddParents(
        string idOrPrefix,
        IEnumerable<long> parentIds,
        string? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(parentIds);
        var current = Resolve(idOrPrefix);
        var parents = current.Parents.Concat(parentIds).Distinct().ToArray();
        return parents.SequenceEqual(current.Parents)
            ? current
            : Edit(idOrPrefix, parents: parents, expectedRevision: expectedRevision ?? current.Revision);
    }

    /// <summary>Adds child issue references without removing existing children.</summary>
    /// <param name="idOrPrefix">The issue ID to update.</param>
    /// <param name="childIds">The child issue IDs to add.</param>
    /// <param name="expectedRevision">The revision the caller edited, when optimistic concurrency is required.</param>
    /// <returns>The updated issue, or the current issue when no new child IDs were supplied.</returns>
    public Issue AddChildren(
        string idOrPrefix,
        IEnumerable<long> childIds,
        string? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(childIds);
        var current = Resolve(idOrPrefix);
        var children = current.Children.Concat(childIds).Distinct().ToArray();
        return children.SequenceEqual(current.Children)
            ? current
            : Edit(idOrPrefix, children: children, expectedRevision: expectedRevision ?? current.Revision);
    }

    /// <summary>Removes a parent relationship from a child issue.</summary>
    /// <param name="childIdOrPrefix">The child issue ID to update.</param>
    /// <param name="parentId">The parent issue ID to remove.</param>
    /// <param name="expectedRevision">The revision the caller edited, when optimistic concurrency is required.</param>
    /// <returns>The updated child issue.</returns>
    public Issue RemoveParent(
        string childIdOrPrefix,
        long parentId,
        string? expectedRevision = null)
    {
        var child = Resolve(childIdOrPrefix);
        var parentIdText = parentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var parent = Resolve(parentIdText);
        if (child.Id == parent.Id)
        {
            throw new IssueException("An issue cannot be its own parent.");
        }

        var childHasParent = child.Parents.Contains(parent.Id);
        if (!childHasParent && !parent.Children.Contains(child.Id))
        {
            return child;
        }

        if (childHasParent)
        {
            child = Edit(
                childIdOrPrefix,
                parents: child.Parents.Where(id => id != parent.Id),
                expectedRevision: expectedRevision ?? child.Revision);
        }

        parent = Resolve(parentIdText);
        if (parent.Children.Contains(child.Id))
        {
            Edit(
                parentIdText,
                children: parent.Children.Where(id => id != child.Id),
                expectedRevision: parent.Revision);
        }

        return Resolve(childIdOrPrefix);
    }

    /// <summary>Removes a child relationship from a parent issue.</summary>
    /// <param name="parentIdOrPrefix">The parent issue ID to update.</param>
    /// <param name="childId">The child issue ID to remove.</param>
    /// <param name="expectedRevision">The revision the caller edited, when optimistic concurrency is required.</param>
    /// <returns>The updated parent issue.</returns>
    public Issue RemoveChild(
        string parentIdOrPrefix,
        long childId,
        string? expectedRevision = null)
    {
        var parent = Resolve(parentIdOrPrefix);
        var childIdText = childId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var child = Resolve(childIdText);
        if (parent.Id == child.Id)
        {
            throw new IssueException("An issue cannot be its own child.");
        }

        var parentHasChild = parent.Children.Contains(child.Id);
        if (!parentHasChild && !child.Parents.Contains(parent.Id))
        {
            return parent;
        }

        if (parentHasChild)
        {
            parent = Edit(
                parentIdOrPrefix,
                children: parent.Children.Where(id => id != child.Id),
                expectedRevision: expectedRevision ?? parent.Revision);
        }

        child = Resolve(childIdText);
        if (child.Parents.Contains(parent.Id))
        {
            Edit(
                childIdText,
                parents: child.Parents.Where(id => id != parent.Id),
                expectedRevision: child.Revision);
        }

        return Resolve(parentIdOrPrefix);
    }

    /// <summary>Moves an issue under a different parent, removing its previous parent links.</summary>
    /// <param name="childIdOrPrefix">The issue being moved.</param>
    /// <param name="parentId">The new parent issue ID.</param>
    /// <returns>The updated child issue.</returns>
    public Issue MoveToParent(string childIdOrPrefix, long parentId)
    {
        var child = Resolve(childIdOrPrefix);
        var parentIdText = parentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var parent = Resolve(parentIdText);
        if (child.Id == parent.Id)
        {
            throw new IssueException("An issue cannot be its own parent.");
        }

        if (child.Parents.Contains(parent.Id) || parent.Children.Contains(child.Id))
        {
            return child;
        }

        var allIssues = Repository.List().Issues;
        var oldParents = allIssues
            .Where(candidate => candidate.Id != parent.Id && candidate.Children.Contains(child.Id))
            .ToArray();
        var linked = AddParents(childIdOrPrefix, [parent.Id], child.Revision);
        var moved = linked.Parents.SequenceEqual([parent.Id])
            ? linked
            : Edit(
                childIdOrPrefix,
                parents: [parent.Id],
                expectedRevision: linked.Revision);
        RemoveExplicitChildReferences(child.Id, oldParents);
        return Resolve(childIdOrPrefix);
    }

    /// <summary>Removes all parent relationships from an issue.</summary>
    /// <param name="idOrPrefix">The issue to move to the top level.</param>
    /// <returns><see langword="true"/> when one or more parent links were removed.</returns>
    public bool MoveToRoot(string idOrPrefix)
    {
        var child = Resolve(idOrPrefix);
        var parentsWithExplicitChildren = Repository.List().Issues
            .Where(parent => parent.Children.Contains(child.Id))
            .ToArray();
        var changed = RemoveExplicitChildReferences(child.Id, parentsWithExplicitChildren);
        child = Resolve(idOrPrefix);
        if (child.Parents.Count == 0)
        {
            return changed;
        }

        Edit(idOrPrefix, parents: [], expectedRevision: child.Revision);
        return true;
    }

    public Issue SetStatus(string idOrPrefix, IssueStatus status)
    {
        var current = Resolve(idOrPrefix);
        current.Status = status;
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
                                  issue.EffectiveTags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase))))
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

    private bool RemoveExplicitChildReferences(long childId, IEnumerable<Issue> parents)
    {
        var changed = false;
        foreach (var parent in parents)
        {
            var parentId = parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var current = Resolve(parentId);
            if (!current.Children.Contains(childId))
            {
                continue;
            }

            Edit(
                parentId,
                children: current.Children.Where(id => id != childId),
                expectedRevision: current.Revision);
            changed = true;
        }

        return changed;
    }
}
