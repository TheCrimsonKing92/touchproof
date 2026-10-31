using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.ProcessSpawning)]
public sealed class IcaclsIntegrityLabelerTestsAcceptanceGateMergeTrainIntegrityTests : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Materialize_LowParent_CheckRunsMediumInTrainAndPartition(bool partition)
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        using var fixture = new AcceptanceGateIntegrityTestSupport();
        await fixture.AssertMediumHostAsync();
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "first.txt", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "second.txt", "second");
            var parent = Directory.CreateDirectory(Path.Combine(repo, GoalWorktrees.DirectoryName)).FullName;
            Assert.True(fixture.Labels.SetIntegrity(parent, "(OI)(CI)L", recursive: true));
            Assert.True(fixture.Labels.Query(parent).Low);
            if (partition)
            {
                using var workspace = GoalWorktrees.CreateAcceptancePartitionWorkspace(repo, main,
                    Bind(first.GoalId, first.Revision, "first.txt", "first"), TrunkBranchName.Default);
                Assert.True(File.Exists(Path.Combine(workspace.Path, "first.txt")));
                Assert.True(fixture.Labels.Query(workspace.Path).Medium);
                await fixture.AssertMediumProbeAsync(workspace.Path);
            }
            else
            {
                using var workspace = GoalWorktrees.CreateMergeTrainWorkspace(repo, main,
                    [TrainBind(first.GoalId, first.Revision, "first.txt", "first"),
                     TrainBind(second.GoalId, second.Revision, "second.txt", "second")], TrunkBranchName.Default);
                Assert.Equal(2, workspace.Members.Count);
                Assert.Empty(workspace.Ejections);
                Assert.True(File.Exists(Path.Combine(workspace.Path, "first.txt")));
                Assert.True(File.Exists(Path.Combine(workspace.Path, "second.txt")));
                Assert.True(fixture.Labels.Query(workspace.Path).Medium);
                await fixture.AssertMediumProbeAsync(workspace.Path);
            }
            Assert.True(fixture.Labels.Query(parent).Medium);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
