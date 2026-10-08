using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using OpenIssue.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;
using VirtualKey = Windows.System.VirtualKey;

namespace OpenIssue.Desktop;

public sealed partial class MainWindow : Window
{
    private const double CollapsedIssueListColumnWidth = 44;
    private const double CollapsedIssueDetailsColumnWidth = 44;
    private static readonly ResourceLoader Resources = new();
    private readonly ColorThemePreferenceStore _colorThemePreferenceStore = new();
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
    private long? _relationshipDropTargetIssueId;
    private IssueDropHint _dropHint;
    private bool _relationshipDropHandled;
    private readonly string? _launchArguments;
    private ScrollViewer? _bodyScrollViewer;
    private double _expandedIssueListWidth;
    private double _expandedIssueListMinWidth;
    private double _expandedIssueDetailsWidth;
    private double _expandedIssueDetailsMinWidth;
    private long? _tagInputIssueId;
    private ScrollContentPresenter? _bodyScrollContentPresenter;
    private ColorThemePreference _colorThemePreference = ColorThemePreference.System;
    private ResourceDictionary? _activeColorThemeDictionary;

    public MainWindowViewModel ViewModel { get; }

    /// <summary>Determines whether the issue list's empty-state guidance should be visible.</summary>
    /// <param name="issueCount">The number of issues currently shown.</param>
    /// <param name="isRefreshing">Whether the issue list is refreshing.</param>
    /// <returns>Visible when no issues are shown and a refresh is not in progress.</returns>
    public static Visibility EmptyStateVisibility(int issueCount, bool isRefreshing) =>
        issueCount == 0 && !isRefreshing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Formats the accessible name for removing a parent relationship.</summary>
    /// <param name="issueLabel">The linked issue label.</param>
    /// <returns>A localized action name.</returns>
    public static string FormatParentRelationshipRemovalName(string issueLabel) =>
        Format("RemoveParentRelationshipNameFormat", issueLabel);

    /// <summary>Formats the accessible name for removing a child relationship.</summary>
    /// <param name="issueLabel">The linked issue label.</param>
    /// <returns>A localized action name.</returns>
    public static string FormatChildRelationshipRemovalName(string issueLabel) =>
        Format("RemoveChildRelationshipNameFormat", issueLabel);

    /// <summary>Creates an automation ID for a parent relationship removal button.</summary>
    /// <param name="issueId">The linked issue ID.</param>
    /// <returns>A stable automation ID.</returns>
    public static string ParentRelationshipRemovalAutomationId(long issueId) =>
        $"RemoveParentRelationship_{issueId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Creates an automation ID for a child relationship removal button.</summary>
    /// <param name="issueId">The linked issue ID.</param>
    /// <returns>A stable automation ID.</returns>
    public static string ChildRelationshipRemovalAutomationId(long issueId) =>
        $"RemoveChildRelationship_{issueId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Shows the temporary root-level drop target during an issue drag.</summary>
    public static Visibility DraggingVisibility(bool isDragging) =>
        isDragging ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows the nested-row guide for issues that have a parent.</summary>
    public static Visibility NestedIssueVisibility(bool isNested) =>
        isNested ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows group headings and hides issue-row content.</summary>
    /// <param name="isGroup">Whether the tree node is a group heading.</param>
    /// <returns>Visible for a group heading.</returns>
    public static Visibility IssueListGroupHeaderVisibility(bool isGroup) =>
        isGroup ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows issue-row content and hides group headings.</summary>
    /// <param name="isGroup">Whether the tree node is a group heading.</param>
    /// <returns>Visible for an issue row.</returns>
    public static Visibility IssueListRowVisibility(bool isGroup) =>
        isGroup ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Shows the empty state when an issue has no links in a relationship group.</summary>
    /// <param name="hasRelationships">Whether the relationship group contains any links.</param>
    /// <returns>Visible when the group is empty.</returns>
    public static Visibility EmptyRelationshipVisibility(bool hasRelationships) =>
        hasRelationships ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Shows relationship links only when the group contains at least one issue.</summary>
    /// <param name="hasRelationships">Whether the relationship group contains any links.</param>
    /// <returns>Visible when the group contains links.</returns>
    public static Visibility RelationshipItemsVisibility(bool hasRelationships) =>
        hasRelationships ? Visibility.Visible : Visibility.Collapsed;

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
        _colorThemePreference = LoadColorThemePreference();
        ApplyColorTheme(_colorThemePreference);
        ColorThemeSelector.SelectedValue = _colorThemePreference.ToString();
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
            (int)Math.Ceiling(1280 * scale),
            (int)Math.Ceiling(760 * scale)));
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.WindowTitle))
        {
            Title = ViewModel.WindowTitle;
        }

        if (e.PropertyName == nameof(ViewModel.SelectedIssue))
        {
            var selectedId = ViewModel.SelectedIssue?.Id;
            if (selectedId != _tagInputIssueId)
            {
                _tagInputIssueId = selectedId;
                TagInput.Text = string.Empty;
                ViewModel.UpdateTagSuggestions(string.Empty);
            }
        }
    }

    private ColorThemePreference LoadColorThemePreference()
    {
        try
        {
            return _colorThemePreferenceStore.Load();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not load the saved color theme preference: {0}", exception.Message);
            ViewModel.StatusMessage = Resources.GetString("MessageColorThemePreferenceLoadFailed");
            return ColorThemePreference.System;
        }
    }

    private void ColorThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ColorThemeSelector.SelectedValue is not string selectedValue ||
            !Enum.TryParse(selectedValue, out ColorThemePreference selectedPreference) ||
            !Enum.IsDefined(selectedPreference))
        {
            throw new InvalidOperationException("The color theme selector has an invalid selection.");
        }

        if (selectedPreference == _colorThemePreference)
        {
            return;
        }

        try
        {
            _colorThemePreferenceStore.Save(selectedPreference);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError("Could not save the selected color theme preference: {0}", exception.Message);
            ViewModel.StatusMessage = Resources.GetString("MessageColorThemePreferenceSaveFailed");
            ColorThemeSelector.SelectedValue = _colorThemePreference.ToString();
            return;
        }

        _colorThemePreference = selectedPreference;
        ApplyColorTheme(selectedPreference);
    }

    private void ApplyColorTheme(ColorThemePreference preference)
    {
        var root = Content as FrameworkElement
            ?? throw new InvalidOperationException("The main window content must be a FrameworkElement.");
        var dictionarySource = preference switch
        {
            ColorThemePreference.Dracula => "ms-appx:///Themes/Dracula.xaml",
            _ => null
        };
        var nextDictionary = dictionarySource is null
            ? null
            : new ResourceDictionary { Source = new Uri(dictionarySource) };
        var mergedDictionaries = Application.Current.Resources.MergedDictionaries;
        var refreshThemeResources = _activeColorThemeDictionary is not null || nextDictionary is not null;

        if (_activeColorThemeDictionary is not null)
        {
            mergedDictionaries.Remove(_activeColorThemeDictionary);
        }

        if (nextDictionary is not null)
        {
            mergedDictionaries.Add(nextDictionary);
        }

        _activeColorThemeDictionary = nextDictionary;
        if (refreshThemeResources)
        {
            // Reapply through a different theme so existing ThemeResource bindings pick up the dictionary swap.
            root.RequestedTheme = ElementTheme.Light;
        }

        root.RequestedTheme = preference switch
        {
            ColorThemePreference.System => ElementTheme.Default,
            ColorThemePreference.Light => ElementTheme.Light,
            _ => ElementTheme.Dark
        };
        AppWindow.TitleBar.PreferredTheme = preference switch
        {
            ColorThemePreference.System => TitleBarTheme.UseDefaultAppMode,
            ColorThemePreference.Light => TitleBarTheme.Light,
            _ => TitleBarTheme.Dark
        };
        UpdateBodyInlineHighlights();
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

    private void SetWatcher(string dataDirectory)
    {
        _watcher = new FileSystemWatcher(dataDirectory, "*.md")
        {
            IncludeSubdirectories = true,
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
        var previousNode = ViewModel.SelectedIssueListNode;
        if (previous?.Id == issue.Id)
        {
            return;
        }

        if (previous is not null &&
            previous.Id != issue.Id &&
            !await ConfirmLeaveCurrentAsync())
        {
            IssueList.SelectedItem = previousNode;
            return;
        }

        if (!await ViewModel.SelectIssueAsync(issue))
        {
            IssueList.SelectedItem = previousNode;
        }
    }

    private void IssueListItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _relationshipDropTargetIssueId = ViewModel.IsEditorEnabled
            ? ViewModel.SelectedIssue?.Id
            : null;
        _pointerPressedIssueId = sender is TreeViewItem container
            ? ResolveIssueListItem(container)?.Id
            : null;
    }

    private void IssueListItem_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pointerPressedIssueId = null;
        if (_draggedIssueId is null)
        {
            _relationshipDropTargetIssueId = null;
        }
    }

    private void IssueList_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs e)
    {
        if (!ViewModel.CanReorderIssueList)
        {
            e.Cancel = true;
            return;
        }

        var pointerPressedIssueId = _pointerPressedIssueId;
        var issue = pointerPressedIssueId is { } pressedIssueId
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
            _relationshipDropTargetIssueId = null;
            _relationshipDropHandled = false;
            e.Cancel = true;
            return;
        }

        _draggedIssueId = issue.Id;
        if (pointerPressedIssueId is null)
        {
            _relationshipDropTargetIssueId = ViewModel.IsEditorEnabled
                ? ViewModel.SelectedIssue?.Id
                : null;
        }

        _dropTargetIssueId = null;
        _dropHint = IssueDropHint.None;
        _relationshipDropHandled = false;
        ViewModel.BeginIssueDrag(issue);
        e.Data.RequestedOperation = DataPackageOperation.Move | DataPackageOperation.Link;
        e.Data.SetText(MainWindowViewModel.FormatIssueLabel(issue.Id, issue.Title));
    }

    private void IssueList_DragOver(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanReorderIssueList)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

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
        if (!ViewModel.CanReorderIssueList ||
            !e.DataView.Contains(StandardDataFormats.Text) ||
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

    private void ParentIssuesDropTarget_DragOver(object sender, DragEventArgs e) =>
        RelationshipDropTarget_DragOver(sender, e, addingParents: true);

    private void ChildIssuesDropTarget_DragOver(object sender, DragEventArgs e) =>
        RelationshipDropTarget_DragOver(sender, e, addingParents: false);

    private void RelationshipDropTarget_DragOver(
        object sender,
        DragEventArgs e,
        bool addingParents)
    {
        if (sender is not Border dropTarget ||
            !e.DataView.Contains(StandardDataFormats.Text) ||
            _draggedIssueId is not { } draggedIssueId ||
            _relationshipDropTargetIssueId is not { } targetIssueId ||
            draggedIssueId == targetIssueId)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ClearRelationshipDropTargetHighlight(sender);
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.Caption = GetString(
            addingParents ? "ParentIssuesDropCaption" : "ChildIssuesDropCaption");
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        dropTarget.BorderThickness = new Thickness(1);
    }

    private void IssueRelationshipDropTarget_DragLeave(object sender, DragEventArgs e) =>
        ClearRelationshipDropTargetHighlight(sender);

    private static void ClearRelationshipDropTargetHighlight(object sender)
    {
        if (sender is Border dropTarget)
        {
            dropTarget.BorderThickness = new Thickness(0);
        }
    }

    private async void IssueList_DragItemsCompleted(
        TreeView sender,
        TreeViewDragItemsCompletedEventArgs e)
    {
        var draggedIssueId = _draggedIssueId;
        var dropTargetIssueId = _dropTargetIssueId;
        var dropHint = _dropHint;
        var relationshipDropHandled = _relationshipDropHandled;
        _draggedIssueId = null;
        _dropTargetIssueId = null;
        _relationshipDropTargetIssueId = null;
        _dropHint = IssueDropHint.None;
        _relationshipDropHandled = false;
        ClearRelationshipDropTargetHighlight(ParentIssuesDropTarget);
        ClearRelationshipDropTargetHighlight(ChildIssuesDropTarget);
        if (!ViewModel.CanReorderIssueList ||
            relationshipDropHandled ||
            draggedIssueId is null ||
            e.DropResult != DataPackageOperation.Move)
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
                IssueListNode issueNode => issueNode.Row,
                IssueListItem issue => issue,
                TreeViewNode treeNode => treeNode.Content,
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

    private static T? FindVisualDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
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

    private void CollapseIssueList_Click(object sender, RoutedEventArgs e)
    {
        _expandedIssueListWidth = IssueListColumn.ActualWidth;
        _expandedIssueListMinWidth = IssueListColumn.MinWidth;

        IssueListColumn.MinWidth = CollapsedIssueListColumnWidth;
        IssueListColumn.Width = new GridLength(CollapsedIssueListColumnWidth);
        IssueListPane.Visibility = Visibility.Collapsed;
        IssueListDivider.Visibility = Visibility.Collapsed;
        IssueListSplitter.Visibility = Visibility.Collapsed;
        CollapsedIssueListRail.Visibility = Visibility.Visible;
        ExpandIssueListButton.Focus(FocusState.Programmatic);
    }

    private void ExpandIssueList_Click(object sender, RoutedEventArgs e)
    {
        IssueListColumn.MinWidth = _expandedIssueListMinWidth;
        IssueListColumn.Width = new GridLength(
            Math.Max(_expandedIssueListWidth, _expandedIssueListMinWidth));
        IssueListPane.Visibility = Visibility.Visible;
        IssueListDivider.Visibility = Visibility.Visible;
        IssueListSplitter.Visibility = Visibility.Visible;
        CollapsedIssueListRail.Visibility = Visibility.Collapsed;
        CollapseIssueListButton.Focus(FocusState.Programmatic);
    }

    private void CollapseIssueDetails_Click(object sender, RoutedEventArgs e)
    {
        _expandedIssueDetailsWidth = IssueDetailsColumn.ActualWidth;
        _expandedIssueDetailsMinWidth = IssueDetailsColumn.MinWidth;

        IssueDetailsColumn.MinWidth = CollapsedIssueDetailsColumnWidth;
        IssueDetailsColumn.Width = new GridLength(CollapsedIssueDetailsColumnWidth);
        IssueDetailsPane.Visibility = Visibility.Collapsed;
        IssueDetailsDivider.Visibility = Visibility.Collapsed;
        IssueDetailsSplitter.Visibility = Visibility.Collapsed;
        CollapsedIssueDetailsRail.Visibility = Visibility.Visible;
        ExpandIssueDetailsButton.Focus(FocusState.Programmatic);
    }

    private void ExpandIssueDetails_Click(object sender, RoutedEventArgs e)
    {
        IssueDetailsColumn.MinWidth = _expandedIssueDetailsMinWidth;
        IssueDetailsColumn.Width = new GridLength(
            Math.Max(_expandedIssueDetailsWidth, _expandedIssueDetailsMinWidth));
        IssueDetailsPane.Visibility = Visibility.Visible;
        IssueDetailsDivider.Visibility = Visibility.Visible;
        IssueDetailsSplitter.Visibility = Visibility.Visible;
        CollapsedIssueDetailsRail.Visibility = Visibility.Collapsed;
        CollapseIssueDetailsButton.Focus(FocusState.Programmatic);
    }

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
        if (sender is FrameworkElement { DataContext: IssueListNode { Row: { } row } })
        {
            await DeleteIssueAsync(row.Issue);
        }
    }

    private async void MoveIssueToRootContextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IssueListNode { Row: { } row } } ||
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

    private async void DuplicateIssueContextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IssueListNode { Row: { } row } } ||
            !await ConfirmLeaveCurrentAsync())
        {
            return;
        }

        try
        {
            await ViewModel.DuplicateIssueCommand.ExecuteAsync(row.Id);
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync(GetString("MessageCannotDuplicateIssueTitle"), exception.Message);
        }
    }

    private async void EditIssueExternallyContextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IssueListNode { Row: { } row } } ||
            !await ConfirmLeaveCurrentAsync())
        {
            return;
        }

        try
        {
            var issuePath = await ViewModel.GetIssueFilePathAsync(row.Id);
            Process.Start(new ProcessStartInfo(issuePath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            await ShowMessageAsync(GetString("MessageCannotOpenIssueExternallyTitle"), exception.Message);
        }
    }

    private async void RevealIssueInExplorerContextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IssueListNode { Row: { } row } })
        {
            return;
        }

        try
        {
            var issuePath = await ViewModel.GetIssueFilePathAsync(row.Id);
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add($"/select,{issuePath}");
            Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            await ShowMessageAsync(GetString("MessageCannotRevealIssueTitle"), exception.Message);
        }
    }

    private async void AddParentIssuesButton_Click(object sender, RoutedEventArgs e) =>
        await AddIssueRelationshipsAsync(addingParents: true);

    private async void AddChildIssuesButton_Click(object sender, RoutedEventArgs e) =>
        await AddIssueRelationshipsAsync(addingParents: false);

    private async void ParentIssuesDropTarget_Drop(object sender, DragEventArgs e) =>
        await AddIssueRelationshipFromDropAsync(addingParents: true, sender, e);

    private async void ChildIssuesDropTarget_Drop(object sender, DragEventArgs e) =>
        await AddIssueRelationshipFromDropAsync(addingParents: false, sender, e);

    private async Task AddIssueRelationshipFromDropAsync(
        bool addingParents,
        object sender,
        DragEventArgs e)
    {
        if (sender is not Border dropTarget ||
            !e.DataView.Contains(StandardDataFormats.Text) ||
            _draggedIssueId is not { } droppedIssueId ||
            _relationshipDropTargetIssueId is not { } targetIssueId ||
            droppedIssueId == targetIssueId)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Link;
        _relationshipDropHandled = true;
        ClearRelationshipDropTargetHighlight(dropTarget);

        try
        {
            if (!await ConfirmLeaveCurrentAsync())
            {
                return;
            }

            var target = ViewModel.Issues.FirstOrDefault(issue => issue.Id == targetIssueId);
            if (target is null)
            {
                await ShowMessageAsync(
                    GetString("MessageCannotUpdateIssueLinksTitle"),
                    Format("MessageRelatedIssueNotAvailable", targetIssueId));
                return;
            }

            if (ViewModel.SelectedIssue?.Id != targetIssueId)
            {
                if (!await ViewModel.SelectIssueAsync(target.Issue))
                {
                    return;
                }

                IssueList.SelectedItem = ViewModel.Issues.FindNode(target.Id);
            }

            if (addingParents)
            {
                await ViewModel.AddParentIssuesAsync([droppedIssueId]);
                ViewModel.ExpandIssueAncestors(targetIssueId);
            }
            else
            {
                await ViewModel.AddChildIssuesAsync([droppedIssueId]);
                ViewModel.ExpandIssueAncestors(droppedIssueId);
            }
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync(GetString("MessageCannotUpdateIssueLinksTitle"), exception.Message);
            await ViewModel.RefreshIssuesAsync(preserveCurrentEditor: true);
        }
        finally
        {
            ClearRelationshipDropTargetHighlight(dropTarget);
        }
    }

    private async void RelatedIssue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: long issueId } ||
            ViewModel.SelectedIssue?.Id == issueId)
        {
            return;
        }

        var row = ViewModel.Issues.FirstOrDefault(candidate => candidate.Id == issueId);
        if (row is null)
        {
            ViewModel.SearchText = string.Empty;
            ViewModel.SelectedStatusFilter = ViewModel.StatusFilterOptions[0];
            ViewModel.SelectedPriorityFilter = ViewModel.PriorityFilterOptions[0];
            ViewModel.DraftOnly = false;
            _refreshTimer.Stop();
            if (!await ViewModel.RefreshIssuesAsync(preserveCurrentEditor: true))
            {
                return;
            }

            row = ViewModel.Issues.FirstOrDefault(candidate => candidate.Id == issueId);
        }

        if (row is null)
        {
            ViewModel.StatusMessage = Format("MessageRelatedIssueNotAvailable", issueId);
            return;
        }

        ViewModel.ExpandIssueAncestors(issueId);
        IssueList.SelectedItem = ViewModel.Issues.FindNode(issueId);
    }

    private async void RemoveParentRelationshipButton_Click(object sender, RoutedEventArgs e) =>
        await RemoveIssueRelationshipAsync(removingParent: true, sender);

    private async void RemoveChildRelationshipButton_Click(object sender, RoutedEventArgs e) =>
        await RemoveIssueRelationshipAsync(removingParent: false, sender);

    private async Task RemoveIssueRelationshipAsync(bool removingParent, object sender)
    {
        if (sender is not Button { Tag: long issueId } ||
            ViewModel.SelectedIssue is null ||
            !await ConfirmLeaveCurrentAsync())
        {
            return;
        }

        try
        {
            if (removingParent)
            {
                await ViewModel.RemoveParentIssueAsync(issueId);
            }
            else
            {
                await ViewModel.RemoveChildIssueAsync(issueId);
            }
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync(GetString("MessageCannotUpdateIssueLinksTitle"), exception.Message);
            await ViewModel.RefreshIssuesAsync(preserveCurrentEditor: true);
        }
    }

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
        ScheduleEditorSave();
    }

    private void ScheduleEditorSave()
    {
        if (ViewModel.IsLoadingIssue || ViewModel.SelectedIssue is null)
        {
            return;
        }

        ViewModel.StatusMessage = GetString("MessageSavingChanges");
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void TagInput_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (e.Reason is not AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        var separatorIndex = sender.Text.LastIndexOf(',');
        if (separatorIndex >= 0)
        {
            if (ViewModel.AddIssueTags(sender.Text[..separatorIndex]))
            {
                ScheduleEditorSave();
            }

            sender.Text = sender.Text[(separatorIndex + 1)..].TrimStart();
        }

        ViewModel.UpdateTagSuggestions(sender.Text);
    }

    private void TagInput_SuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs e)
    {
        if (e.SelectedItem is string tag && ViewModel.AddIssueTags(tag))
        {
            ScheduleEditorSave();
        }

        sender.Text = string.Empty;
        ViewModel.UpdateTagSuggestions(string.Empty);
    }

    private void TagInput_QuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs e)
    {
        var tagText = e.ChosenSuggestion as string ?? sender.Text;
        if (ViewModel.AddIssueTags(tagText))
        {
            ScheduleEditorSave();
        }

        sender.Text = string.Empty;
        ViewModel.UpdateTagSuggestions(string.Empty);
    }

    private void TagPill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && ViewModel.RemoveIssueTag(tag))
        {
            ScheduleEditorSave();
        }
    }

    private void BodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateBodyInlineHighlights();
        if (ViewModel.IsLoadingIssue || ViewModel.SelectedIssue is null)
        {
            ViewModel.ClearIssueReferenceSuggestions();
            return;
        }

        Editor_Changed(sender, e);
        if (sender is TextBox textBox)
        {
            UpdateIssueReferenceSuggestionsForCaret(textBox);
        }
    }

    private void BodyBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateBodyInlineHighlights();
        if (!ViewModel.IsLoadingIssue && ViewModel.SelectedIssue is not null && sender is TextBox textBox)
        {
            UpdateIssueReferenceSuggestionsForCaret(textBox);
        }
    }

    private void BodyBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (_bodyScrollViewer is not null)
        {
            _bodyScrollViewer.ViewChanged -= BodyScrollViewer_ViewChanged;
        }

        _bodyScrollViewer = FindVisualDescendant<ScrollViewer>(BodyBox);
        if (_bodyScrollViewer is not null)
        {
            _bodyScrollViewer.ViewChanged += BodyScrollViewer_ViewChanged;
        }

        _bodyScrollContentPresenter =
            FindVisualDescendant<ScrollContentPresenter>(BodyBox);
        UpdateBodyInlineHighlights();
    }

    private void BodyBox_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateBodyInlineHighlights();

    private void BodyBox_ActualThemeChanged(FrameworkElement sender, object args) =>
        UpdateBodyInlineHighlights();

    private void BodyScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        UpdateBodyInlineHighlights();

    private void UpdateBodyInlineHighlights()
    {
        BodyHighlightCanvas.Children.Clear();
        if (!ViewModel.IsEditorEnabled ||
            string.IsNullOrEmpty(BodyBox.Text) ||
            BodyHighlightCanvas.ActualWidth <= 0 ||
            BodyHighlightCanvas.ActualHeight <= 0 ||
            _bodyScrollContentPresenter is not { } contentPresenter)
        {
            return;
        }

        var contentToCanvas = contentPresenter.TransformToVisual(BodyHighlightCanvas);
        AddBodyInlineHighlights(
            IssueBodyTagParser.GetTagRanges(BodyBox.Text),
            (Style)RootGrid.Resources["IssueBodyTagHighlightStyle"],
            contentToCanvas);
        AddBodyInlineHighlights(
            IssueBodyLinkParser.GetReferenceRanges(BodyBox.Text),
            (Style)RootGrid.Resources["IssueBodyTagHighlightStyle"],
            contentToCanvas);
    }

    private void AddBodyInlineHighlights(
        IEnumerable<(int Start, int Length)> ranges,
        Style style,
        Microsoft.UI.Xaml.Media.GeneralTransform contentToCanvas)
    {
        foreach (var range in ranges)
        {
            foreach (var textRect in GetBodyTextRangeRects(
                         range.Start,
                         range.Length,
                         contentToCanvas))
            {
                AddBodyInlineHighlight(textRect, style);
            }
        }
    }

    private IEnumerable<Rect> GetBodyTextRangeRects(
        int start,
        int length,
        Microsoft.UI.Xaml.Media.GeneralTransform contentToCanvas)
    {
        var segmentTop = double.NaN;
        var segmentHeight = 0d;
        var segmentLeft = double.PositiveInfinity;
        var segmentRight = double.NegativeInfinity;
        var end = start + length;
        for (var index = start; index < end; index++)
        {
            var leadingEdge = ToBodyHighlightCanvasRect(
                BodyBox.GetRectFromCharacterIndex(index, false),
                contentToCanvas);
            var trailingEdge = ToBodyHighlightCanvasRect(
                BodyBox.GetRectFromCharacterIndex(index, true),
                contentToCanvas);
            if (leadingEdge.IsEmpty || trailingEdge.IsEmpty)
            {
                continue;
            }

            var top = Math.Min(leadingEdge.Top, trailingEdge.Top);
            if (!double.IsFinite(top))
            {
                continue;
            }

            if (!double.IsNaN(segmentTop) && Math.Abs(top - segmentTop) > 1)
            {
                if (segmentRight > segmentLeft)
                {
                    yield return new Rect(
                        segmentLeft,
                        segmentTop,
                        segmentRight - segmentLeft,
                        segmentHeight);
                }

                segmentTop = double.NaN;
                segmentHeight = 0;
                segmentLeft = double.PositiveInfinity;
                segmentRight = double.NegativeInfinity;
            }

            if (double.IsNaN(segmentTop))
            {
                segmentTop = top;
            }

            segmentHeight = Math.Max(
                segmentHeight,
                Math.Max(leadingEdge.Height, trailingEdge.Height));
            segmentLeft = Math.Min(
                segmentLeft,
                Math.Min(leadingEdge.X, trailingEdge.X));
            segmentRight = Math.Max(
                segmentRight,
                Math.Max(leadingEdge.X, trailingEdge.X));
        }

        if (segmentRight > segmentLeft)
        {
            yield return new Rect(
                segmentLeft,
                segmentTop,
                segmentRight - segmentLeft,
                segmentHeight);
        }
    }

    private static Rect ToBodyHighlightCanvasRect(
        Rect textContentRect,
        Microsoft.UI.Xaml.Media.GeneralTransform contentToCanvas)
    {
        if (textContentRect.IsEmpty)
        {
            return Rect.Empty;
        }

        var topLeft = contentToCanvas.TransformPoint(
            new Point(textContentRect.Left, textContentRect.Top));
        var bottomRight = contentToCanvas.TransformPoint(
            new Point(textContentRect.Right, textContentRect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void AddBodyInlineHighlight(Rect textRect, Style style)
    {
        const double horizontalPadding = 4;
        const double verticalInset = 1;

        var canvasWidth = BodyHighlightCanvas.ActualWidth;
        var canvasHeight = BodyHighlightCanvas.ActualHeight;
        var left = Math.Max(0, textRect.Left - horizontalPadding);
        var right = Math.Min(canvasWidth, textRect.Right + horizontalPadding);
        var top = Math.Max(0, textRect.Top + verticalInset);
        var bottom = Math.Min(canvasHeight, textRect.Bottom - verticalInset);
        if (right <= left || bottom <= top)
        {
            return;
        }

        var height = bottom - top;
        var pill = new Border
        {
            Style = style,
            Width = right - left,
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Opacity = BodyBox.ActualTheme == ElementTheme.Dark ? 0.42 : 1,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(pill, left);
        Canvas.SetTop(pill, top);
        BodyHighlightCanvas.Children.Add(pill);
    }

    private void MarkdownHeading_Click(object sender, RoutedEventArgs e) =>
        ApplyMarkdownEdit(MarkdownTextEditor.CreateHeadingEdit(
            BodyBox.Text,
            BodyBox.SelectionStart,
            BodyBox.SelectionLength));

    private void MarkdownBold_Click(object sender, RoutedEventArgs e) =>
        ApplyMarkdownEdit(MarkdownTextEditor.CreateInlineFormatEdit(
            BodyBox.Text,
            BodyBox.SelectionStart,
            BodyBox.SelectionLength,
            "**",
            "**"));

    private void MarkdownItalic_Click(object sender, RoutedEventArgs e) =>
        ApplyMarkdownEdit(MarkdownTextEditor.CreateInlineFormatEdit(
            BodyBox.Text,
            BodyBox.SelectionStart,
            BodyBox.SelectionLength,
            "*",
            "*"));

    private void MarkdownLink_Click(object sender, RoutedEventArgs e) =>
        ApplyMarkdownEdit(MarkdownTextEditor.CreateLinkEdit(
            BodyBox.Text,
            BodyBox.SelectionStart,
            BodyBox.SelectionLength));

    private void MarkdownBulletedList_Click(object sender, RoutedEventArgs e) =>
        ApplyMarkdownEdit(MarkdownTextEditor.CreateListEdit(
            BodyBox.Text,
            BodyBox.SelectionStart,
            BodyBox.SelectionLength,
            MarkdownListKind.Bulleted));

    private void MarkdownNumberedList_Click(object sender, RoutedEventArgs e) =>
        ApplyMarkdownEdit(MarkdownTextEditor.CreateListEdit(
            BodyBox.Text,
            BodyBox.SelectionStart,
            BodyBox.SelectionLength,
            MarkdownListKind.Numbered));

    private void MarkdownCheckboxList_Click(object sender, RoutedEventArgs e) =>
        ApplyMarkdownEdit(MarkdownTextEditor.CreateListEdit(
            BodyBox.Text,
            BodyBox.SelectionStart,
            BodyBox.SelectionLength,
            MarkdownListKind.Checkbox));

    private void MarkdownIndent_Click(object sender, RoutedEventArgs e) =>
        ApplyMarkdownEdit(MarkdownTextEditor.CreateIndentEdit(
            BodyBox.Text,
            BodyBox.SelectionStart,
            BodyBox.SelectionLength));

    private void MarkdownOutdent_Click(object sender, RoutedEventArgs e)
    {
        if (MarkdownTextEditor.TryCreateOutdentEdit(
                BodyBox.Text,
                BodyBox.SelectionStart,
                BodyBox.SelectionLength,
                out var edit))
        {
            ApplyMarkdownEdit(edit);
        }
        else
        {
            BodyBox.Focus(FocusState.Programmatic);
        }
    }

    private void ApplyMarkdownEdit(MarkdownTextEdit edit)
    {
        BodyBox.SelectionStart = edit.Start;
        BodyBox.SelectionLength = edit.Length;
        BodyBox.SelectedText = edit.Replacement;
        BodyBox.SelectionStart = edit.Start + edit.CaretOffset;
        BodyBox.SelectionLength = edit.SelectionLength;
        BodyBox.Focus(FocusState.Programmatic);
    }

    private void UpdateIssueReferenceSuggestionsForCaret(TextBox textBox)
    {
        if (_suppressIssueReferenceSuggestions)
        {
            return;
        }

        if (textBox.SelectionLength != 0 ||
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
        UpdateIssueReferenceSuggestionsPosition();
    }

    private void UpdateIssueReferenceSuggestionsPosition()
    {
        if (!ViewModel.HasIssueReferenceSuggestions || BodyBox.Text.Length == 0)
        {
            return;
        }

        var caretIndex = Math.Clamp(BodyBox.SelectionStart, 0, BodyBox.Text.Length);
        var characterIndex = Math.Min(caretIndex, BodyBox.Text.Length - 1);
        var caretRect = BodyBox.GetRectFromCharacterIndex(
            characterIndex,
            caretIndex == BodyBox.Text.Length);
        if (caretRect.IsEmpty)
        {
            return;
        }

        var caretPosition = BodyBox.TransformToVisual(RootGrid)
            .TransformPoint(new Point(caretRect.X, caretRect.Y + caretRect.Height));
        var margin = RootGrid.Margin;
        var popupContent = IssueReferenceSuggestionsPopup.Child as FrameworkElement;
        var popupWidth = popupContent?.Width ?? 0;
        var popupHeight = popupContent is { ActualHeight: > 0 }
            ? popupContent.ActualHeight
            : popupContent?.MaxHeight ?? 144;
        var horizontalOffset = margin.Left + caretPosition.X;
        if (popupWidth > 0)
        {
            horizontalOffset = Math.Clamp(
                horizontalOffset,
                margin.Left,
                Math.Max(margin.Left, margin.Left + RootGrid.ActualWidth - popupWidth));
        }

        var verticalOffset = margin.Top + caretPosition.Y + 4;
        var windowBottom = margin.Top + RootGrid.ActualHeight;
        if (popupHeight > 0 && verticalOffset + popupHeight > windowBottom)
        {
            verticalOffset = Math.Max(
                margin.Top,
                margin.Top + caretPosition.Y - popupHeight - caretRect.Height - 4);
        }

        IssueReferenceSuggestionsPopup.HorizontalOffset = horizontalOffset;
        IssueReferenceSuggestionsPopup.VerticalOffset = verticalOffset;
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
                case VirtualKey.Enter:
                {
                    var suggestion = IssueReferenceSuggestionsList.SelectedItem as IssueListItem;
                    if (suggestion is null && ViewModel.IssueReferenceSuggestions.Count > 0)
                    {
                        var selectedIndex = Math.Clamp(
                            IssueReferenceSuggestionsList.SelectedIndex,
                            0,
                            ViewModel.IssueReferenceSuggestions.Count - 1);
                        suggestion = ViewModel.IssueReferenceSuggestions[selectedIndex];
                    }

                    if (suggestion is not null)
                    {
                        ApplyIssueReferenceSuggestion(suggestion);
                        e.Handled = true;
                        return;
                    }

                    break;
                }
                case VirtualKey.Escape:
                    ViewModel.ClearIssueReferenceSuggestions();
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == VirtualKey.Enter &&
            sender is TextBox bodyTextBox &&
            MarkdownTextEditor.TryCreateEnterEdit(
                bodyTextBox.Text,
                bodyTextBox.SelectionStart,
                bodyTextBox.SelectionLength,
                out var enterEdit))
        {
            ApplyMarkdownEdit(enterEdit);
            e.Handled = true;
            return;
        }

        if (e.Key != VirtualKey.Tab || sender is not TextBox textBox)
        {
            return;
        }

        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            if (MarkdownTextEditor.TryCreateOutdentEdit(
                    textBox.Text,
                    textBox.SelectionStart,
                    textBox.SelectionLength,
                    out var outdentEdit))
            {
                ApplyMarkdownEdit(outdentEdit);
            }

            return;
        }

        if (textBox.SelectionLength > 0)
        {
            ApplyMarkdownEdit(MarkdownTextEditor.CreateIndentEdit(
                textBox.Text,
                textBox.SelectionStart,
                textBox.SelectionLength));
            e.Handled = true;
            return;
        }

        ApplyMarkdownEdit(MarkdownTextEditor.CreateTabEdit(
            textBox.Text,
            textBox.SelectionStart));
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
