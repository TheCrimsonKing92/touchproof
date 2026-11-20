using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable assembly inputs and one published architecture; no external state.
public sealed class CliReadRouteCapabilityArchitectureTests
{
    private static DependencyIndex Index => DependencyIndexCache.Get(
        typeof(CliReadOnlyCommandRunner).Assembly, typeof(IOrchestratorStateQueries).Assembly);

    [Xunit.Fact]
    public void ReadRoute_ContainsCommandSubjectsAndHasNoWriterDependencies()
    {
        var subjects = DeriveReadRouteSubjects(Index);
        foreach (var expected in new[]
        {
            typeof(CliReadOnlyCommandRunner), typeof(CliInspectionQueryCommand),
            typeof(CliReadinessQueryCommand), typeof(GoalBoardCommand), typeof(CliTaskQueryCommand)
        })
            Xunit.Assert.Contains(subjects, subject => subject.FullName == expected.FullName);

        var failures = ReadRouteRule(subjects);
        Xunit.Assert.True(failures.Length == 0, failures.ToErrorMessage());
    }

    [Xunit.Fact]
    public void WriterRunner_NegativeControlNamesSubjectAndTransactionalRepository()
    {
        var subject = Index.Types.Single(type => type.FullName == typeof(CliPersistentStateRunner).FullName);
        var failures = ReadRouteRule([subject]);

        Xunit.Assert.NotEmpty(failures);
        Xunit.Assert.Contains(failures, result =>
            result.Description.Contains(typeof(CliPersistentStateRunner).FullName!, StringComparison.Ordinal)
            && result.Description.Contains(typeof(ITransactionalOrchestratorStateRepository).FullName!,
                StringComparison.Ordinal));
    }

    private static DependencyDeclaration[] DeriveReadRouteSubjects(DependencyIndex index)
    {
        var runner = index.Types.Single(type => type.FullName == typeof(CliReadOnlyCommandRunner).FullName);
        var roots = runner.References
            .Where(type => type.Namespace == "Mcg.AgentOrchestrator.App.Cli")
            .Append(runner.Type).Select(type => type.FullName).ToHashSet(StringComparer.Ordinal);
        return index.Types.Where(type => roots.Contains(type.FullName)
            || roots.Any(root => type.FullName.StartsWith(root + "+", StringComparison.Ordinal)
                || type.FullName.StartsWith(root + "/", StringComparison.Ordinal))).ToArray();
    }

    private static DependencyViolation[] ReadRouteRule(DependencyDeclaration[] subjects)
    {
        var writerTypes = new[]
        {
            typeof(IOrchestratorStateRepository).FullName!,
            typeof(ITransactionalOrchestratorStateRepository).FullName!,
            typeof(IOrchestratorStateOutboxRepository).FullName!,
            typeof(SqliteOrchestratorStateRepository).FullName!
        }.ToHashSet(StringComparer.Ordinal);
        return DependencyIndexRules.WalkTypes(subjects, target => writerTypes.Contains(target.FullName), "writer types");
    }
}
