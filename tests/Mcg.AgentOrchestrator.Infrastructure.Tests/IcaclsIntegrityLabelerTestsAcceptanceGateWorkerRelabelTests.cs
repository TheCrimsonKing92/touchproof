using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.ProcessSpawning)]
public sealed class IcaclsIntegrityLabelerTestsAcceptanceGateWorkerRelabelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prepare_AfterGateNormalization_InvalidatesReceiptAndRestoresLow(bool explicitFileLabel)
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        using var fixture = new AcceptanceGateIntegrityTestSupport();
        var sandbox = Path.Combine(fixture.Worktree, ".mcg-sandbox");
        var child = Directory.CreateDirectory(Path.Combine(fixture.Worktree, "output")).FullName;
        var existingFile = Path.Combine(child, "existing.txt");
        File.WriteAllText(existingFile, "worker output");
        var preparer = new WorkerSandboxPreparer(fixture.Labels);
        Assert.Null(preparer.Prepare(fixture.Worktree, sandbox).RecoveryAction);
        Assert.True(preparer.Prepare(fixture.Worktree, sandbox).PrepReceiptHit);
        if (explicitFileLabel)
            Assert.True(fixture.Labels.SetIntegrity(existingFile, "L", recursive: false));
        Assert.True(fixture.Labels.Query(existingFile).Low);

        AcceptanceGateIntegrityNormalizer.Normalize(fixture.Worktree);

        Assert.True(fixture.Labels.Query(fixture.Worktree).Medium);
        Assert.True(fixture.Labels.Query(existingFile).Medium);
        Assert.True(fixture.Labels.Query(sandbox).Low);
        Assert.True(File.Exists(Path.Combine(fixture.Worktree, WorkerSandboxPreparer.ReceiptFileName)));
        var gateOutput = Path.Combine(child, "gate-output.txt");
        File.WriteAllText(gateOutput, "gate output");
        Assert.True(fixture.Labels.Query(gateOutput).Medium);

        var nextDispatch = preparer.Prepare(fixture.Worktree, sandbox);

        Assert.Null(nextDispatch.RecoveryAction);
        Assert.False(nextDispatch.PrepReceiptHit);
        Assert.True(nextDispatch.WorktreeRecursiveRelabel);
        foreach (var directory in new[] { fixture.Worktree, child, sandbox })
        {
            var state = fixture.Labels.Query(directory);
            Assert.True(state.Low, directory);
            Assert.True(state.Inheritable, directory);
            Assert.False(state.Medium, directory);
        }
        Assert.True(fixture.Labels.Query(existingFile).Low);
        Assert.True(fixture.Labels.Query(gateOutput).Low);
        Assert.True(preparer.Prepare(fixture.Worktree, sandbox).PrepReceiptHit);
    }
}
