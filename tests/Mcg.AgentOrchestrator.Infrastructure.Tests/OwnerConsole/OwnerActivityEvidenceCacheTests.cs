using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns its loader, receipt values and counting reader.
public sealed class OwnerActivityEvidenceCacheTests
{
    [Fact]
    public void Resolve_RepeatedRefreshes_ReadsEachSuccessfulReceiptOnce()
    {
        var reads = new Dictionary<string, int>();
        var loader = new OwnerActivityEvidenceLoader(item =>
        {
            var receipt = OwnerEventFields.Field(item, "receipt")!;
            reads[receipt] = reads.GetValueOrDefault(receipt) + 1;
            return new([receipt + ".FailedTest"], ["test-check"], true, "failure " + receipt);
        });
        OwnerConductEvent[] events = [Item("one"), Item("two")];
        var first = loader.Resolve(events);
        // A refreshed event timestamp is not part of the immutable receipt identity.
        var again = events.Select(item => item with { Timestamp = item.Timestamp.AddSeconds(1) }).ToArray();
        var second = loader.Resolve(again);
        Assert.Equal(2, reads.Count);
        Assert.All(reads.Values, count => Assert.Equal(1, count));
        for (var i = 0; i < events.Length; i++)
        {
            Assert.NotNull(first[events[i]]);
            Assert.Same(first[events[i]], second[again[i]]);
        }
    }

    [Fact]
    public void Resolve_MissingReceipt_RetriesUntilEvidenceAppears()
    {
        var reads = 0;
        var evidence = new OwnerActivityTestEvidence(["Late.Test"], [], FirstFailure: "late failure");
        var loader = new OwnerActivityEvidenceLoader(_ => ++reads == 1 ? null : evidence);
        var item = Item("late");
        Assert.Null(loader.Resolve([item])[item]);
        Assert.Same(evidence, loader.Resolve([item])[item]);
        Assert.Same(evidence, loader.Resolve([item])[item]);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Resolve_CapacityExceeded_EvictsOldestAndKeepsNewest()
    {
        var reads = new Dictionary<OwnerConductEvent, int>();
        var loader = new OwnerActivityEvidenceLoader(item =>
        {
            reads[item] = reads.GetValueOrDefault(item) + 1;
            return new(["Test"], []);
        });
        var events = Enumerable.Range(0, 513).Select(i => Item(i.ToString())).ToArray();
        Assert.Equal(513, loader.Resolve(events).Count);
        loader.Resolve([events[^1]]);
        Assert.Equal(1, reads[events[^1]]);
        loader.Resolve([events[0]]);
        Assert.Equal(2, reads[events[0]]);
    }

    [Fact]
    public void Resolve_ReceiptlessEvent_ReadsOnEveryRefresh()
    {
        var reads = 0;
        var loader = new OwnerActivityEvidenceLoader(_ => new(["Read" + ++reads], []));
        var item = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "acceptance", "goal", "result=failed");
        Assert.Equal("Read1", Assert.Single(loader.Resolve([item])[item]!.Tests));
        Assert.Equal("Read2", Assert.Single(loader.Resolve([item])[item]!.Tests));
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("event-kind")]
    [InlineData("identity")]
    [InlineData("attempt")]
    [InlineData("kind")]
    public void Resolve_SharedReceiptDifferentSource_DoesNotReuseEvidence(string field)
    {
        var reads = 0;
        var loader = new OwnerActivityEvidenceLoader(_ => new(["Read" + ++reads], []));
        var first = Item("shared", "identity=first attempt=first kind=cohort");
        var second = field switch
        {
            "goal" => first with { GoalId = "other" },
            "event-kind" => first with { EventKind = "canary-gate" },
            _ => first with { Detail = first.Detail.Replace(field + "=first", field + "=second") }
        };
        if (field == "kind") second = first with { Detail = first.Detail.Replace("kind=cohort", "kind=train") };
        Assert.NotEqual(first, second);
        loader.Resolve([first, second]);
        loader.Resolve([first, second]);
        Assert.Equal(2, reads);
    }

    private static OwnerConductEvent Item(string receipt, string extra = "") =>
        new(DateTimeOffset.UnixEpoch, "acceptance-cohort", "goal",
            $"outcome=failed result=failed receipt={receipt} {extra}");
}
