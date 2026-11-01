using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliTimingLogWriter
{
    internal static void Append<T>(string directory, T record)
    {
        // Callers contain all errors, including mutex creation, serialization and rotation.
        var path = Path.GetFullPath(Path.Combine(directory, "cli-timing.jsonl"));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        using var mutex = new Mutex(false, "mcg-cli-timing-" + key);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(200)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
            var line = JsonSerializer.Serialize(record) + Environment.NewLine;
            Directory.CreateDirectory(directory);
            if (File.Exists(path) && new FileInfo(path).Length >= 1_048_576)
            {
                var stem = "cli-timing-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                var archive = Path.Combine(directory, stem + ".jsonl");
                for (var n = 1; File.Exists(archive); n++) archive = Path.Combine(directory, $"{stem}-{n}.jsonl");
                File.Move(path, archive);
                foreach (var old in Directory.EnumerateFiles(directory, "cli-timing-*.jsonl")
                    .OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(p => p, StringComparer.Ordinal).Skip(8))
                {
                    try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            File.AppendAllText(path, line);
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
}
