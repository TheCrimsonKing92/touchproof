namespace Mcg.AgentOrchestrator.App.Orchestration;

// The backlog item exists; attention failed and needs operator repair without another insert or raise.
internal sealed class RecurringTestFixItemAttentionException(string itemId, Exception innerException)
    : Exception($"{itemId} (attention not raised: {innerException.GetType().Name}: {innerException.Message}; " +
        "owner: operator; next action: raise attention for this existing backlog item)", innerException)
{
    internal string ItemId { get; } = itemId;
}
