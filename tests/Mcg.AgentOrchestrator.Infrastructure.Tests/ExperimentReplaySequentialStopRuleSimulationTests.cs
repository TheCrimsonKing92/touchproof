using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: simulation uses a private seeded generator and private in-memory rules.
public sealed class ExperimentReplaySequentialStopRuleSimulationTests
{
    [Fact]
    public void Observe_EveryGaussianLook_ControlsFalseStopsAndDetectsShift()
    {
        const int sequences = 2000;
        const int observations = 200;
        const double significanceLevel = 0.05;
        const double noiseScale = 250;
        // Approximately three binomial standard errors at alpha = 0.05 and 2000 sequences.
        const double simulationTolerance = 0.015;
        var random = new Random(42);
        var nullStops = 0;
        var shiftedStops = 0;
        var nullReads = 0;
        var shiftedReads = 0;

        for (var sequence = 0; sequence < sequences; sequence++)
        {
            var nullRule = new ExperimentReplaySequentialStopRule(significanceLevel, 1, 10);
            var shiftedRule = new ExperimentReplaySequentialStopRule(significanceLevel, 1, 10);
            var everNullDifference = false;
            var everShiftedDifference = false;
            for (var observation = 0; observation < observations; observation++)
            {
                var noise = noiseScale * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble()))
                    * Math.Cos(2 * Math.PI * random.NextDouble());
                var nullDecision = nullRule.Observe(noise);
                var shiftedDecision = shiftedRule.Observe(noise + noiseScale);
                nullReads++;
                shiftedReads++;
                // Read every observation, including after either kind of stop decision.
                everNullDifference |= nullDecision.State == "stop-difference";
                everShiftedDifference |= shiftedDecision.State == "stop-difference";
            }
            if (everNullDifference) nullStops++;
            if (everShiftedDifference) shiftedStops++;
        }

        var falseStopFraction = nullStops / (double)sequences;
        var shiftedStopFraction = shiftedStops / (double)sequences;
        Assert.True(falseStopFraction <= significanceLevel + simulationTolerance,
            $"Ever false-stop fraction {falseStopFraction} exceeded {significanceLevel + simulationTolerance}.");
        // An implementation that always continues cannot pass the power arm.
        Assert.True(shiftedStopFraction >= 0.95, $"Shifted-mean detection fraction was {shiftedStopFraction}.");
        Assert.Equal(400000, nullReads);
        Assert.Equal(400000, shiftedReads);
    }
}
