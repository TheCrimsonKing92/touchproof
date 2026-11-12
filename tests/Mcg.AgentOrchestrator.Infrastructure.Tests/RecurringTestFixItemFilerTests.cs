using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static RecurringUnrelatedTestRuleTests;

// Parallel-safe: a local fake owns all backlog state.
public sealed class RecurringTestFixItemFilerTests
{
    internal sealed class FakeStore : IRecurringTestFixItemStore
    {
        internal readonly Dictionary<string, BacklogItem> Items = [];
        internal readonly List<string> Notes = [];
        internal bool FailWrites;
        public IReadOnlyList<BacklogItem> Find(string prefix) => Items.Values.Where(item => item.Id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        public bool Insert(BacklogItem item)
        {
            if (FailWrites) throw new IOException("fake backlog unavailable");
            return Items.TryAdd(item.Id, item);
        }
        public void AppendNote(string id, string text) => Notes.Add(text);
    }

    [Fact(DisplayName = "Three unrelated goals file one fix item and one attention item")]
    public void FilesExactlyOnceWhileOpen()
    {
        var store = new FakeStore();
        var attention = new List<string>();
        var filer = new RecurringTestFixItemFiler(store, (id, _) => attention.Add(id), _ => { });
        Assert.Null(filer.Ensure("a", Test, [], "sha-a", Now));
        Assert.Null(filer.Ensure("b", Test, [Occurrence("a")], "sha-b", Now));
        var id = filer.Ensure("c", Test, [Occurrence("a"), Occurrence("b")], "sha-c", Now, attemptId: "filing-attempt");
        var item = Assert.Single(store.Items.Values);
        Assert.Equal(id, Assert.Single(attention));
        foreach (var text in new[] { Key, "goal=a", "goal=b", "goal=c", "verifier lane", "drain was incomplete", "attempt-a", "filing-attempt" })
            Assert.Contains(text, item.Body, StringComparison.Ordinal);
        Assert.Equal(id, filer.Ensure("d", Test, History, "sha-d", Now));
        Assert.Equal(id, filer.Ensure("e", Test, History, "sha-e", Now));
        Assert.Single(store.Items);
        Assert.Single(attention);
        Assert.Equal(2, store.Notes.Count);
    }

    [Fact(DisplayName = "Inherited evidence across two goals files through the same deduplication key")]
    public void InheritedUsesSameOpenItem()
    {
        var store = new FakeStore();
        var count = 0;
        var filer = new RecurringTestFixItemFiler(store, (_, _) => count++, _ => { });
        var id = filer.Ensure("b", Test, [Occurrence("a")], "sha-b", Now, minimumGoals: 2);
        Assert.NotNull(id);
        Assert.Equal(id, filer.Ensure("c", Test, History, "sha-c", Now));
        Assert.Single(store.Items);
        Assert.Equal(1, count);
    }

    [Fact(DisplayName = "A closed fix generation supplies a watermark and resolves its attention")]
    public void ClosedGenerationResolvesAndCanBeRefiled()
    {
        var store = new FakeStore();
        var resolved = new List<string>();
        var filer = new RecurringTestFixItemFiler(store, (_, _) => { }, id => resolved.Add(id));
        var id = filer.Ensure("c", Test, History, "sha", Now)!;
        store.Items[id] = store.Items[id] with { Status = BacklogItemStatus.Done, UpdatedAt = Now.AddMinutes(1) };
        Assert.Equal(Now.AddMinutes(1), filer.ClosedAt(Key));
        Assert.Equal(id, Assert.Single(resolved));
        var next = filer.Ensure("d", Test, [Occurrence("a", Now.AddMinutes(2)), Occurrence("b", Now.AddMinutes(3))], "sha", Now.AddMinutes(4));
        Assert.NotEqual(id, next);
        Assert.Equal(2, store.Items.Count);
        Assert.Equal(Now.AddMinutes(1), filer.ClosedAt(Key));
    }

    [Fact(DisplayName = "An attention failure records the owned fix and operator repair without raising again")]
    public void AttentionFailureRetainsItem()
    {
        var store = new FakeStore();
        var attempts = 0;
        var filer = new RecurringTestFixItemFiler(store, (_, _) =>
        { attempts++; throw new IOException("fake attention unavailable"); }, _ => { });
        var failure = Assert.Throws<RecurringTestFixItemAttentionException>(() => filer.Ensure("c", Test, History, "sha", Now));
        var id = failure.ItemId;
        Assert.Equal(Assert.Single(store.Items.Values).Id, id);
        Assert.Equal("fake attention unavailable", Assert.IsType<IOException>(failure.InnerException).Message);
        var note = Assert.Single(store.Notes);
        Assert.Contains(id, note, StringComparison.Ordinal);
        Assert.Contains("attention not raised: IOException: fake attention unavailable", note, StringComparison.Ordinal);
        Assert.Contains("owner: operator; next action: raise attention for this existing backlog item", note, StringComparison.Ordinal);
        Assert.Equal(id, filer.Ensure("d", Test, History, "sha", Now));
        Assert.Equal(1, attempts);
        Assert.Single(store.Items);
    }
}
