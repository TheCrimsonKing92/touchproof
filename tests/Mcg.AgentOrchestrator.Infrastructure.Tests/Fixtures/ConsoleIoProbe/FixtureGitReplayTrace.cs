using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;

[SupportedOSPlatform("windows")]
internal static class FixtureGitReplayTrace
{
    private const string Provider = "Microsoft-Windows-Kernel-Process";
    private const int TraceLimitMiB = 64;
    private const int JobTimeoutSeconds = 900;

    internal sealed record Job(int RootPid, long RootStartFileTime, uint ProcessCount, long UserCpu100ns,
        long KernelCpu100ns, int ExitCode)
    {
        public long Cpu100ns => checked(UserCpu100ns + KernelCpu100ns);
    }

    internal sealed record Start(int Pid, int ParentPid, string Sequence, string ParentSequence, long StartFiletime,
        string Image, string? CommandLine);

    private sealed record TraceLoss(uint EventsLost, uint FileBuffersLost, uint RealTimeBuffersLost);

    internal static Job Capture(ProcessStartInfo runner, string directory, string trace)
    {
        var session = $"McgFixtureGit-{Guid.NewGuid():N}";
        var started = DateTimeOffset.UtcNow;
        FixtureGitReplay.RunNative(directory, "logman.exe", "create", "trace", session, "-ets", "-p", Provider,
            "0x10", "4", "-o", trace, "-f", "bin", "-max", TraceLimitMiB.ToString(CultureInfo.InvariantCulture));
        Job? job = null;
        TraceLoss loss;
        try
        {
            job = RunContained(runner, directory);
        }
        finally
        {
            loss = StopTrace(directory, session);
            FixtureGitReplay.WriteJson(Path.Combine(directory, "capture.json"), new
            {
                job, loss, trace_start_utc = started, trace_end_utc = DateTimeOffset.UtcNow,
                trace_provider = Provider, trace_limit_mib = TraceLimitMiB, job_timeout_seconds = JobTimeoutSeconds
            });
        }

        if (loss is not { EventsLost: 0, FileBuffersLost: 0, RealTimeBuffersLost: 0 })
            throw new InvalidOperationException($"ETW session {session} lost events or buffers. See {directory}.");
        if (job.ExitCode != 0)
            throw new InvalidOperationException($"Replay runner exited {job.ExitCode}. See {directory}.");
        return job;
    }

    private static Job RunContained(ProcessStartInfo runner, string directory)
    {
        using var launch = OwnedProcessGroup.StartSuspendedContainedWithFileCapture(runner,
            Path.Combine(directory, "runner.log"), Path.Combine(directory, "runner.stderr.log"));
        try
        {
            if (!launch.Group.TryDuplicateAccountingHandle(out var job))
                throw new InvalidOperationException("Replay job accounting handle is unavailable.");
            using (job)
            {
                var pid = launch.Process.Id;
                var start = launch.Process.StartTime.ToFileTimeUtc();
                launch.Resume();
                if (!OwnedProcessGroup.WaitForJobExit(job, TimeSpan.FromSeconds(JobTimeoutSeconds)))
                    throw new InvalidOperationException($"Replay job drain could not be confirmed within {JobTimeoutSeconds} seconds.");
                if (!QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read final replay job accounting.");
                var exitCode = launch.TryReadOwnedExitCode()
                    ?? throw new InvalidOperationException("Replay runner exit code is unavailable after job drain.");
                return new Job(pid, start, accounting.TotalProcesses, accounting.UserTime, accounting.KernelTime, exitCode);
            }
        }
        catch (Exception failure)
        {
            if (!launch.Group.TerminateAndConfirmEmpty(TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Could not confirm teardown of the replay job.", failure);
            throw;
        }
    }

    private static TraceLoss StopTrace(string directory, string session)
    {
        var offset = Marshal.SizeOf<TraceProperties>();
        const int nameBuffer = 1024 * sizeof(char);
        var size = offset + 2 * nameBuffer;
        var properties = new TraceProperties
        {
            Header = new TraceHeader { BufferSize = (uint)size },
            LoggerNameOffset = (uint)offset,
            LogFileNameOffset = (uint)(offset + nameBuffer)
        };
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(properties, buffer, false);
            var status = ControlTrace(0, session, buffer, 1);
            if (status != 0)
            {
                var failure = new Win32Exception((int)status, $"Could not read stop counters for ETW session {session}.");
                try { FixtureGitReplay.RunNative(directory, "logman.exe", "stop", session, "-ets"); }
                catch (Exception fallback) { throw new AggregateException($"ETW session {session} teardown is unconfirmed.", failure, fallback); }
                throw new InvalidOperationException($"logman stopped ETW session {session}. Loss counters are unavailable.", failure);
            }

            var result = Marshal.PtrToStructure<TraceProperties>(buffer);
            return new TraceLoss(result.EventsLost, result.LogBuffersLost, result.RealTimeBuffersLost);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static List<Start> ReadTree(string path, Job job)
    {
        if (new FileInfo(path).Length >= TraceLimitMiB * 1024L * 1024)
            throw new InvalidOperationException($"ETW reached its size bound. See {path}.");

        var nodesByParent = new Dictionary<string, List<Start>>(StringComparer.Ordinal);
        Start? root = null;
        using var reader = new EventLogReader(new EventLogQuery(path, PathType.FilePath,
            $"*[System[Provider[@Name='{Provider}'] and EventID=1]]"));
        while (reader.ReadEvent() is { } record)
        {
            using (record)
            {
                var xml = XElement.Parse(record.ToXml());
                var ns = xml.Name.Namespace;
                var fields = xml.Element(ns + "EventData")!.Elements(ns + "Data")
                    .ToDictionary(item => (string)item.Attribute("Name")!, item => item.Value, StringComparer.Ordinal);
                var createTime = fields["CreateTime"];
                var start = long.TryParse(createTime, CultureInfo.InvariantCulture, out var filetime)
                    ? filetime : DateTimeOffset.Parse(createTime, CultureInfo.InvariantCulture).ToFileTime();
                if (start < job.RootStartFileTime) continue;
                var node = new Start(int.Parse(fields["ProcessID"], CultureInfo.InvariantCulture),
                    int.Parse(fields["ParentProcessID"], CultureInfo.InvariantCulture), fields["ProcessSequenceNumber"],
                    fields["ParentProcessSequenceNumber"], start, fields["ImageName"], fields.GetValueOrDefault("CommandLine"));
                if (node.Pid == job.RootPid && node.StartFiletime == job.RootStartFileTime)
                {
                    if (root is not null) throw new InvalidDataException("Duplicate replay root in the native process trace.");
                    root = node;
                }
                if (!nodesByParent.TryGetValue(node.ParentSequence, out var siblings))
                    nodesByParent.Add(node.ParentSequence, siblings = []);
                siblings.Add(node);
            }
        }

        var tree = new List<Start> { root ?? throw new InvalidDataException("The native process trace is missing the replay root.") };
        var sequences = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tree.Count; i++)
        {
            var node = tree[i];
            if (!sequences.Add(node.Sequence)) throw new InvalidDataException("Duplicate process sequence in the replay tree.");
            if (!nodesByParent.TryGetValue(node.Sequence, out var children)) continue;
            foreach (var child in children)
            {
                if (child.ParentPid != node.Pid) throw new InvalidDataException("Parent PID and sequence disagree in the replay tree.");
                tree.Add(child);
            }
        }
        if (tree.Count != job.ProcessCount)
            throw new InvalidDataException($"ETW retained {tree.Count} replay starts. The contained job counted {job.ProcessCount} processes.");
        return tree;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        internal long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceHeader
    {
        internal uint BufferSize, ProviderId;
        internal ulong HistoricalContext;
        internal long TimeStamp;
        internal Guid Guid;
        internal uint ClientContext, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceProperties
    {
        internal TraceHeader Header;
        internal uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
        internal int AgeLimit;
        internal uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        internal IntPtr LoggerThreadId;
        internal uint LogFileNameOffset, LoggerNameOffset;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int type, out Accounting accounting,
        uint length, IntPtr returned);

    [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ControlTrace(ulong handle, string session, IntPtr properties, uint controlCode);
}
