using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCommandResourceHelperCoverageTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    // Explicit classification, checked against the complete declared helper surface. A new
    // helper must be classified here even if it was accidentally introduced as static.
    private static readonly HashSet<string> ResourceHelpers = new(StringComparer.Ordinal)
    {
        "CreateIsolatedCleanupContext", "CreateRefinedWorkspace", "ExecuteCliAndCapture",
        "ExecuteCliAndCaptureResult", "AssertHelpCommandDoesNotResolveGoal", "SnapshotFiles",
        "RunAppCommand", "WritePlanningBacklog", "WriteCompilationBacklog",
        "WriteConflictingCompilationBacklog", "MakeLeaseOwnerStale", "DirtyGoalWorktree",
        "EnsureGitRepository", "RecordRunningProcess", "WriteIdentityBoundHeartbeat",
        "CreateAcceptanceRepository", "CreateShortAcceptanceRepository", "CreateGoalBranchScenario", "CommitGoalWork",
        "CleanupAcceptanceRepository", "RunAppCli", "RunAppCliWithExitTimeout", "RunGit", "RunGitOutput"
    };

    private static readonly HashSet<string> NonResourceHelpers = new(StringComparer.Ordinal)
    {
        "ClearWorkerSandboxEnv", "BacklogItemFor", "NormalizeVolatileTimes", "SubscriptionPlanner",
        "SubscriptionDeveloper", "ReadyBlockedLines", "CountLinesContaining", "CountNonEmptyLines",
        "SingleLineContaining", "TestAgent", "AssignedAgentId", "WithGoalStatus", "BuildTaskStatusProjectionJson"
    };

    [Fact]
    public async Task EveryDeclaredResourceHelperUsesOnlyItsInitializedCapacity()
    {
        var helpers = typeof(CliCommandTestBase).GetMethods(
                BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name != nameof(CliCommandTestBase.InitializeAsync) &&
                !method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .ToArray();
        Assert.Equal(ResourceHelpers.Concat(NonResourceHelpers).Order(StringComparer.Ordinal),
            helpers.Select(method => method.Name).Distinct().Order(StringComparer.Ordinal));

        // The two repository-copy scenarios must not depend on whether another test has
        // initialized the shared template. Warm it explicitly under a separate fixture.
        var root = Directory.CreateTempSubdirectory("helper-slot-coverage-").FullName;
        try
        {
            await using (var setup = new CliCommandSlotProbe(new HostCapacityTestBudget(1, _ => null), usesGit: true))
            {
                await setup.InitializeAsync();
                CliAcceptanceGitRepositoryTemplate.CopyTo(root);
            }

            foreach (var method in helpers.Where(method => ResourceHelpers.Contains(method.Name)))
            {
                Assert.False(method.IsStatic, $"{method} cannot own an instance lease while static.");
                var budget = new HostCapacityTestBudget(1, _ => null);
                var held = await budget.EnterAsync();
                var launchesGit = method.Name is "DirtyGoalWorktree" or "EnsureGitRepository" or
                    "RunGit" or "RunGitOutput";
                var probe = new CliCommandSlotProbe(budget, usesGit: launchesGit);
                var initialization = probe.InitializeAsync().AsTask();
                try
                {
                    if (launchesGit)
                    {
                        // Admission is awaited before any helper runs; no launch thread waits.
                        Assert.False(initialization.IsCompleted);
                        Assert.Equal(1, probe.AcquisitionRequests);
                        await held.DisposeAsync();
                    }
                    await initialization.WaitAsync(HangGuard);
                }
                catch
                {
                    await held.DisposeAsync();
                    await initialization.WaitAsync(HangGuard);
                    await probe.DisposeAsync();
                    throw;
                }
                using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
                var starts = 0;
                var startedWithCapacity = false;
                using var seam = InfrastructureTestSupport.UseGitProbeSeam(startProcess: _ =>
                {
                    Interlocked.Increment(ref starts);
                    startedWithCapacity = probe.AcquisitionRequests == 1 && budget.AvailableSlots == 0;
                    return false; // Observe the real start boundary without mutating any repository.
                });
                // Keep every original null/default-argument scenario. Give git helpers an owned
                // path and explicit arguments so null validation cannot hide the launch boundary.
                var arguments = method.GetParameters()
                    .Select(parameter => parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray();
                if (launchesGit) arguments[0] = root;
                if (method.Name is "RunGit" or "RunGitOutput") arguments[1] = new[] { "--version" };
                object? result = null;
                var invocation = Task.Run(async () => await Record.ExceptionAsync(async () =>
                {
                    try
                    {
                        result = method.Invoke(probe, arguments);
                        if (result is Task task) await task;
                    }
                    catch (TargetInvocationException exception) when (exception.InnerException is not null)
                    {
                        ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    }
                }));

                try
                {
                    if (launchesGit)
                    {
                        Assert.IsType<InvalidOperationException>(await invocation.WaitAsync(HangGuard));
                        Assert.Equal(1, launches.Count);
                        Assert.Equal(1, starts);
                        Assert.True(startedWithCapacity, $"{method} must use its asynchronously initialized slot.");
                        Assert.Equal(1, probe.AcquisitionRequests);
                        Assert.Equal(0, budget.AvailableSlots); // Held until fixture disposal.
                    }
                    else
                    {
                        // Poison arguments may fail or the helper may complete. Neither outcome
                        // may queue for capacity, and the warmed copy helpers launch no git.
                        await invocation.WaitAsync(HangGuard);
                        Assert.Equal(0, launches.Count);
                        Assert.Equal(0, starts);
                        Assert.Equal(0, probe.AcquisitionRequests);
                        Assert.Equal(0, budget.AvailableSlots); // Only the other fixture holds it.
                    }
                }
                finally
                {
                    await held.DisposeAsync();
                    try
                    {
                        await invocation.WaitAsync(HangGuard);
                    }
                    finally
                    {
                        await probe.DisposeAsync().AsTask().WaitAsync(HangGuard);
                        if (method.Name is "CreateAcceptanceRepository" or "CreateShortAcceptanceRepository" &&
                            result is string repository)
                            GoalWorktrees.DeleteDirectoryWithRetry(repository);
                    }
                }

                Assert.Equal(1, budget.AvailableSlots);
                await probe.DisposeAsync();
                Assert.Equal(1, budget.AvailableSlots);
            }
        }
        finally
        {
            GoalWorktrees.DeleteDirectoryWithRetry(root);
        }
    }
}
