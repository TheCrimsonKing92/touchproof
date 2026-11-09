namespace Mcg.AgentOrchestrator.App.OwnerConsole;

[Flags]
internal enum OwnerConsoleRefreshTrigger
{
    Startup = 1,
    Periodic = 2,
    ConductorEvent = 4,
    ChangeStream = 8,
    OwnerCommand = 16
}
