using System.ComponentModel;
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
    [InlineData("go-module", typeof(GoProjectDiscoveryAdapter))]
    [InlineData("node", typeof(NodeProjectDiscoveryAdapter))]
    [InlineData("solution", typeof(DotnetProjectDiscoveryAdapter))]
    [InlineData("no-solution", typeof(DotnetProjectDiscoveryAdapter))]
    public void Select_ProcessStartFailure_UsesMarkerFallback(string name, Type adapterType)
    {
        using var fixture = CreateFixture(name, out var root);
        using var crosswalk = new FakeCrosswalkProcess();
        crosswalk.DetectException = new Win32Exception("injected process-start failure");

        var adapter = ProjectDiscoveryAdapterSelector.Select(root, crosswalk.Reader);

        Assert.Equal(adapterType, adapter.GetType());
        Assert.Equal(new[] { "detect", root }, Assert.Single(crosswalk.Calls).Arguments);
    }

    [Theory]
    [InlineData("go-module", typeof(GoProjectDiscoveryAdapter))]
    [InlineData("node", typeof(NodeProjectDiscoveryAdapter))]
    [InlineData("solution", typeof(DotnetProjectDiscoveryAdapter))]
    [InlineData("no-solution", typeof(DotnetProjectDiscoveryAdapter))]
    public void Select_MissingTool_UsesMarkerFallback(string name, Type adapterType)
    {
        using var fixture = CreateFixture(name, out var root);
        var path = Path.Combine(root, "missing-crosswalk.exe");
        var resolver = new CrosswalkExecutableResolver(path,
            runVersionAsync: (_, _) => throw new InvalidOperationException("Missing tool must never launch."));
        var reader = new CrosswalkStackReader(resolver, TimeSpan.FromSeconds(30),
            (_, _) => throw new InvalidOperationException("Missing tool must never launch."));

        Assert.Equal(adapterType, ProjectDiscoveryAdapterSelector.Select(root, reader).GetType());
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
        foreach (var path in paths.Split('|')) fixture.Write(path);
        using var crosswalk = new FakeCrosswalkProcess();
        crosswalk.DetectException = new Win32Exception("injected process-start failure");

        if (refusal is null)
            Assert.IsType<NodeProjectDiscoveryAdapter>(ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader));
        else
        {
            Assert.Equal(refusal, ProjectDiscoveryAdapterSelector.Detect(fixture.Root).Refusal);
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
