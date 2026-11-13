namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ExperimentReplaySequentialStopRule
{
    internal const string Continue = "continue";
    internal const string StopDifference = "stop-difference";
    internal const string StopEquivalent = "stop-equivalent";

    internal sealed record Decision(string State, double Estimate, double? Lower, double? Upper, int Count);

    private readonly double _significanceLevel;
    private readonly double _mixingVariance;
    private readonly double _equivalenceMargin;
    private double _mean;
    private double _squaredDeviations;

    // Wang and Ramdas, arXiv:2310.03722v5, Theorem 4.9, with c^2 = 1 / mixingVariance.
    // The guarantee is exact for independent, identically distributed Gaussian differences.
    // Mixing variance is on the standardized (unitless) scale; the margin uses observation units.
    internal ExperimentReplaySequentialStopRule(double significanceLevel, double mixingVariance,
        double equivalenceMargin)
    {
        if (!double.IsFinite(significanceLevel) || significanceLevel <= 0 || significanceLevel >= 1)
            throw new ArgumentOutOfRangeException(nameof(significanceLevel));
        if (!double.IsFinite(mixingVariance) || mixingVariance <= 0)
            throw new ArgumentOutOfRangeException(nameof(mixingVariance));
        if (!double.IsFinite(equivalenceMargin) || equivalenceMargin <= 0)
            throw new ArgumentOutOfRangeException(nameof(equivalenceMargin));
        _significanceLevel = significanceLevel;
        _mixingVariance = mixingVariance;
        _equivalenceMargin = equivalenceMargin;
    }

    internal int Count { get; private set; }
    internal Decision? Current { get; private set; }

    internal Decision Observe(double difference)
    {
        if (!double.IsFinite(difference))
            throw new InvalidDataException("Sequential observations must be finite.");

        Count++;
        var delta = difference - _mean;
        _mean += delta / Count;
        _squaredDeviations = Math.Max(0, _squaredDeviations + delta * (difference - _mean));

        var g = 1 + Count * _mixingVariance;
        var r = Math.Exp((Math.Log(g) - 2 * Math.Log(_significanceLevel)) / Count);
        if (r >= g)
            return Current = new(Continue, _mean, null, null, Count);

        // This form cancels g before multiplication to avoid overflowing g * Q.
        var radius = Math.Sqrt((_squaredDeviations / Count) * ((r - 1) / (1 - r / g)));
        var lower = _mean - radius;
        var upper = _mean + radius;
        if (!double.IsFinite(lower) || !double.IsFinite(upper))
            return Current = new(Continue, _mean, null, null, Count);

        // Difference takes precedence when a small, nonzero effect also fits inside the margin.
        var state = lower > 0 || upper < 0 ? StopDifference
            : lower >= -_equivalenceMargin && upper <= _equivalenceMargin ? StopEquivalent : Continue;
        return Current = new(state, _mean, lower, upper, Count);
    }
}
