using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: the only writes are inside a unique temporary root; no environment is mutated.
public sealed class FixtureProbeLocatorPathOverloadTests
{
    private const string ProbeProject = "Mcg.AgentOrchestrator.SupervisorLaunchProbe";

    [Theory]
    [InlineData(ProbeProject)]
    [InlineData("MissingFixtureProbePathOverloadProject")]
    public void RunningAssembly_PathAndTypeOverloadsReturnSameDirectoryAndPaths(string project)
    {
        var anchor = typeof(FixtureProbeLocatorPathOverloadTests);
        var closure = Path.GetDirectoryName(anchor.Assembly.Location)!;
        var configuration = anchor.Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        string[] requiredFiles = [project + ".dll", project + ".runtimeconfig.json"];

        Assert.Equal(FixtureProbeLocator.FindDirectory(anchor, project, requiredFiles),
            FixtureProbeLocator.FindDirectory(closure, configuration, project, requiredFiles));
        Assert.Equal(FixtureProbeLocator.TriedPaths(anchor, project, requiredFiles),
            FixtureProbeLocator.TriedPaths(closure, configuration, project, requiredFiles));
    }

    [Fact]
    public void RunningAssembly_ProbeOverloadsReturnSameDirectoryAndPaths()
    {
        var anchor = typeof(FixtureProbeLocatorPathOverloadTests);
        var closure = Path.GetDirectoryName(anchor.Assembly.Location)!;
        var configuration = anchor.Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;

        var fromType = DispatchProcessHostTestsConductorSupervisorWindowsLaunchAuthority.LocateProbe(anchor);
        var fromPath = DispatchProcessHostTestsConductorSupervisorWindowsLaunchAuthority.LocateProbe(closure, configuration);

        Assert.Equal(fromType.Directory, fromPath.Directory);
        Assert.Equal(fromType.TriedPaths, fromPath.TriedPaths);
    }

    [Fact]
    public void TemporaryClosure_FindsProbeWithOnlyExplicitlyWrittenFiles()
    {
        var root = Directory.CreateTempSubdirectory("mcg-probe-path-overloads-").FullName;
        try
        {
            var closure = Directory.CreateDirectory(Path.Combine(root, "summary", ".c", "closure-token")).FullName;
            string[] writtenFiles = [Path.Combine(closure, ProbeProject + ".dll"),
                Path.Combine(closure, ProbeProject + ".runtimeconfig.json")];
            foreach (var file in writtenFiles)
                File.WriteAllText(file, "probe marker");

            var found = DispatchProcessHostTestsConductorSupervisorWindowsLaunchAuthority.LocateProbe(closure, "Debug");

            Assert.Equal(closure, found.Directory);
            Assert.Equal([closure], found.TriedPaths);
            Assert.Equal(writtenFiles.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
            Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly =>
                !assembly.IsDynamic && assembly.Location.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
