using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OwnerProtectedConfigurationPolicyTests : IDisposable
{
    private const string Manifest = "config/acceptance-manifest.json";
    private const string Policy = "config/conductor-policy.json";
    private const string Main = """{"engine":{"maxConcurrentShards":2}}""";
    private const string Candidate = """{"engine":{"maxConcurrentShards":3}}""";
    private const string EquivalentMain = """
        {"version":1,"engine":{"maxConcurrentShards":2,"infrastructureTestLanes":[
          {"name":"Spawn","filter":"FullyQualifiedName~Alpha|FullyQualifiedName~Beta"},
          {"name":"Remainder","filter":"FullyQualifiedName~Other"}],
          "localTestPartitions":[{"name":"P","laneNames":["Spawn","Remainder"]}]}}
        """;
    private const string EquivalentCandidate = """
        {"version":1,"engine":{"maxConcurrentShards":2,"infrastructureTestLanes":[
          {"name":"Spawn","filter":"FullyQualifiedName~Alpha"},
          {"name":"Local","filter":"FullyQualifiedName~Beta","ownedCollections":["LocalCol"],"exclusiveResourceKeys":["xunit:LocalCol"]},
          {"name":"Remainder","filter":"FullyQualifiedName~Other"}],
          "localTestPartitions":[{"name":"P","laneNames":["Spawn","Local","Remainder"]}]}}
        """;
    private static readonly string MemberSha = new('a', 40);
    private static readonly string OtherSha = new('b', 40);
    private static readonly string CombinedSha = new('c', 40);
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    // Expectations preserve the pre-extraction verifier's approval and equivalence decisions.
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Evaluate_ProtectedChange_RequiresOwnerApproval(bool approve)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Manifest change", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var decisions = CollaborationItemStore.ForDirectory(_root);
        WriteCandidate(Manifest, Candidate);
        if (approve) await ApproveAsync(kernel, goal, decisions, MemberSha);

        var result = OwnerProtectedConfigurationPolicy.EvaluateOwnerProtectedConfigurationForTests(_root, goal.Id,
            [Manifest], null, (_, args) => args[0] == "diff" ? Manifest + "\n" : Main,
            decisions, Inventory, MemberSha);

        Assert.Null(result.Pass);
        Assert.Equal(approve, result.OwnerApprovalSatisfied);
        if (approve) Assert.Null(result.Failure);
        else Assert.Equal(new AcceptanceCheckResult("owner-protected configuration", false, 1,
                "config/acceptance-manifest.json: changed field(s) engine.maxConcurrentShards; owner decision required for this candidate",
                ResultSummary: "operator review required"), result.Failure);
    }

    [Xunit.Fact]
    public void Evaluate_IdenticalManifest_IsUnchanged()
    {
        WriteCandidate(Manifest, Main);
        var result = OwnerProtectedConfigurationPolicy.EvaluateOwnerProtectedConfigurationForTests(_root, null,
            [Manifest], null, (_, args) => args[0] == "diff" ? Manifest + "\n" : Main,
            null, Inventory, MemberSha);

        Assert.Null(result.Failure);
        Assert.Null(result.Pass);
        Assert.False(result.OwnerApprovalSatisfied);
    }

    [Xunit.Fact]
    public void Evaluate_EquivalentPartition_PassesWithoutOwnerApproval()
    {
        WriteCandidate(Manifest, EquivalentCandidate);
        var result = OwnerProtectedConfigurationPolicy.EvaluateOwnerProtectedConfigurationForTests(_root, null,
            [Manifest], null, (_, args) => args[0] == "diff" ? Manifest + "\n" : EquivalentMain,
            null, Inventory, MemberSha);

        Assert.Null(result.Failure);
        Assert.Equal(new AcceptanceCheckResult("owner-protected configuration", true, 0,
            "partition-equivalent", ResultSummary: "partition-equivalent"), result.Pass);
        Assert.False(result.OwnerApprovalSatisfied);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Evaluate_Cohort_RequiresApprovalForEveryProtectedMember(bool approveOther)
    {
        var kernel = new AgentOrchestratorKernel();
        var member = kernel.CreateGoal("Manifest member", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var other = kernel.CreateGoal("Policy member", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var decisions = CollaborationItemStore.ForDirectory(_root);
        WriteCandidate(Manifest, Candidate);
        WriteCandidate(Policy, """{"enabled":true}""");
        await ApproveAsync(kernel, member, decisions, MemberSha);
        if (approveOther) await ApproveAsync(kernel, other, decisions, OtherSha);
        string? Git(string _, string[] args) => args[0] switch
        {
            "show" => args[1] switch
            {
                "main:" + Manifest => Main,
                "main:" + Policy => """{"enabled":false}""",
                var key when key == MemberSha + ":" + Manifest || key == "HEAD:" + Manifest => Candidate,
                var key when key == OtherSha + ":" + Policy || key == "HEAD:" + Policy => """{"enabled":true}""",
                _ => null
            },
            "diff" => args[3] switch
            {
                "main...HEAD" => Manifest + "\n" + Policy + "\n",
                var key when key == "main..." + MemberSha => Manifest + "\n",
                var key when key == "main..." + OtherSha => Policy + "\n",
                _ => null
            },
            _ => null
        };

        var result = OwnerProtectedConfigurationPolicy.EvaluateOwnerProtectedConfigurationForTests(_root, null,
            [Manifest, Policy], [new(member.Id, MemberSha), new(other.Id, OtherSha)],
            Git, decisions, Inventory, CombinedSha);

        Assert.Null(result.Pass);
        Assert.Equal(approveOther, result.OwnerApprovalSatisfied);
        if (approveOther) Assert.Null(result.Failure);
        else
        {
            var failure = Assert.IsType<AcceptanceCheckResult>(result.Failure);
            Assert.Equal("owner-protected configuration", failure.Name);
            Assert.False(failure.Passed);
            Assert.Equal(1, failure.ExitCode);
            Assert.Equal("operator review required", failure.ResultSummary);
        }
    }

    private async Task ApproveAsync(AgentOrchestratorKernel kernel, Goal goal,
        ICollaborationItemStore decisions, string sha)
    {
        var intents = new SqliteOperatorIntentStore(Path.Combine(_root, "operator-intents.db"), Path.Combine(_root, "logs"));
        await intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            OperatorIntentVerbs.ApprovePolicyChange, goal.Id.Value, null,
            JsonSerializer.Serialize(new ApprovePolicyChangeOperatorIntentPayload(sha, "Owner approved candidate.")),
            [], "owner", "cli", "local-process", DateTimeOffset.UtcNow, ActorKind: OperatorActorKind.Human));
        new OperatorIntentCoordinator(intents, decisions: decisions, goalStateVersionResolver: _ => 0).ExecutePending(kernel, goal);
    }

    private void WriteCandidate(string path, string text)
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, path), text);
    }

    private static AcceptanceTestInventory Inventory() => new(
        [new("Tests.Alpha", "SpawnCol"), new("Tests.Beta", "LocalCol"), new("Tests.Other", null)],
        new HashSet<string>(["SpawnCol", "LocalCol"], StringComparer.Ordinal),
        new HashSet<string>(["LocalCol"], StringComparer.Ordinal));

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
