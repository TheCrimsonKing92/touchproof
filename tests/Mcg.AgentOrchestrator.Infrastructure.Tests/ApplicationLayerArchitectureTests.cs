using Mcg.AgentOrchestrator.App.Cli;

public sealed class ApplicationLayerArchitectureTests
{
    private const string ApplicationNamespace = "Mcg.AgentOrchestrator.App.Application";
    private const string CliNamespace = "Mcg.AgentOrchestrator.App.Cli";
    private const string RenderingNamespace = "Mcg.AgentOrchestrator.App.Rendering";

    private static DependencyIndex Index => DependencyIndexCache.Get(typeof(GoalMonitoringQuery).Assembly);

    [Fact]
    public void Application_types_Presentation_boundary_Has_no_dependencies()
    {
        var index = Index;
        var applicationTypes = Subjects(index, ApplicationNamespace);

        Assert.Contains(applicationTypes, type => type.FullName == typeof(GoalMonitoringQuery).FullName);
        Assert.Contains(applicationTypes, type => type.FullName == typeof(AgentDefinitionFactory).FullName);
        Assert.Contains(applicationTypes, type => type.FullName == typeof(GoalDispatchOperations).FullName);
        Assert.All(applicationTypes, type => Assert.Equal(ApplicationNamespace, type.Namespace));

        var failures = NoDependencyRule(index, applicationTypes, CliNamespace, RenderingNamespace);
        Assert.True(failures.Length == 0, failures.ToErrorMessage());
    }

    [Fact]
    public void Cli_types_Application_dependency_Control_names_command_handlers()
    {
        var index = Index;
        var failures = NoDependencyRule(index, Subjects(index, CliNamespace), ApplicationNamespace);

        Assert.NotEmpty(failures);
        Assert.True(failures.Any(result => result.Subject.FullName == typeof(CliCommandHandlers).FullName
                && result.Description.Contains(nameof(CliCommandHandlers), StringComparison.Ordinal)),
            failures.ToErrorMessage());
    }

    internal static DependencyDeclaration[] Subjects(DependencyIndex index, string subjectNamespace) =>
        index.Types.Where(type => type.Namespace == subjectNamespace).ToArray();

    internal static DependencyViolation[] NoDependencyRule(
        DependencyIndex index, DependencyDeclaration[] subjects, params string[] forbiddenNamespaces) =>
        DependencyIndexRules.ForbiddenTypes(index, subjects,
            target => forbiddenNamespaces.Contains(target.Namespace, StringComparer.Ordinal));
}
