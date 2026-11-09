using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleBoardProject(string Name, string StatePath, string PortfolioPath,
    string? Error = null, string? OrchestratorDirectory = null, string? ConductEventsLogPath = null,
    bool DeliveryAnswered = false, bool UseLearnedBranch = false);
internal sealed record OwnerConsoleBoardScope(string Label, int ProjectCount, string? DisclosureWorkspace,
    IReadOnlyList<string>? Sections = null);
internal sealed record OwnerConsoleScopeCommandResult(string Message, bool Success, bool Changed);

// Owns BOARD-only reads. Foreign builders and their goal-id caches never escape a refresh.
internal sealed class OwnerConsoleBoardSource(string launchProject, OwnerConsoleProjectScope initialScope,
    Func<IReadOnlyList<OwnerConsoleBoardProject>> catalog, TimeProvider clock,
    OwnerConsoleBoardProject? launchWorkspace = null)
{
    private readonly object _sync = new();
    private OwnerConsoleProjectScope _scope = initialScope;
    internal OrchestratorProjectRegistry? Registry { get; private init; }

    internal static OwnerConsoleBoardSource FromRegistry(OrchestratorWorkspace workspace,
        string repositoryRoot, OrchestratorProjectRegistry registry, TimeProvider clock) =>
        new(OwnerConsoleProjectLabel.ForWorkspace(workspace), OwnerConsoleProjectScope.ForLaunch(workspace), () =>
        {
            var inventory = OwnerConsoleWorkspaceCatalog.ReadWorkspaceCatalog(repositoryRoot, workspace.TenantName, registry, workspace);
            if (inventory.RegistryError is { } error) throw error;
            return inventory.Projects.Select(entry => entry.Workspace is { } target
                ? new OwnerConsoleBoardProject(entry.Name, target.SqliteStatePath, target.PortfolioStorePath,
                    OrchestratorDirectory: target.OrchestratorDirectory, ConductEventsLogPath: target.ConductEventsLogPath,
                    DeliveryAnswered: entry.DeliveryAnswered, UseLearnedBranch: entry.UseLearnedBranch)
                : new OwnerConsoleBoardProject(entry.Name, "", "", entry.Error!.Message)).ToArray();
        }, clock, new(OwnerConsoleProjectLabel.ForWorkspace(workspace), workspace.SqliteStatePath,
            workspace.PortfolioStorePath, OrchestratorDirectory: workspace.OrchestratorDirectory,
            ConductEventsLogPath: workspace.ConductEventsLogPath)) { Registry = registry };

    internal IReadOnlyList<OwnerConsoleBoardProject> InScopeProjects()
    {
        OwnerConsoleProjectScope scope;
        lock (_sync) scope = _scope;
        return Ordered(catalog()).Where(project => scope.IsAll ||
            scope.Names.Contains(project.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    internal OwnerConsoleScopeCommandResult HandleScope(string? argument)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(argument)) return new($"scope: {_scope.Label}", true, false);
            try
            {
                var known = argument.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
                    ? Array.Empty<string>() : Ordered(catalog()).Select(project => project.Name).ToArray();
                var scope = OwnerConsoleProjectScope.Parse(argument, known, out var error);
                if (scope is null) return new(error!, false, false);
                var changed = !_scope.SameAs(scope);
                _scope = scope;
                return new($"scope: {scope.Label}", true, changed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { return new($"scope: could not read projects: {ex.Message}", false, false); }
        }
    }

    internal async Task<OwnerConsoleViewModel> ComposeAsync(OwnerConsoleViewModel own,
        CancellationToken token = default)
    {
        OwnerConsoleProjectScope scope;
        lock (_sync) scope = _scope;
        return await OwnerConsoleBoardComposer.ComposeAsync(launchProject, scope, catalog, clock,
            launchWorkspace, own, token);
    }

    internal static string ForeignRowMessage(OwnerConsoleBoardRow row) =>
        $"{row.GoalPrefix} belongs to {row.Project}. Detail and actions for other projects arrive in a later slice; open it with --project={(row.Project.Equals("home", StringComparison.OrdinalIgnoreCase) ? "default" : row.Project)} console.";

    internal static IEnumerable<OwnerConsoleBoardProject> Ordered(IReadOnlyList<OwnerConsoleBoardProject> projects) =>
        projects.DistinctBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(project => project.Name.Equals("home", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

    internal static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static OwnerConsoleBoardRow Diagnostic(string name, Exception error) =>
        new("", "", $"{name}: state read failed: {error.Message.ReplaceLineEndings(" ")}", "", "", null, "", Dimmed: true)
        { Project = name, IsDiagnostic = true };
}
