using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CrosswalkJunitParserArgumentVectorTests
{
    [Theory]
    [InlineData("declared-test")]
    [InlineData("it's tool")]
    [InlineData("tool with space")]
    public async Task Parse_ToolName_PreservesExactVector(string tool)
    {
        var binary = Path.GetFullPath("crosswalk.exe");
        var report = Path.GetFullPath("report's folder/results.xml");
        var calls = new List<string[]>();
        var result = await CrosswalkJunitParser.ParseAsync(new(binary, "v", "hash"), report, tool, args =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "")
                { StandardOutput = CrosswalkAcceptanceFixture.Report(("id", "passed")) });
        });
        Assert.Equal(new[] { binary, "parse", "junit", report, "--tool", tool }, Assert.Single(calls));
        Assert.DoesNotContain(WorkerShell.Executable, calls[0]);
        Assert.DoesNotContain("-Command", calls[0]);
        Assert.Equal(TestResultReadStatus.Readable, result.Status);
    }

    [Fact]
    public async Task Parse_NonzeroExit_Throws()
    {
        await Assert.ThrowsAsync<IOException>(() => CrosswalkJunitParser.ParseAsync(new("crosswalk.exe", "v", "hash"),
            "report.xml", "tool", _ => Task.FromResult(new GoalAcceptanceVerifier.CommandResult(2, "failure"))));
    }
}
