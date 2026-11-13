using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record PreReviewEvidenceRelaunchProvenance(
    string PriorAttemptId,
    ConductorParallelAcceptanceAttemptOutcome PriorAttemptOutcome,
    string? PriorBranchHeadSha,
    bool SameBranchHead,
    bool SameFocusedEvidenceRequest,
    PreReviewEvidenceRelaunchTrigger Trigger,
    PreReviewEvidencePriorResultClass PriorResultClass)
{
    internal const string EventKind = "evidence-relaunch-provenance";

    internal string EventDetail(ConductorParallelAcceptanceAttempt attempt) =>
        $"EVIDENCE_RELAUNCH_PROVENANCE goal={attempt.GoalId} attempt={attempt.AttemptId} " +
        $"prior_attempt={PriorAttemptId} trigger={Trigger.ToWireValue()} " +
        $"same_head={SameBranchHead.ToString().ToLowerInvariant()} prior_result_class={PriorResultClass.ToWireValue()}";

    internal static PreReviewEvidencePriorResultClass Classify(
        ConductorParallelAcceptanceAttempt prior,
        FocusedEvidenceRunResult? evidence,
        bool terminalWithoutRun)
    {
        if (terminalWithoutRun)
            return PreReviewEvidencePriorResultClass.DidNotRun;
        if (evidence is null)
            return prior.Outcome == ConductorParallelAcceptanceAttemptOutcome.Passed
                ? PreReviewEvidencePriorResultClass.Passed : PreReviewEvidencePriorResultClass.Other;
        if (!evidence.Accepted)
            return PreReviewEvidencePriorResultClass.Other;
        if (evidence.Passed)
            return PreReviewEvidencePriorResultClass.Passed;
        if (evidence.OutcomeReason == FindingEvidenceOutcomeReason.ApparatusFailure)
            return PreReviewEvidencePriorResultClass.ApparatusFailure;
        if (evidence.Checks is null || evidence.Checks.Any(check => check is null || check.Name is null ||
                check.FailingTestIdentities?.Any(identity => identity is null) == true))
            return PreReviewEvidencePriorResultClass.Other;
        if (ConductorDriver.ExtractFailingTestIdentities(evidence.Checks).Count > 0)
            return PreReviewEvidencePriorResultClass.TestFailure;
        var failed = evidence.Checks.Where(check => !check.Passed).ToArray();
        return failed.Length > 0 && failed.All(check => ConductorDriver.IsBlockingTimeoutCheck(check.Name))
            ? PreReviewEvidencePriorResultClass.Timeout : PreReviewEvidencePriorResultClass.Other;
    }
}

[JsonConverter(typeof(PreReviewEvidenceRelaunchTriggerJsonConverter))]
internal enum PreReviewEvidenceRelaunchTrigger
{
    ReconciledFallthrough,
    RegateSupersession,
    TerminalWithoutRunReplaced,
    StaleReplaced,
    DidNotRunRetry
}

[JsonConverter(typeof(PreReviewEvidencePriorResultClassJsonConverter))]
internal enum PreReviewEvidencePriorResultClass
{
    Other,
    TestFailure,
    Timeout,
    ApparatusFailure,
    Passed,
    DidNotRun
}

internal static class PreReviewEvidenceProvenanceWireValues
{
    internal static string ToWireValue(this PreReviewEvidenceRelaunchTrigger value) => value switch
    {
        PreReviewEvidenceRelaunchTrigger.ReconciledFallthrough => "reconciled-fallthrough",
        PreReviewEvidenceRelaunchTrigger.RegateSupersession => "regate-supersession",
        PreReviewEvidenceRelaunchTrigger.TerminalWithoutRunReplaced => "terminal-without-run-replaced",
        PreReviewEvidenceRelaunchTrigger.StaleReplaced => "stale-replaced",
        PreReviewEvidenceRelaunchTrigger.DidNotRunRetry => "did-not-run-retry",
        _ => "reconciled-fallthrough"
    };

    internal static string ToWireValue(this PreReviewEvidencePriorResultClass value) => value switch
    {
        PreReviewEvidencePriorResultClass.TestFailure => "test-failure",
        PreReviewEvidencePriorResultClass.Timeout => "timeout",
        PreReviewEvidencePriorResultClass.ApparatusFailure => "apparatus-failure",
        PreReviewEvidencePriorResultClass.Passed => "passed",
        PreReviewEvidencePriorResultClass.DidNotRun => "did-not-run",
        _ => "other"
    };
}

internal sealed class PreReviewEvidenceRelaunchTriggerJsonConverter : JsonConverter<PreReviewEvidenceRelaunchTrigger>
{
    public override PreReviewEvidenceRelaunchTrigger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var label = reader.GetString();
            foreach (var value in Enum.GetValues<PreReviewEvidenceRelaunchTrigger>())
                if (string.Equals(value.ToWireValue(), label, StringComparison.OrdinalIgnoreCase))
                    return value;
        }
        else
            reader.Skip();
        return PreReviewEvidenceRelaunchTrigger.ReconciledFallthrough;
    }

    public override void Write(Utf8JsonWriter writer, PreReviewEvidenceRelaunchTrigger value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireValue());
}

internal sealed class PreReviewEvidencePriorResultClassJsonConverter : JsonConverter<PreReviewEvidencePriorResultClass>
{
    public override PreReviewEvidencePriorResultClass Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var label = reader.GetString();
            foreach (var value in Enum.GetValues<PreReviewEvidencePriorResultClass>())
                if (string.Equals(value.ToWireValue(), label, StringComparison.OrdinalIgnoreCase))
                    return value;
        }
        else
            reader.Skip();
        return PreReviewEvidencePriorResultClass.Other;
    }

    public override void Write(Utf8JsonWriter writer, PreReviewEvidencePriorResultClass value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireValue());
}
