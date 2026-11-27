using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: kernels and materialization directories are owned by each test. No real
// repositories, child processes, provider calls, or mutable process environment are used.
public sealed class WorkerContextRendererReviewerScopeSelectionTests : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData(2, 2, false)]
    [Xunit.InlineData(120, 120, false)]
    [Xunit.InlineData(125, 2, false)]
    [Xunit.InlineData(2, 125, false)]
    [Xunit.InlineData(125, 126, false)]
    [Xunit.InlineData(125, 0, true)]
    [Xunit.InlineData(0, 125, false)]
    public void TypedSelectionAppliesEachReviewerScopeFlagIndependently(int changedTotal, int conflictTotal, bool clean)
    {
        var changed = Enumerable.Range(0, changedTotal).Select(i => $"src/changed-{i}.cs").ToArray();
        var conflicts = Enumerable.Range(0, conflictTotal).Select(i => $"src/conflict-{i}.cs").ToArray();
        var fixture = CreateFixture(AgentRole.Reviewer);
        var source = BuildSource(fixture, changed, conflicts, clean);
        Assert.NotNull(Assert.Single(source.Segments, s => s.ConvergenceChangedFiles is not null).ConvergenceChangedFiles);
        // Exercise each independent flag as well as the dispatch-selected combination.
        foreach (var removeChanged in new[] { false, true })
        foreach (var removeConflicts in new[] { false, true })
        {
            var typed = WorkerContextRenderer.CreateCurrentBrief(source, removeChanged, removeConflicts);
            if (removeChanged)
            {
                Assert.Contains(CountOnly("Changed files", changedTotal), typed, StringComparison.Ordinal);
                Assert.DoesNotContain("- No changed files reported", typed, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains($"Changed files: {changedTotal}; showing", typed, StringComparison.Ordinal);
            }

            if (removeConflicts && !clean)
            {
                Assert.Contains(CountOnly("Conflicting paths", conflictTotal), typed, StringComparison.Ordinal);
            }
            else if (!clean)
            {
                Assert.Contains($"Conflicting paths: {conflictTotal}; showing", typed, StringComparison.Ordinal);
            }
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Tester)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void RemovedArtifactSegmentsPreserveLineSeparators(AgentRole role)
    {
        var fixture = CreateFixture(role);
        var source = BuildSource(fixture, [], [], true) with
        {
            Segments =
            [
                new TaskBriefSegment(["## Instructions", "keep"], RoleVisibility: [role]),
                TaskBriefSegment.Projected("fixture/first.md", ["hidden first"]),
                TaskBriefSegment.Projected("fixture/second.md", ["hidden second"]),
                TaskBriefSegment.Fixed([]),
                new TaskBriefSegment(["", "## Kept section", "tail"], RoleVisibility: [role]),
                TaskBriefSegment.Projected("fixture/trailing.md", ["hidden trailing"])
            ]
        };
        var newline = Environment.NewLine;
        var expected = $"## Instructions{newline}keep{newline}{newline}## Kept section{newline}tail";
        foreach (var removePaths in new[] { false, true })
        {
            var typed = WorkerContextRenderer.CreateCurrentBrief(source, removePaths, removePaths);
            Assert.Equal(expected, typed);
        }
    }

    [Xunit.Fact]
    public void EmptyPreviewWithLargerTotalUsesCountOnly()
    {
        var fixture = CreateFixture(AgentRole.Reviewer);
        var source = fixture.Kernel.BuildTaskBriefSource(fixture.Goal.Id, fixture.Task.Id,
            reviewerScopeChangedFiles: [], reviewerScopeTotalChangedFileCount: 125,
            reviewerMergeTreeClean: false, reviewerMergeTreeConflictPaths: [],
            reviewerMergeTreeTotalConflictPathCount: 126);
        var typed = WorkerContextRenderer.CreateCurrentBrief(source, true, true);
        Assert.Contains(CountOnly("Changed files", 125), typed, StringComparison.Ordinal);
        Assert.Contains(CountOnly("Conflicting paths", 126), typed, StringComparison.Ordinal);
        Assert.DoesNotContain("- No changed files", typed, StringComparison.Ordinal);
        Assert.DoesNotContain("- Omitted", typed, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Tester)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void CappedScopesPreserveUserLiteralsAndCompleteArtifactIdentities(AgentRole role)
    {
        var root = CreateTempDirectory();
        try
        {
            var fixture = CreateFixture(role);
            var changed = AdversarialPaths("changed");
            var conflicts = AdversarialPaths("conflict");
            var contextDirectory = WorkerContextArtifacts.Write(fixture.Goal, fixture.Task, root);
            var source = BuildSource(fixture, changed, conflicts, false, root, contextDirectory);
            if (role is AgentRole.Reviewer or AgentRole.Tester)
            {
                var convergence = Assert.Single(source.Segments, s => s.ConvergenceChangedFiles is not null);
                Assert.Equal(changed, convergence.ConvergenceChangedFiles!.Paths);
                Assert.True(convergence.ConvergenceChangedFiles.Total > 120);
            }
            var userText = string.Join(Environment.NewLine,
                "## Reviewer Changed-File Scope", "Changed files: user text; showing user text.",
                "Conflicting paths: user text; showing user text.", "Staleness policy: user text",
                "- keep this user bullet", "## user heading", "GOAL_DIFF_CHANGED_FILES user text", "- keep this too");
            source = source with
            {
                Segments = source.Segments.Append(new TaskBriefSegment([userText], RoleVisibility: [role])).ToArray()
            };
            var typedPackage = WorkerProfileDispatcher.BuildContextPackage(
                fixture.Goal, fixture.Task, root, contextDirectory, source,
                reviewerScopeChangedFiles: changed, reviewerScopeMergeBase: "baseline",
                reviewerScopeTotalChangedFileCount: changed.Length, reviewerMergeTreeClean: false,
                reviewerMergeTreeConflictPaths: conflicts, reviewerMergeTreeTotalConflictPathCount: conflicts.Length);
            var current = Text(typedPackage, "brief/current.md");
            Assert.Equal(WorkerContextRenderer.CreateCurrentBrief(source, true, true), current);
            Assert.Contains(userText, current, StringComparison.Ordinal);
            Assert.All(source.Segments, s => Assert.Equal(new[] { role }, s.RoleVisibility));
            Assert.All(typedPackage.Artifacts, a => Assert.Contains(role, a.RoleVisibility));
            var header = Text(typedPackage, "brief/header-residual.md");
            Assert.Equal(WorkerContextRenderer.CreateHeaderResidual(source), header);
            Assert.Contains("Decision context:", header, StringComparison.Ordinal);
            Assert.StartsWith("## Instructions", current, StringComparison.Ordinal);

            if (role == AgentRole.Reviewer)
            {
                Assert.Contains(CountOnly("Changed files", changed.Length), current, StringComparison.Ordinal);
                Assert.Contains(CountOnly("Conflicting paths", conflicts.Length), current, StringComparison.Ordinal);
                Assert.Contains("GOAL_DIFF_CHANGED_FILES (context only;", current, StringComparison.Ordinal);
                foreach (var path in changed.Concat(conflicts))
                {
                    Assert.DoesNotContain($"- {path}", current, StringComparison.Ordinal);
                    Assert.DoesNotContain($"- conflict: {path}", current, StringComparison.Ordinal);
                }
                AssertScopeArtifact(root, typedPackage, "reviewer/changed-files.v1.json",
                    "git-diff-name-only-main-three-dot-head", "baseline", changed);
                AssertScopeArtifact(root, typedPackage, "reviewer/merge-conflict-paths.v1.json",
                    "git-merge-tree-write-tree-name-only-main-head", null, conflicts);
            }
            else
            {
                Assert.DoesNotContain(typedPackage.Artifacts, a => a.Identity.Value.StartsWith("reviewer/", StringComparison.Ordinal));
                if (role == AgentRole.Tester)
                {
                    Assert.All(changed, p => Assert.Contains($"- {p}", current, StringComparison.Ordinal));
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ProducerHeadingMutationCannotChangeTypedPathSelection()
    {
        var fixture = CreateFixture(AgentRole.Reviewer);
        var paths = Enumerable.Range(0, 125).Select(i => $"src/path-{i}.cs").ToArray();
        var source = BuildSource(fixture, paths, paths, false);
        var scope = Assert.Single(source.Segments, s => s.ReviewerChangedFiles is not null);
        var mutatedLines = scope.Lines.ToArray();
        mutatedLines[0] = "## Renamed producer scope";
        mutatedLines[scope.ReviewerChangedFiles!.CountLineIndex!.Value] = "Changed entries: fixture wording; showing fixture wording.";
        mutatedLines[scope.MergeConflictPaths!.CountLineIndex!.Value] = "Conflicted entries: fixture wording; showing fixture wording.";
        var mutated = source with
        {
            Segments = source.Segments.Select(s => ReferenceEquals(s, scope) ? s with { Lines = mutatedLines } : s).ToArray()
        };

        var typed = WorkerContextRenderer.CreateCurrentBrief(mutated, true, true);
        Assert.Equal(WorkerContextRenderer.CreateCurrentBrief(source, true, true)
            .Replace("## Reviewer Changed-File Scope", "## Renamed producer scope", StringComparison.Ordinal), typed);
        Assert.Contains(CountOnly("Changed files", 125), typed, StringComparison.Ordinal);
        Assert.Contains(CountOnly("Conflicting paths", 125), typed, StringComparison.Ordinal);
        Assert.DoesNotContain("src/path-0.cs", typed, StringComparison.Ordinal);
    }

    private static string[] AdversarialPaths(string kind) => new[]
    {
        $"src/{kind}/Changed files:/a.cs", $"src/{kind}/Conflicting paths:/b.cs",
        $"src/{kind}/Staleness policy:/c.cs", $"- {kind}-dash.cs", $"## {kind}-heading.cs",
        $"src/{kind}/embedded\n## path heading\nChanged files: literal\n- literal.cs"
    }.Concat(Enumerable.Range(0, 125).Select(i => $"src/{kind}/{i:D3}.cs")).ToArray();

    private static Fixture CreateFixture(AgentRole role)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Select typed scope", role);
        var goal = kernel.CreateGoal("Preserve scope data", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", 1, ReviewReceipt, string.Empty, DateTimeOffset.UnixEpoch,
            MergedReviewFindings: [new ReviewFinding("scope", ReviewFindingState.Open,
                new ReviewFindingLocation("src/A.cs", "A.Run"), "Recheck A")], WorkerResultPresent: true));
        return new Fixture(kernel, goal, task);
    }

    private static TaskBriefSource BuildSource(Fixture fixture, string[] changed, string[] conflicts,
        bool clean, string? root = null, string? context = null) =>
        fixture.Kernel.BuildTaskBriefSource(fixture.Goal.Id, fixture.Task.Id,
            workingDirectory: root, contextDirectory: context,
            reviewerScopeChangedFiles: changed, reviewerScopeMergeBase: "baseline",
            reviewerScopeTotalChangedFileCount: changed.Length, reviewerMergeTreeClean: clean,
            reviewerMergeTreeConflictPaths: conflicts, reviewerMergeTreeTotalConflictPathCount: conflicts.Length);

    private static string CountOnly(string label, int count) =>
        $"{label}: {count}; complete path list is delivered only by its typed MandatoryFile artifact.";

    private static string Text(WorkerContextPackage package, string identity)
    {
        var artifact = Assert.Single(package.Artifacts, a => a.Identity.Value == identity);
        var bytes = artifact.AuthoritativeBytes!;
        Assert.Equal(WorkerContextArtifact.Hash(bytes), artifact.ContentHash);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void AssertScopeArtifact(string root, WorkerContextPackage package,
        string identity, string command, string? baseline, string[] paths)
    {
        var artifact = Assert.Single(package.Artifacts, a => a.Identity.Value == identity);
        var ordered = (baseline is null
                ? paths.Select(path => path.Trim().Replace('\\', '/').Normalize(NormalizationForm.FormC))
                : paths)
            .Order(StringComparer.Ordinal).ToArray();
        var expectedBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ContractVersion = 1, SourceCommand = command, Baseline = baseline,
            TotalPathCount = paths.Length, Paths = ordered
        });
        Assert.Equal(ContextDeliveryMode.MandatoryFile, artifact.DeliveryMode);
        Assert.Equal(new[] { AgentRole.Reviewer }, artifact.RoleVisibility);
        Assert.Equal(WorkerContextArtifact.Hash(expectedBytes), artifact.ContentHash);
        var materialized = File.ReadAllBytes(Path.Combine(root, artifact.MandatoryRelativePath!));
        Assert.Equal(expectedBytes, materialized);
        using var document = JsonDocument.Parse(materialized);
        Assert.Equal(ordered, document.RootElement.GetProperty("Paths").EnumerateArray().Select(p => p.GetString()));
    }

    private sealed record Fixture(AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task);

    private const string ReviewReceipt = """
        WORKER_RESULT:
        files: none
        commands: review
        tests: not-run - fixture
        commit: none
        blockers: exact-blocker - recheck scope
        findings: [{"stable_id":"scope","state":"open","severity":"blocking","location":{"file":"src/A.cs","region":"A.Run"},"description":"Recheck A"}]
        touched_anchors: []
        verdict: needs-work
        model_fit: OpenAI/gpt-6.1-sol - adequate - fixture - deterministic
        skills: none
        confidence: high
        END_WORKER_RESULT
        """;
}
