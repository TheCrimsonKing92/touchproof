using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;
using static DeveloperResumedDeferredNoChangeQualifierTests;

// Parallel-safe: new documentation fixtures use only in-memory state and an injected git boundary.
public sealed class DeveloperDeferredNoChangeDeclineCodeTests
{
    private const string Unusable = "DEFERRED_NO_CHANGE_EVIDENCE_UNUSABLE candidate_sha=" +
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb; requested_classes=OtherTests";

    [Xunit.Theory]
    [Xunit.InlineData("not-developer")]
    [Xunit.InlineData("not-retry-or-resume")]
    [Xunit.InlineData("dirty")]
    [Xunit.InlineData("commit-after-dispatch")]
    [Xunit.InlineData("candidate-not-head")]
    [Xunit.InlineData("no-rationale")]
    [Xunit.InlineData("no-worker-result")]
    [Xunit.InlineData("blockers")]
    [Xunit.InlineData("tests-not-deferred")]
    [Xunit.InlineData("no-classes")]
    [Xunit.InlineData("failing-test-unresolvable")]
    [Xunit.InlineData("no-finding-classes")]
    [Xunit.InlineData("failing-test-class-undeclared")]
    [Xunit.InlineData("finding-class-undeclared")]
    public void EachDeclineCheckHasOneFixedCode(string expected)
    {
        var cause = expected is "no-finding-classes" or "finding-class-undeclared"
            ? RetryCause.NewTestFinding : RetryCause.Unknown;
        var message = expected switch
        {
            "failing-test-unresolvable" => "failing_tests=DisplayNameOnly",
            "failing-test-class-undeclared" => "failing_tests=Suite.UndeclaredTests.Fails",
            _ => "Review candidate again."
        };
        var (kernel, goal, task, _) = Scenario(message, cause,
            role: expected == "not-developer" ? AgentRole.Tester : AgentRole.Developer,
            retry: expected != "not-retry-or-resume");
        if (expected == "finding-class-undeclared") RecordFinding(kernel, goal, "UndeclaredTests");
        var output = expected switch
        {
            "no-rationale" => Output.Replace(Rationale + "\n", string.Empty, StringComparison.Ordinal).Replace("files: none", "files: Alpha.cs", StringComparison.Ordinal),
            "no-worker-result" => Rationale,
            "blockers" => Output.Replace("blockers: none", "blockers: source work remains", StringComparison.Ordinal),
            "tests-not-deferred" => Output.Replace("deferred - ClassA", "pass - ClassA", StringComparison.Ordinal),
            "no-classes" => Output.Replace("deferred - ClassA", "deferred - conductor will verify", StringComparison.Ordinal),
            _ => Output
        };

        Xunit.Assert.False(QualifyWithCode(goal, task, output, out var code,
            clean: expected != "dirty", relevantCommit: expected == "commit-after-dispatch",
            head: expected == "candidate-not-head" ? new string('c', 40) : Candidate));
        Xunit.Assert.Equal(expected, code);
    }

    [Xunit.Theory]
    [Xunit.InlineData("quoted-marker")]
    [Xunit.InlineData("lowercase-marker")]
    [Xunit.InlineData("newer-retry")]
    public void OnlyNewestOrdinalPrefixCanRelaxMissingFindingClasses(string invalid)
    {
        var message = invalid switch
        {
            "quoted-marker" => "Retry after " + Unusable,
            "lowercase-marker" => Unusable.ToLowerInvariant(),
            _ => Unusable
        };
        var (kernel, goal, task, clock) = Scenario(message, RetryCause.NewTestFinding, dispatch: false);
        if (invalid == "newer-retry")
        {
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            kernel.RetryTask(goal.Id, task.Id, "New evidence request without a marker.", RetryCause.NewTestFinding);
        }
        Dispatch(kernel, goal, task, clock.UtcNow);

        Xunit.Assert.False(QualifyWithCode(goal, task, Output, out var code));
        Xunit.Assert.Equal("no-finding-classes", code);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void UnusableRetryAcceptsAnyDeclaredClassesAndStillAcceptsDeclaredFinding(bool hasFinding)
    {
        var (kernel, goal, task, _) = Scenario("  " + Unusable, RetryCause.NewTestFinding);
        if (hasFinding) RecordFinding(kernel, goal, "ClassA");

        Xunit.Assert.True(QualifyWithCode(goal, task, Output, out var code));
        Xunit.Assert.Null(code);
    }

    [Xunit.Fact]
    public void UnusableRetryStillRequiresEveryFailingTestClass()
    {
        var (_, goal, task, _) = Scenario(Unusable + "; failing_tests=Suite.UndeclaredTests.Fails",
            RetryCause.NewTestFinding);

        Xunit.Assert.False(QualifyWithCode(goal, task, Output, out var code));
        Xunit.Assert.Equal("failing-test-class-undeclared", code);
    }

    [Xunit.Fact]
    public void FirstFailingCheckWinsWhenSeveralChecksFail()
    {
        var (_, goal, task, _) = Scenario("Retry the candidate.", RetryCause.Unknown);

        Xunit.Assert.False(QualifyWithCode(goal, task, string.Empty, out var code,
            clean: false, relevantCommit: true, head: new string('c', 40)));
        Xunit.Assert.Equal("dirty", code);
    }

    [Xunit.Fact]
    public void DocumentationRouteAcceptsReproducedAlreadyImplementedCase()
    {
        var fixture = new DocumentationScenario();
        Xunit.Assert.True(fixture.Accept(out var marker, out var code));
        Xunit.Assert.Null(code);
        Xunit.Assert.Equal(fixture.ExpectedMarker, marker);
        Xunit.Assert.Equal(4, fixture.GitCalls);
        Xunit.Assert.Empty(DocumentationScopePaths.FromText("Reference docs/a.md without a Scope heading."));
    }

    [Xunit.Theory]
    [Xunit.InlineData("- `docs/a.md`")]
    [Xunit.InlineData("- docs/a.md")]
    [Xunit.InlineData("- `docs/`")]
    [Xunit.InlineData("- `.agents/skills/example/`")]
    [Xunit.InlineData("- `docs/*.md`")]
    [Xunit.InlineData("- `docs/a.md::Symbol`")]
    [Xunit.InlineData("- `docs/123/a.md`")]
    [Xunit.InlineData("Do not edit docs/a.md; edit docs/b.md")]
    [Xunit.InlineData("- docs/a.md\nOut of scope: docs/b.md\n- docs/c.md")]
    [Xunit.InlineData("- docs\\a.md")]
    [Xunit.InlineData("- `docs/`\n- `docs/a.md`")]
    [Xunit.InlineData("- `package/guide.md`\n- package/other.txt")]
    public void DocumentationScopePathsMatchGoalFileScopeInference(string declarations)
    {
        var description = "Reference docs/ignored.md\n## Scope\n" + declarations +
            "\n## Verification\nReference docs/also-ignored.md";
        var expected = GoalFileScopeInference.FromText(description).Select(scope => scope.Path)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Xunit.Assert.Equal(expected, DocumentationScopePaths.FromText(description));
    }

    [Xunit.Theory]
    [Xunit.InlineData("docs/a.md", "docs/a.md", true)]
    [Xunit.InlineData("docs/a.md", "docs/a.md/child.md", false)]
    [Xunit.InlineData("docs/LICENSE", "docs/LICENSE/child.md", false)]
    [Xunit.InlineData("docs/", "docs/a.md", true)]
    [Xunit.InlineData("docs/a.md/", "docs/a.md/child.md", true)]
    [Xunit.InlineData("docs/", "docs-other/a.md", false)]
    [Xunit.InlineData("docs/", "Docs/a.md", false)]
    public void ScopeCoverageRequiresExactFilesOrDirectoryDescendants(string entry, string path, bool expected)
    {
        Xunit.Assert.Equal(expected, DocumentationScopePaths.Covers(entry, path));
    }

    public static IEnumerable<object[]> DocumentationFailures()
    {
        yield return ["mismatch", "doc-head-mismatch"];
        yield return ["abbreviated", "doc-head-mismatch"];
        yield return ["unobserved-head", "doc-head-mismatch"];
        yield return ["initial", "doc-not-retry-or-resume"];
        yield return ["scope-missing", "doc-scope-unmet"];
        yield return ["deleted", "doc-scope-unmet"];
        yield return ["file-descendant", "doc-unrelated-paths"];
        yield return ["file-replaced-by-directory", "doc-unrelated-paths"];
        yield return ["empty", "doc-diff-empty"];
        yield return ["diff-failed", "doc-diff-unavailable"];
        yield return ["diff-exception", "doc-diff-unavailable"];
        yield return ["diff-drain", "doc-diff-unavailable"];
        yield return ["diff-malformed", "doc-diff-unavailable"];
        yield return ["raw-failed", "doc-diff-unavailable"];
        yield return ["source", "doc-non-documentation-paths"];
        yield return ["symlink", "doc-non-documentation-paths"];
        yield return ["old-symlink", "doc-non-documentation-paths"];
        yield return ["submodule", "doc-non-documentation-paths"];
        yield return ["mode-only", "doc-non-documentation-paths"];
        yield return ["unrelated", "doc-unrelated-paths"];
        yield return ["case-mismatch", "doc-unrelated-paths"];
        yield return ["dirty", "doc-dirty"];
        yield return ["commit", "doc-commit-after-dispatch"];
        yield return ["tester", "doc-not-developer"];
        yield return ["reviewer", "doc-not-developer"];
        yield return ["baseline-missing", "documentation-base-unavailable"];
        yield return ["baseline-unresolved", "documentation-base-unavailable"];
        yield return ["scope-unresolved", "doc-scope-unresolved"];
        yield return ["brief-scope", "doc-scope-unresolved"];
        yield return ["objective-scope", "doc-scope-unresolved"];
        yield return ["no-result", "doc-no-worker-result"];
        yield return ["files", "doc-result-not-no-change"];
        yield return ["blockers", "doc-blockers"];
        yield return ["not-deferred", "doc-tests-not-deferred"];
        yield return ["classes", "doc-tests-claimed"];
        yield return ["trailing-class", "doc-tests-claimed"];
        yield return ["go-tests", "doc-tests-claimed"];
        yield return ["test-count", "doc-tests-claimed"];
        yield return ["tests-ran", "doc-tests-claimed"];
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(DocumentationFailures))]
    public void DocumentationRouteDeclinesEachFailedCheckWithItsOwnCode(string change, string expected)
    {
        var fixture = new DocumentationScenario(change);
        Xunit.Assert.False(fixture.Accept(out var marker, out var code));
        Xunit.Assert.Empty(marker);
        Xunit.Assert.Equal(expected, code);
    }

    [Xunit.Fact]
    public void CodeCandidateWithoutClassesStillDeclinesNoClassesAndBareNarrativeIsNotAccepted()
    {
        var code = new DocumentationScenario("source");
        Xunit.Assert.False(code.Accept(out _, out var documentationCode));
        Xunit.Assert.Equal("doc-non-documentation-paths", documentationCode);
        Xunit.Assert.False(QualifyWithCode(code.Goal, code.Task, code.StandardOutput, out var qualifierCode));
        Xunit.Assert.Equal("no-classes", qualifierCode);
        var bare = new DocumentationScenario("no-result");
        Xunit.Assert.False(bare.Accept(out _, out _));
        Xunit.Assert.False(QualifyWithCode(bare.Goal, bare.Task, bare.StandardOutput, out _));
        var noDiff = new DocumentationScenario("empty");
        Xunit.Assert.False(noDiff.Accept(out _, out var emptyCode));
        Xunit.Assert.Equal("doc-diff-empty", emptyCode);
        Xunit.Assert.False(QualifyWithCode(noDiff.Goal, noDiff.Task, noDiff.StandardOutput, out _));
    }

    [Xunit.Fact]
    public void RunnerAcceptsReproducedDocumentationCaseWithMarker()
    {
        var fixture = new DocumentationScenario();
        // AllowsNoChangeCompletion is reached by the successful native-process path.
        var native = new TaskVerificationRecord("codex exec", DocumentationScenario.Root, 0,
            fixture.StandardOutput, string.Empty, DateTimeOffset.Parse("2026-11-15T12:00:00Z"));
        Xunit.Assert.Equal(0, native.ExitCode);
        Xunit.Assert.True(fixture.RunnerAccept(native.StandardOutput, out var diagnostic));
        Xunit.Assert.Equal(fixture.ExpectedMarker, diagnostic);
        Xunit.Assert.Equal(4, fixture.GitCalls);
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(RunnerDocumentationFailures))]
    public void RunnerDeclinesWithMatchingCodeWhenOneCheckFails(string change, string expected)
    {
        var fixture = new DocumentationScenario(change);
        Xunit.Assert.False(fixture.RunnerAccept(fixture.StandardOutput, out var diagnostic));
        Xunit.Assert.Contains("DOCUMENTATION_NO_CHANGE_DECLINED reason=" + expected, diagnostic);
        Xunit.Assert.DoesNotContain("DOCUMENTATION_NO_CHANGE_ACCEPTED", diagnostic);
    }

    // Other roles retain their independently authorized classifier route.
    public static IEnumerable<object[]> RunnerDocumentationFailures() => DocumentationFailures()
        .Where(row => (string)row[0] is not ("tester" or "reviewer"));

    [Xunit.Theory]
    [Xunit.InlineData("docs/data.txt")]
    [Xunit.InlineData(".agents/skills/example/script.py")]
    [Xunit.InlineData("nested/README.MD")]
    public void DocumentationBoundaryAcceptsRegularFilesInDeclaredDirectories(string path)
    {
        var fixture = new DocumentationScenario(path: path);
        Xunit.Assert.True(fixture.Accept(out _, out _));
    }

    [Xunit.Theory]
    [Xunit.InlineData("without-rationale")]
    [Xunit.InlineData("directory")]
    [Xunit.InlineData("added")]
    [Xunit.InlineData("rename")]
    [Xunit.InlineData("no-test-execution")]
    public void DocumentationEvidenceAcceptsSupportedScopeAndChangeShapes(string change)
    {
        var fixture = new DocumentationScenario(change, "docs/new.md");
        Xunit.Assert.True(fixture.Accept(out var marker, out var code));
        Xunit.Assert.Null(code);
        Xunit.Assert.Equal(fixture.ExpectedMarker, marker);
        Xunit.Assert.Equal(4, fixture.GitCalls);
    }

    [Xunit.Theory]
    [Xunit.InlineData("nested/docs/a.txt")]
    [Xunit.InlineData(".claude/skills/example/a.txt")]
    [Xunit.InlineData("docs-other/a.txt")]
    [Xunit.InlineData("nested/a.markdown")]
    [Xunit.InlineData("nested/a.mdx")]
    public void DocumentationBoundaryDeclinesOtherRootsAndExtensions(string path)
    {
        var fixture = new DocumentationScenario(path: path);
        Xunit.Assert.False(fixture.Accept(out _, out var code));
        Xunit.Assert.Equal("doc-non-documentation-paths", code);
    }

    private sealed class DocumentationScenario
    {
        internal const string Root = "documentation-fixture-without-go-module";
        private static readonly string Baseline = new('a', 40);
        private readonly string _change;
        private readonly string _path;
        private readonly bool _clean;
        private readonly string _head;
        internal Goal Goal { get; }
        internal TaskSpec Task { get; }
        internal string StandardOutput { get; }
        internal int GitCalls { get; private set; }
        internal string ExpectedMarker => $"DOCUMENTATION_NO_CHANGE_ACCEPTED candidate_sha={Candidate} accepted_base={Baseline} changed_paths=" + Uri.EscapeDataString(_path) +
            (_change == "rename" ? "," + Uri.EscapeDataString("docs/old.md") : "");

        internal DocumentationScenario(string change = "", string path = ".agents/skills/example/SKILL.md")
        {
            _change = change;
            _path = change == "source" ? "src/Feature.cs" :
                change is "file-descendant" or "file-replaced-by-directory" ? "docs/a.md" : path;
            _clean = change != "dirty";
            _head = change == "mismatch" ? new string('c', 40) : Candidate;
            var clock = new TestClock { UtcNow = DateTimeOffset.Parse("2026-11-15T11:00:00Z") };
            var kernel = new AgentOrchestratorKernel(clock);
            var scope = change == "scope-unresolved" ? "No declared paths." :
                "## Scope\n- `" + (change is "directory" or "rename" ? "docs/" : _path) + "`" + (change == "scope-missing" ? "\n- `docs/missing.md`" : "");
            var role = change == "tester" ? AgentRole.Tester : change == "reviewer" ? AgentRole.Reviewer : AgentRole.Developer;
            Task = new TaskSpec(TaskId.New(), change is "brief-scope" or "objective-scope" ? "Implement the scoped slice." : scope, role);
            Goal = kernel.CreateGoal(change == "objective-scope" ? scope : "Documentation fixture", [Task]);
            kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            if (change != "initial") kernel.RetryTask(Goal.Id, Task.Id, "Recheck the candidate.", RetryCause.NewTestFinding);
            kernel.RecordTaskDispatch(Goal.Id, Task.Id, new TaskDispatchRecord("codex-cli", "codex exec", Root,
                clock.UtcNow, BaseCommit: change == "abbreviated" ? Candidate[..8] : Candidate,
                BriefSnapshot: change == "brief-scope" ? scope : null,
                WorkerBuildScope: change == "baseline-missing" ? null : new WorkerBuildScopeIdentity(Baseline, "digest", "metadata")));
            StandardOutput = Output.Replace("deferred - ClassA", "deferred", StringComparison.Ordinal);
            StandardOutput = change switch
            {
                "no-result" => Rationale,
                "files" => StandardOutput.Replace("files: none", "files: docs/a.md", StringComparison.Ordinal),
                "blockers" => StandardOutput.Replace("blockers: none", "blockers: implementation pending", StringComparison.Ordinal),
                "not-deferred" => StandardOutput.Replace("tests: deferred", "tests: not-run", StringComparison.Ordinal),
                "classes" => StandardOutput.Replace("tests: deferred", "tests: deferred - ClassA", StringComparison.Ordinal),
                "trailing-class" => StandardOutput.Replace("tests: deferred", "tests: deferred; ClassA", StringComparison.Ordinal),
                "go-tests" => StandardOutput.Replace("tests: deferred", "tests: deferred - pkg.TestExample", StringComparison.Ordinal),
                "test-count" => "Passed: 3\n" + StandardOutput,
                "tests-ran" => "I ran tests.\n" + StandardOutput,
                "without-rationale" => StandardOutput.Replace(Rationale + "\n", "", StringComparison.Ordinal),
                "no-test-execution" => "No tests were executed.\n" + StandardOutput,
                _ => StandardOutput
            };
        }

        internal bool Accept(out string marker, out string? code) => DocumentationNoChangeCompletion.TryAccept(
            Goal, Task, Root, _head, _clean, _change == "commit", StandardOutput, Git, out marker, out code);

        internal bool RunnerAccept(string output, out string? diagnostic)
        {
            var runner = new BackgroundDispatchRunner { DocumentationNoChangeGit = Git };
            var evidence = new GoalWorktreeDispatchEvidence("goal/fixture", _head, "clean", _clean,
                "clean", "", _change == "commit" ? 1 : 0, _change == "commit" ? ["docs/new.md"] : [], []);
            diagnostic = null;
            return runner.AllowsNoChangeCompletion(Goal, Task, evidence, output, output, "", Root, ref diagnostic);
        }

        private GitCli.GitResult Git(string root, string[] arguments)
        {
            GitCalls++;
            Xunit.Assert.Equal(Root, root);
            if (arguments[0] == "rev-parse")
            {
                Xunit.Assert.Equal(["rev-parse", "--verify", "--quiet", arguments[3]], arguments);
                return arguments[3] switch
                {
                    "HEAD^{commit}" => new(0, _change == "unobserved-head" ? new string('c', 40) : _head, ""),
                    _ when arguments[3] == Baseline + "^{commit}" => new(_change == "baseline-unresolved" ? 1 : 0, Baseline, ""),
                    _ => throw new InvalidOperationException("Unexpected revision")
                };
            }
            var raw = arguments[1] == "--raw";
            Xunit.Assert.Equal(raw
                ? ["diff", "--raw", "-z", "--no-renames", "--no-abbrev", Baseline, _head, "--"]
                : new[] { "diff", "--name-status", "-z", "--no-renames", Baseline, _head, "--" }, arguments);
            if (_change == "diff-exception") throw new IOException("diff unavailable");
            if (_change == "diff-drain") return new(0, "", "", DrainTimedOut: true);
            if (_change == "diff-failed" || (_change == "raw-failed" && raw)) return new(1, "", "diff failed");
            if (_change == "empty") return new(0, "", "");
            if (_change == "diff-malformed") return new(0, "M\0unterminated", "");
            var status = _change == "deleted" ? "D" :
                _change is "added" or "rename" or "file-descendant" or "file-replaced-by-directory" ? "A" : "M";
            var path = _change == "unrelated" ? "docs/unrelated.md" :
                _change is "file-descendant" or "file-replaced-by-directory" ? _path + "/child.md" :
                _change == "case-mismatch" ? _path.ToUpperInvariant() : _path;
            var oldMode = _change == "old-symlink" ? "120000" : status == "A" ? "000000" : "100644";
            var newMode = _change switch { "symlink" => "120000", "submodule" => "160000", "deleted" => "000000", "mode-only" => "100755", _ => "100644" };
            var newBlob = _change == "mode-only" ? Baseline : _change == "deleted" ? new string('0', 40) : Candidate;
            var oldBlob = status == "A" ? new string('0', 40) : Baseline;
            var diff = raw ? $":{oldMode} {newMode} {oldBlob} {newBlob} {status}\0{path}\0" : $"{status}\0{path}\0";
            if (_change == "rename") diff += raw
                ? $":100644 000000 {Baseline} {new string('0', 40)} D\0docs/old.md\0" : "D\0docs/old.md\0";
            if (_change == "file-replaced-by-directory") diff += raw
                ? $":100644 000000 {Baseline} {new string('0', 40)} D\0{_path}\0" : $"D\0{_path}\0";
            return new(0, diff, "");
        }
    }

    internal static void RecordFinding(AgentOrchestratorKernel kernel, Goal goal, string testClass)
    {
        var reviewer = kernel.AddTask(goal.Id, AgentRole.Reviewer, "Record a source finding.");
        var finding = new ReviewFinding("source-finding", ReviewFindingState.Open,
            new ReviewFindingLocation("seed.txt", "candidate"), "The selected source evidence is required.",
            FindingSeverity.Blocking, FindingCategory.Correctness,
            new FindingEvidenceRequest([new FindingEvidenceSelection("Infrastructure.Tests", testClass)]));
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review fixture", "C:\\repo", 1, string.Empty, "Source finding remains.",
            DateTimeOffset.Parse("2026-10-02T09:01:00Z"), MergedReviewFindings: [finding]));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, TestClock Clock) Scenario(
        string message, RetryCause cause, AgentRole role = AgentRole.Developer, bool retry = true, bool dispatch = true)
    {
        var clock = new TestClock { UtcNow = DateTimeOffset.Parse("2026-10-02T09:00:00Z") };
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Review the candidate.", role);
        var goal = kernel.CreateGoal("Deferred no-change decline codes", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        if (retry) kernel.RetryTask(goal.Id, task.Id, message, cause);
        if (dispatch) Dispatch(kernel, goal, task, clock.UtcNow);
        return (kernel, goal, task, clock);
    }

    private static void Dispatch(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, DateTimeOffset time) =>
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", time, BaseCommit: Candidate));

    private static bool QualifyWithCode(Goal goal, TaskSpec task, string output, out string? code,
        bool clean = true, bool relevantCommit = false, string? head = null)
    {
        var catalog = WorkerProviderCatalog.Default();
        var classifier = new WorkerDispatchCompletionClassifier(
            dispatch => catalog.ResolveProfile(dispatch.WorkerName), new TestClock(),
            _ => false, _ => throw new FileNotFoundException());
        // Keep the added facts compilable against the original qualifier for Acceptance's negative control.
        var method = typeof(DeveloperDeferredNoChangeQualifier).GetMethods(
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .SingleOrDefault(method => method.Name == "TryQualify" && method.GetParameters().Length == 10);
        Xunit.Assert.NotNull(method);
        object?[] arguments = [goal, task, head ?? Candidate, clean, relevantCommit, output,
            string.Empty, classifier, null, null];
        var qualified = (bool)method.Invoke(null, arguments)!;
        code = (string?)arguments[9];
        return qualified;
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}
