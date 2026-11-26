namespace Mcg.AgentOrchestrator.Core;

internal static class RepositoryProjectOwnership
{
    internal const string InfrastructureTestsRoot = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";

    internal static string? FindNestedProjectFile(string repositoryRoot, string projectDirectory, string sourcePath)
    {
        try
        {
            repositoryRoot = Path.GetFullPath(repositoryRoot);
            var projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory, repositoryRoot));
            var source = Path.GetFullPath(sourcePath, repositoryRoot);
            var projectPrefix = Path.EndsInDirectorySeparator(projectRoot)
                ? projectRoot : projectRoot + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!source.StartsWith(projectPrefix, comparison))
                return null;

            for (var directory = Path.GetDirectoryName(source);
                directory is not null && !directory.Equals(projectRoot, comparison);
                directory = Path.GetDirectoryName(directory))
            {
                if (!Directory.Exists(directory))
                    continue;

                var project = Directory.EnumerateFiles(directory, "*.csproj")
                    .Order(StringComparer.Ordinal).FirstOrDefault();
                if (project is not null)
                    return Path.GetRelativePath(repositoryRoot, project).Replace('\\', '/');
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unavailable filesystem observation cannot establish a nested owner.
        }

        return null;
    }

    internal static bool IsExtractedInfrastructureProject(string? project)
    {
        if (string.IsNullOrWhiteSpace(project))
            return false;

        var normalized = project.Replace('\\', '/').Trim();
        var fileName = Path.GetFileName(normalized);
        return normalized.Contains(InfrastructureTestsRoot, StringComparison.OrdinalIgnoreCase) &&
            !normalized.EndsWith(InfrastructureTestsRoot + "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                StringComparison.OrdinalIgnoreCase) &&
            fileName.StartsWith("Mcg.AgentOrchestrator.Infrastructure.", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase);
    }
}
