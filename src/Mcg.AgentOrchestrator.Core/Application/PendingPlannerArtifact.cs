using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>The exact plan accepted from a Planner dispatch, with its wait and answer provenance.</summary>
public sealed record PendingPlannerArtifact(
    string Plan,
    string Sha256,
    DateTimeOffset DispatchAttemptAt,
    int BriefVersion,
    IReadOnlyList<string>? RequestIds = null,
    string? AnsweredRequestId = null,
    string? AnswerId = null)
{
    public static string ComputeSha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static bool IsBlockingDirective(HumanInputDirective directive) =>
        directive.Kind != HumanWaitKind.PlannerBriefAmendment && HumanWaitPolicyDefaults.BlocksActiveWork(directive.Kind);

    public bool MatchesRound(TaskVerificationRecord verification) =>
        verification.Succeeded && DispatchAttemptAt == verification.DispatchAttemptAt &&
        !string.IsNullOrWhiteSpace(Plan) && string.Equals(Sha256, ComputeSha256(Plan), StringComparison.Ordinal);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPromoted => !string.IsNullOrWhiteSpace(AnswerId) && AnsweredRequestId is { } requestId &&
        RequestIds?.Contains(requestId, StringComparer.Ordinal) == true;

    internal void Bind(Goal goal, TaskSpec task, TaskVerificationRecord verification,
        IEnumerable<HumanInputRequest> requests)
    {
        if (task.RequiredRole != AgentRole.Planner || RequestIds is not null || AnswerId is not null || AnsweredRequestId is not null ||
            BriefVersion != goal.AuthoritativeBrief.Version || !MatchesRound(verification) ||
            DispatchAttemptAt != task.LastDispatch?.DispatchedAt)
            return;

        var requestIds = requests.Where(request => request.GoalId == goal.Id && request.TaskId == task.Id &&
                !request.IsCompleted && request.Kind != HumanWaitKind.PlannerBriefAmendment &&
                HumanWaitPolicyDefaults.BlocksActiveWork(request.Kind) && request.IsCurrentForBrief(BriefVersion))
            .Select(request => request.Id.Value).Order(StringComparer.Ordinal).ToArray();
        if (requestIds.Length > 0)
            task.RecordPlannerArtifact(verification, this with { RequestIds = requestIds });
    }

    internal PendingPlannerArtifact? Promote(Goal goal, TaskSpec task, TaskVerificationRecord verification,
        IEnumerable<HumanInputRequest> requests)
    {
        if (RequestIds is not { Count: > 0 } || AnswerId is not null || AnsweredRequestId is not null ||
            BriefVersion != goal.AuthoritativeBrief.Version ||
            DispatchAttemptAt != task.LastDispatch?.DispatchedAt || !MatchesRound(verification))
            return null;

        var answered = requests
            .Where(request => request.GoalId == goal.Id && request.TaskId == task.Id &&
                request.IsCurrentForBrief(BriefVersion) && RequestIds.Contains(request.Id.Value, StringComparer.Ordinal) &&
                request.SupersededByRequestId is null &&
                request.AuthoritativeAnswer is { Origin: HumanInputAnswerOrigin.Operator, IsAutoDefaulted: false } answer &&
                answer.BriefVersion == BriefVersion)
            .OrderByDescending(request => request.AuthoritativeAnswer!.AnsweredAt)
            .FirstOrDefault();
        return answered is null ? null : this with { AnsweredRequestId = answered.Id.Value, AnswerId = answered.AuthoritativeAnswer!.Id };
    }

    internal static PendingPlannerArtifact? Merge(PendingPlannerArtifact? prior, PendingPlannerArtifact? preferred) =>
        prior is { IsPromoted: true } && preferred is not { IsPromoted: true } ? prior : preferred ?? prior;
}
