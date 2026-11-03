using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: overrides belong to each async flow; files and stores use private temp directories.
public sealed class ScopedOperatorInboxSeamTests
{
    [Theory]
    [InlineData("resolution")]
    [InlineData("backoff")]
    [InlineData("writer")]
    [InlineData("journal")]
    public async Task IndependentFlow_ExercisesDefaultPathWithoutCallingPeerOverride(string seam)
    {
        var installed = Signal();
        var peerFinished = Signal();
        var calls = 0;
        var owner = Task.Run(async () =>
        {
            using (UseOverride(seam, () => Interlocked.Increment(ref calls)))
            {
                installed.SetResult();
                await WaitFor(peerFinished.Task, "independent flow exercised the default path");
                Assert.Equal(0, Volatile.Read(ref calls));
                Exercise(seam);
                Assert.Equal(1, Volatile.Read(ref calls));
            }
        });
        // Created from the test root, so this flow does not inherit the owner's override.
        var peer = Task.Run(async () =>
        {
            try
            {
                await WaitFor(installed.Task, "owner installed its override");
                Exercise(seam);
                Assert.Equal(0, Volatile.Read(ref calls));
            }
            finally { peerFinished.TrySetResult(); }
        });

        await WaitFor(Task.WhenAll(owner, peer), "both independent flows completed");
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("resolution")]
    [InlineData("backoff")]
    [InlineData("writer")]
    [InlineData("journal")]
    public void NestedScopes_RestorePreviousOverrideAndThenDefault(string seam)
    {
        var outerCalls = 0;
        var innerCalls = 0;
        var thirdCalls = 0;
        using (UseOverride(seam, () => outerCalls++))
        {
            Exercise(seam);
            Assert.Equal(1, outerCalls);
            using (var inner = UseOverride(seam, () => innerCalls++))
            {
                Exercise(seam);
                Assert.Equal(1, innerCalls);
                Assert.Equal(1, outerCalls);
                inner.Dispose();
                Exercise(seam);
                Assert.Equal(2, outerCalls);
                using (UseOverride(seam, () => thirdCalls++))
                {
                    inner.Dispose();
                    Exercise(seam);
                    Assert.Equal(1, thirdCalls);
                    Assert.Equal(2, outerCalls);
                }
            }
            Exercise(seam);
            Assert.Equal(3, outerCalls);
        }

        Exercise(seam);
        Assert.Equal(3, outerCalls);
        Assert.Equal(1, innerCalls);
        Assert.Equal(1, thirdCalls);
    }

    [Theory]
    [InlineData("resolution")]
    [InlineData("backoff")]
    [InlineData("writer")]
    [InlineData("journal")]
    public async Task ChildTask_InheritsOverrideCapturedBeforeParentDisposes(string seam)
    {
        var parentDisposed = Signal();
        var calls = 0;
        Task child;
        using (UseOverride(seam, () => Interlocked.Increment(ref calls)))
        {
            async Task RunChild()
            {
                await WaitFor(parentDisposed.Task, "parent disposed its override");
                Exercise(seam);
            }

            child = seam == "journal"
                ? Task.Factory.StartNew(RunChild, CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default).Unwrap()
                : Task.Run(RunChild);
        }
        parentDisposed.SetResult();
        await WaitFor(child, "child exercised its captured override");
        Assert.Equal(1, calls);
        Exercise(seam);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("resolution")]
    [InlineData("backoff")]
    [InlineData("writer")]
    [InlineData("journal")]
    public void NoOverride_PreservesProductionEffects(string seam) => Exercise(seam);

    [Fact]
    public void NoOverride_UsesOriginalSleepAndFileWriteDelegates()
    {
        Assert.Equal((Action<TimeSpan>)Thread.Sleep, OperatorInbox.HoldWriteBackoff);
        Assert.Equal((Action<string, string>)File.WriteAllText, OperatorInbox.HoldStoreWriteAllText);
    }

    [Fact]
    public void UseMethods_RejectNullOverrides()
    {
        Assert.Throws<ArgumentNullException>(() => OperatorInbox.UseBeforeLandingEscalationResolution(null!));
        Assert.Throws<ArgumentNullException>(() => OperatorInbox.UseHoldWriteBackoff(null!));
        Assert.Throws<ArgumentNullException>(() => OperatorInbox.UseHoldStoreWriteAllText(null!));
        Assert.Throws<ArgumentNullException>(() => GoalOperationJournal.UseBeforeAcceptanceRetryAppend(null!));
    }

    [Fact]
    public void RemovedHook_IsAbsentAndLiveSeamsHaveNoSetters()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic;
        Assert.Empty(typeof(OperatorInbox).GetMember("BeforeLandingEscalationResolutionSave", flags));
        Assert.Null(typeof(OperatorInbox).GetProperty("BeforeLandingEscalationResolution", flags)?.SetMethod);
        Assert.Null(typeof(OperatorInbox).GetProperty("HoldWriteBackoff", flags)?.SetMethod);
        Assert.Null(typeof(OperatorInbox).GetProperty("HoldStoreWriteAllText", flags)?.SetMethod);
        Assert.Null(typeof(GoalOperationJournal).GetProperty("BeforeAcceptanceRetryAppend", flags)?.SetMethod);
    }

    private static IDisposable UseOverride(string seam, Action observe) => seam switch
    {
        "resolution" => OperatorInbox.UseBeforeLandingEscalationResolution(observe),
        "backoff" => OperatorInbox.UseHoldWriteBackoff(delay =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(100), delay);
            observe();
        }),
        "writer" => OperatorInbox.UseHoldStoreWriteAllText((path, contents) =>
        {
            observe();
            File.WriteAllText(path, contents);
        }),
        "journal" => GoalOperationJournal.UseBeforeAcceptanceRetryAppend(observe),
        _ => throw new ArgumentOutOfRangeException(nameof(seam))
    };

    // Always drive the production invocation site, including when no override is installed.
    private static void Exercise(string seam)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var task = new TaskSpec(TaskId.New(), "Scoped seam task", AgentRole.Developer);
            var goal = new Goal(GoalId.New(), "Scoped seam goal", [task]);
            switch (seam)
            {
                case "resolution":
                    var failureAt = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
                    var resolvedAt = failureAt.AddMinutes(1);
                    var result = OperatorInbox.RecordLandingEscalation(workspace, goal, "Failed", "main");
                    Assert.Equal("ok", result.JsonOutcome);
                    Assert.True(OperatorInbox.ResolveLandingEscalation(
                        workspace, goal, "Repaired", failureAt, resolvedAt));
                    using (var document = JsonDocument.Parse(File.ReadAllText(
                        Path.Combine(workspace.OrchestratorDirectory, "landing-escalations.json"))))
                    {
                        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
                        Assert.Equal(resolvedAt, item.GetProperty("resolvedAtUtc").GetDateTimeOffset());
                        Assert.Equal("Repaired", item.GetProperty("resolutionReason").GetString());
                    }
                    break;
                case "writer":
                    WriteHold(workspace, goal, task);
                    break;
                case "backoff":
                    var attempts = 0;
                    using (OperatorInbox.UseHoldStoreWriteAllText((path, contents) =>
                    {
                        if (Interlocked.Increment(ref attempts) == 1)
                            throw new IOException("Retry this hold write once.");
                        File.WriteAllText(path, contents);
                    }))
                    {
                        WriteHold(workspace, goal, task);
                    }
                    Assert.Equal(2, attempts);
                    break;
                case "journal":
                    Assert.Empty(GoalOperationJournal.Read(root, goal.Id).Entries);
                    GoalOperationJournal.AcceptanceRetried(root, goal, "Repaired", null, null, 1);
                    var entry = Assert.Single(GoalOperationJournal.Read(root, goal.Id).Entries);
                    Assert.Equal("acceptance-retry", entry.Operation);
                    Assert.Equal("Repaired", entry.OperatorReason);
                    Assert.Equal(1, entry.OperatorRegateCount);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(seam));
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static void WriteHold(OrchestratorWorkspace workspace, Goal goal, TaskSpec task)
    {
        var hold = Assert.Single(OperatorInbox.RecordOwnershipHolds(workspace, goal,
            [new OwnershipHoldRequest(task.Id, 1, task.RequiredRole, ["src/Protected.cs"], "Scoped hold")]));
        Assert.Equal(task.Id.Value, hold.TaskId);
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(workspace.OrchestratorDirectory, "ownership-holds.json")));
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(task.Id.Value, item.GetProperty("taskId").GetString());
        Assert.Equal("src/Protected.cs", Assert.Single(item.GetProperty("approvalPaths").EnumerateArray()).GetString());
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitFor(Task task, string expectedEvent)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { Assert.Fail($"Timed out waiting for {expectedEvent}."); }
    }
}
