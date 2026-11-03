using System.Reflection;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each override belongs to its async flow; no external resources are used.
public sealed class ScopedFaultHookTests
{
    private static readonly GoalId Goal = GoalId.New();
    private const string DeliveryId = "scoped-fault-hook:delivery";
    private static readonly GoalLandingIntent Intent = new(
        Goal.Value, "goal/scoped-hook", "main", "merge-revision",
        DateTimeOffset.UnixEpoch, "scoped-fault-hook-test");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task IndependentFlows_IsolateOverlappingOverrides(int hook)
    {
        var firstInstalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInstalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCalls = 0;
        var secondCalls = 0;

        var first = Task.Run(async () =>
        {
            try
            {
                using (UseHook(hook, () => firstCalls++))
                {
                    firstInstalled.SetResult();
                    await WaitFor(secondInstalled.Task, "second flow installed its hook");
                    Dispatch(hook);
                    Assert.Equal(1, firstCalls);
                    firstChecked.SetResult();
                }
                Dispatch(hook);
                Assert.Equal(1, firstCalls);
            }
            finally
            {
                firstInstalled.TrySetResult();
                firstChecked.TrySetResult();
            }
        });
        var second = Task.Run(async () =>
        {
            try
            {
                await WaitFor(firstInstalled.Task, "first flow installed its hook");
                // The first override is active, but was installed after this flow was captured.
                Dispatch(hook);
                using (UseHook(hook, () => secondCalls++))
                {
                    secondInstalled.SetResult();
                    await WaitFor(firstChecked.Task, "first flow dispatched its overlapping hook");
                    Dispatch(hook);
                    Assert.Equal(1, secondCalls);
                }
                Dispatch(hook);
                Assert.Equal(1, secondCalls);
            }
            finally { secondInstalled.TrySetResult(); }
        });

        await WaitFor(Task.WhenAll(first, second), "both independent hook flows finished");
        Dispatch(hook);
        Assert.Equal(1, firstCalls);
        Assert.Equal(1, secondCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NestedScopes_RestorePreviousHookAndDisposeOnlyOnce(int hook)
    {
        var calls = new List<string>();
        using (UseHook(hook, () => calls.Add("outer")))
        {
            Dispatch(hook);
            using (var inner = UseHook(hook, () => calls.Add("inner")))
            {
                Dispatch(hook);
                inner.Dispose();
                Dispatch(hook);
                using (UseHook(hook, () => calls.Add("next")))
                {
                    inner.Dispose();
                    Dispatch(hook);
                }
            }
            Dispatch(hook);
        }
        Dispatch(hook);
        Assert.Equal(new[] { "outer", "inner", "outer", "next", "outer" }, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NoOverride_IsANoOpBeforeAndAfterFaultScope(int hook)
    {
        Dispatch(hook);
        var fault = new InvalidOperationException("injected scoped fault");
        using (UseHook(hook, () => throw fault))
            Assert.Same(fault, Assert.Throws<InvalidOperationException>(() => Dispatch(hook)));
        Dispatch(hook);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DescendantFlow_InheritsAndRestoresHook(int hook)
    {
        var outerCalls = 0;
        var innerCalls = 0;
        using (UseHook(hook, () => Interlocked.Increment(ref outerCalls)))
        {
            await WaitFor(Task.Run(() =>
            {
                Dispatch(hook);
                using (UseHook(hook, () => innerCalls++))
                    Dispatch(hook);
                Dispatch(hook);
            }), "descendant dispatched inherited and nested hooks");
            Dispatch(hook);
        }
        Dispatch(hook);
        Assert.Equal(3, outerCalls);
        Assert.Equal(1, innerCalls);
    }

    [Fact]
    public void NullHooks_AreRejectedWithoutInstallingAnOverride()
    {
        Assert.Throws<ArgumentNullException>(() => GoalCreationSideEffectDelivery.UseBeforeStateCommit(null!));
        Assert.Throws<ArgumentNullException>(() => GoalCreationSideEffectDelivery.UseBeforeEffectDelivery(null!));
        Assert.Throws<ArgumentNullException>(() => GoalOperationJournal.UseBeforeLandingIntentAppend(null!));
        Assert.Throws<ArgumentNullException>(() => GoalReplacementEvidence.UseAfterFinalTransferValidation(null!));
        for (var hook = 0; hook < 4; hook++)
            Dispatch(hook);
    }

    [Theory]
    [InlineData(typeof(GoalCreationSideEffectDelivery), "BeforeStateCommit")]
    [InlineData(typeof(GoalCreationSideEffectDelivery), "BeforeEffectDelivery")]
    [InlineData(typeof(GoalOperationJournal), "BeforeLandingIntentAppend")]
    [InlineData(typeof(GoalReplacementEvidence), "AfterFinalTransferValidation")]
    public void HookDispatch_HasNoAssignableCompatibilitySurface(Type owner, string name)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        Assert.Null(owner.GetProperty(name, flags));
        Assert.Null(owner.GetField(name, flags));
        Assert.NotNull(owner.GetMethod(name, flags));
    }

    private static IDisposable UseHook(int hook, Action action) => hook switch
    {
        0 => GoalCreationSideEffectDelivery.UseBeforeStateCommit(goal =>
        {
            Assert.Equal(Goal, goal);
            action();
        }),
        1 => GoalCreationSideEffectDelivery.UseBeforeEffectDelivery(deliveryId =>
        {
            Assert.Equal(DeliveryId, deliveryId);
            action();
        }),
        2 => GoalOperationJournal.UseBeforeLandingIntentAppend(intent =>
        {
            Assert.Same(Intent, intent);
            action();
        }),
        3 => GoalReplacementEvidence.UseAfterFinalTransferValidation(goal =>
        {
            Assert.Equal(Goal, goal);
            action();
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(hook))
    };

    private static void Dispatch(int hook)
    {
        // These are the dispatch methods called by the production state/side-effect paths.
        switch (hook)
        {
            case 0: GoalCreationSideEffectDelivery.BeforeStateCommit(Goal); break;
            case 1: GoalCreationSideEffectDelivery.BeforeEffectDelivery(DeliveryId); break;
            case 2: GoalOperationJournal.BeforeLandingIntentAppend(Intent); break;
            case 3: GoalReplacementEvidence.AfterFinalTransferValidation(Goal); break;
            default: throw new ArgumentOutOfRangeException(nameof(hook));
        }
    }

    private static async Task WaitFor(Task task, string awaitedEvent)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Hang detector: {awaitedEvent} did not complete.", ex);
        }
    }
}
