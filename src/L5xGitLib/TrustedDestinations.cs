using YamlDotNet.Serialization;

namespace L5xGitLib;

/// <summary>
/// Per-user record of which Git destination the user approved for each ACD file.
/// Stored outside the project folder so a configuration file shipped next to an ACD
/// cannot approve its own destination.
/// </summary>
public sealed class TrustedDestinations
{
    private readonly string storePath;

    public TrustedDestinations(string storePath)
    {
        this.storePath = storePath;
    }

    public static TrustedDestinations ForCurrentUser() => new(DefaultStorePath);

    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "l5xgit",
        "trusted_destinations.yml");

    public string StorePath => storePath;

    public bool IsTrusted(string acdPath, string destinationPath)
    {
        var acd = Path.GetFullPath(acdPath);
        return Load().Any(entry =>
            entry.AcdPath is not null &&
            entry.DestinationPath is not null &&
            string.Equals(entry.AcdPath, acd, DestinationPath.PathComparison) &&
            DestinationPath.AreSame(entry.DestinationPath, destinationPath));
    }

    /// <summary>
    /// Records <paramref name="destinationPath"/> as the approved destination for <paramref name="acdPath"/>,
    /// replacing any destination previously approved for that ACD.
    /// </summary>
    public void Trust(string acdPath, string destinationPath)
    {
        var acd = Path.GetFullPath(acdPath);

        var directory = Path.GetDirectoryName(storePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Serialize read-modify-write across l5xgit processes so concurrent approvals are not lost.
        using var storeLock = AcquireLock();

        var entries = Load()
            .Where(entry => !string.Equals(entry.AcdPath, acd, DestinationPath.PathComparison))
            .ToList();
        entries.Add(new Entry { AcdPath = acd, DestinationPath = destinationPath });

        var yaml = new SerializerBuilder()
            .WithIndentedSequences()
            .Build()
            .Serialize(new StoreFile { Entries = entries });

        var tempPath = $"{storePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, yaml);
            File.Move(tempPath, storePath, overwrite: true);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private FileStream AcquireLock()
    {
        var lockPath = storePath + ".lock";
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }

    private List<Entry> Load()
    {
        if (!File.Exists(storePath))
        {
            return [];
        }

        try
        {
            var file = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<StoreFile?>(File.ReadAllText(storePath));
            return file?.Entries ?? [];
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // An unreadable store approves nothing; the user is asked again and the file is rewritten.
            return [];
        }
    }

    private sealed class StoreFile
    {
        [YamlMember(Alias = "trusted_destinations")]
        public List<Entry> Entries { get; set; } = [];
    }

    private sealed class Entry
    {
        [YamlMember(Alias = "acd_path")]
        public string? AcdPath { get; set; }

        [YamlMember(Alias = "destination_path")]
        public string? DestinationPath { get; set; }
    }
}
