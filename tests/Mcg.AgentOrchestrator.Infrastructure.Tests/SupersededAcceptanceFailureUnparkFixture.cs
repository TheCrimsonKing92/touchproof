using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: the backing fixture owns its git repository, SQLite store and artifacts.
internal sealed class SupersededAcceptanceFailureUnparkFixture : IDisposable
{
    private readonly SoloAttributedFailureRouteFixture _storage = new();
    internal AgentOrchestratorKernel Kernel => _storage.Kernel;
    internal Goal Goal => _storage.Goal;
    internal SoloAcceptanceConsumptionContext Current { get; set; }
    internal AcceptanceVerificationSummary Original { get; private set; } = null!;
    internal List<TaskId> Starts { get; } = [];
    internal List<string> Holds { get; } = [];
    internal List<GateReadyCandidateRevisionPair> Gates { get; } = [];
    private AcceptanceVerificationSummary? _freshFailure;
    private bool _landed;
    private int _tick;
    internal static readonly ConductorAutonomyPolicy Policy = ConductorAutonomyPolicy.Permissive with
        { AutomaticRetryOperatorRoutingHoldSeconds = 0 };

    internal SupersededAcceptanceFailureUnparkFixture() => Current = _storage.Context;

    internal async Task RecordRed()
    {
        var reviewer = Kernel.AddTask(Goal.Id, AgentRole.Reviewer, "Review source", DefaultAgents());
        PassVerification(Kernel, Goal, reviewer);
        Assert.Equal(GoalStatus.Verified, Goal.Status);
        Assert.True(reviewer.LastVerification!.Succeeded);
        await _storage.Produce();
        Original = _storage.Summary;
        var driver = Driver();
        driver.BeginTick(Kernel, ++_tick);
        driver.CompleteParallelLandingAcceptance(ConductorParallelAcceptanceCandidate.Create(
            Goal, 0, [], Current.CandidateSha, Current.MainSha), Policy, Original, out var leaseHeld);
        Assert.False(leaseHeld);
        Assert.Equal(GoalStatus.AcceptanceFailed, Goal.Status);
        Assert.Equal(Original.AttemptId, Goal.LatestAcceptanceFailure!.AttemptId);
        Assert.Empty(Starts);
    }

    internal async Task ParkAdvanceUnpark(bool advance, bool recoverVerified = true)
    {
        Kernel.ParkGoal(Goal.Id, "Supported park before pair advance");
        Assert.Equal(GoalStatus.Parked, Goal.Status);
        await _storage.Restart();
        if (advance)
        {
            Git("checkout", "main");
            File.WriteAllText(Path.Combine(_storage.Repo, "main-advance.txt"), "new main");
            Git("add", "main-advance.txt");
            Git("commit", "-m", "Advance main while parked");
            var main = Git("rev-parse", "HEAD");
            Git("checkout", "candidate");
            File.WriteAllText(Path.Combine(_storage.Repo, "branch-advance.txt"), "new branch");
            Git("add", "branch-advance.txt");
            Git("commit", "-m", "Advance branch while parked");
            Current = new(Git("rev-parse", "HEAD"), main, Git("rev-parse", "HEAD^{tree}"),
                Git("merge-base", "HEAD", "main"));
            Assert.NotEqual(Original.BranchHeadSha, Current.CandidateSha);
            Assert.NotEqual(Original.MainHeadSha, Current.MainSha);
        }
        Kernel.UnparkGoal(Goal.Id, "Supported unpark");
        Assert.Equal(GoalStatus.AcceptanceFailed, Goal.Status);
        if (!recoverVerified) return;
        // This public recovery only restores source-review readiness, preserving the RED.
        var reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        Kernel.RecordTaskVerification(Goal.Id, reviewer.Id, new("review advanced source", _storage.Repo,
            0, "Source review passed for the current candidate", "", DateTimeOffset.UtcNow)
            { ReviewedCommit = Current.CandidateSha });
        Assert.True(reviewer.LastVerification!.Succeeded);
        Assert.Equal(Current.CandidateSha, reviewer.LastVerification.ReviewedCommit);
        Assert.True(Kernel.ReconcileGoalVerificationStatus(Goal.Id, "Recover passing source review"));
        Assert.Equal(GoalStatus.Verified, Goal.Status);
        Assert.Equal(Original.AttemptId, Goal.LatestAcceptanceFailure!.AttemptId);
    }

    internal ConductorDriver Driver()
    {
        var driver = MakeDriver(
            getFacts: _ => new(WorkspaceExists: true, IsMerged: _landed),
            getPreReviewEvidenceContext: _ => NoPreReviewContext(Current.CandidateSha),
            recordCriterionRetryFeedback: Kernel.RecordCriterionRetryFeedback,
            retryTaskWithCause: (id, task, message, round, cause) =>
                Kernel.RetryTaskAutomatically(id, task, message, cause, retryRoundKind: round),
            dispatchAndStart: goal =>
            {
                var task = Assert.Single(goal.Tasks, item => item.RequiredRole == AgentRole.Developer &&
                    item.Status == WorkTaskStatus.Assigned);
                Assert.Equal(AgentRole.Developer, task.RequiredRole);
                Starts.Add(task.Id);
                DispatchTask(Kernel, goal, task);
                var dispatch = task.LastDispatch!;
                Kernel.RecordTaskProcessStarted(goal.Id, task.Id, new(12345, dispatch.Command,
                    dispatch.WorkingDirectory, "stdout", "stderr", "exit", DateTimeOffset.UtcNow, null, null));
                return DispatchStartOutcome.Started();
            },
            runAcceptanceSummary: _ =>
            {
                Gates.Add(new(Current.CandidateSha, Current.MainSha));
                return _freshFailure ?? AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            recordAcceptanceFailure: (goal, checks, branch, main, attrs, baseline) =>
                Kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main, attrs, baseline),
            land: goal =>
            {
                _landed = true;
                return new(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(),
                    "integration", true, "Stub landing after executed gate");
            },
            writeEscalation: (_, _, reason) => Holds.Add(reason));
        driver.SoloAcceptanceContextReader = _ => Current;
        driver.CohortAttributionRevisionReader = _ => new(Current.CandidateSha, Current.MainSha);
        return driver;
    }

    internal ConductorAdvanceResult Tick(ConductorDriver driver)
    {
        driver.BeginTick(Kernel, ++_tick);
        return driver.AdvanceOnce(Goal, Policy);
    }

    internal ConductorAdvanceResult Complete(AcceptanceVerificationSummary summary)
    {
        var driver = Driver();
        driver.BeginTick(Kernel, ++_tick);
        return driver.CompleteParallelLandingAcceptance(ConductorParallelAcceptanceCandidate.Create(
            Goal, 0, [], Current.CandidateSha, Current.MainSha), Policy, summary, out _);
    }

    internal Task Restart() => _storage.Restart();
    internal Task RefreshMetadata() => _storage.RefreshMetadata();
    internal async Task<AcceptanceVerificationSummary> PrepareFreshRed()
    {
        _storage.Context = Current;
        await _storage.Produce(attemptId: "fresh-attempt");
        return _freshFailure = _storage.Summary;
    }
    internal string Tasks() => JsonSerializer.Serialize(Goal.ToSnapshot().Tasks);
    internal ProgressEvent[] Supersessions() => Goal.Timeline.Where(evt => evt.Message.StartsWith(
        "Superseded acceptance failure restored Verified:", StringComparison.Ordinal)).ToArray();

    internal void AssertHistory()
    {
        var history = Assert.Single(Supersessions());
        Assert.Contains($"recorded branch={Original.BranchHeadSha} main={Original.MainHeadSha}", history.Message);
        Assert.Contains($"current branch={Current.CandidateSha} main={Current.MainSha}", history.Message);
        // The original diagnostic artifact remains readable after active-state clearing.
        Assert.True(File.Exists(Original.SoloFailureReceipt!.Evidence.Path));
        var proof = JsonSerializer.Deserialize<SoloAcceptanceFailureEvidence>(
            File.ReadAllBytes(Original.SoloFailureReceipt.Evidence.Path));
        Assert.Equal(Original.AttemptId, proof!.Identity.AttemptId);
    }

    private string Git(params string[] args)
    {
        var result = GitCli.Run(_storage.Repo, args);
        Assert.True(result.Succeeded, result.Output);
        return result.Output.Trim();
    }

    public void Dispose() => _storage.Dispose();
}
