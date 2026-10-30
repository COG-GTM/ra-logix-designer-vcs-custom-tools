
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

        if (!DestinationTrust.EnsureTrusted(acdPath, configFilePath, config.DestinationPath, trustDestination))
        {
            return false;
        }

        using var gitService = GitService.Create(config.DestinationPath);
        if (gitService == null)
        {
            Console.Error.WriteLine($"Failed to initialize Git service for path: {config.DestinationPath}");
            return false;
        }
        var repoRoot = gitService.RepoRoot;

        // The repository's own .git/config travels with the repository, so it must not choose
        // the program git difftool runs. Diff tools belong in the user's global configuration.
        var commandSettings = gitService.GetRepositoryCommandSettings();
        if (commandSettings.Count > 0)
        {
            Console.Error.WriteLine(
                $"Refusing to run git difftool in '{repoRoot}': the repository's own configuration sets " +
                $"{string.Join(", ", commandSettings)}, which can make git run arbitrary programs. " +
                "Remove these settings from the repository (git config --local --unset <key>) and configure " +
                "your diff tool with 'git config --global' instead.");
            return false;
        }

        // run a git difftool --dir-diff HEAD~1 command, unfortunately lib2git does not help us here
        // we just use the git CLI
        var diffCommand = $"difftool HEAD~1 HEAD --dir-diff  --no-prompt";
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = diffCommand,
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

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