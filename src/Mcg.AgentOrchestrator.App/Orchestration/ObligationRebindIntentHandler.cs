using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ObligationRebindIntentHandler
{
    internal static void ValidatePair(string? oldClass, string? newClass)
    {
        if (!IsValidClassName(oldClass) || !IsValidClassName(newClass) || oldClass == newClass)
            throw new ArgumentException("invalid-class-name");
        // The unchanged answer reader must consume exactly the mapping we authorize.
        var parsed = AnsweredSelectorRebinding.Parse($"{oldClass} becomes {newClass}");
        if (parsed.Count != 1 || parsed[0] != (oldClass, newClass))
            throw new ArgumentException("invalid-class-name");
    }

    private static bool IsValidClassName(string? name) => !string.IsNullOrEmpty(name) &&
        char.GetUnicodeCategory(name, 0) == UnicodeCategory.UppercaseLetter &&
        SyntaxFacts.IsValidIdentifier(name);

    internal static void Apply(AgentOrchestratorKernel kernel, Goal goal, OperatorIntentRecord intent,
        string orchestratorDirectory, string? candidateSha)
    {
        ObligationRebindOperatorIntentPayload? payload;
        try { payload = JsonSerializer.Deserialize<ObligationRebindOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options); }
        catch (JsonException) { throw new InvalidOperationException("invalid-payload"); }
        if (payload is null || string.IsNullOrWhiteSpace(payload.Reason))
            throw new InvalidOperationException("invalid-payload");
        ValidatePair(payload.OldClass, payload.NewClass);
        var current = GateTestObligationEvaluator.Evaluate(goal, orchestratorDirectory, candidateSha,
            answeredRequests: kernel.HumanInputRequests);
        if (!current.Any(result =>
                result.Outcome is not (GateTestObligationEvaluator.Outcome.Satisfied or
                    GateTestObligationEvaluator.Outcome.Waived or GateTestObligationEvaluator.Outcome.TraitExcluded) &&
                result.Obligation.Selectors.Any(selector => selector.Split('.').Contains(payload.OldClass, StringComparer.Ordinal))))
            throw new InvalidOperationException("no-matching-obligation");

        // Keep explanatory text outside the parsed answer: a reason may itself
        // contain rebind-like prose, but authorizes no additional mappings.
        kernel.RecordAnsweredOperatorHumanInput(goal.Id,
            $"Operator obligation-rebind intent {intent.Id}: {payload.Reason}",
            $"{payload.OldClass} becomes {payload.NewClass}");
    }
}
