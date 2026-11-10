using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fake build hooks and an independently owned temporary ledger.
public sealed class BuildReceiptLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "build-receipts", Guid.NewGuid().ToString("N"));
    private readonly StringWriter _errors = new();

    [Fact]
    public void ThreeBuilds_PreserveAllSchemaFieldsAndOrigins()
    {
        var writer = new BuildReceiptLedger(error: _errors);
        var time = DateTimeOffset.Parse("2026-11-09T12:00:00Z");
        foreach (var (origin, tree, cpu) in new[] { ("worker-check", "a", 100), ("gate", "a", 200), ("train", "b", 300) })
            writer.Record(_root, new(time, "goal", origin, "wbc1:git-tree:" + tree, ["app.csproj"], 400, cpu, null, "none"));

        var lines = File.ReadAllLines(BuildReceiptLedger.ResolveStorePath(_root));
        Assert.Equal(3, lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            using var json = JsonDocument.Parse(lines[i]);
            var row = json.RootElement;
            Assert.Equal(1, row.GetProperty("version").GetInt32());
            Assert.Equal(time, row.GetProperty("recorded_at").GetDateTimeOffset());
            Assert.Equal("goal", row.GetProperty("goal_id").GetString());
            Assert.Equal(new[] { "worker-check", "gate", "train" }[i], row.GetProperty("origin").GetString());
            Assert.Equal(i < 2 ? "wbc1:git-tree:a" : "wbc1:git-tree:b", row.GetProperty("tree_digest").GetString());
            Assert.Equal("app.csproj", row.GetProperty("projects")[0].GetString());
            Assert.Equal(400, row.GetProperty("wall_ms").GetDouble());
            Assert.Equal((i + 1) * 100, row.GetProperty("cpu_ms").GetDouble());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("cpu_ms_unavailable_reason").ValueKind);
            Assert.Equal("none", row.GetProperty("cache").GetString());
        }
        Assert.Equal("", _errors.ToString());
    }

    [Fact]
    public async Task LockedLedger_LeavesBuildResultUnchangedAndWarnsOnce()
    {
        var writer = new BuildReceiptLedger(error: _errors);
        var path = BuildReceiptLedger.ResolveStorePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var locked = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var environment = EnvironmentFor();
        var hooks = Hooks(environment, "RESOURCE phase=gate cpu_ms=42 accounting_source=job");
        var baseline = await AcceptanceDotnetBuildPhase.EnsureAsync(new(["dotnet", "build", "app.csproj"], null), "attempt", hooks);
        var recording = hooks with { ReceiptSite = new(_root, "goal", "gate", "wbc1:git-tree:a", writer) };

        for (var i = 0; i < 2; i++)
        {
            var result = await AcceptanceDotnetBuildPhase.EnsureAsync(new(["dotnet", "build", "app.csproj"], null), "attempt", recording);
            Assert.Equal(baseline, result);
        }
        Assert.Single(_errors.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("warning: build receipt ledger write skipped:", _errors.ToString());
        Assert.Equal(0, locked.Length);
    }

    [Fact]
    public async Task BuildPhase_RecordsCpuOnceAndCompletedReplayDoesNotRecord()
    {
        var writer = new BuildReceiptLedger(error: _errors);
        var phase = new DotnetTestBuildPhase(["dotnet", "build", "app.csproj"], null);
        var hooks = Hooks(EnvironmentFor(), "RESOURCE phase=gate cpu_ms=42 accounting_source=job") with
        {
            ReceiptSite = new(_root, "goal", "pre-review-evidence", "wbc1:git-tree:a", writer)
        };
        await AcceptanceDotnetBuildPhase.EnsureAsync(phase, "attempt", hooks);
        await AcceptanceDotnetBuildPhase.EnsureAsync(phase, "attempt", hooks);

        var line = Assert.Single(File.ReadAllLines(BuildReceiptLedger.ResolveStorePath(_root)));
        Assert.True(BuildReceipt.TryParse(line, out var receipt));
        Assert.Equal(42, receipt!.CpuMs);
        Assert.Equal("pre-review-evidence", receipt.Origin);
        Assert.Equal("none", receipt.Cache);
        Assert.Equal(new[] { "app.csproj" }, receipt.Projects);
    }

    [Fact]
    public async Task Meter_SumsEveryProjectAndMarksMissingCpuUnavailable()
    {
        var meter = new BuildCpuMeter();
        var environment = EnvironmentFor();
        var hooks = meter.Wrap(Hooks(environment, "RESOURCE phase=gate cpu_ms=42 accounting_source=job"));
        await hooks.RunManagedCheckAsync(["dotnet", "build", "one.csproj"], "one", null, environment);
        await hooks.RunManagedCheckAsync(["dotnet", "build", "two.csproj"], "two", null, environment);
        Assert.Equal(2, meter.Runs);
        Assert.Equal(84, meter.CpuMs);
        Assert.Null(meter.UnavailableReason);
        var missing = meter.Wrap(Hooks(environment, "no accounting"));
        await missing.RunManagedCheckAsync(["dotnet", "build", "three.csproj"], "three", null, environment);
        Assert.Equal(3, meter.Runs);
        Assert.Equal("no-resource-receipt", meter.UnavailableReason);
    }

    [Theory]
    [InlineData("base-build-cache projects=Core=hit,Execution=hit built_projects=App", "hit")]
    [InlineData("base-build-cache projects=Core=miss,Execution=miss built_projects=App", "miss")]
    [InlineData("base-build-cache projects=Core=hit,Execution=miss,published built_projects=App", "miss")]
    [InlineData("base-build-cache projects=Core=hit,Execution=changed built_projects=App", "miss")]
    [InlineData("base-build-cache status=miss reason=unresolved-build-inputs", "miss")]
    [InlineData("base-build-cache projects= built_projects=Core=hit", "miss")]
    public async Task CacheSummary_RecordsHitOnlyWhenEveryProjectHit(string summary, string expected)
    {
        var phase = new DotnetTestBuildPhase(["dotnet", "build", "app.csproj"],
            new("main", ["Core.csproj"], ["app.csproj"], ["Core.csproj", "app.csproj"]));
        var site = new BuildReceiptSite(_root, "goal", "gate", "wbc1:git-tree:a",
            new BuildReceiptLedger(error: _errors));
        using (site.Observe(phase, out var meter))
        {
            var hooks = meter.Wrap(Hooks(EnvironmentFor(),
                summary + "; RESOURCE phase=gate cpu_ms=42 accounting_source=job"));
            phase.Run = await hooks.RunManagedCheckAsync(phase.BuildArguments, "build", null, EnvironmentFor());
        }

        var line = Assert.Single(File.ReadAllLines(BuildReceiptLedger.ResolveStorePath(_root)));
        Assert.True(BuildReceipt.TryParse(line, out var receipt));
        Assert.Equal(expected, receipt!.Cache);
        Assert.Equal(42, receipt.CpuMs);
        Assert.Equal("", _errors.ToString());
    }

    [Fact]
    public void ManagedRebuild_UsesLaneAccountingAndSkipsExplicitReuse()
    {
        var ledger = new BuildReceiptLedger(error: _errors);
        var result = new AcceptanceCheckResult("lane", true, 0, null,
            ResultSummary: "RESOURCE phase=gate cpu_ms=99 accounting_source=job");
        ManagedRebuildReceipt.Record(ledger, _root, "goal", "Mtp managed project rebuild", result,
            TimeSpan.FromMilliseconds(100), DateTimeOffset.UtcNow, "a");
        ManagedRebuildReceipt.Record(ledger, _root, "goal", "another lane", result,
            TimeSpan.FromMilliseconds(100), DateTimeOffset.UtcNow, "a");
        ManagedRebuildReceipt.Record(ledger, _root, "goal", "Mtp managed project rebuild",
            result with { TestResultIsExplicitCrossAttemptReuse = true }, TimeSpan.FromMilliseconds(100), DateTimeOffset.UtcNow, "a");
        var line = Assert.Single(File.ReadAllLines(BuildReceiptLedger.ResolveStorePath(_root)));
        Assert.True(BuildReceipt.TryParse(line, out var receipt));
        Assert.Equal("managed-rebuild", receipt!.Origin);
        Assert.Equal("wbc1:git-tree:a", receipt.TreeDigest);
        Assert.Equal(99, receipt.CpuMs);
    }

    private DotnetBuildEnvironment EnvironmentFor() => new("owned", _root, _root, Path.Combine(_root, "lock"), [], "owner");

    private static AcceptanceDotnetBuildPhaseHooks Hooks(DotnetBuildEnvironment environment, string summary) => new(
        (_, _, _, _) => Task.FromResult((new AcceptanceCheckResult("build", true, 0, null,
            environment.ArtifactsPath, "test", environment.LeaseId, ResultSummary: summary), false)),
        _ => environment, value => value, () => throw new InvalidOperationException("No cache expected"),
        (_, _) => throw new InvalidOperationException("No cache identity expected"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        _errors.Dispose();
    }
}
