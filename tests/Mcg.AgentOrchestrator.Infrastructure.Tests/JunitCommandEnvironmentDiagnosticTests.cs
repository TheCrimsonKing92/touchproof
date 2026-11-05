using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: per-test owned processes and roots; no ambient environment mutation.
public sealed class JunitCommandEnvironmentDiagnosticTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureLimit_PreservesEventAndCapWithCheckLocalReferenceSuppression(bool optIn)
    {
        const long capBytes = 4096;
        using var fixture = new JunitCommandEnvironmentFixture("FIXTURE_" + Guid.NewGuid().ToString("N"), optIn,
            exitCode: 9, diagnostic: string.Concat(Enumerable.Repeat(JunitCommandEnvironmentFixture.Literal, 1024)));
        var output = await AsyncLocalConsoleRouter.Out.CaptureLocalAsync(async () =>
        {
            var result = await fixture.RunAsync(settings: new AcceptanceGateEngineSettings
                { OutputCaptureLimitBytes = capBytes });
            Assert.False(result.Passed);
            Assert.Contains(result.CrosswalkDiagnostics!, diagnostic => diagnostic.Category == CrosswalkDiagnosticCategory.NonzeroExit);
        });

        Assert.Equal(optIn ? JunitCommandEnvironmentFixture.Literal : null,
            JunitCommandEnvironmentFixture.ObservedValue(fixture.CommandObservation));
        var events = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("ACCEPTANCE_CAPTURE_LIMIT_REACHED ", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(events); // Prove the capped publication path ran before checking suppression.
        Assert.All(events, line =>
        {
            Assert.Contains("cap_bytes=4096", line);
            Assert.Contains("run=attempt-current", line);
            if (optIn) Assert.DoesNotContain(" path=", line);
            else Assert.Contains(" path=", line); // Ordinary commands retain their capture references.
        });
        Assert.DoesNotContain(JunitCommandEnvironmentFixture.Literal, output);
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("run-42", "run-42")]
    public void SuppressedCaptureLimit_HasNoCaptureFilenameRunFallback(string? attempt, string expectedRun)
    {
        using var writer = new StringWriter();
        GoalAcceptanceVerifierCaptureCustody.EmitCaptureLimitReached("0123456789abcdef", attempt,
            path: null, capBytes: 4096, writer: writer);
        var line = writer.ToString().Trim();
        Assert.Equal($"ACCEPTANCE_CAPTURE_LIMIT_REACHED goal=01234567 run={expectedRun} cap_bytes=4096", line);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandAndParserFailures_SuppressExcerptsAndCaptureReferences(bool parserFailure)
    {
        var literal = JunitCommandEnvironmentFixture.Literal;
        // Include a suffix as well as the complete value: suppression must cover truncated captures too.
        using var fixture = new JunitCommandEnvironmentFixture("FIXTURE_" + Guid.NewGuid().ToString("N"), true,
            exitCode: parserFailure ? 0 : 9, failParser: parserFailure, diagnostic: literal + "\n" + literal[4..]);
        var result = await fixture.RunAsync();
        Assert.False(result.Passed);
        Assert.Contains(result.CrosswalkDiagnostics!, diagnostic => diagnostic.Category ==
            (parserFailure ? CrosswalkDiagnosticCategory.MalformedResults : CrosswalkDiagnosticCategory.NonzeroExit));
        Assert.DoesNotContain(literal, result.OutputTail ?? "");
        Assert.DoesNotContain(literal[4..], result.ProcessStderr ?? "");
        Assert.Null(result.ProcessStderrPath);
        Assert.NotNull(result.ChildProcessId);
        Assert.NotNull(result.ChildProcessStartedAt);
        Assert.NotNull(result.GateHeartbeatPath);
        var heartbeat = File.ReadAllText(result.GateHeartbeatPath);
        using var document = JsonDocument.Parse(heartbeat);
        Assert.False(document.RootElement.TryGetProperty("stdoutPath", out _));
        Assert.False(document.RootElement.TryGetProperty("stderrPath", out _));
        Assert.False(document.RootElement.TryGetProperty("commandLine", out _));
        Assert.DoesNotContain(literal, heartbeat);
        Assert.Null(JunitCommandEnvironmentFixture.ObservedValue(fixture.ParserObservation));
        if (!parserFailure)
            Assert.Equal(literal + "\n" + literal[4..], Assert.Single(JunitCommandFixture.ReadRows(result)).Message);
    }

    [Fact]
    public async Task LaunchAndReportErrors_SuppressValuesWithoutChangingFailureCategory()
    {
        using var fixture = new JunitCommandFixture();
        const string value = "private-fixture-value";
        var check = new Check { Name = fixture.Check.Name, Type = "junit-command", Command = fixture.Check.Command,
            Reports = fixture.Check.Reports, Environment = new Dictionary<string, string> { ["FIXTURE"] = value } };
        await using var owner = fixture.Owner();
        var launch = await JunitCommandAcceptanceCheck.RunOwnedAsync(check, fixture.Root, owner,
            owner.CreateInvocation(check.Name), null, TimeProvider.System, fixture.Verifier.CrosswalkExecutable, false,
            _ => throw new IOException(value),
            (_, _, _, _, _, _) => throw new IOException(value),
            (_, _, _, _, _, _) => throw new IOException(value), CancellationToken.None);
        Assert.False(launch.Passed);
        Assert.Equal(CrosswalkDiagnosticCategory.StartFailed, Assert.Single(launch.CrosswalkDiagnostics!).Category);
        Assert.DoesNotContain(value, launch.OutputTail!);
        Assert.DoesNotContain(value, JsonSerializer.Serialize(launch.CrosswalkDiagnostics));

        var apparatus = await Assert.ThrowsAsync<InvalidOperationException>(() => JunitCommandAcceptanceCheck.RunOwnedAsync(
            check, fixture.Root, owner, owner.CreateInvocation(check.Name), null, TimeProvider.System,
            fixture.Verifier.CrosswalkExecutable, false,
            _ => throw new InvalidOperationException(value, new IOException(value)),
            (_, _, _, _, _, _) => throw new InvalidOperationException(value),
            (_, _, _, _, _, _) => throw new InvalidOperationException(value), CancellationToken.None));
        Assert.DoesNotContain(value, apparatus.ToString());
        Assert.Null(apparatus.InnerException); // Preserve loud failure without retaining unsuppressed nested launch evidence.

        fixture.WriteReports = () => fixture.WriteReport("reports/fresh.xml", JunitCommandFixture.Now, ("src/test", "case", false));
        fixture.TransformJson = _ => value;
        var report = await fixture.RunAsync(check);
        Assert.False(report.Passed);
        Assert.Contains(report.CrosswalkDiagnostics!, diagnostic => diagnostic.Category == CrosswalkDiagnosticCategory.MalformedResults);
        Assert.DoesNotContain(value, report.OutputTail!);
    }

    [Theory]
    [InlineData("{\"MCG_OWNER\":\"private-fixture-value\"}")]
    [InlineData("{\"FIXTURE\":\"private-fixture-value\",\"FIXTURE\":\"other\"}")]
    [InlineData("{\"FIXTURE\":[\"private-fixture-value\"]}")]
    public void ValidationFailure_DoesNotPublishConfiguredValue(string map)
    {
        var exception = Assert.Throws<JsonException>(() => JunitCommandEnvironmentParserTests.Parse(",\"environment\":" + map));
        Assert.DoesNotContain("private-fixture-value", exception.ToString());
    }

    [Fact]
    public void OverlappingAndEmptyValues_DiagnosticsStaySuppressed()
    {
        var check = new Check { Type = "junit-command", Environment = new Dictionary<string, string>
            { ["A"] = "abcdef", ["B"] = "abc", ["EMPTY"] = "" } };
        var privacy = new JunitCommandEnvironmentDiagnostics(check);
        Assert.DoesNotContain("abc", privacy.Identity("abcdef::abc"));
        Assert.Equal("Diagnostic details suppressed for check-local environment inputs.", privacy.Detail("def"));
    }
}
