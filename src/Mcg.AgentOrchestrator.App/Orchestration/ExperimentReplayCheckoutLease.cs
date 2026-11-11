using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The parent holds a non-inherited, write-denying handle for the entire checkout lifetime.
// Children validate the receipt and live owner, then hold a read handle across destructive work.
internal sealed class ExperimentReplayCheckoutLease : IDisposable
{
    private sealed record Receipt(string CheckoutRoot, string Checkout, string CandidateCommit, string GitDirectory,
        int ProcessId, long ProcessStartTicks, string Executable);

    private readonly FileStream _handle;
    private readonly string _path;
    internal string CandidateCommit { get; }
    internal string CheckoutRoot { get; }

    private ExperimentReplayCheckoutLease(string path, FileStream handle, string candidate, string checkoutRoot)
    { _path = path; _handle = handle; CandidateCommit = candidate; CheckoutRoot = checkoutRoot; }

    internal static ExperimentReplayCheckoutLease Create(string checkout, string candidate, string checkoutRoot)
    {
        ValidateLocation(checkout, checkoutRoot);
        var gitDirectory = Git(checkout, "rev-parse", "--absolute-git-dir");
        ValidateGit(checkout, candidate, gitDirectory);
        using var owner = Process.GetCurrentProcess();
        var receipt = new Receipt(Path.GetFullPath(checkoutRoot), Path.GetFullPath(checkout), candidate, gitDirectory, owner.Id,
            owner.StartTime.ToUniversalTime().Ticks, owner.MainModule!.FileName!);
        var path = checkout + ".lease.json";
        var handle = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            JsonSerializer.Serialize(handle, receipt, ExperimentReplayStore.Json);
            handle.Flush(flushToDisk: true);
            return new(path, handle, candidate, checkoutRoot);
        }
        catch { handle.Dispose(); File.Delete(path); throw; }
    }

    internal static IDisposable Acquire(string checkout, string candidate, string? checkoutRoot)
    {
        ValidateLocation(checkout, checkoutRoot);
        var path = checkout + ".lease.json";
        var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            var receipt = JsonSerializer.Deserialize<Receipt>(handle, ExperimentReplayStore.Json)
                ?? throw new InvalidDataException("Replay checkout lease is empty.");
            if (!SamePath(receipt.CheckoutRoot, checkoutRoot!) || !SamePath(receipt.Checkout, checkout) || receipt.CandidateCommit != candidate)
                throw new InvalidDataException("Replay checkout lease does not own the requested checkout/candidate.");
            // A stale receipt after a crash is not authority. Its writer must still deny write access.
            var locked = false;
            try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
            catch (IOException) { locked = true; }
            if (!locked) throw new InvalidDataException("Replay checkout lease owner released its handle.");
            using var owner = Process.GetProcessById(receipt.ProcessId);
            if (owner.HasExited || owner.StartTime.ToUniversalTime().Ticks != receipt.ProcessStartTicks ||
                !SamePath(owner.MainModule!.FileName!, receipt.Executable))
                throw new InvalidDataException("Replay checkout lease owner identity expired.");
            ValidateGit(checkout, candidate, receipt.GitDirectory);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static void ValidateLocation(string checkout, string? root)
    {
        if (root is null || !SamePath(Path.GetDirectoryName(Path.GetFullPath(checkout))!, root) ||
            !Guid.TryParseExact(Path.GetFileName(checkout), "N", out _) ||
            !File.Exists(Path.Combine(checkout, ".git")) ||
            (File.GetAttributes(checkout) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Procedure requires a lifecycle-owned replay checkout.");
    }

    private static void ValidateGit(string checkout, string candidate, string gitDirectory)
    {
        var branch = GitCli.RunForRepository(checkout, "symbolic-ref", "--quiet", "HEAD");
        if (!branch.ProcessStarted || branch.DrainTimedOut || branch.ExitCode != 1 ||
            Git(checkout, "rev-parse", "HEAD") != candidate ||
            !SamePath(Git(checkout, "rev-parse", "--show-toplevel"), checkout) ||
            !SamePath(Git(checkout, "rev-parse", "--absolute-git-dir"), gitDirectory))
            throw new InvalidDataException("Replay checkout lost its pinned detached Git identity.");
    }

    private static string Git(string checkout, params string[] args)
    {
        var result = GitCli.RunForRepository(checkout, args);
        if (!result.ProcessStarted || !result.Succeeded || result.DrainTimedOut)
            throw new InvalidDataException($"Replay checkout identity unavailable: {result.Error}");
        return result.Output.Trim();
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    public void Dispose() { _handle.Dispose(); File.Delete(_path); }
}
