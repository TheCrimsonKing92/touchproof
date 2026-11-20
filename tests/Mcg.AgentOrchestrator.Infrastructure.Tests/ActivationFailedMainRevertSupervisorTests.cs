using Mcg.AgentOrchestrator.App.Orchestration;
using Repository = ActivationFailedMainRevertTests.Repository;

public sealed class ActivationFailedMainRevertSupervisorTests
{
    [Theory]
    [InlineData("adopted")]
    [InlineData("restored")]
    [InlineData("suppressed")]
    [InlineData("failed-both")]
    public void OtherStatusesSkipCompensation(string status)
    {
        using var repo = new Repository();
        var compensationCalls = 0;
        var supervisor = repo.Supervisor(revert: (_, _, _) =>
        {
            compensationCalls++;
            return new(ActivationFailedMainRevert.Outcome.Held, Reason: "Unexpected compensation");
        });

        supervisor.RecordActivation(status, 1, new(repo.Merge, "candidate", null, null),
            new(repo.FirstParent, "prior", null, null), null, "fixture");

        Assert.Equal(0, compensationCalls);
        Assert.Equal(repo.Merge, repo.Tip);
        Assert.Equal(status, Assert.Single(repo.Events.Entries).Status);
        Assert.StartsWith($"ACTIVATION_{status.ToUpperInvariant().Replace('-', '_')} ", Assert.Single(repo.Lines));
    }

    [Fact]
    public async Task ThrowingCompensationPreservesActivation()
    {
        using var repo = new Repository();
        var compensationCalls = new List<string>();
        var pushCalls = 0;
        repo.SetPushPolicy(true);

        await RunFailedSuccessor(repo, revert: (_, _, failed) =>
        {
            compensationCalls.Add(failed);
            throw new InvalidOperationException("Injected compensation error");
        }, push: (_, _, _) => { pushCalls++; return true; });

        Assert.Equal(repo.Merge, Assert.Single(compensationCalls));
        Assert.Equal(0, pushCalls);
        Assert.Equal(repo.Merge, repo.Tip);
        Assert.Contains("compensation failed (InvalidOperationException)", repo.HeldEvent(repo.Merge), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PushUsesLocalOutcomeAndConfiguredPolicy(bool enabled, bool eligible)
    {
        using var repo = new Repository();
        repo.SetPushPolicy(enabled);
        var pushed = new List<(string Root, string Commit, string Branch)>();
        var supervisor = repo.Supervisor(push: (root, commit, branch) =>
        {
            pushed.Add((root, commit, branch));
            return true;
        });

        repo.RecordReverted(supervisor, eligible ? repo.Merge : repo.FirstParent);

        if (eligible)
        {
            Assert.NotEqual(repo.Merge, repo.Tip);
            Assert.Contains($"ACTIVATION_MAIN_REVERTED failedCommit={repo.Merge} revertCommit={repo.Tip}", repo.Lines);
        }
        else
        {
            Assert.Equal(repo.Merge, repo.Tip);
            Assert.Contains("not a two-parent merge", repo.HeldEvent(repo.FirstParent), StringComparison.Ordinal);
        }

        if (enabled && eligible)
            Assert.Equal((repo.Root, repo.Tip, Repository.Branch), Assert.Single(pushed));
        else
            Assert.Empty(pushed);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("exception")]
    [InlineData("cancellation")]
    public async Task PushFailurePreservesRestore(string failure)
    {
        using var repo = new Repository();
        repo.SetPushPolicy(true);
        var pushed = new List<(string Root, string Commit, string Branch)>();

        await RunFailedSuccessor(repo, push: (root, commit, branch) =>
        {
            pushed.Add((root, commit, branch));
            return failure switch
            {
                "exception" => throw new InvalidOperationException("Injected push error"),
                "cancellation" => throw new OperationCanceledException("Injected push cancellation"),
                _ => false
            };
        });

        AssertLocalRevert(repo, pushed);
    }

    [Fact]
    public async Task ThrowingOutcomeSinkPreservesPushAndRestore()
    {
        using var repo = new Repository();
        repo.SetPushPolicy(true);
        var pushed = new List<(string Root, string Commit, string Branch)>();

        await RunFailedSuccessor(repo, push: (root, commit, branch) =>
        {
            pushed.Add((root, commit, branch));
            return true;
        }, afterConductEvent: line =>
        {
            if (line.StartsWith("ACTIVATION_MAIN_REVERTED ", StringComparison.Ordinal))
                throw new InvalidOperationException("Injected outcome sink error");
        });

        AssertLocalRevert(repo, pushed);
    }

    private static void AssertLocalRevert(Repository repo, List<(string Root, string Commit, string Branch)> pushed)
    {
        var tip = repo.Tip;
        Assert.NotEqual(repo.Merge, tip);
        Assert.Equal(repo.Merge, repo.Git("rev-parse", tip + "^"));
        Assert.Equal((repo.Root, tip, Repository.Branch), Assert.Single(pushed));
        Assert.Equal($"ACTIVATION_MAIN_REVERTED failedCommit={repo.Merge} revertCommit={tip}",
            Assert.Single(repo.Lines, line => line.StartsWith("ACTIVATION_MAIN_REVERT", StringComparison.Ordinal)));
    }

    private static async Task RunFailedSuccessor(
        Repository repo,
        Func<string, string, string, ActivationFailedMainRevert.Result>? revert = null,
        Func<string, string, string, bool>? push = null,
        Action<string>? afterConductEvent = null)
    {
        using var priorLease = new MemoryStream();
        using var failedLease = new MemoryStream();
        var prior = Build(repo, "prior", repo.FirstParent, priorLease);
        var failed = Build(repo, "failed", repo.Merge, failedLease);
        var builds = new Queue<ConductorPreparedSuccessor>([prior, failed]);
        var host = new RestoreHost();
        var supervisor = new ConductorContinuitySupervisor(host, repo.Events,
            inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)], stageSuccessor: _ => builds.Dequeue(),
            activationHealthyTicks: 3,
            activationDelay: (_, _) => throw new InvalidOperationException("No deadline should be needed."),
            verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"),
            appendConductEvent: (_, _, line) =>
            {
                repo.Lines.Add(line);
                afterConductEvent?.Invoke(line);
            })
        {
            ActivationRevertWorkspaceFactory = () => repo.Workspace,
            RevertFailedActivation = revert,
            PushActivationRevert = push
        };

        var result = await supervisor.RunAsync(["conduct", "--loop"], repo.Root,
            Path.Combine(repo.Sandbox, "supervision"), "default", "default")
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        Assert.Equal(4, host.Requests.Count);
        Assert.Equal(failed.AppDllPath, host.Requests[2].CommandPrefix![1]);
        Assert.Equal(prior.AppDllPath, host.Requests[3].CommandPrefix![1]);
        var activation = repo.Events.Entries.Where(evt => evt.Operation == "activation").ToArray();
        Assert.Collection(activation,
            evt => Assert.Equal("adopted", evt.Status),
            evt =>
            {
                Assert.Equal("reverted", evt.Status);
                Assert.Contains($"failedCommit={repo.Merge}", evt.Detail, StringComparison.Ordinal);
                Assert.Contains($"restoredCommit={repo.FirstParent}", evt.Detail, StringComparison.Ordinal);
            },
            evt => Assert.Equal("restored", evt.Status));
        Assert.Contains(repo.Lines, line => line.StartsWith("ACTIVATION_REVERTED ", StringComparison.Ordinal));
    }

    private static ConductorPreparedSuccessor Build(Repository repo, string name, string commit, IDisposable lease)
    {
        var directory = Path.Combine(repo.Sandbox, name);
        return new(directory, Path.Combine(directory, "Mcg.AgentOrchestrator.App.dll"), commit, commit,
            "LOOP_START selfCheck=true", lease);
    }

    private sealed class RestoreHost : IConductorSupervisorProcessHost
    {
        internal List<ConductorSupervisorProcessRequest> Requests { get; } = [];

        public Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request, CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Requests.Add(request);
            if (index > 0)
            {
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                var ticks = index == 2 ? 1 : 3;
                for (var tick = 1; tick <= ticks; tick++)
                    request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
            }

            if (index == 2)
                return Task.FromResult(new ConductorSupervisorProcessResult(-1, 102));

            var restoring = index == 3;
            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new(restoring ? "stop-file" : "max-duration", 3, 1, RestartRequested: !restoring));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 100 + index));
        }
    }
}
