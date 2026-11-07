using Mcg.AgentOrchestrator.Core;
using static ConductorDriverTests;

// Parallel-safe: each test owns its kernel and goal timeline.
public sealed class PreTesterFailingTestsLineBoundTests
{
    [Theory]
    [InlineData(11, "C:\\results\\run;one.result.json")]
    [InlineData(1036, "C:\\results\\run;one.result.json")]
    [InlineData(1036, null)]
    public void ForTester_MoreThanTenNames_BoundsLineAndPointsToResult(int count, string? resultPath)
    {
        var names = Enumerable.Range(0, count)
            .Select(index => $"Infrastructure.Tests.Case{index:D4}.Fails(value: X, expected: True)").ToArray();

        var lines = Lines(names, resultPath);
        var line = Assert.Single(lines, value => value.StartsWith("evidence_failing_tests:", StringComparison.Ordinal));

        Assert.Equal($"evidence_failing_tests: total={count}; first_10={string.Join(',', names.Take(10))}; " +
            $"result_path={resultPath ?? "none"}", line);
        Assert.All(names.Skip(10), name => Assert.DoesNotContain(name, line, StringComparison.Ordinal));
        Assert.True(line.Length < 4096, $"Failing-test line contained {line.Length} characters.");
        Assert.Contains("evidence_not_run: MissingTests", lines);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void ForTester_AtMostTenNames_PreservesExactPreviousFormat(int count)
    {
        var names = Enumerable.Range(0, count).Select(index => $"TheoryTests.Case{index}.Fails(value: X, expected: True)").ToArray();

        var line = Assert.Single(Lines(names, "result.json"),
            value => value.StartsWith("evidence_failing_tests:", StringComparison.Ordinal));

        Assert.Equal($"evidence_failing_tests: {string.Join(',', names)}", line);
    }

    private static IReadOnlyList<string> Lines(IReadOnlyList<string> names, string? resultPath)
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var entry = new PreTesterEvidenceEntry("red", "abc1234", "receipt-1",
            ["Infrastructure.Tests:TheoryTests"], ["MissingTests"], resultPath, names);
        kernel.RecordFindingEvidenceRun(goal.Id, tester.Id, PreTesterEvidenceIndexLines.FormatMarker(entry));
        return PreTesterEvidenceIndexLines.ForTester(goal, tester, entry.CandidateSha);
    }
}
