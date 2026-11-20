using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.RegularExpressions;
using ReflectionAssembly = System.Reflection.Assembly;

public sealed class RuntimeAssemblyArchitectureTests
{
    private static readonly ReflectionAssembly CoreAssembly = typeof(AgentOrchestratorKernel).Assembly;
    private static readonly ReflectionAssembly InfrastructureAssembly = typeof(GoalAcceptanceVerifier).Assembly;
    private static readonly ReflectionAssembly ExecutionAssembly = typeof(CohortAcceptanceStore).Assembly;
    private static readonly ReflectionAssembly ProvidersAssembly = typeof(ProviderSmokeTester).Assembly;
    private static readonly ReflectionAssembly AppAssembly = typeof(SubscriptionPlanBuilder).Assembly;
    private static readonly ReflectionAssembly[] RuntimeAssemblies =
        [CoreAssembly, InfrastructureAssembly, ExecutionAssembly, ProvidersAssembly, AppAssembly];

    private static DependencyIndex Index => DependencyIndexCache.Get(RuntimeAssemblies);

    private sealed record SanctionedCaller(string FullName, string Reason, string Source);

    private static readonly SanctionedCaller[] ProcessStartAllowlist =
    [
        new("Mcg.AgentOrchestrator.Infrastructure.ProcessTreeGuiSuppression", "Shared launch-lock policy for managed child creation", "src/Mcg.AgentOrchestrator.Execution/Processes/ProcessTreeGuiSuppression.cs:177,195,198")
    ];

    // ProcessStartAllowlistDifferences: none. The empty-allowlist control must prove every entry.
    // If the IL model omits a method-group reference, record its type, source and cause here before removing it.

    [Fact]
    public void Architecture_loads_all_five_assemblies_once()
    {
        var architecture = Index;
        Assert.Same(architecture, Index);
        Assert.Equal(1, DependencyIndexCache.Process.BuildCount(RuntimeAssemblies));
        string[] expected =
        [
            "Mcg.AgentOrchestrator.Core", "Mcg.AgentOrchestrator.Infrastructure",
            "Mcg.AgentOrchestrator.Execution",
            "Mcg.AgentOrchestrator.Infrastructure.Providers",
            "Mcg.AgentOrchestrator.App"
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal),
            RuntimeAssemblies.Select(assembly => assembly.GetName().Name!).Order(StringComparer.Ordinal));
        foreach (var assembly in RuntimeAssemblies)
        {
            Assert.True(architecture.Types.Any(type => type.AssemblyName == assembly.GetName().Name),
                $"No architecture type loaded from {assembly.FullName}");
        }
    }

    [Fact]
    public void Core_has_no_outward_runtime_dependencies() => AssertPasses(LayeringRule(
        [CoreAssembly], [InfrastructureAssembly, ExecutionAssembly, ProvidersAssembly, AppAssembly]));

    [Fact]
    public void Core_layering_control_detects_App_dependency_on_Infrastructure() =>
        AssertNamesSubject(LayeringRule([AppAssembly], [InfrastructureAssembly]), AppAssembly);

    [Fact]
    public void Infrastructure_assemblies_do_not_depend_on_App() => AssertPasses(LayeringRule(
        [InfrastructureAssembly, ExecutionAssembly, ProvidersAssembly], [AppAssembly]));

    [Fact]
    public void Execution_types_do_not_depend_on_Infrastructure_or_App() => AssertPasses(LayeringRule(
        [ExecutionAssembly], [InfrastructureAssembly, AppAssembly]));

    [Fact]
    public void Execution_project_references_only_Core_and_Providers()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src",
            "Mcg.AgentOrchestrator.Execution", "Mcg.AgentOrchestrator.Execution.csproj"));
        var references = project.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Select(path => Path.GetFileName(path!.Replace('\\', '/')))
            .Order(StringComparer.Ordinal).ToArray();

        Assert.DoesNotContain("Mcg.AgentOrchestrator.Infrastructure.csproj", references);
        Assert.DoesNotContain("Mcg.AgentOrchestrator.App.csproj", references);
        Assert.Equal(new[]
        {
            "Mcg.AgentOrchestrator.Core.csproj",
            "Mcg.AgentOrchestrator.Infrastructure.Providers.csproj"
        }, references);
    }

    [Fact]
    public void Infrastructure_layering_control_detects_dependency_on_Core() =>
        AssertNamesSubject(LayeringRule([InfrastructureAssembly], [CoreAssembly]), InfrastructureAssembly);

    [Fact]
    public void Core_is_isolated_from_process_network_and_SQLite_runtime() =>
        AssertPasses(RuntimeIsolationRule([CoreAssembly]));

    [Fact]
    public void Runtime_isolation_control_detects_each_forbidden_family()
    {
        var infrastructureFailures = RuntimeIsolationRule([InfrastructureAssembly]);
        AssertNames(infrastructureFailures, "Microsoft.Data.Sqlite.");
        AssertNamesExactType(infrastructureFailures, "System.Diagnostics.Process");
        AssertNamesExactType(infrastructureFailures, "System.Diagnostics.ProcessStartInfo");
        AssertNamesExactType(RuntimeIsolationRule([ProvidersAssembly]), "System.Net.Http.HttpClient");
    }

    [Fact]
    public void Process_Start_callers_are_sanctioned() =>
        AssertPasses(SanctionedProcessStartRule(ProcessStartAllowlist.Select(entry => entry.FullName)));

    [Fact]
    public void Process_Start_control_detects_shared_launcher_and_rejects_stale_allowlist_entries()
    {
        const string sharedLauncher = "Mcg.AgentOrchestrator.Infrastructure.ProcessTreeGuiSuppression";
        var missingSharedLauncher = SanctionedProcessStartRule(
            ProcessStartAllowlist.Select(entry => entry.FullName).Where(name => name != sharedLauncher));
        AssertNames(missingSharedLauncher, sharedLauncher);
        AssertReportedCallers(missingSharedLauncher, [sharedLauncher]);

        var allCallers = SanctionedProcessStartRule([]);
        AssertReportedCallers(allCallers, ProcessStartAllowlist.Select(entry => entry.FullName));
    }

    [Fact]
    public void Runtime_types_have_no_dashboard_namespace() => AssertPasses(NamespaceAbsenceRule(
        ["Mcg.AgentOrchestrator.App.Dashboard", "Mcg.AgentOrchestrator.Dashboard"]));

    [Fact]
    public void Namespace_control_detects_Cli_types() =>
        AssertNames(NamespaceAbsenceRule(["Mcg.AgentOrchestrator.App.Cli"]),
            "Mcg.AgentOrchestrator.App.Cli.");

    private static DependencyViolation[] LayeringRule(ReflectionAssembly[] subjects, ReflectionAssembly[] forbidden)
    {
        var index = Index;
        var forbiddenNames = AssemblyNames(forbidden);
        return DependencyIndexRules.ForbiddenTypes(index, Subjects(index, subjects),
            target => forbiddenNames.Contains(target.AssemblyName));
    }

    private static DependencyViolation[] RuntimeIsolationRule(ReflectionAssembly[] subjects) =>
        DependencyIndexRules.WalkTypes(Subjects(Index, subjects),
            target => target.FullName is "System.Diagnostics.Process" or "System.Diagnostics.ProcessStartInfo"
                || IsInNamespace(target.FullName, "System.Net") || IsInNamespace(target.FullName, "Microsoft.Data.Sqlite"),
            "forbidden runtime types");

    private static DependencyViolation[] SanctionedProcessStartRule(IEnumerable<string> allowlist)
    {
        var sanctioned = allowlist.ToHashSet(StringComparer.Ordinal);
        return DependencyIndexRules.ForbiddenCalls(
            Subjects(Index, RuntimeAssemblies).Where(type => !sanctioned.Contains(type.OutermostName)),
            call => call.DeclaringType.FullName == "System.Diagnostics.Process" && call.Name == "Start",
            type => $"{type.FullName} ({type.OutermostName}) calls System.Diagnostics.Process.Start without sanction");
    }

    private static DependencyViolation[] NamespaceAbsenceRule(string[] forbiddenNamespaces) =>
        Subjects(Index, RuntimeAssemblies)
            .Where(type => forbiddenNamespaces.Any(name => IsInNamespace(type.Namespace, name)))
            .Select(type => new DependencyViolation(type, $"{type.FullName} resides in forbidden namespace {type.Namespace}"))
            .ToArray();

    private static bool IsInNamespace(string name, string forbidden) =>
        name.Equals(forbidden, StringComparison.Ordinal) || name.StartsWith(forbidden + ".", StringComparison.Ordinal);

    private static IEnumerable<DependencyDeclaration> Subjects(DependencyIndex index, ReflectionAssembly[] assemblies)
    {
        var names = AssemblyNames(assemblies);
        return index.Types.Where(type => names.Contains(type.AssemblyName));
    }

    private static HashSet<string> AssemblyNames(ReflectionAssembly[] assemblies) =>
        assemblies.Select(assembly => assembly.GetName().Name!).ToHashSet(StringComparer.Ordinal);

    private static void AssertPasses(DependencyViolation[] failures) =>
        Assert.True(failures.Length == 0, failures.ToErrorMessage());

    private static void AssertNames(DependencyViolation[] failures, string expected) =>
        Assert.True(failures.Any(result => result.Description.Contains(expected, StringComparison.Ordinal)),
            $"Expected a failed description naming {expected}. {failures.ToErrorMessage()}");

    private static void AssertNamesExactType(DependencyViolation[] failures, string expected) =>
        Assert.True(failures.Any(result => Regex.IsMatch(result.Description,
                $@"(?<![\w.]){Regex.Escape(expected)}(?![\w.])", RegexOptions.CultureInvariant)),
            $"Expected a failed description naming the exact type {expected}. {failures.ToErrorMessage()}");

    private static void AssertNamesSubject(DependencyViolation[] failures, ReflectionAssembly subject) =>
        Assert.True(failures.Any(result => result.Subject.AssemblyName == subject.GetName().Name
            && result.Description.Contains(result.Subject.FullName, StringComparison.Ordinal)), failures.ToErrorMessage());

    private static void AssertReportedCallers(DependencyViolation[] failures, IEnumerable<string> expected)
    {
        var reported = failures.Select(result => result.Subject.OutermostName)
            .ToHashSet(StringComparer.Ordinal);
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        Assert.True(reported.SetEquals(expectedSet),
            $"Expected callers: {string.Join(", ", expectedSet.Order(StringComparer.Ordinal))}. "
            + $"Reported callers: {string.Join(", ", reported.Order(StringComparer.Ordinal))}. {failures.ToErrorMessage()}");
    }
}
