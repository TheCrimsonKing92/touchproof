using System.Text.Json;
using System.Text.Json.Serialization;
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

    internal void WithCostHistory(string engine = "",
        int completedGateAttempts = ReverseDependencyEvidenceCostInputs.MinimumCompletedGateAttempts)
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
        WriteCandidateFailureHistory(Root, completedGateAttempts);
        var failure = AcceptanceClassDurationHistoryStore.RecordAttempt(Root, "fixture-duration",
            [new("fixture-duration", "fixture", "ConsumerTests", 1)], 20, completed: true, passed: true);
        Assert.Null(failure);
    }

    internal static void WriteCandidateFailureHistory(string root,
        int completedGateAttempts = ReverseDependencyEvidenceCostInputs.MinimumCompletedGateAttempts)
    {
        var completed = DateTimeOffset.UtcNow.AddMinutes(-1);
        var result = new ConductorParallelAcceptanceRunArtifact("accepted", null, null, null, null,
            new(false, [new("tests", false, 1, null, FailingTestAttributions:
                [new("ConsumerTests.Works", AcceptanceTestFailureOrigin.Introduced, "fixture")])]),
            null, null, null, null);
        var attempts = Path.Combine(HostLedgerStateDirectory.Resolve(root), "acceptance-gate-attempts");
        Directory.CreateDirectory(attempts);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        var resultJson = JsonSerializer.Serialize(result, json);
        for (var index = 0; index < completedGateAttempts; index++)
        {
            var id = $"fixture-red-{index:D2}";
            // Keep metadata compact for the source-read byte comparison.
            var metadata = new
            {
                AttemptId = id,
                Kind = "gate",
                CompletedAt = completed,
                Outcome = ConductorParallelAcceptanceAttemptOutcome.Failed
            };
            File.WriteAllText(Path.Combine(attempts, $"{id}.attempt.json"), JsonSerializer.Serialize(metadata, json));
            File.WriteAllText(Path.Combine(attempts, $"{id}.result.json"), resultJson);
        }
    }

    public void Dispose() => _files.Dispose();
}
