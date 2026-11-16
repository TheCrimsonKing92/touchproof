using System.Globalization;
using System.ComponentModel;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableOwner_ReturnsUnknownWithEitherLeaseFormat(bool hasStartTime)
    {
        WithDirectory(root =>
        {
            WriteOwner(root, hasStartTime);
            IConductLockPidProbe probe = new UnavailablePidProbe();
            Assert.Equal(ConductorLiveness.Unknown, ConductorLoopLease.ProbeLiveness(root, probe));
        });
    }

    [Theory]
    [InlineData("missing", false, "Gone", "NotRunning")]
    [InlineData("missing", true, "Gone", "NotRunning")]
    [InlineData("win32", false, "Unavailable", "Unknown")]
    [InlineData("win32", true, "Unavailable", "Unknown")]
    [InlineData("invalid-state", false, "Unavailable", "Unknown")]
    [InlineData("invalid-state", true, "Unavailable", "Unknown")]
    public void ProcessReadFailure_ClassifiesAbsenceSeparatelyFromUnavailable(
        string failureKind, bool hasStartTime, string outcome, string liveness)
    {
        WithDirectory(root =>
        {
            WriteOwner(root, hasStartTime);
            Exception failure = failureKind switch
            {
                "missing" => new ArgumentException("synthetic process is absent"),
                "win32" => new Win32Exception(5),
                _ => new InvalidOperationException("synthetic process state is unreadable")
            };
            var reads = new List<(int Pid, bool ReadStartTime)>();
            IConductLockPidProbe probe = new ConductLockPidProbe((pid, readStartTime) =>
            {
                reads.Add((pid, readStartTime));
                throw failure;
            });
            Assert.Equal(outcome, probe.Probe(OwnerPid, hasStartTime ? StartedAt : null).ToString());
            Assert.Equal(liveness, ConductorLoopLease.ProbeLiveness(root, probe).ToString());
            Assert.False(probe.IsRunning(OwnerPid));
            Assert.False(probe.IsSameProcess(OwnerPid, StartedAt));
            Assert.Equal([(OwnerPid, hasStartTime), (OwnerPid, hasStartTime),
                (OwnerPid, false), (OwnerPid, true)], reads);
        });
    }

    [Theory]
    [InlineData(false, true, false, "Alive", "Running", true)]
    [InlineData(false, true, true, "Alive", "Running", true)]
    [InlineData(true, true, false, "Gone", "NotRunning", false)]
    [InlineData(true, true, true, "Gone", "NotRunning", false)]
    [InlineData(false, false, false, "Alive", "Running", false)]
    [InlineData(false, false, true, "Gone", "NotRunning", false)]
    public void ProcessSnapshot_PreservesIdentityAndBooleanResults(
        bool exited, bool matchingStartTime, bool hasStartTime, string outcome,
        string liveness, bool sameProcess)
    {
        WithDirectory(root =>
        {
            WriteOwner(root, hasStartTime);
            var reads = new List<(int Pid, bool ReadStartTime)>();
            IConductLockPidProbe probe = new ConductLockPidProbe((pid, readStartTime) =>
            {
                reads.Add((pid, readStartTime));
                return (exited, readStartTime
                    ? (matchingStartTime ? StartedAt : StartedAt.AddMinutes(1)) : default);
            });
            var recordedStart = hasStartTime ? StartedAt.ToOffset(TimeSpan.FromHours(2)) : (DateTimeOffset?)null;
            Assert.Equal(outcome, probe.Probe(OwnerPid, recordedStart).ToString());
            Assert.Equal(liveness, ConductorLoopLease.ProbeLiveness(root, probe).ToString());
            Assert.Equal(!exited, probe.IsRunning(OwnerPid));
            Assert.Equal(sameProcess, probe.IsSameProcess(OwnerPid, StartedAt));
            Assert.Equal([(OwnerPid, hasStartTime), (OwnerPid, hasStartTime),
                (OwnerPid, false), (OwnerPid, true)], reads);
        });
    }

    private sealed class UnavailablePidProbe : IConductLockPidProbe
    {
        public bool IsRunning(int processId) => throw new InvalidOperationException("Use the three-state probe");
        public bool IsSameProcess(int processId, DateTimeOffset processStartedAt) =>
            throw new InvalidOperationException("Use the three-state probe");
        public ConductLockProbeOutcome Probe(int processId, DateTimeOffset? processStartedAt = null) =>
            ConductLockProbeOutcome.Unavailable;
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
