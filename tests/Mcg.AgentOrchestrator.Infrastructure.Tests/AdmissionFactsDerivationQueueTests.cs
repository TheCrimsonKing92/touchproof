using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: instance queue, private event gates, no process or global state.
public sealed class AdmissionFactsDerivationQueueTests
{
    [Fact]
    public void NewInputsWhileBlocked_CoalesceAndNeverPublishSupersededFacts()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var active = 0;
        var peak = 0;
        var calls = new List<AdmissionFactsInputs>();
        var first = AdmissionFactsTestData.Input(GoalId.New());
        var middle = first with { Branch = new string('c', 40) };
        var newest = first with { Branch = new string('d', 40) };
        var queue = new AdmissionFactsDerivation((inputs, _) =>
        {
            var concurrency = Interlocked.Increment(ref active);
            peak = Math.Max(peak, concurrency);
            calls.AddRange(inputs);
            if (inputs.Contains(first))
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(60)), "The blocked derivation was not released.");
            }
            Interlocked.Decrement(ref active);
            return inputs.Select(AdmissionFactsTestData.Fact).ToArray();
        });
        try
        {
            queue.Request(first);
            queue.Start();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(60)), "The first derivation did not enter.");
            queue.Request(middle);
            queue.Request(newest);
            queue.Request(newest);
            queue.Start();
            Assert.Equal(1, queue.PendingCount);
            Assert.Null(queue.Read(newest));
            release.Set();
            AdmissionFactsTestData.Wait(queue.Completion, "The newest derivation did not finish.");
            Assert.Equal(new[] { first, newest }, calls);
            Assert.Equal(1, peak);
            Assert.Null(queue.Read(first));
            Assert.Equal(newest, Assert.IsType<AdmissionFacts>(queue.Read(newest)).Inputs);
            Assert.Equal(0, queue.PendingCount);
        }
        finally { release.Set(); queue.Stop(); AdmissionFactsTestData.Wait(queue.Completion, "Queue cleanup did not finish."); }
    }

    [Fact]
    public void FailedBatch_RetriesOnlyAfterBackoffAndRetainRemovesDepartedGoals()
    {
        var now = DateTimeOffset.UtcNow;
        var calls = 0;
        var input = AdmissionFactsTestData.Input(GoalId.New());
        var queue = new AdmissionFactsDerivation((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("forced derivation failure");
        }, () => now);
        queue.Request(input);
        queue.Start();
        AdmissionFactsTestData.Wait(queue.Completion, "The failed batch did not finish.");
        queue.Start();
        AdmissionFactsTestData.Wait(queue.Completion, "The unchanged worker did not finish.");
        Assert.Equal(1, calls);
        Assert.Null(queue.Read(input));
        now = now.AddSeconds(31);
        queue.Start();
        AdmissionFactsTestData.Wait(queue.Completion, "The retry did not finish.");
        Assert.Equal(2, calls);
        queue.Retain(new HashSet<GoalId>());
        Assert.Equal(0, queue.PendingCount);
        queue.Stop();
    }
}
