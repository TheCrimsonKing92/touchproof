namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AttributedFailureDiagnosisNote
{
    internal static string Insert(string feedback, AttributedFailureDiagnosisOutcome outcome)
    {
        var start = feedback.IndexOf('\n') + 1;
        var end = feedback.IndexOf('\n', start);
        if (start == 0 || end < 0 || !feedback.AsSpan(start).StartsWith("failing_tests=", StringComparison.Ordinal))
            throw new InvalidOperationException("Attributed failure feedback has no failing_tests line.");
        var block = outcome.CauseFound
            ? new[] { "DIAGNOSIS (advice; the Developer must decide whether the code or test is wrong):" }
                .Concat(AttributedFailureDiagnosisOutcome.Lines(outcome.Text).Select(line => "| " + line))
            : ["DIAGNOSIS unavailable: " + AttributedFailureDiagnosisOutcome.SingleLine(outcome.Text)];
        // Preserve reproduction detail verbatim, including any mixed line endings it already carries.
        return feedback.Insert(end + 1, string.Join(Environment.NewLine, block) + Environment.NewLine);
    }
}
