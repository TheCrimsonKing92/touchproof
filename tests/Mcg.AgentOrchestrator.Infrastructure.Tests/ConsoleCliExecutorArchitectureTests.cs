using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class ConsoleCliExecutorArchitectureTests
{
    private const string ConsoleNamespace = "Mcg.AgentOrchestrator.App.OwnerConsole";
    private const string CliNamespace = "Mcg.AgentOrchestrator.App.Cli";
    private static readonly string[] ExecutorNames =
    [
        typeof(CliPersistentStateRunner).FullName!,
        typeof(CliCommandDispatcher).FullName!,
        typeof(CliCommandHandlers).FullName!
    ];

    private static DependencyIndex Index => DependencyIndexCache.Get(typeof(AttentionAnswerHandlerAdapter).Assembly);

    [Fact]
    public void Console_types_Executor_boundary_Has_no_dependencies()
    {
        var index = Index;
        var consoleTypes = Subjects(index, ConsoleNamespace);

        Assert.Contains(consoleTypes, type => type.FullName == typeof(AttentionAnswerHandlerAdapter).FullName);
        Assert.Contains(consoleTypes, type => type.FullName == typeof(OwnerConsoleScreenController).FullName);
        Assert.Contains(consoleTypes, type => type.FullName == typeof(ExperimentDecideIntentSubmitter).FullName);
        Assert.All(consoleTypes, type => Assert.Equal(ConsoleNamespace, type.Namespace));

        var failures = NoExecutorDependencyRule(index, consoleTypes);
        Assert.True(failures.Length == 0, failures.ToErrorMessage());
    }

    [Fact]
    public void Cli_types_Executor_dependency_Control_names_persistent_runner()
    {
        var index = Index;
        var failures = NoExecutorDependencyRule(index, Subjects(index, CliNamespace));

        Assert.NotEmpty(failures);
        Assert.True(failures.Any(result => result.Subject.FullName == typeof(CliPersistentStateRunner).FullName),
            failures.ToErrorMessage());
    }

    private static DependencyDeclaration[] Subjects(DependencyIndex index, string subjectNamespace) =>
        index.Types.Where(type => type.Namespace == subjectNamespace).ToArray();

    private static DependencyViolation[] NoExecutorDependencyRule(DependencyIndex index, DependencyDeclaration[] subjects) =>
        DependencyIndexRules.ForbiddenTypes(index, subjects,
            target => ExecutorNames.Contains(target.OutermostName, StringComparer.Ordinal));
}
