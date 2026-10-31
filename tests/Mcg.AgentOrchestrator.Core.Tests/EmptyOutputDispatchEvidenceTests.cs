using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: fixed timestamps and unique temp files, deleted after each case.
public sealed class EmptyOutputDispatchEvidenceTests
{
    private static readonly DateTimeOffset CompletedAt = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    [InlineData(-1, false)]
    public void SilentLaunch_ZeroByteFiles_PreservesDurationWindow(int seconds, bool expectedSilentLaunch)
    {
        WithEmptyOutputFiles(verification =>
        {
            verification = verification with { DispatchStartedAt = CompletedAt - TimeSpan.FromSeconds(seconds) };

            // Baseline 6cf01770e: DispatchFailureClassifier.cs:433-450 includes both boundaries.
            Assert.Equal(expectedSilentLaunch, EmptyOutputDispatchEvidence.IsSilentLaunchFailure(verification));
            // Baseline :418-421: nonzero exits remain flakes even outside the silent-launch window.
            Assert.True(EmptyOutputDispatchEvidence.IsTransientEmptyOutputDispatchFlake(verification));
        });
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(4228, false)]
    public void EmptyExitZero_HeartbeatBytes_DetermineFlake(long heartbeatBytes, bool expectedFlake)
    {
        WithEmptyOutputFiles(verification =>
        {
            verification = verification with { ExitCode = 0, HeartbeatStandardOutputBytes = heartbeatBytes };

            // Baseline 6cf01770e: DispatchFailureClassifier.cs:407-410,430.
            Assert.Equal(expectedFlake, EmptyOutputDispatchEvidence.IsTransientEmptyOutputDispatchFlake(verification));
            Assert.False(EmptyOutputDispatchEvidence.IsSilentLaunchFailure(verification));
        });
    }

    [Theory]
    [InlineData("RESOURCE cpu_ms=1\nCLASSIFIER rule=unknown-failure", true)]
    [InlineData("fatal: worker crashed", false)]
    [InlineData("RESOURCE cpu_ms=1\r\n  fatal: worker crashed  \r\nCLASSIFIER rule=unknown-failure", false)]
    public void StandardError_RealErrorWinsOverBookkeepingAndEmptyFile(string stderr, bool expectedEmpty)
    {
        WithEmptyOutputFiles(verification =>
        {
            verification = verification with { StandardError = stderr };

            // Baseline 6cf01770e: DispatchFailureClassifier.cs:418-421,480-517.
            // No start timestamp: force the flake predicate to assess substantive stderr itself.
            Assert.Equal(expectedEmpty, EmptyOutputDispatchEvidence.IsTransientEmptyOutputDispatchFlake(verification));
            // The empty redirected file must not hide real error text injected into the record.
            verification = verification with { DispatchStartedAt = CompletedAt - TimeSpan.FromSeconds(30) };
            Assert.Equal(expectedEmpty, EmptyOutputDispatchEvidence.IsSilentLaunchFailure(verification));
        });
    }

    private static void WithEmptyOutputFiles(Action<TaskVerificationRecord> assertions)
    {
        var prefix = Path.Combine(Path.GetTempPath(), $"mcg-empty-output-{Guid.NewGuid():N}");
        var stdoutPath = prefix + ".out.log";
        var stderrPath = prefix + ".err.log";
        try
        {
            File.WriteAllBytes(stdoutPath, []);
            File.WriteAllBytes(stderrPath, []);
            Assert.Equal(0L, new FileInfo(stdoutPath).Length);
            Assert.Equal(0L, new FileInfo(stderrPath).Length);

            assertions(new TaskVerificationRecord(
                "codex exec",
                "C:\\repo",
                1,
                string.Empty,
                string.Empty,
                CompletedAt,
                StandardOutputPath: stdoutPath,
                StandardErrorPath: stderrPath));
        }
        finally
        {
            File.Delete(stdoutPath);
            File.Delete(stderrPath);
        }
    }
}
