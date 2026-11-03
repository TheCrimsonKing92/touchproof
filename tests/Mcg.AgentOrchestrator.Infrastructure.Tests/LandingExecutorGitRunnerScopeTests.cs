using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: overrides belong to each async flow; the repository and census sidecar are private.
public sealed class LandingExecutorGitRunnerScopeTests
{
    [Fact]
    public async Task IndependentFlows_KeepOverlappingOverridesIsolated()
    {
        var baseline = LandingExecutor.GitRunner;
        var installed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCalls = new List<string>();
        var secondCalls = new List<string>();
        Func<string, string[], GitCli.GitResult> first = (_, args) =>
        {
            firstCalls.Add(args[1]);
            return new(0, "first", "");
        };
        Func<string, string[], GitCli.GitResult> second = (_, args) =>
        {
            secondCalls.Add(args[1]);
            return new(0, "second", "");
        };

        var firstFlow = Task.Run(async () =>
        {
            using (LandingExecutor.UseGitRunner(first))
            {
                installed.SetResult();
                await WaitFor(otherFinished.Task, "second flow completed its git call");
                Assert.Same(first, LandingExecutor.GitRunner);
                Assert.Equal("first", LandingExecutor.UpdateRef("unused", "first", "revision", null).Output);
            }
            Assert.Equal(baseline, LandingExecutor.GitRunner);
        });
        var secondFlow = Task.Run(async () =>
        {
            try
            {
                await WaitFor(installed.Task, "first flow installed its override");
                Assert.Equal(baseline, LandingExecutor.GitRunner);
                using (LandingExecutor.UseGitRunner(second))
                    Assert.Equal("second", LandingExecutor.UpdateRef("unused", "second", "revision", null).Output);
                Assert.Equal(baseline, LandingExecutor.GitRunner);
            }
            finally { otherFinished.TrySetResult(); }
        });

        await WaitFor(Task.WhenAll(firstFlow, secondFlow), "both independent flows finished");
        Assert.Equal(["refs/heads/first"], firstCalls);
        Assert.Equal(["refs/heads/second"], secondCalls);
        Assert.Equal(baseline, LandingExecutor.GitRunner);
    }

    [Fact]
    public async Task DescendantFlow_InheritsActiveOverride()
    {
        Func<string, string[], GitCli.GitResult> runner = (_, _) => new(0, "inherited", "");
        using (LandingExecutor.UseGitRunner(runner))
        {
            var descendant = Task.Run(() =>
            {
                Assert.Same(runner, LandingExecutor.GitRunner);
                Assert.Equal("inherited", LandingExecutor.UpdateRef("unused", "child", "revision", null).Output);
            });
            await WaitFor(descendant, "descendant flow finished its git call");
        }
    }

    [Fact]
    public void NestedScopes_RestorePreviousRunner_AndRepeatedDisposeDoesNothing()
    {
        var baseline = LandingExecutor.GitRunner;
        Func<string, string[], GitCli.GitResult> outer = (_, _) => new(0, "outer", "");
        Func<string, string[], GitCli.GitResult> inner = (_, _) => new(0, "inner", "");
        using (LandingExecutor.UseGitRunner(outer))
        {
            using (var innerScope = LandingExecutor.UseGitRunner(inner))
            {
                Assert.Equal("inner", LandingExecutor.UpdateRef("unused", "nested", "revision", null).Output);
                innerScope.Dispose();
                Assert.Same(outer, LandingExecutor.GitRunner);
                Assert.Equal("outer", LandingExecutor.UpdateRef("unused", "outer", "revision", null).Output);
                using (LandingExecutor.UseGitRunner(inner))
                {
                    innerScope.Dispose();
                    Assert.Same(inner, LandingExecutor.GitRunner);
                }
            }
            Assert.Same(outer, LandingExecutor.GitRunner);
        }
        Assert.Equal(baseline, LandingExecutor.GitRunner);
    }

    [Fact]
    public void NullRunner_IsRejectedWithoutReplacingActiveOverride()
    {
        Func<string, string[], GitCli.GitResult> runner = (_, _) => new(0, "active", "");
        using (LandingExecutor.UseGitRunner(runner))
        {
            Assert.Throws<ArgumentNullException>(() => LandingExecutor.UseGitRunner(null!));
            Assert.Same(runner, LandingExecutor.GitRunner);
        }
    }

    [Fact]
    public async Task DefaultRunner_UpdatesRealRef_AndCensusNamesLandingCaller()
    {
        var root = LandingExecutorTests.CreateGitRepository();
        try
        {
            var revision = GitCli.Run(root, "rev-parse", "HEAD");
            Assert.True(revision.Succeeded, revision.Error);
            var path = Path.Combine(root, "census.jsonl");
            var budget = new HostCapacityTestBudget(1, _ => path);
            await using (var held = await budget.EnterAsync())
            {
                var result = LandingExecutor.UpdateRef(root, "scope-default", revision.Output.Trim(), null);
                Assert.True(result.Succeeded, result.Error);
            }

            var actual = GitCli.Run(root, "rev-parse", "refs/heads/scope-default");
            Assert.True(actual.Succeeded, actual.Error);
            Assert.Equal(revision.Output.Trim(), actual.Output.Trim());
            using var row = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path)));
            Assert.Equal(1, row.RootElement.GetProperty("git_launches").GetInt32());
            var callers = row.RootElement.GetProperty("git_callers").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetInt32());
            Assert.DoesNotContain(callers.Keys, key => key.Contains(".cctor", StringComparison.Ordinal));
            var caller = Assert.Single(HostCapacitySlotGitCensus.GroupByCaller(callers)
                .Where(pair => pair.Value != 0));
            Assert.Contains(caller.Key, new[]
            {
                "src:Mcg.AgentOrchestrator.App.Orchestration.LandingExecutor.UpdateRef|update-ref",
                "src:Mcg.AgentOrchestrator.App.Orchestration.LandingExecutor.RunGit|update-ref"
            });
            Assert.Equal(1, caller.Value);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static async Task WaitFor(Task signal, string eventName)
    {
        try { await signal.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"Hang waiting for event: {eventName}.", error);
        }
    }
}
