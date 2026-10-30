using L5xGitLib;
using L5xGitLib.Services;
using Xunit;

namespace L5xGitLib.Tests;

public sealed class CommitDestinationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"l5xgit_dest_{Guid.NewGuid():N}");
    private readonly TrustedDestinationStore store;
    private readonly string acdPath;

    public CommitDestinationTests()
    {
        Directory.CreateDirectory(root);
        store = new TrustedDestinationStore(Path.Combine(root, "profile", "trusted-destinations.yml"));
        acdPath = Path.Combine(root, "share", "Line1.ACD");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(root, recursive: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/dir")]
    [InlineData("..")]
    public void NormalizeDestination_rejects_missing_or_relative_paths(string configured)
    {
        Assert.Throws<InvalidCommitDestinationException>(() => CommitDestination.NormalizeDestination(configured));
    }

    [Fact]
    public void NormalizeDestination_returns_full_path_without_trailing_separator()
    {
        var configured = Path.Combine(root, "repo", "sub", "..") + Path.DirectorySeparatorChar;

        Assert.Equal(Path.Combine(root, "repo"), CommitDestination.NormalizeDestination(configured));
    }

    [Theory]
    [InlineData(@"\\mallory-box\drop\line1")]
    [InlineData("//mallory-box/drop/line1")]
    [InlineData(@"\\?\UNC\mallory-box\drop")]
    public void IsNetwork_flags_unc_and_device_paths(string path)
    {
        Assert.True(CommitDestination.IsNetwork(path));
    }

    [Fact]
    public void IsNetwork_does_not_flag_local_paths()
    {
        Assert.False(CommitDestination.IsNetwork(Path.Combine(root, "repo")));
    }

    [Fact]
    public void Sidecar_destination_that_was_never_confirmed_is_not_trusted()
    {
        var repo = InitRepo("foreign");

        var destination = CommitDestination.Evaluate(acdPath, repo, promptForCommitMessage: true, store, Discover);

        Assert.False(destination.IsTrusted);
        Assert.True(destination.RepoRootChecked);
        Assert.Equal(repo, destination.RepoRoot);
        Assert.False(destination.RepoRootIsAncestor);
    }

    [Fact]
    public void Confirmed_destination_is_trusted_on_the_next_commit()
    {
        var repo = InitRepo("project");
        store.Trust(CommitDestination.NormalizeAcdPath(acdPath), repo, repo, promptForCommitMessage: true);

        var destination = CommitDestination.Evaluate(acdPath, repo + Path.DirectorySeparatorChar, promptForCommitMessage: true, store, Discover);

        Assert.True(destination.IsTrusted);
    }

    [Fact]
    public void Sidecar_rewritten_to_another_repository_is_not_trusted()
    {
        var original = InitRepo("project");
        var foreign = InitRepo("foreign");
        store.Trust(CommitDestination.NormalizeAcdPath(acdPath), original, original, promptForCommitMessage: true);

        var destination = CommitDestination.Evaluate(acdPath, foreign, promptForCommitMessage: true, store, Discover);

        Assert.False(destination.IsTrusted);
    }

    [Fact]
    public void Trust_is_scoped_to_the_acd_it_was_confirmed_for()
    {
        var repo = InitRepo("project");
        store.Trust(CommitDestination.NormalizeAcdPath(acdPath), repo, repo, promptForCommitMessage: true);

        var otherAcd = Path.Combine(root, "share", "Line2.ACD");
        var destination = CommitDestination.Evaluate(otherAcd, repo, promptForCommitMessage: true, store, Discover);

        Assert.False(destination.IsTrusted);
    }

    [Fact]
    public void Destination_inside_another_repository_reports_the_enclosing_root()
    {
        var repo = InitRepo("unrelated");
        var nested = Path.Combine(repo, "projects", "line1");
        Directory.CreateDirectory(nested);

        var destination = CommitDestination.Evaluate(acdPath, nested, promptForCommitMessage: true, store, Discover);

        Assert.Equal(repo, destination.RepoRoot);
        Assert.True(destination.RepoRootIsAncestor);
        Assert.False(destination.IsTrusted);
    }

    [Fact]
    public void Trusted_destination_whose_repository_root_moved_is_not_trusted()
    {
        var outer = Path.Combine(root, "outer");
        var dest = Path.Combine(outer, "line1");
        Directory.CreateDirectory(dest);
        using (GitService.Init(dest)) { }
        store.Trust(CommitDestination.NormalizeAcdPath(acdPath), dest, dest, promptForCommitMessage: true);

        // The confirmed repository is removed and the folder now resolves to an enclosing repository instead.
        DeleteDirectory(Path.Combine(dest, ".git"));
        using (GitService.Init(outer)) { }

        var destination = CommitDestination.Evaluate(acdPath, dest, promptForCommitMessage: true, store, Discover);

        Assert.Equal(outer, destination.RepoRoot);
        Assert.False(destination.IsTrusted);
    }

    [Fact]
    public void Unconfirmed_network_destination_is_not_probed()
    {
        var probed = false;

        var destination = CommitDestination.Evaluate(acdPath, "//mallory-box/drop/line1", promptForCommitMessage: true, store, _ =>
        {
            probed = true;
            return null;
        });

        Assert.False(probed);
        Assert.False(destination.RepoRootChecked);
        Assert.True(destination.IsNetworkPath);
        Assert.False(destination.IsTrusted);
    }

    [Fact]
    public void Confirmed_network_destination_is_probed_and_trusted()
    {
        const string unc = "//fileserver/projects/line1";
        var acd = CommitDestination.NormalizeAcdPath(acdPath);
        var dest = CommitDestination.NormalizeDestination(unc);
        store.Trust(acd, dest, dest, promptForCommitMessage: true);

        var destination = CommitDestination.Evaluate(acdPath, unc, promptForCommitMessage: true, store, path => path);

        Assert.True(destination.RepoRootChecked);
        Assert.True(destination.IsTrusted);
    }

    [Fact]
    public void Trust_replaces_the_previous_entry_for_the_same_acd()
    {
        var first = InitRepo("first");
        var second = InitRepo("second");
        var acd = CommitDestination.NormalizeAcdPath(acdPath);

        store.Trust(acd, first, first, promptForCommitMessage: true);
        store.Trust(acd, second, second, promptForCommitMessage: true);

        Assert.False(store.IsTrusted(acd, first, first, promptForCommitMessage: true));
        Assert.True(store.IsTrusted(acd, second, second, promptForCommitMessage: true));
    }

    [Fact]
    public void Store_lives_outside_the_acd_directory()
    {
        var acdDirectory = Path.GetDirectoryName(Path.GetFullPath(acdPath))!;

        Assert.False(TrustedDestinationStore.DefaultFilePath.StartsWith(acdDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith(Path.Combine("L5xGit", "trusted-destinations.yml"), TrustedDestinationStore.DefaultFilePath);
    }

    [Fact]
    public void Sidecar_that_turns_off_the_confirmed_commit_message_prompt_is_not_trusted()
    {
        var repo = InitRepo("project");
        store.Trust(CommitDestination.NormalizeAcdPath(acdPath), repo, repo, promptForCommitMessage: true);

        var destination = CommitDestination.Evaluate(acdPath, repo, promptForCommitMessage: false, store, Discover);

        Assert.False(destination.IsTrusted);
        Assert.False(destination.PromptForCommitMessage);
    }

    [Fact]
    public void Sidecar_that_turns_on_the_commit_message_prompt_stays_trusted()
    {
        var repo = InitRepo("project");
        store.Trust(CommitDestination.NormalizeAcdPath(acdPath), repo, repo, promptForCommitMessage: false);

        var destination = CommitDestination.Evaluate(acdPath, repo, promptForCommitMessage: true, store, Discover);

        Assert.True(destination.IsTrusted);
    }

    [Fact]
    public void Concurrent_confirmations_for_different_acds_are_all_kept()
    {
        var repo = InitRepo("project");
        var acds = Enumerable.Range(0, 16).Select(i => Path.Combine(root, "share", $"Line{i}.ACD")).ToList();

        Parallel.ForEach(acds, acd => store.Trust(acd, repo, repo, promptForCommitMessage: true));

        Assert.All(acds, acd => Assert.True(store.IsTrusted(acd, repo, repo, promptForCommitMessage: true)));
    }

    private string InitRepo(string name)
    {
        var path = Path.Combine(root, name);
        using (GitService.Init(path)) { }
        return path;
    }

    private static string? Discover(string path)
    {
        using var gitService = GitService.Create(path);
        return gitService?.RepoRoot;
    }

    private static void DeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }
}
