using System.Text;
using System.Text.Json;
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

    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    public void TypedReviewerExcerptsUseOnlySpareProviderCapacity(int spareCharacters)
    {
        using var repo = DivergentCandidate();
        var scope = Capture(repo);
        const int probePadding = 700_000;
        string? probeBaseline = null;
        var probe = Prepare(repo, NewWork(AgentRole.Reviewer, probePadding), true, scope, text => probeBaseline = text);
        Assert.NotNull(probeBaseline);
        Assert.True(CandidateSection(File.ReadAllText(probe.PromptPath)).Length > spareCharacters);

        var tokenBudget = WorkerPromptInputBudget.InputTokenBudget(
            "OpenAI", AgentCatalog.OpenAiSolSubscriptionModelAlias, "codex-cli");
        var baselineLength = tokenBudget * 4 - spareCharacters;
        var padding = probePadding + baselineLength - probeBaseline.Length;
        string? baseline = null;
        var dispatch = Prepare(repo, NewWork(AgentRole.Reviewer, padding), true, scope, text => baseline = text);
        Assert.NotNull(baseline);
        Assert.Equal(baselineLength, baseline.Length);
        var delivered = File.ReadAllText(dispatch.PromptPath);
        Assert.StartsWith(baseline, delivered);
        Assert.Contains("## Reviewer Changed-File Scope", delivered);
        Assert.Contains($"git diff {Range(scope)}", baseline);
        Assert.InRange(WorkerPromptInputBudget.CountTokens(delivered), 1, tokenBudget);
        var receipt = Assert.IsType<WorkerContextPackageReceipt>(dispatch.Task.LastDispatch?.ContextPackageReceipt);
        Assert.Equal(delivered.Length, receipt.RenderedPromptCharacters);
        Assert.Equal(WorkerPromptInputBudget.CountTokens(delivered), receipt.ModelInputTokenEstimate);
        if (spareCharacters == 0)
        {
            Assert.Equal(baseline, delivered);
            Assert.DoesNotContain(Heading, delivered);
            Assert.Contains($"All paths omitted if no excerpts follow. git diff {Range(scope)}", delivered);
        }
        else
            Assert.Contains("omitted by the excerpt budget", CandidateSection(delivered));
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

    [Fact]
    public void DeliversBothHunksForTypeChangeWithIdenticalBlobIds()
    {
        using var repo = new InProcessGitFixture(commit: false);
        const string path = "link.txt";
        Write(repo, path, "same-blob");
        Commit(repo, "Regular file");
        var blob = repo.Git("rev-parse", $"HEAD:{path}");
        repo.Git("switch", "-c", "candidate");
        repo.Git("update-index", "--cacheinfo", $"120000,{blob},{path}");
        repo.Git("commit", "-m", "Symlink with the same blob");
        var scope = Capture(repo);
        Assert.Contains($":100644 120000 {blob} {blob} T\t{path}",
            repo.Git("diff", "--raw", "--no-abbrev", Range(scope)));

        var dispatch = Prepare(repo, NewWork(AgentRole.Reviewer), true, scope);
        var section = CandidateSection(File.ReadAllText(dispatch.PromptPath));
        Assert.Contains($"### {path}\n@@ -1 +0,0 @@\n-same-blob", section);
        Assert.Contains("@@ -0,0 +1 @@\n+same-blob", section);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisplayEscapesControlPathInScopeAndExcerpts(bool typed)
    {
        using var repo = new InProcessGitFixture(commit: false);
        const string path = "one\n- decoy.txt";
        const string escapedPath = "\"one\\n- decoy.txt\"";
        const string literalPath = escapedPath;
        var escapedLiteralPath = JsonSerializer.Serialize(literalPath);
        repo.Git("commit", "--allow-empty", "-m", "Base");
        Write(repo, "blob.txt", "control-path-sentinel\n");
        Write(repo, "literal.txt", "literal-path-sentinel\n");
        var blob = repo.Git("hash-object", "-w", "blob.txt");
        var literalBlob = repo.Git("hash-object", "-w", "literal.txt");
        // Construct the commit without checking out a name Windows cannot represent.
        var tree = GitCli.RunWithStandardInput(repo.Worktree, GitCli.DefaultTimeoutMilliseconds,
            $"100644 blob {blob}\t{path}\0" +
            $"100644 blob {literalBlob}\t{literalPath}\0", "mktree", "-z");
        Assert.True(tree.Succeeded && !tree.DrainTimedOut, tree.Error);
        var candidate = repo.Git("commit-tree", tree.Output.Trim(), "-p", "HEAD", "-m", "Control path");
        repo.Git("update-ref", "refs/heads/candidate", candidate);
        repo.Git("symbolic-ref", "HEAD", "refs/heads/candidate");
        var scope = Capture(repo);
        Assert.Equal(new[] { literalPath, path }, scope.ChangedFiles);

        var work = NewWork(AgentRole.Reviewer);
        var dispatch = Prepare(repo, work, typed, scope);
        var prompt = File.ReadAllText(dispatch.PromptPath);
        var preview = prompt[..prompt.IndexOf(Heading, StringComparison.Ordinal)];
        Assert.Contains($"- {escapedPath}{Environment.NewLine}", preview);
        Assert.Contains($"- {escapedLiteralPath}{Environment.NewLine}", preview);
        Assert.DoesNotContain(path, preview);
        var section = CandidateSection(prompt);
        Assert.Contains($"### {escapedPath}\n@@ -0,0 +1 @@\n+control-path-sentinel", section);
        Assert.Contains($"### {escapedLiteralPath}\n@@ -0,0 +1 @@\n+literal-path-sentinel", section);

        var checklistPath = typed ? DeliveredChecklistPath(repo, work) :
            Path.Combine(repo.Worktree, ".orchestrator-context", work.Goal.Id.Value, "deterministic-verification.md");
        var checklistRows = File.ReadAllLines(checklistPath);
        var countIndex = Array.IndexOf(checklistRows, "Committed branch-diff files: 2");
        Assert.True(countIndex >= 0, "Committed branch-diff file count missing from checklist");
        Assert.Equal(new[] { $"- `{escapedLiteralPath}`", $"- `{escapedPath}`", string.Empty },
            checklistRows.Skip(countIndex + 1).Take(3).ToArray());
    }

    [Fact]
    public void UnicodePathSurvivesRawScopeCaptureAndExcerptRendering()
    {
        using var repo = new InProcessGitFixture(commit: false);
        const string path = "review-\u65E5\u672C\u8A9E.txt";
        repo.Git("commit", "--allow-empty", "-m", "Base");
        repo.Git("switch", "-c", "candidate");
        Write(repo, path, "unicode-path-sentinel\n");
        Commit(repo, "Unicode filename");
        // Git's quoted output is ASCII even when the host console code page is legacy.
        Assert.Equal("\"review-\\346\\227\\245\\346\\234\\254\\350\\252\\236.txt\"",
            repo.Git("-c", "core.quotePath=true", "diff", "--name-only", "trunk...HEAD"));

        var scope = Capture(repo);
        Assert.Equal(path, Assert.Single(scope.ChangedFiles));
        var prompt = File.ReadAllText(Prepare(repo, NewWork(AgentRole.Reviewer), true, scope).PromptPath);
        Assert.Contains($"- {path}{Environment.NewLine}", prompt[..prompt.IndexOf(Heading, StringComparison.Ordinal)]);
        Assert.Contains($"### {path}\n@@ -0,0 +1 @@\n+unicode-path-sentinel", CandidateSection(prompt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeliversDistinctHunksForNonUtf8GitPaths(bool typed)
    {
        using var repo = new InProcessGitFixture(commit: false);
        const string first = "\"raw-\\200.txt\"";
        const string second = "\"raw-\\201.txt\"";
        const string quotedLiteral = "\"\\\"raw-\\\\200.txt\\\"\"";
        var entries = new[] { (first, "byte-80"), (second, "byte-81"), (quotedLiteral, "literal") };
        repo.Git("commit", "--allow-empty", "-m", "Base");
        repo.Git("switch", "-c", "candidate");
        var records = new StringBuilder();
        foreach (var (quotedPath, body) in entries)
        {
            Write(repo, "blob.txt", body + "\n");
            var blob = repo.Git("hash-object", "-w", "blob.txt");
            records.Append($"100644 blob {blob}\t{quotedPath}\n");
        }
        var tree = GitCli.RunWithStandardInput(repo.Worktree, GitCli.DefaultTimeoutMilliseconds, records.ToString(), "mktree");
        Assert.True(tree.Succeeded, tree.Error);
        var candidate = repo.Git("commit-tree", tree.Output.Trim(), "-p", "HEAD", "-m", "Byte-distinct paths");
        repo.Git("update-ref", "HEAD", candidate);

        var scope = Capture(repo);
        var oracle = repo.Git("-c", "core.quotePath=true", "diff", "--name-only", Range(scope))
            .Split('\n', StringSplitOptions.TrimEntries);
        Assert.Equal(new[] { quotedLiteral, first, second }, oracle);
        Assert.Equal(oracle.Length, scope.ChangedFiles.Distinct(StringComparer.Ordinal).Count());
        var restored = Assert.IsType<ReviewerChangedFileScope>(
            JsonSerializer.Deserialize<ReviewerChangedFileScope>(JsonSerializer.Serialize(scope)));
        Assert.Equal(scope.ChangedFiles.ToArray(), restored.ChangedFiles.ToArray());

        repo.Git("config", "diff.noprefix", "true");
        var prompt = File.ReadAllText(Prepare(repo, NewWork(AgentRole.Reviewer), typed, restored).PromptPath);
        var section = CandidateSection(prompt);
        var preview = prompt[..prompt.IndexOf(Heading, StringComparison.Ordinal)];
        var labels = new[] { first, second, JsonSerializer.Serialize(first) };
        for (var index = 0; index < entries.Length; index++)
        {
            Assert.Contains($"- {labels[index]}{Environment.NewLine}", preview);
            Assert.Contains($"### {labels[index]}\n@@ -0,0 +1 @@\n+{entries[index].Item2}\n", section);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedPreparationPreservesLiteralGitPathsAcrossPreviewCap(bool capped)
    {
        using var repo = new InProcessGitFixture(commit: false);
        var rootPaths = new (string LiteralPath, string GitPath)[]
        {
            ("caf\u00e9.txt", "\"caf\\303\\251.txt\""),
            ("cafe\u0301.txt", "\"cafe\\314\\201.txt\""),
            (" leading.txt", " leading.txt"),
            ("leading.txt", "leading.txt"),
            ("dir\\file.txt", "\"dir\\\\file.txt\"")
        };
        var extraPaths = capped
            ? Enumerable.Range(0, WorkerGitContext.ReviewerChangedFilePromptMaxFiles).Select(index => $"z-{index:D3}.txt").ToArray()
            : [];
        repo.Git("commit", "--allow-empty", "-m", "Base");
        repo.Git("switch", "-c", "candidate");
        Write(repo, "blob.txt", "literal-path-sentinel\n");
        var blob = repo.Git("hash-object", "-w", "blob.txt");
        // mktree takes path components. Build the dir/file.txt tree separately from the
        // root path containing a literal backslash. ASCII quotes keep stdin code-page independent.
        var subtree = GitCli.RunWithStandardInput(repo.Worktree, GitCli.DefaultTimeoutMilliseconds,
            $"100644 blob {blob}\tfile.txt\n", "mktree");
        Assert.True(subtree.Succeeded, subtree.Error);
        var quotedPaths = rootPaths.Select(path => path.GitPath);
        var treeInput = string.Concat(quotedPaths.Concat(extraPaths).Select(path => $"100644 blob {blob}\t{path}\n")) +
            $"040000 tree {subtree.Output.Trim()}\tdir\n";
        var tree = GitCli.RunWithStandardInput(repo.Worktree, GitCli.DefaultTimeoutMilliseconds, treeInput, "mktree");
        Assert.True(tree.Succeeded, tree.Error);
        var candidate = repo.Git("commit-tree", tree.Output.Trim(), "-p", "HEAD", "-m", "Literal path identities");
        repo.Git("update-ref", "HEAD", candidate);

        // Check the committed bytes through Git before exercising the scope decoder.
        // The whitespace in a path is significant, including at the start of stdout.
        var gitPaths = GitCli.RunDirect(repo.Worktree, "-c", "core.quotePath=true",
            "diff", "--name-only", $"trunk...{candidate}", "--");
        Assert.True(gitPaths.Succeeded && !gitPaths.DrainTimedOut, gitPaths.Error);
        Assert.Equal(quotedPaths.Append("dir/file.txt").Concat(extraPaths).Order(StringComparer.Ordinal),
            gitPaths.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));

        var scope = Capture(repo);
        var literalPaths = rootPaths.Select(path => path.LiteralPath).Append("dir/file.txt");
        var expected = literalPaths.Concat(extraPaths).Order(StringComparer.Ordinal);
        Assert.Equal(expected, scope.ChangedFiles.Order(StringComparer.Ordinal));
        var work = NewWork(AgentRole.Reviewer);
        var dispatch = Prepare(repo, work, typed: true, scope);
        var prompt = File.ReadAllText(dispatch.PromptPath);
        var section = CandidateSection(prompt);
        foreach (var path in literalPaths)
            Assert.Contains($"### {path}\n@@ -0,0 +1 @@\n+literal-path-sentinel", section);

        if (capped)
        {
            var receipt = Assert.IsType<WorkerContextPackageReceipt>(work.Task.LastDispatch?.ContextPackageReceipt);
            var artifact = Assert.Single(receipt.Sections, row => row.LogicalIdentity == "reviewer/changed-files.v1.json");
            Assert.NotNull(artifact.MandatoryRelativePath);
            using var complete = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repo.Worktree, artifact.MandatoryRelativePath)));
            Assert.Equal(expected, complete.RootElement.GetProperty("Paths").EnumerateArray().Select(path => path.GetString()));
        }
    }

    [Fact]
    public void DeliversCapturedDeletionWhenPathFilteringWouldReclassifyItAsARename()
    {
        using var repo = new InProcessGitFixture(commit: false);
        repo.Git("config", "diff.renames", "true");
        repo.Git("config", "diff.renameLimit", "1");
        string[] paths = ["entry", "other"];
        foreach (var path in paths)
            Write(repo, path, string.Concat(Enumerable.Range(1, 32).Select(i => $"{path} original line {i:D2}\n")));
        Commit(repo, "Base files");
        repo.Git("switch", "-c", "candidate");
        foreach (var path in paths)
        {
            var fullPath = Path.Combine(repo.Worktree, path);
            var original = File.ReadAllText(fullPath);
            File.Delete(fullPath);
            Directory.CreateDirectory(fullPath);
            Write(repo, $"{path}/nested.txt", original + $"{path} extra line\n");
        }
        Commit(repo, "Replace files with similar descendants");
        var scope = Capture(repo);
        foreach (var path in paths)
        {
            Assert.Contains(new ReviewerScopedChange(path, null, 'D', true), scope.Changes);
            Assert.Empty(repo.Git("--literal-pathspecs", "diff", "--diff-filter=D", Range(scope), "--", path));
        }
        var oracle = repo.Git("diff", "--no-ext-diff", "--no-textconv", "--no-color", Range(scope), "--");

        var prompt = File.ReadAllText(Prepare(repo, NewWork(AgentRole.Reviewer), true, scope).PromptPath);
        var section = CandidateSection(prompt);
        foreach (var path in paths)
        {
            Assert.Contains($"### {path}\n{Hunks(oracle, path)}", section);
            Assert.Contains($"### {path}/nested.txt\n{Hunks(oracle, $"{path}/nested.txt")}", section);
        }
    }

    [Fact]
    public void DeliveredRenamesUseOnlyHunksForTheirExactPathPair()
    {
        using var repo = new InProcessGitFixture(commit: false);
        const string oldAlpha = "alpha";
        const string oldNested = "target/nest ed.txt";
        const string newAlpha = "target";
        const string newNested = "alpha/renamed [nested].txt";
        Directory.CreateDirectory(Path.Combine(repo.Worktree, "target"));
        var alpha = string.Concat(Enumerable.Range(1, 32).Select(i => $"alpha original line {i:D2}\n"));
        var nested = string.Concat(Enumerable.Range(1, 32).Select(i => $"nested original line {i:D2}\n"));
        Write(repo, oldAlpha, alpha);
        Write(repo, oldNested, nested);
        Commit(repo, "Base files and directories");
        repo.Git("switch", "-c", "candidate");
        File.Delete(Path.Combine(repo.Worktree, oldAlpha));
        File.Delete(Path.Combine(repo.Worktree, oldNested));
        Directory.Delete(Path.Combine(repo.Worktree, "target"));
        Directory.CreateDirectory(Path.Combine(repo.Worktree, "alpha"));
        Write(repo, newAlpha, alpha + "alpha-only-edit\n");
        Write(repo, newNested, nested + "nested-only-edit\n");
        Commit(repo, "Rename across file and directory swaps");
        var scope = Capture(repo);
        Assert.Contains(new ReviewerScopedChange(newAlpha, oldAlpha, 'R', true), scope.Changes);
        Assert.Contains(new ReviewerScopedChange(newNested, oldNested, 'R', true), scope.Changes);
        var overlappingPatch = repo.Git("--literal-pathspecs", "diff", "--diff-filter=R", Range(scope), "--", newAlpha, oldAlpha);
        Assert.Contains("+alpha-only-edit", overlappingPatch);
        Assert.Contains("+nested-only-edit", overlappingPatch);

        var section = CandidateSection(File.ReadAllText(Prepare(repo, NewWork(AgentRole.Reviewer), true, scope).PromptPath));
        var blocks = section.Split("\n### ", StringSplitOptions.None).Skip(1).ToArray();
        Assert.Collection(blocks,
            block =>
            {
                Assert.StartsWith($"{oldNested} → {newNested}\n@@ ", block);
                Assert.Contains("+nested-only-edit", block);
                Assert.DoesNotContain("+alpha-only-edit", block);
            },
            block =>
            {
                Assert.StartsWith($"{oldAlpha} → {newAlpha}\n@@ ", block);
                Assert.Contains("+alpha-only-edit", block);
                Assert.DoesNotContain("+nested-only-edit", block);
            });
    }

    [Fact]
    public void DeliversCapturedRenameHunksWhenPathFilteringWouldRePairTheSources()
    {
        using var repo = new InProcessGitFixture(commit: false);
        repo.Git("config", "diff.renames", "true");
        repo.Git("config", "diff.renameLimit", "100");
        repo.Git("config", "core.autocrlf", "false");
        const string entry = "entry";
        const string other = "other";
        const string target = "target";
        const string nested = "entry/renamed.txt";
        var common = Enumerable.Range(0, 28).Select(i => $"common context line {i:D2}\n").ToArray();
        var entryLines = Enumerable.Range(0, 12).Select(i => $"entry specific line {i:D2}\n").ToArray();
        var otherLines = Enumerable.Range(0, 12).Select(i => $"other specific line {i:D2}\n").ToArray();
        Write(repo, entry, string.Concat(entryLines.Concat(common)));
        Write(repo, other, string.Concat(otherLines.Concat(common)));
        Commit(repo, "Base with competing rename sources");
        repo.Git("switch", "-c", "candidate");
        File.Delete(Path.Combine(repo.Worktree, entry));
        File.Delete(Path.Combine(repo.Worktree, other));
        Directory.CreateDirectory(Path.Combine(repo.Worktree, entry));
        Write(repo, target, string.Concat(entryLines.Concat(common.Take(12))) + "target-only-edit\n");
        Write(repo, nested, string.Concat(otherLines[..^1]) + "nested-only-edit\n" + string.Concat(common));
        Commit(repo, "Rename competing sources");

        var scope = Capture(repo);
        Assert.Equal(new[] { new ReviewerScopedChange(nested, other, 'R', true),
            new ReviewerScopedChange(target, entry, 'R', true) }, scope.Changes);
        var oracle = repo.Git("diff", "--no-ext-diff", "--no-textconv", "--no-color", Range(scope), "--");
        var filtered = repo.Git("--no-literal-pathspecs", "diff", "--diff-filter=R", scope.MergeBase,
            scope.CandidateCommit, "--", $":(top,literal){target}", $":(top,literal){entry}");
        Assert.Contains($"rename from {entry}\nrename to {nested}\n", filtered);
        Assert.DoesNotContain($"rename to {target}\n", filtered);

        var section = CandidateSection(File.ReadAllText(Prepare(repo, NewWork(AgentRole.Reviewer), true, scope).PromptPath));
        Assert.Contains($"### {other} → {nested}\n{Hunks(oracle, nested, other)}\n", section);
        Assert.Contains($"### {entry} → {target}\n{Hunks(oracle, target, entry)}\n", section);
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

    [Fact]
    public void EmptyPatchForExpectedScopedContentIsUnavailable()
    {
        var change = new ReviewerScopedChange(Alpha, null, 'D', true);
        using var stream = new MemoryStream();
        var patch = ReviewerCandidateDiffSection.ReadPatch(stream, 512, change);
        Assert.Equal(ReviewerCandidateDiffSection.PatchKind.Failed, patch.Kind);
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
    public void RestoredPromptKeepsTheRecordedCandidateAndScopeAfterSnapshotReload()
    {
        using var repo = DivergentCandidate();
        var scope = Capture(repo);
        var work = NewWork(AgentRole.Reviewer);
        var dispatch = Prepare(repo, work, true, scope);
        var originalSection = CandidateSection(File.ReadAllText(dispatch.PromptPath));
        var snapshot = JsonSerializer.Deserialize<OrchestratorSnapshot>(JsonSerializer.Serialize(work.Kernel.ExportSnapshot()))!;
        File.Delete(DeliveredChecklistPath(repo, work));
        File.Delete(dispatch.PromptPath);
        Write(repo, Alpha, "later-restore-sentinel\n");
        Write(repo, "later-only.txt", "later-path-sentinel\n");
        repo.Git("add", "--", Alpha, "later-only.txt");
        repo.Git("commit", "-m", "Move candidate before restoration");
        repo.Git("update-ref", "refs/heads/trunk", "HEAD");
        Assert.Empty(Capture(repo).Changes);

        var reloaded = Restore(work, snapshot);
        var retained = Assert.IsType<ReviewerDispatchScope>(reloaded.Task.LastDispatch?.ReviewerScope);
        Assert.Equal(scope.IntegrationCommit, retained.Scope.IntegrationCommit);
        Assert.Equal(scope.MergeBase, retained.Scope.MergeBase);
        Assert.Equal(scope.CandidateCommit, retained.Scope.CandidateCommit);
        Assert.Equal(scope.Changes.ToArray(), retained.Scope.Changes.ToArray());
        Assert.True(retained.MergeTreeClean);
        Assert.Empty(retained.MergeTreeConflictPaths);
        Assert.Equal(0, retained.MergeTreeTotalConflictPathCount);
        WorkerProfileDispatcher.RestorePreparedContextIfMissing(reloaded.Kernel, reloaded.Goal, reloaded.Task, "trunk",
            backlogStorePath: Path.Combine(repo.Repository, "backlog.db"));
        var prompt = File.ReadAllText(dispatch.PromptPath);
        Assert.Contains($"HEAD commit: {scope.CandidateCommit}", prompt);
        Assert.Contains($"git diff {Range(scope)}", prompt);
        Assert.Contains(Alpha, prompt[..prompt.IndexOf(Heading, StringComparison.Ordinal)]);
        Assert.Contains(Beta, prompt[..prompt.IndexOf(Heading, StringComparison.Ordinal)]);
        Assert.Equal(originalSection, CandidateSection(prompt));
        var checklist = File.ReadAllText(DeliveredChecklistPath(repo, reloaded));
        Assert.Contains($"Scope checks: git diff {Range(scope)}", checklist);
        Assert.Contains("Committed branch-diff files: 2", checklist);
        Assert.Contains($"- `{Alpha}`", checklist);
        Assert.Contains($"- `{Beta}`", checklist);
        Assert.DoesNotContain("later-only.txt", checklist);
    }

    [Fact]
    public void LegacyRestoreReportsMissingScopeAfterBothRefsMove()
    {
        using var repo = DivergentCandidate();
        var scope = Capture(repo);
        var work = NewWork(AgentRole.Reviewer);
        var dispatch = Prepare(repo, work, true, scope);
        var snapshot = work.Kernel.ExportSnapshot();
        var goal = Assert.Single(snapshot.Goals);
        var task = Assert.Single(goal.Tasks);
        var legacy = Assert.IsType<TaskDispatchSnapshot>(task.LastDispatch) with
        {
            ReviewerScope = null,
            BaseCommit = scope.CandidateCommit
        };
        var reloaded = Restore(work, snapshot with
        {
            Goals = [goal with { Tasks = [task with { LastDispatch = legacy, DispatchHistory = [legacy] }] }]
        });
        Assert.Null(reloaded.Task.LastDispatch?.ReviewerScope);

        File.Delete(DeliveredChecklistPath(repo, reloaded));
        File.Delete(dispatch.PromptPath);
        Write(repo, Alpha, "later-restore-sentinel\n");
        Commit(repo, "Move candidate before legacy restoration");
        repo.Git("update-ref", "refs/heads/trunk", "HEAD");
        Assert.Empty(Capture(repo).Changes);

        WorkerProfileDispatcher.RestorePreparedContextIfMissing(reloaded.Kernel, reloaded.Goal, reloaded.Task, "trunk",
            backlogStorePath: Path.Combine(repo.Repository, "backlog.db"));
        var prompt = File.ReadAllText(dispatch.PromptPath);
        Assert.Contains($"HEAD commit: {scope.CandidateCommit}", prompt);
        Assert.Contains("Scope and candidate diff excerpts are unavailable.", prompt);
        Assert.DoesNotContain("Changed files: 0", prompt);
        Assert.DoesNotContain("later-restore-sentinel", prompt);
        Assert.DoesNotContain(Heading, prompt);
        var checklist = File.ReadAllText(DeliveredChecklistPath(repo, reloaded));
        Assert.Contains("Committed branch diff unavailable:", checklist);
        Assert.Contains("Requires tests: unknown", checklist);
        Assert.DoesNotContain("Committed branch-diff files: 0", checklist);
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

    private static string Hunks(string diff, string path, string? previousPath = null)
    {
        var prefix = $"diff --git a/{previousPath ?? path} b/{path}\n";
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

    private static Work NewWork(AgentRole role, int objectivePadding = 0)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Compare the candidate to its integration branch.", role);
        var goal = kernel.CreateGoal("Review a fixture with candidate changes. " + new string('x', objectivePadding), [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return new Work(kernel, goal, task);
    }

    private static Work Restore(Work work, OrchestratorSnapshot snapshot)
    {
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot);
        return new Work(kernel, kernel.GetGoal(work.Goal.Id), kernel.GetTask(work.Goal.Id, work.Task.Id));
    }

    private static string DeliveredChecklistPath(InProcessGitFixture repo, Work work)
    {
        var receipt = Assert.IsType<WorkerContextPackageReceipt>(work.Task.LastDispatch?.ContextPackageReceipt);
        var section = Assert.Single(receipt.Sections,
            candidate => candidate.LogicalIdentity == "context/deterministic-verification.md");
        Assert.NotNull(section.MandatoryRelativePath);
        return Path.Combine(repo.Worktree, section.MandatoryRelativePath);
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
