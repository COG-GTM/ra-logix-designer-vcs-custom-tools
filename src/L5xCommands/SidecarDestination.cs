using L5xGitLib;
using L5xGitLib.Services;
using RockwellAutomation.LogixDesigner.Logging;

namespace L5xCommands;

/// <summary>
/// Guards use of <c>destination_path</c> from an ACD's <c>_L5xGit.yml</c>. That file travels with the project,
/// so its destination is only used once the current user has confirmed it for that ACD on this computer.
/// </summary>
internal static class SidecarDestination
{
    public static bool EnsureConfirmed(
        string acdPath,
        string configFilePath,
        L5xGitConfig config,
        string consequence,
        bool trustConfig,
        IOperationEvent? logger)
    {
        var destination = config.DestinationPath;
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
        {
            logger?.Error(configFilePath, $"destination_path '{destination}' must be an absolute path. Refusing to use it.");
            return false;
        }

        var trusted = TrustedDestinations.ForCurrentUser();
        if (trusted.IsTrusted(acdPath, destination))
        {
            return true;
        }

        var repoRoot = FindRepoRoot(destination);

        if (trustConfig)
        {
            logger?.Status(configFilePath, $"Using unconfirmed destination '{Path.GetFullPath(destination)}' (repository: {repoRoot ?? "none"}) because --trust-config was given.");
            return true;
        }

        if (!UserPrompts.PromptToConfirmSidecarDestination(acdPath, configFilePath, Path.GetFullPath(destination), repoRoot, consequence))
        {
            logger?.Error(configFilePath, "Destination not confirmed. No changes were made.");
            return false;
        }

        trusted.Trust(acdPath, destination);
        return true;
    }

    public static void Remember(string acdPath, string destinationPath) =>
        TrustedDestinations.ForCurrentUser().Trust(acdPath, destinationPath);

    private static string? FindRepoRoot(string destination)
    {
        using var gitService = GitService.Create(destination);
        return gitService?.RepoRoot;
    }
}
