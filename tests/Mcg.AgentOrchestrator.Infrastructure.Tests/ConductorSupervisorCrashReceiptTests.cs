using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns its receipt and append delegate, with no process-wide subscriptions.
public sealed class ConductorSupervisorCrashReceiptTests
{
    [Xunit.Fact]
    public void Record_Exception_AppendsOneCrashReceipt()
    {
        var events = new List<(string Kind, string? GoalId, string Detail)>();
        var receipt = new ConductorSupervisorCrashReceipt(
            (kind, goalId, detail) => events.Add((kind, goalId, detail)));

        receipt.Record("unhandled", new InvalidOperationException("boom"));

        var entry = Assert.Single(events);
        Assert.Equal("loop-handoff", entry.Kind);
        Assert.Null(entry.GoalId);
        Assert.StartsWith("SUPERVISOR_CRASHED source=unhandled ", entry.Detail);
        Assert.Contains("exception=InvalidOperationException ", entry.Detail);
        Assert.EndsWith("message=boom", entry.Detail);
    }

    [Xunit.Fact]
    public void Record_MultilineMessage_CollapsesEachLineBreakToOneSpace()
    {
        var events = new List<string>();
        var receipt = new ConductorSupervisorCrashReceipt((_, _, detail) => events.Add(detail));

        receipt.Record("unobserved-task", new Exception("first\r\nsecond\nthird\rfourth"));

        var detail = Assert.Single(events);
        Assert.EndsWith("message=first second third fourth", detail);
        Assert.DoesNotContain("\r", detail);
        Assert.DoesNotContain("\n", detail);
    }

    [Xunit.Fact]
    public void Record_AppendThrows_ContainsFailureWithoutRetry()
    {
        var attempts = 0;
        var receipt = new ConductorSupervisorCrashReceipt((_, _, _) =>
        {
            attempts++;
            throw new IOException("append failed");
        });

        var failure = Xunit.Record.Exception(
            () => receipt.Record("startup-or-run", new Exception("boom")));

        Assert.Null(failure);
        Assert.Equal(1, attempts);
    }

    [Xunit.Fact]
    public void Record_MessageGetterThrows_ContainsFormattingFailure()
    {
        var events = new List<string>();
        var receipt = new ConductorSupervisorCrashReceipt((_, _, detail) => events.Add(detail));

        var failure = Xunit.Record.Exception(
            () => receipt.Record("unhandled", new ThrowingMessageException()));

        Assert.Null(failure);
        Assert.Empty(events);
    }

    private sealed class ThrowingMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("message unavailable");
    }
}
