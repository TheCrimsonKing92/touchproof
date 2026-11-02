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
        if (IsSelectorEdit(intent.PayloadJson))
        {
            ApplySelectorEdit(kernel, goal, intent, orchestratorDirectory, candidateSha);
            return;
        }
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

    internal sealed record SelectorEditPayload(string? Selector, string? To, bool? Drop, string? Reason);

    private static bool IsSelectorEdit(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.EnumerateObject()
                .Any(property => property.Name.Equals("selector", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("to", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("drop", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException) { throw new InvalidOperationException("invalid-payload"); }
    }

    private static void ApplySelectorEdit(AgentOrchestratorKernel kernel, Goal goal, OperatorIntentRecord intent,
        string orchestratorDirectory, string? candidateSha)
    {
        SelectorEditPayload? payload;
        try
        {
            using var document = JsonDocument.Parse(intent.PayloadJson);
            var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Contains("oldClass", StringComparer.OrdinalIgnoreCase) ||
                names.Contains("newClass", StringComparer.OrdinalIgnoreCase) ||
                names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
                throw new InvalidOperationException("invalid-payload");
            payload = JsonSerializer.Deserialize<SelectorEditPayload>(intent.PayloadJson, OperatorIntentJson.Options);
        }
        catch (JsonException) { throw new InvalidOperationException("invalid-payload"); }
        if (payload is null || string.IsNullOrWhiteSpace(payload.Selector) || string.IsNullOrWhiteSpace(payload.Reason) ||
            (payload.Drop is not null && payload.Drop != true) ||
            (payload.Drop == true ? payload.To is not null : string.IsNullOrWhiteSpace(payload.To)))
            throw new InvalidOperationException("invalid-payload");

        var matches = GateTestObligationEvaluator.Evaluate(goal, orchestratorDirectory, candidateSha,
                answeredRequests: kernel.HumanInputRequests)
            .Where(result => result.Outcome is not (GateTestObligationEvaluator.Outcome.Satisfied or
                GateTestObligationEvaluator.Outcome.Waived or GateTestObligationEvaluator.Outcome.TraitExcluded) &&
                result.Obligation.Selectors.Contains(payload.Selector, StringComparer.Ordinal)).ToArray();
        if (matches.Length == 0) throw new InvalidOperationException("no-matching-obligation");
        if (payload.Drop == true && matches.Any(result => result.Obligation.Selectors.Count <= 1))
            throw new InvalidOperationException("last-selector");

        var ids = matches.Select(result => result.Obligation.Id).ToArray();
        var answer = AnsweredSelectorRebinding.FormatSelectorEdit(payload.Selector, payload.To, ids);
        var parsed = AnsweredSelectorRebinding.ParseSelectorEdit(answer);
        if (AnsweredSelectorRebinding.Parse(answer).Count != 0 || parsed is null ||
            parsed.Selector != payload.Selector || parsed.Replacement != payload.To ||
            !parsed.ObligationIds.SequenceEqual(ids, StringComparer.Ordinal))
            throw new InvalidOperationException("invalid-payload");
        kernel.RecordAnsweredOperatorHumanInput(goal.Id,
            $"Operator obligation-rebind intent {intent.Id}: {payload.Reason}", answer);
    }
}
