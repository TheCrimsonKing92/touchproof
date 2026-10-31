using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PassedGateHoldFaultIsolation
{
    internal static void Run(Goal goal, string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            // A failed read or write for one held goal must not stop other goals.
            // Report partial writes as failures too; the next tick reconciles durable state.
            try
            {
                Console.Error.WriteLine(
                    $"PASSED_GATE_HOLD_MAINTENANCE_FAILED goal={goal.Id.Value} operation={operation} " +
                    $"exception={exception.GetType().Name} message={exception.Message.Replace('\r', ' ').Replace('\n', ' ')}");
                Console.Error.Flush();
            }
            catch
            {
                // Console diagnostics are best effort during fault isolation.
            }
        }
    }
}
