using System.Text.RegularExpressions;

namespace L5xploderLib;

public static class FileHelpers
{
    /// <summary>
    /// Creates the directory that will contain the given file, including any missing parent levels.
    /// Does nothing if it already exists.
    /// </summary>
    public static void CreateDirectoryForFile(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    // Windows treats these as devices rather than files, with or without an extension.
    private static readonly Regex ReservedDeviceName = new(
        @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9\u00B9\u00B2\u00B3]|LPT[0-9\u00B9\u00B2\u00B3])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string VirtualRoot =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "l5xploder-virtual-root"));

    /// <summary>
    /// Turns an arbitrary element name into a single file name segment that is safe on Windows.
    /// Invalid characters, and trailing dots or spaces (which Windows strips, and which make up
    /// '.' and '..'), become '_'. Reserved device names (CON, NUL, COM1, ...) get a '_' appended
    /// to the device part. Names Logix Designer accepts are returned unchanged.
    /// </summary>
    public static string ToSafeFileName(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(InvalidFileNameChars, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        for (int i = chars.Length - 1; i >= 0 && (chars[i] == '.' || chars[i] == ' '); i--)
        {
            chars[i] = '_';
        }

        var safeName = new string(chars);

        var dotIndex = safeName.IndexOf('.');
        var stem = dotIndex < 0 ? safeName : safeName[..dotIndex];
        if (ReservedDeviceName.IsMatch(stem.TrimEnd(' ')))
        {
            safeName = stem + "_" + safeName[stem.Length..];
        }

        return safeName;
    }

    /// <summary>
    /// Lexically resolves '.' and '..' segments in a relative path so that two spellings of the
    /// same location compare equal. Does not touch the file system.
    /// </summary>
    public static string NormalizeRelativePath(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(VirtualRoot, relativePath));
        return Path.GetRelativePath(VirtualRoot, fullPath);
    }

    /// <summary>
    /// True when <paramref name="relativePath"/> resolves strictly inside <paramref name="relativeContainer"/>.
    /// </summary>
    public static bool IsStrictlyInside(string relativeContainer, string relativePath)
    {
        var container = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(VirtualRoot, relativeContainer)));
        var fullPath = Path.GetFullPath(Path.Combine(VirtualRoot, relativePath));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return fullPath.StartsWith(container + Path.DirectorySeparatorChar, comparison)
            && fullPath.Length > container.Length + 1;
    }
}
