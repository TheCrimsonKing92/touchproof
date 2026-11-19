namespace Mcg.AgentOrchestrator.Core;

/// <summary>Caches hold evaluations for one synchronous operation over an unchanged goal collection.</summary>
public sealed class ProviderBudgetHoldMemo
{
    internal delegate bool Evaluator(IEnumerable<Goal> goals, string providerName,
        string? credentialBinding, out ProviderBudgetExhaustionHold hold);

    private readonly IReadOnlyCollection<Goal> _goals;
    private readonly Evaluator _evaluate;
    private readonly Dictionary<SubscriptionLimitEvidence.ProviderBindingKey,
        (bool Found, ProviderBudgetExhaustionHold Hold)> _results = new();
    private readonly Dictionary<string, ProviderBudgetExhaustionHold?> _modelResults = new(StringComparer.OrdinalIgnoreCase);

    public ProviderBudgetHoldMemo(IReadOnlyCollection<Goal> goals)
        : this(goals, ProviderLimitSignals.TryGetProviderBudgetExhaustionHold)
    {
    }

    internal ProviderBudgetHoldMemo(IReadOnlyCollection<Goal> goals, Evaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(goals);
        ArgumentNullException.ThrowIfNull(evaluator);
        _goals = goals;
        _evaluate = evaluator;
    }

    public int EvaluationCount { get; private set; }

    internal bool TryGetExact(ProviderHoldBinding binding, out ProviderBudgetExhaustionHold hold)
    {
        if (binding.Model is null) return TryGet(binding.Provider, binding.Credential, out hold);
        if (!_modelResults.TryGetValue(binding.Key, out var found))
        {
            EvaluationCount++;
            ProviderLimitSignals.TryGetExactHold(_goals, binding, out hold);
            found = hold;
            _modelResults.Add(binding.Key, found);
        }
        hold = found!;
        return found is not null;
    }

    public bool TryGet(string providerName, string? credentialBinding, out ProviderBudgetExhaustionHold hold)
    {
        var key = SubscriptionLimitEvidence.BuildProviderBindingKey(providerName, credentialBinding);
        if (!_results.TryGetValue(key, out var result))
        {
            EvaluationCount++;
            var found = _evaluate(_goals, providerName, credentialBinding, out hold);
            result = (found, hold);
            _results.Add(key, result);
        }

        hold = result.Hold;
        return result.Found;
    }
}
