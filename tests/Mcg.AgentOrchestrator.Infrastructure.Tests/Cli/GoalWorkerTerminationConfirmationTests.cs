using System.ComponentModel;
using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.App.Cli.GoalWorkerTermination;

// Parallel-safe: fake probes are local, and the real-process fact owns its child and handles.
public sealed class GoalWorkerTerminationConfirmationTests
{
    [Xunit.Fact]
    public void ConfirmExit_AllAbsent_Confirmed()
    {
        RecordedProcessCapture[] captures =
        [new(101, RecordedProcessCaptureKind.Absent), new(102, RecordedProcessCaptureKind.Absent)];

        Xunit.Assert.Equal(GoalWorkerTerminationOutcome.ConfirmedTerminated,
            ConfirmExit(captures, TimeSpan.Zero));
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void ConfirmExit_PartialExit_Unconfirmed(bool firstExited)
    {
        var calls = new List<int>();
        RecordedProcessCapture[] captures =
        [
            new(101, RecordedProcessCaptureKind.Held, _ => { calls.Add(101); return firstExited; }),
            new(102, RecordedProcessCaptureKind.Held, _ => { calls.Add(102); return !firstExited; })
        ];

        Xunit.Assert.Equal(GoalWorkerTerminationOutcome.TerminationUnconfirmed,
            ConfirmExit(captures, TimeSpan.Zero));
        Xunit.Assert.Equal(firstExited ? new[] { 101, 102 } : [101], calls);
    }

    [Xunit.Fact]
    public void ConfirmExit_AllHandlesExited_ConfirmedWithSharedBudget()
    {
        var clock = new ConfirmationClock();
        var waits = new List<TimeSpan>();
        bool Wait(TimeSpan budget)
        {
            waits.Add(budget);
            clock.Advance(TimeSpan.FromSeconds(4));
            return true;
        }
        RecordedProcessCapture[] captures =
        [
            new(101, RecordedProcessCaptureKind.Held, Wait),
            new(102, RecordedProcessCaptureKind.Held, Wait),
            new(103, RecordedProcessCaptureKind.Held, Wait)
        ];

        Xunit.Assert.Equal(GoalWorkerTerminationOutcome.ConfirmedTerminated,
            ConfirmExit(captures, TimeSpan.FromSeconds(5), clock));
        Xunit.Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.Zero], waits);
    }

    [Xunit.Fact]
    public void ConfirmExit_NoRecordedProcesses_Unconfirmed()
    {
        Xunit.Assert.Equal(GoalWorkerTerminationOutcome.TerminationUnconfirmed,
            ConfirmExit([], TimeSpan.Zero));
    }

    [Xunit.Fact]
    public void ConfirmExit_UnverifiableOrMissingHandle_Unconfirmed()
    {
        foreach (var kind in new[] { RecordedProcessCaptureKind.Unverifiable, RecordedProcessCaptureKind.Held })
        {
            RecordedProcessCapture[] captures = [new(101, RecordedProcessCaptureKind.Absent), new(102, kind)];
            Xunit.Assert.Equal(GoalWorkerTerminationOutcome.TerminationUnconfirmed,
                ConfirmExit(captures, TimeSpan.Zero));
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("denied")]
    [Xunit.InlineData("unreadable")]
    [Xunit.InlineData("other")]
    public void ConfirmExit_WaitFails_Unconfirmed(string failure)
    {
        RecordedProcessCapture[] captures =
            [new(101, RecordedProcessCaptureKind.Held, _ => throw ProbeFailure(failure))];

        Xunit.Assert.Equal(GoalWorkerTerminationOutcome.TerminationUnconfirmed,
            ConfirmExit(captures, TimeSpan.Zero));
    }

    [Xunit.Theory]
    [Xunit.InlineData("denied")]
    [Xunit.InlineData("unreadable")]
    [Xunit.InlineData("other")]
    public void Capture_ProbeFails_Unconfirmed(string failure)
    {
        var calls = 0;
        var captures = CaptureRecordedProcesses(Record(101), _ =>
        {
            calls++;
            throw ProbeFailure(failure);
        });

        Xunit.Assert.Equal(1, calls);
        Xunit.Assert.Equal(RecordedProcessCaptureKind.Unverifiable, Xunit.Assert.Single(captures).Kind);
        Xunit.Assert.Equal(GoalWorkerTerminationOutcome.TerminationUnconfirmed,
            ConfirmExit(captures, TimeSpan.Zero));
    }

    [Xunit.Fact]
    public void Capture_UnreadableHandle_Unconfirmed()
    {
        using var unattached = new Process();
        var captures = CaptureRecordedProcesses(Record(101), _ => unattached);

        Xunit.Assert.Equal(RecordedProcessCaptureKind.Unverifiable, Xunit.Assert.Single(captures).Kind);
        Xunit.Assert.Equal(GoalWorkerTerminationOutcome.TerminationUnconfirmed,
            ConfirmExit(captures, TimeSpan.Zero));
    }

    [Xunit.Fact]
    public void Capture_MissingHostAndDistinctDescendants_Confirmed()
    {
        var calls = new List<int>();
        var record = Record(101) with { OwnedProcessIds = [102, 102] };
        var captures = CaptureRecordedProcesses(record, pid =>
        {
            calls.Add(pid);
            throw new ArgumentException("Recorded process is absent.");
        });

        Xunit.Assert.Equal([102, 101], calls);
        Xunit.Assert.All(captures, capture => Xunit.Assert.Equal(RecordedProcessCaptureKind.Absent, capture.Kind));
        Xunit.Assert.Equal(GoalWorkerTerminationOutcome.ConfirmedTerminated,
            ConfirmExit(captures, TimeSpan.Zero));
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(-1)]
    public void Capture_InvalidProcessId_Unconfirmed(int pid)
    {
        var calls = 0;
        var captures = CaptureRecordedProcesses(Record(pid), _ =>
        {
            calls++;
            throw new ArgumentException("Invalid pid must not be probed.");
        });

        Xunit.Assert.Equal(0, calls);
        Xunit.Assert.Equal(RecordedProcessCaptureKind.Unverifiable, Xunit.Assert.Single(captures).Kind);
        Xunit.Assert.Equal(GoalWorkerTerminationOutcome.TerminationUnconfirmed,
            ConfirmExit(captures, TimeSpan.Zero));
    }

    [Xunit.Fact]
    public void Receipt_OutcomeOmitted_Unconfirmed()
    {
        var receipt = new GoalWorkerTerminationReceipt(TaskId.New(), Record(101),
            CancellationCandidateEvidence.Indeterminate("test receipt"), []);

        Xunit.Assert.Equal(GoalWorkerTerminationOutcome.TerminationUnconfirmed, receipt.Outcome);
    }

    [Xunit.Fact]
    public async Task Capture_OwnedChild_ConfirmsOnlyAfterHandleExit()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("30");
        start.ArgumentList.Add("127.0.0.1");
        start.Environment.Clear();
        start.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Owned confirmation child did not start.");
        child.StandardInput.Close();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdout = DrainOutput(child.StandardOutput, ready);
        var stderr = child.StandardError.ReadToEndAsync();
        IReadOnlyList<RecordedProcessCapture> captures = [];
        try
        {
            try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException exception)
            {
                throw new TimeoutException("Owned confirmation child did not emit readiness output.", exception);
            }
            Xunit.Assert.False(child.HasExited, "Owned confirmation child exited before capture.");
            var record = Record(child.Id) with
            {
                StartedAt = new DateTimeOffset(child.StartTime),
                ProcessIdentityStartedAt = new DateTimeOffset(child.StartTime),
                ProcessIdentityImagePath = child.MainModule!.FileName
            };
            captures = CaptureRecordedProcesses(record);
            Xunit.Assert.Equal(RecordedProcessCaptureKind.Held, Xunit.Assert.Single(captures).Kind);
            Xunit.Assert.Equal(GoalWorkerTerminationOutcome.TerminationUnconfirmed,
                ConfirmExit(captures, TimeSpan.Zero));

            using var reusedPid = Xunit.Assert.Single(CaptureRecordedProcesses(record with
            {
                ProcessIdentityStartedAt = record.ProcessIdentityStartedAt!.Value.AddMinutes(1)
            }));
            Xunit.Assert.Equal(RecordedProcessCaptureKind.Absent, reusedPid.Kind);
            using var differentImage = Xunit.Assert.Single(CaptureRecordedProcesses(record with
            {
                ProcessIdentityImagePath = record.ProcessIdentityImagePath + ".different"
            }));
            Xunit.Assert.Equal(RecordedProcessCaptureKind.Unverifiable, differentImage.Kind);
            Xunit.Assert.Equal(GoalWorkerTerminationOutcome.TerminationUnconfirmed,
                ConfirmExit([differentImage], TimeSpan.Zero));
            Xunit.Assert.False(child.HasExited, "Owned confirmation child exited before the image-mismatch check.");

            child.Kill();
            Xunit.Assert.True(child.WaitForExit(TimeSpan.FromSeconds(30)), "Owned confirmation child did not exit after Kill.");

            Xunit.Assert.Equal(GoalWorkerTerminationOutcome.ConfirmedTerminated,
                ConfirmExit(captures, TimeSpan.Zero));
        }
        finally
        {
            foreach (var capture in captures) capture.Dispose();
            if (!child.HasExited) child.Kill();
            Xunit.Assert.True(child.WaitForExit(TimeSpan.FromSeconds(30)), "Owned confirmation child did not exit during cleanup.");
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private static async Task DrainOutput(StreamReader output, TaskCompletionSource ready)
    {
        while (await output.ReadLineAsync() is { } line)
            if (!string.IsNullOrWhiteSpace(line)) ready.TrySetResult();
    }

    private static TaskProcessRecord Record(int pid) => new(pid, "test dispatch", "worktree",
        "stdout", "stderr", "exit", DateTimeOffset.UnixEpoch, null, null);

    private static Exception ProbeFailure(string failure) => failure switch
    {
        "denied" => new Win32Exception(5),
        "unreadable" => new InvalidOperationException("Process evidence is unavailable."),
        _ => new IOException("Process probe failed.")
    };

    private sealed class ConfirmationClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
}
