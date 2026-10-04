using System.Collections.ObjectModel;
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
    private IssueService? _service;
    private Issue? _current;
    private FileSystemWatcher? _watcher;
    private bool _loading;
    private bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();
        IssueList.ItemsSource = _issues;
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshList();
        };
        Closed += (_, _) => _watcher?.Dispose();
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
            MessageText.Text = "Issue files changed on disk. Refreshing…";
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    private void RefreshList()
    {
        if (_service is null || _refreshing || !ConfirmLeaveCurrent())
        {
            return;
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
                Ready: ReadyFilter.IsChecked == true ? true : null);
            var listed = _service.List(filter);
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
            LoadIssue(IssueList.SelectedItem as Issue);

            MessageText.Text = result.Diagnostics.Count == 0
                ? $"Loaded {_issues.Count} issue(s)."
                : string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Path}: {diagnostic.Message}"));
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            MessageText.Text = exception.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private bool ConfirmLeaveCurrent()
    {
        if (_current is null || !IsDirty())
        {
            return true;
        }

        var answer = MessageBox.Show(this, "Save changes before continuing?", "Unsaved changes",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Cancel)
        {
            return false;
        }

        return answer != MessageBoxResult.Yes || SaveCurrent();
    }

    private bool IsDirty() =>
        _current is not null &&
        (TitleBox.Text != _current.Title ||
         BodyBox.Text != _current.Body ||
         TagsBox.Text != string.Join(", ", _current.Tags) ||
         Selected(StatusBox) != _current.Status.ToString().ToLowerInvariant() ||
         Selected(PriorityBox) != _current.Priority.ToString().ToLowerInvariant() ||
         DraftBox.IsChecked != !_current.Ready);

    private bool SaveCurrent()
    {
        if (_service is null || _current is null)
        {
            return false;
        }

        try
        {
            _current = _service.Edit(_current.Id.ToString(), TitleBox.Text, BodyBox.Text,
                ParsePriority(Selected(PriorityBox)),
                TagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                status: ParseStatus(Selected(StatusBox)),
                ready: DraftBox.IsChecked == false);
            LoadIssue(_current);
            MessageText.Text = "Issue saved.";
            return true;
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            MessageText.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "Could not save issue", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void LoadIssue(Issue? issue)
    {
        _loading = true;
        try
        {
            _current = issue;
            TitleBox.Text = issue?.Title ?? "";
            BodyBox.Text = issue?.Body ?? "";
            TagsBox.Text = issue is null ? "" : string.Join(", ", issue.Tags);
            SetSelected(StatusBox, issue?.Status.ToString().ToLowerInvariant());
            SetSelected(PriorityBox, issue?.Priority.ToString().ToLowerInvariant());
            DraftBox.IsChecked = issue is null || !issue.Ready;
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
            _loading = true;
            IssueList.SelectedItem = _issues.FirstOrDefault(item => item.Id == previous.Id);
            _loading = false;
            return;
        }

        LoadIssue(issue);
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SaveCurrent())
        {
            RefreshList();
        }
    }

    private void Ready_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _current is null || !SaveCurrent())
        {
            return;
        }

        try
        {
            _current = _service.SetReady(_current.Id.ToString(), true);
            LoadIssue(_current);
            RefreshList();
        }
        catch (IssueException exception)
        {
            MessageBox.Show(this, exception.Message, "Cannot mark ready", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Draft_Click(object sender, RoutedEventArgs e) => SetStatusOrReady(ready: false);
    private void Close_Click(object sender, RoutedEventArgs e) => SetStatus(IssueStatus.Closed);
    private void Decline_Click(object sender, RoutedEventArgs e) => SetStatus(IssueStatus.Declined);
    private void Reopen_Click(object sender, RoutedEventArgs e) => SetStatus(IssueStatus.Open);

    private void SetStatusOrReady(bool ready)
    {
        if (_service is null || _current is null || !SaveCurrent())
        {
            return;
        }

        try
        {
            _current = _service.SetReady(_current.Id.ToString(), ready);
            LoadIssue(_current);
            RefreshList();
        }
        catch (IssueException exception)
        {
            MessageBox.Show(this, exception.Message, "Cannot update issue", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetStatus(IssueStatus status)
    {
        if (_service is null || _current is null || !SaveCurrent())
        {
            return;
        }

        try
        {
            _current = _service.SetStatus(_current.Id.ToString(), status);
            LoadIssue(_current);
            RefreshList();
        }
        catch (IssueException exception)
        {
            MessageBox.Show(this, exception.Message, "Cannot update issue", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
            var issue = _service.Create(title.IssueTitle, "# Context\n\n# Desired outcome\n\n# Acceptance criteria\n\n");
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
