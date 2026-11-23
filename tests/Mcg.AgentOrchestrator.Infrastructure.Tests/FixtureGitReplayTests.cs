using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class FixtureGitReplayTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    // Each invocation owns its files and replaces native processes, ETW and integrity labeling.
    [Fact(Skip = "The compiled replay command requires Windows.", SkipUnless = nameof(IsWindows))]
    public void Run_WithControlledSelections_WritesReceiptAndCleansStaging()
    {
        var worktree = nameof(GoalWorktreeTestsRebaseMergeNoHistoryRewrite);
        var factory = nameof(WorkerDispatchTestsSeededRepositoryFactoryTests);
        var factoryMethod = nameof(WorkerDispatchTestsSeededRepositoryFactoryTests.Create_MultipleCopies_ProducesIndependentCommittedRepositories);
        var selections = new[]
        {
            new Selection("goal-worktree", $"FullyQualifiedName={worktree}", worktree,
                ["MergeIntegratedHistory_PreservesEarlierGoalCommitsAndExactTree",
                 "ExistingSquashPath_NegativeControlDropsReviewedHeadFromAncestry",
                 "OverlappingConflict_AbortsAndLeavesBranchAndCheckoutUnchanged",
                 "AlreadyContainsMain_LeavesBranchUnchanged",
                 "DirtyWorktree_PreservesBranchAndUncommittedContent",
                 "AdditiveConflict_MergesWithOriginalHeadAsFirstParent",
                 "MissingBranch_ReturnsRecoveryWithoutCreatingBranch",
                 "MissingWorktree_LeavesExistingBranchUnchanged"], 1, 1_000_000_001, 500_000_000),
            new Selection("seeded-factory", $"FullyQualifiedName={factory}&Name~{factoryMethod}", factory,
                [factoryMethod], 0, 700_000_002, 300_000_000)
        };

        var root = Directory.CreateTempSubdirectory("mcg-fixture-replay-").FullName;
        try
        {
            const string project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
            var repo = Path.Combine(root, "repo");
            var script = Path.Combine(repo, "scripts/Invoke-TestSummary.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.WriteAllText(script, "# The capture stand-in supplies the runner artifacts.");
            var evidence = Path.Combine(root, "evidence");
            var profile = Path.Combine(root, "profile");
            var lowRoot = Path.Combine(profile, "Temp", "Low", "mcg-tests");
            var labeledDirectories = new List<string>();

            FixtureGitReplayTrace.Job Capture(ProcessStartInfo runner, string directory, string trace)
            {
                var selection = Assert.Single(selections, item => Path.Combine(evidence, item.Family) == directory);
                var staging = Assert.Single(labeledDirectories.Where(Directory.Exists));
                Assert.Equal(lowRoot, Path.GetDirectoryName(staging));
                Assert.True(File.Exists(Path.Combine(staging, ".mtp-run-ownership.json")));
                Assert.Equal(WorkerShell.Executable, runner.FileName);
                Assert.Equal(repo, runner.WorkingDirectory);
                var results = Path.Combine(staging, "tests");
                var slots = Path.Combine(staging, "slot.jsonl");
                var probes = Path.Combine(staging, "probes.jsonl");
                Assert.Equal(new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Target", Path.Combine(repo, project),
                    "-Configuration", "Release", "-NoBuild", "-Filter", selection.Filter, "-ResultsRoot", results,
                    "-SlotTimingFile", slots, "-TestInput", $"MCG_TEST_INPUT_FIXTURE_GIT_PROBE_TRACE={probes}" }, runner.ArgumentList);

                WriteReports(selection, results, slots, probes);
                File.WriteAllText(trace, selection.Family);
                File.WriteAllText(Path.Combine(directory, "runner.log"), $"Completed {selection.Filter}");
                File.WriteAllText(Path.Combine(directory, "capture.json"), "{\"source\":\"controlled capture\"}");
                return new FixtureGitReplayTrace.Job(101, 1000, (uint)(2 + selection.GitChildren),
                    selection.UserCpu, selection.KernelCpu, 0);
            }

            List<FixtureGitReplayTrace.Start> ReadTree(string trace, FixtureGitReplayTrace.Job job)
            {
                Assert.All(labeledDirectories, path => Assert.False(Directory.Exists(path), path));
                var selection = Assert.Single(selections, item => item.Family == File.ReadAllText(trace));
                Assert.Equal((uint)(2 + selection.GitChildren), job.ProcessCount);
                var tree = new List<FixtureGitReplayTrace.Start>
                {
                    new(101, 100, "1", "0", 1000, "pwsh.exe", null),
                    new(102, 101, "2", "1", 1001, "git.exe", "git init")
                };
                if (selection.GitChildren == 1)
                    tree.Add(new(103, 102, "3", "2", 1002, "git-helper.exe", null));
                return tree;
            }

            var host = new FixtureGitReplay.ReplayHost(ControlledNative, () => profile, labeledDirectories.Add, Capture, ReadTree);
            Assert.Equal(0, FixtureGitReplay.Run([repo, evidence, "Release"], host));
            Assert.Equal(2, labeledDirectories.Distinct().Count());
            Assert.Empty(Directory.EnumerateFileSystemEntries(lowRoot));

            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "replay.json")));
            var receipt = document.RootElement;
            Assert.Equal(repo, receipt.GetProperty("repository").GetString());
            Assert.Equal("controlled-revision", receipt.GetProperty("revision").GetString());
            Assert.Equal("Release", receipt.GetProperty("configuration").GetString());
            Assert.Equal(HashFile(typeof(FixtureGitReplay).Assembly.Location), receipt.GetProperty("measurement_runner_sha256").GetString());
            Assert.Equal(3, receipt.GetProperty("native_git").GetInt32());
            Assert.Equal(2_500_000_003L, receipt.GetProperty("cpu_100ns").GetInt64());
            Assert.Collection(receipt.GetProperty("selections").EnumerateArray(),
                selection => AssertSelection(selection, selections[0], 8, 2, 1_500_000_001L),
                selection => AssertSelection(selection, selections[1], 1, 1, 1_000_000_002L));
            AssertArtifacts(evidence, receipt, selections);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static string ControlledNative(string directory, string executable, string[] args) => (executable, args) switch
    {
        ("git.exe", ["rev-parse", "HEAD"]) => "controlled-revision",
        ("git.exe", ["status", "--porcelain"]) => "",
        ("git.exe", ["merge-base", "--is-ancestor", _, "controlled-revision"]) => "",
        ("git.exe", ["log", "-1", "--format=%P"]) => "controlled-parent",
        ("git.exe", ["remote", "get-url", "origin"]) => "fixture://repository",
        ("git.exe", ["--version"]) => "git version fixture",
        ("dotnet.exe", ["--version"]) => "controlled-sdk",
        (var shell, ["-NoProfile", "-NonInteractive", "--version"]) when shell == WorkerShell.Executable => "controlled-shell",
        _ => throw new InvalidOperationException($"Unexpected command in {directory}: {executable} {string.Join(' ', args)}")
    };

    private static void WriteReports(Selection selection, string results, string slots, string probes)
    {
        Directory.CreateDirectory(results);
        new XElement("TestRun",
            new XElement("TestDefinitions", selection.Methods.Select(method => new XElement("UnitTest", new XAttribute("id", method),
                new XElement("TestMethod", new XAttribute("className", selection.TestClass), new XAttribute("name", method))))),
            new XElement("Results", selection.Methods.Select(method => new XElement("UnitTestResult",
                new XAttribute("testId", method), new XAttribute("testName", method), new XAttribute("outcome", "Passed")))))
            .Save(Path.Combine(results, "controlled.trx"));
        var probeTest = $"{selection.TestClass}.{selection.Methods[0]}";
        File.WriteAllLines(slots, selection.Methods.Select((method, index) =>
        {
            var launches = index == 0 ? 1 : 0;
            return JsonSerializer.Serialize(new
            {
                test_name = $"{selection.TestClass}.{method}", process_id = 101, git_launches = launches,
                git_fixture_probe_launches = launches, git_subcommands = new { init = launches },
                git_callers = new Dictionary<string, int> { ["Controlled<-Runner|init"] = launches }
            });
        }));
        File.WriteAllText(probes, JsonSerializer.Serialize(new
        {
            test_name = probeTest, process_id = 101, child_process_id = 102, child_start_filetime = 1001,
            caller = "Controlled<-Runner", subcommand = "init", arguments = new[] { "init" }
        }));
    }

    private static void AssertSelection(JsonElement selection, Selection expected, int cases, int git, long cpu)
    {
        Assert.Equal(expected.Family, selection.GetProperty("family").GetString());
        Assert.Equal(expected.Filter, selection.GetProperty("filter").GetString());
        Assert.Equal(cases, selection.GetProperty("executed_cases").GetInt32());
        Assert.Equal(0, selection.GetProperty("skipped_cases").GetInt32());
        Assert.Equal(git, selection.GetProperty("native_git").GetInt32());
        Assert.Equal(1, selection.GetProperty("shared_probe").GetInt32());
        Assert.Equal(expected.GitChildren, selection.GetProperty("git_child").GetInt32());
        Assert.Equal(0, selection.GetProperty("unattributed").GetInt32());
        Assert.Equal(cpu, selection.GetProperty("cpu_100ns").GetInt64());
    }

    private static void AssertArtifacts(string evidence, JsonElement receipt, Selection[] selections)
    {
        var artifacts = receipt.GetProperty("artifacts").EnumerateArray()
            .ToDictionary(entry => entry.GetProperty("path").GetString()!);
        var requiredFiles = new[] { Path.Combine("tests", "controlled.trx"), "slot.jsonl", "probes.jsonl", "process.etl",
            "runner.log", "capture.json", "cases.json", "native-git.json", "shared-probe-callers.json", "process-tree.json" };
        foreach (var selection in selections)
        foreach (var file in requiredFiles)
        {
            var path = Path.Combine(selection.Family, file);
            Assert.Contains(path, artifacts.Keys);
            var artifact = artifacts[path];
            var fullPath = Path.Combine(evidence, path);
            var length = new FileInfo(fullPath).Length;
            Assert.True(length > 0, path);
            Assert.Equal(length, artifact.GetProperty("bytes").GetInt64());
            Assert.Equal(HashFile(fullPath), artifact.GetProperty("sha256").GetString());
        }
        Assert.DoesNotContain(artifacts.Keys, path => Path.GetFileName(path) == ".mtp-run-ownership.json");
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed record Selection(string Family, string Filter, string TestClass, string[] Methods,
        int GitChildren, long UserCpu, long KernelCpu);
}
