using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Retains successful classifications for commit pairs for the lifetime of one driver.
internal sealed class ChangeRiskClassificationMemo
{
    private const int Capacity = 256;
    private readonly object _gate = new();
    private readonly Dictionary<(string IntegrationCommit, string GoalCommit), ChangeRiskTier> _entries = new();
    private readonly Queue<(string IntegrationCommit, string GoalCommit)> _insertionOrder = new();

    internal int Count { get { lock (_gate) return _entries.Count; } }

    internal ChangeRiskTier? Classify(string directory, string integrationBranch, string goalBranch)
    {
        string integrationCommit;
        string goalCommit;
        bool integrationResolved;
        bool goalResolved;
        try
        {
            integrationResolved = GitRevisionReader.Shared.TryResolve(directory, integrationBranch, out integrationCommit);
            goalResolved = GitRevisionReader.Shared.TryResolve(directory, goalBranch, out goalCommit);
        }
        catch
        {
            return Compute(directory, $"{integrationBranch}...{goalBranch}");
        }

        return integrationResolved && goalResolved
            ? GetOrAdd(integrationCommit, goalCommit, () => Compute(directory, $"{integrationCommit}...{goalCommit}"))
            : Compute(directory, $"{integrationBranch}...{goalBranch}");
    }

    internal ChangeRiskTier? GetOrAdd(string integrationCommit, string goalCommit, Func<ChangeRiskTier?> compute)
    {
        lock (_gate)
        {
            var key = (integrationCommit, goalCommit);
            if (_entries.TryGetValue(key, out var remembered)) return remembered;
            var result = compute();
            if (result is not { } tier) return null;
            if (_entries.Count == Capacity) _entries.Remove(_insertionOrder.Dequeue());
            _entries.Add(key, tier);
            _insertionOrder.Enqueue(key);
            return tier;
        }
    }

    private static ChangeRiskTier? Compute(string directory, string range)
    {
        try
        {
            var result = GitCli.Run(directory, "diff", "--name-only", range);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output)) return null;
            var files = result.Output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var summary = RepositoryChangeClassifier.Classify(files);
            var riskClass = LandingDecisionEngine.ClassifyRisk(summary);
            return (ChangeRiskTier)(int)riskClass;
        }
        catch
        {
            return null;
        }
    }
}
