using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

// Session-wide build-server shutdown must remain suppressed throughout this test host,
// including independent threads whose ExecutionContext does not inherit a scoped override.
// Explicit shutdown scopes take precedence; disposing them restores this safe default.
internal static class AssemblyBuildServerShutdownIsolation
{
    [ModuleInitializer]
    internal static void Install()
    {
        AppContext.SetSwitch(DotnetBuildServerRecovery.SuppressShutdownSwitchName, true);
    }
}
