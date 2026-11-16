public sealed class ScenarioStageTraceTests
{
    [Fact]
    public void ThrowingStageRecordsBothLinesAndUnfinishedStageRecordsOnlyStarted()
    {
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            using var trace = new ScenarioStageTrace(directory, "throwing-stage");
            var failure = new InvalidOperationException("boom");
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
                trace.Run("throws", () => { throw failure; })));
            trace.Start("never-completes");

            // Read before disposal: every line must already be visible to another reader.
            var lines = ReadLines(trace.TracePath);
            Assert.Equal(new[] { "started throws", "completed throws", "started never-completes" },
                lines.Select(WithoutElapsed));
            foreach (var line in lines)
            {
                Assert.True(long.TryParse(line[..line.IndexOf(' ')], out var elapsed), line);
                Assert.True(elapsed >= 0, line);
            }
            Assert.Equal("never-completes", trace.ActiveStage);

            trace.Dispose();
            Assert.Equal(lines, ReadLines(trace.TracePath));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(directory);
        }
    }

    [Fact]
    public void AssertionFailureInStageNamesTracePathAndActiveStage()
    {
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            using var trace = new ScenarioStageTrace(directory, "assertion-stage");
            var failure = Assert.Throws<Xunit.Sdk.XunitException>(() =>
                trace.Run("assert-stage", () => Assert.Equal(1, 2)));

            var original = Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(failure.InnerException);
            Assert.Contains(original.Message, failure.Message, StringComparison.Ordinal);
            Assert.Contains(trace.TracePath, failure.Message, StringComparison.Ordinal);
            Assert.Contains("active stage: assert-stage", failure.Message, StringComparison.Ordinal);
            Assert.Null(trace.ActiveStage);
            Assert.Equal(new[] { "started assert-stage", "completed assert-stage" },
                ReadLines(trace.TracePath).Select(WithoutElapsed));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(directory);
        }
    }

    [Fact]
    public void SuccessfulStagesReturnValuesAndUseUniqueRetainedFiles()
    {
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            string firstPath;
            string secondPath;
            using (var first = new ScenarioStageTrace(directory, "scenario"))
            using (var second = new ScenarioStageTrace(directory, "scenario"))
            {
                firstPath = first.TracePath;
                secondPath = second.TracePath;
                Assert.NotEqual(firstPath, secondPath);
                foreach (var path in new[] { firstPath, secondPath })
                {
                    Assert.Equal(directory, Path.GetDirectoryName(path));
                    var name = Path.GetFileName(path);
                    Assert.StartsWith("scenario-", name, StringComparison.Ordinal);
                    Assert.EndsWith(".trace.log", name, StringComparison.Ordinal);
                    Assert.True(Guid.TryParseExact(name["scenario-".Length..^".trace.log".Length], "N", out _));
                }
                Assert.Equal(42, first.Run("value", () => 42));
                var called = false;
                first.Run("action", () => { called = true; });
                Assert.True(called);
                Assert.Null(first.ActiveStage);
                Assert.Equal(new[] { "started value", "completed value", "started action", "completed action" },
                    ReadLines(firstPath).Select(WithoutElapsed));
            }
            Assert.Equal(4, ReadLines(firstPath).Length);
            Assert.Empty(ReadLines(secondPath));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(directory);
        }
    }

    private static string WithoutElapsed(string line) => line[(line.IndexOf(' ') + 1)..];

    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines.ToArray();
    }
}
