using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleFullScreenHostStartup
{
    // Eager history/model helper retained for callers that need a complete snapshot.
    // Production progressive startup uses OwnerConsoleStartupLoader.FillAsync.
    internal static async Task<(OwnerConsoleViewModel Model, List<OwnerConductEvent> Recent)> BuildInitialViewModelAsync(
        OwnerConsoleViewModelBuilder builder, string conductLogPath, DateTimeOffset opened,
        DateTimeOffset? last, CancellationToken token)
    {
        var history = OwnerConsoleStartupActivity.ReadHistory(conductLogPath, null, TimeProvider.System, token);
        var recent = history.Recent.ToList();
        var model = await builder.BuildAsync(new(opened, last, recent, history.LandedToday), token);
        return (model, recent);
    }

    internal static async Task<int> RunAsync(OrchestratorWorkspace workspace, CancellationToken cancellationToken,
        string? inputTracePath = null, OwnerConsoleHostRowOwner? owners = null, OwnerConsoleBoardSource? boardSource = null)
    {
        if (inputTracePath is null) return await OwnerConsoleFullScreenHost.RunCoreAsync(workspace, cancellationToken, null, owners, boardSource);
        try
        {
            using var destination = OwnerConsoleInputTraceOptions.OpenDestination(inputTracePath);
            var diagnostics = new OwnerConsoleInputDiagnostics();
            var result = await OwnerConsoleFullScreenHost.RunCoreAsync(workspace, cancellationToken, diagnostics, owners, boardSource);
            // RunCoreAsync has disposed the application and restored the terminal before export.
            diagnostics.Export(destination);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            System.Console.Error.WriteLine($"Input trace failed: {ex.Message}");
            return 1;
        }
    }
}
