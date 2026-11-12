using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: local samples and scoped activation accumulator only.
public sealed class ActivationAdmissionSampleOwnersParseTests
{
    [Fact]
    public void Format_Owners_RoundTripsAndPreservesLegacyBytes()
    {
        var sample = new ActivationAdmissionSample(42, false, 5, 1, 0, 0, 2, "cohort:a,train:b");
        Assert.True(ActivationAdmissionSample.TryParse(sample.Format(), out var parsed));
        Assert.Equal(sample, parsed);
        var legacy = sample with { PreparingOwners = "" };
        Assert.Equal("ACTIVATION_ADMISSION_HEALTH tick=42 census=ok waiting=5 occupied=1 live=0 evidenceHeld=0 preparing=2",
            legacy.Format());
        Assert.True(ActivationAdmissionSample.TryParse(legacy.Format(), out parsed));
        Assert.Equal(legacy, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("train:")]
    [InlineData("unknown:a")]
    [InlineData("train:a,")]
    [InlineData("train:a,train:a")]
    [InlineData("train:a:b")]
    [InlineData("train:a/b")]
    public void TryParse_MalformedOwners_RejectsLine(string owners) =>
        Assert.False(ActivationAdmissionSample.TryParse(
            new ActivationAdmissionSample(1, false, 5, 1, 0, 0, 1).Format() + " owners=" + owners, out _));

    [Fact]
    public void TryParse_UnsortedOwners_CanonicalizesIdentityOrder()
    {
        Assert.True(ActivationAdmissionSample.TryParse(
            new ActivationAdmissionSample(1, false, 5, 2, 0, 0, 2, "train:z,cohort:a").Format(), out var sample));
        Assert.Equal("cohort:a,train:z", sample.PreparingOwners);
    }

    [Fact]
    public void RecordCensus_LastCensusWinsAndTakeClearsOwners()
    {
        using var scope = ActivationAdmissionAccumulator.Begin(true);
        var accumulator = ActivationAdmissionAccumulator.Current.Value!;
        accumulator.RecordCensus(1, new(1, 1, "train:old"));
        accumulator.RecordCensus(1, new(1, 1, "cohort:new"));
        var sample = accumulator.Take(1);
        Assert.Equal(1, sample.PreparingAttempts);
        Assert.Equal("cohort:new", sample.PreparingOwners);
        var cleared = accumulator.Take(2);
        Assert.Equal(0, cleared.PreparingAttempts);
        Assert.Empty(cleared.PreparingOwners);
        accumulator.RecordCensus(1, new(1, 1, "train:old"));
        accumulator.RecordCensus(1, new(1, 0));
        Assert.Empty(accumulator.Take(3).PreparingOwners);
    }
}
