using Mcg.AgentOrchestrator.App.Cli;

// Parallel-safe: flag validation and async-local console capture only.
public sealed class CliHelpTestsCriterionEvidence
{
    [Xunit.Theory]
    [Xunit.InlineData("criterion-evidence-map")]
    [Xunit.InlineData("criterion-evidence-record")]
    [Xunit.InlineData("criterion-evidence-rephase")]
    public void PublicEvidenceCommandsAcceptGoalAndIntentAttribution(string command)
    {
        CliCommandHelp.ThrowIfInvalidFlags([command, "--goal", "goal-prefix",
            "--idempotency-key", "operator-replay-key", "--operator-actor", "operator"]);
    }

    [Xunit.Theory]
    [Xunit.InlineData("criterion-evidence-map")]
    [Xunit.InlineData("criterion-evidence-record")]
    [Xunit.InlineData("criterion-evidence-rephase")]
    public void PublicEvidenceCommandsStillRejectUnknownFlags(string command)
    {
        var error = Xunit.Assert.Throws<ArgumentException>(() =>
            CliCommandHelp.ThrowIfInvalidFlags([command, "--waive-evidence"]));
        Xunit.Assert.Contains("Unknown option '--waive-evidence'", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Rephase_UsageAndFlags_ExposeExplicitScopeWithoutCriterionSelection()
    {
        Xunit.Assert.Equal("Usage: criterion-evidence-rephase --goal <goal-prefix> <obligation-id> <repositorystate|runtime> <condition> <target> <reason> [--idempotency-key <key>] [--operator-actor <actor>] [--json]",
            CliCommandHelp.CriterionEvidenceRephaseUsage);
        var output = AsyncLocalConsoleRouter.Capture(() => Xunit.Assert.True(
            CliCommandHelp.TryPrintStartupHelp(["criterion-evidence-rephase", "--help"])));
        Xunit.Assert.Contains(CliCommandHelp.CriterionEvidenceRephaseUsage, output, StringComparison.Ordinal);
        foreach (var flag in new[] { "--criterion", "--version" })
        {
            var error = Xunit.Assert.Throws<ArgumentException>(() =>
                CliCommandHelp.ThrowIfInvalidFlags(["criterion-evidence-rephase", flag, "1"]));
            Xunit.Assert.Contains($"Unknown option '{flag}'", error.Message, StringComparison.Ordinal);
        }
        CliCommandHelp.ThrowIfInvalidFlags(["criterion-evidence-rephase", "--actor-kind", "human"]);
    }
}
