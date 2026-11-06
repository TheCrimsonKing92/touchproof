namespace Mcg.AgentOrchestrator.Infrastructure;

// A scheduling lower-bound estimate, not a calibrated forecast. A class/collection
// executes serially; run-alone collections cannot overlap any other collection.
internal sealed class AcceptanceLaneWallModel(int slotCount)
{
    internal sealed record Input(double Seconds, double? SlotHeldSeconds = null, int? SlotCount = null);
    internal sealed record Unit(double SerialSeconds, double? SlotHeldSeconds, bool RunsAlone);

    internal double Predict(IEnumerable<Unit> units)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 1);
        double alone = 0, longest = 0, held = 0;
        foreach (var unit in units)
        {
            if (!double.IsFinite(unit.SerialSeconds) || unit.SerialSeconds < 0 ||
                unit.SlotHeldSeconds is { } value && (!double.IsFinite(value) || value < 0))
                throw new ArgumentException("Wall model measurements must be finite and non-negative.", nameof(units));
            if (unit.RunsAlone) alone += unit.SerialSeconds;
            else
            {
                longest = Math.Max(longest, unit.SerialSeconds);
                // Legacy/missing telemetry assumes all serial work holds one slot.
                held += unit.SlotHeldSeconds ?? unit.SerialSeconds;
            }
        }
        return alone + Math.Max(longest, held / slotCount);
    }
}
