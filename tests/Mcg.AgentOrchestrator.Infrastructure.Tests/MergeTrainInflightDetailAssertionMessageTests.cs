// Parallel-safe: uses only in-memory strings and no shared state.
public sealed class MergeTrainInflightDetailAssertionMessageTests
{
    [Fact]
    public void FailedContainsCheck_LongDetail_ExceptionTextKeepsDetailTail()
    {
        const string marker = "detail-tail-7e8fa67b";
        var detail = "decision detail: " + new string('x', 4096) + marker;
        Assert.False(detail.Contains("outcome=inflight", StringComparison.Ordinal));

        var exception = Assert.Throws<Xunit.Sdk.TrueException>(() =>
            Assert.True(detail.Contains("outcome=inflight", StringComparison.Ordinal), $"expected substring outcome=inflight; detail={detail}"));

        Assert.Contains(marker, exception.Message, StringComparison.Ordinal);
    }
}
