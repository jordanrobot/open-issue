using OpenIssue.Core;
using OpenIssue.Desktop;
using System.Collections.Specialized;

var tests = new (string Name, Action Run)[]
{
    ("project initialization and discovery", ProjectInitialization),
    ("project issue list order persists and validates", IssueListOrderPersistence),
    ("color theme preference persists and migrates removed theme", ColorThemePreferencePersistence),
    ("nested projects are isolated", NestedProjectIsolation),
    ("dangling project symlinks cannot fall through to a parent project", DanglingProjectLink),
    ("issue creation, listing, search, and Unicode", IssueOperations),
    ("issue-body references and dependencies are parsed", IssueBodyLinks),
    ("issue-body tags are parsed and filterable", IssueBodyTags),
    ("issues can be duplicated as new drafts", IssueDuplication),
    ("Markdown editor formatting and indentation edits", MarkdownTextEditing),
    ("parent and child links can be added", IssueRelationshipLinks),
    ("parent and child links can be removed", IssueRelationshipLinkRemoval),
    ("issues can be reparented and moved to the top level", IssueParentMoves),
    ("creator and timestamp metadata is persisted and immutable", IssueMetadata),
    ("new issues are drafts by default", DraftByDefault),
    ("blank draft titles can be completed before readiness", BlankDraftTitles),
    ("legacy ready values map to draft values", LegacyReadyMetadata),
    ("numeric issue IDs are allocated without collisions", NumericIdAllocation),
    ("deleting an issue preserves its ID high-water mark", IssueDeletion),
    ("deleting a referenced issue removes its references", IssueDeletionRemovesReferences),
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
    ("unsafe IDs are rejected", UnsafeIds),
    ("list refresh retains rows without collection notifications", ListRefreshRetainsRows),
    ("title edits update the existing row without replacement", ListTitleUpdatesInPlace),
    ("body edits do not notify the displayed row", ListBodyUpdatesWithoutRedraw),
    ("filter changes remove only excluded rows", ListFilterRemovesOnlyExcludedRows),
    ("list additions and reordering preserve surviving rows", ListReorderingPreservesRows),
    ("issue list rows form a parent and child tree", IssueListTreeRelationships),
    ("issue list indentation follows visible tree depth", IssueListDepthFollowsVisibleTree),
    ("issue list reordering preserves the tree", IssueListReorderPreservesTree),
    ("empty list results remove rows without a reset", ListEmptyResultsWithoutReset),
    ("list row identity cannot be changed", ListRowIdentity)
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

static void IssueListOrderPersistence()
{
    using var project = new TemporaryProject();
    var store = new IssueListOrderStore(project.Root);
    Equal(0, store.Load().Count);

    store.Save([3, 1, 2]);
    Equal("3,1,2", string.Join(',', store.Load()));
    Throws<IssueException>(() => store.Save([1, 1]));
    Throws<IssueException>(() => store.Save([0]));
    Equal("3,1,2", string.Join(',', store.Load()));

    File.WriteAllText(Path.Combine(project.Root, ".issues", "issue-list-order.json"), "[1,");
    Throws<IssueException>(() => store.Load());
}

static void ColorThemePreferencePersistence()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "open-issue-theme-tests-" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "color-theme.txt");
    try
    {
        var store = new ColorThemePreferenceStore(path);
        Equal(ColorThemePreference.System, store.Load());

        foreach (var preference in Enum.GetValues<ColorThemePreference>())
        {
            store.Save(preference);
            Equal(preference, store.Load());
        }

        File.WriteAllText(path, "MaterialDark");
        Equal(ColorThemePreference.Dark, store.Load());

        File.WriteAllText(path, "not-a-color-theme");
        Equal(ColorThemePreference.System, store.Load());
        Throws<ArgumentOutOfRangeException>(() => store.Save((ColorThemePreference)999));
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
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

static void IssueDuplication()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var parent = service.Create("Parent", "");
    var source = service.Create("Original", "Copied body", IssuePriority.High, ["Docs"]);
    var child = service.Create("Child", "");
    var related = service.Create("Related", "");
    var blocker = service.Create("Blocker", "");

    service.AddParents(source.Id.ToString(), [parent.Id]);
    service.AddChildren(source.Id.ToString(), [child.Id]);
    service.Edit(
        source.Id.ToString(),
        related: [related.Id],
        blocks: [blocker.Id],
        status: IssueStatus.Closed,
        draft: false);
    var sourceSnapshot = service.Show(source.Id.ToString());

    var duplicate = service.Duplicate(source.Id.ToString());

    True(duplicate.Id != sourceSnapshot.Id, "duplicate reused the source issue ID");
    Equal(sourceSnapshot.Title, duplicate.Title);
    Equal(sourceSnapshot.Body, duplicate.Body);
    Equal(sourceSnapshot.Priority, duplicate.Priority);
    Equal(string.Join(',', sourceSnapshot.Tags), string.Join(',', duplicate.Tags));
    Equal(IssueStatus.Open, duplicate.Status);
    True(duplicate.Draft, "duplicate was not created as a draft");
    Equal(0, duplicate.Parents.Count);
    Equal(0, duplicate.Children.Count);
    Equal(0, duplicate.Related.Count);
    Equal(0, duplicate.Blocks.Count);
    True(File.Exists(service.Repository.GetIssuePath(duplicate.Id)), "duplicate issue file was not created");
    Throws<IssueException>(() => service.Duplicate("999"));
}

static void IssueBodyLinks()
{
    using var project = new TemporaryProject();
    var firstDependency = project.Service.Create("First dependency", "");
    var secondDependency = project.Service.Create("Second dependency", "");
    var body =
        $"See #{secondDependency.Id} and #{firstDependency.Id}; avoid ##2.\n" +
        $"Depends on #{firstDependency.Id}\n- depends on #{secondDependency.Id}\n";
    Equal($"{secondDependency.Id},{firstDependency.Id}", string.Join(',', IssueBodyLinkParser.GetReferences(body)));
    Equal($"{firstDependency.Id},{secondDependency.Id}", string.Join(',', IssueBodyLinkParser.GetDependencies(body)));

    const string textWithReference = "Before #12 after";
    True(IssueBodyLinkParser.TryReplaceReference(
            textWithReference,
            7,
            3,
            123,
            out var updatedText,
            out var caretIndex),
        "issue reference could not be replaced");
    Equal("Before #123 after", updatedText);
    Equal(11, caretIndex);

    var issue = project.Service.Create("Dependencies", body);
    Equal($"{firstDependency.Id},{secondDependency.Id}", string.Join(',', issue.Dependencies));

    const string token = "Depends on #12";
    True(IssueBodyLinkParser.TryGetReferenceAtCaret(
            token,
            token.Length,
            out var start,
            out var length,
            out var prefix),
        "issue reference token was not found at the caret");
    Equal(token.LastIndexOf('#'), start);
    Equal(3, length);
    Equal("12", prefix);
    True(!IssueBodyLinkParser.TryGetReferenceAtCaret("# heading", 1, out _, out _, out _),
        "a Markdown heading was treated as an issue reference");

    var suggestions = IssueBodyLinkParser.SuggestIssues(
        [ListIssue(13, "Thirteen"), ListIssue(12, "Twelve"), ListIssue(2, "Two"), ListIssue(123, "One twenty-three")],
        "12",
        3);
    Equal("12,123,13", string.Join(',', suggestions.Select(suggestion => suggestion.Id)));
}

static void IssueBodyTags()
{
    using var project = new TemporaryProject();
    const string body =
        "Use #docs, #release-2, and #Café; repeat #DOCS, link #123, heading # heading, " +
        "and ##ignored; not embeddedword#inside.";
    Equal("docs,release-2,Café", string.Join(',', IssueBodyTagParser.GetTags(body)));
    Equal("123", string.Join(',', IssueBodyLinkParser.GetReferences(body)));
    var tagCaret = body.IndexOf("#docs", StringComparison.Ordinal) + "#docs".Length;
    True(!IssueBodyLinkParser.TryGetReferenceAtCaret(body, tagCaret, out _, out _, out _),
        "a body tag was treated as an issue reference");
    Equal(
        "Keep #docs-v2; remove .",
        IssueBodyTagParser.RemoveInlineTag("Keep #docs-v2; remove #docs.", "DOCS"));

    var issue = project.Service.Create("Inline tags", body, tags: ["Docs"]);
    Equal("Docs,release-2,Café", string.Join(',', issue.EffectiveTags));
    Equal("Docs", string.Join(',', issue.Tags));
    Equal(1, project.Service.List(new IssueFilter(Tag: "release-2")).Issues.Count);
    Equal(1, project.Service.List(new IssueFilter(Tag: "café")).Issues.Count);
    Equal(1, project.Service.Search("release-2").Count);

    var updated = project.Service.Edit(issue.Id.ToString(), body: "No inline tags.");
    Equal(0, project.Service.List(new IssueFilter(Tag: "release-2")).Issues.Count);
    Equal("Docs", string.Join(',', updated.EffectiveTags));

    const string highlightedBody = "Tag #docs and #DOCS; issue #123 and #0; skip ##ignored.";
    var tagRanges = IssueBodyTagParser.GetTagRanges(highlightedBody);
    Equal(
        $"{highlightedBody.IndexOf("#docs", StringComparison.Ordinal)}:5," +
        $"{highlightedBody.IndexOf("#DOCS", StringComparison.Ordinal)}:5",
        string.Join(',', tagRanges.Select(range => $"{range.Start}:{range.Length}")));
    var referenceRange = IssueBodyLinkParser.GetReferenceRanges(highlightedBody).Single();
    Equal(highlightedBody.IndexOf("#123", StringComparison.Ordinal), referenceRange.Start);
    Equal(4, referenceRange.Length);
}

static void MarkdownTextEditing()
{
    const string checkedItem = "- [x] Finish the task";
    True(MarkdownTextEditor.TryCreateEnterEdit(
            checkedItem,
            checkedItem.Length,
            0,
            out var checkedItemEdit),
        "a Markdown task item did not provide a continuation edit");
    var continuedChecklist = ApplyTextEdit(checkedItem, checkedItemEdit);
    Equal(checkedItem + Environment.NewLine + "- [ ] ", continuedChecklist);
    Equal(continuedChecklist.Length, checkedItemEdit.Start + checkedItemEdit.CaretOffset);

    const string emptyItem = "\t- [ ] ";
    True(MarkdownTextEditor.TryCreateEnterEdit(
            emptyItem,
            emptyItem.Length,
            0,
            out var emptyItemEdit),
        "an empty Markdown task item did not provide an exit edit");
    Equal("\t", ApplyTextEdit(emptyItem, emptyItemEdit));
    Equal(1, emptyItemEdit.CaretOffset);

    const string orderedItem = "9. Finish the task";
    True(MarkdownTextEditor.TryCreateEnterEdit(
            orderedItem,
            orderedItem.Length,
            0,
            out var orderedItemEdit),
        "an ordered list item did not provide a continuation edit");
    Equal(orderedItem + Environment.NewLine + "10. ", ApplyTextEdit(orderedItem, orderedItemEdit));

    const string tabbedLine = "\t\tlet value = 1;";
    var splitIndex = tabbedLine.IndexOf("value", StringComparison.Ordinal);
    True(MarkdownTextEditor.TryCreateEnterEdit(
            tabbedLine,
            splitIndex,
            0,
            out var tabbedLineEdit),
        "an indented line did not provide an indentation edit");
    Equal(
        tabbedLine[..splitIndex] + Environment.NewLine + "\t\t" + tabbedLine[splitIndex..],
        ApplyTextEdit(tabbedLine, tabbedLineEdit));

    const string crlfItem = "- Finish the task\r\nNext line";
    var crlfCaret = crlfItem.IndexOf("\r\n", StringComparison.Ordinal);
    True(MarkdownTextEditor.TryCreateEnterEdit(
            crlfItem,
            crlfCaret,
            0,
            out var crlfEdit),
        "a list item did not provide a CRLF continuation edit");
    Equal("- Finish the task\r\n- \r\nNext line", ApplyTextEdit(crlfItem, crlfEdit));
    True(!MarkdownTextEditor.TryCreateEnterEdit("plain text", 5, 0, out _),
        "an unindented paragraph was intercepted");
    True(!MarkdownTextEditor.TryCreateEnterEdit("- selected", 2, 3, out _),
        "a selection was intercepted");

    const string indentedBlock = "    first\r\n\tsecond\r\nthird";
    True(MarkdownTextEditor.TryCreateOutdentEdit(
            indentedBlock,
            0,
            indentedBlock.Length,
            out var outdentEdit),
        "an indented selection did not provide an outdent edit");
    Equal("first\r\nsecond\r\nthird", ApplyTextEdit(indentedBlock, outdentEdit));
    Equal(outdentEdit.Replacement.Length, outdentEdit.SelectionLength);
    True(!MarkdownTextEditor.TryCreateOutdentEdit("unindented", 0, 0, out _),
        "an unindented line provided an outdent edit");

    const string linesToIndent = "first\r\nsecond";
    var indentEdit = MarkdownTextEditor.CreateIndentEdit(
        linesToIndent,
        0,
        linesToIndent.Length);
    Equal("\tfirst\r\n\tsecond", ApplyTextEdit(linesToIndent, indentEdit));
    Equal(indentEdit.Replacement.Length, indentEdit.SelectionLength);

    var boldEdit = MarkdownTextEditor.CreateInlineFormatEdit(
        "hello world",
        6,
        5,
        "**",
        "**");
    Equal("**world**", boldEdit.Replacement);
    Equal(2, boldEdit.CaretOffset);
    Equal(5, boldEdit.SelectionLength);
    var emptyBoldEdit = MarkdownTextEditor.CreateInlineFormatEdit("hello", 2, 0, "**", "**");
    Equal("****", emptyBoldEdit.Replacement);
    Equal(2, emptyBoldEdit.CaretOffset);

    var selectedLinkEdit = MarkdownTextEditor.CreateLinkEdit("visit docs", 6, 4);
    Equal("[docs](url)", selectedLinkEdit.Replacement);
    Equal(7, selectedLinkEdit.CaretOffset);
    Equal(3, selectedLinkEdit.SelectionLength);
    var emptyLinkEdit = MarkdownTextEditor.CreateLinkEdit("visit ", 6, 0);
    Equal("[text](url)", emptyLinkEdit.Replacement);
    Equal(1, emptyLinkEdit.CaretOffset);
    Equal(4, emptyLinkEdit.SelectionLength);

    var headingEdit = MarkdownTextEditor.CreateHeadingEdit("title", 0, 5);
    Equal("# title", headingEdit.Replacement);
    var existingHeadingEdit = MarkdownTextEditor.CreateHeadingEdit("## title", 0, 0);
    Equal("# title", existingHeadingEdit.Replacement);
    var levelOneHeadingEdit = MarkdownTextEditor.CreateHeadingEdit("# title", 0, 0);
    Equal("title", levelOneHeadingEdit.Replacement);

    var bulletEdit = MarkdownTextEditor.CreateListEdit(
        "first\r\n- second",
        0,
        "first\r\n- second".Length,
        MarkdownListKind.Bulleted);
    Equal("- first\r\n- second", ApplyTextEdit("first\r\n- second", bulletEdit));
    var numberedEdit = MarkdownTextEditor.CreateListEdit(
        "- first\r\n- second",
        0,
        "- first\r\n- second".Length,
        MarkdownListKind.Numbered);
    Equal("1. first\r\n2. second", ApplyTextEdit("- first\r\n- second", numberedEdit));
    var checkboxEdit = MarkdownTextEditor.CreateListEdit(
        "- [x] first",
        0,
        "- [x] first".Length,
        MarkdownListKind.Checkbox);
    Equal("- [ ] first", ApplyTextEdit("- [x] first", checkboxEdit));

    using var project = new TemporaryProject();
    var issue = project.Service.Create("Checklist", "- [ ] pending\n- [x] complete\n");
    const string updatedBody = "- [x] pending\n- [ ] complete\n";
    project.Service.Edit(issue.Id.ToString(), body: updatedBody);
    Equal(updatedBody, project.Service.Show(issue.Id.ToString()).Body);
}

static string ApplyTextEdit(string text, MarkdownTextEdit edit) =>
    text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];

static void IssueRelationshipLinks()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var parent = service.Create("Parent", "Draft");
    var secondParent = service.Create("Second parent", "Draft");
    var child = service.Create("Child", "Draft");

    service.AddParents(child.Id.ToString(), [parent.Id, secondParent.Id, parent.Id]);
    Equal($"{parent.Id},{secondParent.Id}", string.Join(',', service.Show(child.Id.ToString()).Parents));
    service.AddParents(child.Id.ToString(), [parent.Id]);
    Equal(2, service.Show(child.Id.ToString()).Parents.Count);

    service.AddChildren(parent.Id.ToString(), [child.Id]);
    Equal(child.Id.ToString(), string.Join(',', service.Show(parent.Id.ToString()).Children));
    Throws<IssueException>(() => service.AddChildren(parent.Id.ToString(), [long.MaxValue]));
}

static void IssueRelationshipLinkRemoval()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var parent = service.Create("Parent", "");
    var secondParent = service.Create("Second parent", "");
    var child = service.Create("Child", "");

    service.AddParents(child.Id.ToString(), [parent.Id, secondParent.Id]);
    service.AddChildren(parent.Id.ToString(), [child.Id]);
    service.AddChildren(secondParent.Id.ToString(), [child.Id]);

    var afterFirstRemoval = service.RemoveParent(child.Id.ToString(), parent.Id);
    Equal(secondParent.Id.ToString(), string.Join(',', afterFirstRemoval.Parents));
    Equal(0, service.Show(parent.Id.ToString()).Children.Count);

    var afterSecondRemoval = service.RemoveParent(child.Id.ToString(), secondParent.Id);
    Equal(0, afterSecondRemoval.Parents.Count);
    Equal(0, service.Show(secondParent.Id.ToString()).Children.Count);
    Equal(afterSecondRemoval.Revision, service.RemoveParent(
        child.Id.ToString(),
        secondParent.Id,
        afterSecondRemoval.Revision).Revision);

    var childWithParentLink = service.Create("Child with parent link", "");
    service.AddParents(childWithParentLink.Id.ToString(), [parent.Id]);
    service.RemoveChild(parent.Id.ToString(), childWithParentLink.Id);
    Equal(0, service.Show(childWithParentLink.Id.ToString()).Parents.Count);

    var explicitlyLinkedChild = service.Create("Explicit child", "");
    service.AddChildren(parent.Id.ToString(), [explicitlyLinkedChild.Id]);
    var updatedParent = service.RemoveChild(parent.Id.ToString(), explicitlyLinkedChild.Id);
    Equal(0, updatedParent.Children.Count);
    Equal(0, service.Show(explicitlyLinkedChild.Id.ToString()).Parents.Count);
}

static void IssueParentMoves()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var firstParent = service.Create("First parent", "");
    var secondParent = service.Create("Second parent", "");
    var newParent = service.Create("New parent", "");
    var child = service.Create("Child", "");

    service.AddParents(child.Id.ToString(), [firstParent.Id, secondParent.Id]);
    service.AddChildren(firstParent.Id.ToString(), [child.Id]);
    service.AddChildren(secondParent.Id.ToString(), [child.Id]);

    var moved = service.MoveToParent(child.Id.ToString(), newParent.Id);
    Equal(newParent.Id.ToString(), string.Join(',', moved.Parents));
    Equal(0, service.Show(firstParent.Id.ToString()).Children.Count);
    Equal(0, service.Show(secondParent.Id.ToString()).Children.Count);

    True(service.MoveToRoot(child.Id.ToString()), "the child was not moved to the top level");
    Equal(0, service.Show(child.Id.ToString()).Parents.Count);
    True(!service.MoveToRoot(child.Id.ToString()), "moving a top-level issue reported a change");

    var explicitlyLinkedChild = service.Create("Explicit child", "");
    service.AddChildren(newParent.Id.ToString(), [explicitlyLinkedChild.Id]);
    True(service.MoveToRoot(explicitlyLinkedChild.Id.ToString()),
        "an explicit parent children link was not removed");
    Equal(0, service.Show(newParent.Id.ToString()).Children.Count);
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

static void IssueDeletionRemovesReferences()
{
    using var project = new TemporaryProject();
    var service = project.Service;
    var remainingChild = service.Create("Remaining child", "");
    var parentOfChild = service.Create("Parent of child", "");
    var targetChild = service.Create("Target child", "");
    service.Edit(parentOfChild.Id.ToString(), children: [targetChild.Id, remainingChild.Id]);

    var remainingDependency = service.Create("Remaining dependency", "");
    var targetDependency = service.Create("Target dependency", "");
    var dependent = service.Create(
        "Dependent",
        $"- Depends on #{targetDependency.Id}\n- Depends on #{remainingDependency.Id}\n");
    var blocker = service.Create("Blocker", "");
    service.Edit(blocker.Id.ToString(), blocks: [targetDependency.Id, remainingDependency.Id]);
    var related = service.Create("Related", "");
    service.Edit(related.Id.ToString(), related: [targetDependency.Id, remainingDependency.Id]);

    var otherParent = service.Create("Other parent", "");
    var targetParent = service.Create("Target parent", "");
    var childOfParent = service.Create("Child of parent", "");
    service.Edit(childOfParent.Id.ToString(), parents: [targetParent.Id, otherParent.Id]);

    service.Delete(targetChild.Id.ToString());
    service.Delete(targetDependency.Id.ToString());
    service.Delete(targetParent.Id.ToString());

    Equal(remainingChild.Id, service.Show(parentOfChild.Id.ToString()).Children.Single());
    Equal(remainingDependency.Id, service.Show(dependent.Id.ToString()).Dependencies.Single());
    Equal($"- Depends on #{remainingDependency.Id}\n", service.Show(dependent.Id.ToString()).Body);
    Equal(remainingDependency.Id, service.Show(blocker.Id.ToString()).Blocks.Single());
    Equal(remainingDependency.Id, service.Show(related.Id.ToString()).Related.Single());
    Equal(otherParent.Id, service.Show(childOfParent.Id.ToString()).Parents.Single());
    Equal(0, service.List().Issues.Count(issue =>
        issue.Id == targetChild.Id || issue.Id == targetDependency.Id || issue.Id == targetParent.Id));
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

static Issue ListIssue(long id, string title, string body = "") => new()
{
    Id = id,
    Title = title,
    Body = body,
    CreatedAt = DateTimeOffset.UnixEpoch,
    UpdatedAt = DateTimeOffset.UnixEpoch
};

static void ListRefreshRetainsRows()
{
    var rows = new IssueListCollection();
    rows.Synchronize([ListIssue(1, "First"), ListIssue(2, "Second")]);
    var first = rows[0];
    var second = rows[1];
    var actions = new List<NotifyCollectionChangedAction>();
    rows.CollectionChanged += (_, args) => actions.Add(args.Action);
    rows.Synchronize([ListIssue(1, "First"), ListIssue(2, "Second")]);
    Equal(0, actions.Count);
    True(ReferenceEquals(first, rows[0]) && ReferenceEquals(second, rows[1]),
        "unchanged refresh recreated list rows");
}

static void ListTitleUpdatesInPlace()
{
    var rows = new IssueListCollection();
    rows.Synchronize([ListIssue(1, "Old title")]);
    var row = rows[0];
    var actions = new List<NotifyCollectionChangedAction>();
    var properties = new List<string?>();
    rows.CollectionChanged += (_, args) => actions.Add(args.Action);
    row.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
    var latest = ListIssue(1, "New title");
    row.Update(latest);
    Equal(0, actions.Count);
    Equal(nameof(IssueListItem.Title), properties.Single());
    Equal("New title", row.Title);
    True(ReferenceEquals(row, rows[0]) && ReferenceEquals(latest, row.Issue),
        "title update did not preserve the row and latest snapshot");
}

static void ListBodyUpdatesWithoutRedraw()
{
    var rows = new IssueListCollection();
    rows.Synchronize([ListIssue(1, "Title", "Old body")]);
    var properties = new List<string?>();
    var actions = new List<NotifyCollectionChangedAction>();
    rows[0].PropertyChanged += (_, args) => properties.Add(args.PropertyName);
    rows.CollectionChanged += (_, args) => actions.Add(args.Action);
    rows.Synchronize([ListIssue(1, "Title", "New body")]);
    Equal(0, properties.Count);
    Equal(0, actions.Count);
    Equal("New body", rows[0].Issue.Body);
}

static void ListFilterRemovesOnlyExcludedRows()
{
    var rows = new IssueListCollection();
    rows.Synchronize([ListIssue(1, "First"), ListIssue(2, "Second"), ListIssue(3, "Third")]);
    var retained = rows[1];
    var actions = new List<NotifyCollectionChangedAction>();
    rows.CollectionChanged += (_, args) => actions.Add(args.Action);
    rows.Synchronize([ListIssue(2, "Second")]);
    Equal(1, rows.Count);
    True(ReferenceEquals(retained, rows[0]), "filtering recreated the surviving row");
    True(actions.SequenceEqual([NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Remove]),
        "filtering performed changes other than removing excluded rows");
}

static void ListReorderingPreservesRows()
{
    var rows = new IssueListCollection();
    rows.Synchronize([ListIssue(1, "First"), ListIssue(2, "Second")]);
    var first = rows[0];
    var second = rows[1];
    var actions = new List<NotifyCollectionChangedAction>();
    rows.CollectionChanged += (_, args) => actions.Add(args.Action);
    rows.Synchronize([ListIssue(2, "Second"), ListIssue(3, "Third"), ListIssue(1, "First")]);
    True(rows.Select(row => row.Id).SequenceEqual([2L, 3L, 1L]), "results were not in the requested order");
    True(ReferenceEquals(second, rows[0]) && ReferenceEquals(first, rows[2]),
        "reordering replaced surviving rows");
    True(actions.SequenceEqual([NotifyCollectionChangedAction.Move, NotifyCollectionChangedAction.Add]),
        "reordering performed unnecessary collection changes");
}

static void IssueListTreeRelationships()
{
    var parent = ListIssue(1, "Parent");
    parent.Children = [3];
    var child = ListIssue(2, "Child");
    child.Parents = [1];
    var explicitlyListedChild = ListIssue(3, "Explicit child");
    var otherRoot = ListIssue(4, "Other root");
    var rows = new IssueListCollection();

    rows.Synchronize([parent, child, explicitlyListedChild, otherRoot]);

    True(rows.RootItems.Select(row => row.Id).SequenceEqual([1L, 4L]),
        "issues with parent links were not nested under their parent");
    True(rows.RootItems[0].Children.Select(row => row.Id).SequenceEqual([2L, 3L]),
        "the tree did not combine parent and child relationship metadata");
    True(rows.Single(row => row.Id == 2).IsNested,
        "an issue with a parent was not marked as nested");
    rows.SetDropHint(2, IssueDropHint.Nest);
    Equal(IssueDropHint.Nest, rows.Single(row => row.Id == 2).DropHint);
    rows.ClearDropHints();
    Equal(IssueDropHint.None, rows.Single(row => row.Id == 2).DropHint);

    var first = ListIssue(10, "First cycle member");
    first.Parents = [11];
    var second = ListIssue(11, "Second cycle member");
    second.Parents = [10];
    rows.Synchronize([first, second]);
    Equal(1, rows.RootItems.Count);
    Equal(1, rows.RootItems[0].Children.Count);
}

static void IssueListDepthFollowsVisibleTree()
{
    var parent = ListIssue(1, "Parent");
    var child = ListIssue(2, "Child");
    child.Parents = [1];
    var grandchild = ListIssue(3, "Grandchild");
    grandchild.Parents = [2];
    var rows = new IssueListCollection();

    rows.Synchronize([parent, child, grandchild]);

    Equal(0, rows.Single(row => row.Id == parent.Id).Depth);
    Equal(1, rows.Single(row => row.Id == child.Id).Depth);
    Equal(2, rows.Single(row => row.Id == grandchild.Id).Depth);

    rows.Synchronize([grandchild]);
    Equal(0, rows.Single().Depth);
    True(rows.Single().IsNested, "a filtered parent link was not retained for the root-level row");
    True(ReferenceEquals(rows.Single(), rows.RootItems.Single()),
        "a row whose parent was filtered out was not displayed at the root");
}

static void IssueListReorderPreservesTree()
{
    var parent = ListIssue(1, "Parent");
    var child = ListIssue(2, "Child");
    child.Parents = [1];
    var otherRoot = ListIssue(3, "Other root");
    var rows = new IssueListCollection();
    rows.Synchronize([parent, child, otherRoot]);
    var parentRow = rows.Single(row => row.Id == parent.Id);
    var childRow = rows.Single(row => row.Id == child.Id);

    rows.Reorder([3, 1, 2]);

    True(rows.Select(row => row.Id).SequenceEqual([3L, 1L, 2L]),
        "the flat issue order did not change");
    True(rows.RootItems.Select(row => row.Id).SequenceEqual([3L, 1L]),
        "the root rows did not follow the saved order");
    True(ReferenceEquals(parentRow.Children.Single(), childRow),
        "reordering replaced or detached the child row");
}

static void ListEmptyResultsWithoutReset()
{
    var rows = new IssueListCollection();
    rows.Synchronize([ListIssue(1, "First")]);
    var actions = new List<NotifyCollectionChangedAction>();
    rows.CollectionChanged += (_, args) => actions.Add(args.Action);
    rows.Synchronize([]);
    Equal(0, rows.Count);
    Equal(NotifyCollectionChangedAction.Remove, actions.Single());
    rows.Synchronize([ListIssue(2, "Second")]);
    Equal(2L, rows.Single().Id);
}

static void ListRowIdentity()
{
    var row = new IssueListItem(ListIssue(1, "First"));
    Throws<ArgumentException>(() => row.Update(ListIssue(2, "Second")));
    Equal(1L, row.Id);
    Equal("First", row.Title);
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
