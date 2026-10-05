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
using Microsoft.Windows.ApplicationModel.Resources;
using OpenIssue.Core;
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
    private readonly string? _launchArguments;

    public MainWindowViewModel ViewModel { get; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(nint hwnd);

    public MainWindow(string? launchArguments)
    {
        _launchArguments = launchArguments;
        ViewModel = new MainWindowViewModel();
        InitializeComponent();
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

    private async void IssueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.IsLoadingIssue ||
            ViewModel.IsRefreshing ||
            IssueList.SelectedItem is not Issue issue)
        {
            return;
        }

        var previous = ViewModel.SelectedIssue;
        if (previous is not null &&
            previous.Id != issue.Id &&
            !await ConfirmLeaveCurrentAsync())
        {
            IssueList.SelectedItem = previous;
            return;
        }

        if (!await ViewModel.SelectIssueAsync(issue))
        {
            IssueList.SelectedItem = previous;
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
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

    private void BodyBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
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

        var titleBox = new TextBox
        {
            Header = GetString("NewIssueTitleLabel"),
            PlaceholderText = GetString("TitleBox.PlaceholderText")
        };
        AutomationProperties.SetAutomationId(titleBox, "NewIssueTitle");
        AutomationProperties.SetName(titleBox, GetString("NewIssueTitleLabel"));

        var validationMessage = new TextBlock();
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(titleBox);
        content.Children.Add(validationMessage);
        titleBox.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(titleBox.Text))
            {
                validationMessage.Text = string.Empty;
            }
        };

        var dialog = new ContentDialog
        {
            Title = GetString("DialogNewIssueTitle"),
            Content = content,
            PrimaryButtonText = GetString("ButtonCreate"),
            CloseButtonText = GetString("ButtonCancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        AutomationProperties.SetAutomationId(dialog, "NewIssueDialog");
        AutomationProperties.SetName(dialog, GetString("DialogNewIssueTitle"));
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(titleBox.Text))
            {
                args.Cancel = true;
                validationMessage.Text = GetString("MessageIssueTitleCannotBeEmpty");
                titleBox.Focus(FocusState.Programmatic);
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await ViewModel.CreateIssueCommand.ExecuteAsync(titleBox.Text);
            TitleBox.Focus(FocusState.Programmatic);
            TitleBox.SelectAll();
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
