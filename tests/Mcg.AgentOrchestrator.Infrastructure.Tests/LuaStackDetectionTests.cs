using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = LuaProjectDiscoveryAdapterTests.Fixture;

// Parallel-safe: all filesystem evidence belongs to a unique fixture, without processes.
public sealed class LuaStackDetectionTests
{
    [Theory]
    [InlineData("go.mod")]
    [InlineData("Root.sln")]
    [InlineData("Root.slnx")]
    [InlineData("src/App/App.csproj")]
    [InlineData("src/App/App.fsproj")]
    [InlineData("src/App/App.vbproj")]
    [InlineData("package.json")]
    [InlineData("go.mod|Root.sln")]
    [InlineData("go.mod|package.json")]
    [InlineData("package.json|Root.sln")]
    [InlineData("package.json|src/App/App.csproj")]
    [InlineData("package.json|src/App/App.fsproj")]
    [InlineData("package.json|src/App/App.vbproj")]
    public void Detect_ExistingStackOrRefusal_WithLuaEvidenceIsUnchanged(string paths)
    {
        using var fixture = new Fixture();
        foreach (var path in paths.Split('|'))
            fixture.Write(path);
        File.WriteAllText(Path.Combine(fixture.Root, "go.mod"), "module example.com/app\n");
        var stack = paths.StartsWith("go.mod", StringComparison.Ordinal) ? "go"
            : paths.StartsWith("package.json", StringComparison.Ordinal) ? "node" : "dotnet";
        using var crosswalk = new FakeCrosswalkProcess(stack);
        var originalPaths = paths.Split('|');
        SetEvidence(originalPaths);
        var before = Outcome();
        Assert.True(before is "go" or "node" or "dotnet" || before.Contains("Ambiguous stack markers", StringComparison.Ordinal));
        SetEvidence([.. originalPaths, ".busted", "spec/a_spec.lua", "x-1.0-1.rockspec", "scripts/a.lua"]);
        Assert.Equal(before, Outcome());

        void SetEvidence(string[] evidencePaths) => crosswalk.DetectResult = new(0,
            JsonSerializer.Serialize(new { stack, evidence = evidencePaths.Select(path => new { path, reason = "" }) }), "");

        string Outcome()
        {
            try
            {
                return ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader) switch
                {
                    GoProjectDiscoveryAdapter => "go",
                    NodeProjectDiscoveryAdapter => "node",
                    DotnetProjectDiscoveryAdapter => "dotnet",
                    var adapter => throw new InvalidOperationException($"Unexpected adapter: {adapter.GetType().Name}")
                };
            }
            catch (InvalidOperationException error) when (error.Message.Contains("Ambiguous stack markers", StringComparison.Ordinal))
            {
                return error.Message;
            }
        }
    }

    [Fact]
    public void Serialize_Lua_PreservesStackIdentityAndDispatchMapping()
    {
        using var fixture = new Fixture();
        fixture.Write(".busted");
        var model = fixture.Discover();

        Assert.Equal(3, (int)ProjectStack.Lua);
        Assert.Equal("\"lua\"", JsonSerializer.Serialize(ProjectStack.Lua));
        var json = ProjectModelJson.Serialize(model);
        Assert.Equal(ProjectStack.Lua, ProjectModelJson.Deserialize(json).Stack);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("lua", document.RootElement.GetProperty("stack").GetString());
        fixture.Write("home/project-model.json", json);
        Assert.Equal(Toolchain.Unknown, ProjectStackResolver.Resolve(fixture.Root, Path.Combine(fixture.Root, "home")));
    }
}
