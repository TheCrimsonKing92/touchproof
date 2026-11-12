using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class BacklogRecurringTestFixItemStore(string path) : IRecurringTestFixItemStore
{
    private BacklogStore? _store;
    private BacklogStore Store => _store ??= new(path);
    public IReadOnlyList<BacklogItem> Find(string prefix) => Store.FindByIdPrefixAsync(prefix).GetAwaiter().GetResult();
    public bool Insert(BacklogItem item) => Store.UpsertAsync(item).GetAwaiter().GetResult();
    public void AppendNote(string id, string text) => Store.AppendNoteAsync(id, text).GetAwaiter().GetResult();
}
