using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

internal static class PlannerBriefAmendmentBriefEditor
{
    internal static string Apply(GoalBriefVersion brief, PlannerBriefAmendmentPayload amendment)
    {
        if (string.IsNullOrWhiteSpace(amendment.ProposedText))
            throw new PlannerBriefAmendmentAcceptRejectedException("The amendment has no stored proposed text.");
        if (brief.Version > amendment.RequestedBriefVersion)
            throw new PlannerBriefAmendmentAcceptRejectedException(
                $"The amendment targets brief v{amendment.RequestedBriefVersion}, but the current brief is v{brief.Version}.");

        var expected = AcceptanceCriteriaParser.ParseDeclared(brief.Text).ToArray();
        var index = amendment.CriterionIndex;
        if (index < 1 || index > expected.Length ||
            AcceptanceCriteriaParser.FindDeclaredSection(brief.Text) is not { } section)
            throw new PlannerBriefAmendmentAcceptRejectedException(
                $"Amendment criterion {index} is outside the brief's {expected.Length} declared criteria.");

        // Parse progressively to locate the criterion and its continuation lines
        // with the same rules used to count and read declared criteria.
        var parts = Regex.Split(brief.Text, @"(\r\n|[\r\n\f\u0085\u2028\u2029])");
        var prefix = new StringBuilder(section.Heading);
        string? previous = null;
        for (var line = section.LineNumber; line < section.LineNumber + section.Body.Length; line++)
        {
            var partIndex = 2 * line;
            prefix.Append('\n').Append(parts[partIndex]);
            var criteria = AcceptanceCriteriaParser.ParseDeclared(prefix.ToString());
            if (criteria.Count > index)
                break;
            if (criteria.Count < index || string.Equals(criteria[index - 1], previous, StringComparison.Ordinal))
                continue;

            var current = criteria[index - 1];
            if (previous is null)
            {
                var originalLine = parts[partIndex].TrimEnd();
                parts[partIndex] = originalLine[..^current.Length] + amendment.ProposedText;
            }
            else
            {
                parts[partIndex - 1] = string.Empty;
                parts[partIndex] = string.Empty;
            }
            previous = current;
        }

        var revised = string.Concat(parts);
        expected[index - 1] = amendment.ProposedText;
        if (!AcceptanceCriteriaParser.ParseDeclared(revised).SequenceEqual(expected, StringComparer.Ordinal))
            throw new PlannerBriefAmendmentAcceptRejectedException("The proposed text cannot be applied as a single declared criterion.");
        if (string.Equals(revised, brief.Text, StringComparison.Ordinal))
            throw new PlannerBriefAmendmentAcceptRejectedException("The proposed text leaves the brief unchanged.");
        return revised;
    }
}

internal static class PlannerBriefAmendmentAnswer
{
    internal static void ValidateDisposition(HumanInputRequest request, PlannerBriefAmendmentDisposition? disposition)
    {
        if (disposition is null) return;
        if (request.Amendment is null || request.Kind != HumanWaitKind.PlannerBriefAmendment)
            throw new ArgumentException("--amendment requires a request carrying a Planner brief amendment payload.");
        if (!PlannerBriefAmendmentDispositions.IsSupported(disposition))
            throw new ArgumentException("--amendment accepts only accept or decline.");
    }

    internal static void Apply(AgentOrchestratorKernel kernel, Goal goal, HumanInputRequest request,
        string note, HumanInputAnswerOrigin origin)
    {
        if (origin != HumanInputAnswerOrigin.Operator)
            throw new PlannerBriefAmendmentAcceptRejectedException("Accepting a Planner brief amendment requires an operator answer.");
        if (string.IsNullOrWhiteSpace(note))
            throw new PlannerBriefAmendmentAcceptRejectedException("Accepting a Planner brief amendment requires a note.");
        if (request.IsCompleted && kernel.GetPlannerBriefAmendmentRevisionPending(goal.Id)?.RequestId != request.Id)
            throw new PlannerBriefAmendmentAcceptRejectedException(
                "An answered amendment can only be accepted while it is waiting for a brief revision.");

        var revised = PlannerBriefAmendmentBriefEditor.Apply(goal.AuthoritativeBrief, request.Amendment!);
        try
        {
            kernel.ReviseGoalBrief(goal.Id, revised, note);
        }
        catch (Exception ex) when (ex is GoalBriefRevisionNotAllowedException or GoalBriefRevisionNoChangeException)
        {
            throw new PlannerBriefAmendmentAcceptRejectedException(ex.Message, ex);
        }
    }
}
