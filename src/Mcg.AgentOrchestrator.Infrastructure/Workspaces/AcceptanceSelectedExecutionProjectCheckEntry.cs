using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A scheduled whole-project check retains its identity while selection changes its execution.
internal sealed record AcceptanceSelectedExecutionProjectCheckEntry(
    string Name, string Project, AcceptanceSelectedExecutionLane Lane)
{
    internal static AcceptanceSelectedExecutionProjectCheckEntry Create(
        AcceptanceWholeProjectCheckInventory check, IReadOnlySet<string> retainedClasses,
        IReadOnlyList<string> floors, bool reachAvailable = true, bool hasChangedTestFiles = false)
    {
        var inventory = check.Classes.Select(item => item.FullName).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var whole = !reachAvailable || hasChangedTestFiles || inventory.Length == 0 || floors.Contains(check.CheckName, StringComparer.Ordinal) ||
            AcceptanceSelectedExecutionPlan.IsDefaultFloor(check.CheckName);
        var retained = whole ? inventory : inventory.Where(retainedClasses.Contains).ToArray();
        var skipped = inventory.Except(retained, StringComparer.Ordinal).ToArray();
        var disposition = whole ? "retained" : retained.Length == 0 ? "skipped" : "narrowed";
        var filter = disposition == "narrowed"
            ? "(" + string.Join('|', retained.Select(name => "FullyQualifiedName=" + name)) + ")" : null;
        if (filter is not null) _ = AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(filter).ToArray();
        return new(check.CheckName, check.Project, new(check.CheckName, disposition, retained, skipped, filter));
    }

    internal bool Matches(Check check) => check.Name == Name &&
        check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        AcceptancePolicyShardPlanner.ProjectMatches(check.Project, Project) &&
        !AcceptancePolicyShardPlanner.IsFocusedProjectDotnetCheck(check);

    internal Check Apply(Check check)
    {
        if (Lane.Disposition != "narrowed") return check;
        // Dotnet checks cannot declare an environment; leave the absent field unset.
        check.ValidateEnvironment();
        return new()
        {
            Name = check.Name, Type = check.Type, Command = check.Command, Project = check.Project,
            Arguments = [.. check.Arguments, "--filter", Lane.Filter!], Reports = check.Reports,
            Pattern = check.Pattern, FilePath = check.FilePath,
            TimeoutMinutes = check.TimeoutMinutes, Advisory = check.Advisory, Runner = check.Runner,
            EstimatedSerialSeconds = check.EstimatedSerialSeconds, ExclusiveResourceKeys = check.ExclusiveResourceKeys,
            IsFocusedEvidenceSelection = check.IsFocusedEvidenceSelection,
            FocusedEvidenceTokens = check.FocusedEvidenceTokens, FocusedEvidenceSelections = check.FocusedEvidenceSelections,
            FocusedEvidenceMatchedClasses = check.FocusedEvidenceMatchedClasses,
            RequiresBuildSystemChange = check.RequiresBuildSystemChange,
            IsResolvedInfrastructureLane = true, AllowsEmptySelection = true
        };
    }
}
