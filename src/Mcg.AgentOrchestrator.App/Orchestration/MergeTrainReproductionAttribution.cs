using System.Xml.Linq;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Shared focused TRX interpretation and compatibility reader for persisted reproduction receipts.
internal static class MergeTrainReproductionAttribution
{
    internal sealed record RunResult(IReadOnlyList<string> ExecutedTests, IReadOnlyList<AcceptanceTrxFailure> Failures,
        MergeTrainReproductionProvenance? Provenance = null);

    // The parent owns goal checkpoints. Keep the member's reproduction evidence with
    // its receipt-bound ejection so reconciliation does not lose names or messages.
    internal static IReadOnlyList<AcceptanceTrxFailure>? ReadEjectionFailures(MergeTrainReceipt receipt, string detail)
    {
        var prefix = $"Dropped after RED receipt {receipt.ReceiptId}.\nreproduction=";
        if (!detail.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var failures = JsonSerializer.Deserialize<AcceptanceTrxFailure[]>(detail[prefix.Length..]);
        var selected = MergeTrainRedAttribution.ReadFatalFailures(receipt.GateTestResultPaths)
            .Select(failure => failure.TestName).ToHashSet(StringComparer.Ordinal);
        return failures is not { Length: > 0 } || failures.Any(failure =>
            failure is null || string.IsNullOrWhiteSpace(failure.TestName) || !selected.Contains(failure.TestName) ||
            !AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome) ||
            ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is not null)
            ? null : failures;
    }

    internal static RunResult? ReadResults(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        var executed = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<AcceptanceTrxFailure>();
        foreach (var path in paths)
        {
            var read = AcceptanceTrxFailureReader.Read(path);
            if (read.Status != AcceptanceTrxReadStatus.Readable) return null;
            failures.AddRange(read.Failures);
            var document = XDocument.Load(path);
            var definitions = document.Descendants().Where(element => element.Name.LocalName == "UnitTest")
                .Where(element => element.Attribute("id") is not null)
                .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var result in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
            {
                var outcome = result.Attribute("outcome")?.Value.Trim() ?? "Unknown";
                if (!outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase) &&
                    !AcceptanceTrxOutcomeTaxonomy.IsFatal(outcome)) continue;
                definitions.TryGetValue(result.Attribute("testId")?.Value ?? "", out var definition);
                if (AcceptanceTrxTestIdentityResolver.Resolve(result, definition) is { } identity) executed.Add(identity);
            }
        }
        return new(executed.ToArray(), failures);
    }
}
