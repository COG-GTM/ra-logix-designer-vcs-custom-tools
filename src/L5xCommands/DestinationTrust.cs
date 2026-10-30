using L5xGitLib;

namespace L5xCommands;

/// <summary>
/// Gate between an <c>_L5xGit.yml</c> file found next to an ACD and any file system or Git work
/// in the destination it names. The file can come from whoever supplied the project folder, so
/// its destination is used only after this user has approved it for this ACD.
/// </summary>
internal static class DestinationTrust
{
    public static bool EnsureTrusted(string acdPath, string configFilePath, string destinationPath, bool trustDestination)
        => EnsureTrusted(TrustedDestinations.ForCurrentUser(), acdPath, configFilePath, destinationPath, trustDestination);

    public static bool EnsureTrusted(TrustedDestinations store, string acdPath, string configFilePath, string destinationPath, bool trustDestination)
    {
        if (store.IsTrusted(acdPath, destinationPath))
        {
            return true;
        }

        if (!trustDestination && !UserPrompts.PromptToTrustDestination(acdPath, configFilePath, destinationPath))
        {
            Console.Error.WriteLine($"Destination '{destinationPath}' was not approved. Nothing was written or run.");
            return false;
        }

        store.Trust(acdPath, destinationPath);
        return true;
    }

    /// <summary>
    /// Records a destination the user just entered at a prompt, so later runs do not ask again.
    /// </summary>
    public static void Remember(string acdPath, string destinationPath)
        => TrustedDestinations.ForCurrentUser().Trust(acdPath, destinationPath);
}
