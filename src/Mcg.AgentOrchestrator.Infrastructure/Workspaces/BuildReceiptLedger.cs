using Mcg.AgentOrchestrator.Core;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class BuildReceiptLedger(Action<string, string>? appender = null, TextWriter? error = null)
{
    internal const string FileName = "build-receipts.jsonl";
    internal static BuildReceiptLedger Default { get; } = new();
    private int _warned;

    internal static string ResolveStorePath(string worktreePath) =>
        Path.Combine(HostLedgerStateDirectory.Resolve(worktreePath), FileName);

    internal void Record(string worktreePath, BuildReceipt receipt)
    {
        try
        {
            var path = ResolveStorePath(worktreePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            (appender ?? AppendOnce)(path, receipt.ToJson());
        }
        catch (Exception failure)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
                try { (error ?? Console.Error).WriteLine("warning: build receipt ledger write skipped: " + failure.GetType().Name); }
                catch (Exception) { /* Diagnostics cannot change build results. */ }
        }
    }

    private static void AppendOnce(string path, string line)
    {
        // No retry delay on the build path. Exclusive writing also prevents interleaved JSON.
        var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.Read | FileShare.Delete, bufferSize: 1);
        stream.Write(bytes);
    }
}
