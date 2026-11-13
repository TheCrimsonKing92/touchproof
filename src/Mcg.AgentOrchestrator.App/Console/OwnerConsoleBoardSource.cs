using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleBoardProject(string Name, string StatePath, string PortfolioPath,
    string? Error = null, string? OrchestratorDirectory = null, string? ConductEventsLogPath = null,
    bool DeliveryAnswered = false, bool UseLearnedBranch = false);
internal sealed record OwnerConsoleBoardScope(string Label, int ProjectCount, string? DisclosureWorkspace,
    IReadOnlyList<string>? Sections = null);
internal sealed record OwnerConsoleScopeCommandResult(string Message, bool Success, bool Changed);

// Owns BOARD reads and observers for the current project scope, for one console instance.
internal sealed class OwnerConsoleBoardSource(string launchProject, OwnerConsoleProjectScope initialScope,
    Func<IReadOnlyList<OwnerConsoleBoardProject>> catalog, TimeProvider clock,
    OwnerConsoleBoardProject? launchWorkspace = null) : IDisposable
{
    private readonly object _sync = new();
    private OwnerConsoleProjectScope _scope = initialScope;
    private readonly SemaphoreSlim _reads = new(1, 1);
    private readonly Dictionary<string, ProjectReads> _projects = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _activeProjects = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    internal int RetainedProjects => _projects.Count;
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
        CancellationToken token = default, bool force = false)
    {
        await _reads.WaitAsync(token);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            OwnerConsoleProjectScope scope;
            lock (_sync) scope = _scope;
            return await OwnerConsoleBoardComposer.ComposeAsync(launchProject, scope, catalog, clock,
                launchWorkspace, own, token, project =>
                {
                    used.Add(Key(project));
                    return Project(project);
                }, force);
        }
        finally { _activeProjects = used; Prune(used); _reads.Release(); }
    }

    internal async Task<bool> HasLandedAsync(OwnerConsoleBoardProject project, CancellationToken token)
    {
        await _reads.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (await Project(project).MetadataAsync(token)).Any(goal => goal.PromotionFact is not null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException) { return false; }
        finally { Prune(_activeProjects); _reads.Release(); }
    }

    private static string Key(OwnerConsoleBoardProject project) =>
        project.Name + "\n" + Path.GetFullPath(project.StatePath) + "\n" + Path.GetFullPath(project.PortfolioPath);

    private ProjectReads Project(OwnerConsoleBoardProject project)
    {
        var key = Key(project);
        if (!_projects.TryGetValue(key, out var reads)) _projects.Add(key, reads = new(project, clock));
        return reads;
    }

    private void Prune(HashSet<string> used)
    {
        foreach (var key in _projects.Keys.Where(key => !used.Contains(key)).ToArray())
        { _projects[key].Dispose(); _projects.Remove(key); }
    }

    public void Dispose()
    {
        // Do not dispose a connection under an active read (including a late canceled read).
        _reads.Wait();
        try { if (_disposed) return; _disposed = true; Prune([]); }
        finally { _reads.Release(); }
    }

    internal sealed class ProjectReads : IDisposable
    {
        private readonly SqliteStoreChangeObservation _stateChanges;
        private readonly SqliteStoreChangeObservation _portfolioChanges;
        private readonly IOrchestratorStateQueries _state;
        private string? _metadataVersion;
        private IReadOnlyList<GoalSummary>? _metadata;
        internal OwnerConsoleViewModelBuilder Builder { get; }

        internal ProjectReads(OwnerConsoleBoardProject project, TimeProvider clock)
        {
            _stateChanges = new(project.StatePath);
            _portfolioChanges = new(project.PortfolioPath);
            _state = new EmptyOwnerConsoleStateQueries(project.StatePath);
            Builder = new(_state, new NoQuestions(), new NoLiveness(), new PortfolioGoalEpicLookup(project.PortfolioPath),
                clock, boardMarker: _stateChanges.Observe, epicMarker: _portfolioChanges.Observe);
        }

        internal async Task<IReadOnlyList<GoalSummary>> MetadataAsync(CancellationToken token, bool force = false)
        {
            token.ThrowIfCancellationRequested();
            var version = _stateChanges.Observe();
            if (!force && version is not null && version == _metadataVersion && _metadata is not null) return _metadata;
            _metadataVersion = null;
            var metadata = await _state.ListGoalMetadataAsync(token);
            token.ThrowIfCancellationRequested();
            _metadata = metadata;
            _metadataVersion = version is not null && version == _stateChanges.Observe() ? version : null;
            return metadata;
        }

        public void Dispose() { _stateChanges.Dispose(); _portfolioChanges.Dispose(); }
        private sealed class NoQuestions : IOwnerQuestionSource
        {
            public Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken token) =>
                Task.FromResult<IReadOnlyList<OwnerQuestion>>([]);
        }
        private sealed class NoLiveness : IConductorLiveness { public ConductorLiveness IsRunning() => ConductorLiveness.NotRunning; }
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
