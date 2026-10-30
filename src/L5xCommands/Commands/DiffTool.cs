
using System.CommandLine;
using L5xploderLib;
using L5xGitLib;
using L5xGitLib.Services;
using RockwellAutomation.LogixDesigner.Logging;

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
            var trustConfigOption = CommandOptions.TrustConfig();

            command.Options.Add(acdOption);
            command.Options.Add(trustConfigOption);

            command.SetAction(parseResult => 
            {
                var acdPath = parseResult.GetValue(acdOption) ?? throw new ArgumentNullException(nameof(acdOption));
                var trustConfig = parseResult.GetValue(trustConfigOption);

                try
                {
                    return Execute(acdPath, trustConfig) ? 0 : 1;
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

    private static bool Execute(string acdPath, bool trustConfig)
    {
        var logger = new StdOutEventLogger();
        var configFilePath = Paths.GetL5xConfigFilePathFromAcdPath(acdPath);
        var config = L5xGitConfig.LoadFromFile(configFilePath);
        if (config == null)
        {
            Console.Error.WriteLine($"Configuration file not found at {configFilePath}. Please run 'l5xgit commit' first.");
            return false;
        }

        // git difftool runs whatever tool the discovered repository configures, so the destination must be confirmed first.
        const string consequence = "Difftool will run 'git difftool' inside that repository, which runs the diff tool configured there.";
        if (!SidecarDestination.EnsureConfirmed(acdPath, configFilePath, config, consequence, trustConfig, logger))
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