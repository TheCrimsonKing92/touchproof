using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleCommandHandler(
    IOwnerAnswerSubmitter answers,
    AnswerIntentStatusTracker answerTracking,
    OwnerConsoleControlCommands control,
    OwnerConsoleSessionPrinter printer,
    IOwnerConsoleOutput output,
    Func<OwnerFleetViewModel>? fleet,
    Func<int?>? terminalWidth,
    IReadOnlyDictionary<int, OwnerQuestion> open,
    IReadOnlySet<int> retired,
    Action<bool> setBell,
    Func<CancellationToken, Task<IReadOnlyList<GoalSummary>?>> refreshQuestions,
    Action<string> announce)
{
    internal async Task<bool> HandleCommandAsync(string raw, CancellationToken cancellationToken)
    {
        var line = raw.TrimEnd('\r').Trim();
        if (line.Length == 0) return true;
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        if (command == "fleet")
        {
            if (parts.Length != 1) announce("usage: fleet");
            else if (fleet is null) announce("fleet view unavailable in this session");
            else
            {
                try
                {
                    foreach (var row in OwnerFleetDiagram.Render(fleet(), OwnerConsoleDialogWidth.ForDiagram(terminalWidth?.Invoke())).Split('\n')) output.WriteLine(row);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { announce($"fleet unavailable: {ex.Message}"); }
            }
            return true;
        }
        var metadata = await refreshQuestions(cancellationToken);
        switch (command)
        {
            case "quit": return false;
            case "help":
                announce("board | goal <id-prefix> | answer <n> <text> | accept <n> | bell on|off | help | quit");
                output.WriteLine("conductor start [--clear-stop] | conductor stop [--yes] | conductor status");
                output.WriteLine("digest | metrics | board");
                output.WriteLine("fleet: Show the fleet diagram: host and executor boxes.");
                break;
            case "board": await printer.PrintBoardAsync(metadata, cancellationToken); break;
            case "conductor": control.HandleConductor(line); break;
            case "digest":
                if (parts.Length != 1) announce("usage: digest");
                else await printer.PrintDigestAsync(metadata, cancellationToken);
                break;
            case "metrics": control.HandleMetrics(line); break;
            case "goal":
                if (parts.Length < 2) announce("usage: goal <id-prefix>");
                else await printer.PrintGoalAsync(parts[1], cancellationToken);
                break;
            case "bell":
                if (parts.Length < 2 || parts[1] is not ("on" or "off")) announce("usage: bell on|off");
                else { setBell(parts[1] == "on"); announce($"bell {parts[1]}"); }
                break;
            case "answer":
            case "accept":
                if (parts.Length < 2 || !int.TryParse(parts[1], out var number) || number < 1)
                { announce($"usage: {command} <n>{(command == "answer" ? " <text>" : "")}"); break; }
                await SubmitAsync(command, number, parts.Length == 3 ? parts[2] : null, cancellationToken);
                break;
            default: announce("unknown command; type help"); break;
        }
        return true;
    }

    private async Task SubmitAsync(string command, int number, string? text, CancellationToken cancellationToken)
    {
        if (!open.TryGetValue(number, out var question))
        {
            announce(retired.Contains(number) ? $"question {number} is no longer open" : $"no question {number}");
            return;
        }
        if (OwnerQuestionViewOnly.IsViewOnly(question.Kind))
        { announce(OwnerQuestionViewOnly.Notice(question.Kind, question.GoalId)); return; }
        if (command == "accept")
        {
            text = question.ProposedDefault;
            if (string.IsNullOrWhiteSpace(text)) { announce($"question {number} has no proposed default"); return; }
        }
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("--", StringComparison.Ordinal))
        { announce("usage: answer <n> <text> (text cannot start with --)"); return; }
        try
        {
            var submission = answers.Submit(question, text);
            var prefix = question.GoalId[..Math.Min(8, question.GoalId.Length)];
            announce(AnswerIntentStatusTracker.Queued(number, prefix));
            answerTracking.Track(submission.IntentId, number, prefix, announce);
            await refreshQuestions(cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        { announce($"error: {ex.Message}"); await refreshQuestions(cancellationToken); }
    }
}
