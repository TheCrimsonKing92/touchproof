using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: uses the fleet fixture's private roots and fake process host.
public sealed class ProjectFleetStatusReportTests
{
    [Fact]
    public async Task Report_StartPass_ContainsOneRowPerEnabledProjectFromHostData()
    {
        using var fixture = new ProjectFleetSupervisorTests.Fixture();
        var ready = fixture.Project("A");
        var live = fixture.Project("C");
        fixture.WriteLock(live);
        var pass = await ProjectFleetSupervisorTests.Reported(fixture.Supervisor().StartAsync(
            [fixture.Project("disabled") with { Enabled = false }, live,
             fixture.Project("D") with { ToolRequirements = [new("Gradle", "GRADLE_HOME")] }, ready], default));
        var rows = ProjectFleetStatusReport.BuildRows(pass);
        Assert.Equal(new[] { "A", "C", "D" }, rows.Select(row => row.Name));
        var request = Assert.Single(fixture.Host.Requests);
        Assert.Equal(1001, rows[0].ProcessId);
        Assert.Equal("build-commit", rows[0].BuildCommit);
        Assert.Equal("JAVA_HOME=" + request.AdditionalEnvironment!["JAVA_HOME"], rows[0].ToolVariables);
        Assert.Equal("already-running", rows[1].State);
        Assert.Null(rows[1].ProcessId);
        Assert.Null(rows[1].BuildCommit);
        Assert.Equal("Gradle (GRADLE_HOME)", rows[2].Missing);
        using var output = new StringWriter();
        ProjectFleetStatusReport.Write(output, rows);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Equal("NAME\tSTATE\tPID\tBUILD\tTOOL VARIABLES\tMISSING\tERROR", lines[0]);
        Assert.StartsWith("A\tstarted\t1001\tbuild-commit\tJAVA_HOME=user-jdk\t", lines[1]);
        Assert.StartsWith("C\talready-running\t-\t-\t", lines[2]);
        Assert.Contains("Gradle (GRADLE_HOME)", lines[3]);
    }

    [Fact]
    public void Write_EmbeddedNewlineAndTabs_KeepsEachProjectOnOneTableRow()
    {
        using var output = new StringWriter();
        ProjectFleetStatusReport.Write(output,
            [new("sample", "start-failed", null, null, "VAR=line1\nline2", "", "error\tmessage\r\nend")]);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(7, lines[1].Split('\t').Length);
        Assert.Contains("VAR=line1 line2", lines[1]);
    }
}
