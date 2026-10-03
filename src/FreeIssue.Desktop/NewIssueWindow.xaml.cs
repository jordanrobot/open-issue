using System.Windows;

namespace FreeIssue.Desktop;

public partial class NewIssueWindow : Window
{
    public string IssueTitle { get; private set; } = "";

    public NewIssueWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => TitleBox.Focus();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            MessageBox.Show(this, "Issue title cannot be empty.", "Invalid title", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IssueTitle = TitleBox.Text;
        DialogResult = true;
    }
}
