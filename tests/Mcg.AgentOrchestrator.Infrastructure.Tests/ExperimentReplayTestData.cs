using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class ExperimentReplayTestData : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("experiment-replay-tests-");
    internal string Root => _directory.FullName;
    internal static readonly DateTimeOffset Now = new(2026, 11, 9, 23, 0, 0, TimeSpan.Zero);
    internal static readonly string Base = new('a', 40);
    internal static readonly string Candidate = new('b', 40);
    internal static readonly string Main = new('c', 40);
    internal static ExperimentReplayCase Case(int index = 0) => new($"case-{index:D3}", "home", "goal", $"task-{index}",
        Now.AddHours(-2), Now.AddHours(-1).AddSeconds(-index), Base, Candidate, Main, "replayable", [], [], [new("dispatch", "hash")]);
    internal static ExperimentReplayRegistration Registration() => new("registration", "home", Now, 42, "runtime", "toolchain", "config",
        Enumerable.Range(0, 20).Select(Case).ToArray(), new("value-bound", "hash"));
    internal static ExperimentReplayExecution Execution(ExperimentReplaySlot slot, long cpu = 100, string state = "passed") =>
        new($"execution-{slot.Ordinal}", "run", slot, state, null, "host", "runtime", "toolchain", "BelowNormal", "fixed", Now,
            new(cpu, 1024, 50, new Dictionary<string, int> { ["dotnet"] = 1 }, "job-object/live", true, 1),
            new(slot.Stage == "AA" ? "B" : slot.Arm, ["ExampleTests"], ["Example.Tests.csproj"], "resolved", []), [], []);
    internal ExperimentReplayStore Store()
    {
        var path = Path.Combine(Root, ExperimentReplayStore.DatabaseName);
        ExperimentReplayStore.Setup(path);
        return new(path);
    }
    public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
}
