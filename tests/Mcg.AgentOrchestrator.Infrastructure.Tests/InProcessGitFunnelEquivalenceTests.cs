using System.Text;
using static GoalRepositoryWorktreeProvisionerFixtureSeedTests;

// Parallel-safe: unique roots and async-flow scopes; no process environment mutations.
public sealed class InProcessGitFunnelEquivalenceTests
{
    private static readonly IReadOnlyDictionary<string, string> FixedEnvironment = new Dictionary<string, string>
    {
        ["GIT_AUTHOR_DATE"] = "2026-01-02T03:04:05.0000000+02:30",
        ["GIT_COMMITTER_DATE"] = "2026-01-02T03:04:05.0000000+02:30",
        ["GIT_PAGER"] = "cat"
    };

    [Fact]
    public void ServedShapes_SameScriptAsRealGit_ProduceIdenticalResults()
    {
        WithRepository(root =>
        {
            var served = RunScript(root, realOnly: false);
            ResetDirectory(root);
            var real = RunScript(root, realOnly: true);
            Assert.Equal(real.Calls, served.Calls);
            Assert.Equal(real.Head, served.Head);
            Assert.Equal(real.Status, served.Status);
            Assert.Equal(real.Index, served.Index);
            Assert.Equal(real.InitFiles.Keys, served.InitFiles.Keys);
            foreach (var (path, bytes) in real.InitFiles) Assert.Equal(bytes, served.InitFiles[path]);
            Assert.Equal(real.ReflogSubjects, served.ReflogSubjects);
            Assert.Equal(InProcessGitFunnel.ServedShapes.Order(StringComparer.Ordinal), served.Served.Distinct().Order(StringComparer.Ordinal));
            Assert.Empty(real.Served);
            Assert.True(real.Launched >= served.Served.Length);
        });
    }

    [Fact]
    public void LinkedWorktree_PathspecAndPlainCommits_MatchRealGitAndIsolateMain()
    {
        WithRepository(root =>
        {
            var served = RunLinkedWorktreeScript(root, realOnly: false);
            ResetDirectory(root);
            var real = RunLinkedWorktreeScript(root, realOnly: true);
            Assert.Equal(real, served);
            Assert.Equal("MM unrelated.txt\n", served.PathspecStatus);
            Assert.Equal(" M unrelated.txt\n", served.FinalStatus);
        });
    }

    [Fact]
    public void AcceptanceFixture_PathspecAndCommitChangeCalls_AreServedAndMatchRealTips()
    {
        (string HomeSetup, string SecondarySetup, string HomeFinal, string SecondaryFinal) Build(bool realOnly)
        {
            using var observation = InProcessGitFunnel.Observe(realOnly);
            using var dates = GoalRepositoryWorktreeProvisionerFixture.WithCommitDates(FixedEnvironment["GIT_AUTHOR_DATE"]);
            using var fixture = new MultiRepositoryAcceptanceGateFixture();
            Assert.Equal(realOnly ? Array.Empty<string>() : new[] { "commit-pathspec", "commit-pathspec" },
                observation.Served.Where(shape => shape.StartsWith("commit-", StringComparison.Ordinal)));
            Assert.Equal(realOnly ? 2 : 0, observation.Launched.Count(command => command == "commit"));
            string homeSetup;
            string secondarySetup;
            using (var readbackLaunches = new GitLaunches())
            {
                homeSetup = GoalRepositoryWorktreeProvisionerFixture.Run(fixture.HomeWorktree, "rev-parse", "HEAD");
                secondarySetup = GoalRepositoryWorktreeProvisionerFixture.Run(fixture.SecondaryWorktree, "rev-parse", "HEAD");
                Assert.Equal(new[] { "rev-parse", "rev-parse" }, readbackLaunches.Commands);
            }
            var homeFinal = GoalRepositoryWorktreeProvisionerFixture.CommitChange(fixture.HomeWorktree, "next home\n");
            var secondaryFinal = GoalRepositoryWorktreeProvisionerFixture.CommitChange(fixture.SecondaryWorktree, "next secondary\n");
            Assert.Equal(realOnly ? Array.Empty<string>() : new[] { "commit-pathspec", "commit-pathspec", "commit-message", "commit-message" },
                observation.Served.Where(shape => shape.StartsWith("commit-", StringComparison.Ordinal)));
            Assert.Equal(realOnly ? 4 : 0, observation.Launched.Count(command => command == "commit"));
            Assert.Equal(homeFinal, RealGit(fixture.HomeWorktree, "rev-parse", "HEAD").Trim());
            Assert.Equal(secondaryFinal, RealGit(fixture.SecondaryWorktree, "rev-parse", "HEAD").Trim());
            return (homeSetup, secondarySetup, homeFinal, secondaryFinal);
        }

        Assert.Equal(Build(realOnly: true), Build(realOnly: false));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("quiet")]
    [InlineData("long-quiet")]
    [InlineData("long-branch")]
    [InlineData("equals-branch")]
    public void InitOptions_RealGitAndWriter_HaveIdenticalOutputAndMetadata(string form)
    {
        WithRepository(root =>
        {
            var args = form switch
            {
                "quiet" => new[] { "init", "-q", "-b", "main" },
                "long-quiet" => ["init", "--quiet", "-b", "main"],
                "long-branch" => ["init", "--initial-branch", "main"],
                "equals-branch" => ["init", "--initial-branch=main"],
                _ => ["init"]
            };
            using var serving = InProcessGitFunnel.Observe();
            var served = RunGitProbe(root, args);
            Assert.Null(served.ChildProcessId);
            Assert.Equal(new[] { "init" }, serving.Served);
            var files = Directory.EnumerateFiles(Path.Combine(root, ".git"), "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes);
            ResetDirectory(root);
            using var real = InProcessGitFunnel.Observe(realOnly: true);
            var launched = RunGitProbe(root, args);
            Assert.NotNull(launched.ChildProcessId);
            Assert.True(served.Succeeded, served.ToString());
            Assert.True(launched.Succeeded, launched.ToString());
            Assert.Equal(launched.StandardOutput, served.StandardOutput);
            Assert.Equal(files.Keys.Order(StringComparer.Ordinal), Directory.EnumerateFiles(Path.Combine(root, ".git"), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.Ordinal));
            foreach (var (path, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, path)));
        });
    }

    [Theory]
    [InlineData("unrecognised")]
    [InlineData("empty-environment")]
    [InlineData("extra-environment")]
    [InlineData("seam")]
    [InlineData("capture-hook")]
    [InlineData("exit-hook")]
    [InlineData("inherited-environment")]
    [InlineData("verification-environment")]
    [InlineData("counter")]
    [InlineData("switch")]
    public void IneligibleCalls_ReachRealGit_LeaveServingCounterUnchanged(string control)
    {
        WithRepository(root =>
        {
            BuildSeed(root);
            using var serving = InProcessGitFunnel.Observe(realOnly: control == "switch");
            using var seam = control == "seam" ? UseGitProbeSeam() : null;
            using var counter = control == "counter" ? CountGitProbeLaunches() : null;
            var commandEnvironment = control switch
            {
                "empty-environment" => new Dictionary<string, string>(),
                "extra-environment" => new Dictionary<string, string> { ["GIT_TRACE"] = "0" },
                _ => null
            };
            var inherited = control == "inherited-environment" ? NativeEnvironment() : null;
            var verification = control == "verification-environment" ? NativeEnvironment() : null;
            var result = RunGitProbe(root, control == "unrecognised" ? ["log", "-1", "--format=%H"] : ["config", "user.name", "Changed Name"],
                commandEnvironment,
                inheritedEnvironment: inherited,
                beforeOwnedCaptureRead: control == "capture-hook" ? (_, _) => { } : null,
                beforeOwnedExitObservation: control == "exit-hook" ? _ => { } : null,
                verificationEnvironment: verification);
            Assert.True(result.Succeeded, result.ToString());
            Assert.NotNull(result.ChildProcessId);
            Assert.Empty(serving.Served);
            if (counter is not null) Assert.Equal(1, counter.Count);
        });
    }

    [Theory]
    [InlineData("gitignore")]
    [InlineData("hook")]
    [InlineData("unsupported-option")]
    [InlineData("missing-path")]
    [InlineData("existing-branch")]
    [InlineData("nothing-staged")]
    [InlineData("invalid-branch")]
    [InlineData("invalid-utf8")]
    [InlineData("info-attributes")]
    [InlineData("working-attributes")]
    public void UnsupportedState_FallsThrough_WithoutServing(string state)
    {
        WithRepository(root =>
        {
            BuildSeed(root);
            if (state == "gitignore") File.WriteAllText(Path.Combine(root, ".gitignore"), "ignored\n");
            if (state == "hook") File.WriteAllText(Path.Combine(root, ".git", "hooks", "post-checkout"), "#!/bin/sh\nexit 0\n");
            if (state == "info-attributes") File.WriteAllText(Path.Combine(root, ".git", "info", "attributes"), "*.txt binary\n");
            if (state == "working-attributes")
            {
                File.WriteAllText(Path.Combine(root, "content.txt"), "text\n");
                RealGit(root, "add", "content.txt");
                File.WriteAllText(Path.Combine(root, ".gitattributes"), "*.txt binary\n");
            }
            if (state == "invalid-utf8")
            {
                File.WriteAllBytes(Path.Combine(root, "invalid.txt"), [255, 254, 10]);
                RealGit(root, "add", "invalid.txt");
            }
            using var serving = InProcessGitFunnel.Observe();
            var args = state switch
            {
                "unsupported-option" => new[] { "config", "--get", "user.name" },
                "missing-path" => ["add", "missing.txt"],
                "existing-branch" => ["checkout", "-b", "main"],
                "nothing-staged" => ["commit", "-m", "nothing"],
                "invalid-branch" => ["checkout", "-b", "--invalid"],
                "invalid-utf8" => ["commit", "-m", "Invalid text"],
                "working-attributes" => ["commit", "-m", "Attribute text"],
                _ => ["add", "-A"]
            };
            var result = RunGitProbe(root, args);
            Assert.NotNull(result.ChildProcessId);
            Assert.Empty(serving.Served);
            Assert.Equal(state is "missing-path" or "existing-branch" or "nothing-staged" or "invalid-branch" ? 1 : 0,
                result.ExitCode == 0 ? 0 : 1);
        });
    }

    [Theory]
    [InlineData("merge-in-progress")]
    [InlineData("hook")]
    [InlineData("packed-refs")]
    public void LinkedWorktree_UnsupportedState_LaunchesRealGit(string state)
    {
        WithRepository(root =>
        {
            var main = Path.Combine(root, "main");
            var worktree = Path.Combine(root, "linked");
            Directory.CreateDirectory(main);
            BuildSeed(main);
            File.WriteAllText(Path.Combine(main, "content.txt"), "seed\n");
            RealGit(main, "add", "content.txt");
            RealGitWithFixedEnvironment(main, "commit", "-m", "Seed tracked content");
            RealGit(main, "worktree", "add", "-b", "linked", worktree);

            var content = Path.Combine(worktree, "content.txt");
            string[] add = ["add", "content.txt"];
            File.WriteAllText(content, "eligible\n");
            using (var baseline = InProcessGitFunnel.Observe())
            {
                var result = RunGitProbe(worktree, add);
                Assert.True(result.Succeeded, result.ToString());
                Assert.Equal(new[] { "add-paths" }, baseline.Served);
                Assert.Empty(baseline.Launched);
            }
            RealGit(worktree, "restore", "--staged", "--worktree", "content.txt");

            var commonGit = Path.Combine(main, ".git");
            switch (state)
            {
                case "merge-in-progress":
                    RealGitWithFixedEnvironment(main, "commit", "--allow-empty", "-m", "Advance main");
                    RealGitWithFixedEnvironment(worktree, "merge", "--no-ff", "--no-commit", "main");
                    Assert.Equal(RealGit(main, "rev-parse", "HEAD"), RealGit(worktree, "rev-parse", "MERGE_HEAD"));
                    break;
                case "hook":
                    File.WriteAllText(Path.Combine(commonGit, "hooks", "post-checkout"), "#!/bin/sh\nexit 0\n");
                    break;
                case "packed-refs":
                    // Keep the linked branch loose so the packed-refs guard is the reason for refusal.
                    RealGit(main, "pack-refs", "--all", "--no-prune");
                    Assert.True(File.Exists(Path.Combine(commonGit, "packed-refs")));
                    Assert.True(File.Exists(Path.Combine(commonGit, "refs", "heads", "linked")));
                    break;
            }

            File.WriteAllText(content, "fallback\n");
            using (var observation = InProcessGitFunnel.Observe())
            {
                var result = RunGitProbe(worktree, add);
                Assert.True(result.Succeeded, result.ToString());
                Assert.NotNull(result.ChildProcessId);
                Assert.Equal(new[] { "add" }, observation.Launched);
                Assert.Empty(observation.Served);
            }
            Assert.Equal("fallback\n", RealGit(worktree, "show", ":content.txt"));
        });
    }

    [Fact]
    public void MutationFailure_AfterEligibility_ReturnsFailureWithoutRelaunch()
    {
        WithRepository(root =>
        {
            BuildSeed(root);
            // An impossible reflog destination fails after the ref update has happened.
            var logs = Path.Combine(root, ".git", "logs", "refs", "heads");
            File.WriteAllText(Path.Combine(logs, "blocked"), "not a directory\n");
            using var serving = InProcessGitFunnel.Observe();
            var result = RunGitProbe(root, ["branch", "blocked/child"], FixedEnvironment);
            Assert.Equal(128, result.ExitCode);
            Assert.Null(result.ChildProcessId);
            Assert.Contains("in-process fixture write failed", result.StandardError);
            Assert.Equal(new[] { "branch-create" }, serving.Served);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("HEAD")]
    [InlineData("main")]
    [InlineData("refs/heads/main")]
    [InlineData("commit-id")]
    public void BranchStart_OmittedOrExplicit_MatchesRealGitReflog(string? start)
    {
        WithRepository(root =>
        {
            BuildSeed(root);
            var head = RealGit(root, "rev-parse", "HEAD").Trim();
            var args = start is null ? new[] { "branch", "spare" }
                : ["branch", "spare", start == "commit-id" ? head : start];
            var log = Path.Combine(root, ".git", "logs", "refs", "heads", "spare");
            using var serving = InProcessGitFunnel.Observe();
            var served = RunGitProbe(root, args, FixedEnvironment);
            Assert.True(served.Succeeded, served.ToString());
            Assert.Null(served.ChildProcessId);
            Assert.Equal(new[] { "branch-create" }, serving.Served);
            var servedLog = File.ReadAllText(log);
            Assert.Equal(head + "\n", File.ReadAllText(Path.Combine(root, ".git", "refs", "heads", "spare")));

            ResetDirectory(root);
            BuildSeed(root);
            using var real = InProcessGitFunnel.Observe(realOnly: true);
            var launched = RunGitProbe(root, args, FixedEnvironment);
            Assert.True(launched.Succeeded, launched.ToString());
            Assert.NotNull(launched.ChildProcessId);
            Assert.Empty(real.Served);
            Assert.Equal(launched.ExitCode, served.ExitCode);
            Assert.Equal(launched.StandardOutput, served.StandardOutput);
            Assert.Equal(File.ReadAllText(log), servedLog);
            Assert.Equal(head + "\n", File.ReadAllText(Path.Combine(root, ".git", "refs", "heads", "spare")));
        });
    }

    private sealed record ScriptResult((int? Exit, string Output)[] Calls, string Head, string Status, string Index,
        SortedDictionary<string, byte[]> InitFiles, string[] ReflogSubjects, string[] Served, int Launched);

    private sealed record LinkedWorktreeResult(string PathspecTip, string PathspecStatus, string PathspecIndex,
        string Tip, string BranchTip, string FinalStatus, string HeadReflog, string BranchReflog, string CommitMessage);

    private static LinkedWorktreeResult RunLinkedWorktreeScript(string root, bool realOnly)
    {
        var main = Path.Combine(root, "main");
        var worktree = Path.Combine(root, "linked");
        Directory.CreateDirectory(main);
        BuildSeed(main);
        foreach (var path in new[] { "selected.txt", "unrelated.txt", "plain.txt" })
            File.WriteAllText(Path.Combine(main, path), "seed\n");
        RealGit(main, "add", "-A");
        RealGitWithFixedEnvironment(main, "commit", "-m", "Seed tracked files");
        RealGit(main, "worktree", "add", "-b", "linked", worktree);
        var mainGit = Path.Combine(main, ".git");
        var worktreeGit = Path.GetFullPath(RealGit(worktree, "rev-parse", "--absolute-git-dir").Trim());
        // Git accepts these pointers relative to the files that contain them.
        // Git for Windows marks the .git pointer hidden, so open the existing file in truncate mode.
        using (var pointer = new StreamWriter(File.Open(Path.Combine(worktree, ".git"), FileMode.Truncate)))
        {
            pointer.Write("gitdir: " + Path.GetRelativePath(worktree, worktreeGit).Replace('\\', '/') + "\n");
        }
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), Path.GetRelativePath(worktreeGit, mainGit).Replace('\\', '/') + "\n");
        var mainHead = RealGit(main, "rev-parse", "HEAD");
        var mainHeadBytes = File.ReadAllBytes(Path.Combine(mainGit, "HEAD"));
        var mainIndex = File.ReadAllBytes(Path.Combine(mainGit, "index"));
        File.WriteAllText(Path.Combine(worktree, "selected.txt"), "staged selected\n");
        File.WriteAllText(Path.Combine(worktree, "unrelated.txt"), "staged unrelated\n");
        RealGit(worktree, "add", "selected.txt", "unrelated.txt");
        File.WriteAllText(Path.Combine(worktree, "selected.txt"), "working selected\n");
        File.WriteAllText(Path.Combine(worktree, "unrelated.txt"), "working unrelated\n");

        string Call(params string[] args)
        {
            var result = RunGitProbe(worktree, args, FixedEnvironment);
            Assert.True(result.Succeeded, result.ToString());
            return result.StandardOutput;
        }

        string pathspecTip;
        using (var observation = InProcessGitFunnel.Observe(realOnly))
        {
            Call("commit", "-m", "Selected tracked file", "--", "selected.txt");
            pathspecTip = Call("rev-parse", "--verify", "HEAD");
            Assert.Equal(realOnly ? Array.Empty<string>() : new[] { "commit-pathspec", "rev-parse" }, observation.Served);
            Assert.Equal(realOnly ? new[] { "commit", "rev-parse" } : Array.Empty<string>(), observation.Launched);
        }
        Assert.Equal(RealGit(worktree, "rev-parse", "HEAD"), pathspecTip);
        Assert.Equal("working selected\n", RealGit(worktree, "show", "HEAD:selected.txt"));
        Assert.Equal("seed\n", RealGit(worktree, "show", "HEAD:unrelated.txt"));
        Assert.Equal("staged unrelated\n", RealGit(worktree, "show", ":unrelated.txt"));
        var pathspecStatus = RealGit(worktree, "status", "--porcelain");
        var pathspecIndex = RealGit(worktree, "ls-files", "--stage");

        File.WriteAllText(Path.Combine(worktree, "plain.txt"), "plain add\n");
        using (var observation = InProcessGitFunnel.Observe(realOnly))
        {
            Call("add", "plain.txt");
            Call("commit", "-m", "Plain commit");
            Assert.Equal(realOnly ? Array.Empty<string>() : new[] { "add-paths", "commit-message" }, observation.Served);
            Assert.Equal(realOnly ? new[] { "add", "commit" } : Array.Empty<string>(), observation.Launched);
        }

        Assert.Empty(RealGitWithFixedEnvironment(worktree, "fsck", "--strict").StandardError);
        Assert.Equal(mainHead, RealGit(main, "rev-parse", "HEAD"));
        Assert.Equal(mainHeadBytes, File.ReadAllBytes(Path.Combine(mainGit, "HEAD")));
        Assert.Equal(mainIndex, File.ReadAllBytes(Path.Combine(mainGit, "index")));
        Assert.False(Path.Exists(Path.Combine(worktreeGit, "logs", "refs")));
        var tip = RealGit(worktree, "rev-parse", "HEAD");
        var branchTip = RealGit(main, "rev-parse", "refs/heads/linked");
        Assert.Equal(tip, branchTip);
        var headLog = File.ReadAllText(Path.Combine(worktreeGit, "logs", "HEAD"));
        var branchLog = File.ReadAllText(Path.Combine(mainGit, "logs", "refs", "heads", "linked"));
        var commitMessage = File.ReadAllText(Path.Combine(worktreeGit, "COMMIT_EDITMSG"));
        // Compare the commit entries, whose dates are pinned by the command environment.
        return new(pathspecTip, pathspecStatus, pathspecIndex, tip, branchTip, RealGit(worktree, "status", "--porcelain"),
            string.Join('\n', headLog.Split('\n').TakeLast(3)),
            string.Join('\n', branchLog.Split('\n').TakeLast(3)), commitMessage);
    }

    private static ScriptResult RunScript(string root, bool realOnly)
    {
        using var serving = InProcessGitFunnel.Observe(realOnly);
        var calls = new List<(int?, string)>();
        var launched = 0;
        string CallWithEnvironment(IReadOnlyDictionary<string, string> environment, params string[] args)
        {
            var result = RunGitProbe(root, args, environment);
            Assert.True(result.Succeeded, result.ToString());
            if (result.ChildProcessId is not null) launched++;
            else if (!realOnly) Assert.Contains("served=fixture-writer", result.EnvironmentContract);
            calls.Add((result.ExitCode, result.StandardOutput));
            return result.StandardOutput.Trim();
        }
        string Call(params string[] args) => CallWithEnvironment(FixedEnvironment, args);
        Call("init", "-b", "main");
        Call("config", "user.name", "Funnel Tests");
        Call("config", "user.email", "funnel@example.com");
        Call("config", "--local", "user.name", "Funnel Tests");
        Call("config", "commit.gpgsign", "false");
        var initFiles = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, ".git"), "*", SearchOption.AllDirectories))
            initFiles.Add(Path.GetRelativePath(Path.Combine(root, ".git"), file), File.ReadAllBytes(file));
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed\n", new UTF8Encoding(false));
        Call("add", "seed.txt");
        Call("commit", "-m", "Seed");
        var first = Call("rev-parse", "HEAD");
        Call("branch", "spare");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllText(Path.Combine(root, "nested", "child.txt"), "child\n", new UTF8Encoding(false));
        Call("add", ".");
        Call("add", "--all");
        Call("add", "-A");
        Call("commit", "-m", "Child");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "pathspec update\n");
        Call("commit", "-m", "Select tracked path", "--", "seed.txt");
        var child = Call("rev-parse", "--verify", "HEAD");
        Call("checkout", "-b", "feature");
        Call("switch", "-c", "another");
        Call("branch", "explicit", "main");
        Call("update-ref", "refs/heads/spare", child, first);
        Call("update-ref", "refs/heads/new-ref", child);
        Call("rev-parse", "--verify", "HEAD^{tree}");
        Call("rev-parse", "--verify", "refs/heads/spare");
        Call("rev-parse", "-q", "--verify", "HEAD^{commit}");
        Call("rev-parse", "--quiet", "HEAD");
        Call("commit", "--allow-empty", "-m", "Empty", "-m", "Second paragraph");
        Call("commit", "--quiet", "--allow-empty", "-m", "Quiet empty");
        var identityEnvironment = FixedEnvironment.ToDictionary(pair => pair.Key, pair => pair.Value);
        identityEnvironment["GIT_AUTHOR_NAME"] = "Other Author";
        identityEnvironment["GIT_AUTHOR_EMAIL"] = "author@example.com";
        identityEnvironment["GIT_COMMITTER_NAME"] = "Other Committer";
        identityEnvironment["GIT_COMMITTER_EMAIL"] = "committer@example.com";
        identityEnvironment["GIT_COMMITTER_DATE"] = "2026-01-03T03:04:05.0000000+02:30";
        CallWithEnvironment(identityEnvironment, "commit", "--allow-empty", "-m", "Explicit identities");

        var bigFile = Path.Combine(root, "big.txt");
        var bigContent = string.Concat(Enumerable.Range(0, 20).Select(i => $"{i:D2} {new string('x', 26)}\n"));
        File.WriteAllText(bigFile, bigContent, new UTF8Encoding(false));
        Call("add", "big.txt");
        Call("commit", "-m", "Add big file");
        File.WriteAllText(bigFile, bigContent[..30], new UTF8Encoding(false));
        Call("add", "big.txt");
        var launchesBeforeRewrite = launched;
        Call("commit", "-m", "Rewrite big file");
        Assert.Equal(launchesBeforeRewrite + 1, launched);
        // RunScript records the actual git summary for exact comparison between runs;
        // large deletions must launch git even when it does not label them a rewrite.

        var head = RealGit(root, "rev-parse", "HEAD");
        var status = RealGit(root, "status", "--porcelain");
        var index = RealGit(root, "ls-files", "--stage");
        var subjects = Directory.EnumerateFiles(Path.Combine(root, ".git", "logs"), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).SelectMany(file => File.ReadAllLines(file)
                .Select(line => Path.GetRelativePath(Path.Combine(root, ".git", "logs"), file) + "\t" +
                    (line.Contains('\t') ? line[(line.IndexOf('\t') + 1)..] : ""))).ToArray();
        return new(calls.ToArray(), head, status, index, initFiles, subjects, serving.Served, launched);
    }

    private static void BuildSeed(string root)
    {
        using var real = InProcessGitFunnel.Observe(realOnly: true);
        foreach (var args in new[] { new[] { "init", "-b", "main" }, ["config", "user.name", "Funnel Tests"],
                     ["config", "user.email", "funnel@example.com"], ["commit", "--allow-empty", "-m", "Seed"] })
        {
            var result = RunGitProbe(root, args, FixedEnvironment);
            Assert.NotNull(result.ChildProcessId);
            Assert.True(result.Succeeded, result.ToString());
        }
    }
    private static string RealGit(string root, params string[] args)
    {
        using var real = InProcessGitFunnel.Observe(realOnly: true);
        var result = RunGitProbe(root, args);
        Assert.NotNull(result.ChildProcessId);
        Assert.True(result.Succeeded, result.ToString());
        return result.StandardOutput;
    }

    private static WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult RealGitWithFixedEnvironment(
        string root, params string[] args)
    {
        using var real = InProcessGitFunnel.Observe(realOnly: true);
        var result = RunGitProbe(root, args, FixedEnvironment);
        Assert.NotNull(result.ChildProcessId);
        Assert.True(result.Succeeded, result.ToString());
        return result;
    }
    private static Dictionary<string, string?> NativeEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP" })
            environment[key] = Environment.GetEnvironmentVariable(key);
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        return environment;
    }
    private static void ResetDirectory(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
        Directory.CreateDirectory(root);
    }
    private static void WithRepository(Action<string> test)
    {
        var root = CreateTempDirectory();
        try { test(root); }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
}
