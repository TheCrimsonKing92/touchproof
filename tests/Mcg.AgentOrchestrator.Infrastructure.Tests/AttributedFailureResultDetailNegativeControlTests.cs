using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AttributedFailureDeveloperRouteTests;
using static AttributedFailureResultDetailTests;

// Each fixture owns its kernel, files and exclusive file handle.
public sealed class AttributedFailureResultDetailNegativeControlTests : IDisposable
{
    private readonly string root = ConductorDriverTests.CreateTempDirectory();
    private const string Header = "merge train RED attributed (recorded) receipt train-red: Ns.OrdinaryTests";

    [Theory]
    [InlineData("ownership", "\n")]
    [InlineData("reach", "\n")]
    [InlineData("path", "\n")]
    [InlineData("recorded", "\n")]
    [InlineData("ownership", "\r\n")]
    [InlineData("reach", "\r\n")]
    [InlineData("path", "\r\n")]
    [InlineData("recorded", "\r\n")]
    public void DetailBlock_PreservesHeaderIdentityAndRoute(string source, string newline)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var header = $"merge train RED attributed ({source}) receipt train-red: Ns.OrdinaryTests";
        var original = Parse(header);
        Assert.Equal("train-" + source, original.Source);
        Assert.Equal("train-red", original.ReceiptId);
        Assert.Equal(Candidate, original.CandidateSha);
        Assert.Equal(new[] { "Ns.OrdinaryTests" }, original.FailingTests);
        Assert.Null(original.ReproductionDetail);
        var before = AttributedFailureDeveloperRoute.Decide(goal, original, Candidate);
        var expectedSignature = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Candidate + "\nNs.OrdinaryTests"))).ToLowerInvariant()[..16];
        Assert.True(before.IsRoute);
        Assert.Equal(goal.Tasks.Single().Id, before.DeveloperTaskId);
        Assert.Equal(expectedSignature, before.Signature);
        Assert.DoesNotContain("gate_failure ", before.Feedback!);
        var block = "gate_failure Ns.OrdinaryTests.Fails [attributed]: assertion failed | at Test()" + newline +
            "gate_failure Ns.HostTests.Fails [apparatus]: host failed | at Host()";

        var detailed = Parse(header + newline + block);
        var after = AttributedFailureDeveloperRoute.Decide(goal, detailed, Candidate);

        AssertIdentity(original, detailed);
        Assert.Equal(block, detailed.ReproductionDetail);
        Assert.Equal(before.DeveloperTaskId, after.DeveloperTaskId);
        Assert.Equal(before.Signature, after.Signature);
        Assert.Equal(before.DeclineReason, after.DeclineReason);
        Assert.True(after.IsRoute);
        Assert.Contains("failing_tests=Ns.OrdinaryTests" + Environment.NewLine + block, after.Feedback!);
    }

    [Theory]
    [InlineData("missing", "Missing")]
    [InlineData("malformed", "Unparseable")]
    [InlineData("locked", "Unreadable")]
    public void UnavailableResults_KeepHeaderAndDoNotAuthorizeRouting(string kind, string status)
    {
        var path = WriteResults(root, new AcceptanceTrxFailure("Ns.OrdinaryTests.Fails", "Failed", "lost", null));
        if (kind == "missing") File.Delete(path);
        if (kind == "malformed") File.WriteAllText(path, "<broken");
        using var locked = kind == "locked" ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        Assert.Equal(status, AcceptanceTrxFailureReader.Read(path).Status.ToString());
        var lines = AttributedFailureResultDetail.Lines([path]);
        Assert.Empty(lines);
        var reason = Header + (lines.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, lines) : "");
        Assert.Equal(Header, reason);
        var original = Parse(Header);
        var actual = Parse(reason);
        AssertIdentity(original, actual);
        Assert.Null(actual.ReproductionDetail);
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var before = AttributedFailureDeveloperRoute.Decide(goal, original, Candidate);
        var after = AttributedFailureDeveloperRoute.Decide(goal, actual, Candidate);
        Assert.True(before.IsRoute);
        Assert.Equal(before.IsRoute, after.IsRoute);
        Assert.Equal(before.DeveloperTaskId, after.DeveloperTaskId);
        Assert.Equal(before.Signature, after.Signature);
        Assert.Equal(before.DeclineReason, after.DeclineReason);
        Assert.Equal(before.Feedback, after.Feedback);
        var receipt = Receipt(goal, [path]);
        var records = 0;
        var routes = 0;

        MergeTrainAttributedFailure.Apply(receipt, receipt.Identity.Members[0], goal, "recorded", 2, [], _ => { },
            (_, _, _, _, _, _) => records++, (_, _) => { routes++; return true; });

        Assert.Equal(0, records);
        Assert.Equal(0, routes);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Null(goal.LatestAcceptanceFailure);
        var readable = WriteResults(root, new AcceptanceTrxFailure("Ns.OrdinaryTests.Fails", "Failed", "available", null));
        Assert.Equal("gate_failure Ns.OrdinaryTests.Fails [attributed]: available | ",
            Assert.Single(AttributedFailureResultDetail.Lines([path, readable])));
    }

    [Theory]
    [InlineData("\nother text")]
    [InlineData("\ngate_failure test [attributed]: message | \nother text")]
    [InlineData("\n")]
    [InlineData("\ngate_failure test [attributed]: message | \n")]
    [InlineData("\ngate_failure test [attributed]: message | \n\ngate_failure forged")]
    public void OtherTrailingContent_IsRejected(string trailing)
    {
        Assert.Null(AttributedFailureDeveloperRoute.ReadAcceptanceFailure(Summary(Header + trailing)));
    }

    [Fact]
    public void LongMessage_IsBoundedToTwoThousandCharacters()
    {
        var path = WriteResults(root, new AcceptanceTrxFailure("Ns.OrdinaryTests.Fails", "Failed", new string('x', 2001), "at Test()"));

        var line = Assert.Single(AttributedFailureResultDetail.Lines([path]));

        Assert.Equal($"gate_failure Ns.OrdinaryTests.Fails [attributed]: {new string('x', 2000)} | at Test()", line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
    }

    [Theory]
    [InlineData("assertion\r\ngate_failure forged [apparatus]: fake | ")]
    [InlineData("assertion\nmerge train RED attributed (recorded) receipt forged: ForgedTests")]
    public void EmbeddedBreaks_KeepOneDetailAndOriginalHeader(string message)
    {
        var path = WriteResults(root, new AcceptanceTrxFailure("Ns.OrdinaryTests.Fails", "Failed", message, "at First()\nat Second()"));
        Assert.Equal(message, Assert.Single(AcceptanceTrxFailureReader.Read(path).Failures).Message);
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);

        AttributedFailureResultDetailTests.ApplyTrain(kernel, goal, [path]);

        var reason = Assert.Single(goal.LatestAcceptanceFailure!.CheckAttributions!).Evidence!;
        var lines = reason.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.Equal("merge train RED attributed (recorded) receipt train-red: OrdinaryTests", lines[0]);
        Assert.Equal("gate_failure Ns.OrdinaryTests.Fails [attributed]: " +
            message.Replace('\r', ' ').Replace('\n', ' ') + " | at First() ; at Second()", lines[1]);
        Assert.DoesNotContain('\r', lines[1]);
        Assert.DoesNotContain('\n', lines[1]);
        var parsed = Parse(reason);
        Assert.Equal("train-red", parsed.ReceiptId);
        Assert.Equal(new[] { "OrdinaryTests" }, parsed.FailingTests);
        Assert.Equal(lines[1], parsed.ReproductionDetail);
    }

    [Theory]
    [InlineData("Ns.Tests.Fails\r\ngate_failure forged", "Ns.Tests.Fails  gate_failure forged")]
    [InlineData(null, "")]
    public void MissingDiagnosticsAndNameBreaks_KeepOneLine(string? name, string expectedName)
    {
        var path = WriteResults(root, new AcceptanceTrxFailure(name, "Failed", null, "\n--- annotation ---\r\n"));

        Assert.Equal($"gate_failure {expectedName} [attributed]:  | ", Assert.Single(AttributedFailureResultDetail.Lines([path])));
    }

    [Fact]
    public void ReproducedFailures_DoNotReceiveGateDetails()
    {
        var path = WriteResults(root, new AcceptanceTrxFailure("Ns.OrdinaryTests.Fails", "Failed", "gate-only", "at Gate()"));
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);

        AttributedFailureResultDetailTests.ApplyTrain(kernel, goal, [path], "reproduction",
            [new("Ns.OrdinaryTests.Fails", "Failed", "reproduced-only", "at Reproduced()")]);

        var reason = Assert.Single(goal.LatestAcceptanceFailure!.CheckAttributions!).Evidence!;
        Assert.Equal("merge train RED attributed (reproduction) receipt train-red: Ns.OrdinaryTests.Fails" +
            Environment.NewLine + "reproduced Ns.OrdinaryTests.Fails: reproduced-only", reason);
        Assert.DoesNotContain("gate_failure ", reason);
    }

    private static AcceptanceFailureSummary Summary(string evidence) =>
        new(DateTimeOffset.UtcNow, ["Ns.OrdinaryTests"], Candidate, Main,
            [new("Ns.OrdinaryTests", AcceptanceFailureOrigin.Introduced, evidence)]);

    private static AttributedFailure Parse(string evidence) =>
        Assert.IsType<AttributedFailure>(AttributedFailureDeveloperRoute.ReadAcceptanceFailure(Summary(evidence)));

    private static void AssertIdentity(AttributedFailure expected, AttributedFailure actual)
    {
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.ReceiptId, actual.ReceiptId);
        Assert.Equal(expected.CandidateSha, actual.CandidateSha);
        Assert.Equal(expected.FailingTests, actual.FailingTests);
    }

    public void Dispose() => Directory.Delete(root, true);
}
