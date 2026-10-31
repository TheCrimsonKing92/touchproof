using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class EnvironmentContractTestFixture
{
    internal static EnvironmentContract Contract => new(1, [new("cargo nextest", "cargo", ["nextest", "--version"])]);
    internal static ConductorChildEnvironment Environment(string path = "sentinel-path") => new(new Dictionary<string, string?> { ["PATH"] = path });
    internal static ProjectModel Model(ProjectStack stack = ProjectStack.Declared, IReadOnlyList<EnvironmentNeed>? needs = null) =>
        new(ProjectModel.CurrentSchemaVersion, ".", [], [], [],
            stack == ProjectStack.Declared ? new[] { DeclaredProjectDiscoveryAdapter.BuildQuestion,
                DeclaredProjectDiscoveryAdapter.TestQuestion, DeclaredProjectDiscoveryAdapter.ReportsQuestion }
                .Select(key => new ProjectOwnerQuestion(key, key, new FactSource(measurementReference: "test"))).ToArray() : [],
            [], needs ?? [], [], [], Stack: stack);
    internal static OwnerAnswer[] Answers(params (string Key, string Value)[] extra) => new[]
    {
        (OwnerAnswerValidator.DefaultTimeoutKey, "5"), (OwnerAnswerValidator.BuildServerShutdownKey, "1"),
        (OwnerAnswerValidator.DiscoveryKey, "1"), (DeclaredProjectDiscoveryAdapter.BuildQuestion, "cargo build"),
        (DeclaredProjectDiscoveryAdapter.TestQuestion, "cargo nextest run --profile ci"),
        (DeclaredProjectDiscoveryAdapter.ReportsQuestion, "reports/*.xml")
    }.Concat(extra).Select(pair => new OwnerAnswer(pair.Item1, pair.Item2, "test", DateTimeOffset.UnixEpoch)).ToArray();

    internal sealed class Runner : IEnvironmentProbeRunner
    {
        internal bool Pass { get; set; }
        internal int Calls { get; private set; }
        internal ConductorChildEnvironment? ObservedEnvironment { get; private set; }
        public Task<EnvironmentProbeResult> RunAsync(EnvironmentToolRequirement tool, ConductorChildEnvironment environment,
            string workingDirectory, CancellationToken cancellationToken = default)
        {
            Calls++;
            ObservedEnvironment = environment;
            return Task.FromResult(new EnvironmentProbeResult(tool, Pass, Pass ? 0 : 7, "missing nextest output tail", Pass ? "pass" : "failed"));
        }
    }
}
