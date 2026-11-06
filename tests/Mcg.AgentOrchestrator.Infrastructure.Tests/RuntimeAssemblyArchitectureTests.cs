using ArchUnitNET.Domain;
using ArchUnitNET.Domain.Dependencies;
using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Conditions;
using ArchUnitNET.Fluent.Extensions;
using ArchUnitNET.Loader;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.RegularExpressions;
using Architecture = ArchUnitNET.Domain.Architecture;
using ReflectionAssembly = System.Reflection.Assembly;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

public sealed class RuntimeAssemblyArchitectureTests
{
    private static readonly ReflectionAssembly CoreAssembly = typeof(AgentOrchestratorKernel).Assembly;
    private static readonly ReflectionAssembly InfrastructureAssembly = typeof(GoalAcceptanceVerifier).Assembly;
    private static readonly ReflectionAssembly ExecutionAssembly = typeof(CohortAcceptanceStore).Assembly;
    private static readonly ReflectionAssembly ProvidersAssembly = typeof(ProviderSmokeTester).Assembly;
    private static readonly ReflectionAssembly OperatorCommsAssembly = typeof(NullOperatorChannel).Assembly;
    private static readonly ReflectionAssembly AppAssembly = typeof(SubscriptionPlanBuilder).Assembly;
    private static readonly ReflectionAssembly[] RuntimeAssemblies =
        [CoreAssembly, InfrastructureAssembly, ExecutionAssembly, ProvidersAssembly, OperatorCommsAssembly, AppAssembly];

    // Parallel-safe: immutable assembly inputs and one published, shared architecture; no external I/O seam.
    private static int architectureLoadCount;
    private static readonly Lazy<Architecture> SharedArchitecture = new(() =>
    {
        Interlocked.Increment(ref architectureLoadCount);
        return new ArchLoader().LoadAssemblies(RuntimeAssemblies).Build();
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record SanctionedCaller(string FullName, string Reason, string Source);

    private static readonly SanctionedCaller[] ProcessStartAllowlist =
    [
        new("Mcg.AgentOrchestrator.App.Cli.CliStableSlotDotnetCommand", "Stable-slot child for CLI verbs", "src/Mcg.AgentOrchestrator.App/Cli/CliStableSlotDotnetCommand.cs:225"),
        new("Mcg.AgentOrchestrator.Infrastructure.ProcessTreeGuiSuppression", "Shared launch-lock policy for managed child creation", "src/Mcg.AgentOrchestrator.Execution/Processes/ProcessTreeGuiSuppression.cs:177,195,198")
    ];

    // ProcessStartAllowlistDifferences: none. The empty-allowlist control must prove every entry.
    // If the IL model omits a method-group reference, record its type, source and cause here before removing it.

    [Fact]
    public void Architecture_loads_all_five_assemblies_once()
    {
        var architecture = SharedArchitecture.Value;
        Assert.Same(architecture, SharedArchitecture.Value);
        Assert.Equal(1, Volatile.Read(ref architectureLoadCount));
        string[] expected =
        [
            "Mcg.AgentOrchestrator.Core", "Mcg.AgentOrchestrator.Infrastructure",
            "Mcg.AgentOrchestrator.Execution",
            "Mcg.AgentOrchestrator.Infrastructure.Providers",
            "Mcg.AgentOrchestrator.Infrastructure.OperatorComms", "Mcg.AgentOrchestrator.App"
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal),
            RuntimeAssemblies.Select(assembly => assembly.GetName().Name!).Order(StringComparer.Ordinal));
        foreach (var assembly in RuntimeAssemblies)
        {
            Assert.True(architecture.Types.Any(type => type.Assembly.FullName == assembly.FullName),
                $"No architecture type loaded from {assembly.FullName}");
        }
    }

    [Fact]
    public void Core_has_no_outward_runtime_dependencies() => AssertPasses(LayeringRule(
        [CoreAssembly], [InfrastructureAssembly, ExecutionAssembly, ProvidersAssembly, OperatorCommsAssembly, AppAssembly]));

    [Fact]
    public void Core_layering_control_detects_App_dependency_on_Infrastructure() =>
        AssertNamesSubject(Failures(LayeringRule([AppAssembly], [InfrastructureAssembly])), AppAssembly);

    [Fact]
    public void Infrastructure_assemblies_do_not_depend_on_App() => AssertPasses(LayeringRule(
        [InfrastructureAssembly, ExecutionAssembly, ProvidersAssembly, OperatorCommsAssembly], [AppAssembly]));

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
        AssertNamesSubject(Failures(LayeringRule([InfrastructureAssembly], [CoreAssembly])), InfrastructureAssembly);

    [Fact]
    public void Core_is_isolated_from_process_network_and_SQLite_runtime() =>
        AssertPasses(RuntimeIsolationRule([CoreAssembly]));

    [Fact]
    public void Runtime_isolation_control_detects_each_forbidden_family()
    {
        var infrastructureFailures = Failures(RuntimeIsolationRule([InfrastructureAssembly]));
        AssertNames(infrastructureFailures, "Microsoft.Data.Sqlite.");
        AssertNamesExactType(infrastructureFailures, "System.Diagnostics.Process");
        AssertNamesExactType(infrastructureFailures, "System.Diagnostics.ProcessStartInfo");
        AssertNamesExactType(Failures(RuntimeIsolationRule([ProvidersAssembly])), "System.Net.Http.HttpClient");
    }

    [Fact]
    public void Process_Start_callers_are_sanctioned() =>
        AssertPasses(SanctionedProcessStartRule(ProcessStartAllowlist.Select(entry => entry.FullName)));

    [Fact]
    public void Process_Start_control_detects_shared_launcher_and_rejects_stale_allowlist_entries()
    {
        const string sharedLauncher = "Mcg.AgentOrchestrator.Infrastructure.ProcessTreeGuiSuppression";
        var missingSharedLauncher = Failures(SanctionedProcessStartRule(
            ProcessStartAllowlist.Select(entry => entry.FullName).Where(name => name != sharedLauncher)));
        AssertNames(missingSharedLauncher, sharedLauncher);
        AssertReportedCallers(missingSharedLauncher, [sharedLauncher]);

        var allCallers = Failures(SanctionedProcessStartRule([]));
        AssertReportedCallers(allCallers, ProcessStartAllowlist.Select(entry => entry.FullName));
    }

    [Fact]
    public void Runtime_types_have_no_dashboard_namespace() => AssertPasses(NamespaceAbsenceRule(
        ["Mcg.AgentOrchestrator.App.Dashboard", "Mcg.AgentOrchestrator.Dashboard"]));

    [Fact]
    public void Namespace_control_detects_Cli_types() =>
        AssertNames(Failures(NamespaceAbsenceRule(["Mcg.AgentOrchestrator.App.Cli"])),
            "Mcg.AgentOrchestrator.App.Cli.");

    private static ICanBeEvaluated LayeringRule(ReflectionAssembly[] subjects, ReflectionAssembly[] forbidden) =>
        Types().That().ResideInAssembly(subjects[0], subjects.Skip(1).ToArray()).Should()
            .NotDependOnAny(Types().That().ResideInAssembly(forbidden[0], forbidden.Skip(1).ToArray()));

    private static ICanBeEvaluated RuntimeIsolationRule(ReflectionAssembly[] subjects) =>
        Types().That().ResideInAssembly(subjects[0], subjects.Skip(1).ToArray()).Should()
            .FollowCustomCondition(type =>
            {
                var forbidden = type.Dependencies.Select(dependency => dependency.Target.FullName)
                    .Where(name => name is "System.Diagnostics.Process" or "System.Diagnostics.ProcessStartInfo"
                        || IsInNamespace(name, "System.Net") || IsInNamespace(name, "Microsoft.Data.Sqlite"))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                return new ConditionResult(type, forbidden.Length == 0,
                    $"{type.FullName} depends on forbidden runtime types: {string.Join(", ", forbidden)}");
            }, "be isolated from process, network and SQLite runtime types");

    private static ICanBeEvaluated SanctionedProcessStartRule(IEnumerable<string> allowlist)
    {
        var sanctioned = allowlist.ToHashSet(StringComparer.Ordinal);
        return Types().That().ResideInAssembly(RuntimeAssemblies[0], RuntimeAssemblies.Skip(1).ToArray()).Should()
            .FollowCustomCondition(type =>
            {
                var callsStart = type.Dependencies.OfType<MethodCallDependency>().Any(dependency =>
                    dependency.TargetMember.DeclaringType.FullName == "System.Diagnostics.Process"
                    && dependency.TargetMember.Name.StartsWith("Start(", StringComparison.Ordinal));
                return new ConditionResult(type, !callsStart || sanctioned.Contains(OutermostTypeName(type)),
                    $"{type.FullName} ({OutermostTypeName(type)}) calls System.Diagnostics.Process.Start without sanction");
            }, "call Process.Start only from sanctioned declaring types or their nested types");
    }

    private static ICanBeEvaluated NamespaceAbsenceRule(string[] forbiddenNamespaces) =>
        Types().That().ResideInAssembly(RuntimeAssemblies[0], RuntimeAssemblies.Skip(1).ToArray()).Should()
            .FollowCustomCondition(type => new ConditionResult(type,
                !forbiddenNamespaces.Any(name => IsInNamespace(type.Namespace.FullName, name)),
                $"{type.FullName} resides in forbidden namespace {type.Namespace.FullName}"),
                "reside outside forbidden namespaces and their descendants");

    private static bool IsInNamespace(string name, string forbidden) =>
        name.Equals(forbidden, StringComparison.Ordinal) || name.StartsWith(forbidden + ".", StringComparison.Ordinal);

    private static string OutermostTypeName(IType type) => type.FullName.Split('+', '/')[0];

    private static EvaluationResult[] Failures(ICanBeEvaluated rule) =>
        rule.Evaluate(SharedArchitecture.Value).Where(result => !result.Passed).ToArray();

    private static void AssertPasses(ICanBeEvaluated rule)
    {
        var failures = Failures(rule);
        Assert.True(failures.Length == 0, failures.ToErrorMessage());
    }

    private static void AssertNames(EvaluationResult[] failures, string expected) =>
        Assert.True(failures.Any(result => result.Description.Contains(expected, StringComparison.Ordinal)),
            $"Expected a failed description naming {expected}. {failures.ToErrorMessage()}");

    private static void AssertNamesExactType(EvaluationResult[] failures, string expected) =>
        Assert.True(failures.Any(result => Regex.IsMatch(result.Description,
                $@"(?<![\w.]){Regex.Escape(expected)}(?![\w.])", RegexOptions.CultureInvariant)),
            $"Expected a failed description naming the exact type {expected}. {failures.ToErrorMessage()}");

    private static void AssertNamesSubject(EvaluationResult[] failures, ReflectionAssembly subject) =>
        Assert.True(failures.Any(result => result.EvaluatedObject is IType type
            && type.Assembly.FullName == subject.FullName
            && result.Description.Contains(type.FullName, StringComparison.Ordinal)), failures.ToErrorMessage());

    private static void AssertReportedCallers(EvaluationResult[] failures, IEnumerable<string> expected)
    {
        Assert.True(failures.All(result => result.EvaluatedObject is IType), failures.ToErrorMessage());
        var reported = failures.Select(result => OutermostTypeName((IType)result.EvaluatedObject))
            .ToHashSet(StringComparer.Ordinal);
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        Assert.True(reported.SetEquals(expectedSet),
            $"Expected callers: {string.Join(", ", expectedSet.Order(StringComparer.Ordinal))}. "
            + $"Reported callers: {string.Join(", ", reported.Order(StringComparer.Ordinal))}. {failures.ToErrorMessage()}");
    }
}
