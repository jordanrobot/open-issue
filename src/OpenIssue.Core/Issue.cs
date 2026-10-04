namespace OpenIssue.Core;

public enum IssueStatus
{
    Open,
    Closed,
    Declined
}

public enum IssuePriority
{
    Low,
    Normal,
    High,
    Urgent
}

public sealed class Issue
{
    public required long Id { get; set; }
    public required string Title { get; set; }
    public IssueStatus Status { get; set; } = IssueStatus.Open;
    public bool Draft { get; set; } = true;
    public IssuePriority Priority { get; set; } = IssuePriority.Normal;
    public List<string> Tags { get; set; } = [];
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public List<long> Parents { get; set; } = [];
    public List<long> Children { get; set; } = [];
    public List<long> Related { get; set; } = [];
    public List<long> Blocks { get; set; } = [];
    public required string Body { get; set; }
    public string Revision { get; internal set; } = "";
    internal YamlDotNet.RepresentationModel.YamlMappingNode? Frontmatter { get; set; }
    internal string LineEnding { get; set; } = "\n";
}

public sealed class IssueException(string message) : Exception(message);

public sealed record IssueDiagnostic(string Path, string Message);

public sealed record IssueListResult(IReadOnlyList<Issue> Issues, IReadOnlyList<IssueDiagnostic> Diagnostics);

public sealed record IssueFilter(
    IssueStatus? Status = null,
    string? Tag = null,
    IssuePriority? Priority = null,
    bool? Draft = null);
