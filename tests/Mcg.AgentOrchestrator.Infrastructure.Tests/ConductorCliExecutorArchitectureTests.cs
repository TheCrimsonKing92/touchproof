using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorCliExecutorArchitectureTests
{
    private const string OrchestrationNamespace = "Mcg.AgentOrchestrator.App.Orchestration";
    private const string ExemptRootType = "Mcg.AgentOrchestrator.App.Orchestration.BoardFillCliGoalIntake";

    private static DependencyIndex Index => DependencyIndexCache.Get(typeof(BoardFillCliGoalIntake).Assembly);

    [Fact]
    public void Orchestration_types_Only_exempt_intake_depends_on_executors()
    {
        var index = Index;
        var orchestrationTypes = Subjects(index, [ExemptRootType]);

        Assert.Contains(orchestrationTypes, type => type.FullName == typeof(ConductorContinuitySupervisor).FullName);
        Assert.Contains(orchestrationTypes, type => type.FullName == typeof(PendingStoreSetupGate).FullName);
        Assert.Contains(orchestrationTypes, type => type.FullName == typeof(WorkspaceStoreSetup).FullName);
        Assert.All(orchestrationTypes, type => Assert.Equal(OrchestrationNamespace, type.Namespace));

        var failures = ExecutorRule(index, orchestrationTypes);
        Assert.True(failures.Length == 0, failures.ToErrorMessage());
    }

    [Fact]
    public void Empty_exemptions_Control_names_exactly_board_fill_intake()
    {
        var index = Index;
        var failures = ExecutorRule(index, Subjects(index, []));

        Assert.NotEmpty(failures);
        var failingSubjects = failures
            .Select(result => result.Subject)
            .Where(type => type.FullName == ExemptRootType || type.OutermostName != ExemptRootType)
            .Select(type => type.FullName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(failingSubjects.SetEquals([ExemptRootType]), failures.ToErrorMessage());
    }

    private static DependencyDeclaration[] Subjects(DependencyIndex index, IReadOnlyCollection<string> exemptRoots) =>
        index.Types.Where(type => type.Namespace == OrchestrationNamespace
            && !exemptRoots.Contains(type.OutermostName, StringComparer.Ordinal)).ToArray();

    private static DependencyViolation[] ExecutorRule(DependencyIndex index, DependencyDeclaration[] subjects)
    {
        string[] executorRoots =
        [
            typeof(CliPersistentStateRunner).FullName!,
            typeof(CliCommandDispatcher).FullName!,
            typeof(CliCommandHandlers).FullName!
        ];
        return DependencyIndexRules.ForbiddenTypes(index, subjects,
            target => executorRoots.Contains(target.OutermostName, StringComparer.Ordinal));
    }
}
