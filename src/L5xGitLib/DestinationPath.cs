namespace L5xGitLib;

/// <summary>
/// Rules for the Git destination directory named in an <c>_L5xGit.yml</c> file.
/// The file sits beside the ACD and travels with project bundles, so its value is untrusted.
/// </summary>
public static class DestinationPath
{
    /// <summary>
    /// Returns the normalized absolute form of <paramref name="path"/>, or <c>null</c> with an
    /// <paramref name="error"/> when the path is empty, relative, a UNC/network path or a device path.
    /// Does not touch the file system, so it is safe to call before the user has approved the path.
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

        if (IsUncOrDevicePath(trimmed))
        {
            error = $"'{trimmed}' is a network (UNC) or device path; only local directories are supported";
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

        if (IsUncOrDevicePath(fullPath))
        {
            error = $"'{trimmed}' is a network (UNC) or device path; only local directories are supported";
            return null;
        }

        return TrimTrailingSeparators(fullPath);
    }

    public static bool AreSame(string left, string right) =>
        string.Equals(TrimTrailingSeparators(left), TrimTrailingSeparators(right), PathComparison);

    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsUncOrDevicePath(string path) =>
        path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);

    private static bool IsSeparator(char c) => c == '\\' || c == '/';

    private static string TrimTrailingSeparators(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }
}
