using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: parser inputs and reason files are private; no CLI host or live stores.
public sealed class CliIntegrationScopeCommandTests
{
    [Fact]
    public void SupportedCommand_MapsExplicitIdentityActionAndPaths()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "Approved adjacent correction");
            var round = Guid.NewGuid().ToString("N");
            string[] args = ["integration-scope", "goal", round, new string('A', 40), new string('B', 40),
                "approve", "--text-file", path, "--idempotency-key", "scope-1", "--path", "src/A.cs", "--path", "src/B.cs"];
            Assert.True(CliArgumentParser.IsRecognizedCommand(args[0]));
            var payload = CliScopeApprovalCommands.ParseIntegrationScope(args, "resolved-goal-id");
            Assert.Equal("resolved-goal-id", payload.GoalId);
            Assert.Equal(round, payload.RoundId);
            Assert.Equal(new string('a', 40), payload.OriginalHead);
            Assert.Equal(new string('b', 40), payload.MainSha);
            Assert.Equal(IntegrationScopeAction.Approve, payload.Action);
            Assert.Equal(new[] { "src/A.cs", "src/B.cs" }, payload.Paths);
            Assert.Equal("Approved adjacent correction", payload.Reason);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("approve", false, false)]
    [InlineData("original-only", true, false)]
    [InlineData("revoke", false, false)]
    [InlineData("revoke", true, true)]
    [InlineData("unknown", false, false)]
    public void InvalidActionPathCombinations_AreRejected(string action, bool paths, bool approval)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "reason");
            var args = Arguments(action, path);
            if (paths) args.AddRange(["--path", "src/A.cs"]);
            if (approval) args.AddRange(["--approval", "intent"]);
            Assert.Throws<ArgumentException>(() => CliScopeApprovalCommands.ParseIntegrationScope(args, "goal"));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("original-only", IntegrationScopeAction.OriginalOnly)]
    [InlineData("revoke", IntegrationScopeAction.Revoke)]
    public void BoundedActions_MapWithoutAdditionalPaths(string action, IntegrationScopeAction expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "reason");
            var args = Arguments(action, path);
            if (action == "revoke") args.AddRange(["--approval", "approval-intent"]);
            var payload = CliScopeApprovalCommands.ParseIntegrationScope(args, "goal");
            Assert.Equal(expected, payload.Action);
            Assert.Empty(payload.Paths);
            Assert.Equal(action == "revoke" ? "approval-intent" : null, payload.ApprovalId);
        }
        finally { File.Delete(path); }
    }

    private static List<string> Arguments(string action, string path) =>
        ["integration-scope", "goal", Guid.NewGuid().ToString("N"), new string('a', 40), new string('b', 40),
            action, "--text-file", path, "--idempotency-key", "key"];
}
