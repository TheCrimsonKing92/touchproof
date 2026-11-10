using System.Text;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerSpecificationDeliveryTests
{
    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Planner)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    [Xunit.InlineData(AgentRole.Tester)]
    public void KernelThroughDispatcherDeliversOneCompleteCurrentView(AgentRole role)
    {
        using var fixture = WorkerSpecificationComparisonTests.CreateFixture(role, true);
        var source = Artifact(fixture.Package, "goal/refined-spec.json");
        var current = Artifact(fixture.Package, "goal/current-specification.json");
        Assert.Equal(ContextDeliveryMode.OnDemandFile, source.DeliveryMode);
        Assert.Equal(ContextDeliveryMode.InlineFull, current.DeliveryMode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(source.AuthoritativeBytes!), WorkerSpecificationComparisonTests.Expand(current.AuthoritativeBytes!)));
        var body = fixture.Goal.RefinedSpec!.Decisions.Single().Choice;
        Assert.Equal(1, Encoding.UTF8.GetString(current.AuthoritativeBytes!).Split(System.Text.Json.JsonSerializer.Serialize(body)[1..^1], StringSplitOptions.None).Length - 1);
        Assert.Contains("Unique brief requirement", fixture.Prompt, StringComparison.Ordinal);
        Assert.Contains("Preserve unique operations.", fixture.Prompt, StringComparison.Ordinal);
        Assert.Equal([role], current.RoleVisibility);
        Assert.Equal(1, fixture.Prompt.Split("INLINE FULL: identity=goal/current-specification.json;", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("INLINE FULL: identity=goal/refined-spec.json;", fixture.Prompt, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing", "typed-materialization-unavailable")]
    [Xunit.InlineData("corrupt", "typed-identity-hash-mismatch")]
    [Xunit.InlineData("stale", "typed-identity-hash-mismatch")]
    [Xunit.InlineData("directory", "typed-materialization-unavailable")]
    public void RequiredSourceFailureCannotBeHiddenByCurrentView(string failure, string reason)
    {
        using var fixture = WorkerSpecificationComparisonTests.CreateFixture(AgentRole.Developer, true);
        var artifact = Artifact(fixture.Package, "goal/refined-spec.json");
        var path = Path.Combine(fixture.Root, artifact.MandatoryRelativePath!);
        if (failure is "missing" or "directory") File.Delete(path);
        if (failure == "directory") Directory.CreateDirectory(path);
        if (failure is "corrupt" or "stale") File.WriteAllText(path, failure == "stale" ? "old candidate spec" : "invalid");
        var error = Assert.Throws<WorkerContextPreparationException>(() => WorkerContextRenderer.Render(
            fixture.Source, fixture.Package, fixture.Root, WorkerContextDeliveryPolicy.Initial));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(artifact.Identity, error.Identity);
    }

    [Xunit.Fact]
    public void SupersededVersionsRemainActualRecordsAndCurrentCriteriaStayExact()
    {
        using var fixture = WorkerSpecificationComparisonTests.CreateFixture(AgentRole.Developer, true);
        var old = fixture.Goal.RefinedSpec!;
        fixture.Kernel.SetGoalRefinedSpec(fixture.Goal.Id, old with { AcceptanceCriteria = ["revised exact criterion"] });
        var source = fixture.Kernel.BuildTaskBriefSource(fixture.Goal.Id, fixture.Task.Id);
        var package = WorkerProfileDispatcher.BuildContextPackage(fixture.Goal, fixture.Task, fixture.Root, fixture.Root,
            source.ToTaskBrief(""), typedSource: source);
        var view = WorkerSpecificationComparisonTests.Expand(Artifact(package, "goal/current-specification.json").AuthoritativeBytes!);
        Assert.Equal("revised exact criterion", view["AcceptanceCriteria"]![0]!.GetValue<string>());
        var history = Artifact(package, "goal/specification-history.json");
        Assert.Equal(ContextDeliveryMode.OnDemandFile, history.DeliveryMode);
        var versions = JsonNode.Parse(history.AuthoritativeBytes!)!["RefinedSpecVersions"]!.AsArray();
        Assert.Equal(3, versions.Count);
        Assert.Equal(2, versions[0]!["SupersededByVersion"]!.GetValue<int>());
        Assert.Equal(3, versions[1]!["SupersededByVersion"]!.GetValue<int>());
        Assert.Null(versions[2]!["SupersededByVersion"]);
        Assert.Equal(old.AcceptanceCriteria[0], versions[1]!["Spec"]!["AcceptanceCriteria"]![0]!.GetValue<string>());
    }

    [Xunit.Fact]
    public void CompactConsumerKeepsOriginalInlineSourceAndPackageIdentity()
    {
        using var fixture = WorkerSpecificationComparisonTests.CreateFixture(AgentRole.Developer, false);
        Assert.Equal(ContextDeliveryMode.InlineFull, Artifact(fixture.Package, "goal/refined-spec.json").DeliveryMode);
        Assert.DoesNotContain(fixture.Package.Artifacts, artifact => artifact.Identity.Value == "goal/current-specification.json");
        var rendered = WorkerContextRenderer.Render(fixture.Source, fixture.Package, fixture.Root, WorkerContextDeliveryPolicy.Initial);
        Assert.Equal(fixture.Prompt, rendered);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public void EveryDeliveryPolicyPreservesCurrentSpecification(int policy)
    {
        using var fixture = WorkerSpecificationComparisonTests.CreateFixture(AgentRole.Developer, true);
        var rendered = WorkerContextRenderer.Render(fixture.Source, fixture.Package, fixture.Root, (WorkerContextDeliveryPolicy)policy);
        Assert.Equal(fixture.Prompt, rendered);
        Assert.Equal(1, rendered.Split("INLINE FULL: identity=goal/current-specification.json;", StringSplitOptions.None).Length - 1);
    }

    [Xunit.Fact]
    public void CompletePlannerPlanStaysVerifiedAndReadableAlongsideCurrentView()
    {
        using var fixture = WorkerSpecificationComparisonTests.CreateFixture(AgentRole.Developer, true);
        var plan = "Complete Planner evidence\n" + new string('p', 20000) + "\nunique-plan-tail";
        var bytes = Encoding.UTF8.GetBytes(plan);
        File.WriteAllBytes(Path.Combine(fixture.Root, "planner-plan.md"), bytes);
        File.WriteAllText(Path.Combine(fixture.Root, "artifact-registry.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            artifacts = new[] { new { Path = "planner-plan.md", Exists = true, Sha256 = WorkerContextArtifact.Hash(bytes),
                RoleVisibility = new[] { "Developer", "Tester", "Reviewer" }, HashVerified = true } }
        }));
        var package = WorkerProfileDispatcher.BuildContextPackage(fixture.Goal, fixture.Task, fixture.Root, fixture.Root,
            fixture.Source.ToTaskBrief(""), typedSource: fixture.Source);
        var artifact = Artifact(package, "context/planner-plan.md");
        Assert.Equal(ContextDeliveryMode.MandatoryFile, artifact.DeliveryMode);
        Assert.Equal(bytes, artifact.AuthoritativeBytes);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Root, artifact.MandatoryRelativePath!)));
        var rendered = WorkerContextRenderer.Render(fixture.Source, package, fixture.Root, WorkerContextDeliveryPolicy.Initial);
        Assert.Contains("MANDATORY READ: identity=context/planner-plan.md;", rendered, StringComparison.Ordinal);
        Assert.Contains("INLINE FULL: identity=goal/current-specification.json;", rendered, StringComparison.Ordinal);
    }

    private static WorkerContextArtifact Artifact(WorkerContextPackage package, string identity) =>
        Assert.Single(package.Artifacts, artifact => artifact.Identity.Value == identity);
}
