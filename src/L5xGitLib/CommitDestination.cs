namespace L5xGitLib;

/// <summary>
/// Validation of the commit destination read from an ACD's <c>_L5xGit.yml</c> sidecar. The sidecar sits next to the
/// ACD, so anyone who can write to that folder can change it; the destination therefore has to be checked against
/// what this user previously confirmed before anything is deleted or committed there.
/// </summary>
public sealed record CommitDestination(
    string AcdPath,
    string DestinationPath,
    string? RepoRoot,
    bool RepoRootChecked,
    bool IsNetworkPath,
    bool PromptForCommitMessage,
    bool IsTrusted)
{
    /// <summary>
    /// True when the Git repository that would receive the commit is rooted above the destination
    /// (i.e. repository discovery walked up to an ancestor directory).
    /// </summary>
    public bool RepoRootIsAncestor => RepoRoot is not null && !PathsEqual(RepoRoot, DestinationPath);

    /// <param name="discoverRepoRoot">
    /// Returns the working directory of the Git repository containing a path, or null. It is not called for an
    /// unconfirmed network destination, so the server it names is not contacted before the user confirms it.
    /// </param>
    public static CommitDestination Evaluate(
        string acdPath,
        string configuredDestination,
        bool promptForCommitMessage,
        TrustedDestinationStore trustStore,
        Func<string, string?> discoverRepoRoot)
    {
        var fullAcdPath = NormalizeAcdPath(acdPath);
        var destination = NormalizeDestination(configuredDestination);
        var isNetworkPath = IsNetwork(configuredDestination.Trim()) || IsNetwork(destination);

        var checkRepoRoot = !isNetworkPath || trustStore.HasDestination(fullAcdPath, destination);
        var repoRoot = checkRepoRoot ? NormalizeRepoRoot(discoverRepoRoot(destination)) : null;

        return new CommitDestination(
            AcdPath: fullAcdPath,
            DestinationPath: destination,
            RepoRoot: repoRoot,
            RepoRootChecked: checkRepoRoot,
            IsNetworkPath: isNetworkPath,
            PromptForCommitMessage: promptForCommitMessage,
            IsTrusted: trustStore.IsTrusted(fullAcdPath, destination, repoRoot, promptForCommitMessage));
    }

    public static string? NormalizeRepoRoot(string? repoRoot) =>
        string.IsNullOrEmpty(repoRoot) ? null : TrimSeparators(Path.GetFullPath(repoRoot));

    /// <summary>
    /// Returns the absolute, separator-trimmed form of a configured destination. Relative paths are rejected rather
    /// than resolved against whatever the current directory happens to be.
    /// </summary>
    public static string NormalizeDestination(string? configuredDestination)
    {
        if (string.IsNullOrWhiteSpace(configuredDestination))
        {
            throw new InvalidCommitDestinationException("The configuration file does not specify a destination_path.");
        }

        var trimmed = configuredDestination.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new InvalidCommitDestinationException($"destination_path '{trimmed}' is not an absolute path.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidCommitDestinationException($"destination_path '{trimmed}' is not a valid path: {ex.Message}");
        }

        return TrimSeparators(fullPath);
    }

    public static string NormalizeAcdPath(string acdPath) => Path.GetFullPath(acdPath);

    /// <summary>
    /// UNC (<c>\\server\share</c>), device (<c>\\?\</c>, <c>\\.\</c>) and mapped network drive paths.
    /// </summary>
    public static bool IsNetwork(string fullPath)
    {
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool PathsEqual(string left, string right) =>
        string.Equals(TrimSeparators(left), TrimSeparators(right), PathComparison);

    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string TrimSeparators(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }
}

public sealed class InvalidCommitDestinationException(string message) : Exception(message);
