using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: in-memory goals and per-test owned artifact roots; no processes or global state.
public sealed class FailedGateClassSelectionTests
{
    [Fact]
    public void NamedFailures_DeduplicateMethodsAndRowsByProjectAndClass()
    {
        var classes = FailedGateFocusedClasses.FromIdentities(
        [
            ("Infrastructure.Tests", "Example.BetaTests.First"),
            ("Infrastructure.Tests", "Example.BetaTests.Second(value: 42)"),
            ("Infrastructure.Tests", "Example.BetaTests.Second(value: 43)"),
            ("Core.Tests", "Example.AlphaTests<SomeType>.First"),
            ("Core.Tests", "Example.OuterTests+Nested.First"),
            ("Core.Tests", "Example.BetaTests.First"),
            (null, "Mcg.AgentOrchestrator.Infrastructure.Tests.NamespaceTests.First"),
            (null, "Unknown.Tests.NoProject.First"),
            ("Core.Tests", "not-an-identity"),
            ("Core.Tests", "Example.Invalid-Class.First"),
            ("Core.Tests", " ")
        ]);

        Assert.Equal(new[]
        {
            ("Core.Tests", "AlphaTests"), ("Core.Tests", "BetaTests"), ("Core.Tests", "OuterTests"),
            ("Infrastructure.Tests", "BetaTests"), ("Infrastructure.Tests", "NamespaceTests")
        }, classes);
    }

    [Fact]
    public void DeferredFailure_ReadsRetainedGateAndRequiredFailuresOnly()
    {
        using var fixture = new SoloCorrectionTestFixture();
        fixture.Record();
        fixture.Trx("Example.FailedTests.First");
        fixture.SaveResult(
            new("solo tests", false, 1, "failed", TestProjectPath: "Infrastructure.Tests",
                FailingTestIdentities: ["Example.FailedTests.First", "Example.FailedTests.Second(x: 2)"]),
            new("advisory", false, 1, "failed", Advisory: true, TestProjectPath: "Core.Tests",
                FailingTestIdentities: ["Example.AdvisoryTests.First"]),
            new("passed", true, 0, "ok", TestProjectPath: "Core.Tests",
                FailingTestIdentities: ["Example.PassedTests.First"]));
        fixture.Goal.DeferAcceptanceFailureForRetry();
        Assert.Null(fixture.Goal.LatestAcceptanceFailure);
        Assert.NotNull(fixture.Goal.RetainedAcceptanceFailure);
        var reads = 0;

        var classes = FailedGateFocusedClasses.Read(fixture.Goal, id =>
        {
            Assert.Equal(fixture.Goal.Id.Value, id);
            reads++;
            return fixture.Attempt;
        });

        Assert.Equal(1, reads);
        Assert.Equal(new[] { ("Infrastructure.Tests", "FailedTests") }, classes);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("unbound")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("passed")]
    [InlineData("summary-attempt")]
    [InlineData("summary-head")]
    [InlineData("artifact-head")]
    [InlineData("attempt-head")]
    [InlineData("wrong-goal")]
    [InlineData("focused-attempt")]
    [InlineData("focused-artifact")]
    [InlineData("null-identities")]
    [InlineData("empty-identities")]
    [InlineData("null-checks")]
    [InlineData("null-check")]
    public void UnusableFailure_PreservesOriginalNoApplicableContext(string scenario)
    {
        using var fixture = new SoloCorrectionTestFixture();
        fixture.Record();
        fixture.Trx("Example.FailedTests.First");
        fixture.SaveResult(new AcceptanceCheckResult("solo tests", false, 1, "failed", TestProjectPath: "Infrastructure.Tests",
            FailingTestIdentities: scenario == "null-identities" ? null :
                scenario == "empty-identities" ? [] : ["Example.FailedTests.First"]));
        var attempt = fixture.Attempt!;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var artifact = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
            File.ReadAllText(attempt.ResultPath), options)!;
        artifact = scenario switch
        {
            "passed" => artifact with { Acceptance = artifact.Acceptance! with { Passed = true } },
            "summary-attempt" => artifact with { Acceptance = artifact.Acceptance! with { AttemptId = "other" } },
            "summary-head" => artifact with { Acceptance = artifact.Acceptance! with { BranchHeadSha = "other" } },
            "artifact-head" => artifact with { MainHeadSha = "other" },
            "focused-artifact" => artifact with { DispatchKind = "pre-review-evidence" },
            _ => artifact
        };
        var artifactJson = JsonSerializer.SerializeToNode(artifact, options)!;
        // Corrupt the wire input after serialization so summary getters cannot fail fixture setup.
        if (scenario is "null-checks" or "null-check")
            artifactJson["acceptance"]!["unmetCriteria"] = scenario == "null-checks"
                ? null : new JsonArray((JsonNode?)null);
        File.WriteAllText(attempt.ResultPath, artifactJson.ToJsonString(options));
        switch (scenario)
        {
            case "absent": fixture.Kernel.ClearAcceptanceFailure(fixture.Goal.Id); break;
            case "unbound": attempt = attempt with { AttemptId = "other" }; break;
            case "attempt-head": attempt = attempt with { MainHeadSha = "other" }; break;
            case "wrong-goal": attempt = attempt with { GoalId = "other" }; break;
            case "focused-attempt": attempt = attempt with { Kind = "pre-review-evidence" }; break;
            case "missing": File.Delete(attempt.ResultPath); break;
            case "malformed": File.WriteAllText(attempt.ResultPath, "{"); break;
        }
        var classes = FailedGateFocusedClasses.Read(fixture.Goal, _ =>
        {
            Assert.NotEqual("absent", scenario);
            return attempt;
        });
        var context = NoPreReviewContext("candidate");

        Assert.Empty(classes);
        Assert.Same(context, PreReviewEvidenceContextBuilder.WithFailedGateClasses(context, classes));
    }

    [Fact]
    public void AttributionWithoutArtifact_UsesOnlyRetainedFailure()
    {
        var (kernel, goal) = SoftwareGoal();
        kernel.RecordAcceptanceFailure(goal.Id, ["gate"], "old", "main",
            [new("gate", AcceptanceFailureOrigin.Introduced,
                "merge train RED attributed (recorded) receipt first: Mcg.AgentOrchestrator.Core.Tests.OlderTests.Fail")]);
        kernel.RecordAcceptanceFailure(goal.Id, ["gate"], "new", "main",
            [new("gate", AcceptanceFailureOrigin.Introduced,
                "merge train RED attributed (recorded) receipt last: Mcg.AgentOrchestrator.Core.Tests.LatestTests.Fail")]);
        goal.DeferAcceptanceFailureForRetry();

        Assert.Equal(new[] { ("Core.Tests", "LatestTests") }, FailedGateFocusedClasses.Read(goal,
            _ => throw new InvalidOperationException("Unbound attribution must not read the attempt store")));
    }

    [Fact]
    public void Merge_SupplementsImpactCoverageWithStableDistinctSelections()
    {
        var original = FocusedPreReviewContext("candidate");
        var classes = new[]
        {
            ("Infrastructure.Tests", "ConductorDriverTestsExtra"), ("Core.Tests", "ConductorDriverTests"),
            ("Infrastructure.Tests", "ConductorDriverTests"), ("Infrastructure.Tests", "OtherTests"),
            ("Infrastructure.Tests", "OtherTests")
        };
        var merged = PreReviewEvidenceContextBuilder.WithFailedGateClasses(original, classes);

        Assert.False(merged.NoApplicableTests);
        Assert.False(merged.MappingNeedsInput);
        Assert.Equal(original.CandidateSha, merged.CandidateSha);
        Assert.Equal(original.SelectedFocusedTests[0], merged.SelectedFocusedTests[0]);
        Assert.Equal(original.FocusedRequest + "; Core.Tests: FullyQualifiedName~ConductorDriverTests; " +
            "Infrastructure.Tests: FullyQualifiedName~ConductorDriverTestsExtra|FullyQualifiedName~OtherTests", merged.FocusedRequest);
        var reordered = PreReviewEvidenceContextBuilder.WithFailedGateClasses(original, classes.Reverse().ToArray());
        Assert.Equal(merged.FocusedRequest, reordered.FocusedRequest);
        Assert.Equal(merged.SelectedFocusedTests, reordered.SelectedFocusedTests);
        Assert.Same(merged, PreReviewEvidenceContextBuilder.WithFailedGateClasses(merged, classes));
    }

    [Theory]
    [InlineData("Infrastructure.Tests: FullyQualifiedName ~ FailedTests")]
    [InlineData("Infrastructure.Tests: FullyQualifiedName\t~\tFailedTests")]
    public void ExistingClass_WithFilterWhitespace_IsNotAddedAgain(string selection)
    {
        var context = new PreReviewEvidenceContext("candidate", [selection], selection, "mapped", false, false);
        Assert.Same(context, PreReviewEvidenceContextBuilder.WithFailedGateClasses(context,
            [("Infrastructure.Tests", "FailedTests")]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BlockedContext_FailedClassesCannotBypassExistingRequirement(bool cleanup)
    {
        var context = NoPreReviewContext("candidate") with
        {
            MappingNeedsInput = !cleanup,
            SourceCleanupPaths = cleanup ? ["bin/generated.dll"] : null
        };
        Assert.Same(context, PreReviewEvidenceContextBuilder.WithFailedGateClasses(context, [("Core.Tests", "FailedTests")]));
    }

    [Fact]
    public void LargeFailureSet_SplitsBrokerRequestsWithoutDroppingClasses()
    {
        var classes = Enumerable.Range(0, 100).Select(index => ("Core.Tests", $"FailedTests{index:D3}")).ToArray();
        var merged = PreReviewEvidenceContextBuilder.WithFailedGateClasses(NoPreReviewContext("candidate"), classes);
        Assert.True(merged.SelectedFocusedTests.Count > 1);
        Assert.Equal(string.Join("; ", merged.SelectedFocusedTests), merged.FocusedRequest);
        Assert.All(merged.SelectedFocusedTests, item => Assert.True(item.Length - "Core.Tests:".Length <= 1024));
        Assert.Equal(classes.Select(item => "FullyQualifiedName~" + item.Item2), merged.SelectedFocusedTests
            .SelectMany(item => item["Core.Tests: ".Length..].Split('|')));
    }
}
