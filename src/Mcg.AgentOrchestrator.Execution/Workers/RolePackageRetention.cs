using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns complete task snapshots outside the worktree; the manifest orders refreshes, not task creation.
internal static class RolePackageRetention
{
    internal const string DirectoryName = "role-replay-packages";
    internal const string ManifestFileName = "retention-manifest.json";
    internal const string FailureNoteSuffix = ".retention-failure.txt";
    internal const long PerGoalByteBudget = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static string GoalDirectory(string storeRoot, GoalId goalId) =>
        Path.Combine(storeRoot, DirectoryName, goalId.Value);

    internal static string TaskDirectory(string storeRoot, GoalId goalId, TaskId taskId) =>
        Path.Combine(GoalDirectory(storeRoot, goalId), taskId.Value);

    internal static void Retain(string? storeRoot, GoalId goalId, TaskId taskId,
        string packageDirectory, IClock clock, long byteBudget = PerGoalByteBudget)
    {
        if (string.IsNullOrWhiteSpace(storeRoot))
            return;

        string? goalDirectory = null;
        string? stagingDirectory = null;
        try
        {
            if (!Directory.Exists(packageDirectory))
                return;
            ArgumentOutOfRangeException.ThrowIfNegative(byteBudget);
            goalDirectory = GoalDirectory(storeRoot, goalId);
            Directory.CreateDirectory(goalDirectory);
            foreach (var leftover in Directory.EnumerateDirectories(goalDirectory, $".{taskId.Value}.*.staging"))
                Directory.Delete(leftover, recursive: true);

            var manifest = ReadManifest(goalDirectory, goalId, byteBudget);
            var retained = ReadRetainedPackages(goalDirectory, manifest);
            var skipped = manifest.Skipped.Where(item => item.TaskId != taskId.Value).ToList();
            var now = clock.UtcNow;
            var sequence = Math.Max(manifest.NextSequence, retained.Select(item => item.Sequence).DefaultIfEmpty(0).Max() + 1);
            var destination = TaskDirectory(storeRoot, goalId, taskId);
            var files = Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
                .Where(file => !Path.GetRelativePath(packageDirectory, file)
                    .Equals(LinkedBacklogRecordEvidence.FileName, StringComparison.OrdinalIgnoreCase)).ToArray();
            var bytes = files.Sum(file => new FileInfo(file).Length);

            if (bytes > byteBudget)
            {
                if (Directory.Exists(destination))
                    Directory.Delete(destination, recursive: true);
                retained.RemoveAll(item => item.TaskId == taskId.Value);
                skipped.Add(new SkippedPackage(taskId.Value, bytes, "package-exceeds-budget", now));
            }
            else
            {
                stagingDirectory = Path.Combine(goalDirectory, $".{taskId.Value}.{Guid.NewGuid():N}.staging");
                Directory.CreateDirectory(stagingDirectory);
                foreach (var file in files)
                {
                    var target = Path.Combine(stagingDirectory, Path.GetRelativePath(packageDirectory, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }

                // Measure the completed copy so the budget describes the bytes actually retained.
                bytes = DirectoryBytes(stagingDirectory);
                if (bytes > byteBudget)
                    throw new IOException("Package grew beyond the retention budget during copying.");
                retained.RemoveAll(item => item.TaskId == taskId.Value);
                var total = retained.Sum(item => item.Bytes) + bytes;
                foreach (var older in retained.OrderBy(item => item.Sequence).ThenBy(item => item.RetainedAt)
                             .ThenBy(item => item.TaskId, StringComparer.Ordinal).ToArray())
                {
                    if (total <= byteBudget)
                        break;
                    Directory.Delete(Path.Combine(goalDirectory, older.TaskId), recursive: true);
                    retained.Remove(older);
                    skipped.RemoveAll(item => item.TaskId == older.TaskId);
                    skipped.Add(new SkippedPackage(older.TaskId, older.Bytes, "evicted-for-newer-package", now));
                    total -= older.Bytes;
                }

                if (Directory.Exists(destination))
                    Directory.Delete(destination, recursive: true);
                Directory.Move(stagingDirectory, destination);
                stagingDirectory = null;
                retained.Add(new RetainedPackage(taskId.Value, bytes, sequence, now));
            }

            WriteManifest(goalDirectory, new Manifest(1, goalId.Value, byteBudget, sequence + 1, retained, skipped));
            File.Delete(Path.Combine(goalDirectory, taskId.Value + FailureNoteSuffix));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A sibling note marks any previous complete copy as stale for replay readers.
            try
            {
                if (goalDirectory is not null)
                    File.WriteAllText(Path.Combine(goalDirectory, taskId.Value + FailureNoteSuffix),
                        $"{clock.UtcNow:O} task={taskId.Value} {exception.GetType().Name}: {exception.Message}".ReplaceLineEndings(" ")
                        + Environment.NewLine);
            }
            catch (Exception noteException) when (noteException is not OperationCanceledException) { }
        }
        finally
        {
            if (stagingDirectory is not null)
            {
                try { Directory.Delete(stagingDirectory, recursive: true); }
                catch (Exception cleanupException) when (cleanupException is not OperationCanceledException) { }
            }
        }
    }

    private static Manifest ReadManifest(string goalDirectory, GoalId goalId, long byteBudget)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(
                File.ReadAllText(Path.Combine(goalDirectory, ManifestFileName)), JsonOptions);
            if (manifest is { Version: 1, Retained: not null, Skipped: not null } &&
                manifest.GoalId == goalId.Value && manifest.NextSequence > 0)
                return manifest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        return new Manifest(1, goalId.Value, byteBudget, 1, [], []);
    }

    private static List<RetainedPackage> ReadRetainedPackages(string goalDirectory, Manifest manifest) =>
        Directory.EnumerateDirectories(goalDirectory)
            .Where(path => !Path.GetFileName(path).StartsWith('.'))
            .Select(path =>
            {
                var taskId = Path.GetFileName(path);
                var recorded = manifest.Retained.FirstOrDefault(item => item.TaskId == taskId);
                return new RetainedPackage(taskId, DirectoryBytes(path), recorded?.Sequence ?? 0,
                    recorded?.RetainedAt ?? new DateTimeOffset(Directory.GetLastWriteTimeUtc(path)));
            }).ToList();

    private static long DirectoryBytes(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .Sum(file => new FileInfo(file).Length);

    private static void WriteManifest(string goalDirectory, Manifest manifest)
    {
        var temporaryPath = Path.Combine(goalDirectory, $".{Guid.NewGuid():N}.manifest.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(temporaryPath, Path.Combine(goalDirectory, ManifestFileName), overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private sealed record Manifest(int Version, string GoalId, long ByteBudget, long NextSequence,
        List<RetainedPackage> Retained, List<SkippedPackage> Skipped);
    private sealed record RetainedPackage(string TaskId, long Bytes, long Sequence, DateTimeOffset RetainedAt);
    private sealed record SkippedPackage(string TaskId, long Bytes, string Reason, DateTimeOffset SkippedAt);
}
