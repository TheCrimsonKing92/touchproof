using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class WorkerProcessRunner
{
    /// <summary>Request-scoped aggregate byte ceiling. Drains both pipes asynchronously and owns
    /// the launched process tree. Existing buffered/streaming callers keep their capture contract.</summary>
    public static Task<WorkerProcessRunResult> RunBoundedDirectAsync(
        WorkerProcessDirectRunRequest request, int maximumCaptureBytes, CancellationToken cancellationToken) =>
        RunBoundedDirectAsync(request, maximumCaptureBytes, cancellationToken, TimeProvider.System, null, null);

    internal static async Task<WorkerProcessRunResult> RunBoundedDirectAsync(
        WorkerProcessDirectRunRequest request, int maximumCaptureBytes, CancellationToken cancellationToken,
        TimeProvider time, Action? started, Action<bool>? cleanedUp)
    {
        if (maximumCaptureBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCaptureBytes));
        if (request.Timeout is not { } timeout || timeout <= TimeSpan.Zero)
            throw new ArgumentException("Bounded execution requires a positive timeout.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(timeout, time);
        using var limit = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, limit.Token);
        using var process = HandleListProcessLauncher.Start(BuildDirectStartInfo(request.FileName,
            request.Arguments, request.WorkingDirectory)) ?? throw new InvalidOperationException("Probe did not start.");
        OwnedProcessGroup? group = null;
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        using var drain = new CancellationTokenSource();
        long captured = 0;
        var timedOut = false;
        async Task CaptureAsync(Stream input, Stream output)
        {
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer, drain.Token).ConfigureAwait(false);
                    if (read == 0) return;
                    if (Interlocked.Add(ref captured, read) > maximumCaptureBytes)
                    {
                        limit.Cancel();
                        return;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), drain.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (drain.IsCancellationRequested) { }
            catch (IOException) when (drain.IsCancellationRequested) { }
        }
        Task? output = null;
        try
        {
            try { group = OwnedProcessGroup.Attach(process); }
            catch
            {
                if (request.RequireOwnedProcessGroup) throw;
            }
            process.StandardInput.Close();
            output = Task.WhenAll(CaptureAsync(process.StandardOutput.BaseStream, stdout),
                CaptureAsync(process.StandardError.BaseStream, stderr));
            started?.Invoke();
            try
            {
                await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
                // A descendant holding a pipe cannot extend the observation deadline.
                await output.WaitAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = deadline.IsCancellationRequested && !limit.IsCancellationRequested;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (timedOut || limit.IsCancellationRequested)
                return new(-1, "", "", TimedOut: timedOut, CaptureLimited: limit.IsCancellationRequested);
            return new(process.ExitCode, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
        }
        finally
        {
            TryKillProcessTree(process, group);
            drain.Cancel();
            // Closing our pipe handles also unblocks a reader if a descendant escaped ownership.
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            if (output is not null)
            {
                try { await output.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException) { }
            }
            WorkerProcessJobs.ReadAccountingAndDispose(group, kill: true, captureAccounting: false, out _);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            cleanedUp?.Invoke(process.HasExited);
        }
    }
}
