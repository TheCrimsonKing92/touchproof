using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns a unique directory; process identity is always injected.
public sealed class ConductorLeaseLivenessProbeTests
{
    private static readonly DateTimeOffset WrittenAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StartedAt = WrittenAt.AddHours(-1);
    private const int OwnerPid = 424242;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsentLock_ReturnsNotRunningWithoutCheckingPid(bool missingDirectory)
    {
        WithDirectory(root =>
        {
            var directory = missingDirectory ? Path.Combine(root, "absent") : root;
            var probe = new RecordingPidProbe(true, true);
            Assert.Equal(ConductorLiveness.NotRunning, ConductorLoopLease.ProbeLiveness(directory, probe));
            Assert.False(ConductorLoopLease.IsActive(directory, probe));
            Assert.Empty(probe.Calls);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RecordedOwner_UsesMatchingIdentityRuleAndPreservesBoolResult(
        bool hasStartTime, bool live)
    {
        WithDirectory(root =>
        {
            WriteOwner(root, hasStartTime);
            // The unused check deliberately disagrees, proving the correct identity branch was used.
            var probe = new RecordingPidProbe(hasStartTime ? !live : live, hasStartTime ? live : !live);
            Assert.Equal(live ? ConductorLiveness.Running : ConductorLiveness.NotRunning,
                ConductorLoopLease.ProbeLiveness(root, probe));
            Assert.Equal(live, ConductorLoopLease.IsActive(root, probe));
            Assert.Equal([(OwnerPid, hasStartTime ? (DateTimeOffset?)StartedAt : null),
                (OwnerPid, hasStartTime ? (DateTimeOffset?)StartedAt : null)], probe.Calls);
        });
    }

    [Fact]
    public void ExclusiveLockRead_ReturnsUnknownWhileBoolRemainsFalse()
    {
        WithDirectory(root =>
        {
            WriteOwner(root, true);
            var probe = new RecordingPidProbe(true, true);
            using (var held = new FileStream(Path.Combine(root, "conduct-loop.lock"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<IOException>(() => File.ReadAllText(held.Name));
                Assert.Equal(ConductorLiveness.Unknown, ConductorLoopLease.ProbeLiveness(root, probe));
                Assert.False(ConductorLoopLease.IsActive(root, probe));
                Assert.Empty(probe.Calls);
            }
            Assert.Equal(ConductorLiveness.Running, ConductorLoopLease.ProbeLiveness(root, probe));
            Assert.Equal([(OwnerPid, (DateTimeOffset?)StartedAt)], probe.Calls);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid-pid\n2026-01-01T12:00:00.0000000+00:00\n")]
    [InlineData("424242\ninvalid-timestamp\n")]
    public void MalformedOwner_ReturnsUnknownWhileBoolStillThrows(string contents)
    {
        WithDirectory(root =>
        {
            File.WriteAllText(Path.Combine(root, "conduct-loop.lock"), contents);
            var probe = new RecordingPidProbe(true, true);
            Assert.Equal(ConductorLiveness.Unknown, ConductorLoopLease.ProbeLiveness(root, probe));
            Assert.Throws<InvalidOperationException>(() => ConductorLoopLease.IsActive(root, probe));
            Assert.Empty(probe.Calls);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ObservationException_ReturnsUnknownWhileBoolRemainsFalse(
        bool hasStartTime, bool denied)
    {
        WithDirectory(root =>
        {
            WriteOwner(root, hasStartTime);
            Exception failure = denied ? new UnauthorizedAccessException("denied") : new IOException("unavailable");
            var probe = new RecordingPidProbe(false, false, failure);
            Assert.Equal(ConductorLiveness.Unknown, ConductorLoopLease.ProbeLiveness(root, probe));
            Assert.False(ConductorLoopLease.IsActive(root, probe));
            Assert.Equal(2, probe.Calls.Count);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonObservationPidException_PropagatesFromBothApis(bool hasStartTime)
    {
        WithDirectory(root =>
        {
            WriteOwner(root, hasStartTime);
            var failure = new InvalidOperationException("pid probe failed");
            var probe = new RecordingPidProbe(false, false, failure);
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
                ConductorLoopLease.ProbeLiveness(root, probe)));
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
                ConductorLoopLease.IsActive(root, probe)));
            Assert.Equal(2, probe.Calls.Count);
        });
    }

    private static void WriteOwner(string root, bool hasStartTime)
    {
        var lines = new List<string> { OwnerPid.ToString(CultureInfo.InvariantCulture),
            WrittenAt.ToString("O", CultureInfo.InvariantCulture) };
        if (hasStartTime) lines.Add(StartedAt.ToString("O", CultureInfo.InvariantCulture));
        File.WriteAllLines(Path.Combine(root, "conduct-loop.lock"), lines);
    }

    private static void WithDirectory(Action<string> assertion)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try { assertion(root); }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private sealed class RecordingPidProbe(bool running, bool sameProcess, Exception? failure = null)
        : IConductLockPidProbe
    {
        internal List<(int Pid, DateTimeOffset? StartedAt)> Calls { get; } = [];

        public bool IsRunning(int processId) => Record(processId, null, running);
        public bool IsSameProcess(int processId, DateTimeOffset processStartedAt) =>
            Record(processId, processStartedAt, sameProcess);

        private bool Record(int pid, DateTimeOffset? startedAt, bool result)
        {
            Calls.Add((pid, startedAt));
            if (failure is not null) throw failure;
            return result;
        }
    }
}
