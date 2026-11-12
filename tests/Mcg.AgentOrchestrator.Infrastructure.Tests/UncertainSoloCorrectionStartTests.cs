using Mcg.AgentOrchestrator.App.Application;

public sealed class UncertainSoloCorrectionStartTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyCurrentDispatchClaimEstablishesUncertainty(bool confirmed)
    {
        var f = new DispatchStartRecoveryTestFixture();
        f.Prepare();
        f.ClaimCurrent(confirmed);
        Assert.True(GoalDispatchOperations.HasUncertainSoloCorrectionStart(f.Task));
        f.SettleAndAnswer();
        f.Prepare();
        Assert.False(GoalDispatchOperations.HasUncertainSoloCorrectionStart(f.Task));
        f.Kernel.DiscardUnstartedPreparedDispatch(f.GoalId, f.TaskId, f.Task.LastDispatch!.DispatchedAt, "Discard fixture preparation");
        Assert.False(GoalDispatchOperations.HasUncertainSoloCorrectionStart(f.Task));
    }
}
