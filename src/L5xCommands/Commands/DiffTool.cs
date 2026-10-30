using System.CommandLine;
using L5xploderLib;
using L5xGitLib;
using L5xGitLib.Services;

namespace L5xCommands.Commands;

public static class Difftool
{
    public static Command Command
    {
        get
        {
            var command = new Command("difftool", "A command to show the diff of HEAD with the previous commit.");

            var acdOption = CommandOptions.AcdInputFile();
            acdOption.Required = true;
            var trustDestinationOption = CommandOptions.TrustDestination();

            command.Options.Add(acdOption);
            command.Options.Add(trustDestinationOption);

            command.SetAction(parseResult => 
            {
                var acdPath = parseResult.GetValue(acdOption) ?? throw new ArgumentNullException(nameof(acdOption));
                var trustDestination = parseResult.GetValue(trustDestinationOption);

                try
                {
                    return Execute(acdPath, trustDestination) ? 0 : 1;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    return 1;
                }
            });

            return command;
        }
    }

    private static bool Execute(string acdPath, bool trustDestination)
    {
        acdPath = Path.GetFullPath(acdPath);
        var configFilePath = Paths.GetL5xConfigFilePathFromAcdPath(acdPath);
        var config = L5xGitConfig.LoadFromFile(configFilePath);
        if (config == null)
        {
            Console.Error.WriteLine($"Configuration file not found at {configFilePath}. Please run 'l5xgit commit' first.");
            return false;
        }

        // The _L5xGit.yml file travels with the project folder, so the repository it names is
        // untrusted until this user approves it for this ACD.
        var destinationPath = DestinationPath.TryNormalize(config.DestinationPath, out var destinationError);
        if (destinationPath == null)
        {
            Console.Error.WriteLine($"Invalid destination_path in {configFilePath}: {destinationError}.");
            return false;
        }

        using var gitService = GitService.Create(destinationPath);
        if (gitService == null)
        {
            Console.Error.WriteLine($"Failed to initialize Git service for path: {destinationPath}");
            return false;
        }
        var repoRoot = gitService.RepoRoot;

        var trustedDestinations = TrustedDestinations.ForCurrentUser();
        if (!trustedDestinations.IsTrusted(acdPath, destinationPath))
        {
            if (!trustDestination && !UserPrompts.PromptToTrustDestination(acdPath, configFilePath, destinationPath, repoRoot))
            {
                Console.Error.WriteLine($"Destination '{destinationPath}' was not approved. git difftool was not run.");
                return false;
            }

            if (!trustedDestinations.TryTrust(acdPath, destinationPath, out var trustError))
            {
                Console.Error.WriteLine($"Warning: could not remember this approval ({trustError}). You will be asked again next time.");
            }
        }

        // The repository's own configuration travels with the repository, so it must not choose
        // the program git difftool runs.
        var commandSettings = GitCli.FindDifftoolCommandSettings(gitService.GetRepositoryConfigKeys());
        if (commandSettings.Count > 0)
        {
            Console.Error.WriteLine(
                $"Refusing to run git difftool in '{repoRoot}': the repository's own configuration sets " +
                $"{string.Join(", ", commandSettings)}, which can make git run arbitrary programs. " +
                "Remove these settings from the repository (git config --local --unset <key>) and configure " +
                "your diff tool with 'git config --global' instead.");
            return false;
        }

        var gitExecutable = GitCli.ResolveExecutable();
        if (gitExecutable == null)
        {
            Console.Error.WriteLine("Could not find git on PATH. Install Git and make sure its directory is on PATH as an absolute path.");
            return false;
        }

        // run a git difftool --dir-diff HEAD~1 command, unfortunately lib2git does not help us here
        // we just use the git CLI
        var startInfo = GitCli.CreateDifftoolStartInfo(gitExecutable, repoRoot);

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                Console.Error.WriteLine("Failed to start the git difftool process.");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error while running git difftool: {ex.Message}");
            return false;
        }

        return true;
    }
}