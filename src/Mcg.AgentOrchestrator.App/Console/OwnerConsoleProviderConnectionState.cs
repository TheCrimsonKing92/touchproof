namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleProviderConnectionState(OwnerConsoleProviderConnection connection)
{
    internal string Id { get; } = connection.Report.ProfileId;
    internal OwnerConsoleProviderConnection Connection { get; set; } = connection;
    internal long CheckRevision { get; set; }
    internal long LastRefresh { get; set; }
    internal bool Checking { get; set; }
    internal string? InteractionText { get; set; }
    internal string? OperationText { get; set; }
}
