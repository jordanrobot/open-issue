using System.Diagnostics;
using System.IO;

namespace OpenIssue.Desktop;

internal enum ColorThemePreference
{
    System,
    Light,
    Dark,
    Dracula
}

internal sealed class ColorThemePreferenceStore
{
    private readonly string _path;

    public ColorThemePreferenceStore()
        : this(GetDefaultPath())
    {
    }

    internal ColorThemePreferenceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    internal ColorThemePreference Load()
    {
        if (!File.Exists(_path))
        {
            return ColorThemePreference.System;
        }

        var value = File.ReadAllText(_path).Trim();
        if (string.Equals(value, "MaterialDark", StringComparison.OrdinalIgnoreCase))
        {
            return ColorThemePreference.Dark;
        }

        if (Enum.TryParse(value, ignoreCase: true, out ColorThemePreference preference) &&
            Enum.IsDefined(preference))
        {
            return preference;
        }

        Trace.TraceWarning("Ignoring invalid color theme preference in '{0}'.", _path);
        return ColorThemePreference.System;
    }

    internal void Save(ColorThemePreference preference)
    {
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The color theme preference path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $"color-theme.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, preference.ToString());
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetDefaultPath()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The local application data directory is unavailable.");
        }

        return Path.Combine(localApplicationData, "OpenIssue", "color-theme.txt");
    }
}
