using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its temporary filesystem tree and removes it in finally.
public sealed class PlannerContractAmbiguousCitationRouteTests : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("Program.cs", "src/First/Program.cs", "src/Second/Program.cs",
        "'src/First/Program.cs', 'src/Second/Program.cs'")]
    [Xunit.InlineData("Program.cs:472", "src/First/Program.cs", "src/Second/Program.cs",
        "'src/First/Program.cs', 'src/Second/Program.cs'")]
    [Xunit.InlineData("...gram.cs:472", "src/First/Program.cs", "src/Second/Program.cs",
        "'src/First/Program.cs', 'src/Second/Program.cs'")]
    [Xunit.InlineData("Duplicate.cs", "a;dir/Duplicate.cs", "z'dir/Duplicate.cs",
        "'a;dir/Duplicate.cs', `z'dir/Duplicate.cs`")]
    [Xunit.InlineData("Duplicate.cs", "Duplicate.cs", "src/Duplicate.cs",
        "'./Duplicate.cs', 'src/Duplicate.cs'")]
    public void Real_ambiguous_rejection_routes_to_both_full_paths(
        string citation, string first, string second, string expectedAlternatives)
    {
        var root = CreateTempDirectory();
        try
        {
            string[] candidates = [first, second];
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(root, candidate);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "// citation fixture");
            }
            StagePlannerCitationFixtures(root, candidates);

            var plan = PlanWithTargetCitation(citation);
            Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(
                plan, root, out _, out var rejection));
            Xunit.Assert.StartsWith($"target citation '{citation}' is ambiguous;", rejection);
            foreach (var candidate in candidates)
                Xunit.Assert.Contains($"'{candidate}'", rejection);

            var stderrPath = Path.Combine(root, "planner.stderr.txt");
            Xunit.Assert.True(PlannerOutputContract.TryPersistRejectionDiagnostic(
                stderrPath, rejection, out var persistenceDiagnostic), persistenceDiagnostic);
            var evidence = File.ReadAllText(stderrPath);
            Xunit.Assert.Contains(rejection, evidence);

            var trigger = new ConductorStewardTrigger(
                "goal", "planner-task", "sha", ConductorStewardTriggerKind.PlannerOutputContractRejected,
                DateTimeOffset.UnixEpoch, evidence, "", ["planner-rule=planner-output-contract-rejected"],
                ["Repair ambiguous citation"]);
            var output = new ConductorStewardDeterministicRoute().TryBuild(trigger, root);

            Xunit.Assert.NotNull(output);
            var route = ConductorStewardAdjudicationParser.Parse(output);
            Xunit.Assert.Equal("route", route.Kind);
            Xunit.Assert.Equal("ContractClarification", route.Cause);
            Xunit.Assert.Equal(trigger.TaskId, route.TargetTaskId);
            Xunit.Assert.Contains($"Offending ambiguous target citation: '{citation}'", route.Text);
            Xunit.Assert.Contains(
                $"Cite exactly one of these full repository-relative paths on the same mapping line: {expectedAlternatives}.",
                route.Text);
            foreach (var candidate in candidates)
            {
                var explicitPath = candidate.Contains('/') ? candidate : "./" + candidate;
                Xunit.Assert.True(PlannerOutputContract.TryValidatePlan(
                    PlanWithTargetCitation(explicitPath), root, out _, out var diagnostic), diagnostic);
            }
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void AmbiguousRejectionPrescribesOnlyUsablePathsWithinTheCap(bool hasUsablePath)
    {
        const string citation = "Duplicate.cs:43";
        string[] candidates = ["a/Duplicate.cs", "b/Duplicate.cs", "c/Duplicate.cs"];
        var root = CreateTempDirectory();
        try
        {
            foreach (var candidate in candidates)
            {
                var file = Path.Combine(root, candidate);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "// citation fixture");
            }
            StagePlannerCitationFixtures(root, candidates);
            foreach (var unavailable in hasUsablePath ? candidates[..2] : candidates)
                File.Delete(Path.Combine(root, unavailable));

            Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(
                PlanWithTargetCitation(citation), root, out _, out var rejection));
            Xunit.Assert.StartsWith($"target citation '{citation}' is ambiguous;", rejection);
            var route = RoutePersistedRejection(root, rejection);
            Xunit.Assert.Contains($"Offending ambiguous target citation: '{citation}'", route.Text);
            if (hasUsablePath)
            {
                Xunit.Assert.Contains("'a/Duplicate.cs', 'c/Duplicate.cs'; source span", rejection);
                Xunit.Assert.Contains("Cite exactly one of these full repository-relative paths on the same mapping line: 'c/Duplicate.cs'.", route.Text);
                Xunit.Assert.True(PlannerOutputContract.TryValidatePlan(
                    PlanWithTargetCitation("c/Duplicate.cs:43"), root, out _, out var diagnostic), diagnostic);
            }
            else
            {
                Xunit.Assert.Contains("Check the tracked matches 'a/Duplicate.cs', 'b/Duplicate.cs'.", route.Text);
                Xunit.Assert.DoesNotContain("Cite exactly one of these", route.Text);
            }
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Xunit.Fact]
    public void AmbiguousDocumentRejectionPrescribesPathAcceptedByTargetSection()
    {
        const string citation = "Guide.md:43";
        string[] candidates = ["a/Guide.md", "b/Guide.md", "docs/Guide.md"];
        var root = CreateTempDirectory();
        try
        {
            foreach (var candidate in candidates)
            {
                var file = Path.Combine(root, candidate);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "# Citation fixture");
            }
            StagePlannerCitationFixtures(root, candidates);

            Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(
                PlanWithTargetCitation(citation), root, out _, out var rejection));
            Xunit.Assert.StartsWith($"target citation '{citation}' is ambiguous;", rejection);
            var route = RoutePersistedRejection(root, rejection);
            Xunit.Assert.Contains($"Offending ambiguous target citation: '{citation}'", route.Text);
            Xunit.Assert.Contains(
                "Cite exactly one of these full repository-relative paths on the same mapping line: 'docs/Guide.md'.",
                route.Text);
            foreach (var candidate in candidates[..2])
            {
                Xunit.Assert.DoesNotContain(candidate, route.Text);
                Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(
                    PlanWithTargetCitation(candidate + ":43"), root, out _, out var invalidDiagnostic));
                Xunit.Assert.Contains("target seams and symbols", invalidDiagnostic);
            }
            Xunit.Assert.True(PlannerOutputContract.TryValidatePlan(
                PlanWithTargetCitation("docs/Guide.md:43"), root, out _, out var diagnostic), diagnostic);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Xunit.Fact]
    public void AmbiguousRejectionPrescribesPathThatPreservesSiblingNewFileDeclaration()
    {
        const string citation = "Guide.cs:43";
        string[] candidates = ["a/Guide.cs", "b/Guide.cs", "docs/Guide.cs"];
        var root = CreateTempDirectory();
        try
        {
            foreach (var candidate in candidates)
            {
                var file = Path.Combine(root, candidate);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "// citation fixture");
            }
            StagePlannerCitationFixtures(root, candidates);

            var plan = PlanWithTargetCitation(citation, " and `NewSibling.cs`").Replace(
                "The premise is valid", "Create `docs/NewSibling.cs` (new file). The premise is valid",
                StringComparison.Ordinal);
            Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(plan, root, out _, out var rejection));
            Xunit.Assert.StartsWith($"target citation '{citation}' is ambiguous;", rejection);

            foreach (var candidate in candidates[..2])
            {
                var invalidRepair = plan.Replace(citation, candidate + ":43", StringComparison.Ordinal);
                Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(invalidRepair, root, out _, out var invalidDiagnostic));
                Xunit.Assert.StartsWith("target citation 'NewSibling.cs' does not exist and is not marked as a new file;",
                    invalidDiagnostic);
            }

            var validRepair = plan.Replace(citation, "docs/Guide.cs:43", StringComparison.Ordinal);
            Xunit.Assert.True(PlannerOutputContract.TryValidatePlan(validRepair, root, out _, out var diagnostic), diagnostic);

            var route = RoutePersistedRejection(root, rejection);
            Xunit.Assert.Contains($"Offending ambiguous target citation: '{citation}'", route.Text);
            Xunit.Assert.Contains(
                "Cite exactly one of these full repository-relative paths on the same mapping line: 'docs/Guide.cs'.",
                route.Text);
            foreach (var candidate in candidates[..2])
                Xunit.Assert.DoesNotContain(candidate, route.Text);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Xunit.Fact]
    public void AmbiguousRejectionPrescribesPathsWhenSiblingCitationIsAlsoAmbiguous()
    {
        const string firstCitation = "Program.cs:43";
        const string secondCitation = "AssemblyInfo.cs:43";
        string[] candidates =
        [
            "src/First/Program.cs", "src/Second/Program.cs",
            "src/First/AssemblyInfo.cs", "src/Second/AssemblyInfo.cs"
        ];
        var root = CreateTempDirectory();
        try
        {
            foreach (var candidate in candidates)
            {
                var file = Path.Combine(root, candidate);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "// citation fixture");
            }
            StagePlannerCitationFixtures(root, candidates);

            var plan = PlanWithTargetCitation(firstCitation, $" and `{secondCitation}`");
            Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(plan, root, out _, out var rejection));
            Xunit.Assert.StartsWith($"target citation '{firstCitation}' is ambiguous;", rejection);
            var route = RoutePersistedRejection(root, rejection);
            Xunit.Assert.Contains($"Offending ambiguous target citation: '{firstCitation}'", route.Text);
            Xunit.Assert.Contains(
                "Cite exactly one of these full repository-relative paths on the same mapping line: 'src/First/Program.cs', 'src/Second/Program.cs'.",
                route.Text);

            var partialRepair = PlanWithTargetCitation("src/First/Program.cs:43", $" and `{secondCitation}`");
            Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(partialRepair, root, out _, out var remainingRejection));
            Xunit.Assert.StartsWith($"target citation '{secondCitation}' is ambiguous;", remainingRejection);
            var repairedPlan = PlanWithTargetCitation("src/First/Program.cs:43", " and `src/Second/AssemblyInfo.cs:43`");
            Xunit.Assert.True(PlannerOutputContract.TryValidatePlan(repairedPlan, root, out _, out var diagnostic), diagnostic);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("...Target.cs:43", "src/TrackedTarget.cs", true)]
    [Xunit.InlineData("...Target.cs:43", "src/has`tick/TrackedTarget.cs", false)]
    [Xunit.InlineData("...Target's.cs:43", "src/TrackedTarget's.cs", true)]
    public void UnavailableTrackedMatchRoutesWithCanonicalPath(string citation, string canonicalPath, bool deleteTrackedFile)
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, canonicalPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "// tracked citation fixture");
            StagePlannerCitationFixtures(root, canonicalPath);
            if (deleteTrackedFile) File.Delete(path);

            Xunit.Assert.False(PlannerOutputContract.TryValidatePlan(
                PlanWithTargetCitation(citation), root, out _, out var rejection));
            var route = RoutePersistedRejection(root, rejection);
            Xunit.Assert.Contains($"Offending target citation: '{citation}'", route.Text);
            Xunit.Assert.Contains($"The tracked path '{canonicalPath}' is unavailable", route.Text);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static ConductorStewardAdjudication RoutePersistedRejection(string root, string rejection)
    {
        var stderrPath = Path.Combine(root, "planner.stderr.txt");
        Xunit.Assert.True(PlannerOutputContract.TryPersistRejectionDiagnostic(
            stderrPath, rejection, out var persistenceDiagnostic), persistenceDiagnostic);
        var trigger = new ConductorStewardTrigger(
            "goal", "planner-task", "sha", ConductorStewardTriggerKind.PlannerOutputContractRejected,
            DateTimeOffset.UnixEpoch, File.ReadAllText(stderrPath), "", ["planner-rule=planner-output-contract-rejected"],
            ["Repair unavailable citation"]);

        var output = new ConductorStewardDeterministicRoute().TryBuild(trigger, root);

        Xunit.Assert.NotNull(output);
        var route = ConductorStewardAdjudicationParser.Parse(output);
        Xunit.Assert.Equal("ContractClarification", route.Cause);
        return route;
    }

    private static string PlanWithTargetCitation(string citation, string sameLineCitations = "")
    {
        var plan = PlannerContractPlanFixture().ReplaceLineEndings("\n");
        const string heading = "## Target seams and symbols";
        var headingStart = plan.IndexOf(heading, StringComparison.Ordinal);
        Xunit.Assert.True(headingStart >= 0, "Fixture must contain the target section.");
        var bodyStart = headingStart + heading.Length;
        var bodyEnd = plan.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        Xunit.Assert.True(bodyEnd > bodyStart, "Fixture must contain the following section.");
        return plan[..bodyStart] + $"\n\n- Extend `{citation}`{sameLineCitations} with focused ambiguity coverage.\n" + plan[bodyEnd..];
    }
}
