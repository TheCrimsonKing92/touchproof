namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class RemoteLaneAdmissionPolicy
{
    internal static bool Fits(int heldSlots, int slotCap, double reservedCost, double candidateCost, double? costBudget) =>
        heldSlots < slotCap && (costBudget is not { } budget ||
            (double.IsFinite(reservedCost) && reservedCost >= 0 &&
             double.IsFinite(candidateCost) && candidateCost > 0 && double.IsFinite(budget) && budget > 0 &&
             reservedCost + candidateCost <= budget));

    internal static string Classify(double cost) => cost <= RemoteLaneCostModel.LightCostCeiling ? "light" :
        cost >= RemoteLaneCostModel.HeavyCostFloor ? "heavy" : "medium";
}
