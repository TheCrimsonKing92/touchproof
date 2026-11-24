using System.Text.Json;
using static Mcg.AgentOrchestrator.App.Orchestration.PreTesterReverseDependencyObservation;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class PreTesterResolverObservationWindow(
    string directory, Func<Guid, Producer> identify, Action<string> report, string? handoff = null) : IDisposable
{
    private readonly Guid _session = Guid.NewGuid();
    private PreTesterReverseDependencyObservation? _writer;
    private Guid? _handledWindow;
    private string? _handoff = handoff;

    internal Tick? ActiveTick { get; private set; }

    internal static PreTesterResolverObservationWindow ForConductLoop(OrchestratorWorkspace workspace,
        ConductorResolverObservationLaunch.VerifiedLaunch launch) =>
        new(Path.Combine(workspace.OrchestratorDirectory, "pretester-resolver-observation"),
            session => CurrentProducer(workspace, session, launch), Console.WriteLine, launch.Checkpoint);

    internal Tick? BeginTick(Guid? request, Func<DateTimeOffset> utcNow)
    {
        if (request is null)
        {
            _handoff = null;
            Interrupt("window-disabled-or-replaced");
            _handledWindow = null;
            return null;
        }
        if (request == _handledWindow && _writer is null) return null;
        var started = utcNow();
        if (request != _handledWindow)
        {
            Interrupt("window-disabled-or-replaced");
            _handledWindow = request;
            Start(request.Value, started);
        }

        if (_writer is null) return null;
        if (started >= _writer.Header.Deadline)
        {
            SealWindow(started);
            return null;
        }

        return ActiveTick = new(_writer, started, (tick, completed) =>
        {
            ActiveTick = null;
            if (!completed)
            {
                Interrupt("unfinished-tick");
                return;
            }
            try
            {
                var ended = utcNow();
                tick.Commit(ended);
                if (ended >= _writer!.Header.Deadline)
                    SealWindow(ended);
            }
            catch (Exception error) when (IsObservationFailure(error))
            {
                Interrupt("tick-capture-failed: " + error.Message);
            }
        });
    }

    private void Start(Guid id, DateTimeOffset started)
    {
        var continuation = _handoff;
        _handoff = null;
        if (id == Guid.Empty) return;
        var path = Path.Combine(directory, $"{id:D}.jsonl");
        try
        {
            Directory.CreateDirectory(directory);
            // Create-new reserves the window. Only the supervisor's clean-exit handoff can resume it.
            if (File.Exists(path))
            {
                if (continuation is not null && !File.Exists(path + ".receipt.json"))
                {
                    _writer = new(path, id, started, () => identify(_session), continuation);
                    Report($"PRE_TESTER_RESOLVER_WINDOW window={id:D} session={_session:D} status=resumed deadline={_writer.Header.Deadline:O}");
                    return;
                }
                using var input = File.OpenText(path);
                var firstLine = input.ReadLine();
                var previous = firstLine is null ? null : JsonSerializer.Deserialize<WindowHeader>(firstLine)?.Producer;
                var status = File.Exists(path + ".receipt.json") ? "existing-window" : "interrupted-without-receipt";
                Report($"PRE_TESTER_RESOLVER_WINDOW window={id:D} session={_session:D} previousSession={previous?.SessionId:D} status={status}");
                return;
            }
            if (continuation?.StartsWith(id.ToString("N") + ":", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidDataException("Resolver observation continuation corpus is missing.");
            _writer = new(path, id, started, () => identify(_session));
            Report($"PRE_TESTER_RESOLVER_WINDOW window={id:D} session={_session:D} status=recording deadline={_writer.Header.Deadline:O} path={path}");
        }
        catch (Exception error) when (IsObservationFailure(error))
        {
            Report($"PRE_TESTER_RESOLVER_WINDOW window={id:D} session={_session:D} status=start-failed reason={error.Message}");
        }
    }

    internal void ObserveDrainPass(Guid? request)
    {
        if (_writer is not null && request != _handledWindow)
            Interrupt("window-disabled-or-replaced");
        if (request is null)
        {
            _handoff = null;
            _handledWindow = null;
        }
    }

    internal string? CloseForRenewal(Func<DateTimeOffset> utcNow)
    {
        if (_writer is not { } writer) return null;
        if (ActiveTick is not null)
        {
            Interrupt("unfinished-tick");
            return null;
        }
        try
        {
            var now = utcNow();
            if (now >= writer.Header.Deadline)
            {
                SealWindow(now);
                return null;
            }
            var continuation = writer.Suspend(now);
            _writer = null;
            Report($"PRE_TESTER_RESOLVER_WINDOW window={writer.Header.WindowId:D} session={_session:D} status=checkpointed");
            return continuation;
        }
        catch (Exception error) when (IsObservationFailure(error))
        {
            Interrupt("checkpoint-failed: " + error.Message);
            return null;
        }
    }

    internal void CloseOrderly(Func<DateTimeOffset> utcNow)
    {
        if (_writer is not { } writer) return;
        var now = utcNow();
        if (ActiveTick is null && now >= writer.Header.Deadline)
            SealWindow(now);
    }

    private void SealWindow(DateTimeOffset sealedAt)
    {
        if (_writer is not { } writer) return;
        try
        {
            Publish(writer.Path, writer.Complete(sealedAt));
            _writer = null;
        }
        catch (Exception error) when (IsObservationFailure(error))
        {
            Interrupt("seal-failed: " + error.Message);
        }
    }

    private void Interrupt(string reason)
    {
        if (_writer is not { } writer) return;
        _writer = null;
        try
        {
            writer.Dispose();
            Publish(writer.Path, new(Schema, writer.Header.WindowId, _session, "SHA-256", null, "incomplete", reason));
        }
        catch (Exception error) when (IsObservationFailure(error))
        {
            Report($"PRE_TESTER_RESOLVER_WINDOW window={writer.Header.WindowId:D} session={_session:D} status=incomplete reason={reason} publication={error.Message}");
        }
    }

    private void Publish(string path, Seal seal)
    {
        var receiptPath = path + ".receipt.json";
        var temporary = receiptPath + "." + _session.ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, seal);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, receiptPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        Report($"PRE_TESTER_RESOLVER_WINDOW window={seal.WindowId:D} session={_session:D} status={seal.Status} receipt={receiptPath}");
    }

    private void Report(string line)
    {
        try { report(line); }
        catch (Exception error) when (IsObservationFailure(error)) { }
    }

    public void Dispose() => Interrupt("conductor-exited-before-deadline");
}
