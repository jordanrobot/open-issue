using System.Text.Json;

namespace FreeIssue.Core;

public sealed record ProjectConfig(int FormatVersion);

public static class ProjectLocator
{
    public const string DataDirectoryName = ".free-issue";
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
            var configPath = Path.Combine(directory.FullName, DataDirectoryName, ConfigFileName);
            if (File.Exists(configPath))
            {
                ReadConfig(configPath);
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new IssueException($"No Free Issue project found from '{path}'. Run 'free-issue init' or pass --project.");
    }

    public static string Initialize(string? projectPath = null)
    {
        var root = Path.GetFullPath(projectPath ?? Environment.CurrentDirectory);
        Directory.CreateDirectory(root);
        var dataPath = Path.Combine(root, DataDirectoryName);
        if (Directory.Exists(dataPath) || File.Exists(dataPath))
        {
            throw new IssueException($"Refusing to overwrite existing Free Issue data at '{dataPath}'.");
        }

        Directory.CreateDirectory(dataPath);
        try
        {
            Directory.CreateDirectory(Path.Combine(dataPath, IssuesDirectoryName));
            var config = JsonSerializer.Serialize(new ProjectConfig(CurrentFormatVersion),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(dataPath, ConfigFileName), config + Environment.NewLine);
            return root;
        }
        catch
        {
            Directory.Delete(dataPath, recursive: true);
            throw;
        }
    }

    public static ProjectConfig ReadConfig(string configPath)
    {
        try
        {
            var config = JsonSerializer.Deserialize<ProjectConfig>(File.ReadAllText(configPath))
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
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IssueException($"Symbolic links and reparse points are not supported: '{path}'.");
        }
    }
}
