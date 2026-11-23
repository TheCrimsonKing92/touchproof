namespace Mcg.AgentOrchestrator.Core;

internal sealed record ProviderHoldBinding(string Provider, string? Credential, string? Model)
{
    private const string ModelSeparator = "|model=";

    internal SubscriptionLimitEvidence.ProviderBindingKey Base =>
        SubscriptionLimitEvidence.BuildProviderBindingKey(Provider, Credential);

    internal string Key => Model is null ? Base.Key : Base.Key + ModelSeparator + Uri.EscapeDataString(Model.ToLowerInvariant());

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

    internal static bool Matches(TaskDispatchRecord dispatch, string key) =>
        From(dispatch).Key.Equals(key, StringComparison.OrdinalIgnoreCase) ||
        From(dispatch, dispatch.ModelName).Key.Equals(key, StringComparison.OrdinalIgnoreCase);

    internal static string BaseKey(string key)
    {
        var separator = key.LastIndexOf(ModelSeparator, StringComparison.Ordinal);
        return separator < 0 ? key : key[..separator];
    }

    internal static IReadOnlyCollection<ProviderHoldBinding> Candidates(IReadOnlyCollection<Goal> goals) =>
        ProviderHoldHistory.ExhaustedBindings(goals);
}
