using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ReviewerCandidateDiffSection
{
    internal const int PerFileBytes = 4096;
    internal const int SectionBytes = 16384;
    internal const string Heading = "\n\n## Candidate diff excerpts\n";
    private const string Truncation = "\n[Excerpt truncated]\n";

    internal static string Render(string root, ReviewerChangedFileScope? scope, int? scopedFileCount)
    {
        if ((scope?.TotalChangedFileCount ?? scopedFileCount) is null or 0)
            return string.Empty;
        if (scope is null)
            return Heading + "Excerpts unavailable because the scope has no captured candidate revisions.\n";

        var inspection = $"Inspect the full candidate diff with `git diff {scope.IntegrationCommit}...{scope.CandidateCommit}`.\n";
        if (!RevisionsAvailable(root, scope))
            return Heading + "Excerpts unavailable because a captured commit or merge base is unavailable.\n" + inspection;

        var count = scope.TotalChangedFileCount;
        var reservedFooter = ByteCount(Omitted(count)) + ByteCount(inspection);
        var output = new StringBuilder(Heading);
        var remaining = SectionBytes - ByteCount(Heading) - reservedFooter;
        var shown = 0;
        var incomplete = false;
        foreach (var change in scope.Changes.OrderBy(change => change.Path, StringComparer.Ordinal))
        {
            var label = change.PreviousPath is null
                ? $"\n### {DisplayPath(change.Path)}\n"
                : $"\n### {DisplayPath(change.PreviousPath)} → {DisplayPath(change.Path)}\n";
            var allowance = Math.Min(PerFileBytes, remaining) - ByteCount(label);
            if (allowance < Math.Max(ByteCount(Truncation), ByteCount("Omitted: diff unavailable.\n")))
                break;

            var patch = change.CanHaveTextHunks
                ? ReadGitPatch(root, PatchArguments(scope, change), allowance)
                : new Patch(PatchKind.NoHunks);
            var content = patch.Kind switch
            {
                PatchKind.Binary => "Omitted: binary file.\n",
                PatchKind.NoHunks => "No textual hunks.\n",
                PatchKind.Failed => "Omitted: diff unavailable.\n",
                _ => patch.Text,
            };
            output.Append(label).Append(content);
            remaining -= ByteCount(label) + ByteCount(content);
            shown++;
            incomplete |= patch.Kind != PatchKind.Complete;
        }

        if (shown < count)
            output.Append(Omitted(count - shown));
        if (incomplete || shown < count)
            output.Append(inspection);
        return output.ToString();
    }

    private static string Omitted(int count) =>
        $"\nAll {count} remaining paths in the authoritative changed-file scope are omitted by the excerpt budget.\n";

    private static string DisplayPath(string path) =>
        path.Any(char.IsControl) ? JsonSerializer.Serialize(path) : path;

    private static bool RevisionsAvailable(string root, ReviewerChangedFileScope scope)
    {
        var revisions = new[] { scope.IntegrationCommit, scope.MergeBase, scope.CandidateCommit }.Distinct().ToArray();
        if (revisions.Any(revision => revision.Length is not (40 or 64) || !revision.All(Uri.IsHexDigit)))
            return false;
        var result = GitCli.RunWithStandardInput(root, GitCli.DefaultTimeoutMilliseconds,
            string.Join('\n', revisions) + "\n", "--no-lazy-fetch", "cat-file",
            "--batch-check=%(objectname) %(objecttype)");
        return result.Succeeded && !result.DrainTimedOut &&
            result.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .SequenceEqual(revisions.Select(revision => $"{revision} commit"), StringComparer.OrdinalIgnoreCase);
    }

    private static string[] PatchArguments(ReviewerChangedFileScope scope, ReviewerScopedChange change)
    {
        // Match the captured classification. In a file/directory transition a literal pathspec
        // can also match descendants, but these have the opposite added/deleted status.
        var args = new List<string>
        {
            "--no-lazy-fetch", "--literal-pathspecs", $"--attr-source={scope.CandidateCommit}",
            "diff", "--patch", "--no-ext-diff", "--no-textconv", "--no-color", "--no-relative",
            "--output-indicator-new=+", "--output-indicator-old=-", "--output-indicator-context= ",
            $"--diff-filter={change.Status}", scope.MergeBase, scope.CandidateCommit, "--", change.Path,
        };
        if (change.PreviousPath is not null)
            args.Add(change.PreviousPath);
        return args.ToArray();
    }

    internal static Patch ReadGitPatch(string root, string[] args, int allowance)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        try
        {
            using var process = GitCli.ApplyLaunchSettings(start, readCommand: true)
                ? HandleListProcessLauncher.StartSuppressed(start, entry: LaunchLockEntryPoint.GitCli)
                : HandleListProcessLauncher.Start(start, LaunchLockEntryPoint.GitCli);
            if (process is null)
                return new(PatchKind.Failed);
            if (GitCli.LaunchObserver is { } observer)
                GitCli.NotifyLaunch(observer, args);

            var stdout = process.StandardOutput.BaseStream;
            var stderr = process.StandardError.BaseStream;
            var patchRead = StartDrain(() => ReadPatch(stdout, allowance));
            var errorDrain = StartDrain(() => { stderr.CopyTo(Stream.Null, 4096); return true; });
            var deadline = Environment.TickCount64 + GitCli.DefaultTimeoutMilliseconds;
            try
            {
                if (!patchRead.Wait(Remaining(deadline)))
                    return new(PatchKind.Failed);
                var patch = patchRead.Result;
                if (patch.Kind == PatchKind.Truncated && !process.HasExited)
                    return patch;
                return process.WaitForExit(Remaining(deadline)) &&
                    errorDrain.Wait(Remaining(deadline)) && process.ExitCode == 0
                    ? patch
                    : new(PatchKind.Failed);
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                    process.WaitForExit(PipeDrain.DefaultTimeoutMilliseconds);
                }
                stdout.Dispose();
                stderr.Dispose();
                try { Task.WaitAll([patchRead, errorDrain], PipeDrain.DefaultTimeoutMilliseconds); }
                catch (AggregateException) { /* Cleanup can interrupt a pending read. */ }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or AggregateException)
        {
            return new(PatchKind.Failed);
        }
    }

    private static Task<T> StartDrain<T>(Func<T> read) =>
        Task.Factory.StartNew(read, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static int Remaining(long deadline) => (int)Math.Max(0, deadline - Environment.TickCount64);

    internal static Patch ReadPatch(Stream input, int allowance)
    {
        var buffer = new byte[4096];
        var line = new byte[allowance + 1];
        var length = 0;
        var output = new StringBuilder();
        var remaining = allowance - ByteCount(Truncation);
        var patchKind = PatchKind.NoHunks;
        var fileHeaders = 0;
        var inHunk = false;
        var skipHunks = false;
        while (true)
        {
            var read = input.Read(buffer);
            if (read == 0)
            {
                if (length != 0 && !ConsumeLine())
                    return new(PatchKind.Truncated, output.Append(Truncation).ToString());
                return new(patchKind, output.ToString());
            }

            foreach (var value in buffer.AsSpan(0, read))
            {
                if (length < line.Length)
                    line[length++] = value;
                if (value == (byte)'\n')
                {
                    if (!ConsumeLine())
                        return new(PatchKind.Truncated, output.Append(Truncation).ToString());
                    length = 0;
                }
                else if (inHunk && (line[0] is (byte)'+' or (byte)'-' or (byte)' ') && length > remaining)
                {
                    // The body line alone has exhausted the budget. Its unseen tail is immaterial.
                    AppendPrefix(line.AsSpan(0, length));
                    return new(PatchKind.Truncated, output.Append(Truncation).ToString());
                }
            }
        }

        bool ConsumeLine()
        {
            var bytes = line.AsSpan(0, length);
            if (bytes.StartsWith("diff --git "u8))
            {
                inHunk = skipHunks = false;
                // Type changes can emit distinct deletion and addition patches for one path.
                if (++fileHeaders > 2)
                    throw new InvalidDataException("Unexpected number of patches for a scoped path.");
                return true;
            }
            if (bytes.StartsWith("Binary files "u8))
            {
                patchKind = PatchKind.Binary;
                output.Clear();
                return true;
            }
            if (bytes.StartsWith("new file mode 160000"u8) || bytes.StartsWith("deleted file mode 160000"u8))
                skipHunks = true;
            if (patchKind == PatchKind.Binary || skipHunks)
                return true;

            var header = bytes.StartsWith("@@ "u8);
            if (!header && !inHunk)
                return true;
            var text = Encoding.UTF8.GetString(bytes);
            var size = ByteCount(text);
            if (size > remaining)
            {
                if (!header)
                    AppendPrefix(bytes);
                return false;
            }

            inHunk = true;
            patchKind = PatchKind.Complete;
            output.Append(text);
            remaining -= size;
            return true;
        }

        void AppendPrefix(ReadOnlySpan<byte> bytes)
        {
            foreach (var rune in Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, remaining + 1)]).EnumerateRunes())
            {
                if (rune.Utf8SequenceLength > remaining)
                    break;
                output.Append(rune.ToString());
                remaining -= rune.Utf8SequenceLength;
            }
        }
    }

    private static int ByteCount(string value) => Encoding.UTF8.GetByteCount(value);

    internal enum PatchKind { Complete, Truncated, Binary, NoHunks, Failed }

    internal readonly record struct Patch(PatchKind Kind, string Text = "");
}
