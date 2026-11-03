using System.Reflection;

internal static class FixtureProbeLocator
{
    internal static string? FindDirectory(Type anchor, string projectName, params string[] requiredFiles)
        => FindDirectory(Path.GetDirectoryName(anchor.Assembly.Location)!,
            anchor.Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration,
            projectName, requiredFiles);

    internal static string? FindDirectory(string closureDirectory, string configuration,
        string projectName, params string[] requiredFiles)
    {
        var location = Locate(closureDirectory, configuration, projectName, requiredFiles);
        return location.Directory is { } directory &&
            requiredFiles.All(name => File.Exists(Path.Combine(directory, name)))
            ? directory : null;
    }

    internal static IReadOnlyList<string> TriedPaths(Type anchor, string projectName, params string[] requiredFiles)
        => TriedPaths(Path.GetDirectoryName(anchor.Assembly.Location)!,
            anchor.Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration,
            projectName, requiredFiles);

    internal static IReadOnlyList<string> TriedPaths(string closureDirectory, string configuration,
        string projectName, params string[] requiredFiles)
        => Locate(closureDirectory, configuration, projectName, requiredFiles).TriedPaths;

    private static TestBuildOutputLocator.Location Locate(string closureDirectory, string configuration,
        string projectName, string[] requiredFiles)
        => TestBuildOutputLocator.Locate(projectName, configuration, closureDirectory, requiredFiles[0]);
}
