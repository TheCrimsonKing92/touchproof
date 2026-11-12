using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AttributedFailureDiagnosisPrompt
{
    internal static string Build(Goal goal, AttributedFailure failure)
    {
        var detail = AttributedFailureDiagnosisOutcome.Lines(failure.ReproductionDetail ?? "");
        var tests = failure.FailingTests.Select(test =>
        {
            var line = detail.FirstOrDefault(line => line.StartsWith("reproduced " + test + ":", StringComparison.Ordinal) ||
                line.StartsWith("failed " + test + ":", StringComparison.Ordinal));
            var prefix = line?.StartsWith("reproduced ", StringComparison.Ordinal) == true ? "reproduced " : "failed ";
            return test + ": " + (line is null ? "absent" : line[(prefix.Length + test.Length + 1)..].Trim());
        });
        return string.Join(Environment.NewLine,
            "Act as a Reviewer. This is one read-only diagnosis round; inspect the candidate worktree and available diff. Do not write files.",
            "Candidate: " + failure.CandidateSha,
            "Goal: " + goal.Objective,
            "Acceptance criteria:", string.Join(Environment.NewLine, goal.RefinedSpec?.AcceptanceCriteria ?? []),
            "Failing tests and first message lines:", string.Join(Environment.NewLine, tests),
            "Reproduction detail:", failure.ReproductionDetail ?? "absent",
            "Identify the cause, candidate-relative file path and line, smallest fix, and what must stay unchanged.",
            "Your answer is advice; the Developer decides whether the code or test is wrong.",
            "Start the answer with DIAGNOSIS_STATUS: cause-found or DIAGNOSIS_STATUS: no-cause on its own line, then diagnostic text. Use no-cause if you cannot name a cause.");
    }

    internal static AttributedFailureDiagnosisOutcome Parse(AttributedFailureRouteDecision route, string? output)
    {
        var lines = AttributedFailureDiagnosisOutcome.Lines(output ?? "");
        var index = Array.FindLastIndex(lines, line => line.Trim() is "DIAGNOSIS_STATUS: cause-found" or "DIAGNOSIS_STATUS: no-cause");
        var text = string.Join(Environment.NewLine, lines.Skip(index + 1)).Trim();
        if (index < 0 || string.IsNullOrWhiteSpace(text))
            return AttributedFailureDiagnosisOutcome.Unavailable(route, "no-cause", "inquiry returned no usable diagnosis");
        if (lines[index].Trim() == "DIAGNOSIS_STATUS: no-cause")
            return AttributedFailureDiagnosisOutcome.Unavailable(route, "no-cause", "inquiry could not name a cause");
        return new(route.Failure!.CandidateSha, route.Signature!, "cause-found", AttributedFailureDiagnosisOutcome.Bound(text));
    }
}
