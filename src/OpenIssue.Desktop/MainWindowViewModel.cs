using System.Globalization;
using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using OpenIssue.Core;

namespace OpenIssue.Desktop;

public sealed record FilterChoice(string Value, string Display);

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
    private IReadOnlyList<Issue> _availableIssues = [];
    private IssueService? _service;
    private CancellationTokenSource? _refreshCancellation;
    private long _refreshVersion;

    public MainWindowViewModel()
    {
        ProjectRoot = string.Empty;
        SearchText = string.Empty;
        IssueTitle = string.Empty;
        IssueBody = string.Empty;
        IssueTags = string.Empty;
        IsDraft = true;
        StatusMessage = string.Empty;

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

    public string ParentIssuesText =>
        Format("ParentIssuesFormat", FormatIssueRelationships(SelectedIssue?.Parents));

    public string ChildIssuesText =>
        Format("ChildIssuesFormat", FormatIssueRelationships(SelectedIssue?.Children));

    public string IssueListAutomationName => GetString("IssueListName");

    public string IssueListSplitterAutomationName => GetString("IssueListSplitterName");

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
         !EditorTags().SequenceEqual(SelectedIssue.Tags, StringComparer.Ordinal) ||
         SelectedIssueStatus?.Value != SelectedIssue.Status.ToString().ToLowerInvariant() ||
         SelectedIssuePriority?.Value != SelectedIssue.Priority.ToString().ToLowerInvariant() ||
         IsDraft != SelectedIssue.Draft);

    public bool IsEditorEnabled => SelectedIssue is not null;

    /// <summary>Gets whether the selected issue can be deleted without interrupting a save.</summary>
    public bool IsDeleteEnabled => IsEditorEnabled && !IsSaving;

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
    public partial bool ReadyOnly { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(IsEditorEnabled))]
    [NotifyPropertyChangedFor(nameof(IsDeleteEnabled))]
    [NotifyPropertyChangedFor(nameof(MetadataText))]
    [NotifyPropertyChangedFor(nameof(SelectedIssueListItem))]
    [NotifyPropertyChangedFor(nameof(ParentIssuesText))]
    [NotifyPropertyChangedFor(nameof(ChildIssuesText))]
    public partial Issue? SelectedIssue { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string IssueTitle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string IssueBody { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string IssueTags { get; set; }

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
    public partial string StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool HasIssueReferenceSuggestions { get; private set; }

    public async Task<string> InitializeProjectAsync(string root)
    {
        var issuesDirectory = await Task.Run(() =>
        {
            var path = ProjectLocator.GetIssuesDirectory(root);
            Directory.CreateDirectory(path);
            return path;
        });

        _service = new IssueService(new IssueRepository(root));
        ProjectRoot = root;
        return issuesDirectory;
    }

    /// <summary>Updates body autocomplete suggestions for the numeric prefix after a typed <c>#</c>.</summary>
    /// <param name="idPrefix">Digits typed after the issue-reference marker.</param>
    public void UpdateIssueReferenceSuggestions(string idPrefix)
    {
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
        IssueReferenceSuggestions.Clear();
        HasIssueReferenceSuggestions = false;
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

            _knownRevisions.Clear();
            foreach (var issue in result.Listed.Issues)
            {
                _knownRevisions[issue.Id] = issue.Revision;
            }

            _availableIssues = result.AllIssues;
            OnPropertyChanged(nameof(ParentIssuesText));
            OnPropertyChanged(nameof(ChildIssuesText));
            ClearIssueReferenceSuggestions();
            Issues.Synchronize(result.Matches);

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
        var tags = EditorTags();
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

        SearchText = string.Empty;
        SelectedStatusFilter = StatusFilterOptions[0];
        SelectedPriorityFilter = PriorityFilterOptions[0];
        ReadyOnly = false;

        var issue = await Task.Run(() => service.Create(string.Empty, string.Empty));
        await RefreshIssuesAsync(preserveCurrentEditor: true);
        var created = Issues.FirstOrDefault(candidate => candidate.Id == issue.Id)?.Issue ?? issue;
        if (!Issues.Any(candidate => candidate.Id == issue.Id))
        {
            Issues.Add(new IssueListItem(created));
        }

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
            Draft: ReadyOnly ? false : null);
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
            IssueTags = issue is null ? string.Empty : string.Join(", ", issue.Tags);
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

        OnPropertyChanged(nameof(ParentIssuesText));
        OnPropertyChanged(nameof(ChildIssuesText));

        var existing = Issues.FirstOrDefault(candidate => candidate.Id == issue.Id);
        if (existing is null)
        {
            return;
        }

        existing.Update(issue);
    }

    private string FormatIssueRelationships(IEnumerable<long>? ids)
    {
        var labels = ids?.Select(id =>
        {
            var relatedIssue = _availableIssues.FirstOrDefault(issue => issue.Id == id);
            return relatedIssue is null
                ? $"#{id.ToString(CultureInfo.InvariantCulture)}"
                : $"#{relatedIssue.Id.ToString(CultureInfo.InvariantCulture)} {relatedIssue.Title}";
        }).ToArray() ?? [];
        return labels.Length == 0 ? GetString("NoIssueRelationships") : string.Join(", ", labels);
    }

    private string[] EditorTags() =>
        IssueTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
