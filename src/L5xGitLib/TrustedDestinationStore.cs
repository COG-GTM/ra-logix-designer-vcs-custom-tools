using YamlDotNet.Serialization;

namespace L5xGitLib;

/// <summary>
/// Per-user record of the commit destinations the user has confirmed for each ACD. It lives in the user's local
/// application data folder, outside the ACD's directory, so whoever can write the ACD's sidecar cannot also mark
/// their destination as confirmed.
/// </summary>
public sealed class TrustedDestinationStore
{
    private readonly string filePath;

    public TrustedDestinationStore(string filePath)
    {
        this.filePath = filePath;
    }

    public static string DefaultFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "L5xGit",
            "trusted-destinations.yml");

    public static TrustedDestinationStore ForCurrentUser() => new(DefaultFilePath);

    /// <summary>
    /// True when the user has confirmed exactly this destination, committing into exactly this repository root,
    /// for this ACD. A changed destination or a repository that now resolves to a different root is not trusted.
    /// </summary>
    public bool IsTrusted(string acdPath, string destinationPath, string? repoRoot)
    {
        if (repoRoot is null)
        {
            return false;
        }

        return Load().Any(entry =>
            CommitDestination.PathsEqual(entry.AcdPath, acdPath) &&
            CommitDestination.PathsEqual(entry.DestinationPath, destinationPath) &&
            CommitDestination.PathsEqual(entry.RepoRoot, repoRoot));
    }

    /// <summary>
    /// True when the user has confirmed this destination for this ACD, whatever repository it resolved to.
    /// </summary>
    public bool HasDestination(string acdPath, string destinationPath) =>
        Load().Any(entry =>
            CommitDestination.PathsEqual(entry.AcdPath, acdPath) &&
            CommitDestination.PathsEqual(entry.DestinationPath, destinationPath));

    public void Trust(string acdPath, string destinationPath, string repoRoot)
    {
        var entries = Load()
            .Where(entry => !CommitDestination.PathsEqual(entry.AcdPath, acdPath))
            .ToList();

        entries.Add(new TrustedDestination
        {
            AcdPath = acdPath,
            DestinationPath = destinationPath,
            RepoRoot = repoRoot,
        });

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var serializer = new SerializerBuilder()
            .WithIndentedSequences()
            .Build();

        var tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, serializer.Serialize(new TrustedDestinationFile { Destinations = entries }));
        File.Move(tempPath, filePath, overwrite: true);
    }

    private List<TrustedDestination> Load()
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();

        var file = deserializer.Deserialize<TrustedDestinationFile?>(File.ReadAllText(filePath));
        return file?.Destinations?
            .Where(entry => !string.IsNullOrWhiteSpace(entry.AcdPath)
                && !string.IsNullOrWhiteSpace(entry.DestinationPath)
                && !string.IsNullOrWhiteSpace(entry.RepoRoot))
            .ToList() ?? [];
    }

    private sealed class TrustedDestinationFile
    {
        [YamlMember(Alias = "destinations")]
        public List<TrustedDestination>? Destinations { get; set; }
    }

    private sealed class TrustedDestination
    {
        [YamlMember(Alias = "acd_path")]
        public string AcdPath { get; set; } = string.Empty;

        [YamlMember(Alias = "destination_path")]
        public string DestinationPath { get; set; } = string.Empty;

        [YamlMember(Alias = "repo_root")]
        public string RepoRoot { get; set; } = string.Empty;
    }
}
