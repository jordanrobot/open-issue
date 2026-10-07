using System.Globalization;
using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using OpenIssue.Core;

namespace OpenIssue.Desktop;

public sealed record FilterChoice(string Value, string Display);

/// <summary>Represents a linked issue displayed in the issue details pane.</summary>
public sealed record IssueRelationshipLink(long Id, string Label);

public enum IssueSaveResult
{
    Saved,
    Reloaded,
    Cancelled,
    Failed
}

public enum IssueConflictResolution
{
    Overwrite,
    LoadNewer,
    KeepEditing
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly ResourceLoader SharedResources = new();
    private readonly ResourceLoader _resources = new();
    private readonly Dictionary<long, string> _knownRevisions = [];
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _orderGate = new(1, 1);
    private IReadOnlyList<Issue> _availableIssues = [];
    private IReadOnlyList<long> _issueOrder = [];
    private string _draggedIssueLabel = string.Empty;
    private readonly List<string> _explicitTags = [];
    private string _tagSuggestionQuery = string.Empty;
    private string _issueReferenceSuggestionPrefix = string.Empty;
    private bool _issueReferenceSuggestionsActive;
    private IssueService? _service;
    private IssueListOrderStore? _issueListOrderStore;
    private CancellationTokenSource? _refreshCancellation;
    private long _refreshVersion;

    public MainWindowViewModel()
    {
        ProjectRoot = string.Empty;
        SearchText = string.Empty;
        IssueTitle = string.Empty;
        IssueBody = string.Empty;
        IsDraft = true;
        StatusMessage = string.Empty;
        DropPreviewText = string.Empty;

        StatusFilterOptions =
        [
            new("all", GetString("FilterAllStatuses")),
            new("open", GetString("StatusOpen")),
            new("closed", GetString("StatusClosed")),
            new("declined", GetString("StatusDeclined"))
        ];
        IssueStatusOptions = StatusFilterOptions.Skip(1).ToArray();
        PriorityFilterOptions =
        [
            new("all", GetString("FilterAllPriorities")),
            new("urgent", GetString("PriorityUrgent")),
            new("high", GetString("PriorityHigh")),
            new("normal", GetString("PriorityNormal")),
            new("low", GetString("PriorityLow"))
        ];
        IssuePriorityOptions = PriorityFilterOptions.Skip(1).ToArray();

        SelectedStatusFilter = StatusFilterOptions[1];
        SelectedPriorityFilter = PriorityFilterOptions[0];
        SelectedIssueStatus = IssueStatusOptions[0];
        SelectedIssuePriority = IssuePriorityOptions.First(choice => choice.Value == "normal");
    }

    public IssueListCollection Issues { get; } = [];

    public ObservableCollection<IssueListItem> IssueReferenceSuggestions { get; } = [];

    /// <summary>Gets the selected issue's tags in display order.</summary>
    public ObservableCollection<string> IssueTags { get; } = [];

    /// <summary>Gets the existing project tags matching the current tag input.</summary>
    public ObservableCollection<string> TagSuggestions { get; } = [];

    /// <summary>Gets the selected issue's parent issue links.</summary>
    public ObservableCollection<IssueRelationshipLink> ParentIssueLinks { get; } = [];

    /// <summary>Gets the selected issue's child issue links.</summary>
    public ObservableCollection<IssueRelationshipLink> ChildIssueLinks { get; } = [];

    /// <summary>Gets whether the selected issue has any parent links.</summary>
    public bool HasParentIssueLinks => ParentIssueLinks.Count > 0;

    /// <summary>Gets whether the selected issue has any child links.</summary>
    public bool HasChildIssueLinks => ChildIssueLinks.Count > 0;

    /// <summary>Gets the localized empty-state label for relationship lists.</summary>
    public string NoIssueRelationshipsText => GetString("NoIssueRelationships");

    /// <summary>Gets the stable list row corresponding to the selected issue snapshot.</summary>
    public IssueListItem? SelectedIssueListItem =>
        Issues.FirstOrDefault(row => row.Id == SelectedIssue?.Id);

    public IReadOnlyList<FilterChoice> StatusFilterOptions { get; }

    public IReadOnlyList<FilterChoice> IssueStatusOptions { get; }

    public IReadOnlyList<FilterChoice> PriorityFilterOptions { get; }

    public IReadOnlyList<FilterChoice> IssuePriorityOptions { get; }

    public Func<Issue, Task<IssueConflictResolution>>? ResolveConflictAsync { get; set; }

    public string WindowTitle => string.IsNullOrEmpty(ProjectRoot)
        ? GetString("AppTitle")
        : Format("WindowTitleFormat", ProjectRoot);

    public string MetadataText => SelectedIssue is null
        ? GetString("SelectIssuePrompt")
        : Format(
            "IssueMetadataFormat",
            string.IsNullOrWhiteSpace(SelectedIssue.CreatedBy)
                ? GetString("UnknownCreator")
                : SelectedIssue.CreatedBy,
            SelectedIssue.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            SelectedIssue.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

    public string IssueListAutomationName => GetString("IssueListName");

    public string IssueListDragDropDescription => GetString("IssueListDragDropDescription");

    public string IssueListSplitterAutomationName => GetString("IssueListSplitterName");

    public string IssueDetailsSplitterAutomationName => GetString("IssueDetailsSplitterName");

    public string SearchBoxAutomationName => GetString("SearchBoxAutomationName");

    public string IssueReferenceSuggestionsAutomationName => GetString("IssueReferenceSuggestionsAutomationName");

    public string LoadingAutomationName => GetString("LoadingIssuesAutomationName");

    /// <summary>Formats an issue list row with the localized issue label template.</summary>
    /// <param name="id">The issue's numeric ID.</param>
    /// <param name="title">The issue title.</param>
    /// <returns>A localized label for the issue list.</returns>
    public static string FormatIssueLabel(long id, string title) =>
        string.Format(
            CultureInfo.CurrentCulture,
            SharedResources.GetString("IssueListEntryFormat"),
            id,
            title);

    public bool IsDirty =>
        SelectedIssue is not null &&
        (IssueTitle != SelectedIssue.Title ||
         IssueBody != SelectedIssue.Body ||
         !_explicitTags.SequenceEqual(SelectedIssue.Tags, StringComparer.Ordinal) ||
         SelectedIssueStatus?.Value != SelectedIssue.Status.ToString().ToLowerInvariant() ||
         SelectedIssuePriority?.Value != SelectedIssue.Priority.ToString().ToLowerInvariant() ||
         IsDraft != SelectedIssue.Draft);

    public bool IsEditorEnabled => SelectedIssue is not null;

    /// <summary>Gets whether the selected issue can be deleted without interrupting a save.</summary>
    public bool IsDeleteEnabled => IsEditorEnabled && !IsSaving;

    public string DisplayStatusMessage =>
        IsDraggingIssue && !string.IsNullOrEmpty(DropPreviewText)
            ? DropPreviewText
            : StatusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial string ProjectRoot { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial FilterChoice? SelectedStatusFilter { get; set; }

    [ObservableProperty]
    public partial FilterChoice? SelectedPriorityFilter { get; set; }

    [ObservableProperty]
    public partial bool DraftOnly { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(IsEditorEnabled))]
    [NotifyPropertyChangedFor(nameof(IsDeleteEnabled))]
    [NotifyPropertyChangedFor(nameof(MetadataText))]
    [NotifyPropertyChangedFor(nameof(SelectedIssueListItem))]
    public partial Issue? SelectedIssue { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string IssueTitle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string IssueBody { get; set; }

    partial void OnIssueBodyChanged(string value) => RefreshIssueTags();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial FilterChoice? SelectedIssueStatus { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial FilterChoice? SelectedIssuePriority { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial bool? IsDraft { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingIssue { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeleteEnabled))]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatusMessage))]
    public partial string StatusMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatusMessage))]
    public partial bool IsDraggingIssue { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatusMessage))]
    public partial string DropPreviewText { get; set; }

    [ObservableProperty]
    public partial bool HasIssueReferenceSuggestions { get; private set; }

    partial void OnSelectedIssueChanged(Issue? value)
    {
        RefreshIssueRelationships();
        _explicitTags.Clear();
        if (value is not null)
        {
            _explicitTags.AddRange(value.Tags);
        }

        RefreshIssueTags();
    }

    public async Task<string> InitializeProjectAsync(string root)
    {
        var issuesDirectory = await Task.Run(() =>
        {
            var path = ProjectLocator.GetIssuesDirectory(root);
            Directory.CreateDirectory(path);
            return path;
        });

        var orderStore = new IssueListOrderStore(root);
        var issueOrder = await Task.Run(orderStore.Load);
        _issueListOrderStore = orderStore;
        _issueOrder = issueOrder;
        _service = new IssueService(new IssueRepository(root));
        ProjectRoot = root;
        return issuesDirectory;
    }

    /// <summary>Gets the validated Markdown file path for an existing issue.</summary>
    /// <param name="issueId">The issue's numeric ID.</param>
    /// <returns>The issue file's full path.</returns>
    public Task<string> GetIssueFilePathAsync(long issueId)
    {
        var repository = _service?.Repository
            ?? throw new IssueException("The issue project has not been initialized.");
        return Task.Run(() =>
        {
            var issue = repository.Read(issueId);
            return repository.GetIssuePath(issue.Id);
        });
    }

    /// <summary>Updates body autocomplete suggestions for the numeric prefix after a typed <c>#</c>.</summary>
    /// <param name="idPrefix">Digits typed after the issue-reference marker.</param>
    public void UpdateIssueReferenceSuggestions(string idPrefix)
    {
        _issueReferenceSuggestionPrefix = idPrefix;
        _issueReferenceSuggestionsActive = true;
        IssueReferenceSuggestions.Clear();
        foreach (var issue in IssueBodyLinkParser.SuggestIssues(_availableIssues, idPrefix))
        {
            IssueReferenceSuggestions.Add(new IssueListItem(issue));
        }

        HasIssueReferenceSuggestions = IssueReferenceSuggestions.Count > 0;
    }

    /// <summary>Clears the issue-reference autocomplete results.</summary>
    public void ClearIssueReferenceSuggestions()
    {
        _issueReferenceSuggestionPrefix = string.Empty;
        _issueReferenceSuggestionsActive = false;
        IssueReferenceSuggestions.Clear();
        HasIssueReferenceSuggestions = false;
    }

    /// <summary>Updates the existing-tag suggestions for the current input.</summary>
    /// <param name="query">The tag text entered by the user.</param>
    public void UpdateTagSuggestions(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        _tagSuggestionQuery = query.Trim();
        TagSuggestions.Clear();

        var suggestions = _availableIssues
            .SelectMany(issue => issue.EffectiveTags)
            .Where(tag =>
                !string.IsNullOrWhiteSpace(tag) &&
                tag.StartsWith(_tagSuggestionQuery, StringComparison.OrdinalIgnoreCase))
            .Where(tag => !IssueTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase);
        foreach (var tag in suggestions)
        {
            TagSuggestions.Add(tag);
        }
    }

    /// <summary>Adds comma-separated tag input without adding duplicate tags.</summary>
    /// <param name="tags">One or more custom or suggested tags.</param>
    /// <returns><see langword="true"/> when at least one tag was added.</returns>
    public bool AddIssueTags(string tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var added = false;
        foreach (var tag in tags.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (_explicitTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            _explicitTags.Add(tag);
            added = true;
        }

        if (added)
        {
            OnPropertyChanged(nameof(IsDirty));
            RefreshIssueTags();
        }

        return added;
    }

    /// <summary>Removes one tag from the selected issue.</summary>
    /// <param name="tag">The tag to remove.</param>
    /// <returns><see langword="true"/> when a tag was removed.</returns>
    public bool RemoveIssueTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var index = _explicitTags.FindIndex(existing =>
            string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase));
        var removedExplicitTag = index >= 0;
        if (removedExplicitTag)
        {
            _explicitTags.RemoveAt(index);
        }

        var updatedBody = IssueBodyTagParser.RemoveInlineTag(IssueBody, tag);
        var removedInlineTag = !string.Equals(updatedBody, IssueBody, StringComparison.Ordinal);
        if (!removedExplicitTag && !removedInlineTag)
        {
            return false;
        }

        if (removedInlineTag)
        {
            IssueBody = updatedBody;
        }
        else
        {
            RefreshIssueTags();
            OnPropertyChanged(nameof(IsDirty));
        }

        return true;
    }

    private void RefreshIssueTags()
    {
        IssueTags.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in _explicitTags.Concat(IssueBodyTagParser.GetTags(IssueBody)))
        {
            if (seen.Add(tag))
            {
                IssueTags.Add(tag);
            }
        }

        UpdateTagSuggestions(_tagSuggestionQuery);
    }

    /// <summary>Lists unlinked open issues that can be added as parents or children of the selected issue.</summary>
    /// <param name="addingParents"><see langword="true"/> to exclude existing parents; otherwise excludes existing children.</param>
    /// <returns>Open issues other than the selected issue and its existing links.</returns>
    public async Task<IReadOnlyList<Issue>> GetOpenIssueCandidatesAsync(bool addingParents)
    {
        var service = _service ?? throw new IssueException("The issue project has not been initialized.");
        var current = SelectedIssue ?? throw new IssueException("Select an issue before adding issue links.");
        var existingIds = (addingParents ? current.Parents : current.Children).ToHashSet();
        return await Task.Run(() => service.List(new IssueFilter(Status: IssueStatus.Open)).Issues
            .Where(issue => issue.Id != current.Id && !existingIds.Contains(issue.Id))
            .OrderBy(issue => issue.Id)
            .ToArray());
    }

    /// <summary>Adds parent relationships to the selected issue.</summary>
    /// <param name="parentIds">The IDs selected by the user.</param>
    public async Task AddParentIssuesAsync(IEnumerable<long> parentIds)
    {
        ArgumentNullException.ThrowIfNull(parentIds);
        var service = _service ?? throw new IssueException("The issue project has not been initialized.");
        var current = SelectedIssue ?? throw new IssueException("Select an issue before adding issue links.");
        var additions = parentIds.Distinct().ToArray();
        if (additions.Length == 0)
        {
            return;
        }

        var saved = await Task.Run(() => service.AddParents(
            current.Id.ToString(CultureInfo.InvariantCulture),
            additions,
            current.Revision));
        TrackSavedIssue(saved);
        StatusMessage = GetString("MessageIssueLinksSaved");
    }

    /// <summary>Adds child relationships to the selected issue.</summary>
    /// <param name="childIds">The IDs selected by the user.</param>
    public async Task AddChildIssuesAsync(IEnumerable<long> childIds)
    {
        ArgumentNullException.ThrowIfNull(childIds);
        var service = _service ?? throw new IssueException("The issue project has not been initialized.");
        var current = SelectedIssue ?? throw new IssueException("Select an issue before adding issue links.");
        var additions = childIds.Distinct().ToArray();
        if (additions.Length == 0)
        {
            return;
        }

        var saved = await Task.Run(() => service.AddChildren(
            current.Id.ToString(CultureInfo.InvariantCulture),
            additions,
            current.Revision));
        TrackSavedIssue(saved);
        StatusMessage = GetString("MessageIssueLinksSaved");
    }

    /// <summary>Removes a parent relationship from the selected issue.</summary>
    /// <param name="parentId">The ID of the parent issue to unlink.</param>
    public async Task RemoveParentIssueAsync(long parentId)
    {
        var service = _service ?? throw new IssueException("The issue project has not been initialized.");
        var current = SelectedIssue ?? throw new IssueException("Select an issue before removing issue links.");
        var saved = await Task.Run(() => service.RemoveParent(
            current.Id.ToString(CultureInfo.InvariantCulture),
            parentId,
            current.Revision));
        TrackSavedIssue(saved);
        if (await RefreshIssuesAsync(preserveCurrentEditor: true))
        {
            StatusMessage = GetString("MessageIssueLinkRemoved");
        }
    }

    /// <summary>Removes a child relationship from the selected issue.</summary>
    /// <param name="childId">The ID of the child issue to unlink.</param>
    public async Task RemoveChildIssueAsync(long childId)
    {
        var service = _service ?? throw new IssueException("The issue project has not been initialized.");
        var current = SelectedIssue ?? throw new IssueException("Select an issue before removing issue links.");
        var saved = await Task.Run(() => service.RemoveChild(
            current.Id.ToString(CultureInfo.InvariantCulture),
            childId,
            current.Revision));
        TrackSavedIssue(saved);
        if (await RefreshIssuesAsync(preserveCurrentEditor: true))
        {
            StatusMessage = GetString("MessageIssueLinkRemoved");
        }
    }

    /// <summary>Adds a parent relationship for an issue moved onto another issue in the tree.</summary>
    /// <param name="childIssueId">The issue being moved.</param>
    /// <param name="parentIssueId">The issue that will contain the moved issue.</param>
    public async Task AddParentIssueAsync(long childIssueId, long parentIssueId)
    {
        if (childIssueId == parentIssueId)
        {
            throw new IssueException("An issue cannot be its own parent.");
        }

        var service = _service ?? throw new IssueException("The issue project has not been initialized.");
        var current = await Task.Run(
            () => service.Resolve(childIssueId.ToString(CultureInfo.InvariantCulture)));
        if (current.Parents.Contains(parentIssueId))
        {
            ExpandIssueRow(parentIssueId);
            return;
        }

        var saved = await Task.Run(() => service.AddParents(
            childIssueId.ToString(CultureInfo.InvariantCulture),
            [parentIssueId],
            current.Revision));
        _knownRevisions[saved.Id] = saved.Revision;
        ReplaceIssueInList(saved);
        ExpandIssueRow(parentIssueId);
        if (SelectedIssue?.Id == saved.Id)
        {
            SelectedIssue = saved;
        }

        StatusMessage = GetString("MessageIssueLinksSaved");
    }

    /// <summary>Moves an issue under a new parent in the issue tree.</summary>
    /// <param name="childIssueId">The issue being moved.</param>
    /// <param name="parentIssueId">The new parent issue ID.</param>
    public async Task MoveIssueUnderParentAsync(long childIssueId, long parentIssueId)
    {
        if (childIssueId == parentIssueId)
        {
            throw new IssueException("An issue cannot be its own parent.");
        }

        var service = _service ?? throw new IssueException("The issue project has not been initialized.");
        var moved = await Task.Run(() => service.MoveToParent(
            childIssueId.ToString(CultureInfo.InvariantCulture),
            parentIssueId));
        _knownRevisions[moved.Id] = moved.Revision;
        if (await RefreshIssuesAsync(preserveCurrentEditor: true))
        {
            ExpandIssueRow(parentIssueId);
            var parent = _availableIssues.FirstOrDefault(issue => issue.Id == parentIssueId);
            StatusMessage = Format(
                "MessageIssueMovedUnderFormat",
                FormatIssueLabel(moved.Id, moved.Title),
                FormatIssueLabel(parentIssueId, parent?.Title ?? string.Empty));
        }
    }

    /// <summary>Removes all parent links so an issue becomes a root-level item.</summary>
    /// <param name="issueId">The issue to move to the top level.</param>
    public async Task MoveIssueToRootAsync(long issueId)
    {
        var service = _service ?? throw new IssueException("The issue project has not been initialized.");
        var changed = await Task.Run(() =>
            service.MoveToRoot(issueId.ToString(CultureInfo.InvariantCulture)));
        if (!changed)
        {
            return;
        }

        if (await RefreshIssuesAsync(preserveCurrentEditor: true))
        {
            var issue = _availableIssues.FirstOrDefault(candidate => candidate.Id == issueId);
            StatusMessage = Format(
                "MessageIssueMovedToRootFormat",
                FormatIssueLabel(issueId, issue?.Title ?? string.Empty));
        }
    }

    /// <summary>Begins a drag and displays the issue being moved.</summary>
    /// <param name="issue">The issue row being dragged.</param>
    public void BeginIssueDrag(IssueListItem issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        _draggedIssueLabel = FormatIssueLabel(issue.Id, issue.Title);
        IsDraggingIssue = true;
        Issues.ClearDropHints();
        DropPreviewText = Format("IssueListDragStartFormat", _draggedIssueLabel);
    }

    /// <summary>Shows the proposed before, after, or child destination for a dragged issue.</summary>
    /// <param name="targetIssueId">The issue currently under the pointer.</param>
    /// <param name="dropHint">The proposed destination relative to the target.</param>
    public void UpdateIssueDropHint(long targetIssueId, IssueDropHint dropHint)
    {
        var target = Issues.FirstOrDefault(issue => issue.Id == targetIssueId);
        if (target is null)
        {
            return;
        }

        Issues.SetDropHint(targetIssueId, dropHint);
        var targetLabel = FormatIssueLabel(target.Id, target.Title);
        DropPreviewText = dropHint switch
        {
            IssueDropHint.Before => Format("IssueListDropBeforeFormat", _draggedIssueLabel, targetLabel),
            IssueDropHint.After => Format("IssueListDropAfterFormat", _draggedIssueLabel, targetLabel),
            IssueDropHint.Nest => Format("IssueListDropAsChildFormat", _draggedIssueLabel, targetLabel),
            _ => Format("IssueListDragStartFormat", _draggedIssueLabel)
        };
    }

    /// <summary>Shows the top-level destination while a nested issue is over an open tree area.</summary>
    public void SetRootDropPreview()
    {
        Issues.ClearDropHints();
        DropPreviewText = Format("IssueListDropToRootFormat", _draggedIssueLabel);
    }

    /// <summary>Clears the temporary drag-and-drop state.</summary>
    public void EndIssueDrag()
    {
        IsDraggingIssue = false;
        _draggedIssueLabel = string.Empty;
        DropPreviewText = string.Empty;
        Issues.ClearDropHints();
    }

    /// <summary>Clears the current row highlight when the pointer leaves its target.</summary>
    public void ClearIssueDropHint()
    {
        Issues.ClearDropHints();
        if (IsDraggingIssue)
        {
            DropPreviewText = Format("IssueListDragStartFormat", _draggedIssueLabel);
        }
    }

    /// <summary>Persists a completed tree drag while retaining the positions of filtered-out issues.</summary>
    /// <param name="visibleIssueIds">Visible issue IDs in their current tree order.</param>
    public async Task PersistVisibleIssueOrderAsync(IReadOnlyList<long> visibleIssueIds)
    {
        ArgumentNullException.ThrowIfNull(visibleIssueIds);
        var orderStore = _issueListOrderStore
            ?? throw new IssueException("The issue list order has not been initialized.");
        await _orderGate.WaitAsync();
        try
        {
            var includedIds = _issueOrder.ToHashSet();
            var visibleOrder = visibleIssueIds
                .Where(includedIds.Contains)
                .Distinct()
                .ToArray();
            var visibleIds = visibleOrder.ToHashSet();
            var nextOrder = _issueOrder.ToList();
            var visibleIndex = 0;
            for (var index = 0; index < nextOrder.Count; index++)
            {
                if (visibleIds.Contains(nextOrder[index]))
                {
                    nextOrder[index] = visibleOrder[visibleIndex++];
                }
            }

            if (nextOrder.SequenceEqual(_issueOrder))
            {
                return;
            }

            await Task.Run(() => orderStore.Save(nextOrder));
            _issueOrder = nextOrder;
            Issues.Reorder(_issueOrder);
            StatusMessage = GetString("MessageIssueListOrderSaved");
        }
        finally
        {
            _orderGate.Release();
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshAsync()
    {
        await RefreshIssuesAsync(preserveCurrentEditor: true);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        // Autosave must not rebuild the list or reload editor values while the user is typing.
        await SaveCurrentAsync(refreshIssueList: false);
    }

    public async Task<bool> RefreshIssuesAsync(bool preserveCurrentEditor = false)
    {
        var service = _service;
        if (service is null)
        {
            return false;
        }

        var version = ++_refreshVersion;
        var cancellation = new CancellationTokenSource();
        var previousCancellation = _refreshCancellation;
        _refreshCancellation = cancellation;
        previousCancellation?.Cancel();
        IsRefreshing = true;

        var query = SearchText.Trim();
        var filter = CreateFilter();
        var currentId = SelectedIssue?.Id;
        var currentRevision = SelectedIssue?.Revision;
        StatusMessage = GetString("MessageLoadingIssues");

        try
        {
            var result = await Task.Run(() =>
            {
                var allIssues = service.List().Issues;
                var listed = service.List(filter);
                var matches = string.IsNullOrEmpty(query)
                    ? listed.Issues
                    : service.Search(query, filter);
                return new RefreshResult(listed, matches, allIssues);
            }, cancellation.Token);

            if (cancellation.IsCancellationRequested || version != _refreshVersion)
            {
                return false;
            }

            await ReconcileIssueOrderAsync(result.AllIssues, cancellation.Token);
            if (cancellation.IsCancellationRequested || version != _refreshVersion)
            {
                return false;
            }

            _knownRevisions.Clear();
            foreach (var issue in result.Listed.Issues)
            {
                _knownRevisions[issue.Id] = issue.Revision;
            }

            _availableIssues = result.AllIssues;
            RefreshIssueRelationships();
            UpdateTagSuggestions(_tagSuggestionQuery);
            if (_issueReferenceSuggestionsActive)
            {
                UpdateIssueReferenceSuggestions(_issueReferenceSuggestionPrefix);
            }

            Issues.Synchronize(OrderIssues(result.Matches));
            UpdateNestedIssueState();

            var selected = currentId is null
                ? null
                : Issues.FirstOrDefault(row => row.Id == currentId)?.Issue;
            if (preserveCurrentEditor &&
                selected is not null &&
                string.Equals(selected.Revision, currentRevision, StringComparison.Ordinal))
            {
                SelectedIssue = selected;
            }
            else
            {
                LoadIssue(selected ?? Issues.FirstOrDefault()?.Issue);
            }

            StatusMessage = result.Listed.Diagnostics.Count == 0
                ? Format("MessageLoadedIssuesFormat", Issues.Count)
                : string.Join(
                    Environment.NewLine,
                    result.Listed.Diagnostics.Select(diagnostic => $"{diagnostic.Path}: {diagnostic.Message}"));
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            if (version == _refreshVersion)
            {
                StatusMessage = exception.Message;
            }

            return false;
        }
        finally
        {
            if (version == _refreshVersion)
            {
                IsRefreshing = false;
                _refreshCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    public async Task<bool> IsKnownRevisionAsync(string path)
    {
        var service = _service;
        if (service is null ||
            !long.TryParse(
                Path.GetFileNameWithoutExtension(path),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var id) ||
            !_knownRevisions.TryGetValue(id, out var knownRevision))
        {
            return false;
        }

        try
        {
            var revision = await Task.Run(
                () => service.Resolve(id.ToString(CultureInfo.InvariantCulture)).Revision);
            return string.Equals(revision, knownRevision, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<bool> SelectIssueAsync(Issue issue)
    {
        var service = _service;
        if (service is null)
        {
            return false;
        }

        try
        {
            var latest = await Task.Run(
                () => service.Resolve(issue.Id.ToString(CultureInfo.InvariantCulture)));
            ReplaceIssueInList(latest);
            LoadIssue(latest);
            return true;
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = Format("MessageCannotLoadFormat", exception.Message);
            return false;
        }
    }

    /// <summary>Expands every visible ancestor of the specified issue in the issue tree.</summary>
    /// <param name="issueId">The issue whose ancestors should be expanded.</param>
    public void ExpandIssueAncestors(long issueId)
    {
        var issue = _availableIssues.FirstOrDefault(candidate => candidate.Id == issueId)
            ?? Issues.FirstOrDefault(row => row.Id == issueId)?.Issue;
        if (issue is null)
        {
            return;
        }

        var pending = new Stack<long>(ParentIssueIds(issue));
        var visited = new HashSet<long> { issueId };
        while (pending.TryPop(out var parentId))
        {
            if (!visited.Add(parentId))
            {
                continue;
            }

            var row = Issues.FirstOrDefault(candidate => candidate.Id == parentId);
            if (row is not null)
            {
                row.IsExpanded = true;
            }

            var parent = _availableIssues.FirstOrDefault(candidate => candidate.Id == parentId)
                ?? row?.Issue;
            if (parent is not null)
            {
                foreach (var ancestorId in ParentIssueIds(parent))
                {
                    pending.Push(ancestorId);
                }
            }
        }
    }

    public async Task<IssueSaveResult> SaveCurrentAsync(bool refreshIssueList = true)
    {
        await _saveGate.WaitAsync();
        try
        {
            return await SaveCurrentCoreAsync(refreshIssueList);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<IssueSaveResult> SaveCurrentCoreAsync(bool refreshIssueList)
    {
        var service = _service;
        var current = SelectedIssue;
        if (service is null || current is null)
        {
            return IssueSaveResult.Failed;
        }

        if (!IsDirty)
        {
            StatusMessage = GetString("MessageAllChangesSaved");
            return IssueSaveResult.Saved;
        }

        var issueId = current.Id.ToString(CultureInfo.InvariantCulture);
        var expectedRevision = current.Revision;
        var title = IssueTitle;
        var body = IssueBody;
        var priority = ParsePriority(SelectedIssuePriority?.Value);
        var tags = _explicitTags.ToArray();
        var status = ParseStatus(SelectedIssueStatus?.Value);
        var draft = IsDraft != false;

        IsSaving = true;
        try
        {
            while (true)
            {
                try
                {
                    var saved = await Task.Run(() => service.Edit(
                        issueId,
                        title,
                        body,
                        priority,
                        tags,
                        status: status,
                        draft: draft,
                        expectedRevision: expectedRevision));
                    TrackSavedIssue(saved);

                    if (refreshIssueList)
                    {
                        if (await RefreshIssuesAsync(preserveCurrentEditor: true))
                        {
                            StatusMessage = Format(
                                "MessageChangesSavedAndRefreshedFormat",
                                StatusMessage);
                        }
                        else
                        {
                            StatusMessage = Format(
                                "MessageChangesSavedRefreshFailedFormat",
                                StatusMessage);
                        }
                    }
                    else
                    {
                        StatusMessage = GetString("MessageChangesSavedAutomatically");
                    }

                    return IssueSaveResult.Saved;
                }
                catch (IssueConflictException exception)
                {
                    Issue latest;
                    try
                    {
                        latest = await Task.Run(() => service.Resolve(issueId));
                    }
                    catch (Exception readException) when (readException is IssueException or IOException or UnauthorizedAccessException)
                    {
                        StatusMessage = Format(
                            "MessageAutosaveReadFailedFormat",
                            exception.Message,
                            readException.Message);
                        return IssueSaveResult.Failed;
                    }

                    if (string.Equals(latest.Revision, expectedRevision, StringComparison.Ordinal))
                    {
                        StatusMessage = Format("MessageAutosaveFailedFormat", exception.Message);
                        return IssueSaveResult.Failed;
                    }

                    var resolution = ResolveConflictAsync is null
                        ? IssueConflictResolution.KeepEditing
                        : await ResolveConflictAsync(latest);
                    if (resolution == IssueConflictResolution.Overwrite)
                    {
                        expectedRevision = latest.Revision;
                        SelectedIssue = latest;
                        _knownRevisions[latest.Id] = latest.Revision;
                        ReplaceIssueInList(latest);
                        continue;
                    }

                    if (resolution == IssueConflictResolution.LoadNewer)
                    {
                        ReplaceIssueInList(latest);
                        LoadIssue(latest);
                        if (refreshIssueList)
                        {
                            await RefreshIssuesAsync(preserveCurrentEditor: true);
                        }

                        StatusMessage = GetString("MessageLoadedNewerVersion");
                        return IssueSaveResult.Reloaded;
                    }

                    StatusMessage = GetString("MessageIssueChangedElsewhere");
                    return IssueSaveResult.Cancelled;
                }
                catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
                {
                    StatusMessage = Format("MessageAutosaveFailedFormat", exception.Message);
                    return IssueSaveResult.Failed;
                }
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    public async Task<bool> ReloadCurrentIssueAsync()
    {
        var service = _service;
        var current = SelectedIssue;
        if (service is null || current is null)
        {
            return false;
        }

        try
        {
            var latest = await Task.Run(
                () => service.Resolve(current.Id.ToString(CultureInfo.InvariantCulture)));
            ReplaceIssueInList(latest);
            LoadIssue(latest);
            return true;
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = Format("MessageCannotReloadFormat", exception.Message);
            return false;
        }
    }

    [RelayCommand]
    private async Task CreateIssueAsync()
    {
        var service = _service;
        if (service is null)
        {
            return;
        }

        var issue = await Task.Run(() => service.Create(string.Empty, string.Empty));
        await RefreshIssuesAsync(preserveCurrentEditor: true);
        var created = _availableIssues.FirstOrDefault(candidate => candidate.Id == issue.Id) ?? issue;
        LoadIssue(created);
    }

    [RelayCommand]
    private async Task DuplicateIssueAsync(long issueId)
    {
        var service = _service ?? throw new IssueException("The issue project has not been initialized.");
        var duplicate = await Task.Run(() =>
            service.Duplicate(issueId.ToString(CultureInfo.InvariantCulture)));
        await RefreshIssuesAsync(preserveCurrentEditor: true);
        var created = _availableIssues.FirstOrDefault(candidate => candidate.Id == duplicate.Id) ?? duplicate;
        LoadIssue(created);
    }

    [RelayCommand]
    private async Task DeleteIssueAsync(long issueId)
    {
        var service = _service;
        if (service is null)
        {
            return;
        }

        await Task.Run(() => service.Delete(issueId.ToString(CultureInfo.InvariantCulture)));
        _knownRevisions.Remove(issueId);
        if (SelectedIssue?.Id == issueId)
        {
            LoadIssue(null);
        }

        await RefreshIssuesAsync(preserveCurrentEditor: true);
    }

    private IssueFilter CreateFilter()
    {
        var status = SelectedStatusFilter?.Value;
        var priority = SelectedPriorityFilter?.Value;
        return new IssueFilter(
            Status: status is null or "all" ? null : ParseStatus(status),
            Priority: priority is null or "all" ? null : ParsePriority(priority),
            Draft: DraftOnly ? true : null);
    }

    private async Task ReconcileIssueOrderAsync(
        IReadOnlyList<Issue> issues,
        CancellationToken cancellationToken)
    {
        var orderStore = _issueListOrderStore
            ?? throw new IssueException("The issue list order has not been initialized.");
        await _orderGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var availableIds = issues.Select(issue => issue.Id).ToHashSet();
            var nextOrder = _issueOrder.Where(availableIds.Contains).ToList();
            var includedIds = nextOrder.ToHashSet();
            nextOrder.AddRange(issues.Where(issue => includedIds.Add(issue.Id)).Select(issue => issue.Id));
            if (nextOrder.SequenceEqual(_issueOrder))
            {
                return;
            }

            await Task.Run(() => orderStore.Save(nextOrder));
            _issueOrder = nextOrder;
        }
        finally
        {
            _orderGate.Release();
        }
    }

    private IReadOnlyList<Issue> OrderIssues(IReadOnlyList<Issue> issues)
    {
        var positions = _issueOrder
            .Select((id, index) => (Id: id, Index: index))
            .ToDictionary(item => item.Id, item => item.Index);
        return issues
            .OrderBy(issue => positions.GetValueOrDefault(issue.Id, int.MaxValue))
            .ToArray();
    }

    private void LoadIssue(Issue? issue)
    {
        ClearIssueReferenceSuggestions();
        IsLoadingIssue = true;
        try
        {
            SelectedIssue = issue;
            if (issue is not null)
            {
                _knownRevisions[issue.Id] = issue.Revision;
            }

            IssueTitle = issue?.Title ?? string.Empty;
            IssueBody = issue?.Body ?? string.Empty;
            _explicitTags.Clear();
            if (issue is not null)
            {
                _explicitTags.AddRange(issue.Tags);
            }

            RefreshIssueTags();
            SelectedIssueStatus = issue is null
                ? IssueStatusOptions[0]
                : IssueStatusOptions.First(choice =>
                    string.Equals(choice.Value, issue.Status.ToString(), StringComparison.OrdinalIgnoreCase));
            SelectedIssuePriority = issue is null
                ? IssuePriorityOptions.First(choice => choice.Value == "normal")
                : IssuePriorityOptions.First(choice =>
                    string.Equals(choice.Value, issue.Priority.ToString(), StringComparison.OrdinalIgnoreCase));
            IsDraft = issue?.Draft ?? true;
        }
        finally
        {
            IsLoadingIssue = false;
        }

        OnPropertyChanged(nameof(IsDirty));
        UpdateTagSuggestions(_tagSuggestionQuery);
    }

    private void TrackSavedIssue(Issue issue)
    {
        _knownRevisions[issue.Id] = issue.Revision;
        ReplaceIssueInList(issue);
        SelectedIssue = issue;
    }

    private void ReplaceIssueInList(Issue issue)
    {
        var availableIndex = _availableIssues.ToList().FindIndex(candidate => candidate.Id == issue.Id);
        if (availableIndex < 0)
        {
            _availableIssues = _availableIssues.Append(issue).OrderBy(candidate => candidate.Id).ToArray();
        }
        else
        {
            var available = _availableIssues.ToArray();
            available[availableIndex] = issue;
            _availableIssues = available;
        }

        RefreshIssueRelationships();
        UpdateTagSuggestions(_tagSuggestionQuery);

        var existing = Issues.FirstOrDefault(candidate => candidate.Id == issue.Id);
        if (existing is null)
        {
            return;
        }

        existing.Update(issue);
        Issues.RebuildHierarchy();
        UpdateNestedIssueState();
    }

    private void UpdateNestedIssueState()
    {
        var explicitChildren = _availableIssues
            .SelectMany(issue => issue.Children)
            .ToHashSet();
        foreach (var row in Issues)
        {
            row.IsNested = row.Issue.Parents.Count > 0 || explicitChildren.Contains(row.Id);
        }
    }

    private void ExpandIssueRow(long issueId)
    {
        var row = Issues.FirstOrDefault(candidate => candidate.Id == issueId);
        if (row is not null)
        {
            row.IsExpanded = true;
        }
    }

    private IEnumerable<long> ParentIssueIds(Issue issue) =>
        issue.Parents.Concat(
            _availableIssues
                .Where(candidate => candidate.Children.Contains(issue.Id))
                .Select(candidate => candidate.Id))
            .Distinct();

    private IEnumerable<long> ChildIssueIds(Issue issue) =>
        issue.Children.Concat(
            _availableIssues
                .Where(candidate => candidate.Parents.Contains(issue.Id))
                .Select(candidate => candidate.Id))
            .Distinct();

    private void RefreshIssueRelationships()
    {
        if (SelectedIssue is { } issue)
        {
            UpdateRelationshipLinks(ParentIssueLinks, ParentIssueIds(issue));
            UpdateRelationshipLinks(ChildIssueLinks, ChildIssueIds(issue));
        }
        else
        {
            ParentIssueLinks.Clear();
            ChildIssueLinks.Clear();
        }

        OnPropertyChanged(nameof(HasParentIssueLinks));
        OnPropertyChanged(nameof(HasChildIssueLinks));
    }

    private void UpdateRelationshipLinks(
        ObservableCollection<IssueRelationshipLink> links,
        IEnumerable<long> issueIds)
    {
        links.Clear();
        foreach (var issueId in issueIds.Distinct())
        {
            var relatedIssue = _availableIssues.FirstOrDefault(issue => issue.Id == issueId);
            var label = relatedIssue is null
                ? $"#{issueId.ToString(CultureInfo.InvariantCulture)}"
                : FormatIssueLabel(relatedIssue.Id, relatedIssue.Title);
            links.Add(new IssueRelationshipLink(issueId, label));
        }
    }

    private string GetString(string key) => _resources.GetString(key);

    private string Format(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, GetString(key), values);

    private static IssueStatus ParseStatus(string? value) =>
        Enum.TryParse<IssueStatus>(value, true, out var parsed) ? parsed : IssueStatus.Open;

    private static IssuePriority ParsePriority(string? value) =>
        Enum.TryParse<IssuePriority>(value, true, out var parsed) ? parsed : IssuePriority.Normal;

    private sealed record RefreshResult(
        IssueListResult Listed,
        IReadOnlyList<Issue> Matches,
        IReadOnlyList<Issue> AllIssues);
}
