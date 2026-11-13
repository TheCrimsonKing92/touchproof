using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum PublicHistoryBuildOutcome { Passed, Failed, Skipped, Cancelled, Unknown }

/// <summary>One source CI observation; collection inputs are snapshotted at construction.</summary>
internal sealed record PublicHistoryBuild(string Repository, string StackLabel, string CommitSha,
    DateTimeOffset CommitTime, string BuildId, DateTimeOffset BuildTime, PublicHistoryBuildOutcome Outcome,
    IReadOnlyList<string> FailingTests, string SourceReference)
{
    // Preserve malformed null input for the builder's structured validation boundary.
    public IReadOnlyList<string> FailingTests { get; } = FailingTests is null
        ? null! : FailingTests.ToImmutableArray();
}

/// <summary>An eligible terminal build and the pending changes it resolves in its repository.</summary>
internal sealed record PublicHistoryChangeUnit
{
    public string Repository => TerminalBuild.Repository;
    public PublicHistoryBuild TerminalBuild { get; }
    public ImmutableArray<PublicHistoryBuild> RolledBuilds { get; }
    public ImmutableArray<string> Commits { get; }

    internal PublicHistoryChangeUnit(PublicHistoryBuild terminalBuild,
        IEnumerable<PublicHistoryBuild> rolledBuilds)
    {
        TerminalBuild = terminalBuild;
        RolledBuilds = rolledBuilds.ToImmutableArray();
        Commits = RolledBuilds.Select(b => b.CommitSha).Append(terminalBuild.CommitSha).ToImmutableArray();
    }
}
