using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: reads the verified repository tree and uses the scanner's synchronized extract cache.
[LiveTreeStructural]
public sealed class GateObligationSelectorDeclaredInventoryRepositoryTests
{
    [Fact]
    public void DeclaredInventory_ContainsRepositoryTestWithSharedCollection()
    {
        var inventory = GateObligationSelectorPreAdmissionClassifier.ReadDeclaredInventory(VerifiedRepositoryRoot.Find());

        Assert.NotEmpty(inventory);
        var test = Assert.Single(inventory, item => item.FullName == nameof(AcceptanceGateEngineSettingsTests));
        Assert.Equal(TestCollections.GoalAcceptanceVerifier, test.Collection);
        Assert.False(test.CollectionUnknown);
    }
}
