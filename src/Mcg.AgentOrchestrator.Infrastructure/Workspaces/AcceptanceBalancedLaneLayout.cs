namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceBalancedLaneLayout(int HistoryWindow, double DefaultClassSeconds,
    IReadOnlyList<AcceptanceBalancedLanePrediction> Lanes, string ResidualLaneName,
    IReadOnlyList<string> ResidualClasses, bool ResidualObserved = false)
{
    public int ResidualCount => ResidualClasses.Count;
    public string? HistoryReadFailure { get; init; }
    public string? HistoryWriteFailure { get; init; }
    public IReadOnlyList<AcceptanceBalancedLaneCommand> Commands { get; init; } = [];
}

internal sealed record AcceptanceBalancedLaneCommand(string Lane, int CommandLineCharacters, int Limit, bool UsesResponseFile);

internal sealed record AcceptanceBalancedLanePrediction(string Name, double PredictedSeconds,
    IReadOnlyList<string> Members);

internal sealed record AcceptanceBalancedLaneExpansion(IReadOnlyList<AcceptanceTestLane> Lanes,
    AcceptanceBalancedLaneLayout? Layout);
