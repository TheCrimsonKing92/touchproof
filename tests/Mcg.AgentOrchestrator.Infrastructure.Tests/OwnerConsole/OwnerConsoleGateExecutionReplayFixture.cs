using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: a private plan directory and a fixed replay clock.
internal sealed class OwnerConsoleGateExecutionReplayFixture : IDisposable
{
    internal const string Ejected = "42a982f4";
    internal const string Survivor = "4eb773ae";
    internal const string Companion = "c9a50f57";
    internal const string Outer = "cf9125b06a8a4f508671ed9e5645806e";
    internal const string Original = "4eb773ae-gate-20261103203920616-979ad9a676b14827bc504e99f624e75e";
    internal const string Current = "4eb773ae-gate-20261103210123951-847dff45d61847a9a48953b999850b28";
    internal const string OriginalTrain = "merge-train-v2-original-full-identity";
    internal const string SurvivorTrain = "merge-train-v2-survivor-full-identity";
    internal static readonly DateTimeOffset Now = new(2026, 11, 3, 21, 14, 0, TimeSpan.Zero);
    internal readonly string Root = Path.Combine(Path.GetTempPath(), "gate-execution-replay-" + Guid.NewGuid().ToString("N"));
    internal readonly OwnerConsoleGateManifestLane[] Manifest = [new("Console", 10), new("Console:expanded", 20), new("Obsolete", 100)];
    internal readonly List<OwnerConductEvent> Events = [];

    internal OwnerConsoleGateExecutionReplayFixture()
    {
        Directory.CreateDirectory(Root);
        WritePlan(Original, ["Console", "Obsolete"]);
        WritePlan(Current, ["Console", "Console:expanded"]);
        Events.Add(OuterEvent(-600));
        Events.Add(Progress(-590, Original, "execution-admitted", "", members: Ejected + "," + Survivor + "," + Companion));
        Events.Add(PlanEvent(-580, Original, Ejected + "," + Survivor + "," + Companion));
        Events.Add(Progress(-570, Original, "shard-complete", "Console", "failed", Ejected + "," + Survivor + "," + Companion));
        Events.Add(new(Now.AddSeconds(-560), "gate-progress", Ejected,
            $"phase=member-ejected goal={Ejected} outer_attempt={Outer} train_identity={OriginalTrain} reason=RedNewestMember"));
        Events.Add(Progress(-550, Current, "execution-admitted", ""));
        Events.Add(PlanEvent(-540, Current));
        Events.Add(Progress(-530, Current, "shard-complete", "Console", "passed"));
        Events.Add(Progress(-520, Current, "verification-check", "Console:expanded"));
        Events.Add(Progress(-510, Current, "check-complete", "build check", "passed"));
        Events.Add(OuterEvent(-60)); // Later stale outer ownership cannot restore participation.
    }

    internal string PlanPath(string execution) => Path.Combine(Root, execution + AcceptanceGateLanePlan.ArtifactSuffix);
    internal void WritePlan(string execution, string[] scheduled) => File.WriteAllText(PlanPath(execution),
        new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion, execution, scheduled, []).ToJson());
    internal OwnerConsoleGateProgress Build(IEnumerable<OwnerConductEvent>? events = null) => OwnerConsoleGateProgress.Build(
        Now, events ?? Events, Manifest, [], planReader: new OwnerConsoleGateLanePlanReader());
    internal OwnerConductEvent PlanEvent(int age, string execution, string members = Survivor + "," + Companion) =>
        Progress(age, execution, "lane-plan", "plan", members: members) with
        { Detail = Progress(age, execution, "lane-plan", "plan", members: members).Detail + " heartbeat=" + JsonSerializer.Serialize(PlanPath(execution)) };
    internal static OwnerConductEvent Progress(int age, string execution, string phase, string target,
        string outcome = "unknown", string members = Survivor + "," + Companion, string outer = Outer, string? train = null) =>
        new(Now.AddSeconds(age), "gate-progress", members.Split(',')[0],
            $"PHASE_PROGRESS phase={phase} execution={execution} outer_attempt={outer} " +
            $"train_identity={train ?? (execution == Original ? OriginalTrain : SurvivorTrain)} members={members} scope=merge-train " +
            $"target={JsonSerializer.Serialize(target)} outcome={outcome} elapsed_ms=0");
    internal static OwnerConductEvent OuterEvent(int age) => new(Now.AddSeconds(age), "acceptance-cohort", Ejected,
        $"ACCEPTANCE_COHORT_INFLIGHT attempt={Outer} members={Ejected},{Survivor},{Companion} outcome=inflight");
    public void Dispose() => Directory.Delete(Root, true);
}
