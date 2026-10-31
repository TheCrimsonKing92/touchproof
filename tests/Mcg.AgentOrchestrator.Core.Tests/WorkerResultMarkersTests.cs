using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: pure string operations; no files, processes or shared state.
// Expectations traced from 1bba6886e, DispatchFailureClassifier.cs:1274-1300.
public sealed class WorkerResultMarkersTests
{
    [Theory]
    [InlineData("  **WORKER_RESULT:**  ", true)]
    [InlineData("\t## `WORKER_RESULT`\t", true)]
    [InlineData("worker_result::", true)]
    [InlineData("WORKER_RESULT :", true)]
    [InlineData("END_WORKER_RESULT", false)]
    [InlineData("Print the WORKER_RESULT block last.", false)]
    [InlineData("WORKER_RESULT: trailing text", false)]
    [InlineData("", false)]
    public void IsWorkerResultOpener_Line_PreservesBaseline(string line, bool expected)
    {
        Assert.Equal(expected, WorkerResultMarkers.IsWorkerResultOpener(line));
    }

    [Theory]
    [InlineData("  **END_WORKER_RESULT**  ", true)]
    [InlineData("## `end_worker_result`", true)]
    [InlineData("END_WORKER_RESULT:", false)]
    [InlineData("WORKER_RESULT", false)]
    [InlineData("Print the END_WORKER_RESULT block last.", false)]
    [InlineData("", false)]
    public void IsWorkerResultEndMarker_Line_PreservesBaseline(string line, bool expected)
    {
        Assert.Equal(expected, WorkerResultMarkers.IsWorkerResultEndMarker(line));
    }

    [Theory]
    [InlineData("  ## **`WORKER_RESULT:`**  ", "WORKER_RESULT:")]
    [InlineData("  **`END_WORKER_RESULT`**  ", "END_WORKER_RESULT")]
    [InlineData("wo#rk*er_`result:", "worker_result:")]
    [InlineData("Print the WORKER_RESULT block last.", "Print the WORKER_RESULT block last.")]
    [InlineData(" \t#*`\t ", "")]
    public void NormalizeWorkerResultMarker_Text_PreservesBaseline(string text, string expected)
    {
        Assert.Equal(expected, WorkerResultMarkers.NormalizeWorkerResultMarker(text));
    }
}
