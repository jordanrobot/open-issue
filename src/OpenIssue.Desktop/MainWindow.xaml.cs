using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using OpenIssue.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;
using VirtualKey = Windows.System.VirtualKey;

namespace OpenIssue.Desktop;

public sealed partial class MainWindow : Window
{
    private static readonly ResourceLoader Resources = new();
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _saveTimer;
    private FileSystemWatcher? _watcher;
    private bool _initializationStarted;
    private bool _closingPromptVisible;
    private bool _allowClose;
    private bool _refreshInProgress;
    private bool _refreshQueued;
    private bool _suppressIssueReferenceSuggestions;
    private int _issueReferenceStart = -1;
    private int _issueReferenceLength;
    private long? _draggedIssueId;
    private long? _pointerPressedIssueId;
    private long? _dropTargetIssueId;
    private IssueDropHint _dropHint;
    private readonly string? _launchArguments;

    public MainWindowViewModel ViewModel { get; }

    /// <summary>Determines whether the issue list's empty-state guidance should be visible.</summary>
    /// <param name="issueCount">The number of issues currently shown.</param>
    /// <param name="isRefreshing">Whether the issue list is refreshing.</param>
    /// <returns>Visible when no issues are shown and a refresh is not in progress.</returns>
    public static Visibility EmptyStateVisibility(int issueCount, bool isRefreshing) =>
        issueCount == 0 && !isRefreshing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Determines whether the issue-reference suggestions list should be shown.</summary>
    /// <param name="hasSuggestions">Whether autocomplete suggestions are available.</param>
    /// <returns>Visible when suggestions are available.</returns>
    public static Visibility SuggestionVisibility(bool hasSuggestions) =>
        hasSuggestions ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows the temporary root-level drop target during an issue drag.</summary>
    public static Visibility DraggingVisibility(bool isDragging) =>
        isDragging ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows the nested-row guide for issues that have a parent.</summary>
    public static Visibility NestedIssueVisibility(bool isNested) =>
        isNested ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Reduces the horizontal inset for each visible tree level.</summary>
    /// <param name="depth">The row's depth in the displayed tree.</param>
    /// <returns>A margin that compresses the indentation while preserving the hierarchy.</returns>
    public static Thickness CompactNestedRowMargin(int depth) =>
        new(-4 * depth, 0, 0, 0);

    /// <summary>Shows the insertion line before a drag target.</summary>
    public static Visibility DropBeforeVisibility(IssueDropHint hint) =>
        hint == IssueDropHint.Before ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows the insertion line after a drag target.</summary>
    public static Visibility DropAfterVisibility(IssueDropHint hint) =>
        hint == IssueDropHint.After ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows the row highlight when a dragged issue will become a child.</summary>
    public static Visibility DropNestVisibility(IssueDropHint hint) =>
        hint == IssueDropHint.Nest ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Outlines a child destination and highlights the active root drop target.</summary>
    public static Thickness DropTargetBorderThickness(IssueDropHint hint) =>
        hint == IssueDropHint.Nest ? new Thickness(1) : new Thickness(0);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(nint hwnd);

    public MainWindow(string? launchArguments)
    {
        _launchArguments = launchArguments;
        ViewModel = new MainWindowViewModel();
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        SetInitialWindowSize();
        AppWindow.SetIcon("Assets/AppIcon.ico");

        ViewModel.ResolveConflictAsync = ResolveConflictAsync;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Title = ViewModel.WindowTitle;

        _refreshTimer = App.DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(500);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Tick += async (_, _) =>
        {
            _refreshTimer.Stop();
            await RefreshFromUiAsync();
        };

        _saveTimer = App.DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(100);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await ViewModel.SaveCommand.ExecuteAsync(null);
        };

        AppWindow.Closing += Window_Closing;
        Activated += Window_Activated;
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _saveTimer.Stop();
            _watcher?.Dispose();
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        };
    }

    private void SetInitialWindowSize()
    {
        var windowHandle = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var dpi = GetDpiForWindow(windowHandle);
        if (dpi == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var scale = dpi / 96.0;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Ceiling(1120 * scale),
            (int)Math.Ceiling(760 * scale)));
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.WindowTitle))
        {
            Title = ViewModel.WindowTitle;
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initializationStarted)
        {
            return;
        }

        _initializationStarted = true;
        await InitializeProjectAsync();
    }

    private async Task InitializeProjectAsync()
    {
        try
        {
            string root;
            try
            {
                var launchProjectRoot = GetProjectRootArgument(_launchArguments);
                root = launchProjectRoot is null
                    ? await Task.Run(() => ProjectLocator.Find())
                    : await Task.Run(() => ProjectLocator.Find(launchProjectRoot));
            }
            catch (IssueException)
            {
                var configPath = await PickProjectConfigurationAsync();
                if (configPath is null)
                {
                    _allowClose = true;
                    Close();
                    return;
                }

                root = await Task.Run(() => GetProjectRoot(configPath));
            }

            var issuesPath = await ViewModel.InitializeProjectAsync(root);
            SetWatcher(issuesPath);
            await ViewModel.RefreshIssuesAsync();
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException or COMException)
        {
            await ShowMessageAsync(GetString("MessageCannotOpenProjectTitle"), exception.Message);
            _allowClose = true;
            Close();
        }
    }

    private static string? GetProjectRootArgument(string? arguments)
    {
        const string prefix = "--open-issue-root=";
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        var argument = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        if (argument is null)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(argument[prefix.Length..]));
        }
        catch (FormatException exception)
        {
            throw new IssueException(Format("MessageInvalidLaunchArguments", exception.Message));
        }
    }

    private async Task<string?> PickProjectConfigurationAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private static string GetProjectRoot(string configPath)
    {
        if (!string.Equals(Path.GetFileName(configPath), ProjectLocator.ConfigFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new IssueException(Format(
                "MessageWrongConfigFileFormat",
                ProjectLocator.ConfigFileName));
        }

        ProjectLocator.ReadConfig(configPath);
        var dataDirectory = Path.GetDirectoryName(configPath);
        var projectRoot = dataDirectory is null ? null : Directory.GetParent(dataDirectory)?.FullName;
        return projectRoot ?? throw new IssueException(GetString("MessageConfigNotInProject"));
    }

    private void SetWatcher(string issuesPath)
    {
        _watcher = new FileSystemWatcher(issuesPath, "*.md")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += FilesChanged;
        _watcher.Created += FilesChanged;
        _watcher.Deleted += FilesChanged;
        _watcher.Renamed += FilesChanged;
    }

    private void FilesChanged(object sender, FileSystemEventArgs args)
    {
        var path = args.FullPath;
        App.DispatcherQueue.TryEnqueue(() => _ = HandleFilesChangedAsync(path));
    }

    private async Task HandleFilesChangedAsync(string path)
    {
        if (await ViewModel.IsKnownRevisionAsync(path))
        {
            return;
        }

        ViewModel.StatusMessage = GetString("MessageIssueFilesChanged");
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private async Task RefreshFromUiAsync()
    {
        if (_refreshInProgress)
        {
            _refreshQueued = true;
            return;
        }

        _refreshInProgress = true;
        try
        {
            do
            {
                _refreshQueued = false;
                if (!await ConfirmLeaveCurrentAsync())
                {
                    return;
                }

                await ViewModel.RefreshCommand.ExecuteAsync(null);
            }
            while (_refreshQueued);
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private async Task<bool> ConfirmLeaveCurrentAsync()
    {
        _saveTimer.Stop();
        if (!ViewModel.IsDirty)
        {
            return true;
        }

        var saveResult = await ViewModel.SaveCurrentAsync(refreshIssueList: false);
        if (saveResult is IssueSaveResult.Saved or IssueSaveResult.Reloaded)
        {
            return true;
        }

        if (saveResult == IssueSaveResult.Cancelled)
        {
            return false;
        }

        var answer = await ShowConfirmationAsync(
            GetString("DialogAutosaveFailedTitle"),
            Format("DialogDiscardChangesFormat", ViewModel.StatusMessage),
            GetString("ButtonDiscardChanges"),
            GetString("ButtonKeepEditing"));
        return answer == ContentDialogResult.Primary && await ViewModel.ReloadCurrentIssueAsync();
    }

    private async Task<IssueConflictResolution> ResolveConflictAsync(Issue latest)
    {
        var answer = await ShowConfirmationAsync(
            GetString("DialogResolveConflictTitle"),
            Format("DialogResolveConflictFormat", latest.Id),
            GetString("ButtonOverwrite"),
            GetString("ButtonKeepEditing"),
            GetString("ButtonLoadNewerVersion"));
        return answer switch
        {
            ContentDialogResult.Primary => IssueConflictResolution.Overwrite,
            ContentDialogResult.Secondary => IssueConflictResolution.LoadNewer,
            _ => IssueConflictResolution.KeepEditing
        };
    }

    private async void IssueList_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs e)
    {
        var row = e.AddedItems
            .Select(ResolveIssueListItem)
            .FirstOrDefault(item => item is not null)
            ?? ResolveIssueListItem(sender.SelectedItem);
        if (ViewModel.IsLoadingIssue ||
            ViewModel.IsRefreshing ||
            row is null)
        {
            return;
        }

        var issue = row.Issue;
        var previous = ViewModel.SelectedIssue;
        var previousRow = ViewModel.SelectedIssueListItem;
        if (previous?.Id == issue.Id)
        {
            return;
        }

        if (previous is not null &&
            previous.Id != issue.Id &&
            !await ConfirmLeaveCurrentAsync())
        {
            IssueList.SelectedItem = previousRow;
            return;
        }

        if (!await ViewModel.SelectIssueAsync(issue))
        {
            IssueList.SelectedItem = previousRow;
        }
    }

    private void IssueListItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _pointerPressedIssueId = sender is TreeViewItem container
            ? ResolveIssueListItem(container)?.Id
            : null;
    }

    private void IssueListItem_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        _pointerPressedIssueId = null;

    private void IssueList_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs e)
    {
        var issue = _pointerPressedIssueId is { } pressedIssueId
            ? ViewModel.Issues.FirstOrDefault(candidate => candidate.Id == pressedIssueId)
            : null;
        issue ??= e.Items
            .Select(ResolveIssueListItem)
            .FirstOrDefault(item => item is not null)
            ?? ResolveIssueListItem(sender.SelectedItem);
        _pointerPressedIssueId = null;
        if (e.Items.Count > 1 || issue is null)
        {
            _draggedIssueId = null;
            e.Cancel = true;
            return;
        }

        _draggedIssueId = issue.Id;
        _dropTargetIssueId = null;
        _dropHint = IssueDropHint.None;
        ViewModel.BeginIssueDrag(issue);
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText(MainWindowViewModel.FormatIssueLabel(issue.Id, issue.Title));
    }

    private void IssueList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        var (target, container) = FindIssueDropTarget(e.OriginalSource);
        if (target is null || container is null)
        {
            if (_dropTargetIssueId is not null)
            {
                return;
            }

            _dropTargetIssueId = null;
            _dropHint = IssueDropHint.None;
            ViewModel.ClearIssueDropHint();
            if (_draggedIssueId is { } issueId &&
                ViewModel.Issues.FirstOrDefault(issue => issue.Id == issueId)?.IsNested == true)
            {
                ViewModel.SetRootDropPreview();
            }

            return;
        }

        if (target.Id == _draggedIssueId)
        {
            _dropTargetIssueId = null;
            _dropHint = IssueDropHint.None;
            ViewModel.ClearIssueDropHint();
            return;
        }

        UpdateIssueDropPreview(e, target, container);
    }

    private void IssueListItem_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text) ||
            sender is not TreeViewItem container ||
            ResolveIssueListItem(container) is not { } target)
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        UpdateIssueDropPreview(e, target, container);
    }

    private void UpdateIssueDropPreview(DragEventArgs e, IssueListItem target, TreeViewItem container)
    {
        if (target.Id == _draggedIssueId)
        {
            _dropTargetIssueId = null;
            _dropHint = IssueDropHint.None;
            ViewModel.ClearIssueDropHint();
            return;
        }

        if (container.ActualHeight <= 0)
        {
            _dropTargetIssueId = target.Id;
            _dropHint = IssueDropHint.Nest;
            ViewModel.UpdateIssueDropHint(target.Id, IssueDropHint.Nest);
            return;
        }

        var y = e.GetPosition(container).Y / container.ActualHeight;
        var hint = y < 0.22
            ? IssueDropHint.Before
            : y > 0.78
                ? IssueDropHint.After
                : IssueDropHint.Nest;
        _dropTargetIssueId = target.Id;
        _dropHint = hint;
        ViewModel.UpdateIssueDropHint(target.Id, hint);
    }

    private void IssueListItem_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is TreeViewItem container &&
            ResolveIssueListItem(container)?.Id == _dropTargetIssueId)
        {
            _dropTargetIssueId = null;
            _dropHint = IssueDropHint.None;
        }

        ViewModel.ClearIssueDropHint();
    }

    private async void IssueList_DragItemsCompleted(
        TreeView sender,
        TreeViewDragItemsCompletedEventArgs e)
    {
        var draggedIssueId = _draggedIssueId;
        var dropTargetIssueId = _dropTargetIssueId;
        var dropHint = _dropHint;
        _draggedIssueId = null;
        _dropTargetIssueId = null;
        _dropHint = IssueDropHint.None;
        if (draggedIssueId is null || e.DropResult != DataPackageOperation.Move)
        {
            ViewModel.EndIssueDrag();
            return;
        }

        var targetParent = dropHint == IssueDropHint.Nest && dropTargetIssueId is { } targetIssueId
            ? ViewModel.Issues.FirstOrDefault(issue => issue.Id == targetIssueId)
            : ResolveIssueListItem(e.NewParentItem);
        var visibleOrder = GetVisibleTreeOrder(ViewModel.Issues.RootItems);
        var draggedRow = ViewModel.Issues.FirstOrDefault(issue => issue.Id == draggedIssueId.Value);
        var alreadyUnderTarget = targetParent is not null &&
            (draggedRow?.Issue.Parents.Contains(targetParent.Id) == true ||
             targetParent.Issue.Children.Contains(draggedIssueId.Value));
        var moveToParent = targetParent is not null &&
                           targetParent.Id != draggedIssueId.Value &&
                           !alreadyUnderTarget;
        var moveToRoot = targetParent is null &&
                         draggedRow?.IsNested == true;

        try
        {
            if (moveToParent || moveToRoot)
            {
                if (!await ConfirmLeaveCurrentAsync())
                {
                    ViewModel.Issues.RebuildHierarchy();
                    return;
                }
            }

            await ViewModel.PersistVisibleIssueOrderAsync(visibleOrder);
            if (moveToRoot)
            {
                await ViewModel.MoveIssueToRootAsync(draggedIssueId.Value);
            }
            else if (moveToParent)
            {
                await ViewModel.MoveIssueUnderParentAsync(draggedIssueId.Value, targetParent!.Id);
            }
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            var title = moveToParent || moveToRoot
                ? GetString("MessageCannotMoveIssueTitle")
                : GetString("MessageCannotReorderIssuesTitle");
            await ShowMessageAsync(title, exception.Message);
            await ViewModel.RefreshIssuesAsync(preserveCurrentEditor: true);
        }
        finally
        {
            ViewModel.EndIssueDrag();
        }
    }

    private static IssueListItem? ResolveIssueListItem(object? item)
    {
        var current = item;
        for (var depth = 0; depth < 4 && current is not null; depth++)
        {
            var next = current switch
            {
                IssueListItem issue => issue,
                TreeViewNode node => node.Content,
                FrameworkElement { DataContext: { } dataContext } => dataContext,
                _ => null
            };
            if (next is IssueListItem resolved)
            {
                return resolved;
            }

            if (next is null || ReferenceEquals(next, current))
            {
                return null;
            }

            current = next;
        }

        return null;
    }

    private static (IssueListItem? Issue, TreeViewItem? Container) FindIssueDropTarget(object? originalSource)
    {
        var element = originalSource as DependencyObject;
        while (element is not null)
        {
            if (element is TreeViewItem container &&
                ResolveIssueListItem(container) is { } issue)
            {
                return (issue, container);
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return (null, null);
    }

    private static IReadOnlyList<long> GetVisibleTreeOrder(IEnumerable<IssueListItem> roots)
    {
        var issueIds = new List<long>();
        var visited = new HashSet<long>();
        var pending = new Stack<IssueListItem>(roots.Reverse());
        while (pending.TryPop(out var issue))
        {
            if (!visited.Add(issue.Id))
            {
                continue;
            }

            issueIds.Add(issue.Id);
            for (var index = issue.Children.Count - 1; index >= 0; index--)
            {
                pending.Push(issue.Children[index]);
            }
        }

        return issueIds;
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (e.Reason is AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ScheduleFilterRefresh();
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) =>
        ScheduleFilterRefresh();

    private void ScheduleFilterRefresh()
    {
        if (ViewModel.IsLoadingIssue)
        {
            return;
        }

        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshFromUiAsync();

    private async void Window_Activated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState is not WindowActivationState.Deactivated)
        {
            await RefreshFromUiAsync();
        }
    }

    private async void DeleteIssue_Click(object sender, RoutedEventArgs e)
    {
        var issue = ViewModel.SelectedIssue;
        if (issue is null)
        {
            return;
        }

        await DeleteIssueAsync(issue);
    }

    private async void DeleteIssueContextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IssueListItem row })
        {
            await DeleteIssueAsync(row.Issue);
        }
    }

    private async void MoveIssueToRootContextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IssueListItem row } ||
            !await ConfirmLeaveCurrentAsync())
        {
            return;
        }

        try
        {
            await ViewModel.MoveIssueToRootAsync(row.Id);
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync(GetString("MessageCannotMoveIssueTitle"), exception.Message);
            await ViewModel.RefreshIssuesAsync(preserveCurrentEditor: true);
        }
    }

    private async void AddParentIssuesButton_Click(object sender, RoutedEventArgs e) =>
        await AddIssueRelationshipsAsync(addingParents: true);

    private async void AddChildIssuesButton_Click(object sender, RoutedEventArgs e) =>
        await AddIssueRelationshipsAsync(addingParents: false);

    private async Task AddIssueRelationshipsAsync(bool addingParents)
    {
        if (ViewModel.SelectedIssue is null || !await ConfirmLeaveCurrentAsync())
        {
            return;
        }

        try
        {
            var candidates = await ViewModel.GetOpenIssueCandidatesAsync(addingParents);
            if (candidates.Count == 0)
            {
                await ShowMessageAsync(
                    GetString("DialogNoOpenIssuesTitle"),
                    GetString("DialogNoOpenIssuesMessage"));
                return;
            }

            var relationshipName = GetString(addingParents ? "ParentIssueLinkRole" : "ChildIssueLinkRole");
            var title = GetString(addingParents ? "DialogAddParentIssuesTitle" : "DialogAddChildIssuesTitle");
            var selectedIds = await ShowIssueSelectionDialogAsync(
                title,
                Format("DialogSelectOpenIssuesFormat", relationshipName),
                candidates,
                addingParents ? "AddParentIssueSelectionDialog" : "AddChildIssueSelectionDialog");
            if (selectedIds is not { Count: > 0 })
            {
                return;
            }

            if (addingParents)
            {
                await ViewModel.AddParentIssuesAsync(selectedIds);
            }
            else
            {
                await ViewModel.AddChildIssuesAsync(selectedIds);
            }
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync(GetString("MessageCannotUpdateIssueLinksTitle"), exception.Message);
        }
    }

    private async Task<IReadOnlyList<long>?> ShowIssueSelectionDialogAsync(
        string title,
        string instruction,
        IReadOnlyList<Issue> candidates,
        string automationId)
    {
        var selectionList = new ListView
        {
            MaxHeight = 320,
            SelectionMode = ListViewSelectionMode.Multiple
        };
        AutomationProperties.SetAutomationId(selectionList, "IssueLinkSelectionList");
        AutomationProperties.SetName(selectionList, title);
        foreach (var issue in candidates)
        {
            var label = MainWindowViewModel.FormatIssueLabel(issue.Id, issue.Title);
            var item = new ListViewItem
            {
                Content = label,
                Tag = issue.Id
            };
            AutomationProperties.SetAutomationId(item, $"IssueLinkOption{issue.Id.ToString(CultureInfo.InvariantCulture)}");
            AutomationProperties.SetName(item, label);
            selectionList.Items.Add(item);
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = instruction,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(selectionList);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = GetString("ButtonAddSelectedIssues"),
            CloseButtonText = GetString("ButtonCancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        AutomationProperties.SetAutomationId(dialog, automationId);
        AutomationProperties.SetName(dialog, title);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return selectionList.SelectedItems
            .OfType<ListViewItem>()
            .Select(item => item.Tag)
            .OfType<long>()
            .Distinct()
            .Order()
            .ToArray();
    }

    private async Task DeleteIssueAsync(Issue issue)
    {
        var answer = await ShowConfirmationAsync(
            GetString("DialogDeleteIssueTitle"),
            Format("DialogDeleteIssueFormat", issue.Id, issue.Title),
            GetString("ButtonDelete"),
            GetString("ButtonCancel"));
        if (answer != ContentDialogResult.Primary || !await ConfirmLeaveCurrentAsync())
        {
            return;
        }

        try
        {
            await ViewModel.DeleteIssueCommand.ExecuteAsync(issue.Id);
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync(GetString("MessageCannotDeleteIssueTitle"), exception.Message);
        }
    }

    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_allowClose || !ViewModel.IsDirty)
        {
            return;
        }

        e.Cancel = true;
        if (_closingPromptVisible)
        {
            return;
        }

        _closingPromptVisible = true;
        try
        {
            if (await ConfirmLeaveCurrentAsync())
            {
                _allowClose = true;
                Close();
            }
        }
        finally
        {
            _closingPromptVisible = false;
        }
    }

    private void Editor_Changed(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsLoadingIssue || ViewModel.SelectedIssue is null)
        {
            return;
        }

        ViewModel.StatusMessage = GetString("MessageSavingChanges");
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void BodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ViewModel.IsLoadingIssue || ViewModel.SelectedIssue is null)
        {
            ViewModel.ClearIssueReferenceSuggestions();
            return;
        }

        Editor_Changed(sender, e);
        if (_suppressIssueReferenceSuggestions ||
            sender is not TextBox textBox ||
            textBox.SelectionLength != 0 ||
            !IssueBodyLinkParser.TryGetReferenceAtCaret(
                textBox.Text,
                textBox.SelectionStart,
                out var start,
                out var length,
                out var prefix))
        {
            ViewModel.ClearIssueReferenceSuggestions();
            return;
        }

        _issueReferenceStart = start;
        _issueReferenceLength = length;
        ViewModel.UpdateIssueReferenceSuggestions(prefix);
        IssueReferenceSuggestionsList.SelectedIndex =
            ViewModel.IssueReferenceSuggestions.Count > 0 ? 0 : -1;
    }

    private void IssueReferenceSuggestionsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is IssueListItem suggestion)
        {
            ApplyIssueReferenceSuggestion(suggestion);
        }
    }

    private void ApplyIssueReferenceSuggestion(IssueListItem suggestion)
    {
        if (!IssueBodyLinkParser.TryReplaceReference(
                BodyBox.Text,
                _issueReferenceStart,
                _issueReferenceLength,
                suggestion.Id,
                out var updatedBody,
                out var caretIndex))
        {
            ViewModel.ClearIssueReferenceSuggestions();
            return;
        }

        _suppressIssueReferenceSuggestions = true;
        try
        {
            BodyBox.Text = updatedBody;
            ViewModel.IssueBody = updatedBody;
            BodyBox.SelectionStart = caretIndex;
            BodyBox.SelectionLength = 0;
        }
        finally
        {
            _suppressIssueReferenceSuggestions = false;
        }

        ViewModel.ClearIssueReferenceSuggestions();
        BodyBox.Focus(FocusState.Programmatic);
    }

    private void BodyBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (ViewModel.HasIssueReferenceSuggestions)
        {
            switch (e.Key)
            {
                case VirtualKey.Down:
                    IssueReferenceSuggestionsList.SelectedIndex = Math.Min(
                        IssueReferenceSuggestionsList.SelectedIndex + 1,
                        ViewModel.IssueReferenceSuggestions.Count - 1);
                    e.Handled = true;
                    return;
                case VirtualKey.Up:
                    IssueReferenceSuggestionsList.SelectedIndex = Math.Max(
                        IssueReferenceSuggestionsList.SelectedIndex - 1,
                        0);
                    e.Handled = true;
                    return;
                case VirtualKey.Enter when IssueReferenceSuggestionsList.SelectedItem is IssueListItem suggestion:
                    ApplyIssueReferenceSuggestion(suggestion);
                    e.Handled = true;
                    return;
                case VirtualKey.Escape:
                    ViewModel.ClearIssueReferenceSuggestions();
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key != VirtualKey.Tab || sender is not TextBox textBox)
        {
            return;
        }

        var selectionStart = textBox.SelectionStart;
        textBox.SelectedText = "\t";
        textBox.SelectionStart = selectionStart + 1;
        e.Handled = true;
    }

    private async void NewIssue_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.ProjectRoot) || !await ConfirmLeaveCurrentAsync())
        {
            return;
        }

        try
        {
            await ViewModel.CreateIssueCommand.ExecuteAsync(null);
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync(GetString("MessageCannotCreateIssueTitle"), exception.Message);
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = GetString("ButtonOk"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        AutomationProperties.SetAutomationId(dialog, "MessageDialog");
        AutomationProperties.SetName(dialog, title);
        await dialog.ShowAsync();
    }

    private async Task<ContentDialogResult> ShowConfirmationAsync(
        string title,
        string message,
        string primaryButtonText,
        string closeButtonText,
        string? secondaryButtonText = null)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        AutomationProperties.SetAutomationId(dialog, "ConfirmationDialog");
        AutomationProperties.SetName(dialog, title);
        if (secondaryButtonText is not null)
        {
            dialog.SecondaryButtonText = secondaryButtonText;
        }

        return await dialog.ShowAsync();
    }

    private static string GetString(string key) => Resources.GetString(key);

    private static string Format(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, GetString(key), values);
}
