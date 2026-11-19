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
