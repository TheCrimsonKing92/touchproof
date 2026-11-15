using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its filesystem, fake transport and completion events; no child processes.
public sealed class RemoteLaneSlotTimingSidecarTests
{
    private const string TrxName = "heavy.trx";
    private static string SidecarName => Path.GetFileName(LaneSlotTimingReader.PathFor(TrxName));

    [Xunit.Fact]
    public async Task Fetch_ListedSidecar_LandsBesideTrxAndSurvivesRetention()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        SetPayloads(fake, 4357);
        var handle = await fake.Submit();
        await Complete(fake, handle, Status(advertise: true));

        var result = Assert.IsType<RemoteLaneResult>(handle.TryGetResult());
        Assert.Equal(7, result.ExitCode);
        var staged = Assert.Single(result.TestResultPaths);
        Assert.Equal(TrxName, Path.GetFileName(staged));
        Assert.Equal(fake.FetchContents[TrxName], File.ReadAllText(staged));
        Assert.Equal(fake.FetchContents[SidecarName], File.ReadAllText(LaneSlotTimingReader.PathFor(staged)));
        var fetch = Assert.Single(SidecarFetches(fake));
        Assert.Equal(Path.GetDirectoryName(staged), fetch.Directory);
        Assert.True(fetch.Bound < TimeSpan.FromSeconds(60));
        Assert.Matches("^f-[0-9a-f]{8}\\.part$", fetch.Arguments[^1]);
        Assert.Contains(handle.Snapshot().Fetches, step => step.Name == "fetch-slot-timing-1");

        var retained = Retain(fake, result);
        Assert.NotEqual(staged, retained);
        Assert.Equal(fake.FetchContents[TrxName], File.ReadAllText(retained));
        Assert.Equal(fake.FetchContents[SidecarName], File.ReadAllText(LaneSlotTimingReader.PathFor(retained)));
        Assert.Empty(Directory.GetFiles(fetch.Directory, "*.part"));
    }

    [Xunit.Theory]
    [Xunit.InlineData("[\"heavy.trx\"]")]
    [Xunit.InlineData("[\"../heavy.slot-timing.jsonl\"]")]
    [Xunit.InlineData("[\"C:\\\\heavy.slot-timing.jsonl\"]")]
    [Xunit.InlineData("[\"heavy.slot-timing.jsonl\\n\"]")]
    [Xunit.InlineData("[\"heavy.slot-timing.jsonl.extra\"]")]
    [Xunit.InlineData("[\"heavy slot-timing.jsonl\"]")]
    [Xunit.InlineData("[42]")]
    [Xunit.InlineData("[null]")]
    [Xunit.InlineData("\"heavy.slot-timing.jsonl\"")]
    [Xunit.InlineData("null")]
    [Xunit.InlineData("{}")]
    public async Task Completion_InvalidSidecarDeclaration_IsRejectedBeforeFetching(string declaration)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        var status = Status(advertise: false);
        status["slotTiming"] = JsonSerializer.Deserialize<JsonElement>(declaration);
        await Complete(fake, handle, status);

        var error = Assert.Throws<RemoteLaneTransportException>(() => handle.TryGetResult());
        Assert.StartsWith("invalid-slot-timing-name: ", error.Message);
        Assert.Empty(SidecarFetches(fake));
        Assert.DoesNotContain(fake.Calls, call => call.Arguments[3].EndsWith("/" + TrxName, StringComparison.Ordinal));
    }

    [Xunit.Theory]
    [Xunit.InlineData(4357, true)]
    [Xunit.InlineData(4358, false)]
    public async Task Evaluate_RetainedRemoteTrxWithFetchedSidecar_ComputesBound(long launches, bool passed)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        SetPayloads(fake, launches);
        var handle = CreateHandle(fake, async (args, directory, bound, token) =>
        {
            var result = await fake.Transport(args, directory, bound, token);
            if (args[3].EndsWith("/" + TrxName, StringComparison.Ordinal))
                fake.Clock.Advance(TimeSpan.FromSeconds(40));
            else if (args[3].EndsWith("/" + SidecarName, StringComparison.Ordinal))
                fake.Clock.Advance(bound);
            return result;
        });
        await Complete(fake, handle, Status(advertise: true));

        Assert.True(fake.Clock.GetUtcNow() - handle.NewestHeartbeat!.Value < TimeSpan.FromSeconds(60));
        var retained = Retain(fake, Assert.IsType<RemoteLaneResult>(handle.TryGetResult()));
        var result = Evaluate(retained);
        Assert.Equal(passed, result.Passed);
        Assert.False(result.Advisory);
        Assert.Equal(passed ? 0 : 1, result.ExitCode);
        Assert.StartsWith(passed ? "status=passed;" : "status=failed;", result.ResultSummary);
        Assert.Contains($"; launches={launches};", result.ResultSummary);
        Assert.Contains("; maximum=4357;", result.ResultSummary);
        Assert.Contains($"; rows={HeavyFixtureGitLaunchBoundCheck.Baselines.Sum(entry => entry.Rows)}", result.ResultSummary);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Evaluate_SidecarNotAdvertised_SkipsWithUnreadableSlotTiming(bool emptyArray)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        SetPayloads(fake, 4357);
        var handle = await fake.Submit();
        var status = Status(advertise: false);
        if (emptyArray) status["slotTiming"] = Array.Empty<string>();
        await Complete(fake, handle, status);

        Assert.Empty(SidecarFetches(fake));
        AssertMissingTiming(fake, handle);
    }

    [Xunit.Theory]
    [Xunit.InlineData("exit", false)]
    [Xunit.InlineData("exit", true)]
    [Xunit.InlineData("timeout", true)]
    [Xunit.InlineData("missing", false)]
    [Xunit.InlineData("exception", true)]
    [Xunit.InlineData("cancelled-fetch", true)]
    public async Task Evaluate_SidecarFetchFails_LaneCompletesAndBoundSkips(string failure, bool partial)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        SetPayloads(fake, 4357);
        var handle = CreateHandle(fake, (args, directory, bound, token) =>
        {
            if (!args[3].EndsWith("/" + SidecarName, StringComparison.Ordinal))
                return fake.Transport(args, directory, bound, token);
            fake.Calls.Enqueue(new(args, directory, bound));
            if (partial) File.WriteAllText(Path.Combine(directory, args[^1]), "{partial");
            if (failure == "exception") throw new IOException("fixture-sidecar-fetch-failed");
            if (failure == "cancelled-fetch") throw new OperationCanceledException("fixture-transfer-bound");
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(failure == "exit" ? 1 : 0,
                "", TimedOut: failure == "timeout"));
        });
        await Complete(fake, handle, Status(advertise: true));

        var fetch = Assert.Single(SidecarFetches(fake));
        Assert.Empty(Directory.GetFiles(fetch.Directory, "*.part"));
        var step = Assert.Single(handle.Snapshot().Fetches, step => step.Name == "fetch-slot-timing-1");
        Assert.NotEmpty(step.StderrTail);
        Assert.NotNull(step.StderrPath);
        AssertMissingTiming(fake, handle);
    }

    [Xunit.Theory]
    [Xunit.InlineData(60, 0, 55)]
    [Xunit.InlineData(60, 40, 15)]
    [Xunit.InlineData(60, 55, 0)]
    [Xunit.InlineData(60, 58, 0)]
    [Xunit.InlineData(4, 0, 0)]
    [Xunit.InlineData(300, 0, 120)]
    public async Task Fetch_SidecarBudget_LeavesLeaseMarginOrSkips(
        int leaseSeconds, int trxSeconds, int expectedBoundSeconds)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        SetPayloads(fake, 4357);
        var handle = CreateHandle(fake, async (args, directory, bound, token) =>
        {
            if (args[3].EndsWith("/" + SidecarName, StringComparison.Ordinal))
            {
                fake.Calls.Enqueue(new(args, directory, bound));
                File.WriteAllText(Path.Combine(directory, args[^1]), "{partial");
                fake.Clock.Advance(bound);
                return new GoalAcceptanceVerifier.CommandResult(0, "", TimedOut: true);
            }
            var result = await fake.Transport(args, directory, bound, token);
            if (args[3].EndsWith("/" + TrxName, StringComparison.Ordinal))
                fake.Clock.Advance(TimeSpan.FromSeconds(trxSeconds));
            return result;
        }, leaseSeconds);
        await Complete(fake, handle, Status(advertise: true));

        if (expectedBoundSeconds == 0) Assert.Empty(SidecarFetches(fake));
        else
        {
            var fetch = Assert.Single(SidecarFetches(fake));
            Assert.Equal(TimeSpan.FromSeconds(expectedBoundSeconds), fetch.Bound);
            Assert.True(fetch.Bound < TimeSpan.FromSeconds(leaseSeconds - trxSeconds));
            Assert.Empty(Directory.GetFiles(fetch.Directory, "*.part"));
        }
        var step = Assert.Single(handle.Snapshot().Fetches, step => step.Name == "fetch-slot-timing-1");
        Assert.NotNull(step.StderrPath);
        if (expectedBoundSeconds == 0)
            Assert.Contains("insufficient-lease-budget", step.StderrTail);
        else Assert.True(step.TimedOut);
        Assert.True(fake.Clock.GetUtcNow() - handle.NewestHeartbeat!.Value < TimeSpan.FromSeconds(leaseSeconds));
        AssertMissingTiming(fake, handle);
    }

    [Xunit.Fact]
    public async Task Fetch_MultipleSidecars_RecalculatesRemainingLease()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        SetPayloads(fake, 4357);
        var handle = CreateHandle(fake, (args, directory, bound, token) =>
        {
            if (!args[3].EndsWith("/" + SidecarName, StringComparison.Ordinal))
                return fake.Transport(args, directory, bound, token);
            fake.Calls.Enqueue(new(args, directory, bound));
            fake.Clock.Advance(bound);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "", TimedOut: true));
        });
        var status = Status(advertise: true);
        var secondSidecar = "second.slot-timing.jsonl";
        status["slotTiming"] = new[] { SidecarName, secondSidecar };
        await Complete(fake, handle, status);

        Assert.Equal(TimeSpan.FromSeconds(55), Assert.Single(SidecarFetches(fake)).Bound);
        Assert.DoesNotContain(fake.Calls, call => call.Arguments[3].EndsWith("/" + secondSidecar, StringComparison.Ordinal));
        var skipped = Assert.Single(handle.Snapshot().Fetches, step => step.Name == "fetch-slot-timing-2");
        Assert.Contains("insufficient-lease-budget", skipped.StderrTail);
        AssertMissingTiming(fake, handle);
    }

    [Xunit.Fact]
    public async Task Fetch_LaneCancelledDuringSidecarTransfer_PreservesCancellation()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        SetPayloads(fake, 4357);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = CreateHandle(fake, async (args, directory, bound, token) =>
        {
            if (!args[3].EndsWith("/" + SidecarName, StringComparison.Ordinal))
                return await fake.Transport(args, directory, bound, token);
            fake.Calls.Enqueue(new(args, directory, bound));
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancellation must end the sidecar transfer.");
        });
        await fake.Polls.Writer.WriteAsync(new(Status(advertise: true)));
        await AwaitEvent(entered.Task, "sidecar transfer entered");
        handle.Abandon();
        await AwaitEvent(handle.PollLoop, "cancelled poll loop completed");

        Assert.Null(handle.TryGetResult());
        var fetch = Assert.Single(SidecarFetches(fake));
        Assert.True(File.Exists(Path.Combine(fetch.Directory, TrxName)));
        Assert.False(File.Exists(Path.Combine(fetch.Directory, SidecarName)));
        Assert.Empty(Directory.GetFiles(fetch.Directory, "*.part"));
    }

    [Xunit.Fact]
    public void ExecutorLane_SidecarSuffix_MatchesReader()
    {
        var script = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(),
            "scripts", "executor", "tools", "Invoke-ExecutorLane.ps1"));
        Assert.Contains("'" + SidecarName["heavy".Length..] + "'", script);
    }

    private static Dictionary<string, object?> Status(bool advertise)
    {
        var status = FakeSshRemoteLaneTransport.Status("completed");
        status["trx"] = new[] { TrxName };
        if (advertise) status["slotTiming"] = new[] { SidecarName };
        return status;
    }

    private static async Task Complete(FakeSshRemoteLaneTransport fake, SshRemoteLaneHandle handle,
        Dictionary<string, object?> status)
    {
        await fake.Polls.Writer.WriteAsync(new(status));
        await AwaitEvent(handle.PollLoop, "completed lane poll loop");
    }

    private static async Task AwaitEvent(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }

    private static SshRemoteLaneHandle CreateHandle(FakeSshRemoteLaneTransport fake,
        Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> transport,
        int leaseSeconds = 60)
    {
        var staging = Path.Combine(fake.Root, "staging");
        Directory.CreateDirectory(staging);
        return fake.Handle = new(FakeSshRemoteLaneTransport.Request,
            new("one", leaseSeconds, "ssh", "runner", RunRoot: "C:/mcg-executor"), staging,
            "123456789", "lane", fake.Clock, transport, TimeSpan.Zero, null);
    }

    private static FakeSshRemoteLaneTransport.Call[] SidecarFetches(FakeSshRemoteLaneTransport fake) =>
        fake.Calls.Where(call => call.Arguments.Length >= 5 &&
            call.Arguments[3].EndsWith("/" + SidecarName, StringComparison.Ordinal)).ToArray();

    private static string Retain(FakeSshRemoteLaneTransport fake, RemoteLaneResult result) =>
        Assert.Single(GoalAcceptanceVerifier.CopyCompletedTestReceiptsToAttemptFolder(result.TestResultPaths,
            Path.Combine(fake.Root, "retained", "attempt"))!);

    private static AcceptanceCheckResult Evaluate(string retained) =>
        Assert.IsType<AcceptanceCheckResult>(HeavyFixtureGitLaunchBoundCheck.Evaluate(
            [new AcceptanceCheckResult("remote lane", true, 0, null, TestResultPaths: [retained])]));

    private static void AssertMissingTiming(FakeSshRemoteLaneTransport fake, SshRemoteLaneHandle handle)
    {
        var lane = Assert.IsType<RemoteLaneResult>(handle.TryGetResult());
        Assert.Equal(7, lane.ExitCode);
        var staged = Assert.Single(lane.TestResultPaths);
        Assert.Equal(fake.FetchContents[TrxName], File.ReadAllText(staged));
        Assert.False(File.Exists(LaneSlotTimingReader.PathFor(staged)));
        var retained = Retain(fake, lane);
        Assert.True(File.Exists(retained));
        Assert.False(File.Exists(LaneSlotTimingReader.PathFor(retained)));
        var bound = Evaluate(retained);
        Assert.False(bound.Passed);
        Assert.True(bound.Advisory);
        Assert.Null(bound.ExitCode);
        Assert.StartsWith("status=skipped;", bound.ResultSummary);
        Assert.Contains("unreadable slot timing", bound.ResultSummary);
    }

    private static void SetPayloads(FakeSshRemoteLaneTransport fake, long launches)
    {
        var rows = HeavyFixtureGitLaunchBoundCheck.Baselines.SelectMany(entry => entry.Classes)
            .SelectMany(member => Enumerable.Range(0, member.Rows)
                .Select(index => $"{member.ClassName}.Scenario(row: {index})")).ToArray();
        Assert.NotEmpty(rows);
        fake.FetchContents[TrxName] = new XDocument(new XElement("TestRun", new XElement("Results",
            rows.Select((identity, index) => new XElement("UnitTestResult", new XAttribute("testId", index),
                new XAttribute("testName", identity), new XAttribute("outcome", "Passed")))))).ToString();
        fake.FetchContents[SidecarName] = string.Join(Environment.NewLine, rows.Select((identity, index) =>
            JsonSerializer.Serialize(new
            {
                test_name = identity[..identity.IndexOf('(')], git_launches = index == 0 ? launches : 0, slot_count = 0
            }))) + Environment.NewLine;
    }
}
