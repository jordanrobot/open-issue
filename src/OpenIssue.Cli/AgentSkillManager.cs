using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenIssue.Core;

namespace OpenIssue.Cli;

internal enum AgentHarness
{
    Copilot,
    Codex,
    Claude
}

internal sealed class AgentSkillManager
{
    private const string ManifestFileName = "manifest.json";
    private const string StateFileName = "agent-skills.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _packageSkillsDirectory;
    private readonly string _stateFilePath;
    private readonly string _userProfileDirectory;

    public AgentSkillManager(string packageSkillsDirectory, string stateDirectory, string userProfileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSkillsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);

        _packageSkillsDirectory = Path.GetFullPath(packageSkillsDirectory);
        _stateFilePath = Path.Combine(Path.GetFullPath(stateDirectory), StateFileName);
        _userProfileDirectory = Path.GetFullPath(userProfileDirectory);
    }

    public static AgentSkillManager CreateForCurrentUser()
    {
        var userProfileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(userProfileDirectory) || string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new IssueException("The current user's profile and local application data directories could not be determined.");
        }

        return new(
            Path.Combine(AppContext.BaseDirectory, "skills"),
            Path.Combine(localApplicationData, "OpenIssue"),
            userProfileDirectory);
    }

    public static string GetHarnessName(AgentHarness harness) =>
        harness switch
        {
            AgentHarness.Copilot => "copilot",
            AgentHarness.Codex => "codex",
            AgentHarness.Claude => "claude",
            _ => throw new IssueException($"Unsupported harness value '{harness}'.")
        };

    public IReadOnlyList<AgentHarness> SynchronizeDetected()
    {
        var harnesses = Enum.GetValues<AgentHarness>()
            .Where(IsHarnessDetected)
            .ToArray();
        Synchronize(harnesses);
        return harnesses;
    }

    private void Synchronize(IReadOnlyCollection<AgentHarness> harnesses)
    {
        ArgumentNullException.ThrowIfNull(harnesses);

        var targetHarnesses = harnesses.Distinct().ToArray();
        var skills = ReadManifest();
        var previousInstallations = ReadInstallations() ?? [];
        var trackedInstallations = previousInstallations.ToHashSet();
        var desiredInstallations = targetHarnesses
            .SelectMany(harness => skills.Select(skill => new SkillInstallation(GetHarnessName(harness), skill.Name)))
            .ToHashSet();
        var skillsByName = skills.ToDictionary(skill => skill.Name, StringComparer.Ordinal);

        foreach (var installation in desiredInstallations)
        {
            var skill = skillsByName[installation.Skill];
            ReplaceSkillDirectory(
                skill.SourceDirectory,
                GetInstalledSkillDirectory(ParseHarnessName(installation.Harness), installation.Skill),
                () =>
                {
                    if (trackedInstallations.Add(installation))
                    {
                        WriteInstallations(trackedInstallations);
                    }
                });
        }

        foreach (var staleInstallation in trackedInstallations.Except(desiredInstallations).ToArray())
        {
            RemoveSkillDirectory(
                ParseHarnessName(staleInstallation.Harness),
                staleInstallation.Skill);
            trackedInstallations.Remove(staleInstallation);
            WriteInstallations(trackedInstallations);
        }

        if (desiredInstallations.Count == 0)
        {
            if (File.Exists(_stateFilePath))
            {
                File.Delete(_stateFilePath);
            }
        }
        else
        {
            WriteInstallations(desiredInstallations);
        }
    }

    public bool Uninstall()
    {
        var installations = ReadInstallations();
        if (installations is null)
        {
            return false;
        }

        foreach (var installation in installations)
        {
            RemoveSkillDirectory(ParseHarnessName(installation.Harness), installation.Skill);
        }

        File.Delete(_stateFilePath);
        return true;
    }

    private IReadOnlyList<SkillDefinition> ReadManifest()
    {
        var manifestPath = Path.Combine(_packageSkillsDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new IssueException($"The packaged agent skill manifest was not found at '{manifestPath}'.");
        }

        SkillManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SkillManifest>(File.ReadAllText(manifestPath), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new IssueException($"The packaged agent skill manifest is invalid: {exception.Message}");
        }

        if (manifest?.Skills is null || manifest.Skills.Count == 0)
        {
            throw new IssueException("The packaged agent skill manifest must contain at least one skill.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var skills = new List<SkillDefinition>();
        foreach (var entry in manifest.Skills)
        {
            if (entry is null)
            {
                throw new IssueException("The packaged agent skill manifest contains an empty entry.");
            }

            var name = ValidateSkillName(entry.Name);
            if (!names.Add(name))
            {
                throw new IssueException($"The packaged agent skill manifest contains duplicate skill '{name}'.");
            }

            var sourceDirectory = ResolvePackageSkillDirectory(entry.Directory);
            if (!Directory.Exists(sourceDirectory) || !File.Exists(Path.Combine(sourceDirectory, "SKILL.md")))
            {
                throw new IssueException($"The packaged skill '{name}' is missing its directory or SKILL.md file.");
            }

            skills.Add(new(name, sourceDirectory));
        }

        return skills;
    }

    private bool IsHarnessDetected(AgentHarness harness)
    {
        var configurationDirectoryName = harness switch
        {
            AgentHarness.Copilot => ".copilot",
            AgentHarness.Codex => ".codex",
            AgentHarness.Claude => ".claude",
            _ => throw new IssueException($"Unsupported harness value '{harness}'.")
        };

        return Directory.Exists(Path.Combine(_userProfileDirectory, configurationDirectoryName)) ||
               Directory.Exists(GetHarnessSkillsDirectory(harness));
    }

    private List<SkillInstallation>? ReadInstallations()
    {
        if (!File.Exists(_stateFilePath))
        {
            return null;
        }

        PersistedInstallationState? persistedState;
        try
        {
            persistedState = JsonSerializer.Deserialize<PersistedInstallationState>(
                File.ReadAllText(_stateFilePath),
                JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new IssueException($"The saved agent skill state is invalid: {exception.Message}");
        }

        if (persistedState?.Installations is null)
        {
            throw new IssueException("The saved agent skill state does not contain an installations list.");
        }

        var installations = new HashSet<SkillInstallation>();
        foreach (var entry in persistedState.Installations)
        {
            if (entry is null || entry.Harness is null || entry.Skill is null)
            {
                throw new IssueException("The saved agent skill state contains an incomplete installation entry.");
            }

            var harness = GetHarnessName(ParseHarnessName(entry.Harness));
            var skill = ValidateSkillName(entry.Skill);
            installations.Add(new(harness, skill));
        }

        return installations.ToList();
    }

    private string ResolvePackageSkillDirectory(string? relativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory))
        {
            throw new IssueException("Every packaged skill must specify a relative source directory.");
        }

        var resolvedDirectory = Path.GetFullPath(Path.Combine(_packageSkillsDirectory, relativeDirectory));
        var relativePath = Path.GetRelativePath(_packageSkillsDirectory, resolvedDirectory);
        if (relativePath == "." ||
            Path.IsPathRooted(relativePath) ||
            relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new IssueException($"The packaged skill source directory '{relativeDirectory}' is outside the skills directory.");
        }

        return resolvedDirectory;
    }

    private string GetHarnessSkillsDirectory(AgentHarness harness) =>
        harness switch
        {
            AgentHarness.Copilot => Path.Combine(_userProfileDirectory, ".copilot", "skills"),
            AgentHarness.Codex => Path.Combine(_userProfileDirectory, ".agents", "skills"),
            AgentHarness.Claude => Path.Combine(_userProfileDirectory, ".claude", "skills"),
            _ => throw new IssueException($"Unsupported harness value '{harness}'.")
        };

    private string GetInstalledSkillDirectory(AgentHarness harness, string skillName) =>
        Path.Combine(GetHarnessSkillsDirectory(harness), ValidateSkillName(skillName));

    private static AgentHarness ParseHarnessName(string harnessName) =>
        harnessName.ToLowerInvariant() switch
        {
            "copilot" => AgentHarness.Copilot,
            "codex" => AgentHarness.Codex,
            "claude" => AgentHarness.Claude,
            _ => throw new IssueException($"The saved agent skill state contains unknown harness '{harnessName}'.")
        };

    private static string ValidateSkillName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > 64 ||
            !char.IsAsciiLetterLower(name[0]) ||
            name.Any(character => !char.IsAsciiLetterLower(character) &&
                                  !char.IsAsciiDigit(character) &&
                                  character != '-'))
        {
            throw new IssueException($"Invalid managed skill name '{name}'.");
        }

        return name;
    }

    private void ReplaceSkillDirectory(string sourceDirectory, string destinationDirectory, Action afterInstall)
    {
        var parentDirectory = Path.GetDirectoryName(destinationDirectory)
            ?? throw new IssueException($"Could not resolve the destination for '{destinationDirectory}'.");
        Directory.CreateDirectory(parentDirectory);

        if (File.Exists(destinationDirectory))
        {
            throw new IssueException($"A file already exists where the skill directory should be: '{destinationDirectory}'.");
        }

        if (Directory.Exists(destinationDirectory))
        {
            EnsureNotReparsePoint(destinationDirectory);
        }

        var stagingDirectory = Path.Combine(parentDirectory, $".openissue-skill-{Guid.NewGuid():N}");
        var backupDirectory = Path.Combine(parentDirectory, $".openissue-backup-{Guid.NewGuid():N}");
        var destinationBackedUp = false;
        var destinationInstalled = false;
        try
        {
            CopyDirectory(sourceDirectory, stagingDirectory);
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Move(destinationDirectory, backupDirectory);
                destinationBackedUp = true;
            }

            try
            {
                Directory.Move(stagingDirectory, destinationDirectory);
                destinationInstalled = true;
                afterInstall();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (destinationInstalled)
                {
                    Directory.Delete(destinationDirectory, recursive: true);
                }

                if (destinationBackedUp)
                {
                    Directory.Move(backupDirectory, destinationDirectory);
                }

                throw;
            }

            if (destinationBackedUp)
            {
                Directory.Delete(backupDirectory, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        EnsureNotReparsePoint(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IssueException($"The packaged skill contains a linked file or directory: '{entry}'.");
            }

            var destination = Path.Combine(destinationDirectory, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                CopyDirectory(entry, destination);
            }
            else
            {
                File.Copy(entry, destination);
            }
        }
    }

    private void RemoveSkillDirectory(AgentHarness harness, string skillName)
    {
        var directory = GetInstalledSkillDirectory(harness, skillName);
        if (File.Exists(directory))
        {
            throw new IssueException($"A file exists where a managed skill directory should be: '{directory}'.");
        }

        if (!Directory.Exists(directory))
        {
            return;
        }

        EnsureNotReparsePoint(directory);
        Directory.Delete(directory, recursive: true);
    }

    private static void EnsureNotReparsePoint(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IssueException($"Refusing to replace or remove linked skill directory '{directory}'.");
        }
    }

    private void WriteInstallations(IEnumerable<SkillInstallation> installations)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
        var temporaryPath = $"{_stateFilePath}.{Guid.NewGuid():N}.tmp";
        var state = new PersistedInstallationState
        {
            Installations = installations
                .OrderBy(installation => installation.Harness, StringComparer.Ordinal)
                .ThenBy(installation => installation.Skill, StringComparer.Ordinal)
                .Select(installation => new PersistedSkillInstallation
                {
                    Harness = installation.Harness,
                    Skill = installation.Skill
                })
                .ToList()
        };

        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, _stateFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record SkillDefinition(string Name, string SourceDirectory);

    private sealed record SkillInstallation(string Harness, string Skill);

    private sealed class SkillManifest
    {
        [JsonPropertyName("skills")]
        public List<SkillManifestEntry?>? Skills { get; init; }
    }

    private sealed class SkillManifestEntry
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("directory")]
        public string? Directory { get; init; }
    }

    private sealed class PersistedInstallationState
    {
        [JsonPropertyName("installations")]
        public List<PersistedSkillInstallation>? Installations { get; init; }
    }

    private sealed class PersistedSkillInstallation
    {
        [JsonPropertyName("harness")]
        public string? Harness { get; init; }

        [JsonPropertyName("skill")]
        public string? Skill { get; init; }
    }
}
