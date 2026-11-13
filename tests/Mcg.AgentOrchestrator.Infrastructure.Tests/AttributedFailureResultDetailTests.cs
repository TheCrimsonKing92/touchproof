using System.Xml;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static AttributedFailureDeveloperRouteTests;

// Each fixture owns its kernel and result files; no shared process or repository state.
public sealed class AttributedFailureResultDetailTests : IDisposable
{
    private readonly string root = ConductorDriverTests.CreateTempDirectory();

    [Fact]
    public void RecordedFailure_RoutesMessageAndThreeFramesToDeveloper()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var path = WriteResults(root, new AcceptanceTrxFailure("Ns.OrdinaryTests.Fails", "Failed", "expected worktree to exist",
            "  at First()\r\n\r\n--- End of stack trace ---\n at Second()\n at Third()\n at Fourth()"));
        ApplyTrain(kernel, goal, [path]);
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
        Assert.Equal(Candidate, goal.LatestAcceptanceFailure!.BranchHeadSha);
        // Feedback must come from persisted evidence, even after the original file disappears.
        File.Delete(path);
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        driver.BeginTick(kernel, 1);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive with
            { AutomaticRetryOperatorRoutingHoldSeconds = 0 });

        Assert.False(result.WasEscalated);
        Assert.Equal(goal.Tasks.Single().Id, Assert.Single(harness.Dispatches));
        Assert.Empty(harness.Escalations);
        var feedback = Assert.Single(MarkerRetries(goal)).Message;
        var lines = feedback.Split(Environment.NewLine);
        Assert.Equal("failing_tests=OrdinaryTests", lines[1]);
        Assert.Equal("gate_failure Ns.OrdinaryTests.Fails [attributed]: expected worktree to exist | " +
            "at First() ; at Second() ; at Third()", lines[2]);
        Assert.Single(lines.Where(line => line.StartsWith("gate_failure ", StringComparison.Ordinal)));
        Assert.DoesNotContain("Fourth()", feedback);
        Assert.DoesNotContain("End of stack trace", feedback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedRows_MarkOriginalFaultEvidenceWithoutChangingClasses(bool inStack)
    {
        var message = inStack ? "host failed" : new string('x', 2001) + "Stable dotnet build slots busy";
        var stack = "at First()\nat Second()\nat Third()\nat DotnetBuildSlotsBusyException()";
        var path = WriteResults(root,
            new("Ns.ApparatusTests.Fails", "Failed", message, inStack ? stack : "at Host()"),
            new("Ns.OrdinaryTests.Fails", "Failed", "assertion failed", "at Test()"));
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);

        ApplyTrain(kernel, goal, [path]);

        var summary = goal.LatestAcceptanceFailure!;
        Assert.Equal(new[] { "OrdinaryTests" }, summary.FailedChecks);
        var reason = Assert.Single(summary.CheckAttributions!).Evidence!;
        var lines = reason.Split(Environment.NewLine);
        Assert.Equal("merge train RED attributed (recorded) receipt train-red: OrdinaryTests", lines[0]);
        Assert.Equal(3, lines.Length);
        var expectedApparatus = inStack
            ? "gate_failure Ns.ApparatusTests.Fails [apparatus]: host failed | at First() ; at Second() ; at Third()"
            : $"gate_failure Ns.ApparatusTests.Fails [apparatus]: {new string('x', 2000)} | at Host()";
        Assert.Equal(expectedApparatus, lines[1]);
        Assert.Equal("gate_failure Ns.OrdinaryTests.Fails [attributed]: assertion failed | at Test()", lines[2]);
        var parsed = Assert.IsType<AttributedFailure>(AttributedFailureDeveloperRoute.ReadAcceptanceFailure(summary));
        Assert.Equal(new[] { "OrdinaryTests" }, parsed.FailingTests);
        Assert.Equal(string.Join(Environment.NewLine, lines.Skip(1)), parsed.ReproductionDetail);
    }

    [Theory]
    [InlineData("Failed", true)]
    [InlineData(" error ", true)]
    [InlineData("Timeout", true)]
    [InlineData("Aborted", true)]
    [InlineData("NotRunnable", true)]
    [InlineData("Passed", false)]
    [InlineData("NotExecuted", false)]
    [InlineData("Unknown", false)]
    public void ResultOutcomes_OnlyFatalRowsProduceDetails(string outcome, bool fatal)
    {
        var path = WriteResults(root, new AcceptanceTrxFailure("Ns.Tests.Case", outcome, null, null));

        var lines = AttributedFailureResultDetail.Lines([path]);

        if (fatal) Assert.Equal("gate_failure Ns.Tests.Case [attributed]:  | ", Assert.Single(lines));
        else Assert.Empty(lines);
    }

    [Fact]
    public void MultipleFiles_RetainRowOrderAndRepeatedTests()
    {
        var first = WriteResults(root,
            new("Ns.Tests.Same", "Failed", "first", null),
            new("Ns.Tests.Other", "Failed", "second", "at Only()"));
        var second = WriteResults(root, new AcceptanceTrxFailure("Ns.Tests.Same", "Failed", "third", null));

        Assert.Equal(new[]
        {
            "gate_failure Ns.Tests.Same [attributed]: first | ",
            "gate_failure Ns.Tests.Other [attributed]: second | at Only()",
            "gate_failure Ns.Tests.Same [attributed]: third | "
        }, AttributedFailureResultDetail.Lines([first, second]));
    }

    internal static string WriteResults(string root, params AcceptanceTrxFailure[] rows)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".trx");
        using var writer = XmlWriter.Create(path, new XmlWriterSettings { NewLineHandling = NewLineHandling.Entitize });
        new XDocument(new XElement("TestRun", new XElement("Results", rows.Select(row =>
            new XElement("UnitTestResult", row.TestName is null ? null : new XAttribute("testName", row.TestName),
                new XAttribute("outcome", row.Outcome), new XElement("Output", new XElement("ErrorInfo",
                    row.Message is null ? null : new XElement("Message", row.Message),
                    row.StackTrace is null ? null : new XElement("StackTrace", row.StackTrace)))))))).Save(writer);
        return path;
    }

    internal static MergeTrainReceipt Receipt(Goal goal, IReadOnlyList<string> paths)
    {
        var member = new MergeTrainMemberBinding(goal.Id, Candidate, Candidate, ["src/Changed.cs"], ["resource"],
            ChangeRiskTier.DocsOnly, ConductorTransitionDecision.Auto, "ready", "fixture", RebasedTrainHeadRevision: Candidate);
        return new("train-red", MergeTrainIdentity.Create([member, member with { GoalId = GoalId.New() }],
            Main, new string('d', 40), "manifest-v1"), MergeTrainGateOutcome.Failed, DateTimeOffset.UtcNow,
            1, ["gate"], 1, paths);
    }

    internal static void ApplyTrain(AgentOrchestratorKernel kernel, Goal goal, IReadOnlyList<string> paths,
        string source = "recorded", IReadOnlyList<AcceptanceTrxFailure>? reproduced = null)
    {
        var receipt = Receipt(goal, paths);
        MergeTrainAttributedFailure.Apply(receipt, receipt.Identity.Members[0], goal, source, 2, [], _ => { },
            (target, checks, branch, main, attributions, baseline) =>
                kernel.RecordAcceptanceFailure(target.Id, checks, branch, main, attributions, baseline),
            kernel.RouteRecordedAcceptanceFailure, reproduced);
    }

    public void Dispose() => Directory.Delete(root, true);
}
