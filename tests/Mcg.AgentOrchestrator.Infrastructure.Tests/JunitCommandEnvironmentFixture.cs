using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Controlled real child command and parser; all files, pipes and owners are fixture-local.
internal sealed class JunitCommandEnvironmentFixture : GoalAcceptanceVerifierTestBase, IDisposable
{
    internal string Root { get; }
    internal string ProjectRoot => Path.Combine(Root, "suite");
    internal string Variable { get; }
    internal const string Literal = "fixtures/a chosen.txt";
    internal string Shell { get; }
    internal Check Check { get; }
    internal GoalAcceptanceVerifier Verifier { get; }
    internal string CommandObservation => Path.Combine(ProjectRoot, "command-observed.json");
    internal string ParserObservation => Path.Combine(ProjectRoot, "parser-observed.json");
    internal Func<string>? CleanupProcessEvidence { get; init; }

    internal JunitCommandEnvironmentFixture(string variable, bool optIn, string? pipe = null,
        int exitCode = 0, bool failParser = false, string? diagnostic = null)
    {
        Variable = variable;
        // Use the shared installed-shell resolver, including its WindowsApps exclusion.
        // A PATH-selected package host is not the controlled child this fixture requires.
        Shell = TestPowerShell.Executable;
        Root = CreateManifestWorkspace("{\"checks\":[],\"environmentContract\":{\"version\":1,\"tools\":[]}}");
        Directory.CreateDirectory(Path.Combine(Root, ".git"));
        Directory.CreateDirectory(Path.Combine(ProjectRoot, "fixtures"));
        File.WriteAllText(Path.Combine(ProjectRoot, Literal), "selected fixture");
        var command = WriteScript("command", new { variable, mode = "command", pipe, observation = CommandObservation,
            expected = optIn ? Literal : null, exitCode, diagnostic });
        var parser = WriteScript("parser", new { variable, mode = "parser", observation = ParserObservation, failParser, diagnostic });
        var declaration = new Dictionary<string, object?> { ["name"] = "declared-test", ["type"] = "junit-command",
            ["command"] = Shell, ["arguments"] = new[] { "-NoProfile", "-File", command }, ["project"] = "suite",
            ["reports"] = new[] { "reports/*.xml" }, ["timeoutMinutes"] = 3 };
        if (optIn) declaration["environment"] = new Dictionary<string, string> { [variable] = Literal };
        File.WriteAllText(Path.Combine(Root, "config", "acceptance-manifest.json"), JsonSerializer.Serialize(new
        { version = 1, environmentContract = new { version = 1, tools = Array.Empty<object>() }, checks = new[] { declaration } }));
        Check = Assert.Single(GoalAcceptanceVerifier.AcceptanceManifest.Load(Root, null).Checks);
        // Only the version probe is injected. Both suite and parser use the production owned launcher.
        var resolver = new CrosswalkExecutableResolver(parser, runVersionAsync: (_, _) => Task.FromResult(new WorkerProcessRunResult(0, "fixture-v1", "")));
        Assert.NotNull(resolver.Resolve().Identity);
        Verifier = new GoalAcceptanceVerifier(resolver);
    }

    internal string WriteScript(string name, object config)
    {
        var destination = Path.Combine(ProjectRoot, name + ".ps1");
        File.Copy(Path.Combine(VerifiedRepositoryRoot.Find(), "tests", "junit-command-fixture", "environment-command.ps1.template"), destination);
        File.WriteAllText(destination + ".json", JsonSerializer.Serialize(config));
        return destination;
    }

    internal async Task<AcceptanceCheckResult> RunAsync(CancellationToken token = default, Check? check = null,
        AcceptanceGateEngineSettings? settings = null)
    {
        await using var owner = new AcceptanceAttemptExecutionOwner(
            new("environment-" + Guid.NewGuid().ToString("N"), "goal", Root, "tree", "main", "commit",
                Path.Combine(Root, ".orchestrator", "acceptance-gate-attempts", "environment", "attempt-current"), null, Environment.ProcessId, null),
            settings ?? new AcceptanceGateEngineSettings(), token);
        return (await Verifier.RunInvocationForOwnerTestsAsync(owner, owner.CreateInvocation(Check.Name), check ?? Check, Root, token)).Result;
    }

    internal static string? ObservedValue(string path)
    {
        Assert.True(File.Exists(path), "Required child environment observation was not written.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("value").GetString();
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Correlate the remaining holders with the identities pinned by the event gate.
            // Preserve observed exit/handle-release state after those handles are disposed;
            // a holder PID alone cannot establish which process the gate already awaited.
            // Never stop processes from this snapshot.
            var snapshot = ProcessCommandLines.Snapshot();
            var report = AssemblyTempRootCleanupHolderDiagnostics.Describe(Root, () => snapshot);
            var holders = report.Holders.Take(8).Select(holder =>
                $"pid={holder.ProcessId} parent_pid={holder.ParentProcessId} " +
                $"start_utc_ticks={snapshot.Records.GetValueOrDefault(holder.ProcessId)?.StartedAt?.UtcTicks} " +
                $"image={holder.Image} match={holder.Match}");
            // Omit command lines and current-directory paths: fixture input values must
            // not become diagnostic excerpts, even while diagnosing fixture teardown.
            var detail = string.Join(Environment.NewLine, holders);
            throw new IOException($"{exception.Message}{Environment.NewLine}" +
                $"JUnit fixture cleanup failed; holder_candidates={report.Holders.Count} " +
                $"unreadable_cwd_count={report.UnreadableCurrentDirectoryCount}{Environment.NewLine}{detail}" +
                $"{Environment.NewLine}JUnit fixture process waits: {CleanupProcessEvidence?.Invoke() ?? "not-observed"}", exception);
        }
    }
}
