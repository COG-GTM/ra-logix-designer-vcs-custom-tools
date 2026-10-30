using System.Text.Json;
using System.Text.Json.Serialization;

namespace L5xGitLib;

/// <summary>
/// Per-user record of the ACD-to-destination mappings this user has confirmed on this computer.
/// It lives outside the project folder so that an <c>_L5xGit.yml</c> shipped alongside an ACD cannot vouch for itself.
/// </summary>
public sealed class TrustedDestinations
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string storePath;

    public TrustedDestinations(string storePath)
    {
        this.storePath = Path.GetFullPath(storePath);
    }

    public string StorePath => storePath;

    public static string DefaultStorePath
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrEmpty(localAppData))
            {
                throw new InvalidOperationException("Unable to locate the per-user application data folder for the l5xgit trusted destinations list.");
            }

            return Path.Combine(localAppData, "l5xgit", "trusted-destinations.json");
        }
    }

    public static TrustedDestinations ForCurrentUser() => new(DefaultStorePath);

    public bool IsTrusted(string acdPath, string destinationPath)
    {
        var acd = Normalize(acdPath);
        var destination = Normalize(destinationPath);
        return Load().Any(e => string.Equals(e.AcdPath, acd, PathComparison) && string.Equals(e.DestinationPath, destination, PathComparison));
    }

    /// <summary>
    /// Records <paramref name="destinationPath"/> as the confirmed destination for <paramref name="acdPath"/>,
    /// replacing any destination previously confirmed for that ACD.
    /// </summary>
    public void Trust(string acdPath, string destinationPath)
    {
        var acd = Normalize(acdPath);
        var destination = Normalize(destinationPath);

        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);

        // Serializes the read-modify-write across l5xgit processes so concurrent confirmations are not lost.
        using var storeLock = AcquireLock();

        var entries = Load().Where(e => !string.Equals(e.AcdPath, acd, PathComparison)).ToList();
        entries.Add(new Entry { AcdPath = acd, DestinationPath = destination });

        var tempPath = $"{storePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(tempPath, storePath, overwrite: true);
    }

    private FileStream AcquireLock()
    {
        var lockPath = storePath + ".lock";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 200)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private List<Entry> Load()
    {
        if (!File.Exists(storePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(storePath), JsonOptions)?
                .Where(e => !string.IsNullOrEmpty(e.AcdPath) && !string.IsNullOrEmpty(e.DestinationPath))
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            // An unreadable list only means every destination gets confirmed again.
            return [];
        }
    }

    private sealed class Entry
    {
        [JsonPropertyName("acd_path")]
        public string AcdPath { get; init; } = string.Empty;

        [JsonPropertyName("destination_path")]
        public string DestinationPath { get; init; } = string.Empty;
    }
}
