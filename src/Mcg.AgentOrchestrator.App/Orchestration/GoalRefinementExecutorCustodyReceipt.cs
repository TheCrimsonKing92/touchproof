using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>
/// Best-effort evidence of a stamped executor's launch, entry and exit. Status describes
/// the recorded sections, not process liveness or the cause of an absent section.
/// </summary>
internal static class GoalRefinementExecutorCustodyReceipt
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal sealed record Launch(int ProcessId, string LaunchDetail, DateTimeOffset LaunchInstant);
    internal sealed record Exit(DateTimeOffset ExitInstant, string Outcome);
    internal sealed record Child(int ProcessId, DateTimeOffset StartInstant, string StateStorePath, Exit? Exit = null);
    internal sealed record Document(Launch? Launch = null, Child? Child = null);
    internal enum Status { Unavailable, Invalid, LaunchedButNeverStarted, StartedButDidNotExit, Exited }
    internal sealed record ReadResult(Status Status, Document? Receipt = null);

    internal static string GetPath(string logDirectory, GoalId goalId, string stamp)
    {
        var prefix = goalId.Value[..Math.Min(8, goalId.Value.Length)];
        ValidateSegment(prefix);
        ValidateSegment(stamp);
        return Path.Combine(logDirectory, $"spec-refinement-{prefix}-{stamp}.custody.json");
    }

    private static void ValidateSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
            throw new ArgumentException("Custody receipt identity must be a filename segment.");
    }

    internal static void RecordLaunch(string logDirectory, GoalId goalId, string stamp, ConductLoopLaunchResult launched)
    {
        try
        {
            var path = GetPath(logDirectory, goalId, stamp);
            var launch = new Launch(launched.ProcessId, launched.LaunchDetail,
                (launched.StartedAt ?? DateTimeOffset.UtcNow).ToUniversalTime());
            TryUpdate(path, document => document with { Launch = launch });
        }
        catch (Exception) { /* Diagnostics must not change a successful launch. */ }
    }

    internal static ChildScope BeginChild(string logDirectory, GoalId goalId, string? stamp, string stateStorePath)
    {
        try
        {
            if (stamp is not null)
            {
                var path = GetPath(logDirectory, goalId, stamp);
                var child = new Child(Environment.ProcessId, DateTimeOffset.UtcNow, Path.GetFullPath(stateStorePath));
                TryUpdate(path, document => document with { Child = child });
                return new ChildScope(path, child);
            }
        }
        catch (Exception) { /* Includes path construction and store-path resolution for recording. */ }
        return new ChildScope(null, null);
    }

    internal sealed class ChildScope(string? path, Child? child)
    {
        private bool? _claimed;
        private string _outcome = "completed";

        internal void RecordClaim(bool claimed) => _claimed = claimed;

        internal void RecordException(Exception exception, bool isExitSignal) =>
            _outcome = _claimed == false && isExitSignal ? "claim-miss" : exception.GetType().Name;

        internal void RecordExit()
        {
            if (path is null || child is null)
                return;
            TryUpdate(path, document => document with
            {
                Child = child with { Exit = new Exit(DateTimeOffset.UtcNow, _outcome) }
            });
        }
    }

    internal static ReadResult Read(string logDirectory, GoalId goalId, string stamp)
    {
        try
        {
            var document = Load(GetPath(logDirectory, goalId, stamp));
            return new ReadResult(document.Child is { } child
                ? child.Exit is null ? Status.StartedButDidNotExit : Status.Exited
                : Status.LaunchedButNeverStarted, document);
        }
        catch (JsonException) { return new ReadResult(Status.Invalid); }
        catch (ArgumentException) { return new ReadResult(Status.Invalid); }
        catch (Exception) { return new ReadResult(Status.Unavailable); }
    }

    private static Document Load(string path)
    {
        // Readers permit atomic replacement while holding the old complete document open.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var document = JsonSerializer.Deserialize<Document>(stream, JsonOptions);
        if (document is null || (document.Launch is null && document.Child is null) ||
            (document.Launch is { } launch && (launch.ProcessId <= 0 || launch.LaunchDetail is null || !ValidInstant(launch.LaunchInstant))) ||
            (document.Child is { } child && (child.ProcessId <= 0 || !ValidInstant(child.StartInstant) ||
                string.IsNullOrWhiteSpace(child.StateStorePath) || !Path.IsPathFullyQualified(child.StateStorePath) ||
                (child.Exit is { } exit && (!ValidInstant(exit.ExitInstant) || string.IsNullOrWhiteSpace(exit.Outcome))))))
            throw new JsonException("Custody receipt sections are incomplete or inconsistent.");
        return document;
    }

    private static bool ValidInstant(DateTimeOffset instant) =>
        instant != DateTimeOffset.MinValue && instant.Offset == TimeSpan.Zero;

    private static void TryUpdate(string path, Func<Document, Document> update)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var coordination = AcquireLock(path + ".lock");
            Document document;
            try { document = Load(path); }
            catch (FileNotFoundException) { document = new Document(); }
            // A malformed or unreadable existing document is preserved, never replaced by guessed contents.
            document = update(document);
            temporary = path + $".{Guid.NewGuid():N}.tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, document, JsonOptions);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception) { /* Custody recording cannot affect work, output, exceptions or exit codes. */ }
        finally
        {
            try { if (temporary is not null) File.Delete(temporary); }
            catch (Exception) { /* Cleanup is best effort too. */ }
        }
    }

    private static FileStream AcquireLock(string path)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(2))
            {
                Thread.Sleep(20);
            }
        }
    }
}
