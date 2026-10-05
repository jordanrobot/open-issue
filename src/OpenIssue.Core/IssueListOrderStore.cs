using System.IO;
using System.Text.Json;

namespace OpenIssue.Core;

/// <summary>Loads and saves the project-specific display order for the desktop issue list.</summary>
public sealed class IssueListOrderStore
{
    private const string FileName = "issue-list-order.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _dataDirectory;
    private readonly string _path;

    /// <summary>Creates a store for an Open Issue project's issue-list order.</summary>
    /// <param name="projectRoot">The root directory of the initialized project.</param>
    public IssueListOrderStore(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        _dataDirectory = Path.Combine(Path.GetFullPath(projectRoot), ProjectLocator.DataDirectoryName);
        _path = Path.Combine(_dataDirectory, FileName);
    }

    /// <summary>Loads the ordered issue IDs, returning an empty list when no order has been saved.</summary>
    /// <returns>Unique positive issue IDs in their saved display order.</returns>
    public IReadOnlyList<long> Load()
    {
        ProjectLocator.RejectLink(_dataDirectory);
        ProjectLocator.RejectLink(_path);
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            var issueIds = JsonSerializer.Deserialize<long[]>(File.ReadAllText(_path), JsonOptions)
                ?? throw new JsonException("The issue list order must be a JSON array.");
            Validate(issueIds);
            return issueIds;
        }
        catch (JsonException exception)
        {
            throw new IssueException($"Invalid issue list order file '{_path}': {exception.Message}");
        }
    }

    /// <summary>Atomically saves a unique ordered list of positive issue IDs.</summary>
    /// <param name="issueIds">The issue IDs in their desired display order.</param>
    public void Save(IReadOnlyList<long> issueIds)
    {
        ArgumentNullException.ThrowIfNull(issueIds);
        var orderedIds = issueIds.ToArray();
        Validate(orderedIds);

        ProjectLocator.RejectLink(_dataDirectory);
        Directory.CreateDirectory(_dataDirectory);
        ProjectLocator.RejectLink(_dataDirectory);
        ProjectLocator.RejectLink(_path);

        var temporaryPath = Path.Combine(_dataDirectory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, orderedIds, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            ProjectLocator.RejectLink(_path);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void Validate(IReadOnlyList<long> issueIds)
    {
        if (issueIds.Any(id => id < 1) || issueIds.Distinct().Count() != issueIds.Count)
        {
            throw new IssueException("Issue list order must contain unique positive issue IDs.");
        }
    }
}
