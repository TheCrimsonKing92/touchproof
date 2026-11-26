namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record FocusedEvidenceUnexecutedClassCause(string Kind, string? OwningProject = null)
{
    internal const string WrongProject = "wrong-project";
    internal const string NotFound = "not-found";
    internal const string NoResultRecorded = FocusedEvidenceNotRunPlan.NoResultRecorded;

    internal string Label => OwningProject is null ? Kind : $"{Kind}:{OwningProject}";

    internal static FocusedEvidenceUnexecutedClassCause Classify(
        string? worktreePath, string selectedProject, string className, string? checkProject = null)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            return new(NoResultRecorded);

        var project = AcceptancePolicyShardPlanner.NormalizePath(selectedProject)!;
        if (!DeclaredTestProjectInventory.IsDeclaredFocusedEvidenceProject(project) &&
            !FocusedEvidenceProjectResolver.TryResolve(project, null, out project))
        {
            project = AcceptancePolicyShardPlanner.NormalizePath(checkProject) ?? "";
        }
        if (!DeclaredTestProjectInventory.IsDeclaredFocusedEvidenceProject(project))
            return new(NoResultRecorded);

        try
        {
            if (FocusedEvidenceNestedProjectClassSelection.ProjectContainsClass(worktreePath, project, className))
                return new(NoResultRecorded);

            var owner = Directory.EnumerateDirectories(worktreePath, "tests")
                .SelectMany(root => Directory.EnumerateFiles(root, "*.csproj", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }))
                .Select(path => Path.GetRelativePath(worktreePath, path).Replace('\\', '/'))
                .Where(path => DeclaredTestProjectInventory.IsDeclaredFocusedEvidenceProject(path) &&
                    !path.Equals(project, StringComparison.OrdinalIgnoreCase) &&
                    !path.Split('/').Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                        part.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault(otherProject =>
                    FocusedEvidenceNestedProjectClassSelection.ProjectContainsClass(worktreePath, otherProject, className));

            return owner is null ? new(NotFound) : new(WrongProject, owner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(NoResultRecorded);
        }
    }

    internal static string SelectorValue(string selector)
    {
        var separator = selector.IndexOfAny(['~', '=']);
        return separator >= 0 && selector[..separator].TrimEnd().Equals("FullyQualifiedName", StringComparison.OrdinalIgnoreCase)
            ? selector[(separator + 1)..].Trim()
            : selector.Trim();
    }
}
