using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;

// Parallel-safe: evaluations use a unique fixture root and child-only environment values.
public sealed class HermeticNuGetPackageRootImportEvaluationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-nuget-import", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Evaluate_UserProfileFallback_ImportsOnlyWithComputedRoot()
    {
        var packageId = Guid.NewGuid().ToString("N");
        var packages = Path.Combine(_root, "packages");
        var markerDirectory = Path.Combine(packages, packageId, "build");
        Directory.CreateDirectory(markerDirectory);
        File.WriteAllText(Path.Combine(markerDirectory, "marker.props"), """
            <Project>
              <PropertyGroup>
                <HermeticNuGetMarker>marker-imported</HermeticNuGetMarker>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_root, "Fixture.nuget.g.props"), $$"""
            <Project>
              <PropertyGroup>
                <NuGetPackageRoot Condition=" '$(NuGetPackageRoot)' == '' ">$(UserProfile)\.nuget\packages\</NuGetPackageRoot>
              </PropertyGroup>
              <Import Project="$(NuGetPackageRoot){{packageId}}\build\marker.props"
                      Condition="Exists('$(NuGetPackageRoot){{packageId}}\build\marker.props')" />
            </Project>
            """);
        var project = Path.Combine(_root, "Fixture.proj");
        File.WriteAllText(project, """
            <Project>
              <Import Project="Fixture.nuget.g.props" />
            </Project>
            """);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "Directory.Build.targets"), "<Project />");
        var start = new ProcessStartInfo(ResolveDotnetHostPath())
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 { "msbuild", project, "-nologo", "-noAutoResponse", "-getProperty:HermeticNuGetMarker" })
            start.ArgumentList.Add(argument);
        start.Environment["NUGET_PACKAGES"] = packages;
        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(
            start.Environment, _root, Path.Combine(_root, "environment"));
        Assert.NotEqual(packages, Path.Combine(start.Environment["USERPROFILE"]!, ".nuget", "packages"));
        Assert.False(File.Exists(Path.Combine(start.Environment["USERPROFILE"]!, ".nuget", "packages",
            packageId, "build", "marker.props")));

        Assert.Equal("marker-imported", Evaluate(start));

        // Reuse every input; only the environment-derived MSBuild property differs.
        Assert.True(start.Environment.Remove("NuGetPackageRoot"));
        Assert.Equal(string.Empty, Evaluate(start));
    }

    private string Evaluate(ProcessStartInfo start)
    {
        // Hang protection only; the gate's TRX records the combined evaluation duration.
        var result = TestChildProcessCapture.Run(start, hangBound: TimeSpan.FromMinutes(1));
        output.WriteLine($"MSBuild property evaluation: exit={result.ExitCode}\n{result.Stdout}\n{result.Stderr}");
        Assert.True(result.ExitCode == 0,
            $"MSBuild property evaluation exited {result.ExitCode}.\n{result.Stdout}\n{result.Stderr}");
        return result.Stdout.Trim();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
