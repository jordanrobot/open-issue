using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace OpenIssue.Core;

public sealed class IssueRepository(string projectRoot)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _openIssuesDirectory = ProjectLocator.GetOpenIssuesDirectory(projectRoot);
    private readonly string _closedIssuesDirectory = ProjectLocator.GetClosedIssuesDirectory(projectRoot);

    public string ProjectRoot { get; } = Path.GetFullPath(projectRoot);

    public IssueListResult List()
    {
        var issuesById = new Dictionary<long, (Issue Issue, string Path)>();
        var ambiguousIds = new HashSet<long>();
        var diagnostics = new List<IssueDiagnostic>();
        EnsureSafeDirectories();
        foreach (var directory in GetIssueDirectories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            ProjectLocator.RejectLink(directory);
            foreach (var path in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                try
                {
                    ProjectLocator.RejectLink(path);
                    var issue = ReadPath(path);
                    var fileIdText = Path.GetFileNameWithoutExtension(path);
                    if (!long.TryParse(fileIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var fileId) ||
                        fileId != issue.Id ||
                        !string.Equals(fileIdText, fileId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                    {
                        throw new IssueException("The filename must be the canonical numeric issue ID.");
                    }

                    if (ambiguousIds.Contains(issue.Id))
                    {
                        diagnostics.Add(new IssueDiagnostic(path, $"Issue '{issue.Id}' exists in both status directories."));
                        continue;
                    }

                    if (issuesById.Remove(issue.Id, out var duplicate))
                    {
                        ambiguousIds.Add(issue.Id);
                        diagnostics.Add(new IssueDiagnostic(
                            duplicate.Path,
                            $"Issue '{issue.Id}' exists in both status directories."));
                        diagnostics.Add(new IssueDiagnostic(
                            path,
                            $"Issue '{issue.Id}' exists in both status directories."));
                        continue;
                    }

                    issuesById.Add(issue.Id, (issue, path));
                }
                catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(new IssueDiagnostic(path, exception.Message));
                }
            }
        }

        var issues = issuesById.Values.Select(entry => entry.Issue).ToList();
        var knownIds = issues.Select(issue => issue.Id).ToHashSet();
        foreach (var issue in issues)
        {
            foreach (var reference in References(issue))
            {
                if (!knownIds.Contains(reference))
                {
                    diagnostics.Add(new IssueDiagnostic(GetIssuePath(issue.Id),
                        $"Referenced issue '{reference}' does not exist."));
                }
            }
        }

        var byId = issues.ToDictionary(issue => issue.Id);
        foreach (var cycleId in FindCycleIds(byId, issue =>
                     issue.Children.Concat(
                         byId.Values.Where(candidate => candidate.Parents.Contains(issue.Id)).Select(candidate => candidate.Id))
                         .Distinct()))
        {
            diagnostics.Add(new IssueDiagnostic(GetIssuePath(cycleId), "Parent/child relationships contain a cycle."));
        }

        foreach (var cycleId in FindCycleIds(byId, issue => issue.Blocks))
        {
            diagnostics.Add(new IssueDiagnostic(GetIssuePath(cycleId), "Blocking relationships contain a cycle."));
        }

        return new IssueListResult(issues, diagnostics);
    }

    public Issue Read(long id)
    {
        var path = FindIssuePath(id) ?? throw new IssueException($"Issue '{id}' was not found.");

        ProjectLocator.RejectLink(path);
        var issue = ReadPath(path);
        if (issue.Id != id)
        {
            throw new IssueException($"Issue file '{path}' does not contain the expected ID '{id}'.");
        }

        return issue;
    }

    public Issue Create(Issue issue)
    {
        EnsureSafeDirectories();
        using (AcquireAllocationLock())
        {
            issue.Id = GetNextId();
            ValidateIssue(issue);
            using var relationshipLock = References(issue).Any() ? AcquireRelationshipLock() : null;
            ValidateReferences(issue, List().Issues);
            var path = GetIssuePath(issue.Id, issue.Status);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (AcquireLock(issue.Id.ToString(CultureInfo.InvariantCulture)))
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    throw new IssueException($"Issue file already exists: '{path}'.");
                }

                WriteAtomically(path, Serialize(issue), overwrite: false);
            }
        }

        return Read(issue.Id);
    }

    public Issue Update(Issue issue, string expectedRevision)
    {
        ValidateId(issue.Id);
        ValidateIssue(issue);
        EnsureSafeDirectories();
        var snapshot = Read(issue.Id);
        var relationshipsChanged = !snapshot.Parents.SequenceEqual(issue.Parents) ||
                                   !snapshot.Children.SequenceEqual(issue.Children) ||
                                   !snapshot.Related.SequenceEqual(issue.Related) ||
                                   !snapshot.Blocks.SequenceEqual(issue.Blocks) ||
                                   !snapshot.Dependencies.SequenceEqual(issue.Dependencies);
        using var relationshipLock = relationshipsChanged ? AcquireRelationshipLock() : null;
        using (AcquireLock(issue.Id.ToString(CultureInfo.InvariantCulture)))
        {
            var path = FindIssuePath(issue.Id) ?? throw new IssueException($"Issue '{issue.Id}' was not found.");
            ProjectLocator.RejectLink(path);
            var current = ReadPath(path);
            if (!string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
            {
                throw new IssueConflictException($"Issue '{issue.Id}' changed since it was read. Reload it and retry.");
            }

            if (relationshipsChanged)
            {
                var all = List().Issues.Where(existing => existing.Id != issue.Id).ToList();
                ValidateReferences(issue, all);
            }

            issue.CreatedBy = current.CreatedBy;
            issue.CreatedAt = current.CreatedAt;
            issue.UpdatedAt = DateTimeOffset.UtcNow;
            issue.Frontmatter = current.Frontmatter;
            issue.LineEnding = current.LineEnding;
            var content = Serialize(issue);
            if (!string.Equals(ReadPath(path).Revision, expectedRevision, StringComparison.Ordinal))
            {
                throw new IssueConflictException($"Issue '{issue.Id}' changed since it was read while the update was being prepared. Reload it and retry.");
            }

            var targetPath = GetIssuePath(issue.Id, issue.Status);
            if (PathsEqual(path, targetPath))
            {
                WriteAtomically(path, content, overwrite: true, expectedRevision);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                ProjectLocator.RejectLink(targetPath);
                if (File.Exists(targetPath) || Directory.Exists(targetPath))
                {
                    throw new IssueConflictException(
                        $"Issue '{issue.Id}' already exists in the target status directory.");
                }

                File.Move(path, targetPath);
                WriteAtomically(targetPath, content, overwrite: true, expectedRevision);
            }
        }

        return Read(issue.Id);
    }

    /// <summary>
    /// Permanently deletes an issue and removes references to it from other issues.
    /// </summary>
    /// <exception cref="IssueException">The issue is missing or invalid.</exception>
    public void Delete(long id)
    {
        ValidateId(id);
        EnsureSafeDirectories();
        using (AcquireAllocationLock())
        using (AcquireRelationshipLock())
        using (AcquireLock(id.ToString(CultureInfo.InvariantCulture)))
        {
            var path = FindIssuePath(id) ?? throw new IssueException($"Issue '{id}' was not found.");
            ProjectLocator.RejectLink(path);

            var issue = ReadPath(path);
            if (issue.Id != id)
            {
                throw new IssueException($"Issue file '{path}' does not contain the expected ID '{id}'.");
            }

            var referencingIds = List().Issues
                .Where(candidate => candidate.Id != id && References(candidate).Contains(id))
                .Select(candidate => candidate.Id)
                .Order()
                .ToArray();
            foreach (var referencingId in referencingIds)
            {
                var referencingPath = GetIssuePath(referencingId);
                using (AcquireLock(referencingId.ToString(CultureInfo.InvariantCulture)))
                {
                    ProjectLocator.RejectLink(referencingPath);
                    var referencingIssue = ReadPath(referencingPath);
                    if (referencingIssue.Id != referencingId)
                    {
                        throw new IssueException(
                            $"Issue file '{referencingPath}' does not contain the expected ID '{referencingId}'.");
                    }

                    if (!References(referencingIssue).Contains(id))
                    {
                        continue;
                    }

                    referencingIssue.Parents.RemoveAll(reference => reference == id);
                    referencingIssue.Children.RemoveAll(reference => reference == id);
                    referencingIssue.Related.RemoveAll(reference => reference == id);
                    referencingIssue.Blocks.RemoveAll(reference => reference == id);
                    referencingIssue.Body = IssueBodyLinkParser.RemoveDependency(referencingIssue.Body, id);
                    referencingIssue.UpdatedAt = DateTimeOffset.UtcNow;
                    WriteAtomically(
                        referencingPath,
                        Serialize(referencingIssue),
                        overwrite: true,
                        referencingIssue.Revision);
                }
            }

            AdvanceIdHighWaterMark(id);
            File.Delete(path);
        }
    }

    public string GetIssuePath(long id)
    {
        ValidateId(id);
        return FindIssuePath(id) ?? GetIssuePath(id, IssueStatus.Open);
    }

    private string? FindIssuePath(long id)
    {
        ValidateId(id);
        EnsureSafeDirectories();
        string? foundPath = null;
        foreach (var directory in GetIssueDirectories())
        {
            var path = Path.Combine(directory, id.ToString(CultureInfo.InvariantCulture) + ".md");
            ProjectLocator.RejectLink(path);
            if (Directory.Exists(path))
            {
                throw new IssueException($"Issue path '{path}' is a directory, not a file.");
            }

            if (!File.Exists(path))
            {
                continue;
            }

            if (foundPath is not null)
            {
                throw new IssueException($"Issue '{id}' exists in both status directories.");
            }

            foundPath = path;
        }

        return foundPath;
    }

    private string GetIssuePath(long id, IssueStatus status)
    {
        ValidateId(id);
        EnsureSafeDirectories();
        return Path.Combine(GetIssueDirectory(status), id.ToString(CultureInfo.InvariantCulture) + ".md");
    }

    private string GetIssueDirectory(IssueStatus status) =>
        status switch
        {
            IssueStatus.Open => _openIssuesDirectory,
            IssueStatus.Closed or IssueStatus.Declined => _closedIssuesDirectory,
            _ => throw new IssueException($"Unsupported issue status '{status}'.")
        };

    private string[] GetIssueDirectories() => [_openIssuesDirectory, _closedIssuesDirectory];

    private IssueStatus GetDirectoryStatus(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
                        ?? throw new IssueException($"Issue path '{path}' has no parent directory.");
        if (PathsEqual(directory, _openIssuesDirectory))
        {
            return IssueStatus.Open;
        }

        if (PathsEqual(directory, _closedIssuesDirectory))
        {
            return IssueStatus.Closed;
        }

        throw new IssueException($"Issue file '{path}' is outside the open and closed directories.");
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private string GetDataDirectory()
    {
        var dataDirectory = Path.Combine(ProjectRoot, ProjectLocator.DataDirectoryName);
        ProjectLocator.RejectLink(dataDirectory);
        return dataDirectory;
    }

    private long GetNextId()
    {
        var maxId = 0L;
        foreach (var directory in GetIssueDirectories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            ProjectLocator.RejectLink(directory);
            foreach (var path in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
            {
                ProjectLocator.RejectLink(path);
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.Length > 0 && name.All(char.IsDigit))
                {
                    if (!long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    {
                        throw new IssueException($"Numeric issue filename '{name}.md' exceeds the supported ID range.");
                    }

                    maxId = Math.Max(maxId, id);
                }
            }
        }

        if (maxId == long.MaxValue)
        {
            throw new IssueException("The numeric issue ID space is exhausted.");
        }

        var counterPath = GetIdCounterPath();
        var reservedId = ReadReservedId(counterPath);
        var nextId = Math.Max(reservedId, maxId + 1);
        if (nextId < 1 || nextId == long.MaxValue)
        {
            throw new IssueException("The numeric issue ID space is exhausted.");
        }

        WriteAtomically(counterPath, (nextId + 1).ToString(CultureInfo.InvariantCulture) + Environment.NewLine,
            overwrite: true);
        return nextId;
    }

    private void AdvanceIdHighWaterMark(long deletedId)
    {
        var nextId = deletedId == long.MaxValue ? long.MaxValue : deletedId + 1;
        var counterPath = GetIdCounterPath();
        if (ReadReservedId(counterPath) < nextId)
        {
            WriteAtomically(counterPath, nextId.ToString(CultureInfo.InvariantCulture) + Environment.NewLine,
                overwrite: true);
        }
    }

    private string GetIdCounterPath() =>
        Path.Combine(ProjectRoot, ProjectLocator.DataDirectoryName, "next-id");

    private static long ReadReservedId(string counterPath)
    {
        ProjectLocator.RejectLink(counterPath);
        long reservedId = 1;
        if (File.Exists(counterPath) &&
            !long.TryParse(File.ReadAllText(counterPath).Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out reservedId))
        {
            throw new IssueException($"Invalid numeric ID counter at '{counterPath}'.");
        }

        return reservedId;
    }

    private void EnsureSafeDirectories()
    {
        if (!PathsEqual(ProjectLocator.GetOpenIssuesDirectory(ProjectRoot), _openIssuesDirectory) ||
            !PathsEqual(ProjectLocator.GetClosedIssuesDirectory(ProjectRoot), _closedIssuesDirectory))
        {
            throw new IssueException("The issue directories changed unexpectedly.");
        }
    }

    private Issue ReadPath(string path)
    {
        string raw;
        try
        {
            raw = File.ReadAllText(path, Utf8);
        }
        catch (DecoderFallbackException exception)
        {
            throw new IssueException($"Issue file is not valid UTF-8: {exception.Message}");
        }

        var revision = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(raw))).ToLowerInvariant();
        if (raw.StartsWith('\uFEFF'))
        {
            raw = raw[1..];
        }

        var firstNewline = raw.IndexOf('\n');
        if (firstNewline < 0 ||
            !raw.AsSpan(0, firstNewline).TrimEnd('\r').SequenceEqual("---"))
        {
            throw new IssueException("Expected YAML frontmatter beginning with '---'.");
        }

        var lineEnding = firstNewline > 0 && raw[firstNewline - 1] == '\r' ? "\r\n" : "\n";
        var contentStart = firstNewline + 1;
        var cursor = contentStart;
        while (cursor <= raw.Length)
        {
            var end = raw.IndexOf('\n', cursor);
            if (end < 0)
            {
                end = raw.Length;
            }

            var lineEnd = end > cursor && raw[end - 1] == '\r' ? end - 1 : end;
            if (raw.AsSpan(cursor, lineEnd - cursor).SequenceEqual("---"))
            {
                var bodyStart = end < raw.Length ? end + 1 : end;
                var frontmatterText = raw[contentStart..(cursor > 0 && raw[cursor - 1] == '\r' ? cursor - 1 : cursor)];
                var body = raw[bodyStart..];
                return ParseFrontmatter(frontmatterText, body, lineEnding, revision, GetDirectoryStatus(path));
            }

            if (end == raw.Length)
            {
                break;
            }

            cursor = end + 1;
        }

        throw new IssueException("Expected a closing '---' after YAML frontmatter.");
    }

    private static Issue ParseFrontmatter(
        string text,
        string body,
        string lineEnding,
        string revision,
        IssueStatus directoryStatus)
    {
        try
        {
            var yaml = new YamlStream();
            yaml.Load(new StringReader(text));
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode map)
            {
                throw new IssueException("YAML frontmatter must be a single mapping.");
            }

            var values = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
            foreach (var pair in map.Children)
            {
                if (pair.Key is not YamlScalarNode key || key.Value is null || !values.TryAdd(key.Value, pair.Value))
                {
                    throw new IssueException("Frontmatter keys must be unique scalar strings.");
                }
            }

            var schema = ReadInt(values, "schema");
            if (schema is not (2 or 3 or 4))
            {
                throw new IssueException($"Unsupported issue schema version {schema}; this program supports versions 2, 3, and 4.");
            }

            var id = ReadLong(values, "id");
            ValidateId(id);
            var title = ReadString(values, "title");
            var metadataStatus = values.ContainsKey("status")
                ? ParseEnum<IssueStatus>(ReadString(values, "status"), "status")
                : (IssueStatus?)null;
            var status = directoryStatus == IssueStatus.Open
                ? IssueStatus.Open
                : metadataStatus == IssueStatus.Declined ? IssueStatus.Declined : IssueStatus.Closed;
            var priority = ParseEnum<IssuePriority>(ReadString(values, "priority"), "priority");
            var draft = schema == 2 ? !ReadBool(values, "ready") : ReadBool(values, "draft");
            var created = ReadDate(values, "created_at");
            var updated = ReadDate(values, "updated_at");
            var createdBy = values.ContainsKey("created_by") ? ReadString(values, "created_by") : string.Empty;
            var tags = ReadStringList(values, "tags");
            var parents = ReadOptionalLongList(values, "parents");
            var children = ReadOptionalLongList(values, "children");
            var related = ReadOptionalLongList(values, "related");
            var blocks = ReadOptionalLongList(values, "blocks");
            foreach (var reference in parents.Concat(children).Concat(related).Concat(blocks))
            {
                ValidateId(reference);
            }

            var issue = new Issue
            {
                Id = id,
                Title = title,
                Status = status,
                Priority = priority,
                Draft = draft,
                CreatedBy = createdBy,
                CreatedAt = created,
                UpdatedAt = updated,
                Tags = tags,
                Parents = parents,
                Children = children,
                Related = related,
                Blocks = blocks,
                Body = body,
                Revision = revision,
                Frontmatter = map,
                LineEnding = lineEnding
            };
            ValidateIssue(issue);
            return issue;
        }
        catch (IssueException)
        {
            throw;
        }
        catch (Exception exception) when (exception is YamlException or FormatException or ArgumentException or InvalidOperationException)
        {
            throw new IssueException($"Malformed issue frontmatter: {exception.Message}");
        }
    }

    private static string Serialize(Issue issue)
    {
        var map = issue.Frontmatter ?? new YamlMappingNode();
        Put(map, "schema", new YamlScalarNode("4"));
        Put(map, "id", new YamlScalarNode(issue.Id.ToString(CultureInfo.InvariantCulture)));
        Put(map, "title", new YamlScalarNode(issue.Title));
        if (issue.Status == IssueStatus.Declined)
        {
            Put(map, "status", new YamlScalarNode("declined"));
        }
        else
        {
            map.Children.Remove(new YamlScalarNode("status"));
        }

        map.Children.Remove(new YamlScalarNode("ready"));
        Put(map, "draft", new YamlScalarNode(issue.Draft ? "true" : "false"));
        Put(map, "priority", new YamlScalarNode(issue.Priority.ToString().ToLowerInvariant()));
        Put(map, "tags", new YamlSequenceNode(issue.Tags.Select(tag => new YamlScalarNode(tag))));
        Put(map, "created_by", new YamlScalarNode(issue.CreatedBy));
        Put(map, "created_at", new YamlScalarNode(issue.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        Put(map, "updated_at", new YamlScalarNode(issue.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        Put(map, "parents", NumberSequence(issue.Parents));
        Put(map, "children", NumberSequence(issue.Children));
        Put(map, "related", NumberSequence(issue.Related));
        Put(map, "blocks", NumberSequence(issue.Blocks));

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(map)).Save(writer, assignAnchors: false);
        var metadata = writer.ToString().TrimEnd('\r', '\n')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", issue.LineEnding, StringComparison.Ordinal);
        return "---" + issue.LineEnding + metadata +
               issue.LineEnding + "---" + issue.LineEnding + issue.Body;
    }

    private static void Put(YamlMappingNode map, string key, YamlNode value)
    {
        var keyNode = new YamlScalarNode(key);
        if (map.Children.ContainsKey(keyNode))
        {
            map.Children[keyNode] = value;
        }
        else
        {
            map.Add(keyNode, value);
        }
    }

    private static YamlSequenceNode NumberSequence(IEnumerable<long> ids) =>
        new(ids.Select(id => new YamlScalarNode(id.ToString(CultureInfo.InvariantCulture))));

    private static string ReadString(IReadOnlyDictionary<string, YamlNode> values, string key)
    {
        if (!values.TryGetValue(key, out var node) || node is not YamlScalarNode scalar || scalar.Value is null)
        {
            throw new IssueException($"Frontmatter field '{key}' must be a string.");
        }

        return scalar.Value;
    }

    private static int ReadInt(IReadOnlyDictionary<string, YamlNode> values, string key) =>
        int.TryParse(ReadString(values, key), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new IssueException($"Frontmatter field '{key}' must be an integer.");

    private static long ReadLong(IReadOnlyDictionary<string, YamlNode> values, string key) =>
        long.TryParse(ReadString(values, key), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new IssueException($"Frontmatter field '{key}' must be an integer.");

    private static bool ReadBool(IReadOnlyDictionary<string, YamlNode> values, string key) =>
        bool.TryParse(ReadString(values, key), out var value)
            ? value
            : throw new IssueException($"Frontmatter field '{key}' must be true or false.");

    private static DateTimeOffset ReadDate(IReadOnlyDictionary<string, YamlNode> values, string key)
    {
        var text = ReadString(values, key);
        if (System.Text.RegularExpressions.Regex.IsMatch(text,
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|\+00:00)$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) &&
            value.Offset == TimeSpan.Zero)
        {
            return value.ToUniversalTime();
        }

        throw new IssueException($"Frontmatter field '{key}' must be a UTC ISO 8601 timestamp.");
    }

    private static List<string> ReadStringList(IReadOnlyDictionary<string, YamlNode> values, string key) =>
        !values.TryGetValue(key, out var node) || node is not YamlSequenceNode sequence
            ? throw new IssueException($"Frontmatter field '{key}' must be a list.")
            : sequence.Children.Select(child => child is YamlScalarNode { Value: not null } scalar
                ? scalar.Value
                : throw new IssueException($"Every '{key}' item must be a string.")).ToList();

    private static List<long> ReadOptionalLongList(IReadOnlyDictionary<string, YamlNode> values, string key) =>
        !values.ContainsKey(key) ? [] :
        values[key] is not YamlSequenceNode sequence
            ? throw new IssueException($"Frontmatter field '{key}' must be a list.")
            : sequence.Children.Select(child =>
                child is YamlScalarNode { Value: not null } scalar &&
                long.TryParse(scalar.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    ? id
                    : throw new IssueException($"Every '{key}' item must be a numeric issue ID.")).ToList();

    private static T ParseEnum<T>(string value, string key) where T : struct, Enum =>
        Enum.GetNames<T>().Any(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) &&
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new IssueException($"Frontmatter field '{key}' has unsupported value '{value}'.");

    private static void ValidateId(long id)
    {
        if (id < 1)
        {
            throw new IssueException($"Issue IDs must be positive integers: '{id}'.");
        }
    }

    private static void ValidateIssue(Issue issue)
    {
        if (!Enum.IsDefined(issue.Status) || !Enum.IsDefined(issue.Priority))
        {
            throw new IssueException("Issue status or priority is invalid.");
        }

        var titleMayBeEmpty = issue.Draft && issue.Title is { Length: 0 };
        if (string.IsNullOrWhiteSpace(issue.Title) && !titleMayBeEmpty)
        {
            throw new IssueException("Issue title cannot be empty.");
        }

        if (issue.Title.Contains('\r') || issue.Title.Contains('\n'))
        {
            throw new IssueException("Issue title must be a single line.");
        }

        if (issue.Tags.Any(string.IsNullOrWhiteSpace) || issue.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != issue.Tags.Count)
        {
            throw new IssueException("Tags must be nonempty and unique (ignoring case).");
        }

        if (issue.Id < 1)
        {
            throw new IssueException("Issue IDs must be positive integers.");
        }

        if (issue.Parents.Contains(issue.Id) || issue.Children.Contains(issue.Id) ||
            issue.Related.Contains(issue.Id) || issue.Blocks.Contains(issue.Id) ||
            issue.Dependencies.Contains(issue.Id))
        {
            throw new IssueException("An issue cannot reference itself.");
        }

        if (issue.Parents.Concat(issue.Children).Concat(issue.Related).Concat(issue.Blocks).Any(id => id < 1))
        {
            throw new IssueException("Every referenced issue ID must be a positive integer.");
        }

        if (issue.Parents.Distinct().Count() != issue.Parents.Count ||
            issue.Children.Distinct().Count() != issue.Children.Count ||
            issue.Related.Distinct().Count() != issue.Related.Count ||
            issue.Blocks.Distinct().Count() != issue.Blocks.Count)
        {
            throw new IssueException("Relationship IDs must not contain duplicates.");
        }
    }

    private static void ValidateReferences(Issue issue, IReadOnlyCollection<Issue> existing)
    {
        var byId = existing.ToDictionary(item => item.Id);
        foreach (var reference in References(issue))
        {
            ValidateId(reference);
            if (reference != issue.Id && !byId.ContainsKey(reference))
            {
                throw new IssueException($"Referenced issue '{reference}' does not exist.");
            }
        }

        var all = existing.Append(issue).ToDictionary(item => item.Id);
        EnsureAcyclic(all, item => item.Children.Concat(
            all.Values.Where(candidate => candidate.Parents.Contains(item.Id)).Select(candidate => candidate.Id))
            .Distinct(), "parent/child");
        EnsureAcyclic(all, item => item.Blocks, "blocking");
    }

    private static void EnsureAcyclic(
        IReadOnlyDictionary<long, Issue> issues,
        Func<Issue, IEnumerable<long>> next,
        string relationship)
    {
        var visiting = new HashSet<long>();
        var visited = new HashSet<long>();
        bool Visit(long id)
        {
            if (visiting.Contains(id))
            {
                return false;
            }

            if (!visited.Add(id))
            {
                return true;
            }

            visiting.Add(id);
            foreach (var neighbor in next(issues[id]))
            {
                if (issues.ContainsKey(neighbor) && !Visit(neighbor))
                {
                    return false;
                }
            }

            visiting.Remove(id);
            return true;
        }

        if (issues.Keys.Any(id => !Visit(id)))
        {
            throw new IssueException($"The {relationship} relationships cannot contain cycles.");
        }
    }

    private static HashSet<long> FindCycleIds(
        IReadOnlyDictionary<long, Issue> issues,
        Func<Issue, IEnumerable<long>> next)
    {
        var cycles = new HashSet<long>();
        var complete = new HashSet<long>();
        var stack = new List<long>();
        void Visit(long id)
        {
            var cycleIndex = stack.IndexOf(id);
            if (cycleIndex >= 0)
            {
                cycles.UnionWith(stack.Skip(cycleIndex));
                return;
            }

            if (!complete.Add(id))
            {
                return;
            }

            stack.Add(id);
            foreach (var neighbor in next(issues[id]).Where(issues.ContainsKey))
            {
                Visit(neighbor);
            }

            stack.RemoveAt(stack.Count - 1);
        }

        foreach (var id in issues.Keys)
        {
            Visit(id);
        }

        return cycles;
    }

    private static IEnumerable<long> References(Issue issue) =>
        issue.Parents.Concat(issue.Children).Concat(issue.Related).Concat(issue.Blocks).Concat(issue.Dependencies);

    private FileStream AcquireLock(string id)
    {
        var lockPath = Path.Combine(GetDataDirectory(), $".{id}.lock");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            ProjectLocator.RejectLink(lockPath);
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new IssueException($"Cannot lock issue '{id}': {exception.Message}");
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new IssueException($"Timed out waiting to write issue '{id}'.");
            }
        }
    }

    private FileStream AcquireAllocationLock() => AcquireLock("allocation");

    private FileStream AcquireRelationshipLock()
    {
        var lockPath = Path.Combine(GetDataDirectory(), ".relationships.lock");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            ProjectLocator.RejectLink(lockPath);
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new IssueException($"Cannot lock issue relationships: {exception.Message}");
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new IssueException("Timed out waiting to update issue relationships.");
            }
        }
    }

    private void WriteAtomically(string path, string content, bool overwrite, string? expectedRevision = null)
    {
        var directory = Path.GetDirectoryName(path)!;
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Utf8.GetBytes(content);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (expectedRevision is not null)
            {
                ProjectLocator.RejectLink(path);
                if (!File.Exists(path) ||
                    !string.Equals(ReadPath(path).Revision, expectedRevision, StringComparison.Ordinal))
                {
                    throw new IssueConflictException("The issue changed since it was read; the update was not written.");
                }
            }

            File.Move(tempPath, path, overwrite);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
