namespace Mcg.AgentOrchestrator.App.Orchestration;

// An advisory result, never authority to change acceptance criteria or routing policy.
internal sealed record AttributedFailureDiagnosisOutcome(string CandidateSha, string Signature, string Status, string Text)
{
    internal bool CauseFound => Status == "cause-found";
    internal static AttributedFailureDiagnosisOutcome Unavailable(AttributedFailureRouteDecision route, string status, string reason) =>
        new(route.Failure!.CandidateSha, route.Signature!, status, SingleLine(reason));

    internal static string SingleLine(string text) => Bound(string.Join(' ', Lines(text).Where(line => !string.IsNullOrWhiteSpace(line))), 200);
    internal static string Bound(string text, int limit = 4000) => (text.Length <= limit ? text : text[..limit]).TrimEnd();
    internal static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}
