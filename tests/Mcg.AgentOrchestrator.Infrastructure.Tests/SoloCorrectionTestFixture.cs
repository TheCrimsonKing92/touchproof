using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: in-memory kernel and a unique owned artifact directory; no processes or git.
internal sealed class SoloCorrectionTestFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-solo-correction", Guid.NewGuid().ToString("N"));
    internal AgentOrchestratorKernel Kernel { get; private set; } = new();
    private readonly GoalId _goalId;
    internal Goal Goal => Kernel.GetGoal(_goalId);
    internal SoloAcceptanceConsumptionContext Context { get; set; } = new("candidate", "main", "tree", "baseline");
    internal ConductorParallelAcceptanceAttempt? Attempt { get; set; }
    internal List<AttributedFailureRouteDecision> Routes { get; } = [];
    internal List<string> Holds { get; } = [];
    internal int RetryCalls { get; private set; }
    internal int AttemptReads { get; private set; }
    internal bool AllowRoute { get; set; } = true;
    internal ConductorAutonomyPolicy Policy { get; } = ConductorAutonomyPolicy.Permissive with { MaxCriterionRetries = 2 };

    internal SoloCorrectionTestFixture()
    {
        Directory.CreateDirectory(Root);
        _goalId = AttributedFailureDeveloperRouteTests.CompletedGoal(Kernel).Id;
    }

    internal SoloAcceptanceFailureHandler Handler() => new(
        () => Kernel,
        (_, _, _, _, _, _) => throw new InvalidOperationException("Route must not record another failure"),
        () => DateTimeOffset.UnixEpoch, _ => Context,
        (goal, task, feedback) =>
        {
            if (!AllowRoute) throw new InvalidOperationException("Unexpected retry charge");
            RetryCalls++;
            return Kernel.RecordCriterionRetryFeedback(goal, task, feedback);
        },
        (goal, prefix, policy, route) =>
        {
            if (!AllowRoute) throw new InvalidOperationException("Unexpected Developer route");
            Routes.Add(route);
            Kernel.RetryTaskAutomatically(goal.Id, route.DeveloperTaskId!, route.Feedback!, RetryCause.NewTestFinding);
            return new(goal.Id.Value, prefix, policy.Name,
                new ConductorAdvanceOutcome.Executed(GoalLifecycleState.AcceptanceFailed, route.Feedback!));
        },
        (_, _, reason) => Holds.Add(reason),
        _ => { AttemptReads++; return Attempt; });

    internal ConductorAdvanceResult Route() => Handler().Route(Goal, Goal.Id.Value[..8], Policy);

    internal void Record(SoloAcceptanceFailureReceipt? receipt = null, AcceptanceFailureOrigin origin = AcceptanceFailureOrigin.Introduced)
    {
        Kernel.RecordAcceptanceFailure(Goal.Id, ["solo tests"], Context.CandidateSha, Context.MainSha,
            [new("solo tests", origin, "hand-written attribution")], attemptId: "attempt", soloFailureReceipt: receipt);
        if (Goal.Status == GoalStatus.Verified)
            Assert.True(Kernel.RouteRecordedAcceptanceFailure(Goal.Id, "fixture failure"));
        Assert.Equal(GoalStatus.AcceptanceFailed, Goal.Status);
    }

    internal SoloAcceptanceFailureReceipt StaleReceipt() => new(1, Goal.Id.Value, "attempt", "old-candidate",
        Context.CandidateTreeSha, Context.MainSha, Context.BaselineSha, Root, new(Path.Combine(Root, "absent.json"), "digest"));

    internal void Trx(params string[] identities)
    {
        var path = Path.Combine(Root, "failed.trx");
        var definitions = new XElement("TestDefinitions");
        var results = new XElement("Results");
        for (var i = 0; i < identities.Length; i++)
        {
            var identity = identities[i];
            var method = AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity);
            definitions.Add(new XElement("UnitTest", new XAttribute("id", i),
                new XElement("TestMethod", new XAttribute("className", method[..method.LastIndexOf('.')]),
                    new XAttribute("name", method[(method.LastIndexOf('.') + 1)..]))));
            results.Add(new XElement("UnitTestResult", new XAttribute("testId", i),
                new XAttribute("testName", identity), new XAttribute("outcome", "Failed"),
                new XElement("Output", new XElement("ErrorInfo",
                    new XElement("Message", $"message {i}\r\nsecond line must stay out")))));
        }
        new XDocument(new XElement("TestRun", new XAttribute("id", "run"), definitions, results)).Save(path);
        Attempt = new("attempt", Goal.Id.Value, Goal.Id.Value[..8], 0, Context.CandidateSha, Context.MainSha,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, ConductorParallelAcceptanceAttemptOutcome.Failed,
            "stdout", "stderr", "exit", "heartbeat", Path.Combine(Root, "result.json"), Path.Combine(Root, "attempt.json"),
            TestResultPaths: [Path.GetFileName(path)]);
        SaveResult(new AcceptanceCheckResult("solo tests", false, 1, "failed", TestResultPaths: [Path.GetFileName(path)],
            FailingTestIdentities: identities));
    }

    internal void SaveResult(params AcceptanceCheckResult[] checks)
    {
        var failure = Goal.LatestAcceptanceFailure!;
        var summary = new AcceptanceVerificationSummary(false, checks, FailedChecks: failure.FailedChecks,
            BranchHeadSha: Attempt!.BranchHeadSha, MainHeadSha: Attempt.MainHeadSha,
            CheckAttributions: failure.CheckAttributions, AttemptId: Attempt.AttemptId);
        var artifact = new ConductorParallelAcceptanceRunArtifact("accepted", null, null, null, null,
            summary, Attempt.BranchHeadSha, Attempt.MainHeadSha, null, null);
        File.WriteAllBytes(Attempt.ResultPath, JsonSerializer.SerializeToUtf8Bytes(artifact,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    internal void CompleteCorrection(string candidate)
    {
        var task = Goal.Tasks.Single();
        ConductorDriverTests.DispatchTask(Kernel, Goal, task, workingDirectory: Root, baseCommit: Context.CandidateSha);
        Kernel.RecordDispatchResultCommit(Goal.Id, task.Id, candidate);
        Kernel.RecordTaskVerification(Goal.Id, task.Id,
            new("fixture", Root, 0, "ok", "", DateTimeOffset.UnixEpoch, HasCommittedChanges: true));
        Assert.Equal(GoalStatus.Verified, Goal.Status);
        Context = Context with { CandidateSha = candidate };
    }

    internal void Restart() => Kernel = AgentOrchestratorKernel.FromSnapshot(Kernel.ExportSnapshot());

    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mcg-solo-correction")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture cleanup escaped its root");
        Directory.Delete(Root, recursive: true);
    }
}
