// TEMP and TMP are process-wide; this collection runs without other tests in the same process.
[Collection("ProbeResolutionTempIndependence")]
public sealed class ProbeResolutionTempIndependenceTests
{
    private const string ProbeProjectName = "Mcg.AgentOrchestrator.IsolatedDotnetProbe";
    private const string ManagedAssemblyName = ProbeProjectName + ".dll";
    private static string ApphostName => ProbeProjectName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);

    [CollectionDefinition("ProbeResolutionTempIndependence", DisableParallelization = true)]
    [ProcessLocalTestCollection("TEMP and TMP are process-local; all files use private temporary roots.")]
    public sealed class SerialCollection;

    [Fact]
    public void ProbeDirectory_IsUnchangedWhenTempAndTmpPointAtAnEmptyDirectory()
    {
        var anchor = typeof(ProbeResolutionTempIndependenceTests);
        string[] requiredFiles = [ManagedAssemblyName, ApphostName];
        var ambientDirectory = FixtureProbeLocator.FindDirectory(anchor, ProbeProjectName, requiredFiles);
        Assert.True(ambientDirectory is not null,
            "Locator tried: " + string.Join("; ", FixtureProbeLocator.TriedPaths(anchor, ProbeProjectName, requiredFiles)));

        var redirectedRoot = Directory.CreateTempSubdirectory("mcg-probe-temp-independence-").FullName;
        var originalTemp = Environment.GetEnvironmentVariable("TEMP");
        var originalTmp = Environment.GetEnvironmentVariable("TMP");
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        try
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(redirectedRoot));
            Environment.SetEnvironmentVariable("TEMP", redirectedRoot);
            Environment.SetEnvironmentVariable("TMP", redirectedRoot);
            if (OperatingSystem.IsWindows())
                Assert.Equal(NormalizePath(redirectedRoot), NormalizePath(Path.GetTempPath()), pathComparer);

            var redirectedDirectory = FixtureProbeLocator.FindDirectory(anchor, ProbeProjectName, requiredFiles);
            Assert.True(redirectedDirectory is not null,
                "Locator tried: " + string.Join("; ", FixtureProbeLocator.TriedPaths(anchor, ProbeProjectName, requiredFiles)));
            Assert.Equal(NormalizePath(ambientDirectory!), NormalizePath(redirectedDirectory!), pathComparer);
            Assert.Empty(Directory.EnumerateFiles(redirectedRoot, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", originalTemp);
            Environment.SetEnvironmentVariable("TMP", originalTmp);
            Directory.Delete(redirectedRoot, recursive: true);
        }
    }

    [Fact]
    public void IncompleteClosure_ApphostWithoutManagedAssembly_IsRefused()
    {
        var root = Directory.CreateTempSubdirectory("mcg-probe-incomplete-closure-").FullName;
        try
        {
            const string configuration = "Debug";
            var closure = Directory.CreateDirectory(Path.Combine(root, "TestAssembly", configuration)).FullName;
            var sibling = Directory.CreateDirectory(Path.Combine(root, ProbeProjectName, configuration)).FullName;
            File.WriteAllText(Path.Combine(closure, ApphostName), "apphost marker");
            File.WriteAllText(Path.Combine(sibling, ApphostName), "apphost marker");
            // Select the reachable apphost first so the locator's all-files check must reject it.
            // No test-assembly marker is written, keeping the real runner's build root unreachable.
            string[] requiredFiles = [ApphostName, ManagedAssemblyName];

            Assert.Null(FixtureProbeLocator.FindDirectory(closure, configuration, ProbeProjectName, requiredFiles));
            var triedPaths = FixtureProbeLocator.TriedPaths(closure, configuration, ProbeProjectName, requiredFiles);
            Assert.NotEmpty(triedPaths);
            Assert.Contains(sibling, triedPaths);

            File.WriteAllText(Path.Combine(sibling, ManagedAssemblyName), "managed assembly marker");
            Assert.Equal(sibling, FixtureProbeLocator.FindDirectory(closure, configuration, ProbeProjectName, requiredFiles));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
