using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenIssue.Core;

namespace OpenIssue.Desktop;

/// <summary>Describes the visual destination shown while an issue is being dragged.</summary>
public enum IssueDropHint
{
    None,
    Before,
    Nest,
    After
}

/// <summary>A stable list row whose displayed title can change without replacing its container.</summary>
public sealed partial class IssueListItem : ObservableObject
{
    /// <summary>Creates a row for an issue.</summary>
    /// <param name="issue">The issue represented by the row.</param>
    public IssueListItem(Issue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        Issue = issue;
        Title = issue.Title;
        IsExpanded = true;
    }

    /// <summary>Gets the latest issue snapshot, including its revision.</summary>
    public Issue Issue { get; private set; }

    /// <summary>Gets the stable numeric identity of the row.</summary>
    public long Id => Issue.Id;

    /// <summary>Gets the child rows displayed beneath this issue.</summary>
    public ObservableCollection<IssueListItem> Children { get; } = [];

    /// <summary>Gets a stable UI Automation identifier for this row.</summary>
    public string AutomationId => $"IssueListItem{Id.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Gets a stable UI Automation identifier for the move-to-root menu item.</summary>
    public string MoveToRootAutomationId => $"MoveIssueToRoot{Id.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Gets whether this issue is displayed beneath a parent.</summary>
    [ObservableProperty]
    public partial bool IsNested { get; set; }

    /// <summary>Gets or sets this row's minimum visible depth in the issue tree.</summary>
    [ObservableProperty]
    public partial int Depth { get; set; }

    /// <summary>Gets or sets the drag destination hint shown on this row.</summary>
    [ObservableProperty]
    public partial IssueDropHint DropHint { get; set; }

    /// <summary>Gets the title displayed by the row.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; }

    /// <summary>Gets or sets whether this issue's children are expanded in the tree.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Updates this row without replacing it or notifying unchanged titles.</summary>
    /// <param name="issue">The latest snapshot of the same issue.</param>
    public void Update(Issue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        if (issue.Id != Id)
        {
            throw new ArgumentException("The issue ID must match the list row.", nameof(issue));
        }

        Issue = issue;
        Title = issue.Title;
    }
}

/// <summary>Reconciles issue results while retaining surviving row objects and containers.</summary>
public sealed class IssueListCollection : ObservableCollection<IssueListItem>
{
    /// <summary>Gets the root rows of the parent/child issue tree.</summary>
    public ObservableCollection<IssueListItem> RootItems { get; } = [];

    /// <summary>Applies ordered results using only necessary inserts, removals, and moves.</summary>
    /// <param name="issues">The issues in their desired display order, with unique IDs.</param>
    public void Synchronize(IReadOnlyList<Issue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var desired = issues.ToDictionary(issue => issue.Id);
        var existing = this.ToDictionary(row => row.Id);

        for (var index = Count - 1; index >= 0; index--)
        {
            if (!desired.ContainsKey(this[index].Id))
            {
                this[index].Children.Clear();
                RemoveAt(index);
            }
        }

        for (var index = 0; index < issues.Count; index++)
        {
            var issue = issues[index];
            if (existing.TryGetValue(issue.Id, out var row))
            {
                row.Update(issue);
                var previousIndex = IndexOf(row);
                if (previousIndex != index)
                {
                    Move(previousIndex, index);
                }
            }
            else
            {
                Insert(index, new IssueListItem(issue));
            }
        }

        RebuildHierarchy();
    }

    /// <summary>Reorders visible rows using a project-wide ordered list of issue IDs.</summary>
    /// <param name="issueIds">The issue IDs in their desired display order.</param>
    public void Reorder(IReadOnlyList<long> issueIds)
    {
        ArgumentNullException.ThrowIfNull(issueIds);
        var positions = issueIds
            .Select((id, index) => (Id: id, Index: index))
            .ToDictionary(item => item.Id, item => item.Index);
        var orderedRows = this
            .OrderBy(row => positions.GetValueOrDefault(row.Id, int.MaxValue))
            .ToArray();

        for (var index = 0; index < orderedRows.Length; index++)
        {
            var previousIndex = IndexOf(orderedRows[index]);
            if (previousIndex != index)
            {
                Move(previousIndex, index);
            }
        }

        RebuildHierarchy();
    }

    /// <summary>Highlights the row that is currently receiving a drag.</summary>
    /// <param name="issueId">The target issue ID.</param>
    /// <param name="hint">The insertion or nesting hint to display.</param>
    public void SetDropHint(long issueId, IssueDropHint hint)
    {
        ClearDropHints();
        var row = this.FirstOrDefault(candidate => candidate.Id == issueId);
        if (row is not null)
        {
            row.DropHint = hint;
        }
    }

    /// <summary>Removes any active drag destination hint.</summary>
    public void ClearDropHints()
    {
        foreach (var row in this)
        {
            row.DropHint = IssueDropHint.None;
        }
    }

    /// <summary>Rebuilds root and child collections from the visible issues' relationship metadata.</summary>
    public void RebuildHierarchy()
    {
        var rowsById = this.ToDictionary(row => row.Id);
        var childrenByParent = rowsById.Keys.ToDictionary(id => id, _ => new HashSet<long>());
        var parentsByChild = rowsById.Keys.ToDictionary(id => id, _ => new HashSet<long>());

        foreach (var row in this)
        {
            foreach (var parentId in row.Issue.Parents)
            {
                AddRelationship(parentId, row.Id, childrenByParent, parentsByChild);
            }
        }

        foreach (var row in this)
        {
            foreach (var childId in row.Issue.Children)
            {
                AddRelationship(row.Id, childId, childrenByParent, parentsByChild);
            }
        }

        var positions = this
            .Select((row, index) => (row.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        foreach (var row in this)
        {
            row.IsNested = parentsByChild[row.Id].Count > 0 || row.Issue.Parents.Count > 0;
            var children = childrenByParent[row.Id]
                .OrderBy(id => positions[id])
                .Select(id => rowsById[id])
                .ToArray();
            SynchronizeRows(row.Children, children);
        }

        var roots = this.Where(row => parentsByChild[row.Id].Count == 0).ToArray();
        SynchronizeRows(RootItems, roots);

        var depthById = new Dictionary<long, int>();
        var pending = new Queue<(long Id, int Depth)>(roots.Select(row => (row.Id, 0)));
        while (pending.TryDequeue(out var item))
        {
            if (depthById.TryGetValue(item.Id, out var currentDepth) && currentDepth <= item.Depth)
            {
                continue;
            }

            depthById[item.Id] = item.Depth;
            foreach (var childId in childrenByParent[item.Id])
            {
                pending.Enqueue((childId, item.Depth + 1));
            }
        }

        foreach (var row in this)
        {
            row.Depth = depthById.GetValueOrDefault(row.Id);
        }
    }

    private static void AddRelationship(
        long parentId,
        long childId,
        Dictionary<long, HashSet<long>> childrenByParent,
        Dictionary<long, HashSet<long>> parentsByChild)
    {
        if (parentId == childId ||
            !childrenByParent.TryGetValue(parentId, out var children) ||
            !parentsByChild.TryGetValue(childId, out var parents) ||
            children.Contains(childId) ||
            WouldCreateCycle(parentId, childId, childrenByParent))
        {
            return;
        }

        children.Add(childId);
        parents.Add(parentId);
    }

    private static bool WouldCreateCycle(
        long parentId,
        long childId,
        IReadOnlyDictionary<long, HashSet<long>> childrenByParent)
    {
        var visited = new HashSet<long>();
        var pending = new Stack<long>();
        pending.Push(childId);
        while (pending.TryPop(out var currentId))
        {
            if (currentId == parentId)
            {
                return true;
            }

            if (!visited.Add(currentId))
            {
                continue;
            }

            foreach (var descendantId in childrenByParent[currentId])
            {
                pending.Push(descendantId);
            }
        }

        return false;
    }

    private static void SynchronizeRows(
        ObservableCollection<IssueListItem> rows,
        IReadOnlyList<IssueListItem> desiredRows)
    {
        var desiredSet = desiredRows.ToHashSet();
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            if (!desiredSet.Contains(rows[index]))
            {
                rows.RemoveAt(index);
            }
        }

        for (var index = 0; index < desiredRows.Count; index++)
        {
            var previousIndex = rows.IndexOf(desiredRows[index]);
            if (previousIndex < 0)
            {
                rows.Insert(index, desiredRows[index]);
            }
            else if (previousIndex != index)
            {
                rows.Move(previousIndex, index);
            }
        }
    }
}
