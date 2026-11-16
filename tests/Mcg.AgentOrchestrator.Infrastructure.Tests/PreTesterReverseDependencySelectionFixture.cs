using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class PreTesterReverseDependencySelectionFixture : IDisposable
{
    internal const string Source = "src/Mcg.AgentOrchestrator.App/Orchestration/AppContract.cs";
    internal const string Project = PreTesterCoupledClassSelectionsTests.Fixture.Project;
    internal const string TestRoot = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";
    private readonly PreTesterCoupledClassSelectionsTests.Fixture _files = new();
    internal string Root => _files.Root;

    internal PreTesterReverseDependencySelectionFixture()
    {
        Write("src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj", "<Project />");
        Write(Project, """
            <Project><ItemGroup>
            <ProjectReference Include="../../src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj" />
            <ProjectReference Include="../../src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj" />
            </ItemGroup></Project>
            """);
        Write(Source, "public class AppContract {}");
        Test("ConsumerTests", "AppContract");
    }

    internal void Write(string path, string text) => _files.Write(path, text);
    internal void Test(string name, string? dependency = null) => _files.Test(name, dependency);

    internal void WithCostHistory(string engine = "")
    {
        Write("tests/Mcg.AgentOrchestrator.Core.Tests/CoreTests.cs",
            "public class CoreTests { [Xunit.Fact] public void Works() {} }");
        Write("config/acceptance-manifest.json", $$"""
            { "engine": { "infrastructureTestLanes": [
              { "name": "fixture", "filter": "FullyQualifiedName~Tests" }
            ] {{engine}} }, "checks": [
              { "name": "infra", "type": "dotnet-test", "project": "{{Project}}" },
              { "name": "core", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj" }
            ] }
            """);
        var completed = DateTimeOffset.UtcNow.AddMinutes(-1);
        var metadata = new ConductorParallelAcceptanceAttempt("fixture-red", "goal", "goal", 0, null, null,
            completed.AddMinutes(-1), completed, 0, ConductorParallelAcceptanceAttemptOutcome.Failed,
            "", "", "", "", "", "", CompletedAt: completed, Kind: "gate");
        var result = new ConductorParallelAcceptanceRunArtifact("accepted", null, null, null, null,
            new(false, [new("tests", false, 1, null, FailingTestAttributions:
                [new("ConsumerTests.Works", AcceptanceTestFailureOrigin.Introduced, "fixture")])]),
            null, null, null, null);
        var attempts = Path.Combine(HostLedgerStateDirectory.Resolve(Root), "acceptance-gate-attempts");
        Directory.CreateDirectory(attempts);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        File.WriteAllText(Path.Combine(attempts, "fixture-red.attempt.json"), JsonSerializer.Serialize(metadata, json));
        File.WriteAllText(Path.Combine(attempts, "fixture-red.result.json"), JsonSerializer.Serialize(result, json));
        var failure = AcceptanceClassDurationHistoryStore.RecordAttempt(Root, "fixture-duration",
            [new("fixture-duration", "fixture", "ConsumerTests", 1)], 20, completed: true, passed: true);
        Assert.Null(failure);
    }

    public void Dispose() => _files.Dispose();
}
