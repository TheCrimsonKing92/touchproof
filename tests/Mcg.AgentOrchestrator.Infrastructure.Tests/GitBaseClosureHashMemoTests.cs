using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns a private memory-only memo.
public sealed class GitBaseClosureHashMemoTests
{
    [Theory]
    [InlineData(40, 'a', true)]
    [InlineData(64, 'f', true)]
    [InlineData(39, 'a', false)]
    [InlineData(41, 'a', false)]
    [InlineData(40, 'A', false)]
    [InlineData(40, 'g', false)]
    public void OnlyFullLowercaseIdsAreEligible(int length, char digit, bool expected) =>
        Assert.Equal(expected, GitBaseClosureHashMemo.TryCreateKey(new string(digit, length), [], out _));

    [Theory]
    [InlineData("main")]
    [InlineData(" aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa ")]
    public void MutableOrPaddedRevisionsAreIneligible(string revision) =>
        Assert.False(GitBaseClosureHashMemo.TryCreateKey(revision, [], out _));

    [Fact]
    public void DigestSortsExactPathsAndFramesEveryEntry()
    {
        Assert.Equal(Key(["b", "a"]), Key(["a", "b"]));
        Assert.NotEqual(Key(["a"]), Key(["a", "a"]));
        Assert.NotEqual(Key(["ab", "c"]), Key(["a", "bc"]));
        Assert.NotEqual(Key([]), Key([""]));
        Assert.NotEqual(Key(["A"]), Key(["a"]));
        Assert.NotEqual(Key(["a/b"]), Key(["a\\b"]));
        Assert.NotEqual(Key(["a\0b"]), Key(["a", "b"]));
        Assert.False(GitBaseClosureHashMemo.TryCreateKey(new string('a', 40), ["\ud800"], out _));
    }

    [Fact]
    public void StoreReplacesAndEvictsLeastRecentlyUsedWithinCapacity()
    {
        var memo = new GitBaseClosureHashMemo(2);
        var first = Key(["first"]);
        var second = Key(["second"]);
        var third = Key(["third"]);
        Assert.False(memo.TryGet(first, out _));
        memo.Store(first, "one");
        memo.Store(first, "updated");
        Assert.Equal(1, memo.Count);
        memo.Store(second, "two");
        Assert.True(memo.TryGet(first, out var hash));
        Assert.Equal("updated", hash);
        memo.Store(third, "three");
        Assert.Equal(2, memo.Count);
        Assert.False(memo.TryGet(second, out _));
        Assert.True(memo.TryGet(first, out _));
        Assert.True(memo.TryGet(third, out _));
        Assert.Equal(3, memo.Hits);
        Assert.Equal(2, memo.Misses);
    }

    [Fact]
    public void ConcurrentStoresNeverExceedCapacity()
    {
        var memo = new GitBaseClosureHashMemo(8);
        Parallel.For(0, 256, index =>
        {
            var key = Key([index.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            memo.Store(key, "hash");
            memo.TryGet(key, out _);
            Assert.InRange(memo.Count, 1, 8);
        });
        Assert.Equal(8, memo.Count);
    }

    private static GitBaseClosureHashMemo.Key Key(string[] paths)
    {
        Assert.True(GitBaseClosureHashMemo.TryCreateKey(new string('a', 40), paths, out var key));
        return key;
    }
}
