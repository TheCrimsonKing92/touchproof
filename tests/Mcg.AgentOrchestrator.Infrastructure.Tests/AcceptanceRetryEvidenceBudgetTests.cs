using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using static AssemblyCleanupTrxFixture;

// Parallel-safe: every TRX fixture owns and removes its unique temporary directory.
public sealed class AcceptanceRetryEvidenceBudgetTests
{
    private const string Partition = "infrastructure tests: budget";

    [Fact]
    public void LongMessageLine_MeasuredFailureShape_KeepsPrefixAndOmittedCount()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var path = fixture.WriteReceipt([Result("Tests.LongLine", "Failed", new string('x', 172_808))]);

        var feedback = Feedback(path);

        var entry = Assert.Single(Failures(feedback));
        Assert.Equal(new string('x', 2_000) + " …[170808 chars omitted]",
            entry.Split(Environment.NewLine)[1]);
        Assert.True(entry.Length <= 8_000);
        Assert.Contains($"TRX receipt for partition \"{Partition}\": {path}", feedback);
    }

    [Fact]
    public void CombinedMessageAndStack_EntryOverflow_EndsWithFullFailureReceipt()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var message = string.Join('\n', Enumerable.Repeat(new string('m', 1_000), 6));
        var stack = string.Join('\n', Enumerable.Repeat(new string('s', 1_000), 6));
        var path = fixture.WriteReceipt([WithStack("Tests.LargeEntry", message, stack)]);

        var entry = Assert.Single(Failures(Feedback(path)));

        Assert.True(entry.Length <= 8_000);
        var marker = Regex.Match(entry, @"\[truncated: (\d+) chars omitted; full failure in (.*)\]$");
        Assert.True(marker.Success, "Entry overflow must end with a full-failure receipt.");
        Assert.Equal(path, marker.Groups[2].Value);
        var retainedLength = marker.Index - Environment.NewLine.Length;
        var originalLength = $"[FAIL] Tests.LargeEntry (Failed){Environment.NewLine}{message}{Environment.NewLine}{stack}".Length;
        Assert.Equal(originalLength, retainedLength + int.Parse(marker.Groups[1].Value));
        Assert.StartsWith("[FAIL] Tests.LargeEntry (Failed)", entry);
        Assert.Contains(new string('s', 100), entry, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManyFailures_TotalOverflow_PreservesPrefixAndOneFinalSummary(bool multipleTrx)
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var message = string.Join('\n', Enumerable.Repeat(new string('m', 1_000), 7));
        var rows = Enumerable.Range(0, 10)
            .Select(index => Result($"Tests.Failure{index}", "Failed", message)).ToArray();
        var first = fixture.WriteReceipt(multipleTrx ? rows.Take(5) : rows);
        string[] paths = multipleTrx ? [first, fixture.WriteReceipt(rows.Skip(5))] : [first];

        var feedback = Feedback(paths);

        var evidence = ConcreteEvidence(feedback);
        Assert.True(evidence.Sum(entry => entry.Length) <= 32_000);
        Assert.Equal(4, Failures(feedback).Length);
        Assert.Equal(Enumerable.Range(0, 4).Select(index => $"[FAIL] Tests.Failure{index} (Failed)"),
            Failures(feedback).Select(entry => entry.Split(Environment.NewLine)[0]));
        var summary = Assert.Single(evidence.Where(entry => entry.Contains("more failures omitted", StringComparison.Ordinal)));
        Assert.Equal($"6 more failures omitted — see {string.Join(", ", paths)}", summary);
        Assert.Equal(summary, evidence[^1]);
        Assert.Equal("Acceptance criteria summary:", feedback[^2]);
    }

    [Fact]
    public void Rerun_SameNameAndFirstMessageLine_AppearsOnceAndAnnotatesReceipt()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var first = fixture.WriteReceipt([WithStack("Tests.Duplicate", "same assertion\nfirst details", "first stack")]);
        var rerun = Path.Combine(fixture.Root, "remainder-run-1.trx");
        File.Copy(fixture.WriteReceipt([WithStack("Tests.Duplicate", "same assertion\nrerun details", "rerun stack")]), rerun);

        var feedback = Feedback(first, rerun);

        var entry = Assert.Single(Failures(feedback));
        Assert.Contains("first details", entry, StringComparison.Ordinal);
        Assert.Contains("first stack", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("rerun details", entry, StringComparison.Ordinal);
        Assert.Equal($"TRX receipt for partition \"{Partition}\": {first}", feedback[2]);
        Assert.Equal($"TRX receipt for partition \"{Partition}\": {rerun} (same failures as {first})", feedback[4]);
    }

    [Fact]
    public void DistinctIdentities_AndNewNote_AreNotDeduplicated()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var first = fixture.WriteReceipt([Result("Tests.First", "Failed", "same message")]);
        var second = fixture.WriteReceipt([Result("Tests.Second", "Failed", "same message"),
            Result("Tests.First", "Failed", "different message")]);

        Assert.Equal(3, Failures(Feedback(first, second)).Length);
        Assert.Single(Failures(Feedback(first)));
    }

    [Fact]
    public void SmallNote_TrxAndOutputFallback_IsByteIdenticalToExistingFormat()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var path = fixture.WriteReceipt([WithStack("Tests.First", "expected 2\nactual 0", "at Tests.cs:line 42"),
            Result("Tests.Second", "Timeout")]);
        var feedback = ConductorDriver.FormatCriterionRetryFeedbackForTests([
            Check(path) with { OutputTail = "[FAIL] unused fallback", ResultSummary = "2 failed" },
            new AcceptanceCheckResult("output-only", false, 1, "[FAIL] fallback details", ResultSummary: "failed output")]);
        string[] expected = [
            "Concrete acceptance failure evidence:",
            $"failed check: {Partition} (exit code 1)",
            $"TRX receipt for partition \"{Partition}\": {path}",
            $"[FAIL] Tests.First (Failed){Environment.NewLine}expected 2\nactual 0{Environment.NewLine}at Tests.cs:line 42",
            $"[FAIL] Tests.Second (Timeout){Environment.NewLine}failure message unavailable{Environment.NewLine}stack trace unavailable",
            "failed check: output-only (exit code 1)",
            "[FAIL] fallback details",
            "detail unavailable: no TRX path was recorded for partition \"output-only\"",
            "Acceptance criteria summary:",
            $"{Partition}: 2 failed",
            "output-only: failed output"];

        Assert.Equal(expected, feedback);
        Assert.Equal(Encoding.UTF8.GetBytes(string.Join('\n', expected)),
            Encoding.UTF8.GetBytes(string.Join('\n', feedback)));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void LineBound_MixedLengths_PreservesOriginalSeparators(string separator)
    {
        var small = "first" + separator + new string('x', 2_000) + separator;
        Assert.Same(small, AcceptanceRetryEvidenceBudget.BoundLine(small));
        var large = "first" + separator + new string('x', 2_001) + separator + "last";

        Assert.Equal("first" + separator + new string('x', 2_000) + " …[1 chars omitted]" + separator + "last",
            AcceptanceRetryEvidenceBudget.BoundLine(large));
    }

    [Fact]
    public void OutputFallback_LongLines_UsesCharacterBudgetAndExistingOmissionText()
    {
        var lines = Enumerable.Range(0, 30).Select(index => $"[FAIL] {index}: {new string('x', 4_000)}");
        var feedback = ConductorDriver.FormatCriterionRetryFeedbackForTests([
            new AcceptanceCheckResult("output-only", false, 1, string.Join('\n', lines), ResultSummary: "failed output")]);

        var evidence = ConcreteEvidence(feedback);
        Assert.True(evidence.Sum(entry => entry.Length) <= 32_000);
        Assert.Equal(15, Failures(feedback).Length);
        Assert.Contains("15 more acceptance evidence entries omitted.", evidence);
        Assert.All(Failures(feedback), line => Assert.Contains("chars omitted]", line, StringComparison.Ordinal));
    }

    private static XElement WithStack(string name, string message, string stack)
    {
        var row = Result(name, "Failed", message);
        var errorInfo = row.Descendants().Single(element => element.Name.LocalName == "ErrorInfo");
        errorInfo.Add(new XElement(errorInfo.Name.Namespace + "StackTrace", stack));
        return row;
    }

    private static AcceptanceCheckResult Check(params string[] paths) =>
        new(Partition, false, 1, null, TestResultPaths: paths, ResultSummary: "failed tests");

    private static string[] Feedback(params string[] paths) =>
        ConductorDriver.FormatCriterionRetryFeedbackForTests([Check(paths)]);

    private static string[] ConcreteEvidence(string[] feedback) =>
        feedback.Skip(1).TakeWhile(entry => entry != "Acceptance criteria summary:").ToArray();

    private static string[] Failures(string[] feedback) =>
        ConcreteEvidence(feedback).Where(entry => entry.StartsWith("[FAIL]", StringComparison.Ordinal)).ToArray();
}
