namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record FocusedEvidenceUnexecutedClassCause(string Kind, string? OwningProject = null)
{
    internal const string WrongProject = "wrong-project";
    internal const string NotFound = "not-found";
    internal const string NoResultRecorded = FocusedEvidenceNotRunPlan.NoResultRecorded;

    internal string Label => OwningProject is null ? Kind : $"{Kind}:{OwningProject}";

    internal static FocusedEvidenceUnexecutedClassCause Classify(
        string? worktreePath, string selectedProject, string selector, string? checkProject = null)
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
            var className = SelectorValue(selector);
            if (FocusedEvidenceNestedProjectClassSelection.ProjectContainsClass(worktreePath, project, className))
                return new(NoResultRecorded);

            var otherProjects = Directory.EnumerateDirectories(worktreePath, "tests")
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
                .Order(StringComparer.Ordinal).ToArray();

            var owner = FindOwner(className);
            var separator = className.LastIndexOf('.');
            if (owner is null && separator > 0)
            {
                // A method selector names its containing type followed by the method.
                var containingClass = className[..separator];
                if (FocusedEvidenceNestedProjectClassSelection.ProjectContainsClass(worktreePath, project, containingClass))
                    return new(NoResultRecorded);
                owner = FindOwner(containingClass);
            }

            return owner is null ? new(NotFound) : new(WrongProject, owner);

            string? FindOwner(string name) => otherProjects.FirstOrDefault(otherProject =>
                FocusedEvidenceNestedProjectClassSelection.ProjectContainsClass(worktreePath, otherProject, name));
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
