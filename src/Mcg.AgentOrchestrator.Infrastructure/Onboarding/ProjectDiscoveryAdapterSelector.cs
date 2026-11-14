using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Chooses an adapter from crosswalk reports after validating evidence ambiguity.</summary>
public static class ProjectDiscoveryAdapterSelector
{
    public static IProjectDiscoveryAdapter Select(string repositoryRoot, CrosswalkStackReader crosswalk)
    {
        var report = crosswalk.Detect(repositoryRoot);
        CrosswalkEvidenceStackAmbiguity.ThrowIfAmbiguous(repositoryRoot, report.Evidence);
        return report.Stack switch
        {
            "go" when File.Exists(Path.Combine(repositoryRoot, "go.mod")) &&
                GoModReader.Read(Path.Combine(repositoryRoot, "go.mod")).Path is not null => new GoProjectDiscoveryAdapter(),
            "go" => throw new InvalidOperationException("No discovery adapter is available for stack 'go' (marker go.mod)."),
            "node" => new NodeProjectDiscoveryAdapter(),
            "dotnet" => new DotnetProjectDiscoveryAdapter(),
            _ => SelectCrosswalk(repositoryRoot, crosswalk, report)
        };
    }

    private static IProjectDiscoveryAdapter SelectCrosswalk(string root, CrosswalkStackReader crosswalk, CrosswalkStackReport report)
    {
        if (report.Stack == "lua")
            return new LuaProjectDiscoveryAdapter();
        var outcome = "not-run";
        if (report.Stack != CrosswalkStackReport.NoStack)
        {
            var list = crosswalk.List(root);
            if (list.Status == CrosswalkDetectOutcome.Detected && list.Result.Count > 0)
                return new CrosswalkProjectDiscoveryAdapter(report, list.Result.Count);
            outcome = list.Status == CrosswalkDetectOutcome.Detected ? "no-tests"
                : string.IsNullOrEmpty(list.Detail) ? list.Status : $"{list.Status}: {list.Detail}";
        }
        var source = new FactSource(measurementReference: "discovery:crosswalk");
        return new DeclaredProjectDiscoveryAdapter([
            new("crosswalk-stack", new(report.Stack, source, FactConfidence.High)),
            new("crosswalk-list-outcome", new(outcome, source, FactConfidence.High))
        ]);
    }
}
