using System.Xml;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Ownership and reference absence are established from this checkout, never from historical paths.
internal static class ChangedFileOwningProjectResolver
{
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static bool TryResolve(string repositoryRoot, string changedPath,
        IReadOnlyList<string> declaredTestProjects, out IReadOnlyList<string> referencingProjects,
        out string? failure)
    {
        referencingProjects = [];
        failure = null;
        var relative = changedPath.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/').Contains("..")) return false;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
            var directory = Path.GetDirectoryName(Path.GetFullPath(relative, root));
            string[] owners = [];
            while (directory is not null && !PathComparer.Equals(directory, root))
            {
                if (Directory.Exists(directory))
                {
                    owners = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
                    if (owners.Length == 0)
                        owners = new[] { "src", "tests" }.Select(child => Path.Combine(directory, child))
                            .Where(Directory.Exists)
                            .SelectMany(child => Directory.GetFiles(child, "*.csproj", SearchOption.TopDirectoryOnly)).ToArray();
                    if (owners.Length > 0) break;
                }
                directory = Path.GetDirectoryName(directory);
            }
            if (owners.Length == 0) return false;

            var owningProjects = owners.Select(Path.GetFullPath).ToHashSet(PathComparer);
            var references = new HashSet<string>(PathComparer);
            foreach (var project in declaredTestProjects)
            {
                var document = XDocument.Load(project);
                foreach (var reference in document.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
                {
                    var include = reference.Attribute("Include")?.Value;
                    if (string.IsNullOrWhiteSpace(include) || include.Contains("$(", StringComparison.Ordinal) ||
                        include.Contains("@(", StringComparison.Ordinal) || include.Contains("%(", StringComparison.Ordinal) ||
                        include.IndexOfAny(['*', '?', ';']) >= 0)
                    {
                        failure = $"Unsupported ProjectReference in {project}.";
                        return false;
                    }
                    var target = Path.GetFullPath(include.Replace('\\', '/'), Path.GetDirectoryName(project)!);
                    if (!File.Exists(target))
                    {
                        failure = $"ProjectReference target is missing: {target}.";
                        return false;
                    }
                    if (owningProjects.Contains(target)) references.Add(project);
                }
            }
            referencingProjects = references.Order(PathComparer).ToArray();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           XmlException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            failure = exception.Message;
            return false;
        }
    }
}
