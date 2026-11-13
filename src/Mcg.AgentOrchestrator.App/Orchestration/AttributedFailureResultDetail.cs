using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AttributedFailureResultDetail
{
    internal static IReadOnlyList<string> Lines(IReadOnlyList<string> resultPaths)
    {
        var lines = new List<string>();
        foreach (var path in resultPaths)
        {
            var result = AcceptanceTrxFailureReader.Read(path);
            if (result.Status != AcceptanceTrxReadStatus.Readable) continue;
            foreach (var failure in result.Failures.Where(failure => AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome)))
            {
                var name = (failure.TestName ?? "").Replace('\r', ' ').Replace('\n', ' ');
                var marker = ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is null
                    ? "attributed" : "apparatus";
                var frames = (failure.StackTrace ?? "")
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(line => line.StartsWith("at ", StringComparison.Ordinal)).Take(3);
                lines.Add($"gate_failure {name} [{marker}]: {MergeTrainAttributedFailure.BoundMessage(failure.Message)} | {string.Join(" ; ", frames)}");
            }
        }
        return lines;
    }
}
