using System.Text.Json;

namespace OpenIssue.Core;

public sealed record ProjectConfig(int FormatVersion);

public static class ProjectLocator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public const string DataDirectoryName = ".issues";
    public const string IssuesDirectoryName = "issues";
    public const string ConfigFileName = "config.json";
    public const int CurrentFormatVersion = 1;

    public static string Find(string? startPath = null)
    {
        var path = Path.GetFullPath(startPath ?? Environment.CurrentDirectory);
        if (File.Exists(path))
        {
            path = Path.GetDirectoryName(path)!;
        }

        var directory = new DirectoryInfo(path);
        while (directory is not null)
        {
            var dataPath = Path.Combine(directory.FullName, DataDirectoryName);
            if (Directory.Exists(dataPath) || File.Exists(dataPath))
            {
                RejectLink(dataPath);
                var configPath = Path.Combine(dataPath, ConfigFileName);
                if (!File.Exists(configPath))
                {
                    throw new IssueException($"Found '{dataPath}' but it has no '{ConfigFileName}'. Refusing to use another issue project.");
                }

                RejectLink(configPath);
                ReadConfig(configPath);
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new IssueException($"No Open Issue project found from '{path}'. Run 'issue init' in the project directory.");
    }

    public static string Initialize(string? projectPath = null)
    {
        var root = Path.GetFullPath(projectPath ?? Environment.CurrentDirectory);
        Directory.CreateDirectory(root);
        var dataPath = Path.Combine(root, DataDirectoryName);
        if (Directory.Exists(dataPath) || File.Exists(dataPath))
        {
            throw new IssueException($"Refusing to overwrite existing Open Issue data at '{dataPath}'.");
        }

        var temporaryPath = Path.Combine(root, $".{DataDirectoryName}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryPath);
        try
        {
            Directory.CreateDirectory(Path.Combine(temporaryPath, IssuesDirectoryName));
            var config = JsonSerializer.Serialize(new ProjectConfig(CurrentFormatVersion),
                new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
            File.WriteAllText(Path.Combine(temporaryPath, ConfigFileName), config + Environment.NewLine);
            File.WriteAllText(Path.Combine(temporaryPath, "next-id"), "1" + Environment.NewLine);
            Directory.Move(temporaryPath, dataPath);
            return root;
        }
        catch
        {
            if (Directory.Exists(temporaryPath))
            {
                Directory.Delete(temporaryPath, recursive: true);
            }

            throw;
        }
    }

    public static ProjectConfig ReadConfig(string configPath)
    {
        try
        {
            var config = JsonSerializer.Deserialize<ProjectConfig>(File.ReadAllText(configPath), JsonOptions)
                         ?? throw new JsonException("Configuration is empty.");
            if (config.FormatVersion != CurrentFormatVersion)
            {
                throw new IssueException(
                    $"Unsupported project format version {config.FormatVersion} in '{configPath}'. This program supports version {CurrentFormatVersion}.");
            }

            return config;
        }
        catch (IssueException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new IssueException($"Invalid project configuration '{configPath}': {exception.Message}");
        }
    }

    public static string GetIssuesDirectory(string projectRoot)
    {
        var dataPath = Path.Combine(Path.GetFullPath(projectRoot), DataDirectoryName);
        var issuesPath = Path.Combine(dataPath, IssuesDirectoryName);
        RejectLink(dataPath);
        RejectLink(issuesPath);
        return issuesPath;
    }

    internal static void RejectLink(string path)
    {
        var isLink = new FileInfo(path).LinkTarget is not null ||
                     new DirectoryInfo(path).LinkTarget is not null;
        if (!isLink && (File.Exists(path) || Directory.Exists(path)))
        {
            isLink = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        if (isLink)
        {
            throw new IssueException($"Symbolic links and reparse points are not supported: '{path}'.");
        }
    }
}
