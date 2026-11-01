namespace Mcg.AgentOrchestrator.Core;

public static class ProjectForgeIdentity
{
    public const string Key = "forge-identity";

    public static string Validate(string value)
    {
        if (!Path.IsPathFullyQualified(value) || !File.Exists(value))
            throw new ArgumentException("forge-identity requires the absolute path to an existing identity settings file.");
        var path = Path.GetFullPath(value);
        for (var directory = Directory.GetParent(path); directory is not null; directory = directory.Parent)
        {
            var marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
                throw new ArgumentException("forge-identity settings must be outside every repository.");
        }
        return path;
    }
}
