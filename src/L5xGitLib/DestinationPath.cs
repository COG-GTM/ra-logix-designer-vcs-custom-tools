namespace L5xGitLib;

/// <summary>
/// Rules for the Git destination directory named in an <c>_L5xGit.yml</c> file.
/// The file sits beside the ACD and travels with project bundles, so its value is untrusted.
/// </summary>
public static class DestinationPath
{
    /// <summary>
    /// Returns the normalized absolute form of <paramref name="path"/>, or <c>null</c> with an
    /// <paramref name="error"/> when the path is empty, relative, or a device path (<c>\\?\</c>, <c>\\.\</c>).
    /// Relative paths are rejected because they would resolve against whatever directory the
    /// tool happens to be started from.
    /// </summary>
    public static string? TryNormalize(string? path, out string? error)
    {
        error = null;
        var trimmed = path?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            error = "destination path is empty";
            return null;
        }

        if (IsDevicePath(trimmed))
        {
            error = $"'{trimmed}' is a device path";
            return null;
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            error = $"'{trimmed}' is not an absolute path";
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"'{trimmed}' is not a valid path: {ex.Message}";
            return null;
        }

        return TrimTrailingSeparators(fullPath);
    }

    public static bool AreSame(string left, string right) =>
        string.Equals(TrimTrailingSeparators(left), TrimTrailingSeparators(right), PathComparison);

    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsDevicePath(string path) =>
        path.Length >= 3 && IsSeparator(path[0]) && IsSeparator(path[1]) && (path[2] == '?' || path[2] == '.');

    private static bool IsSeparator(char c) => c == '\\' || c == '/';

    private static string TrimTrailingSeparators(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }
}
