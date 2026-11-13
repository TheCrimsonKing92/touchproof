using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliStorageHealthQueryCommand
{
    internal static bool IsStorageHealthQueryCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("storage-health", StringComparison.OrdinalIgnoreCase) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        if (args.Skip(1).Any(arg => arg != "--json"))
        {
            Console.Error.WriteLine(CliCommandHelp.StorageHealthUsage);
            return;
        }
        var report = Build(workspace.OrchestratorDirectory, OrchestratorArtifactRegistry.Entries);
        var held = StorageHealthStatusView.Read(workspace.OrchestratorDirectory).HeldAcceptanceEvidence
            .OrderBy(row => row.Action, StringComparer.Ordinal).ThenBy(row => row.Reason, StringComparer.Ordinal).ToArray();
        if (args.Contains("--json"))
        {
            var output = new { report.Classes, report.Unregistered, report.Totals,
                held = held.Select(row => new { row.Action, row.Reason, count = row.DecisionCount, row.Bytes, row.Unmeasured }) };
            Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
            return;
        }
        foreach (var row in report.Classes)
            Console.WriteLine(FormattableString.Invariant($"class\t{Cell(row.Pattern)}\towner={Cell(row.Owner)}\twriter={row.Writer}\tretention={row.Retention}\tretention_age_days={row.RetentionAgeDays?.ToString(CultureInfo.InvariantCulture) ?? "-"}\tbytes={row.Bytes}\tfiles={row.Files}\tbudget_bytes={row.BudgetBytes}\tstatus={row.Status}"));
        foreach (var row in report.Unregistered)
            Console.WriteLine(FormattableString.Invariant($"unregistered\t{Cell(row.Name)}\tbytes={row.Bytes}\tfiles={row.Files}"));
        foreach (var row in held)
            Console.WriteLine(FormattableString.Invariant($"held\taction={Cell(row.Action)}\treason={Cell(row.Reason)}\tcount={row.DecisionCount}\tbytes={row.Bytes}") +
                (row.Unmeasured > 0 ? FormattableString.Invariant($"\tunmeasured={row.Unmeasured}") : ""));
        var totals = report.Totals;
        Console.WriteLine(FormattableString.Invariant($"totals\tbytes={totals.Bytes}\tfiles={totals.Files}\tclasses={totals.Classes}\tover={totals.Over}\tunregistered={totals.Unregistered}\tunreadable={totals.Unreadable}\telapsed_ms={totals.ElapsedMs}"));
    }

    internal static StorageHealthMeasurement.Report Build(string orchestratorDirectory,
        IReadOnlyList<OrchestratorArtifactRegistry.Entry> entries) =>
        StorageHealthMeasurement.Build(orchestratorDirectory, entries);

    private static string Cell(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
