namespace Mcg.AgentOrchestrator.Core;

internal sealed record ProviderHoldBinding(string Provider, string? Credential, string? Model)
{
    private const string ModelSeparator = "|model=";

    internal SubscriptionLimitEvidence.ProviderBindingKey Base =>
        SubscriptionLimitEvidence.BuildProviderBindingKey(Provider, Credential);

    internal string Key => ScopedKey.RenderedKey;

    internal SubscriptionLimitEvidence.ProviderBindingKey ScopedKey => Base with { Model = Model?.ToLowerInvariant() };

    internal static string Render(string baseKey, string? model) =>
        model is null ? baseKey : baseKey + ModelSeparator + Uri.EscapeDataString(model);

    internal static SubscriptionLimitEvidence.ProviderBindingKey Identity(string key, string scope, string? model)
    {
        var normalizedModel = model?.ToLowerInvariant();
        var suffix = Render("", normalizedModel);
        var baseKey = key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? key[..(key.Length - suffix.Length)] : key;
        return new(baseKey.ToLowerInvariant(), scope, normalizedModel);
    }

    internal static ProviderHoldBinding From(TaskDispatchRecord dispatch, string? model = null) =>
        new(dispatch.ProviderName ?? dispatch.WorkerName, dispatch.ClaudeApiHoldIdentity ?? dispatch.ClaudeCredentialSourceDirectory, model);

    internal static TaskDispatchRecord? FindDispatch(TaskSpec task, TaskVerificationRecord verification)
    {
        var dispatch = SubscriptionLimitEvidence.FindDispatchForVerification(task, verification);
        return verification.DispatchAttemptAt is null || dispatch?.DispatchedAt == verification.DispatchAttemptAt
            ? dispatch : null;
    }

    internal sealed class DispatchIndex
    {
        private readonly Dictionary<DateTimeOffset, TaskDispatchRecord> _exact = [];
        private readonly List<TaskDispatchRecord> _completionFallback = [];

        internal int Visits { get; private set; }

        internal void Add(TaskDispatchRecord dispatch)
        {
            Visits++;
            _exact[dispatch.DispatchedAt] = dispatch;
            // A later stored dispatch with an earlier start wins every fallback
            // that could have selected the earlier stored dispatch.
            while (_completionFallback.Count > 0 &&
                _completionFallback[^1].DispatchedAt >= dispatch.DispatchedAt)
                _completionFallback.RemoveAt(_completionFallback.Count - 1);
            _completionFallback.Add(dispatch);
        }

        internal TaskDispatchRecord? MatchVerification(TaskVerificationRecord verification)
        {
            if (verification.DispatchAttemptAt is { } attempt) return Exact(attempt);
            if (verification.DispatchStartedAt is { } started && Exact(started) is { } dispatch)
                return dispatch;
            return LastStoredAtOrBefore(verification.CompletedAt);
        }

        private TaskDispatchRecord? Exact(DateTimeOffset at)
        {
            Visits++;
            return _exact.GetValueOrDefault(at);
        }

        internal TaskDispatchRecord? LastStoredAtOrBefore(DateTimeOffset at)
        {
            var low = 0;
            var high = _completionFallback.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                Visits++;
                if (_completionFallback[middle].DispatchedAt <= at) low = middle + 1;
                else high = middle;
            }
            return low == 0 ? null : _completionFallback[low - 1];
        }
    }

    internal static bool Matches(TaskDispatchRecord dispatch, string key, Func<string, bool>? isCurrentApiKeyIdentity = null)
    {
        bool MatchesRendered(ProviderHoldBinding binding) =>
            binding.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ||
            (binding with { Model = dispatch.ModelName }).Key.Equals(key, StringComparison.OrdinalIgnoreCase);

        var binding = From(dispatch);
        if (MatchesRendered(binding))
            return true;
        if (isCurrentApiKeyIdentity is null || dispatch.ClaudeApiHoldIdentity is not { } dispatchIdentity)
            return false;

        var modelSeparator = key.LastIndexOf(ModelSeparator, StringComparison.OrdinalIgnoreCase);
        var baseKey = modelSeparator < 0 ? key : key[..modelSeparator];
        var separator = baseKey.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0)
            return false;

        var credential = baseKey[(separator + 2)..];
        return MatchesRendered(binding with { Credential = credential }) &&
            isCurrentApiKeyIdentity(dispatchIdentity) && isCurrentApiKeyIdentity(credential);
    }

    internal static bool Matches(TaskDispatchRecord dispatch, SubscriptionLimitEvidence.ProviderBindingKey identity) =>
        From(dispatch, identity.Model is null ? null : dispatch.ModelName).ScopedKey == identity;

    internal static string BaseKey(string key)
    {
        var separator = key.LastIndexOf(ModelSeparator, StringComparison.Ordinal);
        return separator < 0 ? key : key[..separator];
    }

    internal static IReadOnlyCollection<ProviderHoldBinding> Candidates(IReadOnlyCollection<Goal> goals) =>
        ProviderHoldHistory.ExhaustedBindings(goals);
}
