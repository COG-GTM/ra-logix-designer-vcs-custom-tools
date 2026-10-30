using System.Globalization;
using System.Text;
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

    private const char EscapeChar = '%';

    /// <summary>
    /// Turns an arbitrary element name into a single file name segment that is safe on Windows.
    /// Names that are already safe and contain no '%' are returned unchanged. Otherwise '%',
    /// invalid characters, trailing dots or spaces (which Windows strips, and which make up
    /// '.' and '..') and the first character of a reserved device name (CON, NUL, COM1, ...)
    /// are percent-encoded. The encoding is reversible, so distinct names never collide, and
    /// every rewritten name contains a '%', so it can't collide with an unchanged name either.
    /// </summary>
    public static string ToSafeFileName(string name)
    {
        var escape = new bool[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            escape[i] = name[i] == EscapeChar || Array.IndexOf(InvalidFileNameChars, name[i]) >= 0;
        }

        for (int i = name.Length - 1; i >= 0 && (name[i] == '.' || name[i] == ' '); i--)
        {
            escape[i] = true;
        }

        var dotIndex = name.IndexOf('.');
        var stem = dotIndex < 0 ? name : name[..dotIndex];
        if (stem.Length > 0 && ReservedDeviceName.IsMatch(stem.TrimEnd(' ')))
        {
            escape[0] = true;
        }

        if (!escape.Contains(true))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            if (escape[i])
            {
                foreach (var b in Encoding.UTF8.GetBytes(name[i].ToString()))
                {
                    builder.Append(EscapeChar).Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }
            else
            {
                builder.Append(name[i]);
            }
        }

        return builder.ToString();
    }

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
