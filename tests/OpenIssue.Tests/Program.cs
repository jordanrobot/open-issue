using OpenIssue.Core;

var tests = new (string Name, Action Run)[]
{
    ("project initialization and discovery", ProjectInitialization),
    ("nested projects are isolated", NestedProjectIsolation),
    ("dangling project symlinks cannot fall through to a parent project", DanglingProjectLink),
    ("issue creation, listing, search, and Unicode", IssueOperations),
    ("numeric issue IDs are allocated without collisions", NumericIdAllocation),
    ("readiness and deterministic next selection", ReadinessAndNext),
    ("unknown metadata, body, and line-ending preservation", Preservation),
    ("stale revisions do not overwrite edits", RevisionConflict),
    ("concurrent writers detect conflicts", ConcurrentWriters),
    ("concurrent relationship changes cannot create cycles", ConcurrentRelationshipChanges),
    ("malformed files do not hide valid issues", InvalidFiles),
    ("references are checked", References),
    ("broken references are reported", BrokenReferences),
    ("issue symlinks are rejected", SymlinkIssues),
    ("unsafe IDs are rejected", UnsafeIds)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

return failed == 0 ? 0 : 1;

static void ProjectInitialization()
{
    using var project = new TemporaryProject();
    var child = Directory.CreateDirectory(Path.Combine(project.Root, "src", "nested")).FullName;
    Equal(project.Root, ProjectLocator.Find(child));
    Throws<IssueException>(() => ProjectLocator.Initialize(project.Root));
    var config = File.ReadAllText(Path.Combine(project.Root, ".issues", "config.json"));
    True(config.Contains("\"formatVersion\": 1", StringComparison.Ordinal), "configuration formatVersion was not camel-cased");
    Equal("1", File.ReadAllText(Path.Combine(project.Root, ".issues", "next-id")).Trim());
    File.WriteAllText(Path.Combine(project.Root, ".issues", "config.json"), "{\"formatVersion\":2}");
    Throws<IssueException>(() => ProjectLocator.Find(project.Root));
    True(!Directory.Exists(Path.Combine(project.Root, ".git")), "test project unexpectedly has Git metadata");
}

static void DanglingProjectLink()
{
    using var project = new TemporaryProject();
    var child = Directory.CreateDirectory(Path.Combine(project.Root, "nested")).FullName;
    try
    {
        Directory.CreateSymbolicLink(Path.Combine(child, ".issues"), Path.Combine(child, "missing-issues"));
        Throws<IssueException>(() => ProjectLocator.Find(child));
    }
    catch (PlatformNotSupportedException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

static void NestedProjectIsolation()
{
    using var project = new TemporaryProject();
    var outerIssue = project.Service.Create("Outer issue", "Draft");
    var nestedRoot = Path.Combine(project.Root, "nested");
    ProjectLocator.Initialize(nestedRoot);
    var nestedService = new IssueService(new IssueRepository(nestedRoot));
    nestedService.Create("Nested issue", "Draft");
    Equal(nestedRoot, ProjectLocator.Find(Path.Combine(nestedRoot, "src")));
    Equal(1, project.Service.List().Issues.Count);
    Equal(1, nestedService.List().Issues.Count);
    Equal(outerIssue.Id, project.Service.List().Issues[0].Id);
}

static void IssueOperations()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var issue = service.Create("Café docs", "# Context\n\nbody\n", IssuePriority.High, ["Docs"]);
    Equal(1L, issue.Id);
    Equal("Café docs", service.Show(issue.Id.ToString()).Title);
    Equal(1, service.List(new IssueFilter(Tag: "docs")).Issues.Count);
    Equal(1, service.Search("CAFÉ body").Count);
    Equal(IssuePriority.High, service.Show(issue.Id.ToString()).Priority);
    var child = service.Create("Child", "Draft");
    service.Edit(issue.Id.ToString(), children: [child.Id]);
    Equal(child.Id, service.Show(issue.Id.ToString()).Children.Single());
    service.Edit(child.Id.ToString(), parents: [issue.Id]);
    Equal(issue.Id, service.Show(child.Id.ToString()).Parents.Single());
}

static void NumericIdAllocation()
{
    using var project = new TemporaryProject();
    var services = Enumerable.Range(0, 12)
        .Select(_ => new IssueService(new IssueRepository(project.Root)))
        .ToArray();
    var creates = services.Select((service, index) =>
        Task.Run(() => service.Create($"Concurrent {index}", "Draft").Id)).ToArray();
    Task.WaitAll(creates);
    var ids = creates.Select(task => task.Result).Order().ToArray();
    True(ids.SequenceEqual(Enumerable.Range(1, 12).Select(value => (long)value)),
        "concurrent IDs were duplicated or skipped");
    File.Delete(services[0].Repository.GetIssuePath(ids[^1]));
    Equal(13L, services[0].Create("After deletion", "Draft").Id);
}

static void ReadinessAndNext()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var blocked = service.Create("Blocked task", "# Acceptance criteria\n\nMust ship\n", IssuePriority.Low);
    var blocker = service.Create("Urgent blocker", "# Acceptance criteria\n\nMust unblock\n", IssuePriority.Urgent);
    service.SetReady(blocked.Id.ToString(), true);
    service.SetReady(blocker.Id.ToString(), true);
    service.Edit(blocker.Id.ToString(), blocks: [blocked.Id]);
    Equal(blocker.Id, service.Next()!.Id);
    service.SetStatus(blocker.Id.ToString(), IssueStatus.Closed);
    True(service.Next() is null, "blocked issue was selected");
    var draft = service.Create("Draft", "Context only");
    Throws<IssueException>(() => service.SetReady(draft.Id.ToString(), true));
}

static void Preservation()
{
    using var project = new TemporaryProject();
    var repository = project.Service.Repository;
    const long id = 111;
    var content = string.Join("\r\n",
        "---",
        "schema: 2",
        $"id: {id}",
        "title: Original",
        "status: open",
        "ready: false",
        "priority: normal",
        "tags: []",
        "created_at: \"2026-01-01T00:00:00.0000000+00:00\"",
        "updated_at: \"2026-01-01T00:00:00.0000000+00:00\"",
        "parents: []",
        "children: []",
        "related: []",
        "blocks: []",
        "future_field:",
        "  nested: retained",
        "---",
        "# Body",
        "",
        "café",
        "");
    File.WriteAllText(repository.GetIssuePath(id), content);
    var issue = repository.Read(id);
    issue.Title = "Updated";
    issue.UpdatedAt = DateTimeOffset.UtcNow;
    var updated = repository.Update(issue, issue.Revision);
    var result = File.ReadAllText(repository.GetIssuePath(id));
    True(result.Contains("nested: retained", StringComparison.Ordinal), "unknown frontmatter was lost");
    True(result.Contains("# Body\r\n\r\ncafé\r\n", StringComparison.Ordinal), "body or CRLF line endings changed");
    Equal("Updated", updated.Title);
}

static void RevisionConflict()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var issue = service.Create("Original", "Draft");
    var first = service.Repository.Read(issue.Id);
    var stale = service.Repository.Read(issue.Id);
    first.Title = "First write";
    service.Repository.Update(first, first.Revision);
    stale.Title = "Stale write";
    Throws<IssueException>(() => service.Repository.Update(stale, stale.Revision));
    Equal("First write", service.Show(issue.Id.ToString()).Title);
}

static void ConcurrentWriters()
{
    using var project = new TemporaryProject();
    var repository = project.Service.Repository;
    var issue = project.Service.Create("Original", "Draft");
    var first = repository.Read(issue.Id);
    var second = repository.Read(issue.Id);
    using var barrier = new Barrier(2);
    var writes = new[]
    {
        Task.Run(() => TryConcurrentUpdate(repository, first, "Writer one", barrier)),
        Task.Run(() => TryConcurrentUpdate(repository, second, "Writer two", barrier))
    };
    Task.WaitAll(writes);
    Equal(1, writes.Count(task => task.Result));
}

static bool TryConcurrentUpdate(IssueRepository repository, Issue issue, string title, Barrier barrier)
{
    issue.Title = title;
    barrier.SignalAndWait();
    try
    {
        repository.Update(issue, issue.Revision);
        return true;
    }
    catch (IssueException)
    {
        return false;
    }
}

static void ConcurrentRelationshipChanges()
{
    using var project = new TemporaryProject();
    var repository = project.Service.Repository;
    var first = project.Service.Create("First", "Draft");
    var second = project.Service.Create("Second", "Draft");
    var firstCopy = repository.Read(first.Id);
    var secondCopy = repository.Read(second.Id);
    firstCopy.Blocks = [second.Id];
    secondCopy.Blocks = [first.Id];
    using var barrier = new Barrier(2);
    var writes = new[]
    {
        Task.Run(() => TryRelationshipUpdate(repository, firstCopy, barrier)),
        Task.Run(() => TryRelationshipUpdate(repository, secondCopy, barrier))
    };
    Task.WaitAll(writes);
    Equal(1, writes.Count(task => task.Result));
    True(!repository.List().Diagnostics.Any(diagnostic => diagnostic.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase)),
        "concurrent relationship edits created a cycle");
}

static bool TryRelationshipUpdate(IssueRepository repository, Issue issue, Barrier barrier)
{
    barrier.SignalAndWait();
    try
    {
        repository.Update(issue, issue.Revision);
        return true;
    }
    catch (IssueException)
    {
        return false;
    }
}

static void InvalidFiles()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    service.Create("Valid", "Draft");
    const long malformedId = 222;
    File.WriteAllText(service.Repository.GetIssuePath(malformedId),
        $"---\nschema: 99\nid: {malformedId}\n---\nbody\n");
    const long yamlErrorId = 223;
    File.WriteAllText(service.Repository.GetIssuePath(yamlErrorId), "---\nschema: [invalid\n---\n");
    var result = service.List();
    Equal(1, result.Issues.Count);
    Equal(2, result.Diagnostics.Count);
    True(result.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("Unsupported issue schema", StringComparison.Ordinal)),
        "missing version diagnostic");
}

static void References()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var first = service.Create("First", "Draft");
    var second = service.Create("Second", "Draft");
    service.Edit(first.Id.ToString(), children: [second.Id]);
    Throws<IssueException>(() => service.Edit(second.Id.ToString(), children: [first.Id]));
    Throws<IssueException>(() => service.Edit(first.Id.ToString(), parents: [first.Id]));
}

static void BrokenReferences()
{
    using var project = new TemporaryProject();
    var issue = project.Service.Create("Broken relationship", "Draft");
    var file = project.Service.Repository.GetIssuePath(issue.Id);
    var content = File.ReadAllText(file).Replace("blocks: []", "blocks:\n- 999", StringComparison.Ordinal);
    File.WriteAllText(file, content);
    var result = project.Service.List();
    Equal(1, result.Issues.Count);
    True(result.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("does not exist", StringComparison.Ordinal)),
        "broken reference was not reported");
}

static void SymlinkIssues()
{
    using var project = new TemporaryProject();
    const long id = 333;
    var outside = Path.Combine(project.Root, "outside.md");
    File.WriteAllText(outside, "not an issue");
    try
    {
        File.CreateSymbolicLink(project.Service.Repository.GetIssuePath(id), outside);
        var result = project.Service.List();
        Equal(1, result.Diagnostics.Count);
        True(result.Diagnostics[0].Message.Contains("Symbolic links", StringComparison.Ordinal),
            "symlink issue file was not rejected");
    }
    catch (PlatformNotSupportedException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

static void UnsafeIds()
{
    using var project = new TemporaryProject();
    Throws<IssueException>(() => project.Service.Repository.Read(0));
}

static void True(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static void Throws<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class TemporaryProject : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "open-issue-tests-" + Guid.NewGuid().ToString("N"));
    public IssueService Service { get; }

    public TemporaryProject()
    {
        ProjectLocator.Initialize(Root);
        Service = new IssueService(new IssueRepository(Root));
    }

    public void Dispose()
    {
        Directory.Delete(Root, recursive: true);
    }
}
