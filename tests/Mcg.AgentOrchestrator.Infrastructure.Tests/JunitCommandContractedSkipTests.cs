using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: injected processes and clock, unique roots and execution owners.
public sealed class JunitCommandContractedSkipTests
{
    [Theory]
    [InlineData("java not found", false)]
    [InlineData("java is not installed", true)]
    [InlineData("java unavailable", false)]
    [InlineData("missing required tool: java", true)]
    [InlineData(" \tJAVA NOT FOUND.\r\n", true)]
    [InlineData(" MISSING REQUIRED TOOL: JAVA. ", false)]
    public async Task ContractedSkip_FailsAndPreservesEvidence(string reason, bool mixed)
    {
        using var fixture = new JunitCommandFixture(project: "nested");
        fixture.SetContract(ContractedToolSkipPolicyTests.Contract("java"));
        fixture.WriteReports = () => fixture.WriteSkippedReport(reason, mixed);

        var check = await fixture.RunAsync();

        Assert.False(check.Passed);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Single(fixture.ParsedReports);
        var diagnostic = Assert.Single(check.CrosswalkDiagnostics!);
        Assert.Equal(CrosswalkDiagnosticCategory.TestFailures, diagnostic.Category);
        Assert.Contains("nested/src/skipped.case::skipped", diagnostic.Detail);
        Assert.Contains("'java'", diagnostic.Detail);
        Assert.Contains(reason, diagnostic.Detail);
        var rows = JunitCommandFixture.ReadRows(check);
        var row = Assert.Single(rows.Where(row => row.Status == TestResultStatus.Skipped));
        Assert.Equal("nested/src/skipped.case::skipped", row.Id);
        Assert.Equal("nested/src/skipped.case", row.File);
        Assert.Equal("skipped", row.DisplayName);
        Assert.Equal(reason, row.Message);
        Assert.Equal("original trace", row.Trace);
        Assert.Equal(new[] { "suite" }, row.SuitePath);
        Assert.Equal(12L, row.DurationMs);
        Assert.Equal("seed", row.Seed);
        Assert.Equal("output", row.OutputRef);
        Assert.Equal("fixture", row.ReportedBy);
        Assert.Equal(TestResultStatus.Passed, Assert.Single(row.Attempts).Status);
        Assert.Equal(new[] { row.Id }, check.SkippedTestIdentities);
        Assert.Empty(check.FailingTestIdentities!);
        Assert.Equal(mixed ? 1 : 0, check.ExecutedTestCount);
        Assert.Equal(mixed ? 2 : 1, check.DiscoveredTestCount);
        Assert.Equal("junit-attempt", check.TestResultAttemptId);
        Assert.Equal(1, check.TestResultRunOrdinal);
        var cohort = Assert.IsType<Mcg.AgentOrchestrator.Core.AcceptanceCohortCheckEvidence>(check.CohortEvidence);
        Assert.Equal(check.ExecutedTestCount, cohort.ExecutedTestCount);
        Assert.Equal(check.TestResultAttemptId, cohort.AttemptId);
        Assert.Equal(check.TestResultRunOrdinal, cohort.RunOrdinal);
        Assert.Equal(rows.Select(row => row.Id), cohort.SelectedTestIds);
        Assert.Equal(Assert.Single(check.NormalizedTestResultPaths!), Assert.Single(cohort.Artifacts).Path);
    }

    [Theory]
    [InlineData("python not found")]
    [InlineData("javac not found")]
    [InlineData(null)]
    [InlineData("\"java not found\"")]
    [InlineData("Setup failed because java not found")]
    [InlineData("java  not found")]
    [InlineData("java not found..")]
    [InlineData("java.exe not found")]
    public async Task OtherSkipReason_PreservesExistingAllSkippedVerdict(string? reason)
    {
        using var fixture = new JunitCommandFixture();
        fixture.SetContract(ContractedToolSkipPolicyTests.Contract("java"));
        fixture.WriteReports = () => fixture.WriteSkippedReport(reason);
        var check = await fixture.RunAsync();
        Assert.True(check.Passed);
        Assert.Empty(check.CrosswalkDiagnostics!);
        Assert.Equal(0, check.ExecutedTestCount);
        var row = Assert.Single(JunitCommandFixture.ReadRows(check));
        Assert.Equal(TestResultStatus.Skipped, row.Status);
        Assert.Equal(reason, row.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonSkippedReason_DoesNotAddContractedSkipDiagnostic(bool fails)
    {
        using var fixture = new JunitCommandFixture();
        fixture.SetContract(ContractedToolSkipPolicyTests.Contract("java"));
        fixture.WriteReports = () => fixture.WriteReport("reports/row.xml", JunitCommandFixture.Now,
            ("src/row.case", "java not found", fails));
        fixture.TransformJson = json => json.Replace("original message", "java not found");
        var check = await fixture.RunAsync();
        Assert.Equal(!fails, check.Passed);
        Assert.Equal(fails ? 1 : 0, check.CrosswalkDiagnostics!.Count);
        Assert.DoesNotContain(check.CrosswalkDiagnostics, diagnostic => diagnostic.Detail.Contains("missing contracted tool"));
        Assert.Equal(1, check.ExecutedTestCount);
        Assert.Equal("java not found", Assert.Single(JunitCommandFixture.ReadRows(check)).Message);
    }

    [Fact]
    public async Task TestName_WithMissingToolProse_DoesNotInferAbsence()
    {
        using var fixture = new JunitCommandFixture();
        fixture.SetContract(ContractedToolSkipPolicyTests.Contract("java"));
        fixture.WriteReports = () => fixture.WriteSkippedReport("not applicable", name: "java not found");
        var check = await fixture.RunAsync();
        Assert.True(check.Passed);
        Assert.Equal(0, check.ExecutedTestCount);
        Assert.Equal(TestResultStatus.Skipped, Assert.Single(JunitCommandFixture.ReadRows(check)).Status);
    }

    [Fact]
    public async Task MultipleContractedSkips_EachHasDiagnosticAndRemainsSkipped()
    {
        using var fixture = new JunitCommandFixture();
        fixture.SetContract(ContractedToolSkipPolicyTests.Contract("java", "cargo nextest"));
        fixture.WriteReports = () =>
        {
            fixture.WriteSkippedReport("java unavailable", name: "java");
            fixture.WriteSkippedReport("cargo nextest not found", name: "cargo");
        };
        var check = await fixture.RunAsync();
        Assert.False(check.Passed);
        Assert.Equal(2, check.CrosswalkDiagnostics!.Count);
        foreach (var row in JunitCommandFixture.ReadRows(check))
        {
            Assert.Equal(TestResultStatus.Skipped, row.Status);
            Assert.Single(check.CrosswalkDiagnostics.Where(d => d.Detail.Contains(row.Id) && d.Detail.Contains(row.Message!)));
        }
        Assert.Equal(2, check.SkippedTestIdentities!.Count);
        Assert.Equal(0, check.ExecutedTestCount);
    }

    [Theory]
    [InlineData("java", "python", false)]
    [InlineData("python", "java", true)]
    public async Task ProjectHomeContract_WinsOverWorktreeAndCheckProject(
        string homeTool, string otherTool, bool passes)
    {
        using var fixture = new JunitCommandFixture(project: "nested");
        fixture.ProjectHomeDirectory = Path.Combine(fixture.Root, "project-home");
        fixture.SetContract(ContractedToolSkipPolicyTests.Contract(otherTool));
        fixture.SetContract(ContractedToolSkipPolicyTests.Contract(otherTool), Path.Combine(fixture.ProjectRoot, "config"));
        fixture.SetContract(ContractedToolSkipPolicyTests.Contract(homeTool), fixture.ProjectHomeDirectory);
        fixture.WriteReports = () => fixture.WriteSkippedReport("java not found");
        var check = await fixture.RunAsync();
        Assert.Equal(passes, check.Passed);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.All(fixture.Calls, call => Assert.Equal(fixture.ProjectRoot, call.Root));
        Assert.Single(JunitCommandFixture.ReadRows(check));
    }

    [Fact]
    public async Task MissingOptionalManifest_KeepsSkipBehavior()
    {
        using var fixture = new JunitCommandFixture();
        File.Delete(Path.Combine(fixture.Root, "config", "acceptance-manifest.json"));
        fixture.WriteReports = () => fixture.WriteSkippedReport("java not found");
        var check = await fixture.RunAsync();
        Assert.True(check.Passed);
        Assert.Equal(0, check.ExecutedTestCount);
        Assert.Equal(TestResultStatus.Skipped, Assert.Single(JunitCommandFixture.ReadRows(check)).Status);
    }

    [Fact]
    public async Task OmittedContract_UsesExistingCheckDerivation()
    {
        using var fixture = new JunitCommandFixture();
        fixture.WriteReports = () => fixture.WriteSkippedReport("suite-runner not found");
        var check = await fixture.RunAsync();
        Assert.False(check.Passed);
        Assert.Contains("'suite-runner'", Assert.Single(check.CrosswalkDiagnostics!).Detail);
        Assert.Equal(TestResultStatus.Skipped, Assert.Single(JunitCommandFixture.ReadRows(check)).Status);
    }

    [Theory]
    [InlineData("{", "JSON", false)]
    [InlineData("{\"environmentContract\":null}", "Null environment contract", false)]
    [InlineData("{\"environmentContract\":{\"version\":2,\"tools\":[]}}", "requires version 1", true)]
    public async Task InvalidContract_WithoutMatchingSkip_FailsClosed(string json, string error, bool home)
    {
        using var fixture = new JunitCommandFixture();
        var directory = Path.Combine(fixture.Root, home ? "project-home" : "config");
        Directory.CreateDirectory(directory);
        if (home) fixture.ProjectHomeDirectory = directory;
        File.WriteAllText(Path.Combine(directory, "acceptance-manifest.json"), json);
        fixture.WriteReports = () => fixture.WriteReport("reports/pass.xml", JunitCommandFixture.Now, ("src/pass.case", "pass", false));
        var check = await fixture.RunAsync();
        Assert.False(check.Passed);
        Assert.Contains("JUnit environment contract could not be loaded", check.OutputTail);
        Assert.Contains(error, check.OutputTail);
        Assert.Empty(fixture.Calls);
        Assert.Empty(check.NormalizedTestResultPaths ?? []);
    }
}
