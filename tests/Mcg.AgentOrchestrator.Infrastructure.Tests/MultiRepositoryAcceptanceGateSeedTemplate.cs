using Mcg.AgentOrchestrator.Infrastructure;

// Process-owned immutable pair; each fixture owns and disposes only its copies.
internal static class MultiRepositoryAcceptanceGateSeedTemplate
{
    internal enum RepositoryRole { Home, Second }

    private static readonly Lazy<string> Template = new(CreateTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static void CopyTo(string destination, RepositoryRole role)
    {
        var name = role switch
        {
            RepositoryRole.Home => "home",
            RepositoryRole.Second => "second",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown seed repository role.")
        };
        var source = Path.Combine(Template.Value, name);
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }

    private static string CreateTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-multi-repository-acceptance-template-" + Guid.NewGuid().ToString("n"));
        try
        {
            var home = Path.Combine(root, "home");
            var second = Path.Combine(root, "second");
            foreach (var repository in new[] { home, second })
            {
                if (!GoalRepositoryWorktreeProvisionerSeedTemplate.TryCopyTo(repository, "main"))
                    throw new InvalidOperationException("Acceptance gate template requires the provisioner main seed.");
                // Copies retain file contents and mtimes, but have new creation times on Windows.
                // Read-only status does not refresh the copied index before landing's read-tree.
                RunGit(repository, "config", "core.trustctime", "false");
                File.WriteAllText(Path.Combine(repository, ".gitignore"), ".orchestrator/\n");
                RunGit(repository, "add", ".gitignore");
                RunGit(repository, "commit", "-m", "Ignore case-owned runtime state");
            }

            var config = Path.Combine(home, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"),
                MultiRepositoryAcceptanceGateFixture.Manifest("home check", "--info"));
            RunGit(home, "add", "config/acceptance-manifest.json");
            RunGit(home, "commit", "-m", "Seed home manifest");
            return root;
        }
        catch
        {
            SharedTestSupport.RemoveTempDirectory(root);
            throw;
        }
    }

    private static void RunGit(string directory, params string[] arguments)
    {
        var result = GitCli.Run(directory, arguments);
        if (!result.Succeeded || result.DrainTimedOut || !result.ProcessStarted)
            throw new InvalidOperationException($"Acceptance gate seed template git {string.Join(' ', arguments)} failed: {result.Error}");
    }
}
