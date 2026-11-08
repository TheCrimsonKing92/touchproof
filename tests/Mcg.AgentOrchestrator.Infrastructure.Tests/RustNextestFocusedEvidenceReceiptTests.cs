using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static RustNextestFocusedEvidenceTests;

// Parallel-safe: GitCli uses a unique repository and local identity; commands and parser are injected.
// No environment writes, shared git configuration, build slots, or external nextest process.
public sealed class RustNextestFocusedEvidenceReceiptTests
{
    [Theory]
    [InlineData(ModuleTest)]
    [InlineData(IntegrationTest)]
    public async Task RunFocusedEvidence_JunitCommandRoute_BindsReceiptToCandidate(string selector)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var goal = new GoalId(Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "acceptance-manifest.json"), Manifest);
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".orchestrator/\ntarget/\ncrosswalk.exe\n");
            Git(root, "init", "-b", "main");
            Git(root, "add", ".");
            Git(root, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test",
                "-c", "commit.gpgsign=false", "commit", "-m", "nextest fixture");
            var candidate = Git(root, "rev-parse", "HEAD").Trim();
            Assert.Equal(40, candidate.Length);
            var binary = Path.Combine(root, "crosswalk.exe");
            File.WriteAllText(binary, "injected parser binary");
            var resolver = new CrosswalkExecutableResolver(binary, runVersionAsync: (_, _) =>
                Task.FromResult(new WorkerProcessRunResult(0, "fixture-version", "")));
            var calls = new List<string[]>();
            var verifier = new GoalAcceptanceVerifier(new GoalAcceptanceVerifierTestOverrides(), (args, directory, _, _) =>
            {
                Assert.Equal(root, directory);
                calls.Add(args);
                if (args[0] == "cargo")
                {
                    Assert.Equal(new[] { "cargo", "nextest", "run", "--profile", "ci", "-E", $"test(={selector})" }, args);
                    var reportPath = Path.Combine(root, Report);
                    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                    new XDocument(new XElement("testsuite", new XElement("testcase", new XAttribute("name", selector),
                        new XAttribute("classname", "rust-binary")))).Save(reportPath);
                    File.SetLastWriteTimeUtc(reportPath, JunitCommandFixture.Now.UtcDateTime);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "nextest fixture") { StandardOutput = "" });
                }
                Assert.Equal(new[] { binary, "parse", "junit", Path.GetFullPath(Path.Combine(root, Report)),
                    "--tool", "rust nextest focused evidence: declared-test" }, args);
                var name = XDocument.Load(args[3]).Descendants("testcase").Single().Attribute("name")!.Value;
                var json = JsonSerializer.Serialize(new
                {
                    reportFormat = "CTRF", schemaVersion = "mcg-test-result/1",
                    results = new { tool = new { name = "nextest fixture" },
                        tests = new[] { new { testId = "rust-binary::" + name, name, status = "passed" } } }
                });
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, json) { StandardOutput = json });
            }, new FrozenClock()) { CrosswalkExecutable = resolver };

            var result = await verifier.RunFocusedEvidenceAsync(root, goal, "Cargo.toml:" + selector);

            Assert.True(result.Accepted, result.Summary);
            Assert.True(result.Passed, result.Summary);
            var arm = Assert.Single(result.Arms!);
            Assert.Equal(FindingEvidenceArm.Candidate, arm.Arm);
            Assert.Equal(candidate, arm.Sha);
            Assert.Equal(FindingEvidenceArmDisposition.Green, arm.Disposition);
            Assert.Empty(arm.NotRunSelections!);
            var check = Assert.Single(arm.Checks);
            var receiptPath = Assert.Single(check.NormalizedTestResultPaths!);
            Assert.True(File.Exists(receiptPath));
            var receipt = new NormalizedTestResultSource().Read(receiptPath);
            Assert.Equal(TestResultReadStatus.Readable, receipt.Status);
            Assert.Equal(selector, Assert.Single(receipt.Records).DisplayName);
            Assert.Equal(CrosswalkFocusedEvidence.SelectionGrade.Satisfied,
                RustNextestFocusedEvidence.Grade(receipt.Records, selector));
            Assert.Equal(2, calls.Count);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal);
            SharedTestSupport.RemoveTempDirectory(root);
            Assert.False(Directory.Exists(root));
        }
    }

    private static string Git(string root, params string[] arguments)
    {
        var result = GitCli.Run(root, arguments);
        Assert.True(result.Succeeded, result.Error);
        return result.Output;
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => JunitCommandFixture.Now;
    }
}
