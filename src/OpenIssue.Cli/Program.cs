using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenIssue.Core;
using System.CommandLine;

var root = Cli.CreateRootCommand();
return await Cli.InvokeAsync(root, args);

internal static class Cli
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static RootCommand CreateRootCommand()
    {
        var root = new RootCommand("Open Issue — local-first Markdown issue manager.");

        var init = new Command("init", "Initialize an issue project in the current directory or a supplied directory.");
        var initJson = AddJsonOption(init);
        var initDirectory = new Argument<string?>("directory") { Arity = ArgumentArity.ZeroOrOne };
        init.Add(initDirectory);
        init.SetAction(parse => Run(() =>
        {
            var path = ProjectLocator.Initialize(parse.GetValue(initDirectory));
            Write(parse.GetValue(initJson), path, $"Initialized Open Issue project at {path}");
            return 0;
        }, parse.GetValue(initJson)));
        root.Add(init);

        var title = new Option<string?>("--title") { Description = "Issue title." };
        var body = new Option<string?>("--body") { Description = "Issue body as Markdown text." };
        var bodyFile = new Option<string?>("--body-file") { Description = "Read Markdown from a file, or '-' for stdin." };
        var priority = new Option<string?>("--priority") { Description = "Priority: low, normal, high, urgent." };
        var tag = new Option<string?>("--tag") { Description = "Comma-separated tags." };
        var create = new Command("new", "Create a Markdown issue.");
        var createJson = AddJsonOption(create);
        create.Add(title);
        create.Add(body);
        create.Add(bodyFile);
        create.Add(priority);
        create.Add(tag);
        create.SetAction(async parse => await RunAsync(async () =>
        {
            var service = GetService();
            var issueTitle = parse.GetValue(title);
            if (issueTitle is null)
            {
                if (Console.IsInputRedirected || Console.IsOutputRedirected)
                {
                    throw new IssueException("Noninteractive creation requires --title and --body-file.");
                }

                Console.Write("Title: ");
                issueTitle = Console.ReadLine() ?? "";
            }

            var issueBody = await ReadBodyAsync(parse.GetValue(body), parse.GetValue(bodyFile));
            var issue = service.Create(issueTitle, issueBody,
                ParsePriority(parse.GetValue(priority)) ?? IssuePriority.Normal,
                SplitTags(parse.GetValue(tag)));
            Write(parse.GetValue(createJson), issue, FormatIssue(issue));
            return 0;
        }, parse.GetValue(createJson)));
        root.Add(create);

        var list = new Command("list", "List issues.");
        var listJson = AddJsonOption(list);
        var listFilters = AddFilterOptions(list);
        list.SetAction(parse => Run(() =>
        {
            var service = GetService();
            var filter = ParseFilter(parse.GetValue(listFilters.Status), parse.GetValue(listFilters.Tag),
                parse.GetValue(listFilters.Priority), parse.GetValue(listFilters.Ready));
            var result = service.List(filter);
            if (parse.GetValue(listJson))
            {
                Console.WriteLine(JsonSerializer.Serialize(new { issues = result.Issues, diagnostics = result.Diagnostics }, JsonOptions));
            }
            else
            {
                foreach (var issue in result.Issues)
                {
                    Console.WriteLine($"#{issue.Id,-5} {issue.Status.ToString().ToLowerInvariant(),-8} {issue.Priority.ToString().ToLowerInvariant(),-7} {(issue.Draft ? "draft" : "ready"),-5}  {issue.Title}");
                }

                PrintDiagnostics(result.Diagnostics);
            }

            return 0;
        }, parse.GetValue(listJson)));
        root.Add(list);

        var issueId = new Argument<long>("id") { Description = "Positive numeric issue ID." };
        var show = new Command("show", "Show an issue and its Markdown body.");
        var showJson = AddJsonOption(show);
        var showId = new Argument<long>("id") { Description = "Positive numeric issue ID." };
        show.Add(showId);
        show.SetAction(parse => Run(() =>
        {
            var issue = GetService().Show(parse.GetValue(showId).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Write(parse.GetValue(showJson), issue, FormatIssue(issue));
            return 0;
        }, parse.GetValue(showJson)));
        root.Add(show);

        var edit = new Command("edit", "Update issue fields and relationships.");
        var editJson = AddJsonOption(edit);
        edit.Add(issueId);
        var editTitle = new Option<string?>("--title");
        var editBody = new Option<string?>("--body");
        var editBodyFile = new Option<string?>("--body-file");
        var editStatus = new Option<string?>("--status");
        var editReady = new Option<string?>("--ready");
        var editPriority = new Option<string?>("--priority");
        var editTags = new Option<string?>("--tag");
        var parents = new Option<string?>("--parents") { Description = "Comma-separated parent issue IDs." };
        var children = new Option<string?>("--children") { Description = "Comma-separated child issue IDs." };
        var related = new Option<string?>("--related") { Description = "Comma-separated related issue IDs." };
        var blocks = new Option<string?>("--blocks") { Description = "Comma-separated issue IDs blocked by this issue." };
        foreach (var option in new Option[]
                 {
                     editTitle, editBody, editBodyFile, editStatus, editReady, editPriority, editTags,
                     parents, children, related, blocks
                 })
        {
            edit.Add(option);
        }

        edit.SetAction(async parse => await RunAsync(async () =>
        {
            var issue = GetService().Edit(
                parse.GetValue(issueId).ToString(System.Globalization.CultureInfo.InvariantCulture),
                parse.GetValue(editTitle),
                await ReadOptionalBodyAsync(parse.GetValue(editBody), parse.GetValue(editBodyFile)),
                ParsePriority(parse.GetValue(editPriority)),
                parse.GetValue(editTags) is { } tagValue ? SplitTags(tagValue) : null,
                parents: parse.GetValue(parents) is { } parentValue ? ParseIds(parentValue) : null,
                children: parse.GetValue(children) is { } childValue ? ParseIds(childValue) : null,
                related: parse.GetValue(related) is { } relatedValue ? ParseIds(relatedValue) : null,
                blocks: parse.GetValue(blocks) is { } blocksValue ? ParseIds(blocksValue) : null,
                status: ParseStatus(parse.GetValue(editStatus)),
                draft: ParseDraftFromReady(parse.GetValue(editReady)));
            Write(parse.GetValue(editJson), issue, FormatIssue(issue));
            return 0;
        }, parse.GetValue(editJson)));
        root.Add(edit);

        AddStatusCommand(root, "close", IssueStatus.Closed);
        AddStatusCommand(root, "decline", IssueStatus.Declined);
        AddStatusCommand(root, "reopen", IssueStatus.Open);
        AddReadyCommand(root, "ready", true);
        AddReadyCommand(root, "unready", false);

        var searchQuery = new Argument<string>("query") { Description = "Text to search in titles, tags, and bodies." };
        var search = new Command("search", "Search issue titles, tags, and bodies.");
        var searchJson = AddJsonOption(search);
        var searchFilters = AddFilterOptions(search);
        search.Add(searchQuery);
        search.SetAction(parse => Run(() =>
        {
            var service = GetService();
            var filter = ParseFilter(parse.GetValue(searchFilters.Status), parse.GetValue(searchFilters.Tag),
                parse.GetValue(searchFilters.Priority), parse.GetValue(searchFilters.Ready));
            var results = service.Search(parse.GetValue(searchQuery) ?? "", filter);
            var diagnostics = service.List(filter).Diagnostics;
            if (parse.GetValue(searchJson))
            {
                Console.WriteLine(JsonSerializer.Serialize(new { issues = results, diagnostics }, JsonOptions));
            }
            else
            {
                foreach (var issue in results)
                {
                    Console.WriteLine($"#{issue.Id}  {issue.Title}");
                }

                PrintDiagnostics(diagnostics);
            }

            return 0;
        }, parse.GetValue(searchJson)));
        root.Add(search);

        var next = new Command("next", "Select the highest-priority open, ready, unblocked issue.");
        var nextJson = AddJsonOption(next);
        next.SetAction(parse => Run(() =>
        {
            var issue = GetService().Next();
            if (parse.GetValue(nextJson))
            {
                Console.WriteLine(JsonSerializer.Serialize(issue, JsonOptions));
            }
            else if (issue is null)
            {
                Console.WriteLine("No open, ready, unblocked issue is available.");
            }
            else
            {
                Console.WriteLine(FormatIssue(issue));
            }

            return 0;
        }, parse.GetValue(nextJson)));
        root.Add(next);

        var help = new Command("help", "Show command help.");
        help.SetAction(parse =>
        {
            Console.WriteLine("""
                Open Issue CLI

                Commands:
                  issue init [DIRECTORY]
                  issue new --title TITLE (--body TEXT | --body-file FILE)
                  issue list [--status VALUE] [--tag TAG] [--priority VALUE] [--ready true|false] [--json]
                  issue show ID [--json]
                  issue edit ID [--title TITLE] [--body-file FILE] [--parents ID,...] [--children ID,...]
                  issue close|decline|reopen ID
                  issue ready|unready ID
                  issue search QUERY [--json]
                  issue next [--json]

                Use 'issue --help' or 'issue <command> --help' for details.
                """);
            return 0;
        });
        root.Add(help);
        return root;
    }

    public static async Task<int> InvokeAsync(RootCommand root, string[] args)
    {
        var result = root.Parse(args);
        if (result.Errors.Count != 0)
        {
            var message = string.Join(Environment.NewLine, result.Errors.Select(error => error.Message));
            if (args.Contains("--json", StringComparer.Ordinal))
            {
                Console.WriteLine(JsonSerializer.Serialize(new { error = new { code = "invalid_request", message } }, JsonOptions));
            }
            else
            {
                Console.Error.WriteLine(message);
            }

            return 2;
        }

        return await result.InvokeAsync();
    }

    private static Option<bool> AddJsonOption(Command command)
    {
        var json = new Option<bool>("--json") { Description = "Write machine-readable JSON." };
        command.Add(json);
        return json;
    }

    private sealed record FilterOptions(
        Option<string?> Status,
        Option<string?> Tag,
        Option<string?> Priority,
        Option<string?> Ready);

    private static FilterOptions AddFilterOptions(Command command)
    {
        var options = new FilterOptions(
            new Option<string?>("--status") { Description = "Filter by status: open, closed, declined." },
            new Option<string?>("--tag") { Description = "Filter by tag." },
            new Option<string?>("--priority") { Description = "Filter by priority: low, normal, high, urgent." },
            new Option<string?>("--ready") { Description = "Filter readiness: true or false." });
        command.Add(options.Status);
        command.Add(options.Tag);
        command.Add(options.Priority);
        command.Add(options.Ready);
        return options;
    }

    private static void AddStatusCommand(RootCommand root, string name, IssueStatus status)
    {
        var command = new Command(name, $"{name} an issue.");
        var json = AddJsonOption(command);
        var id = new Argument<long>("id");
        command.Add(id);
        command.SetAction(parse => Run(() =>
        {
            var issue = GetService().SetStatus(parse.GetValue(id).ToString(System.Globalization.CultureInfo.InvariantCulture), status);
            Write(parse.GetValue(json), issue, FormatIssue(issue));
            return 0;
        }, parse.GetValue(json)));
        root.Add(command);
    }

    private static void AddReadyCommand(RootCommand root, string name, bool ready)
    {
        var command = new Command(name, $"{(ready ? "Mark" : "Unmark")} an issue as ready.");
        var json = AddJsonOption(command);
        var id = new Argument<long>("id");
        command.Add(id);
        command.SetAction(parse => Run(() =>
        {
            var issue = GetService().SetDraft(parse.GetValue(id).ToString(System.Globalization.CultureInfo.InvariantCulture),
                draft: !ready);
            Write(parse.GetValue(json), issue, FormatIssue(issue));
            return 0;
        }, parse.GetValue(json)));
        root.Add(command);
    }

    private static int Run(Func<int> action, bool json)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            return Fail(exception.Message, json);
        }
    }

    private static async Task<int> RunAsync(Func<Task<int>> action, bool json)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (exception is IssueException or IOException or UnauthorizedAccessException)
        {
            return Fail(exception.Message, json);
        }
    }

    private static IssueService GetService() => new(new IssueRepository(ProjectLocator.Find()));

    private static async Task<string> ReadBodyAsync(string? body, string? bodyFile)
    {
        if (body is not null && bodyFile is not null)
        {
            throw new IssueException("Specify only one of --body and --body-file.");
        }

        if (body is not null)
        {
            return body;
        }

        if (bodyFile is not null)
        {
            return bodyFile == "-" ? await Console.In.ReadToEndAsync() : await File.ReadAllTextAsync(bodyFile);
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            throw new IssueException("Noninteractive creation requires --title and --body-file (use '-' for stdin).");
        }

        return await EditBodyAsync("# Context\n\n# Desired outcome\n\n# Acceptance criteria\n\n");
    }

    private static Task<string?> ReadOptionalBodyAsync(string? body, string? bodyFile)
    {
        if (body is not null && bodyFile is not null)
        {
            throw new IssueException("Specify only one of --body and --body-file.");
        }

        if (bodyFile is null)
        {
            return Task.FromResult(body);
        }

        return ReadOptionalBodyFileAsync(bodyFile);
    }

    private static async Task<string?> ReadOptionalBodyFileAsync(string bodyFile) =>
        bodyFile == "-" ? await Console.In.ReadToEndAsync() : await File.ReadAllTextAsync(bodyFile);

    private static async Task<string> EditBodyAsync(string starter)
    {
        var path = Path.Combine(Path.GetTempPath(), $"issue-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(path, starter);
        try
        {
            var editor = Environment.GetEnvironmentVariable("EDITOR");
            if (string.IsNullOrWhiteSpace(editor))
            {
                editor = OperatingSystem.IsWindows() ? "notepad.exe" : "vi";
            }

            using var process = Process.Start(new ProcessStartInfo(editor, path) { UseShellExecute = false })
                                ?? throw new IssueException($"Could not start editor '{editor}'.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new IssueException($"Editor '{editor}' exited with code {process.ExitCode}.");
            }

            return await File.ReadAllTextAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IssueFilter ParseFilter(string? status, string? tag, string? priority, string? ready) =>
        new(ParseStatus(status), tag, ParsePriority(priority), ParseDraftFromReady(ready));

    private static bool? ParseDraftFromReady(string? ready) =>
        ParseBoolean(ready) is { } isReady ? !isReady : null;

    private static IssueStatus? ParseStatus(string? value) => ParseEnum<IssueStatus>(value, "status");
    private static IssuePriority? ParsePriority(string? value) => ParseEnum<IssuePriority>(value, "priority");

    private static T? ParseEnum<T>(string? value, string key) where T : struct, Enum =>
        value is null ? null :
            Enum.GetNames<T>().Any(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) &&
            Enum.TryParse<T>(value, true, out var parsed)
                ? parsed
                : throw new IssueException($"Unsupported {key} '{value}'.");

    private static bool? ParseBoolean(string? value) =>
        value is null ? null :
            bool.TryParse(value, out var parsed) ? parsed :
            throw new IssueException($"Expected true or false, found '{value}'.");

    private static IEnumerable<string> SplitTags(string? value) =>
        value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private static IEnumerable<long> ParseIds(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => long.TryParse(id, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : throw new IssueException($"Issue IDs must be positive integers: '{id}'."));

    private static void Write<T>(bool json, T value, string human)
    {
        Console.WriteLine(json ? JsonSerializer.Serialize(value, JsonOptions) : human);
    }

    private static string FormatIssue(Issue issue) =>
        $"{issue.Title}\nID: {issue.Id}\nStatus: {issue.Status.ToString().ToLowerInvariant()}\nDraft: {issue.Draft}\nPriority: {issue.Priority.ToString().ToLowerInvariant()}\nParents: {string.Join(", ", issue.Parents)}\nChildren: {string.Join(", ", issue.Children)}\nTags: {string.Join(", ", issue.Tags)}\n\n{issue.Body}";

    private static void PrintDiagnostics(IEnumerable<IssueDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            Console.Error.WriteLine($"{diagnostic.Path}: {diagnostic.Message}");
        }
    }

    private static int Fail(string message, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { error = new { code = "invalid_request", message } }, JsonOptions));
        }
        else
        {
            Console.Error.WriteLine(message);
        }

        return message.Contains("changed since it was read", StringComparison.OrdinalIgnoreCase) ? 3 :
            message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No issue matches", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }
}
