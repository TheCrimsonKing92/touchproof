namespace Mcg.AgentOrchestrator.Infrastructure;

// A cost unit is one average CPU core during testing, excluding build and queue time.
internal sealed class RemoteLaneCostModel
{
    internal const double DefaultCost = 2.0;
    internal const double MinimumCost = 0.5;
    internal const double LightCostCeiling = 1.0;
    internal const double HeavyCostFloor = 1.5;
    private readonly Dictionary<(string Lane, string Executor), double[]> _samples;

    private RemoteLaneCostModel(Dictionary<(string Lane, string Executor), double[]> samples) => _samples = samples;

    internal static RemoteLaneCostModel Build(IEnumerable<RemoteExecutorHealthRecord> records) => new(records
        .Where(record => record.Outcome == RemoteLaneOutcomeCode.Accepted &&
            record.RemoteTestHostCpuSeconds is { } cpu && double.IsFinite(cpu) && cpu >= 0 &&
            record.Phases?.TestSeconds is { } wall && double.IsFinite(wall) && wall > 0)
        .Select(record => (record.Lane, record.ExecutorId,
            Ratio: record.RemoteTestHostCpuSeconds!.Value / record.Phases!.TestSeconds!.Value))
        .Where(sample => double.IsFinite(sample.Ratio))
        .GroupBy(sample => (sample.Lane, sample.ExecutorId))
        .ToDictionary(group => group.Key, group => group.Select(sample => sample.Ratio).Order().ToArray()));

    internal (double Cost, int Samples) Estimate(string? lane, string executorId)
    {
        if (lane is null || !_samples.TryGetValue((lane, executorId), out var values)) return (DefaultCost, 0);
        return (Math.Max(MinimumCost, Median(values)), values.Length);
    }

    internal static double Median(double[] sorted) => sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] :
        sorted[sorted.Length / 2 - 1] / 2 + sorted[sorted.Length / 2] / 2;
}
