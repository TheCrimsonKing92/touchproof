using Mcg.AgentOrchestrator.Core;

// Only immutable history is shared. Each invocation owns its repository, kernel and stores.
internal static class MergeTrainRecheckFixtureTemplate
{
    private static readonly Dictionary<string, MergeTrainFixtureObservation.Initialization<PreparedRepository>> Templates = new(StringComparer.Ordinal);
    private static readonly object Sync = new();

    private static PreparedRepository Template(string mode, MergeTrainFixtureObservation? observer = null)
    {
        MergeTrainFixtureObservation.Initialization<PreparedRepository> template;
        lock (Sync)
        {
            if (!Templates.TryGetValue(mode, out template!))
            {
                template = new(observer => Builder.Build(mode, observer));
                Templates.Add(mode, template);
            }
        }
        // Different modes can prepare independently; same-mode callers share one build or failure.
        return template.Get(observer);
    }

    internal static string TemplateDirectory(string mode) => Template(mode).Repo;

    internal static PreparedRepository Copy(string mode, MergeTrainFixtureObservation? observer = null)
    {
        var source = MergeTrainFixtureObservation.Run(observer, "template-access", () => Template(mode, observer));
        return MergeTrainFixtureObservation.Run(observer, "copy", () => CopyRepository(mode, source, observer));
    }

    private static PreparedRepository CopyRepository(string mode, PreparedRepository source,
        MergeTrainFixtureObservation? observer)
    {
        var repo = Path.Combine(Builder.ProcessRoot, Guid.NewGuid().ToString("n"), "repo");
        try
        {
            MergeTrainFixtureObservation.Run(observer, "files", () =>
            {
                Directory.CreateDirectory(repo);
                foreach (var directory in Directory.EnumerateDirectories(source.Repo, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(Path.Combine(repo, Path.GetRelativePath(source.Repo, directory)));
                foreach (var file in Directory.EnumerateFiles(source.Repo, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(repo, Path.GetRelativePath(source.Repo, file));
                    File.Copy(file, target);
                    // Git for Windows uses creation time as ctime. Preserve both index timestamps
                    // so copied candidate indexes need no additional git refresh processes.
                    File.SetCreationTimeUtc(target, File.GetCreationTimeUtc(file));
                    File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
                }

            });

            MergeTrainFixtureObservation.Run(observer, "backlinks", () =>
            {
                // commondir is relative; repair the two absolute worktree links only in the copy.
                foreach (var entry in Directory.EnumerateDirectories(Path.Combine(repo, ".git", "worktrees")))
                {
                    var backlink = Path.Combine(entry, "gitdir");
                    var oldGitFile = File.ReadAllText(backlink).Trim();
                    var relative = Path.GetRelativePath(source.Repo, oldGitFile);
                    if (Path.IsPathRooted(relative) || relative == ".." ||
                        relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidOperationException($"Recheck template '{mode}' has an external worktree: {oldGitFile}");
                    var gitFile = Path.Combine(repo, relative);
                    File.WriteAllText(backlink, gitFile.Replace('\\', '/') + "\n");
                    // Truncate the existing hidden .git file in place, as in CohortCandidateTemplate.
                    using var writer = new StreamWriter(File.Open(gitFile, FileMode.Truncate, FileAccess.Write));
                    writer.Write("gitdir: " + entry.Replace('\\', '/') + "\n");
                }
            });
            MergeTrainFixtureObservation.Run(observer, "index-refresh", () => Builder.RefreshIndex(repo));
            return source with { Repo = repo };
        }
        catch
        {
            MergeTrainFixtureObservation.Run(observer, "copy-cleanup", () => Builder.Remove(repo));
            throw;
        }
    }

    internal sealed record PreparedMember(GoalId GoalId, string Revision);
    internal sealed record PreparedHistory(string Seed, string Main,
        PreparedMember First, PreparedMember Second, PreparedMember Third)
    {
        internal PreparedMember[] Members => [First, Second, Third];
    }
    internal sealed record PreparedRepository(string Repo, PreparedHistory History);

    private sealed class Builder : AcceptanceCohortWorkflowTests
    {
        internal static string ProcessRoot => SeedRepositoryProcessRootPath;
        internal static void Remove(string repo) => DeleteDirectory(repo);
        internal static void RefreshIndex(string repo) => RunGit(repo, "update-index", "--refresh");

        internal static PreparedRepository Build(string mode, MergeTrainFixtureObservation? observer)
        {
            // Process-root janitor owns this template for the lifetime of the test process.
            var repo = CreateReducedAcceptanceCohortRepository();
            try
            {
                var seed = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
                AddAcceptanceManifest(repo);
                var sharedLines = Enumerable.Range(0, 40).Select(index => $"// line {index}").ToArray();
                var sharedPath = MergeTrainFocusedRecheckTests.SharedPath;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repo, sharedPath))!);
                // Match the candidate line endings so A and C edit separate lines, not the whole file.
                File.WriteAllText(Path.Combine(repo, sharedPath), string.Join('\n', sharedLines) + "\n");
                Directory.CreateDirectory(Path.Combine(repo, "tests"));
                File.WriteAllText(Path.Combine(repo, "tests/UnownedTests.cs"), "namespace Ns; public class UnownedTests { }");
                RunGit(repo, "add", sharedPath, "tests/UnownedTests.cs");
                RunGit(repo, "commit", "-m", "Seed independent and shared source");
                var main = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
                GoalId[] ids = [new("a1000001000000000000000000000000"),
                    new("a2000002000000000000000000000000"), new("a3000003000000000000000000000000")];
                var members = ids.Select((id, index) =>
                {
                    string revision;
                    if (mode == "overlap" && index == 0)
                    {
                        var lines = sharedLines.ToArray();
                        lines[0] = "// A's change";
                        revision = CreateWorktreeCandidate(repo, id, sharedPath, string.Join('\n', lines) + "\n");
                    }
                    else
                        revision = CreateWorktreeCandidate(repo, id, MergeTrainFocusedRecheckTests.Paths[index],
                            index == 2 ? "namespace Ns; public class CulpritTests { }" : $"public class Member{index} {{ }}");
                    if (mode == "overlap" && index == 2)
                    {
                        var lines = sharedLines.ToArray();
                        lines[^1] = "// C's change";
                        revision = CreateWorktreeCandidate(repo, id, sharedPath, string.Join('\n', lines) + "\n");
                    }
                    return new PreparedMember(id, revision);
                }).ToArray();
                return new(repo, new(seed, main, members[0], members[1], members[2]));
            }
            catch (Exception exception)
            {
                MergeTrainFixtureObservation.Run(observer, "builder-cleanup", () => Remove(repo));
                throw new InvalidOperationException($"Recheck fixture template '{mode}' could not be prepared.", exception);
            }
        }
    }
}
