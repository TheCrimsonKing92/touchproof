using System.ComponentModel;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = LuaProjectDiscoveryAdapterTests.Fixture;

// Parallel-safe: every fixture and tool path is unique; process calls are injected.
public sealed class RootMarkerSelectionWithoutCrosswalkTests
{
    [Theory]
    [InlineData("go-module", "go", typeof(GoProjectDiscoveryAdapter))]
    [InlineData("node", "node", typeof(NodeProjectDiscoveryAdapter))]
    [InlineData("solution", "dotnet", typeof(DotnetProjectDiscoveryAdapter))]
    [InlineData("no-solution", "dotnet", typeof(DotnetProjectDiscoveryAdapter))]
    public void Select_CrosswalkReport_SelectsNativeAdapter(string name, string stack, Type adapterType)
    {
        using var fixture = CreateFixture(name, out var root);
        using var crosswalk = new FakeCrosswalkProcess(stack);

        var adapter = ProjectDiscoveryAdapterSelector.Select(root, crosswalk.Reader);

        Assert.Equal(adapterType, adapter.GetType());
        Assert.Equal(1, crosswalk.VersionCalls);
        Assert.Equal(new[] { "detect", root }, Assert.Single(crosswalk.Calls).Arguments);
    }

    [Theory]
    [InlineData("go", typeof(GoProjectDiscoveryAdapter))]
    [InlineData("node", typeof(NodeProjectDiscoveryAdapter))]
    [InlineData("dotnet", typeof(DotnetProjectDiscoveryAdapter))]
    [InlineData("lua", typeof(LuaProjectDiscoveryAdapter))]
    [InlineData("unknown", typeof(DeclaredProjectDiscoveryAdapter))]
    public void Select_CrosswalkReport_OverridesConflictingMarkers(string stack, Type adapterType)
    {
        using var fixture = new Fixture();
        fixture.Write("go.mod", "module example.com/app\n");
        fixture.Write("package.json", "{}");
        fixture.Write("Root.sln");
        using var crosswalk = new FakeCrosswalkProcess(stack);

        var adapter = ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader);

        Assert.Equal(adapterType, adapter.GetType());
        Assert.Equal(new[] { "detect", fixture.Root }, Assert.Single(crosswalk.Calls).Arguments);
    }

    [Theory]
    [InlineData("go-module")]
    [InlineData("node")]
    [InlineData("solution")]
    [InlineData("no-solution")]
    public void Select_ProcessStartFailure_UsesMarkerFallback(string name)
    {
        using var fixture = CreateFixture(name, out var root);
        WriteConflictingMarkers(root);
        using var crosswalk = new FakeCrosswalkProcess();
        crosswalk.DetectException = new Win32Exception("injected process-start failure");

        var error = Assert.Throws<InvalidOperationException>(() => ProjectDiscoveryAdapterSelector.Select(root, crosswalk.Reader));

        Assert.Contains(crosswalk.Binary, error.Message, StringComparison.Ordinal);
        Assert.Same(crosswalk.DetectException, error.InnerException);
        Assert.Equal(new[] { "detect", root }, Assert.Single(crosswalk.Calls).Arguments);
    }

    [Theory]
    [InlineData("go-module")]
    [InlineData("node")]
    [InlineData("solution")]
    [InlineData("no-solution")]
    public void Select_MissingTool_UsesMarkerFallback(string name)
    {
        using var fixture = CreateFixture(name, out var root);
        WriteConflictingMarkers(root);
        var path = Path.Combine(root, "missing-crosswalk.exe");
        var resolver = new CrosswalkExecutableResolver(path,
            runVersionAsync: (_, _) => throw new InvalidOperationException("Missing tool must never launch."));
        var reader = new CrosswalkStackReader(resolver, TimeSpan.FromSeconds(30),
            (_, _) => throw new InvalidOperationException("Missing tool must never launch."));

        var error = Assert.Throws<InvalidOperationException>(() => ProjectDiscoveryAdapterSelector.Select(root, reader));
        Assert.Contains(path, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("timeout")]
    [InlineData("capture")]
    [InlineData("malformed")]
    [InlineData("io")]
    public void Select_DetectFailureWithMarkers_DoesNotFallBack(string failure)
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        using var crosswalk = new FakeCrosswalkProcess("dotnet");
        crosswalk.DetectResult = failure switch
        {
            "exit" => new(1, "", "failed"),
            "timeout" => new(-1, "", "", TimedOut: true),
            "capture" => crosswalk.DetectResult with { CaptureLimited = true },
            "malformed" => new(0, "not json", ""),
            _ => crosswalk.DetectResult
        };
        if (failure == "io") crosswalk.DetectException = new IOException("injected stream failure");

        var error = Assert.Throws<InvalidOperationException>(() => ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader));

        Assert.Contains(crosswalk.Binary, error.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "detect", fixture.Root }, Assert.Single(crosswalk.Calls).Arguments);
    }

    [Theory]
    [InlineData("package.json", null)]
    [InlineData("go.mod|Root.sln", "Ambiguous stack markers: go.mod and Root.sln.")]
    [InlineData("go.mod|Root.slnx", "Ambiguous stack markers: go.mod and Root.slnx.")]
    [InlineData("go.mod|package.json", "Ambiguous stack markers: go.mod and package.json.")]
    [InlineData("package.json|Root.sln", "Ambiguous stack markers: package.json and Root.sln.")]
    [InlineData("package.json|src/App.csproj", "Ambiguous stack markers: package.json and src/App.csproj.")]
    public void Select_LaunchFailure_PreservesNodeOrAmbiguity(string paths, string? refusal)
    {
        using var fixture = new Fixture();
        using var crosswalk = new FakeCrosswalkProcess("node");
        crosswalk.DetectResult = new(0, JsonSerializer.Serialize(new { stack = "node",
            evidence = paths.Split('|').Select(path => new { path, reason = "fixture" }) }), "");

        if (refusal is null)
            Assert.IsType<NodeProjectDiscoveryAdapter>(ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader));
        else
        {
            var error = Assert.Throws<InvalidOperationException>(() => ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader));
            Assert.Equal($"No discovery adapter is available for this repository: {refusal}", error.Message);
        }
        Assert.Equal(1, crosswalk.VersionCalls);
        Assert.Equal(new[] { "detect", fixture.Root }, Assert.Single(crosswalk.Calls).Arguments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Select_MissingTool_ReportsAttemptedPath(bool explicitPath)
    {
        using var fixture = new Fixture();
        var dataRoot = Path.Combine(fixture.Root, "data");
        var path = explicitPath ? Path.Combine(dataRoot, "missing.exe") : Path.Combine(dataRoot, "tools", "crosswalk", "crosswalk.exe");
        var resolver = new CrosswalkExecutableResolver(explicitPath ? path : null, dataRoot,
            (_, _) => throw new InvalidOperationException("Missing tool must never launch."));
        var reader = new CrosswalkStackReader(resolver, TimeSpan.FromSeconds(30),
            (_, _) => throw new InvalidOperationException("Missing tool must never launch."));

        var error = Assert.Throws<InvalidOperationException>(() => ProjectDiscoveryAdapterSelector.Select(fixture.Root, reader));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("go", "go.mod", typeof(GoProjectDiscoveryAdapter))]
    [InlineData("node", "package.json", typeof(NodeProjectDiscoveryAdapter))]
    [InlineData("dotnet", "Root.sln|src/App.csproj", typeof(DotnetProjectDiscoveryAdapter))]
    [InlineData("unknown", "package.json", typeof(DeclaredProjectDiscoveryAdapter))]
    [InlineData("node", "go.mod", typeof(NodeProjectDiscoveryAdapter))]
    public void Select_SingleStackEvidence_PreservesReportedAdapter(string stack, string paths, Type adapterType)
    {
        using var fixture = new Fixture();
        fixture.Write("go.mod", "module example.com/app\n");
        using var crosswalk = new FakeCrosswalkProcess(stack);
        crosswalk.DetectResult = new(0, JsonSerializer.Serialize(new { stack,
            evidence = paths.Split('|').Select(path => new { path, reason = "" }) }), "");

        Assert.Equal(adapterType, ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader).GetType());
    }

    [Theory]
    [InlineData("absolute", true)]
    [InlineData("backslash", true)]
    [InlineData("dot-segments", true)]
    [InlineData("outside", false)]
    [InlineData("prefix-sibling", false)]
    [InlineData("nested-go", false)]
    [InlineData("nested-solution", false)]
    [InlineData("duplicate", false)]
    public void Select_EvidencePaths_UsesLexicalRepositoryMembership(string scenario, bool ambiguous)
    {
        using var fixture = new Fixture();
        var path = scenario switch
        {
            "absolute" => Path.Combine(fixture.Root, "Root.sln"),
            "backslash" => "src\\App.csproj",
            "dot-segments" => "src/../Root.slnx",
            "outside" => "../Outside.csproj",
            "prefix-sibling" => fixture.Root + "-sibling/Outside.csproj",
            "nested-go" => "nested/go.mod",
            "nested-solution" => "nested/Other.sln",
            _ => "./package.json"
        };
        using var crosswalk = new FakeCrosswalkProcess("node");
        crosswalk.DetectResult = new(0, JsonSerializer.Serialize(new { stack = "node",
            evidence = new[] { new { path = "package.json", reason = "" }, new { path, reason = "" } } }), "");

        if (ambiguous)
        {
            var error = Assert.Throws<InvalidOperationException>(() => ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader));
            Assert.Contains("package.json", error.Message, StringComparison.Ordinal);
            Assert.Contains(path, error.Message, StringComparison.Ordinal);
        }
        else
            Assert.IsType<NodeProjectDiscoveryAdapter>(ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader));
    }

    [Fact]
    public void Discover_DefaultReader_RefusesWithInstalledToolPath()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        var registry = new Mcg.AgentOrchestrator.App.Orchestration.OrchestratorProjectRegistry(
            Path.Combine(fixture.Root, "registry"), Path.Combine(fixture.Root, "data"));
        using var output = new StringWriter();

        var error = Assert.Throws<InvalidOperationException>(() => Mcg.AgentOrchestrator.App.Cli.ProjectCliCommand.Execute(
            ["project", "discover"], registry, fixture.Root, null, output));

        Assert.Contains(Path.Combine("tools", "crosswalk", "crosswalk.exe"), error.Message, StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    private static void WriteConflictingMarkers(string root)
    {
        File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/app\n");
        File.WriteAllText(Path.Combine(root, "package.json"), "{}");
        File.WriteAllText(Path.Combine(root, "Root.sln"), "");
    }

    private static IDisposable CreateFixture(string name, out string root)
    {
        if (name == "node")
        {
            var node = new NodeProjectDiscoveryAdapterTests.Fixture();
            root = node.Root;
            return node;
        }
        var fixture = new ProjectOnboardingFixture(name);
        root = fixture.Root;
        return fixture;
    }
}
