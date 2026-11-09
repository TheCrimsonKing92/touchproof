using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleCommandDispatch(IOwnerConsoleDialogs dialogs, IOwnerConsoleConductor conductor,
    IOwnerConsoleDigestReport digest, IOwnerDigestSummary summary, TimeProvider clock,
    Func<OwnerFleetViewModel>? fleet, Func<string, OwnerLandingsPage>? landings, OwnerConsoleSoundCommand? sound,
    OwnerGoalDetailQuery goalDetail, OwnerFleetViewKeys fleetKeys, object notificationGate, OwnerConsoleBellCommand bell,
    Func<bool> settingsRequested, Action requestSettings, Func<string?> requestedEpicId, Func<string?> requestedGateGoalId,
    Func<string, OwnerConsoleScreenOperation?, CancellationToken, Task> showGoalDetail)
{
    private readonly OwnerGoalDetailQuery _goalDetail = goalDetail;
    private readonly OwnerFleetViewKeys _fleetKeys = fleetKeys;
    private readonly object _notificationGate = notificationGate;
    private readonly OwnerConsoleBellCommand _bell = bell;

    internal async Task RunAsync(string raw, OwnerConsoleScreenOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var line = raw.Trim().TrimStart(OwnerConsoleBindings.CommandLineKey).Trim();
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var output = new OwnerConsoleCapturedOutput();
        string? goalId = null;
        OwnerFleetViewModel? fleetModel = null;
        OwnerLandingsPage? landingsPage = null;
        var control = new OwnerConsoleControlCommands(conductor, digest, output, clock);
        if (!await RunDependencyAsync(line, async stepToken =>
            (goalId, fleetModel, landingsPage) = await ExecuteAsync(line, parts, output, control, stepToken), operation, cancellationToken)) return;
        cancellationToken.ThrowIfCancellationRequested();
        if (settingsRequested()) return;
        if (goalId is not null) { await showGoalDetail(goalId, operation, cancellationToken); return; }
        if (fleetModel is not null) { await dialogs.ShowFleetAsync(_fleetKeys, fleetModel, OwnerConsoleDialogWidth.ForDiagram(dialogs.TextWidth)); return; }
        if (landingsPage is not null) { await ShowLandingsAsync(landingsPage, operation, cancellationToken); return; }
        if ((output.Structured ?? control.Fields) is { } commandFields)
            await dialogs.ShowStructuredAsync("Command result", output.Text, commandFields);
        else await dialogs.ShowTextAsync("Command result", output.Text);
    }

    private async Task<(string? GoalId, OwnerFleetViewModel? FleetModel, OwnerLandingsPage? LandingsPage)> ExecuteAsync(
        string line, string[] parts, OwnerConsoleCapturedOutput output, OwnerConsoleControlCommands control, CancellationToken stepToken)
    {
        string? goalId = null;
        OwnerFleetViewModel? fleetModel = null;
        OwnerLandingsPage? landingsPage = null;
        switch (parts[0].ToLowerInvariant())
        {
            case "conductor": control.HandleConductor(line); break;
            case "digest":
                if (parts.Length != 1) output.WriteLine("usage: digest");
                else
                {
                    var result = summary.ReadStructuredSummary();
                    foreach (var item in result.Lines.Take(5)) output.WriteLine(item);
                    output.Structured = result.Fields;
                }
                break;
            case "metrics": control.HandleMetrics(line); break;
            case "fleet":
                if (parts.Length != 1) output.WriteLine("usage: fleet");
                else if (fleet is null) output.WriteLine("fleet view unavailable in this session");
                else fleetModel = fleet();
                break;
            case "landings":
                if (parts.Length > 2) output.WriteLine(OwnerLandingsQuery.Usage);
                else if (landings is null) output.WriteLine("landings view unavailable in this session");
                else landingsPage = landings(parts.Length == 2 ? parts[1] : "");
                break;
            case "sound":
                if (sound is null) output.WriteLine("sound unavailable in this session");
                else sound.Run(line, output);
                break;
            case OwnerConsoleBindings.SettingsCommand:
                if (parts.Length != 1) output.WriteLine("usage: settings");
                else if (sound is null) output.WriteLine("settings unavailable in this session");
                else requestSettings();
                break;
            case "bell":
                lock (_notificationGate) _bell.Run(parts.Skip(1).ToArray(), output);
                break;
            case var verb when verb == OwnerConsoleBindings.Goal.Command:
                if (parts.Length != 2) { output.WriteLine("usage: " + OwnerConsoleBindings.Goal.Usage); break; }
                goalId = await _goalDetail.ResolveGoalIdAsync(parts[1], output, stepToken);
                break;
            default:
                output.WriteLine("unknown command");
                output.WriteLine("valid commands: " + OwnerConsoleKeyHints.CommandPrompt);
                break;
        }
        return (goalId, fleetModel, landingsPage);
    }

    private async Task ShowLandingsAsync(OwnerLandingsPage page, OwnerConsoleScreenOperation? operation,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chosen = page.Fields is { } fields
                ? await dialogs.ShowStructuredAsync("Landings", page.Text, fields, page.ChoiceLines)
                : await dialogs.ShowPageAsync("Landings", page.Text, page.ChoiceLines);
            if (chosen is not { } index || index < 0 || index >= page.ChoiceGoalIds.Count) return;
            await showGoalDetail(page.ChoiceGoalIds[index], operation, cancellationToken);
            if (requestedEpicId() is not null || requestedGateGoalId() is not null) return;
        }
    }

    internal static async Task<bool> RunDependencyAsync(string label, Func<CancellationToken, Task> action,
        OwnerConsoleScreenOperation? operation, CancellationToken cancellationToken)
    {
        if (operation is not null) return await operation.RunAsync(label, action, cancellationToken);
        await action(cancellationToken);
        return true;
    }
}
