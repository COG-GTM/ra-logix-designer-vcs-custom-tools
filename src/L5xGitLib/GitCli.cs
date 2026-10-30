using System.Diagnostics;

namespace L5xGitLib;

/// <summary>
/// Helpers for launching the git command line safely inside a repository whose
/// <c>.git/config</c> was not necessarily written by the current user.
/// </summary>
public static class GitCli
{
    // Repository-level keys that make 'git difftool --dir-diff' launch a program or load
    // further configuration. Diff tools belong in the user's global configuration.
    private static readonly string[] DifftoolCommandKeys =
    [
        "diff.tool", "diff.guitool", "diff.external",
        "merge.tool", "merge.guitool",
        "core.pager",
    ];

    private static readonly string[] DifftoolCommandKeyPrefixes =
    [
        "difftool.", "mergetool.", "filter.", "pager.", "include.", "includeif.",
    ];

    /// <summary>
    /// Finds git on PATH, considering only absolute PATH entries. The result is passed to
    /// <see cref="ProcessStartInfo.FileName"/> as a full path, so Windows never falls back to a
    /// <c>git.exe</c> in the current or application directory.
    /// </summary>
    public static string? ResolveExecutable(string? pathVariable = null)
    {
        pathVariable ??= Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var fileName = OperatingSystem.IsWindows() ? "git.exe" : "git";

        foreach (var rawEntry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = rawEntry.Trim('"');
            if (!Path.IsPathFullyQualified(entry))
            {
                continue;
            }

            var candidate = Path.Combine(entry, fileName);
            if (File.Exists(candidate) && IsExecutable(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the keys from <paramref name="repositoryConfigKeys"/> (keys set by the repository's
    /// own configuration, not the user's global or system configuration) that can make
    /// <c>git difftool</c> run a program.
    /// </summary>
    public static IReadOnlyList<string> FindDifftoolCommandSettings(IEnumerable<string> repositoryConfigKeys) =>
        repositoryConfigKeys
            .Where(IsDifftoolCommandSetting)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsDifftoolCommandSetting(string key)
    {
        var lowerKey = key.ToLowerInvariant();

        // diff.<driver>.command and diff.<driver>.textconv run programs chosen by .gitattributes.
        if (lowerKey.StartsWith("diff.", StringComparison.Ordinal) &&
            (lowerKey.EndsWith(".command", StringComparison.Ordinal) || lowerKey.EndsWith(".textconv", StringComparison.Ordinal)))
        {
            return true;
        }

        return DifftoolCommandKeys.Contains(lowerKey) ||
               DifftoolCommandKeyPrefixes.Any(prefix => lowerKey.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds the 'git difftool' invocation comparing HEAD~1 with HEAD. core.fsmonitor is forced
    /// off on the command line, which takes precedence over any repository setting.
    /// </summary>
    public static ProcessStartInfo CreateDifftoolStartInfo(string gitExecutablePath, string repoRoot)
    {
        if (!Path.IsPathFullyQualified(gitExecutablePath))
        {
            throw new ArgumentException("The git executable must be given as a full path.", nameof(gitExecutablePath));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = gitExecutablePath,
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[] { "-c", "core.fsmonitor=false", "difftool", "--dir-diff", "--no-prompt", "HEAD~1", "HEAD" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & anyExecute) != 0;
    }
}
