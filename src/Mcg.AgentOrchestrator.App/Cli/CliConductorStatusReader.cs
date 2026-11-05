using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.OwnerConsole;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliConductorStatusReader
{
    internal static void Print(ConductorStatusSnapshot snapshot, TextWriter output)
    {
        var owner = snapshot.OwnerPid;
        output.WriteLine(owner is null ? $"Conductor: {snapshot.ConductorState}"
            : $"Conductor: {snapshot.ConductorState} (pid {owner})");
        output.WriteLine(snapshot.Since.Kind switch
        {
            "host-restart" => $"stopped since host restart at {snapshot.Since.At:O}",
            "stopped" => $"Stopped since {snapshot.Since.At:O}; reason={snapshot.Since.Reason}",
            "running" => $"Running since {snapshot.Since.At:O}",
            "starting" => $"Starting since {snapshot.Since.At:O}; stage={snapshot.Since.Reason}",
            _ => "Since: unavailable"
        });
        if (snapshot.Since.Previous is { } previous)
            output.WriteLine($"Previous stop: {previous.At:O}; reason={previous.Reason} (earlier generation)");
        output.WriteLine($"Supervisor build: {snapshot.SupervisorBuild ?? "unavailable"}");
        if (snapshot.SupervisorJob is { } job && ProcessJobTrapProbe.Traps(job))
            output.WriteLine($"Supervisor job: jobFlags={ProcessJobTrapProbe.FormatFlags(job.LimitFlags!.Value)} ends the conductor when the launching app closes; restart it from a terminal outside that job.");
        output.WriteLine(snapshot.LoopBuild is { Commit: { } commit } loop && !string.IsNullOrWhiteSpace(commit)
            ? $"Loop build: {commit} ({(loop.Source == "supervisor" ? "supervisor build; no activation since supervisor start" : loop.Source)})"
            : "Loop build: unavailable");
        output.WriteLine(snapshot.Generation.State switch
        {
            "no-build" => "Generation: unavailable (no supervisor build recorded)",
            "loop-unavailable" => "Generation: unavailable (loop build unreadable)",
            "main-unavailable" => "Generation: main unavailable",
            "current" => $"Generation: current with main (running {snapshot.Generation.Build}, main {snapshot.Generation.Main})",
            _ => $"Generation: differs from main (running {snapshot.Generation.Build}, main {snapshot.Generation.Main})"
        });
        if (snapshot.LateFleetAdoption is { } late)
            output.WriteLine($"Fleet adoption late: commit={late.Commit} elapsed={Math.Floor(late.Elapsed.TotalMinutes).ToString(CultureInfo.InvariantCulture)}m since ACTIVATION_ADOPTED with no FLEET_BUILD_ADOPTED; pending projects unavailable");
        output.WriteLine($"Latest tick: {snapshot.LatestTick?.ToString("O", CultureInfo.InvariantCulture) ?? "unavailable"}");
        if (!snapshot.Goals.Available)
        {
            output.WriteLine("Goals: unavailable (state database missing)");
            output.WriteLine("Pending clarifications: unavailable; human waits: unavailable");
        }
        else
        {
            output.WriteLine($"Non-terminal goals: {snapshot.Goals.NonTerminal!.Count}");
            foreach (var goal in snapshot.Goals.NonTerminal)
            {
                if (output is OwnerConsoleStructuredWriter structured) structured.Mark(OwnerConsoleFieldSnapshot.Create(goal.Id,
                    new("goal", "Goal ID", goal.Id), new("status", "Status", goal.Status), new("role", "Role", goal.Role)));
                output.WriteLine($"  {goal.Id} | {goal.Status} | role={goal.Role}");
                if (goal.LandingBlocker is { } blocker)
                {
                    if (output is OwnerConsoleStructuredWriter blocked) blocked.Mark(OwnerConsoleFieldSnapshot.Create(goal.Id,
                        new("goal", "Goal ID", goal.Id), new("kind", "Landing blocker kind", blocker.Kind.ToString()),
                        new("since", "Since", blocker.Since.ToString("O")), new("actor", "Next actor", blocker.NextActor.ToString()),
                        new("verb", "Next verb", blocker.NextVerb), new("detail", "Detail", blocker.Detail)));
                    output.WriteLine($"    landing-blocker: {blocker.Kind} since={blocker.Since.ToString("O", CultureInfo.InvariantCulture)} next={blocker.NextActor} verb={blocker.NextVerb} detail={blocker.Detail}");
                }
                foreach (var item in goal.Clarifications ?? [])
                {
                    if (output is OwnerConsoleStructuredWriter clarified) clarified.Mark(OwnerConsoleFieldSnapshot.Create(item.Id,
                        new("id", "Clarification ID", item.Id), new("hold", "Hold", item.Hold.ToString()),
                        new("raised", "Raised at", item.RaisedAt.ToString("O")), new("command", "Answer command", item.AnswerCommand)));
                    output.WriteLine($"    clarification: id={item.Id} hold={item.Hold} raised={item.RaisedAt.ToString("O", CultureInfo.InvariantCulture)} answer={item.AnswerCommand}");
                }
            }
            output.WriteLine(snapshot.Goals.PendingClarifications is null
                ? $"Pending clarifications: unavailable (collaboration items unreadable); human waits: {snapshot.Goals.HumanWaits}"
                : $"Pending clarifications: {snapshot.Goals.PendingClarifications}; human waits: {snapshot.Goals.HumanWaits}");
        }
        output.WriteLine(snapshot.MostRecentLanding is { } landing
            ? $"Most recent landing: {landing.Goal} at {landing.At:O}"
            : "Most recent landing: unavailable");
        output.WriteLine(snapshot.WorkerAdoption is { } adoption
            ? $"Worker adoption: {adoption.Summary} deferred-identity-unproven={adoption.DeferredIdentityUnproven}"
            : "Worker adoption: unavailable");
        var intents = snapshot.PendingIntents;
        output.WriteLine(intents.State switch
        {
            "none" => "Pending operator intents: none",
            "pending" => $"Pending operator intents: {intents.Goals.Count} goal(s)",
            _ => intents.Reason is null ? "Pending operator intents: unavailable"
                : $"Pending operator intents: unavailable ({intents.Reason})"
        });
        foreach (var goal in intents.Goals)
        {
            if (output is OwnerConsoleStructuredWriter structured) structured.Mark(OwnerConsoleFieldSnapshot.Create(goal.Goal,
                new("goal", "Goal ID", goal.Goal), new("count", "Intents", goal.Intents.ToString(CultureInfo.InvariantCulture)),
                new("latest", "Latest intent", goal.Latest?.ToString("O"))));
            var latest = goal.Latest?.ToString("O", CultureInfo.InvariantCulture) ?? "none";
            var hint = owner is null ? $" | apply with: conductor apply-intents {goal.Goal[..Math.Min(8, goal.Goal.Length)]}" : "";
            output.WriteLine($"  {goal.Goal} | intents={goal.Intents} | latest={latest}{hint}");
        }
        if (snapshot.ProviderHolds is { Count: > 0 } holds)
        {
            output.WriteLine($"Provider usage holds: {holds.Count}");
            foreach (var row in holds)
            {
                if (output is OwnerConsoleStructuredWriter structured) structured.Mark(OwnerConsoleFieldSnapshot.Create(row.Hold.BindingKey,
                    new("binding", "Binding", row.Hold.BindingKey), new("placed", "Placed at", row.Hold.PlacedAt.ToString("O")),
                    new("source-goal", "Source goal ID", row.Hold.SourceGoalId.Value), new("source-task", "Source task ID", row.Hold.SourceTaskId.Value),
                    new("next", "Next probe at", row.Schedule?.NextProbeAt.ToString("O")), new("attempt", "Attempt", row.Schedule?.Attempt.ToString(CultureInfo.InvariantCulture)),
                    new("reset", "Reset source", row.Schedule?.ResetSource), new("probe", "Last probe outcome", row.Schedule?.LastProbeOutcome)));
                output.WriteLine(ProviderUsageHoldView.FormatStatusLine(row));
            }
        }
    }
}
