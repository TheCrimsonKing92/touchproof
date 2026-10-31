using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class PullRequestTrackingTestFixture : IDisposable
{
    internal PullRequestLandingTestFixture Delivery { get; } = new();
    internal Goal Goal => Delivery.Goal;
    internal AgentOrchestratorKernel Kernel => Delivery.Kernel;
    internal OrchestratorWorkspace Workspace => Delivery.Workspace;
    internal Runner Forge { get; } = new();
    internal TimeSpan Elapsed;
    internal CollaborationItemStore Attention { get; }
    internal SqliteOrchestratorStateRepository Repository { get; }
    internal PullRequestDeliveryTracker Tracker { get; }
    internal List<ConductorLandingReceipt> Receipts { get; } = [];

    internal PullRequestTrackingTestFixture()
    {
        Repository = new(Workspace.SqliteStatePath);
        Repository.SaveAsync(Kernel).GetAwaiter().GetResult();
        Assert.False(Delivery.Land().MainAdvanced);
        Assert.NotNull(Goal.DeliveredForReview);
        Attention = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
        Tracker = CreateTracker();
        Git("update-ref", "refs/remotes/origin/main", Delivery.Candidate);
    }

    internal PullRequestDeliveryTracker CreateTracker(OrchestratorWorkspace? workspace = null) =>
        new(Kernel, workspace ?? Workspace, new GitHubPullRequestStatusReader(Forge), Forge, Attention,
            elapsed: () => Elapsed, landingSink: Receipts.Add);
    internal IReadOnlySet<GoalId> Tick() => Tracker.Service([Goal]);
    internal void Merged() => Forge.Json = JsonSerializer.Serialize(new
    {
        state = "MERGED", mergedAt = "2026-10-31T12:00:00Z", mergeCommit = new { oid = Delivery.Candidate },
        closedAt = "2026-10-31T12:00:00Z", url = PullRequestLandingTestFixture.Url
    });
    internal string Git(params string[] args)
    {
        var result = GitCli.Run(Workspace.ExecutionDirectory, args);
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }
    internal CollaborationItem[] ReconcileItems() => Attention.GetAttentionQueueAsync().GetAwaiter().GetResult()
        .Where(item => item.CorrelationKey?.StartsWith("pull-request-reconcile:", StringComparison.Ordinal) == true).ToArray();
    internal GoalPromotionFact? Fact() => Repository.ReadPromotionFactAsync(Goal.Id).GetAwaiter().GetResult();
    public void Dispose() => Delivery.Dispose();

    internal sealed class Runner : IForgeProcessRunner
    {
        internal string Json = """{"state":"OPEN","mergedAt":null,"mergeCommit":null,"closedAt":null,"url":"https://github.com/octo/sample/pull/7"}""";
        internal int Reads;
        internal bool Unavailable;
        internal bool FetchFails;
        internal Action? OnRead;
        internal List<string[]> Fetches { get; } = [];
        public ForgeProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
        {
            if (fileName == "git")
            {
                if (arguments[0] == "remote") return new(true, 0, "https://github.com/octo/sample.git", "");
                Assert.Equal("fetch", arguments[0]);
                Fetches.Add(arguments.ToArray());
                return new(true, FetchFails ? 1 : 0, "", FetchFails ? "fetch failed" : "");
            }
            Assert.Equal("gh", fileName);
            Assert.Equal(["pr", "view", "7", "--repo", "octo/sample", "--json", "state,mergedAt,mergeCommit,closedAt,url"], arguments);
            Reads++;
            OnRead?.Invoke();
            return Unavailable ? new(true, 1, "", "not available") : new(true, 0, Json, "");
        }
    }
}
