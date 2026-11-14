using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

// One call owns one process. Immutable blob ids and byte framing keep revisions and encodings separate.
internal static class GitBlobBatchReader
{
    internal sealed record Result(GitCli.GitResult Git, IReadOnlyList<string> Contents);

    internal static Result Read(string directory, IReadOnlyList<string> blobIds)
    {
        HandleListLaunchedProcess? process = null;
        GitCli.ProcessCompletion? completion = null;
        Thread? protocol = null;
        PipeDrain? errors = null;
        var deadline = Environment.TickCount64 + GitCli.DefaultTimeoutMilliseconds;
        try
        {
            if (blobIds.Any(id => id.Length is not (40 or 64) || !id.All(Uri.IsHexDigit)))
                throw new IOException("Invalid blob identity.");
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
            if (process is null) throw new IOException("Unable to start git blob reader.");
            completion?.Started();
            if (GitCli.LaunchObserver is { } observer) GitCli.NotifyLaunch(observer, args);
            errors = PipeDrain.Start(process.StandardError, "git-blob-stderr");
            var child = process;
            var contents = new List<string>(blobIds.Count);
            Exception? failure = null;
            protocol = new Thread(() =>
            {
                try
                {
                    foreach (var id in blobIds)
                    {
                        child.StandardInput.Write(id + "\n");
                        child.StandardInput.Flush();
                        contents.Add(ReadBlob(child, id));
                    }
                    child.StandardInput.Close();
                    if (child.StandardOutput.BaseStream.ReadByte() != -1)
                        throw new IOException("Unexpected trailing git batch output.");
                }
                catch (Exception ex) when (IsProcessFailure(ex)) { failure = ex; }
                finally
                {
                    try { child.StandardInput.Close(); }
                    catch (Exception ex) when (IsProcessFailure(ex)) { failure ??= ex; }
                }
            }) { IsBackground = true, Name = "git-blob-protocol" };
            protocol.Start();
            if (!protocol.Join(Remaining()) || !process.WaitForExit(Remaining()))
                return Failed("git cat-file --batch timed out", -1);
            var drained = errors.Join(deadline);
            if (!drained)
                return new(new(process.ExitCode, "", "git cat-file --batch error drain timed out", DrainTimedOut: true), []);
            if (process.ExitCode != 0) return new(new(process.ExitCode, "", errors.Text), []);
            if (failure is not null) return Failed(failure.Message);
            if (contents.Count != blobIds.Count) return Failed("Incomplete git batch output.");
            return new(new(0, "", errors.Text), contents);
        }
        catch (Exception ex) when (IsProcessFailure(ex)) { return Failed(ex.Message); }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (IsProcessFailure(ex)) { }
                protocol?.Join(PipeDrain.DefaultTimeoutMilliseconds);
                errors?.Join(Environment.TickCount64 + PipeDrain.DefaultTimeoutMilliseconds);
                completion?.CompleteIfExited(process);
                process.Dispose();
            }
        }

        int Remaining() => (int)Math.Clamp(deadline - Environment.TickCount64, 0, int.MaxValue);
        Result Failed(string reason, int exitCode = 1) =>
            new(new(exitCode, "", reason, ProcessStarted: process is not null), []);
    }

    private static string ReadBlob(HandleListLaunchedProcess child, string id)
    {
        var stream = child.StandardOutput.BaseStream;
        var header = new StringBuilder();
        int value;
        while ((value = stream.ReadByte()) != '\n')
        {
            if (value < 0 || value > 127 || header.Length >= 128)
                throw new IOException("Invalid git batch header.");
            header.Append((char)value);
        }
        var fields = header.ToString().Split(' ');
        if (fields.Length != 3 || fields[0] != id || fields[1] != "blob" ||
            !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            throw new IOException("Invalid git batch blob identity, type or size.");
        var bytes = new byte[size];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != '\n') throw new IOException("Incomplete git batch blob.");
        using var content = new MemoryStream(bytes, writable: false);
        using var decoder = new StreamReader(content, child.StandardOutput.CurrentEncoding,
            detectEncodingFromByteOrderMarks: true);
        return decoder.ReadToEnd();
    }

    private static bool IsProcessFailure(Exception ex) => ex is IOException or InvalidOperationException or
        System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
