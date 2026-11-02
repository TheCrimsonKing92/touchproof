using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Caches processes, never revisions. All production callers share this bounded owner.
internal sealed class GitRevisionReader : IDisposable
{
    internal static GitRevisionReader Shared { get; } = new();
    private readonly object gate = new();
    private readonly Dictionary<string, ReaderProcess> readers = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly int capacity;
    private readonly TimeSpan idleTime;
    private readonly TimeProvider clock;
    private readonly ITimer timer;
    private long activity;
    private bool disposed;

    static GitRevisionReader() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Shared.Dispose();

    internal GitRevisionReader(int capacity = 16, TimeSpan? idleTime = null, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.capacity = capacity;
        this.idleTime = idleTime ?? TimeSpan.FromSeconds(60);
        if (this.idleTime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTime));
        this.clock = clock ?? TimeProvider.System;
        timer = this.clock.CreateTimer(_ => ExpireIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal bool TryResolve(string workingDirectory, string revision, out string objectId) =>
        TryResolve(workingDirectory, revision, GitCli.DefaultTimeoutMilliseconds, out objectId);

    internal bool TryResolve(string workingDirectory, string revision, int timeoutMilliseconds, out string objectId)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        ReaderProcess? reader = null;
        string? key = null;
        try
        {
            try
            {
                if (CanUseBatch(revision))
                {
                    key = NormalizeDirectory(workingDirectory);
                    reader = Acquire(key);
                    if (reader is not null)
                    {
                        var line = reader.Query(revision, Remaining(deadline));
                        if (TryParseObject(line, out objectId))
                        {
                            if (!IsAbbreviatedRevision(revision, objectId.Length)) return true;
                        }
                        // A normal miss stays usable, but ALWAYS goes through the one-shot below.
                        else if (line != revision + " missing") Discard(key, reader);
                    }
                }
            }
            catch (Exception ex) when (IsReaderFailure(ex))
            {
                if (reader is not null && key is not null) Discard(key, reader);
            }

            var result = GitCli.Run(workingDirectory, Remaining(deadline), "rev-parse", "--verify", revision);
            objectId = result.Succeeded ? result.Output.Trim() : string.Empty;
            return objectId.Length > 0;
        }
        finally
        {
            if (reader is not null)
            {
                lock (gate)
                {
                    reader.Active--;
                    reader.LastCompleted = clock.GetTimestamp();
                    reader.Activity = ++activity;
                    ScheduleExpiry();
                }
            }
        }
    }

    private static int Remaining(long deadline) => (int)Math.Clamp(deadline - Environment.TickCount64, 1, int.MaxValue);
    private static string NormalizeDirectory(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    private static bool CanUseBatch(string revision) =>
        !string.IsNullOrWhiteSpace(revision) && revision[0] != '-' &&
        !revision.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == ':') &&
        !revision.Contains("@{", StringComparison.Ordinal); // Config/reflog-dependent expressions use fresh Git state.

    private static bool IsAbbreviatedRevision(string revision, int objectIdLength)
    {
        // Git caches abbreviated-object enumeration in a long-lived process. Confirm abbreviated
        // results without assuming a hash length; full IDs remain on the persistent path.
        var suffix = revision.IndexOfAny(['^', '~', '@']);
        var basis = suffix < 0 ? revision : revision[..suffix];
        return basis.Length >= 4 && basis.Length < objectIdLength && basis.All(Uri.IsHexDigit);
    }

    private ReaderProcess? Acquire(string key)
    {
        lock (gate)
        {
            if (disposed) return null;
            if (!readers.TryGetValue(key, out var reader))
            {
                var gitDirectory = FindGitDirectory(key);
                if (gitDirectory is null) return null;
                if (readers.Count == capacity)
                {
                    var oldest = readers.MinBy(pair => pair.Value.Activity);
                    readers.Remove(oldest.Key);
                    oldest.Value.Terminate(); // May interrupt an active query; it will fall back.
                }
                reader = ReaderProcess.Start(gitDirectory);
                readers.Add(key, reader);
            }
            reader.Active++;
            reader.Activity = ++activity;
            return reader;
        }
    }

    private static string? FindGitDirectory(string workingDirectory)
    {
        // Repository overrides must retain GitCli's inherited-environment semantics. Likewise,
        // subdirectories and bare repositories use the authoritative one-shot discovery path.
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
            "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
            if (Environment.GetEnvironmentVariable(name) is not null) return null;
        var dotGit = Path.Combine(workingDirectory, ".git");
        if (Directory.Exists(dotGit)) return dotGit;
        if (!File.Exists(dotGit)) return null;
        var text = File.ReadAllText(dotGit).Trim();
        return text.StartsWith("gitdir: ", StringComparison.Ordinal)
            ? Path.GetFullPath(text[8..], workingDirectory) : null;
    }

    private static bool TryParseObject(string? line, out string objectId)
    {
        objectId = string.Empty;
        if (line is null) return false;
        var fields = line.Split(' ');
        if (fields.Length != 3 || fields[0].Length == 0 || !fields[0].All(Uri.IsHexDigit) ||
            fields[1] is not ("commit" or "tree" or "blob" or "tag") ||
            !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 0)
            return false;
        objectId = fields[0];
        return true;
    }

    private void Discard(string key, ReaderProcess reader)
    {
        lock (gate)
        {
            if (readers.TryGetValue(key, out var current) && ReferenceEquals(current, reader)) readers.Remove(key);
            reader.Terminate();
        }
    }

    private void ExpireIdle()
    {
        lock (gate)
        {
            if (disposed) return;
            var now = clock.GetTimestamp();
            foreach (var pair in readers.Where(pair => pair.Value.Active == 0 &&
                clock.GetElapsedTime(pair.Value.LastCompleted, now) >= idleTime).ToArray())
            {
                readers.Remove(pair.Key);
                pair.Value.Terminate();
            }
            ScheduleExpiry();
        }
    }

    private void ScheduleExpiry()
    {
        if (disposed) return;
        var now = clock.GetTimestamp();
        var deadlines = readers.Values.Where(reader => reader.Active == 0)
            .Select(reader => idleTime - clock.GetElapsedTime(reader.LastCompleted, now))
            .ToArray();
        var due = deadlines.Length == 0 ? Timeout.InfiniteTimeSpan : deadlines.Min();
        timer.Change(deadlines.Length > 0 && due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    // Tests inspect/terminate only handles owned by their isolated reader, never the OS PID namespace.
    internal ReaderProcess? GetReader(string workingDirectory)
    {
        lock (gate) return readers.GetValueOrDefault(NormalizeDirectory(workingDirectory));
    }

    internal int RetainedCount { get { lock (gate) return readers.Count; } }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            timer.Dispose();
            foreach (var reader in readers.Values) reader.Terminate();
            readers.Clear();
        }
    }

    private static bool IsReaderFailure(Exception ex) => ex is IOException or InvalidOperationException or
        System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    internal sealed class ReaderProcess
    {
        private readonly Process process;
        private readonly string packedRefsPath;
        private readonly string packedRefsVersion;
        private readonly object terminationGate = new();
        private readonly BlockingCollection<Request> requests = new();
        private int stopped;
        private volatile bool exited;
        internal int Active;
        internal long Activity;
        internal long LastCompleted;
        internal bool HasExited => exited;

        private ReaderProcess(Process process, string packedRefsPath, string packedRefsVersion)
        {
            this.process = process;
            this.packedRefsPath = packedRefsPath;
            this.packedRefsVersion = packedRefsVersion;
        }

        internal static ReaderProcess Start(string gitDirectory)
        {
            var commonDirectoryFile = Path.Combine(gitDirectory, "commondir");
            var commonDirectory = File.Exists(commonDirectoryFile)
                ? Path.GetFullPath(File.ReadAllText(commonDirectoryFile).Trim(), gitDirectory) : gitDirectory;
            var packedRefsPath = Path.Combine(commonDirectory, "packed-refs");
            var packedRefsVersion = ReadPackedRefsVersion(packedRefsPath);
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            // GitCli applies the shared launch contract to the persistent reader too.
            foreach (var arg in new[] { "--git-dir=" + gitDirectory,
                "cat-file", "--batch-check" }) info.ArgumentList.Add(arg);
            GitCli.ApplyLaunchSettings(info, readCommand: true);
            var process = ProcessTreeGuiSuppression.Start(info);
            var reader = new ReaderProcess(process, packedRefsPath, packedRefsVersion);
            try
            {
                // Notify on the querying thread, preserving census/test identity and source frames.
                try { GitCli.LaunchObserver?.Invoke("cat-file"); }
                catch (Exception) { /* Telemetry must not change the lookup result. */ }
                new Thread(reader.DrainErrors) { IsBackground = true, Name = "git-revision-stderr" }.Start();
                new Thread(reader.Run) { IsBackground = true, Name = "git-revision-protocol" }.Start();
                return reader;
            }
            catch
            {
                reader.Terminate();
                process.Dispose();
                throw;
            }
        }

        internal string? Query(string revision, int timeoutMilliseconds)
        {
            if (Volatile.Read(ref stopped) != 0 || process.HasExited) return null;
            // cat-file retains packed refs across requests. Invalidate before trusting any answer
            // after their contents change, including same-size/timestamp replacements. Returning
            // null discards this process and confirms the query through fresh one-shot Git state.
            if (ReadPackedRefsVersion(packedRefsPath) != packedRefsVersion) return null;
            var request = new Request(revision);
            if (!requests.TryAdd(request)) return null;
            return request.Answer.Task.Wait(timeoutMilliseconds) && !process.HasExited ? request.Answer.Task.Result : null;
        }

        private static string ReadPackedRefsVersion(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return string.Empty; }
        }

        private void Run()
        {
            try
            {
                // One dedicated protocol thread serializes both writes and reads. Even a blocked
                // stdin write is covered by the caller's wait, without relying on the thread pool.
                foreach (var request in requests.GetConsumingEnumerable())
                {
                    string? line = null;
                    try
                    {
                        process.StandardInput.Write(request.Revision + "\n");
                        process.StandardInput.Flush();
                        line = process.StandardOutput.ReadLine();
                    }
                    finally { request.Answer.TrySetResult(line); }
                    if (line is null) break;
                }
            }
            catch (Exception ex) when (IsReaderFailure(ex)) { /* Query falls back on a null answer. */ }
            finally
            {
                Terminate();
                while (requests.TryTake(out var pending)) pending.Answer.TrySetResult(null);
                process.Dispose();
                requests.Dispose();
            }
        }

        private void DrainErrors()
        {
            try
            {
                var buffer = new char[4096];
                while (process.StandardError.Read(buffer, 0, buffer.Length) > 0) { }
            }
            catch (Exception ex) when (IsReaderFailure(ex)) { /* Closing a reader also closes its pipes. */ }
        }

        internal void Terminate()
        {
            lock (terminationGate)
            {
                if (Interlocked.Exchange(ref stopped, 1) != 0) return;
                requests.CompleteAdding();
                KillProcess();
            }
        }

        // Also lets real-repository tests kill the owned OS handle without first closing the cache entry.
        internal void KillProcess()
        {
            lock (terminationGate)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    exited = process.WaitForExit(PipeDrain.DefaultTimeoutMilliseconds);
                }
                catch (Exception ex) when (IsReaderFailure(ex)) { /* Already exited/disposed. */ }
            }
        }

        private sealed record Request(string Revision)
        {
            internal TaskCompletionSource<string?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
