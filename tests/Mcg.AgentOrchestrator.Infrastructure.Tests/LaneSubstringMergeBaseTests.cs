using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its temporary repository and git processes; no shared state.
public sealed class LaneSubstringMergeBaseTests
{
    private const string MainAddedClass = "WorkerBuildRequestMainAddedTests";
    private const string GoalAddedClass = "WorkerBuildRequestGoalAddedTests";
    private const string TestRoot = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";
    private const string Manifest = """
        { "version": 1, "engine": {
          "laneTestSourceRoots": ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests", "tests/Mcg.AgentOrchestrator.TestSupport"],
          "infrastructureTestLanes": [
          { "name": "Dotnet build slots", "filter": "FullyQualifiedName~ConductorBatchLoopTests", "ownedCollections": ["DotnetBuildSlots"] },
          { "name": "Isolated process spawning B", "filter": "(FullyQualifiedName~PatchEquivalenceIntegrationMergeTests)&Category!=HostIntegration" },
          { "name": "Remainder balance C", "filter": "FullyQualifiedName~WorkerBuildRequest|FullyQualifiedName~ExitReconcileBuild" },
          { "name": "Remainder", "filter": "FullyQualifiedName!~ConductorBatchLoopTests&FullyQualifiedName!~PatchEquivalenceIntegrationMergeTests&FullyQualifiedName!~WorkerBuildRequest&FullyQualifiedName!~ExitReconcileBuild&Category!=HostIntegration" }
        ] } }
        """;

    [Fact]
    public void WorkerBuildCheck_MainAddedClassMergedIntoGoal_ReportsNoCollision()
    {
        using var fixture = new Fixture();

        // This same candidate collides if the saved goal baseline is used instead.
        var staleBase = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root, null, fixture.GoalBaseline);
        Assert.True(staleBase.EvaluationComplete);
        Assert.True(staleBase.HasViolation);
        Assert.Contains("[lane-substring-collision]", staleBase.Message);
        Assert.Contains(MainAddedClass, staleBase.Message);

        var check = fixture.RunWorkerCheck();

        Assert.Equal(WorkerBuildStructuralStatus.Passed, check.Status);
        Assert.DoesNotContain("lane-substring-collision", check.Diagnostic);
    }

    [Fact]
    public void WorkerBuildCheck_GoalAddedSubstringClass_StillReportsCollision()
    {
        using var fixture = new Fixture(addGoalClass: true);

        var check = fixture.RunWorkerCheck();

        Assert.Equal(WorkerBuildStructuralStatus.Violation, check.Status);
        Assert.Contains("[lane-substring-collision]", check.Diagnostic);
        Assert.Contains(TestRoot + GoalAddedClass + ".cs", check.Diagnostic);
        Assert.Contains(GoalAddedClass, check.Diagnostic);
        Assert.Contains("'FullyQualifiedName~WorkerBuildRequest'", check.Diagnostic);
        Assert.Contains("Remainder balance C", check.Diagnostic);
        Assert.DoesNotContain(MainAddedClass, check.Diagnostic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerBuildCheckAndCompletionCheck_SameGoalHead_ReachSameVerdict(bool addGoalClass)
    {
        using var fixture = new Fixture(addGoalClass);

        var worker = fixture.RunWorkerCheck();
        var conductor = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root, null, "main");

        Assert.True(conductor.EvaluationComplete);
        Assert.True(conductor.IsApplicable);
        Assert.Equal(addGoalClass, conductor.HasViolation);
        Assert.Equal(addGoalClass ? WorkerBuildStructuralStatus.Violation : WorkerBuildStructuralStatus.Passed,
            worker.Status);
        Assert.Equal(conductor.HasViolation, worker.Status == WorkerBuildStructuralStatus.Violation);
        Assert.Equal(conductor.Message, worker.Diagnostic);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        internal string GoalBaseline { get; }
        private readonly WorkerBuildStructuralPreflight _preflight;
        private readonly WorkerBuildInvocation _invocation;

        internal Fixture(bool addGoalClass = false)
        {
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            Directory.CreateDirectory(Path.Combine(Root, TestRoot));
            File.WriteAllText(Path.Combine(Root, "config", "acceptance-manifest.json"), Manifest);
            File.Copy(Path.Combine(VerifiedRepositoryRoot.Find(), TestCollectionGuardRules.ExemptionsRelativePath),
                Path.Combine(Root, TestCollectionGuardRules.ExemptionsRelativePath));
            WriteTestClass("QuokkaHarborProbeTests");
            Git("init", "-b", "main");
            Git("config", "user.email", "lane-merge-base@fixture.invalid");
            Git("config", "user.name", "Lane merge base fixture");
            Commit("seed baseline");
            GoalBaseline = Git("rev-parse", "HEAD").Trim();
            Git("switch", "-c", "goal");

            var at = DateTimeOffset.Parse("2026-11-06T00:00:00Z");
            var dispatch = new TaskDispatchRecord("fixture-worker", "fixture-command", Root, at,
                BaseCommit: GoalBaseline, WorkerBuildScope: new(GoalBaseline, "fixture-scope"));
            var policy = WorkerBuildStructuralPreflight.CapturePolicy(dispatch, null, "main");
            Assert.Null(policy.UnavailableDiagnostic);
            Assert.True(policy.RequiresManifest);
            Assert.Equal(GoalBaseline, policy.BaselineRevision);
            var reference = new WorkerBuildHostReference(Path.Combine(Root, "state.db"), Root,
                "fixture-goal", "fixture-task", at, "fixture-dispatch", StructuralPolicy: policy);
            _preflight = new WorkerBuildStructuralPreflight(reference);
            var process = new WorkerBuildProcess(1, at, "fixture-host");
            _invocation = new WorkerBuildInvocation("fixture-invocation", "fixture-request", reference.GoalId,
                reference.TaskId, at, reference.DispatchId, Root, "fixture-tree", "fixture-scope",
                ["fixture.csproj"], "Debug", at.AddMinutes(5), process, process, "fixture-admission", 1, 1,
                [new(WorkerBuildPhase.Requested, at)], StructuralPolicy: policy);

            // Diverge before main advances so this exercises a merge, not just a fast-forward.
            File.WriteAllText(Path.Combine(Root, "goal.txt"), "goal-owned change");
            Commit("goal diverges");
            Git("switch", "main");
            WriteTestClass(MainAddedClass);
            Commit("main adds substring-selected class");
            Git("switch", "goal");
            Git("merge", "--no-ff", "--no-edit", "main");
            Assert.NotEqual(GoalBaseline, Git("merge-base", "main", "HEAD").Trim());
            Assert.Equal(Git("rev-parse", "main").Trim(), Git("merge-base", "main", "HEAD").Trim());
            if (addGoalClass)
            {
                WriteTestClass(GoalAddedClass);
                Commit("goal adds substring-selected class");
            }
        }

        internal WorkerBuildStructuralCheck RunWorkerCheck() =>
            _preflight.Run(_invocation, _preflight.PolicyDigest(Root), () => { }, CancellationToken.None);

        private void WriteTestClass(string name) =>
            File.WriteAllText(Path.Combine(Root, TestRoot, name + ".cs"),
                "public sealed " + "class " + name + " { [Xunit.Fact] public void Example() { } }");

        private void Commit(string message)
        {
            Git("add", ".");
            Git("commit", "-m", message);
        }

        private string Git(params string[] arguments)
        {
            var result = GitCli.RunForRepository(Root, arguments);
            Assert.True(result.ProcessStarted && result.Succeeded && !result.DrainTimedOut,
                $"git {string.Join(' ', arguments)}: {result.Output} {result.Error}");
            return result.Output;
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
