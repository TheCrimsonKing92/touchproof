using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: instance-owned in-memory state only.
public sealed class BoundedInsertionOrderMemoTests
{
    [Fact]
    public void ManyDistinctKeys_HoldsCapacityAndEvictsOldest()
    {
        var memo = new BoundedInsertionOrderMemo<int, int>(16);
        for (var key = 0; key < 160; key++) memo.Store(key, key);
        Assert.Equal(16, memo.Capacity);
        Assert.Equal(16, memo.Count);
        for (var key = 0; key < 160; key++)
        {
            Assert.Equal(key >= 144, memo.TryGet(key, out var value));
            if (key >= 144) Assert.Equal(key, value);
        }
        Assert.Empty(typeof(BoundedInsertionOrderMemo<,>).GetFields(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
    }

    [Fact]
    public void ReadAndReplace_DoNotRefreshInsertionOrder()
    {
        var memo = new BoundedInsertionOrderMemo<string, int>(2, StringComparer.OrdinalIgnoreCase);
        memo.Store("oldest", 1);
        memo.Store("second", 2);
        Assert.True(memo.TryGet("OLDEST", out _));
        memo.Store("OLDEST", 3);
        Assert.True(memo.TryGet("oldest", out var replaced));
        Assert.Equal(3, replaced);
        memo.Store("third", 4);
        Assert.False(memo.TryGet("oldest", out _));
        Assert.True(memo.TryGet("second", out _));
        Assert.Equal(2, memo.Count);
    }

    [Fact]
    public void ConcurrentStoresAndReads_RemainBoundedAndConsistent()
    {
        var memo = new BoundedInsertionOrderMemo<int, int>(32);
        Parallel.For(0, 10_000, key =>
        {
            memo.Store(key, key);
            if (memo.TryGet(key, out var value)) Assert.Equal(key, value);
            Assert.InRange(memo.Count, 1, memo.Capacity);
        });
        Assert.Equal(32, memo.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCapacity_Throws(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedInsertionOrderMemo<int, int>(capacity));

    [Theory]
    [InlineData(40, 'a', true)]
    [InlineData(64, '0', true)]
    [InlineData(39, 'a', false)]
    [InlineData(41, 'a', false)]
    [InlineData(64, 'A', false)]
    [InlineData(40, 'g', false)]
    public void ObjectIds_RequireFullLowercaseHex(int length, char character, bool expected) =>
        Assert.Equal(expected, GoalWorktrees.IsFullObjectId(new string(character, length)));

    [Fact]
    public void CommitKeys_NormalizeDirectoryAndPreserveArgumentOrder()
    {
        var root = Path.GetTempPath();
        var key = GoalWorktrees.CommitFactKey(root, "a", "b");
        Assert.Equal(key, GoalWorktrees.CommitFactKey(Path.Combine(root, "."), "a", "b"));
        Assert.NotEqual(key, GoalWorktrees.CommitFactKey(root, "b", "a"));
        Assert.NotEqual(key, GoalWorktrees.CommitFactKey(Path.Combine(root, "other"), "a", "b"));
    }
}
