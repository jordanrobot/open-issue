using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeIssue.Core;

return await FreeIssueCommand.RunAsync(args);

internal static class FreeIssueCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> RunAsync(string[] rawArgs)
    {
        var args = rawArgs.ToList();
        var json = args.Remove("--json");
        string? projectPath = null;
        var projectIndex = args.IndexOf("--project");
        if (projectIndex >= 0)
        {
            if (projectIndex + 1 >= args.Count)
            {
                return Fail("--project requires a directory.", json);
            }

            projectPath = args[projectIndex + 1];
            args.RemoveRange(projectIndex, 2);
        }

        if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        args.RemoveAt(0);
        var positional = new List<string>();
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(argument);
                continue;
            }

            var name = argument[2..];
            if (name.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                json = true;
                continue;
            }

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return Fail($"Option '{argument}' requires a value.", json);
            }

            if (!options.TryGetValue(name, out var values))
            {
                values = [];
                options[name] = values;
            }

            values.Add(args[++index]);
        }

        var allowedOptions = command switch
        {
            "init" => new[] { "path" },
            "new" => new[] { "title", "body", "body-file", "priority", "tag" },
            "list" or "search" => new[] { "status", "tag", "priority", "ready" },
            "show" or "next" or "close" or "decline" or "reopen" or "ready" or "unready" or "help" => [],
            "edit" => new[] { "title", "body", "body-file", "status", "ready", "priority", "tag", "parent", "related", "blocks" },
            _ => []
        };
        var unknownOption = options.Keys.FirstOrDefault(name => !allowedOptions.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (unknownOption is not null)
        {
            return Fail($"Unknown option '--{unknownOption}' for '{command}'.", json);
        }

        if (command is "init" or "list" or "next" or "help" && positional.Count != 0)
        {
            return Fail($"Unexpected argument '{positional[0]}' for '{command}'.", json);
        }

        if (command == "new" && positional.Count != 0)
        {
            return Fail($"Unexpected argument '{positional[0]}' for '{command}'.", json);
        }

        if (command is "show" or "edit" or "close" or "decline" or "reopen" or "ready" or "unready" &&
            positional.Count != 1)
        {
            return Fail($"'{command}' requires exactly one issue ID.", json);
        }

        if (command == "search" && positional.Count == 0)
        {
            return Fail("search requires a query.", json);
        }

        if (command == "init")
        {
            try
            {
                var root = ProjectLocator.Initialize(projectPath ?? Option(options, "path") ?? Environment.CurrentDirectory);
                Console.WriteLine($"Initialized Free Issue project at {root}");
                return 0;
            }
            catch (IssueException exception)
            {
                return Fail(exception.Message, json);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Fail(exception.Message, json);
            }
        }

        try
        {
            var root = ProjectLocator.Find(projectPath);
            var service = new IssueService(new IssueRepository(root));
            var filter = ParseFilter(options);
            switch (command)
            {
                case "new":
                {
                    var title = Option(options, "title");
                    if (title is null)
                    {
                        if (Console.IsInputRedirected || Console.IsOutputRedirected)
                        {
                            return Fail("Noninteractive creation requires --title and --body-file (use '-' to read the body from stdin).", json);
                        }

                        title = PromptForTitle();
                    }

                    var body = await ReadBodyAsync(options, json);
                    if (body is null)
                    {
                        return 2;
                    }

                    var issue = service.Create(title, body, ParsePriority(Option(options, "priority")) ?? IssuePriority.Normal,
                        Options(options, "tag"));
                    PrintIssue(issue, json);
                    return 0;
                }
                case "list":
                {
                    var result = service.List(filter);
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new { issues = result.Issues, diagnostics = result.Diagnostics }, JsonOptions));
                    }
                    else
                    {
                        foreach (var issue in result.Issues)
                        {
                            Console.WriteLine($"{issue.Id[..8]}  {issue.Status.ToString().ToLowerInvariant(),-8} {issue.Priority.ToString().ToLowerInvariant(),-7} {(issue.Ready ? "ready" : "      ")}  {issue.Title}");
                        }

                        PrintDiagnostics(result.Diagnostics);
                    }

                    return 0;
                }
                case "show":
                {
                    var issue = service.Show(RequiredPosition(positional, 0, "show requires an issue ID."));
                    PrintIssue(issue, json);
                    return 0;
                }
                case "edit":
                {
                    var id = RequiredPosition(positional, 0, "edit requires an issue ID.");
                    var issue = service.Edit(id,
                        Option(options, "title"),
                        await ReadOptionalBodyAsync(options),
                        ParsePriority(Option(options, "priority")),
                        options.ContainsKey("tag") ? Options(options, "tag") : null,
                        Option(options, "parent"),
                        options.ContainsKey("related") ? SplitIds(Options(options, "related")) : null,
                        options.ContainsKey("blocks") ? SplitIds(Options(options, "blocks")) : null,
                        ParseEnumOption<IssueStatus>(Option(options, "status")),
                        ParseBoolean(Option(options, "ready")));
                    PrintIssue(issue, json);
                    return 0;
                }
                case "close":
                case "decline":
                case "reopen":
                {
                    var id = RequiredPosition(positional, 0, $"{command} requires an issue ID.");
                    var status = command switch
                    {
                        "close" => IssueStatus.Closed,
                        "decline" => IssueStatus.Declined,
                        _ => IssueStatus.Open
                    };
                    PrintIssue(service.SetStatus(id, status), json);
                    return 0;
                }
                case "ready":
                case "unready":
                    PrintIssue(service.SetReady(RequiredPosition(positional, 0, $"{command} requires an issue ID."),
                        command == "ready"), json);
                    return 0;
                case "search":
                {
                    var query = string.Join(' ', positional);
                    if (string.IsNullOrWhiteSpace(query))
                    {
                        throw new IssueException("search requires a query.");
                    }

                    var results = service.Search(query, filter);
                    var diagnostics = service.List(filter).Diagnostics;
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new { issues = results, diagnostics }, JsonOptions));
                    }
                    else
                    {
                        foreach (var issue in results)
                        {
                            Console.WriteLine($"{issue.Id[..8]}  {issue.Title}");
                        }

                        PrintDiagnostics(diagnostics);
                    }

                    return 0;
                }
                case "next":
                {
                    var issue = service.Next();
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(issue, JsonOptions));
                    }
                    else if (issue is null)
                    {
                        Console.WriteLine("No open, ready, unblocked issue is available.");
                    }
                    else
                    {
                        PrintIssue(issue, json: false);
                    }

                    return 0;
                }
                default:
                    return Fail($"Unknown command '{command}'. Run 'free-issue help'.", json);
            }
        }
        catch (IssueException exception)
        {
            return Fail(exception.Message, json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Fail(exception.Message, json);
        }
    }

    private static async Task<string?> ReadBodyAsync(
        IReadOnlyDictionary<string, List<string>> options,
        bool json)
    {
        if (options.ContainsKey("body-file") && options.ContainsKey("body"))
        {
            Fail("Specify only one of --body and --body-file.", json);
            return null;
        }

        if (options.TryGetValue("body-file", out var files))
        {
            if (files.Count != 1)
            {
                Fail("--body-file may be specified only once.", json);
                return null;
            }

            return files[0] == "-"
                ? await Console.In.ReadToEndAsync()
                : await File.ReadAllTextAsync(files[0]);
        }

        if (options.TryGetValue("body", out var bodies))
        {
            if (bodies.Count != 1)
            {
                Fail("--body may be specified only once.", json);
                return null;
            }

            return bodies[0];
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Fail("Noninteractive creation requires --title and --body-file (use '-' to read the body from stdin).", json);
            return null;
        }

        return await EditBodyAsync("# Context\n\n# Desired outcome\n\n# Acceptance criteria\n\n");
    }

    private static async Task<string?> ReadOptionalBodyAsync(IReadOnlyDictionary<string, List<string>> options)
    {
        if (options.TryGetValue("body-file", out var files))
        {
            if (files.Count != 1)
            {
                throw new IssueException("--body-file may be specified only once.");
            }

            return files[0] == "-" ? await Console.In.ReadToEndAsync() : await File.ReadAllTextAsync(files[0]);
        }

        return Option(options, "body");
    }

    private static async Task<string> EditBodyAsync(string starter)
    {
        var path = Path.Combine(Path.GetTempPath(), $"free-issue-{Guid.NewGuid():N}.md");
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

    private static string PromptForTitle()
    {
        Console.Write("Title: ");
        return Console.ReadLine() ?? "";
    }

    private static IssueFilter ParseFilter(IReadOnlyDictionary<string, List<string>> options) =>
        new(ParseEnumOption<IssueStatus>(Option(options, "status")),
            Option(options, "tag"),
            ParsePriority(Option(options, "priority")),
            ParseBoolean(Option(options, "ready")));

    private static bool? ParseBoolean(string? value) =>
        value is null ? null :
            bool.TryParse(value, out var parsed) ? parsed :
            throw new IssueException($"Expected true or false, found '{value}'.");

    private static T? ParseEnumOption<T>(string? value) where T : struct, Enum =>
        value is null ? null :
            Enum.GetNames<T>().Any(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) &&
            Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
                ? parsed
                : throw new IssueException($"Unsupported {typeof(T).Name.ToLowerInvariant()} '{value}'.");

    private static IssuePriority? ParsePriority(string? value) => ParseEnumOption<IssuePriority>(value);

    private static string RequiredPosition(IReadOnlyList<string> positional, int index, string message) =>
        positional.Count > index ? positional[index] : throw new IssueException(message);

    private static string? Option(IReadOnlyDictionary<string, List<string>> options, string name)
    {
        if (!options.TryGetValue(name, out var values))
        {
            return null;
        }

        if (values.Count != 1)
        {
            throw new IssueException($"Option '--{name}' may be specified only once.");
        }

        return values[0];
    }

    private static IReadOnlyList<string> Options(IReadOnlyDictionary<string, List<string>> options, string name) =>
        options.TryGetValue(name, out var values) ? values : [];

    private static IEnumerable<string> SplitIds(IEnumerable<string> values) =>
        values.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static void PrintIssue(Issue issue, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(issue, JsonOptions));
            return;
        }

        Console.WriteLine($"{issue.Title}\nID: {issue.Id}\nStatus: {issue.Status.ToString().ToLowerInvariant()}\nReady: {issue.Ready}\nPriority: {issue.Priority.ToString().ToLowerInvariant()}");
        Console.WriteLine($"Tags: {string.Join(", ", issue.Tags)}");
        Console.WriteLine($"\n{issue.Body}");
    }

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
            Console.Out.WriteLine(JsonSerializer.Serialize(new { error = new { code = "invalid_request", message } }, JsonOptions));
        }
        else
        {
            Console.Error.WriteLine(message);
        }

        return message.Contains("changed since it was read", StringComparison.OrdinalIgnoreCase) ? 3 :
            message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No issue matches", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Free Issue - local Markdown issue and specification manager

            Commands:
              free-issue init [--project DIR]
              free-issue new --title TITLE (--body TEXT | --body-file FILE) [--priority VALUE] [--tag TAG]
              free-issue list [--status VALUE] [--tag TAG] [--priority VALUE] [--ready true|false] [--json]
              free-issue show ID [--json]
              free-issue edit ID [--title TITLE] [--body-file FILE] [--status VALUE] [--ready true|false] [--priority VALUE] [--tag TAG] [--parent ID|none] [--related ID,...] [--blocks ID,...]
              free-issue close|decline|reopen ID
              free-issue ready|unready ID
              free-issue search QUERY [--status VALUE] [--tag TAG] [--priority VALUE] [--ready true|false] [--json]
              free-issue next [--json]
              free-issue help

            Add --project DIR to find a project from an explicit directory.
            IDs may be full UUIDs or unambiguous prefixes. JSON errors use { error: { code, message } }.
            """);
    }
}
