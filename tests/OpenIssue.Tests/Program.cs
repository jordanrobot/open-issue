using OpenIssue.Core;

var tests = new (string Name, Action Run)[]
{
    ("project initialization and discovery", ProjectInitialization),
    ("nested projects are isolated", NestedProjectIsolation),
    ("dangling project symlinks cannot fall through to a parent project", DanglingProjectLink),
    ("issue creation, listing, search, and Unicode", IssueOperations),
    ("creator and timestamp metadata is persisted and immutable", IssueMetadata),
    ("new issues are drafts by default", DraftByDefault),
    ("blank draft titles can be completed before readiness", BlankDraftTitles),
    ("legacy ready values map to draft values", LegacyReadyMetadata),
    ("numeric issue IDs are allocated without collisions", NumericIdAllocation),
    ("deleting an issue preserves its ID high-water mark", IssueDeletion),
    ("referenced issues cannot be deleted", ReferencedIssueDeletion),
    ("deleting imported IDs does not reuse them", ImportedIssueIdDeletion),
    ("readiness and deterministic next selection", ReadinessAndNext),
    ("readiness is independent of issue status", ReadinessIsIndependentOfStatus),
    ("unknown metadata, body, and line-ending preservation", Preservation),
    ("stale revisions do not overwrite edits", RevisionConflict),
    ("service edits reject stale revisions", ServiceRevisionConflict),
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

static void IssueMetadata()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var beforeCreate = DateTimeOffset.UtcNow;
    var issue = service.Create("Metadata", "");
    var afterCreate = DateTimeOffset.UtcNow;

    Equal(Environment.UserName, issue.CreatedBy);
    True(issue.CreatedAt >= beforeCreate && issue.CreatedAt <= afterCreate,
        "creation timestamp was not captured");
    Equal(issue.CreatedAt, issue.UpdatedAt);

    var persisted = service.Show(issue.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Equal(issue.CreatedBy, persisted.CreatedBy);
    Equal(issue.CreatedAt, persisted.CreatedAt);
    Equal(issue.UpdatedAt, persisted.UpdatedAt);

    var beforeEdit = DateTimeOffset.UtcNow;
    var edited = service.Edit(issue.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), title: "Edited");
    var afterEdit = DateTimeOffset.UtcNow;
    Equal(issue.CreatedBy, edited.CreatedBy);
    Equal(issue.CreatedAt, edited.CreatedAt);
    True(edited.UpdatedAt >= beforeEdit && edited.UpdatedAt <= afterEdit,
        "last edit timestamp was not captured");

    var reloaded = service.Show(issue.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Equal(edited.UpdatedAt, reloaded.UpdatedAt);

    var changedCreator = service.Repository.Read(issue.Id);
    changedCreator.CreatedBy = "someone-else";
    var saved = service.Repository.Update(changedCreator, changedCreator.Revision);
    Equal(issue.CreatedBy, saved.CreatedBy);
}

static void DraftByDefault()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var issue = service.Create("Draft issue", "# Acceptance criteria\n\nComplete review\n");
    True(issue.Draft, "new issues should be drafts until explicitly marked ready");
    var issueFile = File.ReadAllText(service.Repository.GetIssuePath(issue.Id));
    True(issueFile.Contains("schema: 3", StringComparison.Ordinal), "new issue did not use the current schema");
    True(issueFile.Contains("draft: true", StringComparison.Ordinal), "new issue was not persisted as a draft");
    True(!issueFile.Contains("ready:", StringComparison.Ordinal), "new issue still uses the legacy ready field");
}

static void BlankDraftTitles()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var issue = service.Create(string.Empty, string.Empty);

    True(issue.Draft, "a blank issue should remain a draft");
    Equal(string.Empty, issue.Title);
    Equal(string.Empty, service.Show(issue.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Title);
    Equal(string.Empty, service.List().Issues.Single().Title);
    Throws<IssueException>(() => service.SetDraft(
        issue.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        draft: false));
    Throws<IssueException>(() => service.Create(" ", string.Empty));

    var completed = service.Edit(
        issue.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        title: "Completed issue",
        draft: false);
    Equal("Completed issue", completed.Title);
    True(!completed.Draft, "a titled issue should be markable as ready");
}

static void LegacyReadyMetadata()
{
    using var project = new TemporaryProject();
    var repository = project.Service.Repository;
    const long id = 112;
    var content = string.Join("\n",
        "---",
        "schema: 2",
        $"id: {id}",
        "title: Legacy ready issue",
        "status: open",
        "ready: true",
        "priority: normal",
        "tags: []",
        "created_at: \"2026-01-01T00:00:00.0000000+00:00\"",
        "updated_at: \"2026-01-01T00:00:00.0000000+00:00\"",
        "parents: []",
        "children: []",
        "related: []",
        "blocks: []",
        "---",
        "# Acceptance criteria",
        "",
        "Complete review",
        "");
    File.WriteAllText(repository.GetIssuePath(id), content);
    True(!repository.Read(id).Draft, "legacy ready: true should map to draft: false");
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
    services[0].Delete(ids[^1].ToString(System.Globalization.CultureInfo.InvariantCulture));
    Equal(13L, services[0].Create("After deletion", "Draft").Id);
}

static void IssueDeletion()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var issue = service.Create("Delete me", "No longer needed");

    service.Delete(issue.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    True(!File.Exists(service.Repository.GetIssuePath(issue.Id)), "deleted issue file still exists");
    Throws<IssueException>(() => service.Show(issue.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    Equal(2L, service.Create("After deletion", "Draft").Id);
}

static void ReferencedIssueDeletion()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var source = service.Create("Source", "Draft");
    var target = service.Create("Target", "Draft");
    service.Edit(source.Id.ToString(), children: [target.Id]);

    Throws<IssueException>(() => service.Delete(target.Id.ToString()));
    True(File.Exists(service.Repository.GetIssuePath(target.Id)), "referenced issue was deleted");

    service.Edit(source.Id.ToString(), children: []);
    service.Delete(target.Id.ToString());
    Equal(1, service.List().Issues.Count);
    Equal(0, service.List().Diagnostics.Count);
}

static void ImportedIssueIdDeletion()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var original = service.Create("Original", "Draft");
    const long importedId = 100;
    var importedContent = File.ReadAllText(service.Repository.GetIssuePath(original.Id))
        .Replace($"id: {original.Id}", $"id: {importedId}", StringComparison.Ordinal);
    File.WriteAllText(service.Repository.GetIssuePath(importedId), importedContent);

    service.Delete(importedId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    Equal(importedId + 1, service.Create("After imported deletion", "Draft").Id);
}

static void ReadinessAndNext()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var blocked = service.Create("Blocked task", "Must ship", IssuePriority.Low);
    var blocker = service.Create("Urgent blocker", "Must unblock", IssuePriority.Urgent);
    service.SetDraft(blocked.Id.ToString(), false);
    service.SetDraft(blocker.Id.ToString(), false);
    Equal(0, service.List(new IssueFilter(Draft: true)).Issues.Count);
    Equal(2, service.List(new IssueFilter(Draft: false)).Issues.Count);
    service.Edit(blocker.Id.ToString(), blocks: [blocked.Id]);
    Equal(blocker.Id, service.Next()!.Id);
    service.SetStatus(blocker.Id.ToString(), IssueStatus.Closed);
    True(service.Next() is null, "blocked issue was selected");
    var bodyless = service.Create("Bodyless ready issue", "");
    var ready = service.SetDraft(bodyless.Id.ToString(), false);
    True(!ready.Draft, "a ready issue should not require a body or acceptance criteria");
    Equal(bodyless.Id, service.Next()!.Id);

}

static void ReadinessIsIndependentOfStatus()
{
    using var project = new TemporaryProject();
    var service = project.Service;

    var readyIssue = service.Create("Ready issue", "");
    service.SetDraft(readyIssue.Id.ToString(), false);
    var closedReadyIssue = service.SetStatus(readyIssue.Id.ToString(), IssueStatus.Closed);
    Equal(IssueStatus.Closed, closedReadyIssue.Status);
    True(!closedReadyIssue.Draft, "closing a ready issue should preserve readiness");

    var closedDraftIssue = service.Create("Closed draft issue", "");
    service.SetStatus(closedDraftIssue.Id.ToString(), IssueStatus.Closed);
    var readyClosedIssue = service.SetDraft(closedDraftIssue.Id.ToString(), false);
    True(!readyClosedIssue.Draft, "a closed issue should be allowed to become ready");

    var editedIssue = service.Create("Issue edited with closed status", "");
    var editedClosedReadyIssue = service.Edit(
        editedIssue.Id.ToString(),
        status: IssueStatus.Closed,
        draft: false);
    Equal(IssueStatus.Closed, editedClosedReadyIssue.Status);
    True(!editedClosedReadyIssue.Draft, "editing status should not change readiness");

    Equal(0, service.List().Diagnostics.Count);
    True(service.Next() is null, "closed ready issues should not be selected by next");
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
    True(issue.Draft, "legacy ready: false should map to draft: true");
    issue.Title = "Updated";
    issue.UpdatedAt = DateTimeOffset.UtcNow;
    var updated = repository.Update(issue, issue.Revision);
    var result = File.ReadAllText(repository.GetIssuePath(id));
    True(result.Contains("nested: retained", StringComparison.Ordinal), "unknown frontmatter was lost");
    True(result.Contains("# Body\r\n\r\ncafé\r\n", StringComparison.Ordinal), "body or CRLF line endings changed");
    True(result.Contains("schema: 3", StringComparison.Ordinal), "legacy issue was not upgraded to schema 3");
    True(result.Contains("draft: true", StringComparison.Ordinal), "legacy readiness was not inverted on write");
    True(!result.Contains("ready:", StringComparison.Ordinal), "legacy ready field was not removed on write");
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

static void ServiceRevisionConflict()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var issue = service.Create("Original", "Original body");
    var staleRevision = issue.Revision;
    var externalEdit = service.Resolve(issue.Id.ToString());
    externalEdit.Body = "External body";
    service.Repository.Update(externalEdit, externalEdit.Revision);

    Throws<IssueConflictException>(() =>
        service.Edit(issue.Id.ToString(), body: "Local body", expectedRevision: staleRevision));

    var saved = service.Resolve(issue.Id.ToString());
    Equal("Original", saved.Title);
    Equal("External body", saved.Body);
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
