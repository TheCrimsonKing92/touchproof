using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierTestsStallTreeSnapshot
{
    private static readonly TimeSpan Failsafe = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Real_tree_records_cpu_advance_and_exited_descendant()
    {
        var root = Directory.CreateTempSubdirectory("mcg-stall-tree-").FullName;
        var token = Guid.NewGuid().ToString("N");
        using var ready = Event(token, "ready");
        using var mark = Event(token, "mark");
        using var advanced = Event(token, "advanced");
        using var release = Event(token, "release");
        using var exit = Event(token, "exit");
        using var exited = Event(token, "exited");
        using var cancellation = new CancellationTokenSource();
        var identityPath = Path.Combine(root, "identity.json");
        var spinnerPath = Path.Combine(root, $"spinner-{token}.ps1");
        var exitingPath = Path.Combine(root, $"exiting-{token}.ps1");
        var parentPath = Path.Combine(root, $"parent-{token}.ps1");
        File.WriteAllText(spinnerPath, $$"""
            $ErrorActionPreference = 'Stop'
            $mark = [Threading.EventWaitHandle]::OpenExisting('{{Name(token, "mark")}}')
            $advanced = [Threading.EventWaitHandle]::OpenExisting('{{Name(token, "advanced")}}')
            $release = [Threading.EventWaitHandle]::OpenExisting('{{Name(token, "release")}}')
            [void]$mark.WaitOne()
            $self = [Diagnostics.Process]::GetCurrentProcess()
            $baseline = $self.TotalProcessorTime.TotalSeconds
            do { for ($i = 0; $i -lt 10000; $i++) { $value = $i * $i }; $self.Refresh() }
            while ($self.TotalProcessorTime.TotalSeconds - $baseline -lt 0.5)
            [void]$advanced.Set()
            [void]$release.WaitOne()
            """);
        File.WriteAllText(exitingPath, $$"""
            $exit = [Threading.EventWaitHandle]::OpenExisting('{{Name(token, "exit")}}')
            [void]$exit.WaitOne()
            """);
        File.WriteAllText(parentPath, $$"""
            $ErrorActionPreference = 'Stop'
            function Start-Child([string] $path) {
                $info = [Diagnostics.ProcessStartInfo]::new()
                $info.FileName = Join-Path $PSHOME 'powershell.exe'
                $info.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $path + '"'
                $info.UseShellExecute = $false
                $info.CreateNoWindow = $true
                $info.RedirectStandardOutput = $true
                $info.RedirectStandardError = $true
                $child = [Diagnostics.Process]::Start($info)
                $child.BeginOutputReadLine()
                $child.BeginErrorReadLine()
                return $child
            }
            $spinner = Start-Child '{{Ps(spinnerPath)}}'
            $exiting = Start-Child '{{Ps(exitingPath)}}'
            [IO.File]::WriteAllText('{{Ps(identityPath)}}', (@{ Parent=$PID; Spinner=$spinner.Id; Exiting=$exiting.Id } | ConvertTo-Json -Compress))
            $ready = [Threading.EventWaitHandle]::OpenExisting('{{Name(token, "ready")}}')
            [void]$ready.Set()
            $exiting.WaitForExit()
            $exiting.Dispose()
            $exited = [Threading.EventWaitHandle]::OpenExisting('{{Name(token, "exited")}}')
            [void]$exited.Set()
            $release = [Threading.EventWaitHandle]::OpenExisting('{{Name(token, "release")}}')
            [void]$release.WaitOne()
            $spinner.WaitForExit()
            $spinner.Dispose()
            """);

        GoalAcceptanceVerifier.CommandResult? result = null;
        var run = AcceptanceProcessRunner.RunProcessForTestsAsync(
            ["powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", parentPath],
            root, TimeSpan.FromMinutes(5), cancellation.Token);
        try
        {
            await WaitEvent(ready, "parent published child identities");
            using var identities = JsonDocument.Parse(File.ReadAllText(identityPath));
            var parentPid = identities.RootElement.GetProperty("Parent").GetInt32();
            var spinnerPid = identities.RootElement.GetProperty("Spinner").GetInt32();
            var exitingPid = identities.RootElement.GetProperty("Exiting").GetInt32();
            var exitReleased = false;
            using var inputs = AcceptanceLaneStallTreeSnapshot.PushInputsForTests(new(
                Read: record =>
                {
                    if (record.ProcessId == exitingPid && !exitReleased)
                    {
                        exitReleased = true;
                        exit.Set();
                        if (!exited.WaitOne(Failsafe))
                            throw new TimeoutException("Expected event did not occur: parent reaped exiting descendant.");
                    }
                    return AcceptanceLaneStallTreeSnapshot.ReadProcess(record);
                },
                Delay: async (_, _) =>
                {
                    mark.Set();
                    await WaitEvent(advanced, "spinner advanced CPU after first reading");
                }, Deadline: Failsafe));
            var snapshot = await AcceptanceLaneStallTreeSnapshot.CaptureAsync(parentPid);

            Assert.Null(snapshot.Unavailable);
            var parent = Assert.Single(snapshot.Processes, entry => entry.Pid == parentPid);
            var spinner = Assert.Single(snapshot.Processes, entry => entry.Pid == spinnerPid);
            var gone = Assert.Single(snapshot.Processes, entry => entry.Pid == exitingPid);
            Assert.Contains($"parent-{token}", parent.CommandLine);
            Assert.Contains($"spinner-{token}", spinner.CommandLine);
            Assert.NotEmpty(parent.Image);
            Assert.NotEmpty(spinner.Image);
            Assert.True(parent.ParentPid > 0);
            Assert.Equal(parentPid, spinner.ParentPid);
            Assert.Equal(parentPid, gone.ParentPid);
            Assert.NotNull(parent.StartedAt);
            Assert.NotNull(spinner.StartedAt);
            Assert.NotNull(parent.CpuSecondsFirst);
            Assert.NotNull(parent.CpuSecondsSecond);
            Assert.NotNull(spinner.CpuSecondsFirst);
            Assert.NotNull(spinner.CpuSecondsSecond);
            Assert.True(spinner.CpuSecondsSecond > spinner.CpuSecondsFirst);
            Assert.Null(parent.Unreadable);
            Assert.Null(spinner.Unreadable);
            Assert.NotEmpty(spinner.Threads);
            Assert.NotNull(gone.Unreadable);
            Assert.DoesNotContain("TimeoutException", gone.Unreadable);
            Assert.Null(gone.CpuSecondsFirst);
            Assert.Null(gone.CpuSecondsSecond);
            release.Set();
            result = await run.WaitAsync(Failsafe); // Hang detector: parent exited and captures drained.
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            mark.Set();
            exit.Set();
            release.Set();
            await cancellation.CancelAsync();
            try { result ??= await run.WaitAsync(Failsafe); } catch (OperationCanceledException) { }
            DeleteCaptures(result);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Stall_emits_live_tree_before_kill_and_preserves_timeout_result()
    {
        var root = Directory.CreateTempSubdirectory("mcg-stall-invoker-").FullName;
        var heartbeatPath = Path.Combine(root, "heartbeat.json");
        var token = Guid.NewGuid().ToString("N");
        using var release = Event(token, "release");
        using var cancellation = new CancellationTokenSource();
        using var registry = WorkerProcessJobs.UseRegistryScopeForTests(null);
        var lines = new List<string>();
        ProcessInspectionRecord? liveAtEmission = null;
        SpawnProcessIdentity? identity = null;
        using var monitor = AcceptanceLaneStallMonitor.PushInputsForTests(TimeSpan.Zero, line =>
        {
            lines.Add(line);
            if (line.StartsWith("LANE_STALL_TREE ", StringComparison.Ordinal) && identity is not null)
            {
                var inspected = ProcessCommandLines.Snapshot([identity.ProcessId]);
                inspected.TryGetRecord(identity.ProcessId, out liveAtEmission);
            }
        });
        using var snapshotInputs = AcceptanceLaneStallTreeSnapshot.PushInputsForTests(new(
            Delay: (_, _) => Task.CompletedTask));
        string[] arguments = ["powershell", "-NoProfile", "-NonInteractive", "-Command",
            $"$gate = [Threading.EventWaitHandle]::OpenExisting('{Name(token, "release")}'); [void]$gate.WaitOne()"];
        var run = AcceptanceProcessRunner.RunProcessWithHeartbeatForTestsAsync(
            arguments, root, TimeSpan.FromMinutes(5), heartbeatPath,
            cleanupObserver: observation =>
            {
                if (observation.Stage != "started") return;
                // The release event keeps this owned root alive; stall monitoring starts after this callback.
                var processId = observation.ProcessId;
                using var process = Process.GetProcessById(processId);
                var read = WindowsNativeProcessInspection.ReadLifecycleIdentity(processId, process.SafeHandle);
                identity = read is { Status: ProcessInspectionStatus.Available,
                    StartedAt: { } startedAt, ExecutablePath: { Length: > 0 } image }
                    ? new SpawnProcessIdentity(processId, startedAt, image)
                    : throw new InvalidOperationException(
                        $"Owned stall child identity unavailable before stall monitoring: pid={processId}; {read}");
            }, cancellationToken: cancellation.Token, registrationIdentityReader: null);
        GoalAcceptanceVerifier.CommandResult? result = null;
        try
        {
            result = (await run.WaitAsync(Failsafe)).Result; // Hang detector: stall stop and owned-tree cleanup.
            Assert.Equal(2, lines.Count);
            Assert.StartsWith("LANE_STALLED ", lines[0]);
            Assert.StartsWith("LANE_STALL_TREE ", lines[1]);
            Assert.NotNull(identity);
            Assert.NotNull(liveAtEmission);
            Assert.Equal(ProcessInspectionStatus.Available, liveAtEmission.Status);
            Assert.Equal(identity.StartedAt, liveAtEmission.StartedAt);
            using var json = JsonDocument.Parse(lines[1]["LANE_STALL_TREE ".Length..]);
            var entries = json.RootElement.GetProperty("Snapshot").GetProperty("Processes").EnumerateArray();
            var entry = Assert.Single(entries, item => item.GetProperty("Pid").GetInt32() == identity.ProcessId);
            Assert.Equal(identity.StartedAt, entry.GetProperty("StartedAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("Unreadable").ValueKind);
            var after = ProcessCommandLines.Snapshot([identity.ProcessId]);
            Assert.False(after.TryGetRecord(identity.ProcessId, out var remaining) &&
                remaining.Status == ProcessInspectionStatus.Available && remaining.StartedAt == identity.StartedAt);
            Assert.True(result.TimedOut);
            Assert.Equal(-1, result.ExitCode);
            Assert.False(result.CaptureLimited);
            Assert.NotNull(result.LaneStallSilence);
            Assert.Equal(TimeSpan.Zero, result.LaneStallLimit);
            Assert.EndsWith(AcceptanceLaneStallPolicy.FormatStallSuffix(result.LaneStallSilence, TimeSpan.Zero),
                AcceptanceCommandResultSummary.BuildTimeoutSummary(result));
            Assert.Contains("reason=lane-stalled", AcceptanceCommandResultSummary.BuildTimeoutSummary(result));
            using var heartbeat = JsonDocument.Parse(File.ReadAllText(heartbeatPath));
            Assert.Equal("timed-out", heartbeat.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            release.Set();
            await cancellation.CancelAsync();
            try { result ??= (await run.WaitAsync(Failsafe)).Result; } catch (OperationCanceledException) { }
            DeleteCaptures(result);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Name(string token, string part) => $"Local\\mcg-stall-{token}-{part}";
    private static EventWaitHandle Event(string token, string part) =>
        new(false, EventResetMode.ManualReset, Name(token, part));
    private static string Ps(string value) => value.Replace("'", "''");

    private static async Task WaitEvent(EventWaitHandle signal, string expected)
    {
        if (!await Task.Run(() => signal.WaitOne(Failsafe)))
            throw new TimeoutException($"Expected event did not occur: {expected}.");
    }

    private static void DeleteCaptures(GoalAcceptanceVerifier.CommandResult? result)
    {
        if (result?.StdoutPath is { } stdout) File.Delete(stdout);
        if (result?.StderrPath is { } stderr) File.Delete(stderr);
    }
}
