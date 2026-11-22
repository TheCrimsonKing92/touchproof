using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ReviewerCandidateDiffSectionTests(ITestOutputHelper output)
{
    private const string Alpha = "alpha.txt";
    private const string Beta = "literal [beta].txt";
    private const string Heading = "\n\n## Candidate diff excerpts\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeliveredHunksMatchPinnedThreeDotDiffAfterRefsAndCheckoutChange(bool typed)
    {
        using var repo = DivergentCandidate();
        var scope = Capture(repo);
        var range = Range(scope);
        var oraclePaths = repo.Git("diff", "--name-only", "-z", range, "--")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(oraclePaths, scope.ChangedFiles);
        Assert.Equal(repo.Git("merge-base", scope.IntegrationCommit, scope.CandidateCommit), scope.MergeBase);
        Assert.NotEqual(scope.IntegrationCommit, scope.MergeBase);
        var oracle = repo.Git("diff", "--no-ext-diff", "--no-textconv", "--no-color", range, "--");

        Write(repo, Alpha, "later-commit-sentinel\n");
        repo.Git("commit", "-am", "Advance candidate");
        repo.Git("update-ref", "refs/heads/trunk", "HEAD");
        Write(repo, Alpha, "staged-sentinel\n");
        repo.Git("add", Alpha);
        Write(repo, Beta, "unstaged-sentinel\n");

        var dispatch = Prepare(repo, NewWork(AgentRole.Reviewer), typed, scope);
        var prompt = File.ReadAllText(dispatch.PromptPath);
        var section = CandidateSection(prompt);
        Assert.Contains($"HEAD commit: {scope.CandidateCommit}", prompt);
        Assert.Contains($"### {Alpha}\n{Hunks(oracle, Alpha)}", section);
        Assert.Contains($"### {Beta}\n{Hunks(oracle, Beta)}", section);
        Assert.Equal(2, Hunks(oracle, Alpha).Split("\n@@ ", StringSplitOptions.None).Length);
        foreach (var sentinel in new[] { "trunk-only-sentinel", "later-commit-sentinel", "staged-sentinel", "unstaged-sentinel" })
            Assert.DoesNotContain(sentinel, section);
        Assert.Equal(typed, dispatch.Task.LastDispatch?.ContextPackageReceipt is not null);
        if (dispatch.Task.LastDispatch?.ContextPackageReceipt is { } receipt)
        {
            Assert.Equal(Encoding.UTF8.GetByteCount(prompt), receipt.RenderedPromptBytes);
            Assert.Equal(prompt.Length, receipt.RenderedPromptCharacters);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordsPairedFullReviewerRendersAndIndependentGitDiff(bool typed)
    {
        using var repo = DivergentCandidate();
        var scope = Capture(repo);
        var work = NewWork(AgentRole.Reviewer);
        var snapshot = work.Kernel.ExportSnapshot();
        string? before = null;
        Prepare(repo, Restore(work, snapshot), typed, scope, baseline => before = baseline);
        Assert.NotNull(before);
        var dispatch = Prepare(repo, Restore(work, snapshot), typed, scope);
        var after = File.ReadAllText(dispatch.PromptPath);
        Assert.DoesNotContain(Heading, before);
        Assert.StartsWith(before, after);
        var section = CandidateSection(after);
        foreach (var path in scope.ChangedFiles)
            Assert.Contains($"### {path}\n", section);
        Assert.DoesNotContain("[Excerpt truncated]", section);
        Assert.DoesNotContain("Omitted:", section);
        var oracleDiff = repo.Git("diff", "--no-ext-diff", "--no-textconv", "--no-color", Range(scope), "--");
        output.WriteLine($"Reviewer prompt measurement, typed={typed}, integration={scope.IntegrationCommit}, " +
            $"mergeBase={scope.MergeBase}, candidate={scope.CandidateCommit}");
        output.WriteLine($"Before {Encoding.UTF8.GetByteCount(before)} bytes, {before.Length} characters. " +
            $"After {Encoding.UTF8.GetByteCount(after)} bytes, {after.Length} characters.");
        output.WriteLine($"Excerpted paths {string.Join(", ", scope.ChangedFiles)}. Truncated and omitted paths: none.");
        output.WriteLine($"Before excerpts\n{before}\nDelivered prompt\n{after}\nIndependent pinned Git diff\n{oracleDiff}");
    }

    [Fact]
    public void UsesFullScopePastPreviewLimit()
    {
        using var repo = DivergentCandidate();
        for (var i = 0; i < 121; i++)
            Write(repo, $"b{i:D3}", "");
        Write(repo, "z.txt", "beyond-preview-sentinel\n");
        Commit(repo, "More paths than the prompt preview");
        var scope = Capture(repo);
        Assert.True(Array.IndexOf(scope.ChangedFiles.ToArray(), "z.txt") >= 120);

        var dispatch = Prepare(repo, NewWork(AgentRole.Reviewer), true, scope);
        Assert.Contains("### z.txt\n@@ -0,0 +1 @@\n+beyond-preview-sentinel", CandidateSection(File.ReadAllText(dispatch.PromptPath)));
    }

    [Fact]
    public void BoundsEveryFileAndEntireSectionInUtf8AndIdentifiesOmittedPaths()
    {
        using var repo = new InProcessGitFixture();
        repo.Git("switch", "-c", "candidate");
        var paths = Enumerable.Range(0, 6).Select(i => $"wide-{i}.txt").ToArray();
        foreach (var path in paths)
            Write(repo, path, string.Concat(Enumerable.Repeat("界🙂", 3000)) + "\n");
        Commit(repo, "Large scoped files");
        var scope = Capture(repo);
        Assert.Equal(paths, scope.ChangedFiles);
        var section = ReviewerCandidateDiffSection.Render(repo.Worktree, scope, scope.TotalChangedFileCount);
        Assert.InRange(Encoding.UTF8.GetByteCount(section), 16000, ReviewerCandidateDiffSection.SectionBytes);
        Assert.DoesNotContain("\ufffd", section);
        Assert.Contains("All 2 remaining paths in the authoritative changed-file scope are omitted", section);
        Assert.Contains($"git diff {Range(scope)}", section);
        Assert.DoesNotContain("### wide-4.txt", section);
        var files = section.Split("\n### ", StringSplitOptions.None).Skip(1).ToArray();
        Assert.Equal(4, files.Length);
        foreach (var file in files)
        {
            var excerpt = file[..(file.IndexOf("[Excerpt truncated]\n", StringComparison.Ordinal) + "[Excerpt truncated]\n".Length)];
            Assert.InRange(Encoding.UTF8.GetByteCount("\n### " + excerpt), 1, ReviewerCandidateDiffSection.PerFileBytes);
            Assert.Contains("@@ -0,0 +1 @@\n+界🙂", excerpt);
        }
    }

    [Fact]
    public void TruncatesOversizedLineAtUtf8BoundaryWithoutReadingItsTail()
    {
        const string header = "@@ -0,0 +1 @@\n";
        const string marker = "\n[Excerpt truncated]\n";
        var input = Encoding.UTF8.GetBytes($"diff --git a/a b/a\n{header}+界🙂" + new string('x', 16000));
        using var stream = new FirstChunkOnly(input);
        var allowance = Encoding.UTF8.GetByteCount(header + "+界" + marker) + 1;
        var result = ReviewerCandidateDiffSection.ReadPatch(stream, allowance);
        Assert.Equal(ReviewerCandidateDiffSection.PatchKind.Truncated, result.Kind);
        Assert.Equal(header + "+界" + marker, result.Text);
    }

    [Fact]
    public void KeepsCompleteUnicodeLineAtItsExactByteBoundary()
    {
        const string hunk = "@@ -0,0 +1 @@\n+é🙂\n";
        const string marker = "\n[Excerpt truncated]\n";
        var input = Encoding.UTF8.GetBytes("diff --git a/a b/a\n" + hunk);
        var allowance = Encoding.UTF8.GetByteCount(hunk + marker);
        using var exact = new MemoryStream(input);
        using var shortByOne = new MemoryStream(input);

        Assert.Equal(new ReviewerCandidateDiffSection.Patch(ReviewerCandidateDiffSection.PatchKind.Complete, hunk),
            ReviewerCandidateDiffSection.ReadPatch(exact, allowance));
        Assert.Equal(new ReviewerCandidateDiffSection.Patch(ReviewerCandidateDiffSection.PatchKind.Truncated,
            hunk.TrimEnd('\n') + marker), ReviewerCandidateDiffSection.ReadPatch(shortByOne, allowance - 1));
    }

    [Fact]
    public void LabelsBinaryRenameDeletionAndNoHunkChanges()
    {
        using var repo = new InProcessGitFixture();
        Write(repo, ".gitattributes", "binary.dat -diff\n");
        Write(repo, "old.txt", string.Concat(Enumerable.Range(1, 32).Select(i => $"rename line {i:D2}\n")));
        Write(repo, "only-rename.txt", "identical\n");
        Write(repo, "removed.txt", "removed-text\n");
        Commit(repo, "Before file changes");
        repo.Git("branch", "scope-base");
        repo.Git("mv", "old.txt", "new.txt");
        File.AppendAllText(Path.Combine(repo.Worktree, "new.txt"), "rename extra line\n");
        repo.Git("mv", "only-rename.txt", "renamed.txt");
        repo.Git("rm", "removed.txt");
        File.WriteAllBytes(Path.Combine(repo.Worktree, "binary.dat"), Encoding.UTF8.GetBytes("\0binary-payload-sentinel"));
        Write(repo, "empty.txt", "");
        Commit(repo, "Change file forms");
        var scope = new WorkerGitContext().ReadReviewerChangedFileScope(repo.Worktree, "scope-base");
        Write(repo, ".gitattributes", "binary.dat diff\n");
        var section = ReviewerCandidateDiffSection.Render(repo.Worktree, scope, scope.TotalChangedFileCount);
        Assert.Contains("### binary.dat\nOmitted: binary file.", section);
        Assert.DoesNotContain("binary-payload-sentinel", section);
        Assert.Contains("### old.txt → new.txt\n@@", section);
        Assert.Contains("+rename extra line", section);
        Assert.Contains("### removed.txt\n@@ -1 +0,0 @@\n-removed-text", section);
        Assert.Contains("### only-rename.txt → renamed.txt\nNo textual hunks.", section);
        Assert.Contains("### empty.txt\nNo textual hunks.", section);
        Assert.Contains($"git diff {Range(scope)}", section);
    }

    [Theory]
    [InlineData("integration")]
    [InlineData("merge-base")]
    [InlineData("candidate")]
    public void MissingCapturedRevisionPreservesDeliveredScope(string missing)
    {
        using var repo = DivergentCandidate();
        var scope = Capture(repo);
        const string absent = "9999999999999999999999999999999999999999";
        scope = missing switch
        {
            "integration" => scope with { IntegrationCommit = absent },
            "merge-base" => scope with { MergeBase = absent },
            _ => scope with { CandidateCommit = absent },
        };
        var prompt = File.ReadAllText(Prepare(repo, NewWork(AgentRole.Reviewer), false, scope).PromptPath);
        var section = CandidateSection(prompt);
        Assert.Contains(Alpha, prompt[..prompt.IndexOf(Heading, StringComparison.Ordinal)]);
        Assert.Contains(Beta, prompt[..prompt.IndexOf(Heading, StringComparison.Ordinal)]);
        Assert.Contains("a captured commit or merge base is unavailable", section);
        Assert.Contains($"git diff {Range(scope)}", section);
        Assert.DoesNotContain("@@", section);
    }

    [Fact]
    public void DiscardsPatchWhenGitExitsNonzeroAfterWritingHunks()
    {
        using var repo = new InProcessGitFixture();
        Write(repo, "left.txt", "before\n");
        Write(repo, "right.txt", "after\n");
        string[] args = ["diff", "--no-index", "--no-color", "--", "left.txt", "right.txt"];
        var oracle = GitCli.RunDirect(repo.Worktree, args);
        Assert.Equal(1, oracle.ExitCode);
        Assert.Contains("+after", oracle.Output);

        var patch = ReviewerCandidateDiffSection.ReadGitPatch(repo.Worktree, args, 512);
        Assert.Equal(ReviewerCandidateDiffSection.PatchKind.Failed, patch.Kind);
        Assert.Empty(patch.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlannerPromptIsIdenticalWithReviewerScopePresent(bool typed)
    {
        using var repo = DivergentCandidate();
        var scope = Capture(repo);
        var work = NewWork(AgentRole.Planner);
        var snapshot = work.Kernel.ExportSnapshot();
        var before = File.ReadAllBytes(Prepare(repo, Restore(work, snapshot), typed, null).PromptPath);
        var after = File.ReadAllBytes(Prepare(repo, Restore(work, snapshot), typed, scope).PromptPath);
        Assert.Equal(before, after);
        Assert.DoesNotContain(Heading, Encoding.UTF8.GetString(after));
    }

    [Fact]
    public void RestoredPromptKeepsTheRecordedCandidateAndScope()
    {
        using var repo = DivergentCandidate();
        var scope = Capture(repo);
        var work = NewWork(AgentRole.Reviewer);
        var dispatch = Prepare(repo, work, true, scope);
        Assert.NotNull(dispatch.Task.LastDispatch?.ReviewerScope);
        File.Delete(dispatch.PromptPath);
        Write(repo, Alpha, "later-restore-sentinel\n");
        repo.Git("commit", "-am", "Move candidate before restoration");
        repo.Git("update-ref", "refs/heads/trunk", "HEAD");

        WorkerProfileDispatcher.RestorePreparedContextIfMissing(work.Kernel, work.Goal, work.Task, "trunk",
            backlogStorePath: Path.Combine(repo.Repository, "backlog.db"));
        var prompt = File.ReadAllText(dispatch.PromptPath);
        Assert.Contains($"HEAD commit: {scope.CandidateCommit}", prompt);
        Assert.Contains("+candidate-first", CandidateSection(prompt));
        Assert.DoesNotContain("later-restore-sentinel", CandidateSection(prompt));
    }

    private static InProcessGitFixture DivergentCandidate()
    {
        var repo = new InProcessGitFixture(commit: false);
        var lines = Enumerable.Range(1, 32).Select(i => $"baseline-{i:D2}").ToArray();
        Write(repo, Alpha, string.Join('\n', lines) + "\n");
        Commit(repo, "Common base");
        repo.Git("branch", "candidate");
        Write(repo, "trunk-only.txt", "trunk-only-sentinel\n");
        Commit(repo, "Diverge integration branch");
        repo.Git("switch", "candidate");
        lines[1] = "candidate-first";
        lines[29] = "candidate-second";
        Write(repo, Alpha, string.Join('\n', lines) + "\n");
        Write(repo, Beta, "candidate-beta\n");
        Commit(repo, "Multi-file candidate");
        File.AppendAllText(Path.Combine(repo.Worktree, ".git", "info", "exclude"),
            "\n/.orchestrator-context/\n/.orchestrator-handoff.md\n/prompts/\n/backlog.db*\n");
        return repo;
    }

    private static ReviewerChangedFileScope Capture(InProcessGitFixture repo) =>
        new WorkerGitContext().ReadReviewerChangedFileScope(repo.Worktree, "trunk");

    private static string Range(ReviewerChangedFileScope scope) => $"{scope.IntegrationCommit}...{scope.CandidateCommit}";

    private static string Hunks(string diff, string path)
    {
        var prefix = $"diff --git a/{path} b/{path}";
        var fileStart = diff.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(fileStart >= 0, $"No Git patch for {path}");
        var bodyStart = diff.IndexOf("\n@@ ", fileStart, StringComparison.Ordinal) + 1;
        Assert.True(bodyStart > fileStart, $"No Git hunk for {path}");
        var end = diff.IndexOf("\ndiff --git ", bodyStart, StringComparison.Ordinal);
        return end < 0 ? diff[bodyStart..] : diff[bodyStart..end];
    }

    private static string CandidateSection(string prompt)
    {
        var index = prompt.IndexOf(Heading, StringComparison.Ordinal);
        Assert.True(index >= 0, "Candidate diff section missing");
        return prompt[index..];
    }

    private static void Write(InProcessGitFixture repo, string path, string text) =>
        File.WriteAllText(Path.Combine(repo.Worktree, path), text);

    private static void Commit(InProcessGitFixture repo, string message)
    {
        repo.Git("add", "-A");
        repo.Git("commit", "-m", message);
    }

    private static Work NewWork(AgentRole role)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Compare the candidate to its integration branch.", role);
        var goal = kernel.CreateGoal("Review a fixture with candidate changes.", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return new Work(kernel, goal, task);
    }

    private static Work Restore(Work work, OrchestratorSnapshot snapshot)
    {
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot);
        return new Work(kernel, kernel.GetGoal(work.Goal.Id), kernel.GetTask(work.Goal.Id, work.Task.Id));
    }

    private static WorkerProfileDispatchResult Prepare(InProcessGitFixture repo, Work work, bool typed,
        ReviewerChangedFileScope? scope, Action<string>? observeBefore = null) =>
        WorkerProfileDispatcher.PrepareTask(work.Kernel, work.Goal, work.Task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(repo.Repository, "prompts"), repo.Worktree, DateTimeOffset.Parse("2026-11-17T12:00:00Z"),
            providerName: typed ? "OpenAI" : null,
            modelName: typed ? AgentCatalog.OpenAiSolSubscriptionModelAlias : null,
            allowPendingRecordedDispatchRefresh: true, integrationBranch: "trunk",
            reviewerMergeTreeClean: work.Task.RequiredRole == AgentRole.Reviewer ? true : null,
            reviewerMergeTreeConflictPaths: work.Task.RequiredRole == AgentRole.Reviewer ? [] : null,
            reviewerMergeTreeTotalConflictPathCount: work.Task.RequiredRole == AgentRole.Reviewer ? 0 : null,
            backlogStorePath: Path.Combine(repo.Repository, "backlog.db"),
            reviewerScope: scope, observeReviewerPromptBeforeExcerpts: observeBefore);

    private sealed record Work(AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task);

    private sealed class FirstChunkOnly(byte[] content) : MemoryStream(content)
    {
        private bool read;

        public override int Read(Span<byte> buffer)
        {
            Assert.False(read, "Reader consumed more than the first chunk of an oversized line.");
            read = true;
            return base.Read(buffer);
        }
    }
}
