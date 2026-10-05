using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenIssue.Core;

namespace OpenIssue.Desktop;

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
    }

    /// <summary>Gets the latest issue snapshot, including its revision.</summary>
    public Issue Issue { get; private set; }

    /// <summary>Gets the stable numeric identity of the row.</summary>
    public long Id => Issue.Id;

    /// <summary>Gets the title displayed by the row.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; }

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
    }
}
