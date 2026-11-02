using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Owned repositories and sidecars; local census instances isolate synthetic identities.
// The throwing observer forwards other tests' launches and restores the prior delegate.
public sealed class HostCapacitySlotGitCensusTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("slot-git-census-").FullName;

    [Fact]
    public async Task Release_FourFixtureLaunches_WritesMatchingCensus()
    {
        var path = Path.Combine(root, "uses.jsonl");
        var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
        var budget = new HostCapacityTestBudget(1, _ => path);
        var identity = HostCapacitySlotTimingWriter.CurrentTestName();
        Assert.NotEqual("<unknown>", identity);
        var environment = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Census", ["GIT_AUTHOR_EMAIL"] = "census@example.test",
            ["GIT_COMMITTER_NAME"] = "Census", ["GIT_COMMITTER_EMAIL"] = "census@example.test",
            ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "commit.gpgSign", ["GIT_CONFIG_VALUE_0"] = "false",
            ["GIT_CONFIG_KEY_1"] = "core.hooksPath", ["GIT_CONFIG_VALUE_1"] = Path.Combine(root, "no-hooks")
        };

        await using (var held = await budget.EnterAsync())
        {
            AssertGitSucceeded(GitCli.RunWithEnvironment(repo, environment, "init"));
            AssertGitSucceeded(GitCli.RunWithEnvironment(repo, environment, "commit", "--allow-empty", "-m", "first"));
            AssertGitSucceeded(GitCli.RunWithEnvironment(repo, environment, "commit", "--allow-empty", "-m", "second"));
            var probe = InfrastructureTestSupport.RunGitProbe(repo, ["status"]);
            Assert.True(probe.Succeeded, probe.StandardError);
        }

        using var row = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path)));
        Assert.Equal(identity, row.RootElement.GetProperty("test_name").GetString());
        AssertCensus(row.RootElement, new() { ["init"] = 1, ["commit"] = 2, ["status"] = 1 });
        Assert.Equal(1, budget.AvailableSlots);
    }

    [Fact]
    public async Task Release_OutsideLaunches_ExcludesThemAndWritesMeasuredZero()
    {
        var path = Path.Combine(root, "window.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        HostCapacitySlotGitCensus.EnsureInstalled(path);
        AssertGitSucceeded(GitCli.Run(root, "--version"));
        await using (var held = await budget.EnterAsync())
            AssertGitSucceeded(GitCli.Run(root, "--version"));
        AssertGitSucceeded(GitCli.Run(root, "--version"));
        await using (var empty = await budget.EnterAsync()) { }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        using var measured = JsonDocument.Parse(lines[0]);
        using var zero = JsonDocument.Parse(lines[1]);
        AssertCensus(measured.RootElement, new() { ["--version"] = 1 });
        AssertCensus(zero.RootElement, new());
    }

    [Fact]
    public async Task Enter_QueuedAcquisition_ExcludesLaunchesDuringWait()
    {
        var path = Path.Combine(root, "queued.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using var first = await budget.EnterAsync();
        var pending = budget.EnterAsync().AsTask();
        Assert.False(pending.IsCompleted); // The occupied slot is the event gate.
        AssertGitSucceeded(GitCli.Run(root, "--version"));
        await first.DisposeAsync();
        await using var second = await pending.WaitAsync(TimeSpan.FromSeconds(30));
        await second.DisposeAsync();

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        using var waiting = JsonDocument.Parse(lines[1]);
        AssertCensus(waiting.RootElement, new());
    }

    [Fact]
    public async Task Windows_ConcurrentNames_KeepIndependentCoherentDeltas()
    {
        var name = new AsyncLocal<string>();
        var census = new HostCapacitySlotGitCensus(() => name.Value!);
        var aRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Task.Run(async () =>
        {
            name.Value = "A";
            census.Record("before");
            var window = census.Open("A")!;
            census.Record("commit");
            aRecorded.SetResult();
            await bRecorded.Task.WaitAsync(TimeSpan.FromSeconds(30));
            census.Record("commit");
            return window.Close()!;
        });
        var b = Task.Run(async () =>
        {
            name.Value = "B";
            var window = census.Open("B")!;
            await aRecorded.Task.WaitAsync(TimeSpan.FromSeconds(30));
            census.Record("status");
            bRecorded.SetResult();
            return window.Close()!;
        });
        var deltas = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(30));
        var first = deltas[0];
        var second = deltas[1];

        Assert.Equal(2, first.Launches);
        Assert.Equal(KeyValuePair.Create("commit", 2), Assert.Single(first.Subcommands));
        Assert.Equal(1, second.Launches);
        Assert.Equal(KeyValuePair.Create("status", 1), Assert.Single(second.Subcommands));
        Assert.Equal(first.Launches, first.Subcommands.Values.Sum());
        Assert.Equal(second.Launches, second.Subcommands.Values.Sum());
    }

    [Fact]
    public void Windows_OverlappingSameName_UseIndependentSnapshots()
    {
        var census = new HostCapacitySlotGitCensus(() => "same");
        var first = census.Open("same")!;
        census.Record("init");
        var second = census.Open("same")!;
        census.Record("commit");
        Assert.Equal(2, first.Close()!.Launches);
        census.Record("commit");
        var delta = second.Close()!;
        Assert.Equal(2, delta.Launches);
        Assert.Equal(KeyValuePair.Create("commit", 2), Assert.Single(delta.Subcommands));
    }

    [Fact]
    public async Task Enter_DisabledCensus_InstallsNothingAndWritesNothing()
    {
        Action<string>? observer = null;
        Assert.False(HostCapacitySlotGitCensus.EnsureInstalled(null, ref observer));
        Assert.False(HostCapacitySlotGitCensus.EnsureInstalled("", ref observer));
        Assert.Null(observer);
        var budget = new HostCapacityTestBudget(1, _ => null);
        await using (var held = await budget.EnterAsync())
        {
            Assert.Equal(0, budget.AvailableSlots);
            AssertGitSucceeded(GitCli.Run(root, "--version"));
        }
        Assert.Empty(Directory.GetFiles(root));
        Assert.Equal(1, budget.AvailableSlots);

        Assert.True(HostCapacitySlotGitCensus.EnsureInstalled("enabled", ref observer));
        var installed = observer;
        Assert.NotNull(installed);
        Assert.False(HostCapacitySlotGitCensus.EnsureInstalled("enabled", ref observer));
        Assert.Same(installed, observer);
    }

    [Fact]
    public void Read_MixedAndLegacyLines_SumsLaunchesAndPreservesOptionalReceipt()
    {
        var mixed = WriteSidecar("mixed", "4", null, "0", "3");
        var reading = LaneSlotTimingReader.Read([mixed, mixed]);
        Assert.Equal(new LaneSlotTimingReader.LaneSlotTotals(4, 8, 2, 7), reading.Totals);
        Assert.Null(reading.UnavailableReason);
        using var receipt = RecordReceipt(mixed);
        Assert.Equal(7, receipt.RootElement.GetProperty("slot_git_launches").GetInt64());

        var legacy = WriteSidecar("legacy", (string?)null);
        Assert.Null(LaneSlotTimingReader.Read([legacy]).Totals!.GitLaunches);
        using var legacyReceipt = RecordReceipt(legacy);
        Assert.False(legacyReceipt.RootElement.TryGetProperty("slot_git_launches", out _));

        var zero = WriteSidecar("zero", "0");
        Assert.Equal(0, LaneSlotTimingReader.Read([zero]).Totals!.GitLaunches);
        using var zeroReceipt = RecordReceipt(zero);
        Assert.Equal(0, zeroReceipt.RootElement.GetProperty("slot_git_launches").GetInt64());
    }

    [Fact]
    public void Read_UnusableCensus_KeepsTimingAndIgnoresInvalidCounts()
    {
        var trx = WriteSidecar("invalid", "-1", "1.5", "\"bad\"", "null", "9223372036854775807", "1");
        var reading = LaneSlotTimingReader.Read([trx]);
        Assert.Equal(new LaneSlotTimingReader.LaneSlotTotals(6, 12, 2, long.MaxValue), reading.Totals);
        Assert.Contains("slot-timing-git-launches-invalid:", reading.UnavailableReason);
        Assert.Contains(":5 lines", reading.UnavailableReason);
        Assert.DoesNotContain("slot-timing-skipped:", reading.UnavailableReason);
    }

    [Fact]
    public async Task Launch_StartFailures_AreExcludedButFailedGitCommandsCount()
    {
        var path = Path.Combine(root, "starts.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using (var held = await budget.EnterAsync())
        {
            var notStarted = GitCli.RunExecutable(Path.Combine(root, "missing-git.exe"), root, 1000, "ignored");
            Assert.False(notStarted.ProcessStarted);
            var probe = InfrastructureTestSupport.RunGitProbe(root, ["ignored"], startProcess: _ => false);
            Assert.False(probe.ProcessStarted);
            var failedCommand = GitCli.Run(root, "no-such-census-command");
            Assert.True(failedCommand.ProcessStarted);
            Assert.False(failedCommand.Succeeded);
            Assert.True(GitCli.Run(root).ProcessStarted);
        }
        using var row = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path)));
        AssertCensus(row.RootElement, new() { ["no-such-census-command"] = 1, [""] = 1 });
    }

    [Fact]
    public async Task Launch_ThrowingObserver_PreservesGitAndProbeResults()
    {
        var baseline = GitCli.Run(root, "--version");
        var probeBaseline = InfrastructureTestSupport.RunGitProbe(root, ["--version"]);
        AssertGitSucceeded(baseline);
        Assert.True(probeBaseline.Succeeded, probeBaseline.StandardError);
        var testName = HostCapacitySlotTimingWriter.CurrentTestName();
        var path = Path.Combine(root, "throwing.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        HostCapacitySlotGitCensus.EnsureInstalled(path);
        var previous = Volatile.Read(ref GitCli.LaunchObserver);
        var observed = 0;
        Action<string> throwing = command =>
        {
            if (HostCapacitySlotTimingWriter.CurrentTestName() != testName)
            {
                previous?.Invoke(command);
                return;
            }
            Interlocked.Increment(ref observed);
            throw new InvalidOperationException("census observer failure");
        };
        Assert.Same(previous, Interlocked.CompareExchange(ref GitCli.LaunchObserver, throwing, previous));
        try
        {
            await using var held = await budget.EnterAsync();
            var actual = GitCli.Run(root, "--version");
            var probe = InfrastructureTestSupport.RunGitProbe(root, ["--version"]);
            Assert.Equal(baseline, actual);
            Assert.Equal(probeBaseline.ExitCode, probe.ExitCode);
            Assert.Equal(probeBaseline.StandardOutput, probe.StandardOutput);
            Assert.Equal(probeBaseline.StandardError, probe.StandardError);
            Assert.Equal(probeBaseline.Succeeded, probe.Succeeded);
            Assert.Equal(2, observed);
            await held.DisposeAsync();
            Assert.Equal(1, budget.AvailableSlots);
            Assert.Single(File.ReadAllLines(path));
        }
        finally { Interlocked.CompareExchange(ref GitCli.LaunchObserver, previous, throwing); }
    }

    private string WriteSidecar(string name, params string?[] launches)
    {
        var trx = Path.Combine(root, name + ".trx");
        File.WriteAllText(trx, """
            <TestRun><Results><UnitTestResult testName="Example.Tests.Method" outcome="Passed" duration="00:00:05" /></Results></TestRun>
            """);
        File.WriteAllLines(LaneSlotTimingReader.PathFor(trx), launches.Select(value =>
            "{\"test_name\":\"Example.Tests.Method\",\"wait_ms\":1,\"held_ms\":2,\"slot_count\":2,\"process_id\":1"
            + (value is null ? "}" : ",\"git_launches\":" + value + "}")));
        return trx;
    }

    private JsonDocument RecordReceipt(string trx)
    {
        string? receipt = null;
        var result = new AcceptanceCheckResult("census", true, 0, null, TestResultPaths: [trx]);
        GateLaneReceiptLedger.Record(root, "goal", "census", result, TimeSpan.FromSeconds(10),
            DateTimeOffset.UnixEpoch, (path, line) =>
            {
                if (Path.GetFileName(path) == GateLaneReceiptLedger.FileName) receipt = line;
            });
        Assert.NotNull(receipt);
        return JsonDocument.Parse(receipt);
    }

    private static void AssertGitSucceeded(GitCli.GitResult result) =>
        Assert.True(result.Succeeded, result.Error);

    private static void AssertCensus(JsonElement row, Dictionary<string, int> expected)
    {
        Assert.Equal(expected.Values.Sum(), row.GetProperty("git_launches").GetInt32());
        var actual = row.GetProperty("git_subcommands").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetInt32());
        Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
