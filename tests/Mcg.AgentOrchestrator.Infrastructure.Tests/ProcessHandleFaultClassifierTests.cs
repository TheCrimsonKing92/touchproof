using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ProcessHandleFaultClassifierTests
{
    private const string Message = "Process was not started by this object, so requested information cannot be determined.";

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public void ClassifiesThroughEightInnerExceptionLevels(int depth, bool bounded)
    {
        Exception fault = new InvalidOperationException(Message);
        for (var level = 0; level < depth; level++)
        {
            fault = new Exception("gate wrapper", fault);
        }

        Assert.Equal(
            bounded ? ProcessHandleFaultDisposition.BoundedRetry : ProcessHandleFaultDisposition.NotEnvironmental,
            ProcessHandleFaultClassifier.Classify(fault));
    }

    [Theory]
    [InlineData("Process has exited")]
    [InlineData("process was not started by this object")]
    [InlineData(" Process was not started by this object")]
    public void RejectsOtherMessages(string message) =>
        Assert.Equal(ProcessHandleFaultDisposition.NotEnvironmental,
            ProcessHandleFaultClassifier.Classify(new InvalidOperationException(message)));

    [Fact]
    public void RequiresTheTypeAndPrefixTogether()
    {
        Assert.Equal(ProcessHandleFaultDisposition.NotEnvironmental, ProcessHandleFaultClassifier.Classify(null));
        Assert.Equal(ProcessHandleFaultDisposition.NotEnvironmental,
            ProcessHandleFaultClassifier.Classify(new ArgumentException(Message)));
        Assert.Equal(ProcessHandleFaultDisposition.BoundedRetry,
            ProcessHandleFaultClassifier.Classify(
                new ObjectDisposedException(null, "Process was not started by this object")));
        Assert.Equal(ProcessHandleFaultDisposition.NotEnvironmental,
            ProcessHandleFaultClassifier.Classify(new AggregateException(
                "gate wrapper", new Exception("first fault"), new InvalidOperationException(Message))));
    }
}
