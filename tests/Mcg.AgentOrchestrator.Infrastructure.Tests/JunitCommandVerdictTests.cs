using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: injected processes, per-test roots and owners, no global mutation.
public sealed class JunitCommandVerdictTests
{
    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 3, false)]
    [InlineData(true, 0, true)]
    public async Task CommandAndReportOutcomes_RequireFreshRowsAndZeroExit(bool writes, int exit, bool passes)
    {
        using var fixture = new JunitCommandFixture(reports: ["reports/*.xml", "other/**/*.xml"]);
        fixture.ExitCode = exit;
        if (writes) fixture.WriteReports = () => fixture.WriteReport("reports/fresh.xml", JunitCommandFixture.Now, ("src/unit.case", "passes", false));
        var check = await fixture.RunAsync();
        Assert.Equal(passes, check.Passed);
        Assert.Equal(writes ? 2 : 1, fixture.Calls.Count);
        if (!writes)
        {
            Assert.Contains("reports/*.xml", check.OutputTail);
            Assert.Contains("other/**/*.xml", check.OutputTail);
            Assert.Empty(check.NormalizedTestResultPaths!);
        }
        else
        {
            Assert.Single(JunitCommandFixture.ReadRows(check));
            Assert.Equal(1, check.ExecutedTestCount);
            if (exit != 0) Assert.Contains("exit code 3", check.OutputTail);
            else Assert.Empty(check.CrosswalkDiagnostics!);
        }
    }

    [Theory]
    [InlineData("duplicate", "Duplicate JUnit test IDs")]
    [InlineData("external", "outside the repository")]
    [InlineData("invalid-json", "invalid results")]
    [InlineData("empty", "no test rows")]
    public async Task InvalidReportEvidence_FailsLoudly(string kind, string reason)
    {
        using var fixture = new JunitCommandFixture();
        fixture.WriteReports = () =>
        {
            var source = kind == "external" ? Path.Combine(Path.GetDirectoryName(fixture.Root)!, "outside.case") : "src/unit.case";
            fixture.WriteReport("reports/a.xml", JunitCommandFixture.Now, (source, "passes", false));
            if (kind == "duplicate") fixture.WriteReport("reports/b.xml", JunitCommandFixture.Now, (source, "passes", false));
            if (kind == "empty") fixture.WriteReport("reports/a.xml", JunitCommandFixture.Now);
        };
        if (kind == "invalid-json") fixture.TransformJson = _ => "{";
        var check = await fixture.RunAsync();
        Assert.False(check.Passed);
        Assert.Contains(reason, check.OutputTail);
        Assert.NotEmpty(fixture.ParsedReports);
    }

    [Fact]
    public async Task TimedOutCommand_DoesNotParseOrPass()
    {
        using var fixture = new JunitCommandFixture();
        fixture.TimedOut = true;
        fixture.WriteReports = () => fixture.WriteReport("reports/a.xml", JunitCommandFixture.Now, ("src/unit.case", "passes", false));
        var check = await fixture.RunAsync();
        Assert.False(check.Passed);
        Assert.Contains(check.CrosswalkDiagnostics!, diagnostic => diagnostic.Category == CrosswalkDiagnosticCategory.Timeout);
        Assert.Empty(fixture.ParsedReports);
    }

    [Fact]
    public async Task UnconfirmedCancellationExit_SurfacesProcessEvidenceWithoutInputValues()
    {
        using var fixture = new JunitCommandFixture();
        const string secret = "private fixture input";
        var original = new OperationCanceledException(secret, new IOException(secret));
        original.Data["AcceptanceCancellationTreeExitConfirmed"] = false;
        original.Data["AcceptanceCancellationProcessId"] = 101;
        original.Data["AcceptanceCancellationRemainingProcessIds"] = "101,102";
        original.Data["AcceptanceCancellationCleanupFailure"] = "Win32Exception";
        original.Data["unrelated"] = secret;
        fixture.WriteReports = () => throw original;
        var check = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = fixture.Check.Name, Type = fixture.Check.Type, Command = fixture.Check.Command,
            Reports = fixture.Check.Reports, Environment = new Dictionary<string, string> { ["FIXTURE_INPUT"] = secret }
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync(check));

        Assert.Single(fixture.Calls); // The command ran; the parser must never run after cancellation.
        Assert.False(Assert.IsType<bool>(exception.Data["AcceptanceCancellationTreeExitConfirmed"]));
        Assert.Equal(101, exception.Data["AcceptanceCancellationProcessId"]);
        Assert.Equal("101,102", exception.Data["AcceptanceCancellationRemainingProcessIds"]);
        Assert.Equal("Win32Exception", exception.Data["AcceptanceCancellationCleanupFailure"]);
        Assert.Contains("remaining_process_ids=101,102", exception.Message);
        Assert.False(exception.Data.Contains("unrelated"));
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(secret, exception.ToString());
    }
}
