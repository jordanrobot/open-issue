using System.Collections.ObjectModel;
using System.ComponentModel;
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

/// <summary>Describes how issue rows are organized in the sidebar.</summary>
public enum IssueListViewMode
{
    Relationships,
    Tags,
    Dependencies
}

/// <summary>Describes the heading used for a grouped issue-list node.</summary>
public enum IssueListGroupKind
{
    None,
    Tag,
    Dependency,
    NoTags,
    NoDependencies
}

/// <summary>Provides a localized label for an issue-list view mode.</summary>
/// <param name="Mode">The view mode represented by the choice.</param>
/// <param name="Display">The localized text shown to the user.</param>
public sealed record IssueListViewChoice(IssueListViewMode Mode, string Display);

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

/// <summary>A tree node that displays either an issue row or a collapsible group heading.</summary>
public sealed partial class IssueListNode : ObservableObject
{
    private string _groupTitle = string.Empty;
    private long? _groupIssueId;
    private bool _groupIsExpanded = true;
    private bool _isRelationshipView;
    private int _depth;

    internal IssueListNode(IssueListItem row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        GroupKind = IssueListGroupKind.None;
        AutomationId = row.AutomationId;
        MoveToRootAutomationId = row.MoveToRootAutomationId;
        row.PropertyChanged += OnRowPropertyChanged;
    }

    internal IssueListNode(
        string automationId,
        IssueListGroupKind groupKind,
        string title,
        long? groupIssueId)
    {
        AutomationId = automationId;
        MoveToRootAutomationId = automationId;
        GroupKind = groupKind;
        _groupTitle = title;
        _groupIssueId = groupIssueId;
    }

    /// <summary>Gets the issue represented by this node, or <see langword="null"/> for a group.</summary>
    public IssueListItem? Row { get; }

    /// <summary>Gets the kind of group represented by this node.</summary>
    public IssueListGroupKind GroupKind { get; private set; }

    /// <summary>Gets the prerequisite issue ID represented by a dependency group.</summary>
    public long? GroupIssueId => _groupIssueId;

    /// <summary>Gets whether this node represents a group heading.</summary>
    public bool IsGroup => Row is null;

    /// <summary>Gets the issue title or raw group heading text.</summary>
    public string Title => Row?.Title ?? _groupTitle;

    /// <summary>Gets a stable UI Automation identifier for this node.</summary>
    public string AutomationId { get; }

    /// <summary>Gets a stable UI Automation identifier for the move-to-root menu item.</summary>
    public string MoveToRootAutomationId { get; }

    /// <summary>Gets whether this issue row is nested in the relationship view.</summary>
    public bool IsNested => _isRelationshipView && Row?.IsNested == true;

    /// <summary>Gets whether the represented issue can be moved to the top level.</summary>
    public bool CanMoveToRoot => Row?.IsNested == true;

    /// <summary>Gets the row depth used by the relationship view.</summary>
    public int Depth => _isRelationshipView ? _depth : 0;

    /// <summary>Gets the active relationship-view drop hint for this issue row.</summary>
    public IssueDropHint DropHint =>
        _isRelationshipView ? Row?.DropHint ?? IssueDropHint.None : IssueDropHint.None;

    /// <summary>Gets whether this issue row accepts relationship-view drops.</summary>
    public bool CanDrop => _isRelationshipView && Row is not null;

    /// <summary>Gets the child nodes displayed beneath this node.</summary>
    public ObservableCollection<IssueListNode> Children { get; } = [];

    /// <summary>Gets or sets whether this node's children are expanded in the tree.</summary>
    public bool IsExpanded
    {
        get => Row?.IsExpanded ?? _groupIsExpanded;
        set
        {
            if (Row is { } row)
            {
                row.IsExpanded = value;
            }
            else
            {
                SetProperty(ref _groupIsExpanded, value);
            }
        }
    }

    internal void SetRelationshipView(bool isRelationshipView)
    {
        if (_isRelationshipView == isRelationshipView)
        {
            return;
        }

        _isRelationshipView = isRelationshipView;
        OnPropertyChanged(nameof(IsNested));
        OnPropertyChanged(nameof(Depth));
        OnPropertyChanged(nameof(DropHint));
        OnPropertyChanged(nameof(CanDrop));
    }

    internal void SetDepth(int depth)
    {
        if (_depth == depth)
        {
            return;
        }

        _depth = depth;
        OnPropertyChanged(nameof(Depth));
    }

    internal void UpdateGroup(IssueListGroupKind groupKind, string title, long? groupIssueId)
    {
        if (GroupKind != groupKind)
        {
            GroupKind = groupKind;
            OnPropertyChanged(nameof(GroupKind));
        }

        if (_groupIssueId != groupIssueId)
        {
            _groupIssueId = groupIssueId;
            OnPropertyChanged(nameof(GroupIssueId));
        }

        if (!string.Equals(_groupTitle, title, StringComparison.Ordinal))
        {
            _groupTitle = title;
            OnPropertyChanged(nameof(Title));
        }
    }

    internal void Detach()
    {
        if (Row is { } row)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IssueListItem.Title):
                OnPropertyChanged(nameof(Title));
                break;
            case nameof(IssueListItem.IsNested):
                OnPropertyChanged(nameof(IsNested));
                OnPropertyChanged(nameof(CanMoveToRoot));
                break;
            case nameof(IssueListItem.Depth):
                OnPropertyChanged(nameof(Depth));
                break;
            case nameof(IssueListItem.DropHint):
                OnPropertyChanged(nameof(DropHint));
                break;
            case nameof(IssueListItem.IsExpanded):
                OnPropertyChanged(nameof(IsExpanded));
                break;
        }
    }
}

/// <summary>Reconciles issue results while retaining surviving row objects and containers.</summary>
public sealed class IssueListCollection : ObservableCollection<IssueListItem>
{
    private readonly Dictionary<long, IssueListNode> _issueNodes = [];
    private readonly Dictionary<string, IssueListNode> _groupNodes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<long, Issue> _knownIssues = [];
    private int _nextGroupAutomationId;

    /// <summary>Gets the current sidebar organization mode.</summary>
    public IssueListViewMode ViewMode { get; private set; } = IssueListViewMode.Relationships;

    /// <summary>Gets the root rows of the parent/child issue tree.</summary>
    public ObservableCollection<IssueListItem> RootItems { get; } = [];

    /// <summary>Gets the current display tree, including group headings when selected.</summary>
    public ObservableCollection<IssueListNode> RootNodes { get; } = [];

    /// <summary>Finds the display node for a visible issue.</summary>
    /// <param name="issueId">The issue's numeric ID.</param>
    /// <returns>The issue node, or <see langword="null"/> when the issue is not visible.</returns>
    public IssueListNode? FindNode(long issueId) =>
        _issueNodes.GetValueOrDefault(issueId);

    /// <summary>Expands every group containing the specified issue.</summary>
    /// <param name="issueId">The issue whose group headings should be expanded.</param>
    public void ExpandIssueGroup(long issueId)
    {
        foreach (var group in RootNodes.Where(node => node.IsGroup))
        {
            if (group.Children.Any(node => node.Row?.Id == issueId))
            {
                group.IsExpanded = true;
            }
        }
    }

    /// <summary>Changes how visible issue rows are organized in the display tree.</summary>
    /// <param name="viewMode">The organization mode to display.</param>
    public void SetViewMode(IssueListViewMode viewMode)
    {
        if (!Enum.IsDefined(viewMode))
        {
            throw new ArgumentOutOfRangeException(nameof(viewMode));
        }

        if (ViewMode == viewMode)
        {
            return;
        }

        ViewMode = viewMode;
        RebuildDisplayTree();
    }

    /// <summary>Applies ordered results using only necessary inserts, removals, and moves.</summary>
    /// <param name="issues">The issues in their desired display order, with unique IDs.</param>
    public void Synchronize(IReadOnlyList<Issue> issues)
    {
        Synchronize(issues, issues);
    }

    /// <summary>Applies visible results and the complete issue set used to label dependency groups.</summary>
    /// <param name="issues">The visible issues in their desired display order, with unique IDs.</param>
    /// <param name="knownIssues">All known issues, including issues hidden by the current filters.</param>
    public void Synchronize(IReadOnlyList<Issue> issues, IReadOnlyList<Issue> knownIssues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(knownIssues);
        _knownIssues = knownIssues.ToDictionary(issue => issue.Id);
        var desired = issues.ToDictionary(issue => issue.Id);
        var existing = this.ToDictionary(row => row.Id);

        for (var index = Count - 1; index >= 0; index--)
        {
            if (!desired.ContainsKey(this[index].Id))
            {
                var row = this[index];
                row.Children.Clear();
                if (_issueNodes.Remove(row.Id, out var node))
                {
                    node.Detach();
                }

                RemoveAt(index);
            }
        }

        for (var index = 0; index < issues.Count; index++)
        {
            var issue = issues[index];
            IssueListItem row;
            if (existing.TryGetValue(issue.Id, out var existingRow))
            {
                row = existingRow;
                row.Update(issue);
                var previousIndex = IndexOf(row);
                if (previousIndex != index)
                {
                    Move(previousIndex, index);
                }
            }
            else
            {
                row = new IssueListItem(issue);
                Insert(index, row);
            }

            if (!_issueNodes.ContainsKey(row.Id))
            {
                _issueNodes.Add(row.Id, new IssueListNode(row));
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

        RebuildDisplayTree();
    }

    private void RebuildDisplayTree()
    {
        var activeGroups = new HashSet<IssueListNode>();
        var rootNodes = ViewMode switch
        {
            IssueListViewMode.Relationships => BuildRelationshipTree(),
            IssueListViewMode.Tags => BuildTagGroups(activeGroups),
            IssueListViewMode.Dependencies => BuildDependencyGroups(activeGroups),
            _ => throw new ArgumentOutOfRangeException(nameof(ViewMode))
        };

        SynchronizeNodes(RootNodes, rootNodes);
        foreach (var group in _groupNodes.Values)
        {
            if (!activeGroups.Contains(group))
            {
                SynchronizeNodes(group.Children, []);
            }
        }
    }

    private IReadOnlyList<IssueListNode> BuildRelationshipTree()
    {
        foreach (var row in this)
        {
            var node = GetIssueNode(row);
            node.SetRelationshipView(true);
            node.SetDepth(row.Depth);
            SynchronizeNodes(node.Children, row.Children.Select(GetIssueNode).ToArray());
        }

        return RootItems.Select(GetIssueNode).ToArray();
    }

    private IReadOnlyList<IssueListNode> BuildTagGroups(HashSet<IssueListNode> activeGroups)
    {
        var rowsByTag = new Dictionary<string, List<IssueListItem>>(StringComparer.OrdinalIgnoreCase);
        var tagOrder = new List<string>();
        var untaggedRows = new List<IssueListItem>();

        foreach (var row in this)
        {
            var tags = row.Issue.EffectiveTags;
            if (tags.Count == 0)
            {
                untaggedRows.Add(row);
                continue;
            }

            foreach (var tag in tags)
            {
                if (!rowsByTag.TryGetValue(tag, out var matchingRows))
                {
                    matchingRows = [];
                    rowsByTag.Add(tag, matchingRows);
                    tagOrder.Add(tag);
                }

                matchingRows.Add(row);
            }
        }

        var rootNodes = new List<IssueListNode>();
        foreach (var tag in tagOrder.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var group = GetGroupNode(
                $"tag-value:{tag}",
                IssueListGroupKind.Tag,
                tag,
                groupIssueId: null,
                activeGroups);
            SynchronizeNodes(group.Children, rowsByTag[tag].Select(GetIssueNode).ToArray());
            rootNodes.Add(group);
        }

        if (untaggedRows.Count > 0)
        {
            var group = GetGroupNode(
                "tag-special:untagged",
                IssueListGroupKind.NoTags,
                string.Empty,
                groupIssueId: null,
                activeGroups);
            SynchronizeNodes(group.Children, untaggedRows.Select(GetIssueNode).ToArray());
            rootNodes.Add(group);
        }

        foreach (var row in this)
        {
            var node = GetIssueNode(row);
            node.SetRelationshipView(false);
            node.SetDepth(0);
            SynchronizeNodes(node.Children, []);
        }

        return rootNodes;
    }

    private IReadOnlyList<IssueListNode> BuildDependencyGroups(HashSet<IssueListNode> activeGroups)
    {
        var rowsByDependency = new Dictionary<long, List<IssueListItem>>();
        var dependencyOrder = new List<long>();
        var independentRows = new List<IssueListItem>();

        foreach (var row in this)
        {
            var dependencies = row.Issue.Dependencies;
            if (dependencies.Count == 0)
            {
                independentRows.Add(row);
                continue;
            }

            foreach (var dependencyId in dependencies)
            {
                if (!rowsByDependency.TryGetValue(dependencyId, out var dependentRows))
                {
                    dependentRows = [];
                    rowsByDependency.Add(dependencyId, dependentRows);
                    dependencyOrder.Add(dependencyId);
                }

                dependentRows.Add(row);
            }
        }

        var rootNodes = new List<IssueListNode>();
        foreach (var dependencyId in dependencyOrder)
        {
            _knownIssues.TryGetValue(dependencyId, out var dependency);
            var group = GetGroupNode(
                $"dependency-value:{dependencyId.ToString(CultureInfo.InvariantCulture)}",
                IssueListGroupKind.Dependency,
                dependency?.Title ?? string.Empty,
                dependencyId,
                activeGroups);
            SynchronizeNodes(group.Children, rowsByDependency[dependencyId].Select(GetIssueNode).ToArray());
            rootNodes.Add(group);
        }

        if (independentRows.Count > 0)
        {
            var group = GetGroupNode(
                "dependency-special:none",
                IssueListGroupKind.NoDependencies,
                string.Empty,
                groupIssueId: null,
                activeGroups);
            SynchronizeNodes(group.Children, independentRows.Select(GetIssueNode).ToArray());
            rootNodes.Add(group);
        }

        foreach (var row in this)
        {
            var node = GetIssueNode(row);
            node.SetRelationshipView(false);
            node.SetDepth(0);
            SynchronizeNodes(node.Children, []);
        }

        return rootNodes;
    }

    private IssueListNode GetIssueNode(IssueListItem row)
    {
        if (_issueNodes.TryGetValue(row.Id, out var node))
        {
            return node;
        }

        node = new IssueListNode(row);
        _issueNodes.Add(row.Id, node);
        return node;
    }

    private IssueListNode GetGroupNode(
        string key,
        IssueListGroupKind groupKind,
        string title,
        long? groupIssueId,
        HashSet<IssueListNode> activeGroups)
    {
        if (!_groupNodes.TryGetValue(key, out var group))
        {
            var automationId = $"IssueListGroup{_nextGroupAutomationId.ToString(CultureInfo.InvariantCulture)}";
            _nextGroupAutomationId++;
            group = new IssueListNode(automationId, groupKind, title, groupIssueId);
            _groupNodes.Add(key, group);
        }
        else
        {
            group.UpdateGroup(groupKind, title, groupIssueId);
        }

        activeGroups.Add(group);
        return group;
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

    private static void SynchronizeNodes(
        ObservableCollection<IssueListNode> nodes,
        IReadOnlyList<IssueListNode> desiredNodes)
    {
        var desiredSet = desiredNodes.ToHashSet();
        for (var index = nodes.Count - 1; index >= 0; index--)
        {
            if (!desiredSet.Contains(nodes[index]))
            {
                nodes.RemoveAt(index);
            }
        }

        for (var index = 0; index < desiredNodes.Count; index++)
        {
            var previousIndex = nodes.IndexOf(desiredNodes[index]);
            if (previousIndex < 0)
            {
                nodes.Insert(index, desiredNodes[index]);
            }
            else if (previousIndex != index)
            {
                nodes.Move(previousIndex, index);
            }
        }
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
