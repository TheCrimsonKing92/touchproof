namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class NoClassDurationHistory : IClassDurationHistory
{
    internal static NoClassDurationHistory Instance { get; } = new();
    private NoClassDurationHistory() { }
    public double? PredictSeconds(string projectQualifiedClass) => null;
}
