using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CrosswalkCmdStartInfoTests
{
    [Fact]
    public async Task ParseVector_Build_StartsCmdWithoutPowerShell()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Requires cmd.exe.");
        var root = Path.GetFullPath("crosswalk folder");
        var binary = Path.Combine(root, "crosswalk.exe");
        string[]? vector = null;
        await CrosswalkJunitParser.ParseAsync(new(binary, "v", "hash"), Path.Combine(root, "report.xml"), "tool", args =>
        {
            vector = args;
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "")
                { StandardOutput = CrosswalkAcceptanceFixture.Report(("id", "passed")) });
        });
        var info = AcceptanceShellProcessStartInfo.Build(vector!, root, "stdout.txt", "stderr.txt", true,
            null, false, out _);
        Assert.Equal("cmd.exe", info.FileName);
        Assert.Contains("\"" + binary + "\"", info.Arguments);
        Assert.Contains("chcp 65001", info.Arguments);
        Assert.Contains("> \"stdout.txt\" 2> \"stderr.txt\"", info.Arguments);
        Assert.DoesNotContain(WorkerShell.Executable, info.Arguments);
        Assert.DoesNotContain("-Command", info.Arguments);
    }
}
