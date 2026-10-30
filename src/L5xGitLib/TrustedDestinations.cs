using System.Text.Json;
using System.Text.Json.Serialization;

namespace L5xGitLib;

/// <summary>
/// Per-user record of which Git destination the user approved for each ACD file.
/// Stored under the user's profile, outside any project folder, so an <c>_L5xGit.yml</c>
/// shipped next to an ACD cannot approve its own destination.
/// </summary>
public sealed class TrustedDestinations
{
    /// <summary>
    /// Overrides the store location; used by the e2e tests to keep the real store untouched.
    /// </summary>
    public const string StorePathEnvironmentVariable = "L5XGIT_TRUSTED_DESTINATIONS_FILE";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public TrustedDestinations(string storePath)
    {
        StorePath = storePath;
    }

    public string StorePath { get; }

    public static TrustedDestinations ForCurrentUser()
    {
        var overridePath = Environment.GetEnvironmentVariable(StorePathEnvironmentVariable);
        return new(string.IsNullOrWhiteSpace(overridePath) ? DefaultStorePath : overridePath);
    }

    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "l5xgit",
        "trusted_destinations.json");

    public bool IsTrusted(string acdPath, string destinationPath)
    {
        var acd = Path.GetFullPath(acdPath);
        var destination = DestinationPath.TryNormalize(destinationPath, out _);
        if (destination is null)
        {
            return false;
        }

        return Load().Any(entry =>
            entry.AcdPath is not null &&
            string.Equals(entry.AcdPath, acd, DestinationPath.PathComparison) &&
            DestinationPath.TryNormalize(entry.DestinationPath, out _) is { } stored &&
            DestinationPath.AreSame(stored, destination));
    }

    /// <summary>
    /// Like <see cref="Trust"/>, but reports failure instead of throwing so callers that only
    /// record the destination for later difftool runs can carry on when the store is unavailable.
    /// </summary>
    public bool TryTrust(string acdPath, string destinationPath, out string? error)
    {
        try
        {
            Trust(acdPath, destinationPath);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Records <paramref name="destinationPath"/> as the approved destination for <paramref name="acdPath"/>,
    /// replacing any destination previously approved for that ACD.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destinationPath"/> is not a valid absolute path.</exception>
    public void Trust(string acdPath, string destinationPath)
    {
        var acd = Path.GetFullPath(acdPath);
        var destination = DestinationPath.TryNormalize(destinationPath, out var destinationError)
            ?? throw new ArgumentException($"Invalid destination path: {destinationError}.", nameof(destinationPath));

        var directory = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var storeLock = AcquireLock();

        var entries = Load()
            .Where(entry => !string.Equals(entry.AcdPath, acd, DestinationPath.PathComparison))
            .ToList();
        entries.Add(new Entry { AcdPath = acd, DestinationPath = destination });

        var json = JsonSerializer.Serialize(new StoreFile { Entries = entries }, JsonOptions);

        var tempPath = $"{StorePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, StorePath, overwrite: true);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private FileStream AcquireLock()
    {
        var lockPath = StorePath + ".lock";
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
        if (!File.Exists(StorePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(StorePath))?.Entries ?? [];
        }
        catch (JsonException)
        {
            // An unreadable store approves nothing; the user is asked again and the file is rewritten.
            return [];
        }
    }

    private sealed class StoreFile
    {
        [JsonPropertyName("trusted_destinations")]
        public List<Entry> Entries { get; set; } = [];
    }

    private sealed class Entry
    {
        [JsonPropertyName("acd_path")]
        public string? AcdPath { get; set; }

        [JsonPropertyName("destination_path")]
        public string? DestinationPath { get; set; }
    }
}
