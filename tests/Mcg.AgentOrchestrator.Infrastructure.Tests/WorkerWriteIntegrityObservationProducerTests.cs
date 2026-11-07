using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Every case owns its directory tree and fake labeler; no shared process or native label state.
public sealed class WorkerWriteIntegrityObservationProducerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueryResultControlsClassificationDespiteBlockerClaim(bool medium)
    {
        using var fixture = new Fixture();
        var labeler = new FakeLabeler(new(true, Low: !medium, Inheritable: false, Medium: medium));
        var output = Output("Cannot write `src/Feature.cs`; integrity mismatch claims Medium");
        Assert.True(WorkerWriteIntegrityObservationProducer.TryObserve(labeler, fixture.Worktree, output, out var observation));
        Assert.NotNull(observation);
        Assert.Equal(Path.Combine("src", "Feature.cs"), observation.WorktreeRelativePath);
        Assert.Equal(medium ? WorkerWriteIntegrityLabel.Medium : WorkerWriteIntegrityLabel.Low, observation.ObservedLabel);
        Assert.True(observation.LabelQuerySucceeded);
        Assert.Equal(Path.Combine(fixture.Worktree, "src", "Feature.cs"), Assert.Single(labeler.Queries));
        var verification = new TaskVerificationRecord("worker", fixture.Worktree, 0, output, "",
            DateTimeOffset.Parse("2026-11-07T12:00:00Z"), WorkerResultPresent: true, AssignedScopeComplete: false)
        { WriteIntegrityObservation = observation };
        var outcome = DispatchFailureClassifier.Classify(new(TaskId.New(), "Implement", AgentRole.Developer), verification);
        Assert.Equal(medium ? "worker-write-integrity-blocked" : "incomplete-scope-declaration",
            TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(medium ? TaskOutcomeClass.Environmental : TaskOutcomeClass.RealFailure, outcome.OutcomeClass);
    }

    [Theory]
    [InlineData("CANNOT-WRITE src/Feature.cs")]
    [InlineData("access   denied 'src/Feature.cs'")]
    [InlineData("integrity-mismatch prevents editing \"src/Feature.cs\"")]
    [InlineData("cannot write src/../src/Feature.cs")]
    [InlineData("src/Feature.cs: access-denied")]
    public void EquivalentWordingSelectsOneTarget(string blocker)
    {
        using var fixture = new Fixture();
        var labeler = MediumLabeler();
        Assert.True(WorkerWriteIntegrityObservationProducer.TryObserve(labeler, fixture.Worktree, Output(blocker), out _));
        Assert.Single(labeler.Queries);
    }

    [Fact]
    public void QuotedAbsolutePathWithSpacesIsWorktreeRelative()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Worktree, "source folder", "Feature.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "source");
        var labeler = MediumLabeler();
        Assert.True(WorkerWriteIntegrityObservationProducer.TryObserve(labeler, fixture.Worktree,
            Output($"Cannot write '{path}'"), out var observation));
        Assert.Equal(Path.Combine("source folder", "Feature.cs"), observation!.WorktreeRelativePath);
        Assert.Equal(path, Assert.Single(labeler.Queries));
    }

    [Theory]
    [InlineData("../outside/Feature.cs")]
    [InlineData("sibling")]
    [InlineData("absolute")]
    public void OutsideTargetsAreRejectedBeforeQuery(string kind)
    {
        using var fixture = new Fixture();
        var path = kind switch
        {
            "sibling" => fixture.Worktree + "-sibling" + Path.DirectorySeparatorChar + "Feature.cs",
            "absolute" => Path.Combine(fixture.Root, "outside", "Feature.cs"),
            _ => kind
        };
        var labeler = MediumLabeler();
        Assert.False(WorkerWriteIntegrityObservationProducer.TryObserve(labeler, fixture.Worktree,
            Output($"Cannot write '{path}'"), out var observation));
        Assert.Null(observation);
        Assert.Empty(labeler.Queries);
    }

    [Theory]
    [InlineData("Cannot write src/Feature.cs or src/Other.cs", true)]
    [InlineData("Cannot write the target", true)]
    [InlineData("Cannot write the target. Details in logs/trace.txt", true)]
    [InlineData("Reading src/Feature.cs failed", true)]
    [InlineData("Cannot write src/Feature.cs", false)]
    public void MissingAmbiguousOrNonExactBlockerDoesNotQuery(string blocker, bool exact)
    {
        using var fixture = new Fixture();
        var labeler = MediumLabeler();
        Assert.False(WorkerWriteIntegrityObservationProducer.TryObserve(labeler, fixture.Worktree,
            Output(blocker, exact), out var observation));
        Assert.Null(observation);
        Assert.Empty(labeler.Queries);
    }

    [Theory]
    [InlineData("native-error")]
    [InlineData("missing")]
    [InlineData("throws")]
    [InlineData("contradictory")]
    [InlineData("unlabeled-medium")]
    [InlineData("unlabeled")]
    [InlineData("other")]
    public void UnusableNativeStateNeverEstablishesAboveLow(string kind)
    {
        using var fixture = new Fixture();
        var state = kind switch
        {
            "native-error" => new IntegrityLabelState(true, false, false, Medium: true, NativeQueryError: 5),
            "missing" => new IntegrityLabelState(false, false, false, Medium: true),
            "contradictory" => new IntegrityLabelState(true, true, false, Medium: true),
            "unlabeled-medium" => new IntegrityLabelState(true, false, false, Medium: true, Unlabeled: true),
            "unlabeled" => new IntegrityLabelState(true, false, false, Unlabeled: true),
            _ => new IntegrityLabelState(true, false, false)
        };
        var labeler = new FakeLabeler(state) { ThrowOnQuery = kind == "throws" };
        Assert.True(WorkerWriteIntegrityObservationProducer.TryObserve(labeler, fixture.Worktree,
            Output("Cannot write src/Feature.cs"), out var observation));
        Assert.NotNull(observation);
        Assert.False(observation.EstablishesAboveLow);
        if (kind is "native-error" or "missing" or "throws")
            Assert.False(observation.LabelQuerySucceeded);
        Assert.Single(labeler.Queries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinksOutsideWorktreeAreRejected(bool fileLink)
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Feature.cs"), "external");
        var link = Path.Combine(fixture.Worktree, fileLink ? "Feature.cs" : "linked");
        try
        {
            if (fileLink) File.CreateSymbolicLink(link, Path.Combine(outside, "Feature.cs"));
            else Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Symbolic links unavailable: {ex.Message}");
        }
        var labeler = MediumLabeler();
        Assert.False(WorkerWriteIntegrityObservationProducer.TryObserve(labeler, fixture.Worktree,
            Output($"Cannot write '{(fileLink ? link : Path.Combine(link, "Feature.cs"))}'"), out var observation));
        Assert.Null(observation);
        Assert.Empty(labeler.Queries);
    }

    [Theory]
    [InlineData(AgentRole.Developer, true)]
    [InlineData(AgentRole.Reviewer, false)]
    public void CompletionCarriesObservationOnlyForDeveloper(AgentRole role, bool expected)
    {
        using var fixture = new Fixture();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Observe denied write", [new TaskSpec(TaskId.New(), "Work", role)]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(new AgentId("worker"), "Worker", role,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey))]);
        var task = goal.Tasks.Single();
        var started = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "worker", fixture.Worktree, started));
        var stdout = Path.Combine(fixture.Root, "worker.out.log");
        var stderr = Path.Combine(fixture.Root, "worker.err.log");
        var exit = Path.Combine(fixture.Root, "worker.exit.txt");
        File.WriteAllText(stdout, Output("Cannot write src/Feature.cs"));
        File.WriteAllText(stderr, "");
        File.WriteAllText(exit, "1");
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(4242, "worker", fixture.Worktree,
            stdout, stderr, exit, started, null, null, OwnedProcessIds: [4242]));
        var labeler = MediumLabeler();
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false, findBuildDaemons: _ => [])
        { WriteIntegrityLabeler = labeler };
        var result = runner.ReconcileLatestProcess(kernel, goal.Id, task.Id);
        Assert.NotNull(result.Verification);
        Assert.Equal(expected, result.Verification.WriteIntegrityObservation is not null);
        Assert.Equal(expected ? 1 : 0, labeler.Queries.Count);
        if (expected) Assert.Equal(WorkerWriteIntegrityLabel.Medium, result.Verification.WriteIntegrityObservation!.ObservedLabel);
    }

    private static FakeLabeler MediumLabeler() => new(new(true, false, false, Medium: true));

    private static string Output(string blocker, bool exact = true) => $$"""
        WORKER_RESULT:
        files: none
        commands: none
        tests: deferred - conductor
        commit: none
        blockers: {{(exact ? "exact-blocker - " : "")}}{{blocker}}
        assigned_scope_complete: false
        model_fit: fixture/model - adequate - producer
        skills: none
        confidence: high
        END_WORKER_RESULT
        """;

    private sealed class FakeLabeler(IntegrityLabelState state) : IWorkerIntegrityLabeler
    {
        public List<string> Queries { get; } = [];
        public bool ThrowOnQuery { get; init; }
        public IntegrityLabelState Query(string path)
        {
            Queries.Add(path);
            if (ThrowOnQuery) throw new UnauthorizedAccessException("Query denied");
            return state;
        }
        public bool SetIntegrity(string path, string level, bool recursive) =>
            throw new InvalidOperationException("Observation must never change a label.");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-write-observation-{Guid.NewGuid():N}");
        public string Worktree => Path.Combine(Root, "worktree");
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Worktree, "src"));
            File.WriteAllText(Path.Combine(Worktree, "src", "Feature.cs"), "source");
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
