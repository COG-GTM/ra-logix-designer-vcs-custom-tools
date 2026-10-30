using L5xGitLib;
using L5xGitLib.Services;
using L5xploderLib;
using L5xploderLib.Enum;
using L5xploderLib.Services;
using RockwellAutomation.LogixDesigner.Logging;
using System.CommandLine;

namespace L5xCommands.Commands;

public static class Commit
{
    public static Command Command {
        get
        {
            var command = new Command("commit", "A command to Copy/Export/l5xplode/commit a representation of a Logix Designer ACD file.");

            var acdOption = CommandOptions.AcdInputFile();
            acdOption.Required = true;
            var unsafeSkipDependencyCheckOption = CommandOptions.UnsafeSkipDependencyCheck();
            var confirmDestinationOption = CommandOptions.ConfirmDestination();

            command.Options.Add(acdOption);
            command.Options.Add(unsafeSkipDependencyCheckOption);
            command.Options.Add(confirmDestinationOption);

            command.SetAction(parseResult => 
            {
                var acdPath = parseResult.GetValue(acdOption) ?? throw new ArgumentNullException(nameof(acdOption));
                var unsafeSkipDependencyCheck = parseResult.GetValue(unsafeSkipDependencyCheckOption);
                var confirmDestination = parseResult.GetValue(confirmDestinationOption);

                return Execute(acdPath, unsafeSkipDependencyCheck, confirmDestination);
            });

            return command;
        }
    }

    private static async Task<int> Execute(string acdPath, bool unsafeSkipDependencyCheck, string? confirmDestination)
    {
        var logger = new StdOutEventLogger();
        var trustStore = TrustedDestinationStore.ForCurrentUser();
        var configFilePath = Paths.GetL5xConfigFilePathFromAcdPath(acdPath);

        logger?.Status(configFilePath, "Loading configuration file...");
        var config = L5xGitConfig.LoadFromFile(configFilePath);

        if (config is null && confirmDestination is not null)
        {
            logger?.Error(configFilePath, "--confirm-destination requires an existing configuration file. Run the commit once without it to choose the destination.");
            return 1;
        }

        if (config is null)
        {
            logger?.Status(configFilePath, "Configuration file not found...");
            Console.WriteLine("Configuration file not found.");

            string destPath;
            try
            {
                destPath = CommitDestination.NormalizeDestination(Path.GetFullPath(UserPrompts.PromptForCommitRestoreDirectory()));
            }
            catch (InvalidCommitDestinationException ex)
            {
                logger?.Error(configFilePath, $"{ex.Message} Commit canceled.");
                return 1;
            }

            var gitService = EnsureGitRepo(destPath, logger);
            if (gitService is null)
                return 1;

            var promptForCommitMessage = UserPrompts.PromptForCommitMessageRequiredOnEachCommit();

            // The user chose these settings themselves, so they are confirmed for this ACD.
            trustStore.Trust(CommitDestination.NormalizeAcdPath(acdPath), destPath, gitService.RepoRoot, promptForCommitMessage);

            config = new L5xGitConfig
            {
                DestinationPath = destPath,
                PromptForCommitMessage = promptForCommitMessage,
            };

            logger?.Status(configFilePath, "Saving configuration file...");
            config.Save(configFilePath);

            return await RunCommit(acdPath, config, gitService, logger, unsafeSkipDependencyCheck) ? 0 : 1;
        }
        else
        {
            // Existing config: the sidecar may have been written by anyone who can write to the ACD's folder, so the
            // destination must match what this user confirmed before anything is deleted or committed there.
            var destination = VerifyConfiguredDestination(acdPath, configFilePath, config, confirmDestination, trustStore, logger);
            if (destination is null)
                return 1;

            var gitService = EnsureGitRepo(destination.DestinationPath, logger);
            if (gitService is null)
                return 1;

            if (!VerifyRepositoryRoot(destination, gitService, confirmDestination, logger))
            {
                gitService.Dispose();
                return 1;
            }

            if (confirmDestination is null && !destination.IsTrusted)
            {
                trustStore.Trust(destination.AcdPath, destination.DestinationPath, gitService.RepoRoot, destination.PromptForCommitMessage);
            }

            var verifiedConfig = new L5xGitConfig
            {
                DestinationPath = destination.DestinationPath,
                PromptForCommitMessage = config.PromptForCommitMessage,
            };

            return await RunCommit(acdPath, verifiedConfig, gitService, logger, unsafeSkipDependencyCheck) ? 0 : 1;
        }
    }

    private static CommitDestination? VerifyConfiguredDestination(
        string acdPath,
        string configFilePath,
        L5xGitConfig config,
        string? confirmDestination,
        TrustedDestinationStore trustStore,
        IOperationEvent? logger)
    {
        CommitDestination destination;
        try
        {
            destination = CommitDestination.Evaluate(acdPath, config.DestinationPath, config.PromptForCommitMessage, trustStore, DiscoverRepoRoot);
        }
        catch (InvalidCommitDestinationException ex)
        {
            logger?.Error(configFilePath, $"{ex.Message} Commit canceled.");
            return null;
        }

        if (confirmDestination is not null)
        {
            string confirmed;
            try
            {
                confirmed = CommitDestination.NormalizeDestination(confirmDestination);
            }
            catch (InvalidCommitDestinationException ex)
            {
                logger?.Error(configFilePath, $"--confirm-destination: {ex.Message} Commit canceled.");
                return null;
            }

            if (!CommitDestination.PathsEqual(confirmed, destination.DestinationPath))
            {
                logger?.Error(configFilePath,
                    $"The configuration file sets destination_path to '{destination.DestinationPath}', which does not match --confirm-destination '{confirmed}'. Commit canceled.");
                return null;
            }

            return destination;
        }

        if (destination.IsTrusted)
        {
            return destination;
        }

        if (!UserPrompts.ConfirmCommitDestination(configFilePath, destination))
        {
            logger?.Error(configFilePath, "Commit canceled: the configured destination was not confirmed.");
            return null;
        }

        return destination;
    }

    private static string? DiscoverRepoRoot(string path)
    {
        using var gitService = GitService.Create(path);
        return gitService?.RepoRoot;
    }

    /// <summary>
    /// Ensures the repository that will receive the commit is the one the user confirmed. Network destinations are not
    /// probed before confirmation, so an enclosing repository found afterwards is confirmed separately.
    /// </summary>
    private static bool VerifyRepositoryRoot(CommitDestination destination, IGitService gitService, string? confirmDestination, IOperationEvent? logger)
    {
        var actualRoot = CommitDestination.NormalizeRepoRoot(gitService.RepoRoot) ?? string.Empty;

        if (destination.RepoRootChecked)
        {
            // No repository at check time means EnsureGitRepo initialized one at the destination itself.
            var expectedRoot = destination.RepoRoot ?? destination.DestinationPath;
            if (!CommitDestination.PathsEqual(actualRoot, expectedRoot))
            {
                logger?.Error(destination.DestinationPath, $"The Git repository for '{destination.DestinationPath}' changed to '{actualRoot}' after it was checked. Commit canceled.");
                return false;
            }

            return true;
        }

        if (confirmDestination is not null || CommitDestination.PathsEqual(actualRoot, destination.DestinationPath))
        {
            return true;
        }

        if (!UserPrompts.ConfirmEnclosingRepository(destination.DestinationPath, actualRoot))
        {
            logger?.Error(destination.DestinationPath, "Commit canceled: the enclosing Git repository was not confirmed.");
            return false;
        }

        return true;
    }

    private static IGitService? EnsureGitRepo(string destinationPath, IOperationEvent? logger)
    {
        var gitService = GitService.Create(destinationPath);
        if (gitService is null)
        {
            if (UserPrompts.PromptToCreateAndInitGitRepo(destinationPath))
            {
                logger?.Status(destinationPath, "Initializing new Git repository...");
                gitService = GitService.Init(destinationPath);
                logger?.Status(destinationPath, "Git repository initialized.");
            }
            else
            {
                logger?.Error(destinationPath, "No Git repository found. Unable to commit changes.");
                return null;
            }
        }
        return gitService;
    }

    private static async Task<bool> RunCommit(string acdPath, L5xGitConfig config, IGitService gitService, IOperationEvent? logger, bool unsafeSkipDependencyCheck)
    {
        var succeeded = false;
        await PathGuard.Guard(
            path: config.DestinationPath,
            millisecondsTimeout: 0,
            timeoutExceptionText: $"Unable to acquire lock on \"{config.DestinationPath}\". Another instance of this process may be running.",
            action: async () =>
            {
                var commitMessage = UserPrompts.GetCommitMessagePromptIfNeeded(config);
                succeeded = await CommitFromAcd(acdPath, config, commitMessage, gitService, logger, unsafeSkipDependencyCheck);
            });
        return succeeded;
    }

    private static async Task<bool> CommitFromAcd(string acdPath, L5xGitConfig config, string commitMessage, IGitService gitService, IOperationEvent? logger, bool unsafeSkipDependencyCheck)
    {
        // Asked before the ACD conversion so a declined upgrade does not waste that work.
        if (!UserPrompts.ConfirmSchemaUpgrade(config.DestinationPath, force: false))
        {
            logger?.Error(config.DestinationPath, "Commit canceled without upgrading the exploded schema.");
            return false;
        }

        logger?.Status(acdPath, "Copying ACD to temp path...");
        var tempAcdFile = TempFile.CopyToTempPath(acdPath);
        var tempL5xFile = TempFile.FromTempFileWithNewExtension(tempAcdFile, ".L5X");

        await LogixProjectConverter.ConvertAsync(tempAcdFile.Path, tempL5xFile.Path, logger: logger, overwrite: false);

        logger?.Status(tempL5xFile.Path, "l5xploding L5X...");

        ExplodeL5x(tempL5xFile.Path, config.DestinationPath, unsafeSkipDependencyCheck);

        logger?.Status(config.DestinationPath, "Committing to Git repository...");

        logger?.Status(config.DestinationPath, "Commit message is:" + System.Environment.NewLine + commitMessage);

        var staged = gitService.Stage(config.DestinationPath);
        if (!staged)
        {
            logger?.Error(config.DestinationPath, "Repository is not in an appropriate state to stage changes.");
            return false;
        }
        var commit = await gitService.CommitAsync(commitMessage);
        if (commit is null)
        {
            logger?.Error(config.DestinationPath, "No changes to commit.");
        }
        else
        {
            logger?.Status(config.DestinationPath, $"Changes committed successfully with commit ID: {commit.Sha}");
            logger?.Status(config.DestinationPath, "You can now push your changes to the remote repository.");
        }

        return true;
    }

    static void ExplodeL5x(string l5xFilePath, string destinationPath, bool unsafeSkipDependencyCheck)
    {
        var defaultOptions = new L5xSerializationOptions()
        {
            PrettyXmlAttributes = false,
            Format = L5xSerializationFormat.Xml,
            OmitExportDate = true,
            UnsafeSkipDependencyCheck = unsafeSkipDependencyCheck,
        };

        var savedOptions = L5xSerializationOptions.LoadFromFile(Paths.GetOptionsFilePath(destinationPath));

        // If we have previously saved options, inherit UnsafeSkipDependencyCheck from them unless
        // the user is explicitly providing the flag now.
        var options = savedOptions != null
            ? new L5xSerializationOptions
            {
                PrettyXmlAttributes = savedOptions.PrettyXmlAttributes,
                Format = savedOptions.Format,
                OmitExportDate = savedOptions.OmitExportDate,
                UnsafeSkipDependencyCheck = unsafeSkipDependencyCheck || savedOptions.UnsafeSkipDependencyCheck,
            }
            : defaultOptions;

        var config = L5xDefaultConfig.DefaultConfig;
        var persistenceHandler = PersistenceServiceFactory.Create(
            explodedDir: destinationPath,
            options: options);

        using var inputStream = new FileStream(l5xFilePath, FileMode.Open, FileAccess.Read);
        L5xExploder.Explode(inputStream, config, persistenceHandler);
    }
}