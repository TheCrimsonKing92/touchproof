using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GateReplayReceiptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaunchPersistsFinalEnvironmentUnlessCapturesAreSuppressed(bool suppress)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        var testResults = Path.Combine(root, "check.trx");
        var timing = Path.Combine(root, "check.slot-timing.jsonl");
        var context = CreateContext(root) with { DeclaredTestResultPaths = [testResults] };
        var candidateRoot = Path.Combine(root, "candidate override");
        Directory.CreateDirectory(candidateRoot);
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
            : "/bin/sh";
        string[] arguments = OperatingSystem.IsWindows()
            ? [executable, "-NoProfile", "-NonInteractive", "-Command",
                """
                [IO.File]::WriteAllText((Join-Path $env:TEMP 'check.trx'), 'test output')
                [IO.File]::WriteAllText($env:MCG_TEST_SLOT_TIMING_FILE, 'timing output')
                [Console]::Write($env:MCG_ORCHESTRATOR_REPOSITORY_ROOT)
                """]
            : [executable, "-c",
                """
                printf 'test output' > "$TEMP/check.trx"
                printf 'timing output' > "$MCG_TEST_SLOT_TIMING_FILE"
                printf '%s' "$MCG_ORCHESTRATOR_REPOSITORY_ROOT"
                """];
        var attempt = context.AttemptIdentity!;
        string? runningJson = null;
        GateHeartbeatSnapshot? final = null;
        try
        {
            var result = await AcceptanceCheckProcessInvoker.RunAsync(arguments, root, TimeSpan.FromMinutes(2),
                forceUtf8ConsoleOutput: false, TestContext.Current.CancellationToken, default, null,
                new AcceptanceProcessRequest
                {
                    HeartbeatContext = context,
                    ProgressSink = progress => runningJson ??= File.ReadAllText(progress.HeartbeatPath),
                    KeepCaptureFiles = true,
                    SuppressDiagnosticCaptures = suppress,
                    GateInvocationId = attempt.AttemptId,
                    ApparatusReceiptPath = Path.Combine(root, "apparatus.jsonl"),
                    ChildEnvironmentOverrides = new Dictionary<string, string?>
                    {
                        ["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = candidateRoot,
                        ["TEMP"] = root,
                        ["TMP"] = root,
                        [LaneSlotTimingReader.VariableName] = timing,
                        [TempRootApparatusLossReceiptStore.ReceiptPathVariable] = null,
                        ["MCG_ACCEPTANCE_CHANGE_SCOPED"] = "true"
                    }
                });

            final = Assert.IsType<GateHeartbeatSnapshot>(GateHeartbeatArtifacts.TryReadSnapshot(context.HeartbeatPath));
            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(runningJson);
            Assert.Contains("\"state\": \"running\"", runningJson, StringComparison.Ordinal);
            var finalJson = File.ReadAllText(context.HeartbeatPath);
            if (suppress)
            {
                Assert.Null(final.ReplayReceipt);
                Assert.Null(final.CommandLine);
                Assert.Null(final.StdoutPath);
                Assert.Null(final.StderrPath);
                Assert.DoesNotContain("\"replayReceipt\"", runningJson, StringComparison.Ordinal);
                Assert.DoesNotContain("\"replayReceipt\"", finalJson, StringComparison.Ordinal);
                return;
            }

            Assert.Equal(candidateRoot, result.StandardOutput);
            var receipt = Assert.IsType<GateReplayReceipt>(final.ReplayReceipt);
            Assert.Contains("\"replayReceipt\"", runningJson, StringComparison.Ordinal);
            Assert.Equal(attempt.GoalId, receipt.GoalId);
            Assert.Equal(attempt.AttemptId, receipt.AttemptId);
            Assert.Equal(context.CurrentTarget, receipt.CheckIdentity);
            Assert.Equal(root, receipt.WorkingDirectory);
            Assert.Equal(executable, receipt.ExecutablePath);
            Assert.Equal(arguments.Skip(1), receipt.Arguments.AsEnumerable());
            var expectedEnvironment = new Dictionary<string, string>
            {
                ["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = candidateRoot,
                ["TEMP"] = root,
                ["TMP"] = root,
                [TempRootApparatusLossReceiptStore.GateInvocationIdVariable] = attempt.AttemptId,
                ["MCG_ACCEPTANCE_CHANGE_SCOPED"] = "true",
                [LaneSlotTimingReader.VariableName] = timing
            };
            var removedName = TempRootApparatusLossReceiptStore.ReceiptPathVariable;
            Assert.False(receipt.Environment.ContainsKey(removedName));
            Assert.Equal(
                [
                    new GateReplayArtifact(GateReplayArtifactRole.Stdout, final.StdoutPath!),
                    new GateReplayArtifact(GateReplayArtifactRole.Stderr, final.StderrPath!),
                    new GateReplayArtifact(GateReplayArtifactRole.TestResults, testResults),
                    new GateReplayArtifact(GateReplayArtifactRole.Timing, timing)
                ],
                receipt.ExpectedArtifacts.AsEnumerable());

            var validation = GateReplayReceiptValidator.Validate(receipt,
                new(attempt.VerifyingCommitSha, attempt.MainSha, executable,
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))),
                    [
                        new(GateReplayArtifactRole.Stdout, final.StdoutPath!, File.Exists(final.StdoutPath)),
                        new(GateReplayArtifactRole.Stderr, final.StderrPath!, File.Exists(final.StderrPath)),
                        new(GateReplayArtifactRole.TestResults, testResults, File.Exists(testResults)),
                        new(GateReplayArtifactRole.Timing, timing, File.Exists(timing))
                    ]));
            Assert.Equal(GateReplayDiagnostic.Ready, validation.Diagnostic);
            var replay = Assert.IsType<GateReplayDescription>(validation.Replay);
            foreach (var (name, value) in expectedEnvironment)
            {
                Assert.Equal(value, receipt.Environment[name]);
                Assert.Contains($"$launch.Environment['{name}'] = '{value.Replace("'", "''", StringComparison.Ordinal)}'",
                    replay.Command, StringComparison.Ordinal);
            }
            Assert.Contains($"[void]$launch.Environment.Remove('{removedName}')", replay.Command, StringComparison.Ordinal);
            Assert.DoesNotContain($"$launch.Environment['{removedName}']", replay.Command, StringComparison.Ordinal);
        }
        finally
        {
            if (final?.StdoutPath is { } stdoutPath) File.Delete(stdoutPath);
            if (final?.StderrPath is { } stderrPath) File.Delete(stderrPath);
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    internal static GateHeartbeatContext CreateContext(string root) =>
        new("goal-1", "verification-check", "ordinary check", null, Path.Combine(root, GateHeartbeatArtifacts.FileName),
            null, "display only", null)
        {
            AttemptIdentity = new("goal-1-0-20261120123456789-00112233445566778899aabbccddeeff",
                "goal-1", root, new string('a', 40), new string('b', 40), new string('c', 40),
                Path.Combine(root, "results"), null, Environment.ProcessId, null)
        };
}
