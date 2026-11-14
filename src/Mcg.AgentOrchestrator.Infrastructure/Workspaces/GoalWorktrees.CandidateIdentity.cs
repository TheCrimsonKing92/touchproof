using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    private const int CandidateGitTimeoutMilliseconds = 10_000;
    private static readonly HashSet<string> CandidateRootInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp", "Directory.Packages.props",
        "global.json", "NuGet.config"
    };

    public static bool TryComputeCandidateIdentity(
        string worktreePath,
        string integrationBranch,
        out CandidateIdentity? identity,
        out string failureReason,
        Func<string, IReadOnlyList<string>, string>? manifestIdentity = null,
        string? projectHomeDirectory = null)
    {
        identity = null;
        failureReason = string.Empty;
        if (!Directory.Exists(worktreePath))
            return Fail("missing-worktree", out failureReason);
        try
        {
            if (GitCli.IsWorktreeDirty(worktreePath))
                return Fail("dirty-worktree", out failureReason);
            if (!TryResolveCandidateMergeBase(worktreePath, integrationBranch, out var baseSha))
                return Fail("missing-merge-base", out failureReason);
            var changed = GitCli.Run(worktreePath, CandidateGitTimeoutMilliseconds,
                "diff", "--name-only", "--no-ext-diff", baseSha, "HEAD");
            if (!IsUsable(changed))
                return Fail("changed-files-unavailable", out failureReason);
            var changedFiles = changed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (changedFiles.Length == 0)
                return Fail("empty-change", out failureReason);
            if (!TryComputeStablePatchId(worktreePath, baseSha, "HEAD", out var patchId, out failureReason))
                return false;
            if (!TryHashBaseClosure(worktreePath, baseSha, changedFiles, out var closureHash, out failureReason))
                return false;
            var manifest = (manifestIdentity ?? ((path, files) =>
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(path, files, projectHomeDirectory)))(
                worktreePath, changedFiles);
            if (string.IsNullOrWhiteSpace(manifest))
                return Fail("manifest-unavailable", out failureReason);
            identity = new CandidateIdentity(patchId, closureHash, manifest);
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"candidate-computation-failed:{ex.GetType().Name}:{ex.Message}", out failureReason);
        }
    }

    internal static bool TryResolveCandidateMergeBase(string worktreePath, string integrationBranch, out string mergeBase)
    {
        var result = GitCli.Run(worktreePath, CandidateGitTimeoutMilliseconds,
            "merge-base", $"refs/heads/{integrationBranch}", "HEAD");
        mergeBase = result.Output.Trim();
        return IsUsable(result) && !string.IsNullOrWhiteSpace(mergeBase);
    }

    internal static bool TryComputeStablePatchId(string directory, string baseSha, string revision,
        out string patchId, out string failureReason, params string[] paths)
    {
        patchId = failureReason = string.Empty;
        var diff = GitCli.Run(directory, CandidateGitTimeoutMilliseconds,
            ["diff", "--binary", "--no-color", "--no-ext-diff", baseSha, revision, .. paths]);
        if (!IsUsable(diff) || string.IsNullOrEmpty(diff.Output))
            return Fail("diff-unavailable", out failureReason);
        var patch = GitCli.RunWithStandardInput(directory, CandidateGitTimeoutMilliseconds,
            diff.Output, "patch-id", "--stable");
        if (!IsUsable(patch)) return Fail("patch-id-unavailable", out failureReason);
        var value = patch.Output.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (value is null || value.Length != 40 || !value.All(Uri.IsHexDigit))
            return Fail("patch-id-malformed", out failureReason);
        patchId = value;
        return true;
    }

    private static bool TryHashBaseClosure(
        string worktreePath, string baseSha, IReadOnlyList<string> changedFiles,
        out string hash, out string failureReason) =>
        TryHashBaseClosure(worktreePath, baseSha, changedFiles, GitBaseClosureHashMemo.Shared,
            out hash, out failureReason);

    internal static bool TryHashBaseClosure(
        string worktreePath, string baseSha, IReadOnlyList<string> changedFiles, GitBaseClosureHashMemo memo,
        out string hash, out string failureReason)
    {
        hash = string.Empty;
        failureReason = string.Empty;
        var eligible = GitBaseClosureHashMemo.TryCreateKey(baseSha, changedFiles, out var key);
        if (eligible && memo.TryGet(key, out hash)) return true;
        var tree = GitCli.Run(worktreePath, CandidateGitTimeoutMilliseconds, "ls-tree", "-r", "--full-tree", baseSha);
        if (!IsUsable(tree))
            return Fail("base-tree-unavailable", out failureReason);
        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in tree.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var metadata = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length == 3 && metadata[1] == "blob")
                blobs[line[(tab + 1)..].TrimEnd('\r')] = metadata[2];
        }
        var projects = blobs.Keys.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();
        var individualFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var changed in changedFiles)
        {
            var owner = projects.Where(project => changed.Equals(project, StringComparison.OrdinalIgnoreCase) ||
                    changed.StartsWith(project[..(project.LastIndexOf('/') + 1)], StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(project => project.Length).FirstOrDefault();
            if (owner is null) individualFiles.Add(changed);
            else pending.Enqueue(owner);
        }
        using (var reader = new BaseClosureBatchReader(worktreePath))
        {
            while (pending.TryDequeue(out var project))
            {
                if (!visited.Add(project)) continue;
                if (!blobs.TryGetValue(project, out var blobId) || !reader.TryRead(blobId, out var source))
                    return Fail("project-reference-unavailable", out failureReason);
                var document = XDocument.Parse(source);
                foreach (var reference in document.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
                {
                    var include = reference.Attribute("Include")?.Value;
                    if (string.IsNullOrWhiteSpace(include)) continue;
                    if (include.Contains("$(", StringComparison.Ordinal) || include.Contains('*'))
                        return Fail("project-reference-unresolved", out failureReason);
                    var absolute = Path.GetFullPath(Path.Combine(worktreePath,
                        project[..project.LastIndexOf('/')].Replace('/', Path.DirectorySeparatorChar),
                        include.Replace('\\', Path.DirectorySeparatorChar)));
                    var relative = Path.GetRelativePath(worktreePath, absolute).Replace('\\', '/');
                    if (!projects.Contains(relative))
                        return Fail("project-reference-missing", out failureReason);
                    pending.Enqueue(relative);
                }
            }
            if (!reader.TryComplete()) return Fail("project-reference-unavailable", out failureReason);
        }
        var lines = blobs.Where(pair => CandidateRootInputs.Contains(pair.Key) ||
                pair.Key.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                individualFiles.Contains(pair.Key) ||
                visited.Any(project => pair.Key.StartsWith(project[..(project.LastIndexOf('/') + 1)], StringComparison.OrdinalIgnoreCase)))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}\0{pair.Value}\n");
        hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines)))).ToLowerInvariant();
        if (eligible) memo.Store(key, hash);
        return true;
    }

    private sealed class BaseClosureBatchReader(string directory) : IDisposable
    {
        private readonly BlockingCollection<Request> requests = new();
        private HandleListLaunchedProcess? process;
        private GitCli.ProcessCompletion? completion;
        private Thread? protocol, errors;
        private bool stopped, succeeded;

        private sealed record Request(string BlobId)
        {
            internal TaskCompletionSource<string?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal bool TryRead(string blobId, out string source)
        {
            source = string.Empty;
            try
            {
                if (stopped) return false;
                if (process is null) Start();
                if (process!.HasExited) return false;
                var request = new Request(blobId);
                requests.Add(request);
                if (!request.Answer.Task.Wait(CandidateGitTimeoutMilliseconds))
                {
                    Kill();
                    return false;
                }
                var answer = request.Answer.Task.Result;
                if (answer is null || process.HasExited) return false;
                source = answer;
                return true;
            }
            catch (Exception ex) when (IsProcessFailure(ex)) { return false; }
        }

        private void Start()
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            string[] args = ["cat-file", "--batch"];
            foreach (var arg in args) info.ArgumentList.Add(arg);
            var suppressed = GitCli.ApplyLaunchSettings(info, readCommand: true);
            completion = GitCli.ProcessCompletion.Begin(Volatile.Read(ref GitCli.CompletionObserver),
                "cat-file", TimeProvider.System);
            using (LaunchLockTelemetry.Attribution.IsActive ? LaunchLockSiteCapture.BeginGit(args) : null)
                process = suppressed
                    ? HandleListProcessLauncher.StartSuppressed(info, entry: LaunchLockEntryPoint.GitCli)
                    : HandleListProcessLauncher.Start(info, LaunchLockEntryPoint.GitCli);
            if (process is null) throw new IOException("Unable to start base closure reader.");
            completion?.Started();
            if (GitCli.LaunchObserver is { } observer) GitCli.NotifyLaunch(observer, args);
            errors = new Thread(DrainErrors) { IsBackground = true, Name = "base-closure-stderr" };
            protocol = new Thread(Run) { IsBackground = true, Name = "base-closure-protocol" };
            errors.Start();
            protocol.Start();
        }

        private void Run()
        {
            try
            {
                foreach (var request in requests.GetConsumingEnumerable())
                {
                    string? answer = null;
                    try
                    {
                        process!.StandardInput.Write(request.BlobId + "\n");
                        process.StandardInput.Flush();
                        answer = ReadBlob(process, request.BlobId);
                    }
                    finally { request.Answer.TrySetResult(answer); }
                    if (answer is null) break;
                }
            }
            catch (Exception ex) when (IsProcessFailure(ex)) { /* The caller observes a missing answer. */ }
            finally
            {
                try { process!.StandardInput.Close(); }
                catch (Exception ex) when (IsProcessFailure(ex)) { }
                while (requests.TryTake(out var pending)) pending.Answer.TrySetResult(null);
            }
        }

        private static string? ReadBlob(HandleListLaunchedProcess child, string blobId)
        {
            var stream = child.StandardOutput.BaseStream;
            var header = new StringBuilder();
            int value;
            while ((value = stream.ReadByte()) != '\n')
            {
                if (value < 0 || header.Length >= 128 || value > 127) return null;
                header.Append((char)value);
            }
            var fields = header.ToString().Split(' ');
            if (fields.Length != 3 || fields[0] != blobId || fields[1] != "blob" ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size)) return null;
            var bytes = new byte[size];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != '\n') return null;
            // Each former `show` had its own BOM-aware stream; framing must precede decoding.
            using var content = new MemoryStream(bytes, writable: false);
            using var decoder = new StreamReader(content, child.StandardOutput.CurrentEncoding,
                detectEncodingFromByteOrderMarks: true);
            return decoder.ReadToEnd();
        }

        private void DrainErrors()
        {
            try
            {
                var buffer = new char[4096];
                while (process!.StandardError.Read(buffer, 0, buffer.Length) > 0) { }
            }
            catch (Exception ex) when (IsProcessFailure(ex)) { }
        }

        internal bool TryComplete()
        {
            if (stopped) return succeeded;
            stopped = true;
            requests.CompleteAdding();
            if (process is null) return succeeded = true;
            try
            {
                var protocolDone = protocol?.Join(CandidateGitTimeoutMilliseconds) == true;
                var exited = protocolDone && process.WaitForExit(CandidateGitTimeoutMilliseconds);
                if (!exited) Kill();
                var errorsDone = errors?.Join(PipeDrain.DefaultTimeoutMilliseconds) == true;
                succeeded = exited && errorsDone && process.ExitCode == 0;
                if (process.HasExited) completion?.Complete(process);
            }
            catch (Exception ex) when (IsProcessFailure(ex)) { Kill(); }
            return succeeded;
        }

        private void Kill()
        {
            try { if (process is not null && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (IsProcessFailure(ex)) { }
        }

        public void Dispose()
        {
            TryComplete();
            // A killed reader must release its protocol thread before its pipes/queue are disposed.
            Kill();
            protocol?.Join(PipeDrain.DefaultTimeoutMilliseconds);
            errors?.Join(PipeDrain.DefaultTimeoutMilliseconds);
            if (process is not null && process.WaitForExit(PipeDrain.DefaultTimeoutMilliseconds))
                completion?.Complete(process);
            process?.Dispose();
            if (protocol?.IsAlive != true) requests.Dispose();
        }

        private static bool IsProcessFailure(Exception ex) => ex is IOException or InvalidOperationException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    }

    private static bool IsUsable(GitCli.GitResult result) =>
        result.Succeeded && !result.DrainTimedOut && result.ProcessStarted;

    private static bool Fail(string reason, out string failureReason)
    {
        failureReason = reason;
        return false;
    }
}
