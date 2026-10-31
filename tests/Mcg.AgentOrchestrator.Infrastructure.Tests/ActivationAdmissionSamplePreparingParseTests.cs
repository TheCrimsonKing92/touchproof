using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure parsing and formatting with no shared mutable state.
public sealed class ActivationAdmissionSamplePreparingParseTests
{
    [Xunit.Theory]
    [Xunit.InlineData("", 0, 0)]
    [Xunit.InlineData(" evidenceHeld=2", 2, 0)]
    [Xunit.InlineData(" preparing=1", 0, 1)]
    [Xunit.InlineData(" evidenceHeld=2 preparing=1", 2, 1)]
    [Xunit.InlineData(" evidenceHeld=2 preparing=0", 2, 0)]
    public void TryParse_HistoricalAndPreparingLines_PreservesSampleValues(
        string suffix, int evidenceHeld, int preparing)
    {
        var line = "ACTIVATION_ADMISSION_HEALTH tick=41 census=ok waiting=2 occupied=3 live=1" + suffix;

        Assert.True(ActivationAdmissionSample.TryParse(line, out var sample));
        Assert.Equal(new ActivationAdmissionSample(41, false, 2, 3, 1,
            evidenceHeld, preparing), sample);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    public void Format_PreparingCount_EmitsFieldAndRoundTrips(int preparing)
    {
        var sample = new ActivationAdmissionSample(42, true, 1, 2, 1, 0, preparing);

        Assert.Equal("ACTIVATION_ADMISSION_HEALTH tick=42 census=failed waiting=1 occupied=2 " +
            $"live=1 evidenceHeld=0 preparing={preparing}", sample.Format());
        Assert.True(ActivationAdmissionSample.TryParse(sample.Format(), out var parsed));
        Assert.Equal(sample, parsed);
    }
}
