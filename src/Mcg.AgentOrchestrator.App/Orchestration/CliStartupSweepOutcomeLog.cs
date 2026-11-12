using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class CliStartupSweepOutcomeLog
{
    internal const string FileName = "orphan-sweep-outcomes.log";

    internal static void Append(string directory, OrphanWorktreeSweepOutcome outcome)
    {
        try
        {
            using var writer = new StringWriter();
            OrphanWorktreeSweepOutcome.Write(outcome, writer);
            var line = writer.ToString();
            if (line.Length == 0) return;
            var path = Path.GetFullPath(Path.Combine(directory, FileName));
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path)));
            using var mutex = new Mutex(false, "mcg-orphan-sweep-log-" + key);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(200)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return;
                Directory.CreateDirectory(directory);
                if (File.Exists(path) && new FileInfo(path).Length >= 1_048_576)
                {
                    File.Delete(path + ".8");
                    for (var n = 7; n >= 1; n--)
                        if (File.Exists(path + "." + n)) File.Move(path + "." + n, path + "." + (n + 1));
                    File.Move(path, path + ".1");
                }
                File.AppendAllText(path, line);
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        // Diagnostic logging must never fail a command or fall back to its console streams.
        catch { }
    }
}
