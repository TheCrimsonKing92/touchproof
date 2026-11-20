using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class RecordControlReceiptCommand
{
    internal const string Name = "record-control-receipt";
    internal const string Usage = "Usage: record-control-receipt --goal <goal-prefix> --obligation <obligation-id> --kind <negative-control|series|shard> --command <command-text> --exit-status <int> --passed <count> --failed <count> --candidate-sha <sha> [--trx <path>]... [--json]";

    private static readonly string[] RequiredOptions =
        ["--goal", "--obligation", "--kind", "--command", "--exit-status", "--passed", "--failed", "--candidate-sha"];

    internal static bool IsCommand(IReadOnlyList<string> parts) =>
        parts.Count > 0 && parts[0].Equals(Name, StringComparison.OrdinalIgnoreCase);

    internal static void Execute(IReadOnlyList<string> parts, AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trxPaths = new List<string>();
        for (var index = 1; index < parts.Count; index++)
        {
            var option = parts[index];
            if (option.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                AddOption(option, string.Empty);
                continue;
            }

            var isTrx = option.Equals("--trx", StringComparison.OrdinalIgnoreCase);
            if (!isTrx && !RequiredOptions.Contains(option, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Unknown option '{option}'. {Usage}");
            if (++index >= parts.Count || string.IsNullOrWhiteSpace(parts[index]))
                throw new ArgumentException($"{option} requires a value. {Usage}");

            if (isTrx)
                trxPaths.Add(parts[index]);
            else
                AddOption(option, parts[index]);
        }

        foreach (var option in RequiredOptions)
        {
            if (!values.ContainsKey(option))
                throw new ArgumentException($"Provide {option}. {Usage}");
        }

        var selector = values["--goal"];
        if (!OperatorControlReceiptStore.IsSafeIdentifier(selector))
            throw new ArgumentException("--goal requires a goal id or unique prefix.");
        var goal = OrchestratorEntityResolver.ResolveGoal(kernel, currentGoal: null, selector);
        var receipt = new OperatorControlReceiptStore(workspace.OrchestratorDirectory).Record(
            goal.Id, values["--obligation"], values["--kind"], values["--command"], ParseInteger("--exit-status"),
            ParseInteger("--passed", nonNegative: true), ParseInteger("--failed", nonNegative: true),
            trxPaths, values["--candidate-sha"]);
        Console.WriteLine(receipt.ReceiptId);

        void AddOption(string option, string value)
        {
            if (!values.TryAdd(option, value))
                throw new ArgumentException($"Provide {option} only once. {Usage}");
        }

        int ParseInteger(string option, bool nonNegative = false)
        {
            if (!int.TryParse(values[option], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ||
                (nonNegative && value < 0))
                throw new ArgumentException($"{option} requires {(nonNegative ? "a non-negative" : "a signed")} integer. {Usage}");
            return value;
        }
    }
}
