using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// No real git or state store: each instance owns its temporary tree and hooks.
internal sealed class OrphanMetadataSweepFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "orphan-metadata-" + Guid.NewGuid().ToString("N"));
    internal OrphanSweepTestFixture Tree { get; }
    internal string Folder { get; }
    internal GoalId Goal { get; } = new("f88c7e54d448431091f896b62b1e3480");
    internal IReadOnlyList<GoalSummary> Metadata { get; set; } = [];
    internal int Reads { get; private set; }
    internal Func<IReadOnlyList<GoalSummary>> Read { get; set; }

    internal OrphanMetadataSweepFixture()
    {
        Tree = new(Root);
        Folder = Tree.AddDirectory("f88c7e54");
        Read = () => Metadata;
    }

    internal GoalSummary Summary(string status, string? id = null) =>
        new(id ?? Goal.Value, status, "stored goal", OrphanSweepTestFixture.ReferenceTime.ToString("O"));

    internal GoalWorktreeCleanupHooks Hooks => Tree.Hooks with
    {
        RunWorktreeList = _ => new(0, "", ""),
        ReadStoredGoalMetadata = () => { Reads++; return Read(); }
    };

    // The public facade probes real git before consulting hooks. Exercise the sweep
    // directly so a private non-repository tree reaches the metadata and outcome seams.
    internal GoalWorktreeSweepResult Sweep(AgentOrchestratorKernel? kernel = null,
        GoalWorktreeCleanupHooks? hooks = null)
    {
        var sweepHooks = hooks ?? Hooks;
        return OrphanWorktreeSweep.Run(Root, kernel, sweepHooks, directory =>
        {
            Assert.True(sweepHooks.DeleteDirectoryForCleanup(directory).Succeeded);
            return "removed";
        }, "orphan-sweep-regression");
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
