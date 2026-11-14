using System.ComponentModel;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its fixture or temporary root; no process is launched.
public sealed class ProjectDiscoveryAdapterSelectorTests
{
    [Theory]
    [InlineData("solution", "Sample.sln")]
    [InlineData("no-solution", "src/Loose.Library/Loose.Library.csproj")]
    public void Detect_DotnetFixture_ReturnsExactMarker(string fixtureName, string marker)
    {
        using var fixture = new ProjectOnboardingFixture(fixtureName);
        using var crosswalk = EvidenceReader("dotnet", marker);
        Assert.IsType<DotnetProjectDiscoveryAdapter>(ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader));
    }

    [Fact]
    public void Detect_GoModOnly_ReturnsGoMarker()
    {
        using var root = new TemporaryRoot();
        root.Write("go.mod");
        File.WriteAllText(Path.Combine(root.Path, "go.mod"), "module example.com/app\n");
        using var crosswalk = EvidenceReader("go", "go.mod");
        Assert.IsType<GoProjectDiscoveryAdapter>(ProjectDiscoveryAdapterSelector.Select(root.Path, crosswalk.Reader));
    }

    [Theory]
    [InlineData("Root.sln")]
    [InlineData("Root.slnx")]
    public void Detect_GoModAndRootSolution_RefusesBothMarkers(string solution)
    {
        using var root = new TemporaryRoot();
        using var crosswalk = EvidenceReader("go", "go.mod", solution);
        var error = Assert.Throws<InvalidOperationException>(() => ProjectDiscoveryAdapterSelector.Select(root.Path, crosswalk.Reader));
        Assert.Contains("Ambiguous", error.Message);
        Assert.Contains("go.mod", error.Message);
        Assert.Contains(solution, error.Message);
    }

    [Fact]
    public void Detect_EmptyRoot_RefusesAndListsMarkers()
    {
        using var root = new TemporaryRoot();
        AssertToolRefusal(root.Path);
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".git")]
    [InlineData("custom-excluded")]
    public void Detect_ProjectUnderExcludedDirectory_Refuses(string directory)
    {
        using var root = new TemporaryRoot();
        root.Write($"src/{directory}/Only.csproj");
        AssertToolRefusal(root.Path);
    }

    [Fact]
    public void Detect_MissingRoot_ThrowsDirectoryNotFound()
    {
        using var root = new TemporaryRoot();
        AssertToolRefusal(Path.Combine(root.Path, "missing"));
    }

    [Fact]
    public void Detect_MultipleSolutions_ChoosesOrdinalFirstBeforeProjects()
    {
        using var root = new TemporaryRoot();
        using var crosswalk = EvidenceReader("dotnet", "Z.sln", "A.slnx", "0.csproj");
        Assert.IsType<DotnetProjectDiscoveryAdapter>(ProjectDiscoveryAdapterSelector.Select(root.Path, crosswalk.Reader));
    }

    [Theory]
    [InlineData("csproj")]
    [InlineData("fsproj")]
    [InlineData("vbproj")]
    public void Detect_DeepProjects_OrdersNormalizedRelativePaths(string extension)
    {
        using var root = new TemporaryRoot();
        using var crosswalk = EvidenceReader("dotnet", $"src/aZ.{extension}", $"src/a/deep/First.{extension}");
        Assert.IsType<DotnetProjectDiscoveryAdapter>(ProjectDiscoveryAdapterSelector.Select(root.Path, crosswalk.Reader));
    }

    [Fact]
    public void Detect_GoModWithNestedDotnetMarkers_PrefersGo()
    {
        using var root = new TemporaryRoot();
        root.Write("go.mod");
        File.WriteAllText(Path.Combine(root.Path, "go.mod"), "module example.com/app\n");
        root.Write("nested/Other.sln");
        root.Write("nested/Other.csproj");
        using var crosswalk = EvidenceReader("go", "go.mod", "nested/Other.sln");
        Assert.IsType<GoProjectDiscoveryAdapter>(ProjectDiscoveryAdapterSelector.Select(root.Path, crosswalk.Reader));
    }

    [Theory]
    [InlineData("go.mod", "go.mod")]
    [InlineData("ambiguous", "Root.sln")]
    public void Select_UnsupportedOrRefusedStack_ThrowsWithDetectionReason(string setup, string reason)
    {
        using var root = new TemporaryRoot();
        if (setup.Length > 0)
            root.Write("go.mod");
        if (setup == "ambiguous")
            root.Write("Root.sln");

        using var crosswalk = new FakeCrosswalkProcess("go");
        if (setup == "ambiguous")
            crosswalk.DetectResult = new(0, JsonSerializer.Serialize(new { stack = "go",
                evidence = new[] { new { path = "go.mod", reason = "" }, new { path = "Root.sln", reason = "" } } }), "");
        var exception = Assert.Throws<InvalidOperationException>(() => ProjectDiscoveryAdapterSelector.Select(root.Path, crosswalk.Reader));

        Assert.Contains("No discovery adapter is available", exception.Message);
        Assert.Contains(reason, exception.Message);
    }

    [Fact]
    public void Select_NoMarker_ReturnsDeclaredAdapter()
    {
        using var root = new TemporaryRoot();
        using var crosswalk = new FakeCrosswalkProcess();
        Assert.IsType<DeclaredProjectDiscoveryAdapter>(ProjectDiscoveryAdapterSelector.Select(root.Path, crosswalk.Reader));
    }

    [Theory]
    [InlineData("solution")]
    [InlineData("no-solution")]
    [InlineData("answer-key-repository")]
    public void Select_DotnetFixture_PreservesDirectAdapterModel(string fixtureName)
    {
        using var fixture = new ProjectOnboardingFixture(fixtureName);
        var exclusions = RepositorySourceInventory.ExcludedDirectoryNames;

        using var crosswalk = new FakeCrosswalkProcess("dotnet");
        var adapter = ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader);
        var selected = adapter.Discover(fixture.Root, exclusions, null, UnitCommandKinds.None);
        var direct = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root, exclusions, null, UnitCommandKinds.None);

        Assert.IsType<DotnetProjectDiscoveryAdapter>(adapter);
        Assert.Equal(ProjectStack.Dotnet, selected.Stack);
        Assert.Equal(ProjectModelJson.Serialize(direct), ProjectModelJson.Serialize(selected));
    }

    [Fact]
    public void Select_GoFixture_PreservesDirectAdapterModel()
    {
        using var fixture = new ProjectOnboardingFixture("go-module");
        var exclusions = RepositorySourceInventory.ExcludedDirectoryNames;
        using var crosswalk = new FakeCrosswalkProcess("go");

        var adapter = ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader);
        var selected = adapter.Discover(fixture.Root, exclusions, null, UnitCommandKinds.None);
        var direct = new GoProjectDiscoveryAdapter().Discover(fixture.Root, exclusions, null, UnitCommandKinds.None);

        Assert.IsType<GoProjectDiscoveryAdapter>(adapter);
        Assert.Equal(ProjectStack.Go, selected.Stack);
        Assert.Equal(ProjectModelJson.Serialize(direct), ProjectModelJson.Serialize(selected));
        Assert.Equal(new[] { "detect", fixture.Root }, Assert.Single(crosswalk.Calls).Arguments);
    }

    [Fact]
    public void Select_NodeFixture_PreservesDirectAdapterModel()
    {
        using var fixture = new NodeProjectDiscoveryAdapterTests.Fixture();
        var exclusions = RepositorySourceInventory.ExcludedDirectoryNames;
        using var crosswalk = new FakeCrosswalkProcess("node");

        var adapter = ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader);
        var selected = adapter.Discover(fixture.Root, exclusions, null, UnitCommandKinds.None);
        var direct = new NodeProjectDiscoveryAdapter().Discover(fixture.Root, exclusions, null, UnitCommandKinds.None);

        Assert.IsType<NodeProjectDiscoveryAdapter>(adapter);
        Assert.Equal(ProjectStack.Node, selected.Stack);
        Assert.Equal(ProjectModelJson.Serialize(direct), ProjectModelJson.Serialize(selected));
        Assert.Equal(new[] { "detect", fixture.Root }, Assert.Single(crosswalk.Calls).Arguments);
    }

    private static FakeCrosswalkProcess EvidenceReader(string stack, params string[] paths)
    {
        var crosswalk = new FakeCrosswalkProcess(stack);
        crosswalk.DetectResult = new(0, JsonSerializer.Serialize(new { stack,
            evidence = paths.Select(path => new { path, reason = "" }) }), "");
        return crosswalk;
    }

    private static void AssertToolRefusal(string root)
    {
        using var crosswalk = new FakeCrosswalkProcess();
        crosswalk.DetectException = new Win32Exception("injected process-start failure");
        var error = Assert.Throws<InvalidOperationException>(() => ProjectDiscoveryAdapterSelector.Select(root, crosswalk.Reader));
        Assert.Contains(crosswalk.Binary, error.Message, StringComparison.Ordinal);
        Assert.Same(crosswalk.DetectException, error.InnerException);
        Assert.Equal(new[] { "detect", root }, Assert.Single(crosswalk.Calls).Arguments);
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "project-selector", Guid.NewGuid().ToString("N"));

        public TemporaryRoot() => Directory.CreateDirectory(Path);

        public void Write(string relativePath)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
