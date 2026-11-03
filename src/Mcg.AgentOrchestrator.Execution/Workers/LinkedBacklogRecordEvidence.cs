using System.Globalization;
using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A dispatch-local snapshot: neither rendering may independently re-read the source item.
internal static class LinkedBacklogRecordEvidence
{
    internal const string FileName = "linked-backlog-record.md";
    internal const string PointerIdentity = "goal/linked-backlog-pointer.md";
    internal const string PointerHeading = "## Linked backlog item";
    private const string TitleMarker = "... [title truncated]";
    private const int PointerMaxBytes = 1_000;
    private const int RecordMaxBytes = 20_000;

    internal sealed record Snapshot(string Pointer, string Record);

    internal static void AddArtifacts(Goal goal, TaskSpec task, string workingDirectory, string contextDirectory,
        string? pointer, bool exists, bool hashVerified, string? expectedHash,
        Action<WorkerProfileDispatcher.WorkerContextSemanticSource, string, ContextArtifactKind, byte[], IReadOnlyList<AgentRole>?, ContextDeliveryMode?> addSource)
    {
        if (task.RequiredRole != AgentRole.Planner || pointer is null)
            return;
        var recordPath = Path.Combine(contextDirectory, FileName);
        try
        {
            WorkerArtifactWriter.RequireRegistryArtifactReady(FileName, exists, hashVerified);
            var recordBytes = File.ReadAllBytes(recordPath);
            if (WorkerContextArtifact.Hash(recordBytes) != expectedHash)
                throw new IOException("Linked backlog record changed during dispatch preparation.");
            addSource(WorkerProfileDispatcher.WorkerContextSemanticSource.RegistryArtifact, $"context/{FileName}",
                ContextArtifactKind.RegisteredContext, recordBytes, [AgentRole.Planner], ContextDeliveryMode.OnDemandFile);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WorkerArtifactWriter.DeleteLinkedBacklogFile(recordPath);
            WorkerArtifactWriter.DeleteLinkedBacklogFile(Path.Combine(contextDirectory, "packages", task.Id.Value, FileName));
            WorkerArtifactWriter.DeleteLinkedBacklogFile(Path.Combine(workingDirectory,
                ".orchestrator-context", goal.Id.Value, "authoritative", task.Id.Value, "typed", "context", FileName));
            return;
        }
        addSource(WorkerProfileDispatcher.WorkerContextSemanticSource.RegistryArtifact, PointerIdentity,
            ContextArtifactKind.RegisteredContext, Encoding.UTF8.GetBytes(pointer),
            [AgentRole.Planner], ContextDeliveryMode.InlineFull);
    }

    internal static Snapshot? Read(string? storePath, Goal goal, AgentRole role)
    {
        if (role != AgentRole.Planner || string.IsNullOrWhiteSpace(storePath) ||
            string.IsNullOrWhiteSpace(goal.SourceBacklogItemId))
            return null;

        try
        {
            var store = BacklogStore.OpenReadOnly(storePath);
            var item = store.GetByExactIdAsync(goal.SourceBacklogItemId).GetAwaiter().GetResult();
            if (item is null)
                return null;

            var links = item.Links.Select(link =>
            {
                var id = link.OtherId(item.Id);
                var target = store.GetByExactIdAsync(id).GetAwaiter().GetResult();
                return $"- {id} | {link.Kind} | {SingleLine(target?.Title ?? "[item unavailable]")}\n";
            }).ToArray();
            return new Snapshot(RenderPointer(item, links), RenderRecord(item));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Optional enrichment must not turn an unavailable store into a failed dispatch.
            return null;
        }
    }

    private static string RenderPointer(BacklogItem item, IReadOnlyList<string> links)
    {
        var header = $"{PointerHeading}\nId: {item.Id} | Status: {item.Status}\nTitle: ";
        const string instruction = "Read linked-backlog-record.md in the context directory for the full record before planning.\n";
        string Omitted(int count) => count == 0 ? string.Empty : $"Omitted {count} links.\n";
        var title = BoundedTitle(item.Title, PointerMaxBytes - Bytes(header + "\n" + instruction + Omitted(links.Count)));
        var prefix = header + title + "\n";
        var included = 0;
        while (included < links.Count &&
               Bytes(prefix + links[included] + Omitted(links.Count - included - 1) + instruction) <= PointerMaxBytes)
            prefix += links[included++];
        return prefix + Omitted(links.Count - included) + instruction;
    }

    private static string RenderRecord(BacklogItem item)
    {
        var header = $"# Linked backlog record\nId: {item.Id}\nStatus: {item.Status}\nTitle: {BoundedTitle(item.Title, 1_000)}\n\n## Body\n";
        const string notesHeader = "\n\n## Notes\n";
        var notes = item.Notes.OrderBy(note => note.CreatedAt)
            .Select(note => $"### {note.CreatedAt.ToString("O", CultureInfo.InvariantCulture)}\n{note.Text.ReplaceLineEndings("\n")}\n")
            .ToArray();
        var notesBytes = notes.Sum(Bytes);
        var body = item.Body.ReplaceLineEndings("\n");
        var bodyScalars = item.Body.EnumerateRunes().Count();
        string Footer(int dropped, int omitted) => $"Omitted {dropped} notes and {omitted} body characters.\n";
        var dropped = 0;
        while (dropped < notes.Length &&
               Bytes(header + body + notesHeader + Footer(dropped, 0)) + notesBytes > RecordMaxBytes)
            notesBytes -= Bytes(notes[dropped++]);

        // Reserve the largest possible omission count, then reclaim any unused digit space.
        var overhead = Bytes(header + notesHeader) + notesBytes;
        var footer = Footer(dropped, bodyScalars);
        var retained = BodyPrefix(item.Body, RecordMaxBytes - overhead - Bytes(footer));
        while (true)
        {
            var nextFooter = Footer(dropped, bodyScalars - retained.Scalars);
            var next = BodyPrefix(item.Body, RecordMaxBytes - overhead - Bytes(nextFooter));
            if (next == retained)
            {
                footer = nextFooter;
                break;
            }
            retained = next;
        }
        return header + retained.Text + notesHeader + string.Concat(notes.Skip(dropped)) + footer;
    }

    private static string SingleLine(string value) => value.ReplaceLineEndings(" ");
    private static int Bytes(string value) => Encoding.UTF8.GetByteCount(value);

    private static string BoundedTitle(string value, int budget)
    {
        value = SingleLine(value);
        return Bytes(value) <= budget ? value : Prefix(value, budget - Bytes(TitleMarker)) + TitleMarker;
    }

    private static string Prefix(string value, int budget)
    {
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > budget)
                break;
            result.Append(rune.ToString());
            budget -= rune.Utf8SequenceLength;
        }
        return result.ToString();
    }

    private static (string Text, int Scalars) BodyPrefix(string value, int budget)
    {
        var result = new StringBuilder();
        var scalars = 0;
        var afterCarriageReturn = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (afterCarriageReturn && rune.Value == '\n')
            {
                scalars++;
                afterCarriageReturn = false;
                continue;
            }
            var text = rune.Value is '\r' or '\n' or 0x85 or 0x2028 or 0x2029 or '\f'
                ? "\n" : rune.ToString();
            var size = Bytes(text);
            if (size > budget)
                break;
            result.Append(text);
            budget -= size;
            scalars++;
            afterCarriageReturn = rune.Value == '\r';
        }
        return (result.ToString(), scalars);
    }
}
