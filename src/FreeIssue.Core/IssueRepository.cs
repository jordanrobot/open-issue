using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace FreeIssue.Core;

public sealed class IssueRepository(string projectRoot)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _issuesDirectory = ProjectLocator.GetIssuesDirectory(projectRoot);

    public string ProjectRoot { get; } = Path.GetFullPath(projectRoot);

    public IssueListResult List()
    {
        var issues = new List<Issue>();
        var diagnostics = new List<IssueDiagnostic>();
        if (!Directory.Exists(_issuesDirectory))
        {
            return new IssueListResult(issues, diagnostics);
        }

        ProjectLocator.RejectLink(_issuesDirectory);
        foreach (var path in Directory.EnumerateFiles(_issuesDirectory, "*.md", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                ProjectLocator.RejectLink(path);
                var issue = ReadPath(path);
                if (Path.GetFileNameWithoutExtension(path) != issue.Id)
                {
                    throw new IssueException("The filename must match the lowercase issue ID.");
                }

                issues.Add(issue);
            }
            catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new IssueDiagnostic(path, exception.Message));
            }
        }

        return new IssueListResult(issues, diagnostics);
    }

    public Issue Read(string id)
    {
        var path = GetIssuePath(id);
        if (!File.Exists(path))
        {
            throw new IssueException($"Issue '{id}' was not found.");
        }

        ProjectLocator.RejectLink(path);
        var issue = ReadPath(path);
        if (!string.Equals(issue.Id, id, StringComparison.Ordinal))
        {
            throw new IssueException($"Issue file '{path}' does not contain the expected ID '{id}'.");
        }

        return issue;
    }

    public Issue Create(Issue issue)
    {
        ValidateId(issue.Id);
        ValidateIssue(issue);
        ValidateReferences(issue, []);
        var path = GetIssuePath(issue.Id);
        using (AcquireLock(issue.Id))
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                throw new IssueException($"Issue file already exists: '{path}'.");
            }

            WriteAtomically(path, Serialize(issue), overwrite: false);
        }

        return Read(issue.Id);
    }

    public Issue Update(Issue issue, string expectedRevision)
    {
        ValidateId(issue.Id);
        ValidateIssue(issue);
        var path = GetIssuePath(issue.Id);
        using (AcquireLock(issue.Id))
        {
            if (!File.Exists(path))
            {
                throw new IssueException($"Issue '{issue.Id}' was not found.");
            }

            ProjectLocator.RejectLink(path);
            var current = ReadPath(path);
            if (!string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
            {
                throw new IssueException($"Issue '{issue.Id}' changed since it was read. Reload it and retry.");
            }

            var all = List().Issues.Where(existing => existing.Id != issue.Id).ToList();
            ValidateReferences(issue, all);
            issue.Frontmatter = current.Frontmatter;
            issue.LineEnding = current.LineEnding;
            WriteAtomically(path, Serialize(issue), overwrite: true);
        }

        return Read(issue.Id);
    }

    public string GetIssuePath(string id)
    {
        ValidateId(id);
        return Path.Combine(_issuesDirectory, id + ".md");
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

        var firstNewline = raw.IndexOf('\n');
        if (!raw.StartsWith("---", StringComparison.Ordinal) || firstNewline < 0)
        {
            throw new IssueException("Expected YAML frontmatter beginning with '---'.");
        }

        var lineEnding = firstNewline > 0 && raw[firstNewline - 1] == '\r' ? "\r\n" : "\n";
        var contentStart = firstNewline + 1;
        var closeStart = -1;
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
                closeStart = cursor;
                var bodyStart = end < raw.Length ? end + 1 : end;
                var frontmatterText = raw[contentStart..(cursor > 0 && raw[cursor - 1] == '\r' ? cursor - 1 : cursor)];
                var body = raw[bodyStart..];
                return ParseFrontmatter(frontmatterText, body, lineEnding, raw);
            }

            if (end == raw.Length)
            {
                break;
            }

            cursor = end + 1;
        }

        _ = closeStart;
        throw new IssueException("Expected a closing '---' after YAML frontmatter.");
    }

    private static Issue ParseFrontmatter(string text, string body, string lineEnding, string raw)
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
            if (schema != 1)
            {
                throw new IssueException($"Unsupported issue schema version {schema}; this program supports version 1.");
            }

            var id = ReadString(values, "id");
            ValidateId(id);
            var title = ReadString(values, "title");
            var status = ParseEnum<IssueStatus>(ReadString(values, "status"), "status");
            var priority = ParseEnum<IssuePriority>(ReadString(values, "priority"), "priority");
            var ready = ReadBool(values, "ready");
            var created = ReadDate(values, "created_at");
            var updated = ReadDate(values, "updated_at");
            var tags = ReadStringList(values, "tags");
            var related = ReadOptionalStringList(values, "related");
            var blocks = ReadOptionalStringList(values, "blocks");
            var parent = ReadOptionalString(values, "parent");
            if (parent is not null)
            {
                ValidateId(parent);
            }

            foreach (var reference in related.Concat(blocks))
            {
                ValidateId(reference);
            }

            var issue = new Issue
            {
                Id = id,
                Title = title,
                Status = status,
                Priority = priority,
                Ready = ready,
                CreatedAt = created,
                UpdatedAt = updated,
                Tags = tags,
                Parent = parent,
                Related = related,
                Blocks = blocks,
                Body = body,
                Revision = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(raw))).ToLowerInvariant(),
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
        Put(map, "schema", new YamlScalarNode("1"));
        Put(map, "id", new YamlScalarNode(issue.Id));
        Put(map, "title", new YamlScalarNode(issue.Title));
        Put(map, "status", new YamlScalarNode(issue.Status.ToString().ToLowerInvariant()));
        Put(map, "ready", new YamlScalarNode(issue.Ready ? "true" : "false"));
        Put(map, "priority", new YamlScalarNode(issue.Priority.ToString().ToLowerInvariant()));
        Put(map, "tags", new YamlSequenceNode(issue.Tags.Select(tag => new YamlScalarNode(tag))));
        Put(map, "created_at", new YamlScalarNode(issue.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        Put(map, "updated_at", new YamlScalarNode(issue.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        Put(map, "parent", issue.Parent is null ? new YamlScalarNode("null") { Style = ScalarStyle.Plain } : new YamlScalarNode(issue.Parent));
        Put(map, "related", new YamlSequenceNode(issue.Related.Select(id => new YamlScalarNode(id))));
        Put(map, "blocks", new YamlSequenceNode(issue.Blocks.Select(id => new YamlScalarNode(id))));

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(map)).Save(writer, assignAnchors: false);
        var metadata = writer.ToString().TrimEnd('\r', '\n');
        return "---" + issue.LineEnding + metadata.Replace("\n", issue.LineEnding, StringComparison.Ordinal) +
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

    private static string ReadString(IReadOnlyDictionary<string, YamlNode> values, string key)
    {
        if (!values.TryGetValue(key, out var node) || node is not YamlScalarNode scalar || scalar.Value is null)
        {
            throw new IssueException($"Frontmatter field '{key}' must be a string.");
        }

        return scalar.Value;
    }

    private static string? ReadOptionalString(IReadOnlyDictionary<string, YamlNode> values, string key)
    {
        if (!values.TryGetValue(key, out var node) || node is YamlScalarNode { Value: null or "null" or "~" })
        {
            return null;
        }

        return ReadString(values, key);
    }

    private static int ReadInt(IReadOnlyDictionary<string, YamlNode> values, string key) =>
        int.TryParse(ReadString(values, key), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new IssueException($"Frontmatter field '{key}' must be an integer.");

    private static bool ReadBool(IReadOnlyDictionary<string, YamlNode> values, string key) =>
        bool.TryParse(ReadString(values, key), out var value)
            ? value
            : throw new IssueException($"Frontmatter field '{key}' must be true or false.");

    private static DateTimeOffset ReadDate(IReadOnlyDictionary<string, YamlNode> values, string key) =>
        DateTimeOffset.TryParse(ReadString(values, key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value.ToUniversalTime()
            : throw new IssueException($"Frontmatter field '{key}' must be an ISO 8601 timestamp.");

    private static List<string> ReadStringList(IReadOnlyDictionary<string, YamlNode> values, string key) =>
        !values.TryGetValue(key, out var node) || node is not YamlSequenceNode sequence
            ? throw new IssueException($"Frontmatter field '{key}' must be a list.")
            : sequence.Children.Select(child => child is YamlScalarNode { Value: not null } scalar
                ? scalar.Value
                : throw new IssueException($"Every '{key}' item must be a string.")).ToList();

    private static List<string> ReadOptionalStringList(IReadOnlyDictionary<string, YamlNode> values, string key) =>
        values.ContainsKey(key) ? ReadStringList(values, key) : [];

    private static T ParseEnum<T>(string value, string key) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new IssueException($"Frontmatter field '{key}' has unsupported value '{value}'.");

    private static void ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed) ||
            !string.Equals(parsed.ToString("D"), id, StringComparison.Ordinal))
        {
            throw new IssueException($"Issue IDs must be lowercase UUIDs in canonical form: '{id}'.");
        }
    }

    private static void ValidateIssue(Issue issue)
    {
        if (string.IsNullOrWhiteSpace(issue.Title))
        {
            throw new IssueException("Issue title cannot be empty.");
        }

        if (issue.Title.Contains('\r') || issue.Title.Contains('\n'))
        {
            throw new IssueException("Issue title must be a single line.");
        }

        if (issue.Ready && (issue.Status != IssueStatus.Open || !HasAcceptanceCriteria(issue.Body)))
        {
            throw new IssueException("A ready issue must be open and have nonempty Acceptance criteria.");
        }

        if (issue.Tags.Any(string.IsNullOrWhiteSpace) || issue.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != issue.Tags.Count)
        {
            throw new IssueException("Tags must be nonempty and unique (ignoring case).");
        }

        if (issue.Parent == issue.Id || issue.Related.Contains(issue.Id, StringComparer.Ordinal) ||
            issue.Blocks.Contains(issue.Id, StringComparer.Ordinal))
        {
            throw new IssueException("An issue cannot reference itself.");
        }

        if (issue.Related.Distinct(StringComparer.Ordinal).Count() != issue.Related.Count ||
            issue.Blocks.Distinct(StringComparer.Ordinal).Count() != issue.Blocks.Count)
        {
            throw new IssueException("Relationship IDs must not contain duplicates.");
        }
    }

    private static bool HasAcceptanceCriteria(string body)
    {
        var lines = body.Split('\n');
        var inSection = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^#{1,6}\s*Acceptance criteria\s*#*\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                inSection = true;
                continue;
            }

            if (inSection && System.Text.RegularExpressions.Regex.IsMatch(line, @"^#{1,6}\s+"))
            {
                return false;
            }

            if (inSection && !string.IsNullOrWhiteSpace(line))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateReferences(Issue issue, IReadOnlyCollection<Issue> existing)
    {
        var byId = existing.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var reference in issue.Related.Concat(issue.Blocks).Concat(issue.Parent is null ? [] : [issue.Parent]))
        {
            ValidateId(reference);
            if (reference != issue.Id && !byId.ContainsKey(reference))
            {
                throw new IssueException($"Referenced issue '{reference}' does not exist.");
            }
        }

        var all = existing.Append(issue).ToDictionary(item => item.Id, StringComparer.Ordinal);
        EnsureAcyclic(all, item => item.Parent is null ? [] : [item.Parent], "parent");
        EnsureAcyclic(all, item => item.Blocks, "blocking");
    }

    private static void EnsureAcyclic(
        IReadOnlyDictionary<string, Issue> issues,
        Func<Issue, IEnumerable<string>> next,
        string relationship)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string id)
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

    private FileStream AcquireLock(string id)
    {
        var lockPath = Path.Combine(_issuesDirectory, $".{id}.lock");
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

    private static void WriteAtomically(string path, string content, bool overwrite)
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
