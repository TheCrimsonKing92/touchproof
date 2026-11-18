using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;
using static DotnetBuildEnvironmentManagerTests;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsFocusedCompactReceipt
{
    private const string FixtureArmedVariable = "FOCUSED_COMPACT_RECEIPT_FIXTURE_ARMED";
    private const string FixtureFilter = "FullyQualifiedName~DotnetBuildEnvironmentManagerTestsReceiptFixture";
    private static readonly string[] CompactFields =
    [
        "outcome", "reason", "exitCode", "filter", "total", "passed", "failed", "skipped",
        "elapsedSeconds", "firstFailure", "omittedFailingTests", "receiptPath", "trxPath", "outputLogPath"
    ];
    private static readonly string[] FullOnlyFields =
    [
        "trackedDirtyFiles", "declaredMutations", "testArguments", "childProcess", "worktreeStateAfter"
    ];

    [Xunit.Fact]
    public void PassingRun_UsesCompactDefaultAndPreservesFullOutput() => AssertBothModes(fail: false);

    [Xunit.Fact]
    public void EightFailureRun_UsesCompactDefaultAndPreservesFullOutput() => AssertBothModes(fail: true);

    private static void AssertBothModes(bool fail)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var scriptPath = Path.Combine(VerifiedRepositoryRoot.Find(), "scripts", "Invoke-IsolatedDotnet.ps1");
        var root = CreateTempDirectory();
        var sharedRoot = Path.Combine(ResolveSharedLocalAppData(), "Temp", "Low", "f", Guid.NewGuid().ToString("N")[..8]);
        var isolatedRoot = Path.Combine(sharedRoot, "dotnet");
        var workDirectory = Path.Combine(root, "repo");
        var shimDirectory = Path.Combine(root, "shim");
        var shimLog = Path.Combine(root, "dotnet.log");
        const string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests";
        const string projectFile = projectName + ".csproj";
        const string goalPrefix = "aaaaaaaa";
        Directory.CreateDirectory(workDirectory);
        Directory.CreateDirectory(shimDirectory);
        try
        {
            RunCommand("git", workDirectory, "init", "--initial-branch=main");
            RunCommand("git", workDirectory, "config", "user.email", "test@example.invalid");
            RunCommand("git", workDirectory, "config", "user.name", "Compact Receipt Test");
            File.WriteAllText(Path.Combine(workDirectory, projectFile), "<Project />");
            RunCommand("git", workDirectory, "add", projectFile);
            RunCommand("git", workDirectory, "commit", "-m", "base");
            File.AppendAllText(Path.Combine(workDirectory, ".git", "info", "exclude"), "\n.orchestrator/\n.mcg-sandbox/\n");
            PrepareFocusedArtifacts(workDirectory, projectFile, projectName, goalPrefix, isolatedRoot);
            File.WriteAllText(Path.Combine(shimDirectory, "dotnet.cmd"),
                "@echo off\r\n>> \"%DOTNET_SHIM_LOG%\" echo args=%*\r\nexit /b 99\r\n");

            foreach (var fullOutput in new[] { false, true })
            {
                var receiptPath = Path.Combine(root, fullOutput ? "full.json" : "compact.json");
                var startInfo = new ProcessStartInfo
                {
                    FileName = WorkerShell.Executable,
                    WorkingDirectory = workDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var argument in new[]
                {
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                    "-FocusedTest", "-GoalPrefix", goalPrefix, "-TestFilter", FixtureFilter,
                    "-ReceiptPath", receiptPath, "-BudgetSeconds", "120", "-LeaseWaitSeconds", "5",
                    "test", projectFile
                })
                {
                    startInfo.ArgumentList.Add(argument);
                }
                if (fullOutput)
                {
                    // Also prove that the value-free switch is case-insensitive and consumed after dotnet arguments.
                    startInfo.ArgumentList.Add("-fUlLoUtPuT");
                }
                foreach (var name in startInfo.Environment.Keys.Where(name => name.StartsWith("MCG_", StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    startInfo.Environment.Remove(name);
                }
                startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = isolatedRoot;
                startInfo.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = workDirectory;
                startInfo.Environment["PATH"] = shimDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
                startInfo.Environment["DOTNET_SHIM_LOG"] = shimLog;
                startInfo.Environment[FixtureArmedVariable] = fail ? "1" : "0";

                var result = TestChildProcessCapture.Run(startInfo, TimeSpan.FromSeconds(210));
                Assert.True(result.ExitCode == (fail ? 1 : 0),
                    $"Focused helper did not finish with the expected test verdict. Exit: {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");
                Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
                var line = Assert.Single(result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
                using var stdout = JsonDocument.Parse(line);
                using var file = JsonDocument.Parse(File.ReadAllText(receiptPath));
                var receipt = file.RootElement;
                var summary = stdout.RootElement;
                Assert.Equal(fail ? "FAIL" : "PASS", receipt.GetProperty("outcome").GetString());
                Assert.Equal(fail ? "test-failures" : null, receipt.GetProperty("reason").GetString());
                Assert.Equal(fail ? 1 : 0, receipt.GetProperty("exitCode").GetInt32());
                Assert.Equal(8, receipt.GetProperty("total").GetInt32());
                Assert.Equal(fail ? 0 : 8, receipt.GetProperty("passed").GetInt32());
                Assert.Equal(fail ? 8 : 0, receipt.GetProperty("failed").GetInt32());
                Assert.Equal(0, receipt.GetProperty("skipped").GetInt32());
                Assert.True(receipt.GetProperty("buildReused").GetBoolean());
                Assert.True(receipt.GetProperty("leaseReleased").GetBoolean());
                foreach (var field in FullOnlyFields)
                {
                    Assert.True(receipt.TryGetProperty(field, out _), $"Full receipt lost {field}.");
                }
                foreach (var field in new[] { "receiptPath", "trxPath", "outputLogPath" })
                {
                    Assert.True(File.Exists(receipt.GetProperty(field).GetString()), $"Missing artifact: {field}.");
                }
                Assert.Equal(receiptPath, receipt.GetProperty("receiptPath").GetString());
                // ProcessStartInfo resolves the real dotnet child; prepared artifacts avoid PowerShell's build shim.
                Assert.False(File.Exists(shimLog), "The prepared focused run unexpectedly invoked the dotnet shim.");
                if (fullOutput)
                {
                    Assert.True(JsonElement.DeepEquals(summary, receipt), "Full stdout differs from the receipt file.");
                    continue;
                }

                Assert.True(line.Length <= 2000, $"Compact receipt has {line.Length} characters.");
                Assert.Equal(CompactFields, summary.EnumerateObject().Select(property => property.Name).ToArray());
                foreach (var field in FullOnlyFields)
                {
                    Assert.False(summary.TryGetProperty(field, out _), $"Compact stdout leaked {field}.");
                }
                foreach (var field in CompactFields.Except(["firstFailure", "omittedFailingTests"]))
                {
                    Assert.True(JsonElement.DeepEquals(summary.GetProperty(field), receipt.GetProperty(field)), $"Changed compact value: {field}.");
                }
                Assert.Equal(fail ? 7 : 0, summary.GetProperty("omittedFailingTests").GetInt32());
                var firstFailure = summary.GetProperty("firstFailure");
                if (!fail)
                {
                    Assert.Equal(JsonValueKind.Null, firstFailure.ValueKind);
                    continue;
                }

                var failures = receipt.GetProperty("failingTests");
                Assert.Equal(8, failures.GetArrayLength());
                Assert.Equal(0, receipt.GetProperty("omittedFailingTests").GetInt32());
                Assert.Equal(new[] { "test", "message", "location" }, firstFailure.EnumerateObject().Select(property => property.Name).ToArray());
                Assert.Equal(failures[0].GetProperty("name").GetString(), firstFailure.GetProperty("test").GetString());
                var assertion = failures[0].GetProperty("assertion").GetString()!;
                Assert.True(assertion.Length > 300, "The fixture must exercise compact assertion truncation.");
                Assert.DoesNotContain('\n', assertion);
                Assert.Equal(assertion[..300] + "...", firstFailure.GetProperty("message").GetString());
                var trx = XDocument.Load(receipt.GetProperty("trxPath").GetString()!);
                var failedResult = trx.Descendants().First(element => element.Name.LocalName == "UnitTestResult" && (string?)element.Attribute("outcome") == "Failed");
                var stack = failedResult.Descendants().FirstOrDefault(element => element.Name.LocalName == "StackTrace")?.Value ?? string.Empty;
                var location = Regex.Match(stack, @"\bin (.+):line (\d+)");
                Assert.Equal(location.Success ? $"{location.Groups[1].Value.Trim()}:{location.Groups[2].Value}" : null,
                    firstFailure.GetProperty("location").GetString());
            }
        }
        finally
        {
            foreach (var directory in new[] { root, sharedRoot })
            {
                try
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch
                {
                    // Best effort after all helper child processes have exited.
                }
            }
        }
    }

    [Xunit.Collection(TestCollections.DotnetBuildSlots)]
    public sealed class DotnetBuildEnvironmentManagerTestsReceiptFixture
    {
        [Xunit.Theory]
        [Xunit.InlineData(1)]
        [Xunit.InlineData(2)]
        [Xunit.InlineData(3)]
        [Xunit.InlineData(4)]
        [Xunit.InlineData(5)]
        [Xunit.InlineData(6)]
        [Xunit.InlineData(7)]
        [Xunit.InlineData(8)]
        public void PreparedResult(int index)
        {
            if (Environment.GetEnvironmentVariable(FixtureArmedVariable) == "1")
            {
                Assert.Fail($"Failure {index}: {new string('x', 400)}\nSecond assertion line must not enter compact stdout.");
            }
        }
    }
}
