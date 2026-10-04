using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenIssue.Core;
using Microsoft.Win32;

namespace OpenIssue.Desktop;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Issue> _issues = [];
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<long, string> _knownRevisions = [];
    private IssueService? _service;
    private Issue? _current;
    private FileSystemWatcher? _watcher;
    private bool _loading;
    private bool _refreshing;

    private enum SaveResult
    {
        Saved,
        Reloaded,
        Cancelled,
        Failed
    }

    public MainWindow()
    {
        InitializeComponent();
        IssueList.ItemsSource = _issues;
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshList();
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveCurrent();
        };
        Closing += Window_Closing;
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _saveTimer.Stop();
            _watcher?.Dispose();
        };
        InitializeProject();
    }

    private void InitializeProject()
    {
        try
        {
            var root = ProjectLocator.Find();
            SetProject(root);
        }
        catch (IssueException)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select an Open Issue project's .issues/config.json",
                Filter = "Open Issue configuration (config.json)|config.json",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true)
            {
                Close();
                return;
            }

            try
            {
                var root = Directory.GetParent(Path.GetDirectoryName(dialog.FileName)!)!.FullName;
                ProjectLocator.ReadConfig(dialog.FileName);
                SetProject(root);
            }
            catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, exception.Message, "Cannot open project", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }
    }

    private void SetProject(string root)
    {
        _service = new IssueService(new IssueRepository(root));
        Title = $"Open Issue — {root}";
        var issuesPath = ProjectLocator.GetIssuesDirectory(root);
        Directory.CreateDirectory(issuesPath);
        _watcher = new FileSystemWatcher(issuesPath, "*.md")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += FilesChanged;
        _watcher.Created += FilesChanged;
        _watcher.Deleted += FilesChanged;
        _watcher.Renamed += FilesChanged;
        RefreshList();
    }

    private void FilesChanged(object sender, FileSystemEventArgs args)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (IsKnownRevision(args.FullPath))
            {
                return;
            }

            MessageText.Text = "Issue files changed on disk. Refreshing…";
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    private bool IsKnownRevision(string path)
    {
        if (_service is null ||
            !long.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None,
                CultureInfo.InvariantCulture, out var id) ||
            !_knownRevisions.TryGetValue(id, out var revision))
        {
            return false;
        }

        try
        {
            return string.Equals(
                _service.Resolve(id.ToString(CultureInfo.InvariantCulture)).Revision,
                revision,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool RefreshList(bool preserveCurrentEditor = false)
    {
        if (_service is null || _refreshing || (!preserveCurrentEditor && !ConfirmLeaveCurrent()))
        {
            return false;
        }

        _refreshing = true;
        try
        {
            var query = SearchBox.Text.Trim();
            var status = SelectedFilter(StatusFilter);
            var priority = SelectedFilter(PriorityFilter);
            var filter = new IssueFilter(
                status == "all status" ? null : ParseStatus(status),
                Priority: priority == "all priority" ? null : ParsePriority(priority),
                Draft: ReadyFilter.IsChecked == true ? false : null);
            var listed = _service.List(filter);
            foreach (var issue in listed.Issues)
            {
                _knownRevisions[issue.Id] = issue.Revision;
            }

            var result = string.IsNullOrEmpty(query)
                ? listed
                : new IssueListResult(_service.Search(query, filter), listed.Diagnostics);
            _issues.Clear();
            foreach (var issue in result.Issues)
            {
                _issues.Add(issue);
            }

            var selected = _current is null ? null : _issues.FirstOrDefault(issue => issue.Id == _current.Id);
            IssueList.SelectedItem = selected ?? _issues.FirstOrDefault();
            var selectedIssue = IssueList.SelectedItem as Issue;
            if (preserveCurrentEditor &&
                _current is not null &&
                selectedIssue?.Id == _current.Id &&
                string.Equals(selectedIssue.Revision, _current.Revision, StringComparison.Ordinal))
            {
                _current = selectedIssue;
            }
            else
            {
                LoadIssue(selectedIssue);
            }

            MessageText.Text = result.Diagnostics.Count == 0
                ? $"Loaded {_issues.Count} issue(s)."
                : string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Path}: {diagnostic.Message}"));
            return true;
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            MessageText.Text = exception.Message;
            return false;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private bool ConfirmLeaveCurrent()
    {
        _saveTimer.Stop();
        if (_current is null || !IsDirty())
        {
            return true;
        }

        var saveResult = SaveCurrent(refreshIssueList: false);
        if (saveResult is SaveResult.Saved or SaveResult.Reloaded)
        {
            return true;
        }

        if (saveResult == SaveResult.Cancelled)
        {
            return false;
        }

        var answer = MessageBox.Show(this,
            $"{MessageText.Text}{Environment.NewLine}{Environment.NewLine}Discard unsaved changes and continue?",
            "Autosave failed", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }

        return ReloadCurrentIssue();
    }

    private bool IsDirty() =>
        _current is not null &&
        (TitleBox.Text != _current.Title ||
         BodyBox.Text != _current.Body ||
         !EditorTags().SequenceEqual(_current.Tags, StringComparer.Ordinal) ||
         Selected(StatusBox) != _current.Status.ToString().ToLowerInvariant() ||
         Selected(PriorityBox) != _current.Priority.ToString().ToLowerInvariant() ||
         DraftBox.IsChecked != _current.Draft);

    private SaveResult SaveCurrent(bool refreshIssueList = true)
    {
        _saveTimer.Stop();
        var service = _service;
        var current = _current;
        if (service is null || current is null)
        {
            return SaveResult.Failed;
        }

        if (!IsDirty())
        {
            MessageText.Text = "All changes are saved.";
            return SaveResult.Saved;
        }

        var issueId = current.Id.ToString(CultureInfo.InvariantCulture);
        var expectedRevision = current.Revision;
        var title = TitleBox.Text;
        var body = BodyBox.Text;
        var priority = ParsePriority(Selected(PriorityBox));
        var tags = EditorTags();
        var status = ParseStatus(Selected(StatusBox));
        var draft = DraftBox.IsChecked != false;

        while (true)
        {
            try
            {
                var saved = service.Edit(issueId, title, body, priority, tags,
                    status: status, draft: draft, expectedRevision: expectedRevision);
                TrackSavedIssue(saved);

                if (refreshIssueList)
                {
                    if (RefreshList(preserveCurrentEditor: true))
                    {
                        MessageText.Text = $"Changes saved automatically. {MessageText.Text}";
                    }
                    else
                    {
                        MessageText.Text = $"Changes saved automatically, but the issue list could not be refreshed: {MessageText.Text}";
                    }
                }
                else
                {
                    MessageText.Text = "Changes saved automatically.";
                }

                return SaveResult.Saved;
            }
            catch (IssueConflictException exception)
            {
                Issue latest;
                try
                {
                    latest = service.Resolve(issueId);
                }
                catch (Exception readException) when (readException is IssueException or IOException or UnauthorizedAccessException)
                {
                    MessageText.Text = $"Autosave failed: {exception.Message} Latest version could not be loaded: {readException.Message}";
                    return SaveResult.Failed;
                }

                if (string.Equals(latest.Revision, expectedRevision, StringComparison.Ordinal))
                {
                    MessageText.Text = $"Autosave failed: {exception.Message}";
                    return SaveResult.Failed;
                }

                var answer = MessageBox.Show(this,
                    $"Issue {latest.Id} was changed by another editor while you have unsaved changes.{Environment.NewLine}{Environment.NewLine}" +
                    "Yes: overwrite the newer version with your edits. Newer title, body, status, priority, tags, or draft changes may be lost." +
                    $"{Environment.NewLine}No: discard your edits and load the newer version." +
                    $"{Environment.NewLine}Cancel: keep editing without saving.",
                    "Resolve edit conflict", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

                if (answer == MessageBoxResult.Yes)
                {
                    expectedRevision = latest.Revision;
                    _current = latest;
                    _knownRevisions[latest.Id] = latest.Revision;
                    ReplaceIssueInList(latest);
                    continue;
                }

                if (answer == MessageBoxResult.No)
                {
                    ReplaceIssueInList(latest);
                    LoadIssue(latest);
                    if (refreshIssueList)
                    {
                        RefreshList(preserveCurrentEditor: true);
                    }

                    MessageText.Text = "Loaded the newer version; your unsaved changes were discarded.";
                    return SaveResult.Reloaded;
                }

                MessageText.Text = "The issue changed elsewhere. Your edits remain on screen and were not saved.";
                return SaveResult.Cancelled;
            }
            catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
            {
                MessageText.Text = $"Autosave failed: {exception.Message}";
                return SaveResult.Failed;
            }
        }
    }

    private bool ReloadCurrentIssue()
    {
        var service = _service;
        var current = _current;
        if (service is null || current is null)
        {
            return false;
        }

        try
        {
            var latest = service.Resolve(current.Id.ToString(CultureInfo.InvariantCulture));
            ReplaceIssueInList(latest);
            LoadIssue(latest);
            return true;
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            MessageText.Text = $"Cannot reload issue: {exception.Message}";
            return false;
        }
    }

    private void TrackSavedIssue(Issue issue)
    {
        _current = issue;
        _knownRevisions[issue.Id] = issue.Revision;
        ReplaceIssueInList(issue);

        if (DraftBox.IsChecked == issue.Draft)
        {
            return;
        }

        var wasLoading = _loading;
        _loading = true;
        try
        {
            DraftBox.IsChecked = issue.Draft;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void ReplaceIssueInList(Issue issue)
    {
        var existing = _issues.FirstOrDefault(candidate => candidate.Id == issue.Id);
        if (existing is null)
        {
            return;
        }

        var index = _issues.IndexOf(existing);
        var wasSelected = IssueList.SelectedItem is Issue selected && selected.Id == issue.Id;
        var wasLoading = _loading;
        _loading = true;
        try
        {
            _issues[index] = issue;
            if (wasSelected)
            {
                IssueList.SelectedItem = issue;
            }
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void RestoreCurrentSelection()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            IssueList.SelectedItem = _current is null
                ? null
                : _issues.FirstOrDefault(issue => issue.Id == _current.Id);
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private string[] EditorTags() =>
        TagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void LoadIssue(Issue? issue)
    {
        _loading = true;
        try
        {
            _current = issue;
            if (issue is not null)
            {
                _knownRevisions[issue.Id] = issue.Revision;
            }

            TitleBox.Text = issue?.Title ?? "";
            BodyBox.Text = issue?.Body ?? "";
            TagsBox.Text = issue is null ? "" : string.Join(", ", issue.Tags);
            SetSelected(StatusBox, issue?.Status.ToString().ToLowerInvariant());
            SetSelected(PriorityBox, issue?.Priority.ToString().ToLowerInvariant());
            DraftBox.IsChecked = issue?.Draft ?? true;
            SetEditorEnabled(issue is not null);
        }
        finally
        {
            _loading = false;
        }
    }

    private void ClearEditor() => LoadIssue(null);

    private void SetEditorEnabled(bool enabled)
    {
        TitleBox.IsEnabled = enabled;
        BodyBox.IsEnabled = enabled;
        TagsBox.IsEnabled = enabled;
        StatusBox.IsEnabled = enabled;
        PriorityBox.IsEnabled = enabled;
        DraftBox.IsEnabled = enabled;
    }

    private void IssueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _refreshing || IssueList.SelectedItem is not Issue issue)
        {
            return;
        }

        var previous = _current;
        if (previous is not null && previous.Id != issue.Id && !ConfirmLeaveCurrent())
        {
            RestoreCurrentSelection();
            return;
        }

        if (_service is null)
        {
            LoadIssue(issue);
            return;
        }

        try
        {
            var latest = _service.Resolve(issue.Id.ToString(CultureInfo.InvariantCulture));
            ReplaceIssueInList(latest);
            LoadIssue(latest);
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            MessageText.Text = $"Cannot load issue: {exception.Message}";
            RestoreCurrentSelection();
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            RefreshList();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();

    private void Window_Activated(object? sender, EventArgs e) => RefreshList();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmLeaveCurrent())
        {
            e.Cancel = true;
        }
    }

    private void Editor_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _current is null)
        {
            return;
        }

        MessageText.Text = "Saving changes...";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void NewIssue_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || !ConfirmLeaveCurrent())
        {
            return;
        }

        var title = new NewIssueWindow { Owner = this };
        if (title.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var issue = _service.Create(title.IssueTitle, string.Empty);
            RefreshList();
            IssueList.SelectedItem = _issues.FirstOrDefault(item => item.Id == issue.Id);
            TitleBox.Focus();
            TitleBox.SelectAll();
        }
        catch (IssueException exception)
        {
            MessageBox.Show(this, exception.Message, "Cannot create issue", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string SelectedFilter(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

    private static string Selected(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

    private static void SetSelected(ComboBox combo, string? value)
    {
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase));
    }

    private static IssueStatus ParseStatus(string value) =>
        Enum.TryParse<IssueStatus>(value, true, out var parsed) ? parsed : IssueStatus.Open;

    private static IssuePriority ParsePriority(string value) =>
        Enum.TryParse<IssuePriority>(value, true, out var parsed) ? parsed : IssuePriority.Normal;
}
