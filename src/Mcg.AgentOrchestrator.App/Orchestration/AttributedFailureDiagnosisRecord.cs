using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AttributedFailureDiagnosisRecord
{
    internal const string Marker = "ATTRIBUTED_FAILURE_DIAGNOSIS ";
    internal static string Format(AttributedFailureDiagnosisOutcome outcome) =>
        $"{Marker}candidate_sha={outcome.CandidateSha} signature={outcome.Signature} status={outcome.Status}" + Environment.NewLine +
        string.Join(Environment.NewLine, AttributedFailureDiagnosisOutcome.Lines(outcome.Text).Select(line => "| " + line));

    internal static AttributedFailureDiagnosisOutcome? Read(Goal goal, string candidate, string signature)
    {
        foreach (var evt in goal.Timeline)
        {
            if (!evt.Message.StartsWith(Marker, StringComparison.Ordinal)) continue;
            var lines = AttributedFailureDiagnosisOutcome.Lines(evt.Message);
            var tokens = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!tokens.Contains("candidate_sha=" + candidate, StringComparer.Ordinal) ||
                !tokens.Contains("signature=" + signature, StringComparer.Ordinal)) continue;
            var status = tokens.FirstOrDefault(token => token.StartsWith("status=", StringComparison.Ordinal))?[7..];
            if (status is not ("cause-found" or "no-cause" or "failed" or "timed-out") ||
                lines.Skip(1).Any(line => !line.StartsWith("| ", StringComparison.Ordinal)))
                throw new InvalidOperationException("Malformed attributed failure diagnosis record.");
            return new(candidate, signature, status, string.Join(Environment.NewLine, lines.Skip(1).Select(line => line[2..])));
        }
        return null;
    }
}
