using System.Text.Json;
using Xunit;

// Parallel-safe: only reads the manifest and generated files from this candidate's build outputs.
public sealed class RuntimeConfigurationTieredPgoTests
{
    [Theory]
    [MemberData(nameof(RuntimeConfigProjects))]
    public void GeneratedRuntimeConfigs_AppAndManifestTestProjects_DisableTieredPgo(string project)
    {
        var failures = new List<string>();
        CheckFalseProperties(project, ["System.Runtime.TieredPGO"], failures);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    public static IEnumerable<object[]> RuntimeConfigProjects()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(VerifiedRepositoryRoot.Find(), "config", "acceptance-manifest.json")));
        var testProjects = manifest.RootElement.GetProperty("checks").EnumerateArray()
            .Where(check => check.GetProperty("type").GetString() == "dotnet-test")
            .Select(check => Path.GetFileNameWithoutExtension(check.GetProperty("project").GetString()!))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(testProjects);

        foreach (var project in testProjects.Append("Mcg.AgentOrchestrator.App"))
            yield return [project];
    }

    [Fact]
    public void AppRuntimeConfig_ExistingGcSettings_AreUnchanged()
    {
        var failures = new List<string>();
        CheckFalseProperties("Mcg.AgentOrchestrator.App",
            ["System.GC.Server", "System.GC.Concurrent"], failures);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static void CheckFalseProperties(string projectName, string[] propertyNames, List<string> failures)
    {
        var path = FindRuntimeConfig(projectName, failures);
        if (path is null)
            return;

        using var runtimeConfig = JsonDocument.Parse(File.ReadAllText(path));
        var properties = runtimeConfig.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");
        foreach (var name in propertyNames)
        {
            if (!properties.TryGetProperty(name, out var value))
                failures.Add($"{path}: missing {name}.");
            else if (value.ValueKind != JsonValueKind.False)
                failures.Add($"{path}: expected {name} to be JSON false, found {value.GetRawText()}.");
        }
    }

    private static string? FindRuntimeConfig(string projectName, List<string> failures)
    {
        var anchor = typeof(RuntimeConfigurationTieredPgoTests);
        var fileName = projectName + ".runtimeconfig.json";
        var localPath = Path.Combine(Path.GetDirectoryName(anchor.Assembly.Location)!, fileName);
        if (File.Exists(localPath))
            return localPath;

        var directory = FixtureProbeLocator.FindDirectory(anchor, projectName, fileName);
        if (directory is not null)
            return Path.Combine(directory, fileName);

        var triedPaths = FixtureProbeLocator.TriedPaths(anchor, projectName, fileName)
            .Select(candidate => Path.Combine(candidate, fileName)).Prepend(localPath);
        failures.Add($"{projectName}: generated runtime configuration missing. Tried: {string.Join(", ", triedPaths)}");
        return null;
    }
}
