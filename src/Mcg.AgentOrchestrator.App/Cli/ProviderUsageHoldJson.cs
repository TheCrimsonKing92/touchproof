using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProviderUsageHoldJson
{
    internal static object Row(ProviderUsageHoldRow row)
    {
        var fields = new OrderedDictionary<string, object?>
        {
            ["bindingKey"] = row.Hold.BindingKey,
            ["provider"] = row.Hold.ProviderName,
            ["sourceGoalId"] = row.Hold.SourceGoalId.Value,
            ["sourceTaskId"] = row.Hold.SourceTaskId.Value,
            ["placedAt"] = ProviderBudgetHoldSchedules.FormatInstant(row.Hold.PlacedAt),
            ["evidence"] = row.Hold.EvidenceReceipt,
            ["scheduleAvailable"] = row.Schedule is not null,
            ["attempt"] = row.Schedule?.Attempt,
            ["nextProbeAt"] = row.Schedule is { } schedule ? ProviderBudgetHoldSchedules.FormatInstant(schedule.NextProbeAt) : null,
            ["resetSource"] = row.Schedule?.ResetSource,
            ["resetAt"] = row.Schedule?.ResetAt is { } reset ? ProviderBudgetHoldSchedules.FormatInstant(reset) : null,
            ["probeInFlight"] = row.Schedule?.ProbeInFlight,
            ["admittedGoalId"] = row.Schedule?.AdmittedGoalId?.Value,
            ["admittedTaskId"] = row.Schedule?.AdmittedTaskId?.Value,
            ["lastProbe"] = row.Schedule?.LastProbeOutcome is { } outcome && outcome != "none" ? outcome : null,
            ["resetCommand"] = $"provider-reset {row.Hold.BindingKey} <ISO-8601 instant> --text-file <path>",
            ["lastProbeAt"] = row.Schedule?.LastProbeAt,
            ["lastActionableFailure"] = row.Schedule?.LastActionableFailure.ToString(),
            ["lastActionableFailureAt"] = row.Schedule?.LastActionableFailureAt,
            ["diagnosis"] = row.Diagnosis,
            ["resetIntentId"] = row.Schedule?.ResetIntentId
        };

        if (row.HasAdditionalEvidence)
        {
            fields.Add("reason", row.Reason.ToString());
            fields.Add("scope", row.Hold.BindingScope);
            fields.Add("model", row.Hold.ModelName);
        }
        fields.Add("providerResetAt", ProviderUsageHoldView.FormatProviderReset(row.ProviderResetAt));

        return fields;
    }
}
