using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.RegularExpressions;

// Process-owned immutable seed; fixture disposal removes only each independent copy.
internal static class GoalRepositoryWorktreeProvisionerSeedTemplate
{
    private static readonly Lazy<string> Template = new(CreateTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static bool TryCopyTo(string destination, string branch)
    {
        // Unusual names retain git's existing validation and initialization behavior.
        if (branch == "HEAD" || !Regex.IsMatch(branch, @"\A[A-Za-z0-9][A-Za-z0-9_-]*\z"))
            return false;

        var source = Template.Value;
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));

        if (branch != "main")
        {
            var gitDirectory = Path.Combine(destination, ".git");
            File.Move(Path.Combine(gitDirectory, "refs", "heads", "main"),
                Path.Combine(gitDirectory, "refs", "heads", branch));
            var reflog = Path.Combine(gitDirectory, "logs", "refs", "heads", "main");
            if (File.Exists(reflog))
                File.Move(reflog, Path.Combine(gitDirectory, "logs", "refs", "heads", branch));
            File.WriteAllText(Path.Combine(gitDirectory, "HEAD"), $"ref: refs/heads/{branch}\n");
        }

        return true;
    }

    private static string CreateTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-goal-repository-provisioner-template-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(root);
            RunGit(root, "init", "-b", "main");
            RunGit(root, "config", "user.name", "Worktree Provisioner Tests");
            RunGit(root, "config", "user.email", "provisioner@example.com");
            RunGit(root, "config", "commit.gpgsign", "false");
            RunGit(root, "config", "core.autocrlf", "false");
            File.WriteAllText(Path.Combine(root, "example.txt"), "seed\n");
            RunGit(root, "add", "example.txt");
            RunGit(root, "commit", "-m", "Seed repository");

            // Copy-time branch retargeting requires the loose-ref layout we just seeded.
            if (File.ReadAllText(Path.Combine(root, ".git", "HEAD")).TrimEnd('\r', '\n') != "ref: refs/heads/main" ||
                !File.Exists(Path.Combine(root, ".git", "refs", "heads", "main")))
                throw new InvalidOperationException("Provisioner seed template requires a loose main branch ref and symbolic HEAD.");
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
            throw new InvalidOperationException($"Provisioner seed template git {string.Join(' ', arguments)} failed: {result.Error}");
    }
}
