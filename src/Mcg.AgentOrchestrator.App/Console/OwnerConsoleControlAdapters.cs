using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class CliConductorConsoleAdapter(OrchestratorWorkspace workspace) : IOwnerConsoleConductor
{
    public int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) =>
        CliConductorCommand.Run(args, workspace, output: output, error: error);
    public int RunStructured(IReadOnlyList<string> args, TextWriter output, TextWriter error, Action<OwnerConsoleStructuredText> fields) =>
        CliConductorCommand.Run(args, workspace, output: output, error: error, observeStatus: snapshot => fields(new(StatusFields(snapshot))));

    internal static OwnerConsoleFieldSnapshot StatusFields(ConductorStatusSnapshot snapshot)
    {
        var fields = new List<OwnerConsoleField>
        {
            new("state", "Conductor state", snapshot.ConductorState), new("pid", "Owner PID", snapshot.OwnerPid?.ToString()),
            new("since", "Since", snapshot.Since.At?.ToString("O")), new("reason", "Reason", snapshot.Since.Reason),
            new("build", "Supervisor build", snapshot.SupervisorBuild),
            new("loop-build", "Loop build", snapshot.LoopBuild?.Commit),
            new("loop-build/source", "Loop build source", snapshot.LoopBuild?.Source),
            new("generation", "Generation", snapshot.Generation.State),
            new("integration-revision", "Main revision", snapshot.Generation.Main), new("tick", "Latest tick", snapshot.LatestTick?.ToString("O")),
            new("clarifications", "Pending clarifications", snapshot.Goals.PendingClarifications?.ToString()),
            new("human-waits", "Human waits", snapshot.Goals.HumanWaits?.ToString()),
            new("landing/goal", "Most recent landing goal", snapshot.MostRecentLanding?.Goal),
            new("landing/at", "Most recent landing at", snapshot.MostRecentLanding?.At.ToString("O")),
            new("adoption", "Worker adoption", snapshot.WorkerAdoption?.Summary),
            new("intents/state", "Pending intents state", snapshot.PendingIntents.State),
            new("intents/count", "Pending intent goals", snapshot.PendingIntents.Goals.Count.ToString()),
            new("goals/count", "Non-terminal goals", snapshot.Goals.Available ? snapshot.Goals.NonTerminal?.Count.ToString() : null),
            new("provider-holds/count", "Provider usage holds", snapshot.ProviderHolds?.Count.ToString()),
            new("supervisor/job-flags", "Supervisor job flags", snapshot.SupervisorJob?.LimitFlags is { } flags ? ProcessJobTrapProbe.FormatFlags(flags) : null),
            new("intents/reason", "Pending intents reason", snapshot.PendingIntents.Reason)
        };
        fields.Add(new("previous/at", "Previous stop at", snapshot.Since.Previous?.At?.ToString("O")));
        fields.Add(new("previous/reason", "Previous stop reason", snapshot.Since.Previous?.Reason));
        fields.Add(new("adoption/deferred", "Deferred identity unproven", snapshot.WorkerAdoption?.DeferredIdentityUnproven.ToString()));
        foreach (var goal in snapshot.Goals.NonTerminal ?? []) fields.AddRange(new OwnerConsoleField[] {
            new(goal.Id + "/id", "Goal ID", goal.Id), new(goal.Id + "/status", "Goal status", goal.Status), new(goal.Id + "/role", "Goal role", goal.Role) });
        foreach (var goal in snapshot.PendingIntents.Goals) fields.AddRange(new OwnerConsoleField[] {
            new("intent/" + goal.Goal + "/id", "Intent goal ID", goal.Goal), new("intent/" + goal.Goal + "/count", "Intent count", goal.Intents.ToString()),
            new("intent/" + goal.Goal + "/latest", "Latest intent", goal.Latest?.ToString("O")) });
        return new("conductor", [.. fields]);
    }
}

internal sealed class CliOwnerDigestConsoleAdapter(OrchestratorWorkspace workspace) : IOwnerConsoleDigestReport
{
    public int Run(TextWriter output) =>
        CliOwnerDigestCommand.Run(["owner-digest"], workspace, output: output);
    public int RunStructured(TextWriter output, Action<OwnerConsoleStructuredText> fields) =>
        CliOwnerDigestCommand.Run(["owner-digest"], workspace, output: output, observeDigest: digest => fields(DigestFields(digest)));

    internal static OwnerConsoleStructuredText DigestFields(Mcg.AgentOrchestrator.Core.OwnerDigestResult digest)
    {
        var rows = new Dictionary<int, OwnerConsoleFieldSnapshot>();
        for (var i = 0; i < digest.Goals.Count; i++)
        {
            var goal = digest.Goals[i];
            rows[2 + i] = OwnerConsoleFieldSnapshot.Create(goal.GoalId,
                new("goal", "Goal ID", goal.GoalId), new("landed", "Landed at", goal.LandedAt.ToString("O")),
                new("revision", "Landing revision", goal.LandingSha), new("landing", "Landing status", goal.LandingStatus),
                new("escape-source", "Escape source", goal.EscapeSource),
                new("human", "Human interventions", goal.Interventions.Human.ToString()),
                new("agent", "Agent interventions", goal.Interventions.Agent.ToString()), new("other", "Other interventions", goal.Interventions.Other.ToString()),
                new("tail", "Tail hours", goal.TailHours?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
                new("mechanical/human", "Human mechanical hours", goal.MechanicalHours.Human.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
                new("mechanical/agent", "Agent mechanical hours", goal.MechanicalHours.Agent.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
                new("mechanical/other", "Other mechanical hours", goal.MechanicalHours.Other.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        }
        return new(SummaryFields(digest), rows);
    }

    internal static OwnerConsoleFieldSnapshot SummaryFields(Mcg.AgentOrchestrator.Core.OwnerDigestResult digest) => OwnerConsoleFieldSnapshot.Create("digest",
        new("since", "Since", digest.Since.ToString("O")), new("until", "Until", digest.Until.ToString("O")), new("reverts", "Reverts", digest.Reverts),
        new("landed", "Landed goals", digest.Totals.LandedGoals.ToString()), new("pending", "Pending", digest.Totals.Pending.ToString()),
        new("interventions", "Interventions", digest.Totals.Interventions.Total.ToString()), new("escapes", "Escapes", digest.Totals.Escapes.ToString()),
        new("correct", "Correct landings", digest.Totals.CorrectLandings.ToString()),
        new("rate", "Correct landing rate", digest.Totals.CorrectLandingRate?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        new("interventions/human", "Human interventions", digest.Totals.Interventions.Human.ToString()),
        new("interventions/agent", "Agent interventions", digest.Totals.Interventions.Agent.ToString()),
        new("interventions/other", "Other interventions", digest.Totals.Interventions.Other.ToString()),
        new("mean", "Mean interventions per landing", digest.Totals.MeanInterventionsPerLanding?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        new("tail/median", "Tail median hours", digest.Totals.TailMedianHours?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        new("tail/p90", "Tail p90 hours", digest.Totals.TailP90Hours?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        new("tail/known", "Known tail count", digest.Totals.KnownTailCount.ToString()), new("tail/unknown", "Unknown tail count", digest.Totals.UnknownTailCount.ToString()),
        new("mechanical/total", "Total mechanical hours", digest.Totals.MechanicalHours.Total.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        new("mechanical/human", "Human mechanical hours", digest.Totals.MechanicalHours.Human.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        new("mechanical/agent", "Agent mechanical hours", digest.Totals.MechanicalHours.Agent.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        new("mechanical/other", "Other mechanical hours", digest.Totals.MechanicalHours.Other.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        new("unresolved", "Unresolved hold hours", digest.Totals.UnresolvedHoldHours.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        new("non-landed", "Non-landed goals with interventions", digest.NonLandedGoalsWithInterventions.ToString()),
        new("none", "Latest decisions none", digest.LatestDecisions.None.ToString()));
}
