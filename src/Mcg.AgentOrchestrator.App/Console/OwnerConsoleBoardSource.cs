using System.Collections.Immutable;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleBoardProject(string Name, string StatePath, string PortfolioPath,
    string? Error = null, string? OrchestratorDirectory = null, string? ConductEventsLogPath = null,
    bool DeliveryAnswered = false, bool UseLearnedBranch = false);
internal sealed record OwnerConsoleBoardScope(string Label, int ProjectCount, string? DisclosureWorkspace,
    IReadOnlyList<string>? Sections = null);
internal sealed record OwnerConsoleScopeCommandResult(string Message, bool Success, bool Changed);

// Shared registry/binding inventory. A failed resolution never supplies a guessed store path.
internal sealed record AttentionWorkspaceEntry(string Name, OrchestratorWorkspace? Workspace,
    Exception? Error = null, bool DeliveryAnswered = false, bool UseLearnedBranch = false);
internal sealed record AttentionWorkspaceCatalog(IReadOnlyList<AttentionWorkspaceEntry> Projects,
    Exception? RegistryError = null)
{
    // Equivalence comes only from resolved canonical state paths; unresolved entries stay separate.
    internal IEnumerable<AttentionWorkspaceEntry[]> CanonicalGroups()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return Projects.Where(entry => entry.Workspace is not null)
            .GroupBy(entry => WorkspaceInvocationBinding.CanonicalPath(entry.Workspace!.SqliteStatePath), comparer)
            .Select(group => group.OrderBy(entry => entry.Name.Equals("home", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal).ToArray())
            .Concat(Projects.Where(entry => entry.Workspace is null).Select(entry => new[] { entry }))
            .OrderBy(group => group[0].Name, StringComparer.Ordinal);
    }

    internal static string[] Aliases(AttentionWorkspaceEntry[] group) => group.Skip(1)
        .Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Where(name => name != group[0].Name)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ThenBy(name => name, StringComparer.Ordinal).ToArray();
}

// Owns BOARD-only reads. Foreign builders and their goal-id caches never escape a refresh.
internal sealed class OwnerConsoleBoardSource(string launchProject, OwnerConsoleProjectScope initialScope,
    Func<IReadOnlyList<OwnerConsoleBoardProject>> catalog, TimeProvider clock,
    OwnerConsoleBoardProject? launchWorkspace = null)
{
    private readonly object _sync = new();
    private OwnerConsoleProjectScope _scope = initialScope;

    internal static OwnerConsoleBoardSource FromRegistry(OrchestratorWorkspace workspace,
        string repositoryRoot, OrchestratorProjectRegistry registry, TimeProvider clock) =>
        new(OwnerConsoleProjectLabel.ForWorkspace(workspace), OwnerConsoleProjectScope.ForLaunch(workspace), () =>
        {
            var inventory = ReadWorkspaceCatalog(repositoryRoot, workspace.TenantName, registry, workspace);
            if (inventory.RegistryError is { } error) throw error;
            return inventory.Projects.Select(entry => entry.Workspace is { } target
                ? new OwnerConsoleBoardProject(entry.Name, target.SqliteStatePath, target.PortfolioStorePath,
                    OrchestratorDirectory: target.OrchestratorDirectory, ConductEventsLogPath: target.ConductEventsLogPath,
                    DeliveryAnswered: entry.DeliveryAnswered, UseLearnedBranch: entry.UseLearnedBranch)
                : new OwnerConsoleBoardProject(entry.Name, "", "", entry.Error!.Message)).ToArray();
        }, clock, new(OwnerConsoleProjectLabel.ForWorkspace(workspace), workspace.SqliteStatePath,
            workspace.PortfolioStorePath, OrchestratorDirectory: workspace.OrchestratorDirectory,
            ConductEventsLogPath: workspace.ConductEventsLogPath));

    internal static AttentionWorkspaceCatalog ReadWorkspaceCatalog(string repositoryRoot, string? tenant,
        OrchestratorProjectRegistry registry, OrchestratorWorkspace? launchWorkspace = null,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<AttentionWorkspaceEntry>();
        cancellationToken.ThrowIfCancellationRequested();
        try { _ = File.GetAttributes(registry.RegistryPath); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Registry.LoadFile's legacy File.Exists probe cannot distinguish access denial.
            // Preserve a supplied launch binding, but never guess a default state location.
            if (launchWorkspace is { IsProjectScoped: false }) entries.Add(new("home", launchWorkspace));
            else entries.Add(new("home", null, ex));
            return new(entries, ex);
        }
        AttentionWorkspaceEntry Describe(string name, Func<OrchestratorWorkspace> resolve,
            bool deliveryAnswered = false, bool useLearnedBranch = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var target = launchWorkspace is not null && name.Equals(
                    OwnerConsoleProjectLabel.ForWorkspace(launchWorkspace), StringComparison.OrdinalIgnoreCase)
                    ? launchWorkspace : resolve();
                return new(name, target, DeliveryAnswered: deliveryAnswered, UseLearnedBranch: useLearnedBranch);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return new(name, null, ex); }
        }
        entries.Add(Describe("home", () => OrchestratorWorkspace.ForDirectory(repositoryRoot, null, tenant, registry)));
        Exception? inventoryError = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var project in registry.ListProjects())
            {
                if (project.Name.Equals("home", StringComparison.OrdinalIgnoreCase))
                {
                    inventoryError = new InvalidOperationException(
                        $"Registered project '{project.Name}' conflicts with the fleet home identity; rename the registration to include it in the inventory.");
                    continue;
                }
                entries.Add(Describe(project.Name, () => OrchestratorWorkspace.ForProject(project.Name,
                    project.RootDirectory, tenantName: tenant, integrationBranch: project.IntegrationBranch,
                    dataRootDirectory: registry.DataRootDirectory), project.DeliveryAnswer is not null,
                    project.UseLearnedIntegrationBranch));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(entries, inventoryError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(entries, ex); }
    }

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
        var rows = new List<OwnerConsoleBoardRow>();
        var resolved = new List<string>();
        OwnerConsoleBoardProject[] projects = [];
        OwnerConsoleBoardProject? FindProject(string name) =>
            projects.FirstOrDefault(project => project.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
            (name.Equals(launchProject, StringComparison.OrdinalIgnoreCase) ? launchWorkspace : null);
        var stores = new Dictionary<string, SqliteOrchestratorStateRepository>(StringComparer.OrdinalIgnoreCase);
        var onboarding = new Dictionary<string, ProjectOnboardingState>(StringComparer.OrdinalIgnoreCase);
        try
        {
            projects = Ordered(catalog()).ToArray();
            foreach (var name in scope.IsAll ? projects.Select(project => project.Name) : scope.Names)
            {
                token.ThrowIfCancellationRequested();
                var project = FindProject(name);
                var isOwn = name.Equals(launchProject, StringComparison.OrdinalIgnoreCase);
                try
                {
                    if (project?.Error is { } error) throw new IOException(error);
                    if (project is not null && !name.Equals("home", StringComparison.OrdinalIgnoreCase))
                    {
                        var goalLanded = await ProjectOnboardingCompletion.HasLandedGoalAsync(project.StatePath, token);
                        onboarding[name] = ProjectOnboardingStateReader.Read(name, project.OrchestratorDirectory ??
                            Path.GetDirectoryName(project.PortfolioPath)!, project.ConductEventsLogPath,
                            project.DeliveryAnswered, project.UseLearnedBranch, goalLanded);
                    }
                    // GetAttributes distinguishes confirmed absence from inaccessible state.
                    if (project is null || !Exists(project.StatePath))
                    {
                        if (onboarding.TryGetValue(name, out var progress) && progress.IsOnboarding)
                        { resolved.Add(name); continue; }
                        if (scope.IsAll && !name.Equals("home", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!isOwn) throw new FileNotFoundException("state database was not found");
                    }
                    resolved.Add(name);
                    if (isOwn)
                        rows.AddRange(own.Board.Select(row => row with { Project = name }));
                    else
                    {
                        var state = SqliteOrchestratorStateRepository.OpenReadOnly(project!.StatePath);
                        var builder = new OwnerConsoleViewModelBuilder(state, new NoQuestions(), new NoLiveness(),
                            new ReadOnlyEpics(project.PortfolioPath), clock);
                        var model = await builder.BuildBoardAsync(token);
                        rows.AddRange(model.Board.Select(row => row with { Project = name, IsForeign = true }));
                        stores[name] = state;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (!resolved.Contains(name, StringComparer.OrdinalIgnoreCase)) resolved.Add(name);
                    rows.Add(Diagnostic(name, ex));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            resolved.Add(launchProject);
            rows.AddRange(own.Board.Select(row => row with { Project = launchProject }));
            rows.Add(Diagnostic("registry", ex));
        }
        token.ThrowIfCancellationRequested();
        var projectCount = resolved.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        IReadOnlyList<string>? sectionOrder = null;
        ImmutableArray<OwnerConsoleBoardRow> sectionRows = [];
        var sectioned = projectCount > 1 || onboarding.Values.Any(state => state.IsOnboarding);
        if (sectioned)
        {
            sectionOrder = resolved.Where(name => name.Equals(launchProject, StringComparison.OrdinalIgnoreCase))
                .Concat(projects.Select(project => project.Name)
                    .Where(name => resolved.Contains(name, StringComparer.OrdinalIgnoreCase)))
                .Concat(resolved).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var sections = new List<OwnerConsoleBoardSection>();
            var now = clock.GetUtcNow();
            foreach (var name in sectionOrder)
            {
                var projectRows = rows.Where(row => row.Project.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (projectRows.FirstOrDefault(row => row.IsDiagnostic) is { } diagnostic)
                {
                    sections.Add(new(name, diagnostic, []));
                    continue;
                }
                try
                {
                    var project = FindProject(name) ?? throw new IOException("state database was not found");
                    DateTimeOffset? landed = null;
                    if (!stores.TryGetValue(name, out var state) && Exists(project.StatePath))
                        state = SqliteOrchestratorStateRepository.OpenReadOnly(project.StatePath);
                    if (state is not null)
                        landed = (await state.ListGoalMetadataAsync(token)).Select(goal => goal.PromotionFact?.PromotedAt).Max();
                    var live = new ConductorLeaseLiveness(project.OrchestratorDirectory ??
                        Path.GetDirectoryName(project.PortfolioPath)!).IsRunning();
                    sections.Add(OwnerConsoleBoardSection.Create(name, projectRows.OrderBy(row => row.SortKey)
                        .ThenBy(row => row.GoalId, StringComparer.Ordinal).ToImmutableArray(), live, landed, now,
                        onboarding.GetValueOrDefault(name)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { sections.Add(new(name, Diagnostic(name, ex), [])); }
            }
            sectionRows = OwnerConsoleBoardSection.Flatten(sections);
        }
        return own with
        {
            Board = sectioned ? sectionRows : rows.OrderBy(row => row.IsDiagnostic).ThenBy(row => row.SortKey)
                .ThenBy(row => row.Project, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.GoalId, StringComparer.Ordinal).ToImmutableArray(),
            BoardScope = new(scope.Label, projectCount,
                resolved.Any(name => !name.Equals(launchProject, StringComparison.OrdinalIgnoreCase)) ? launchProject : null,
                sectionOrder)
        };
    }

    internal static string ForeignRowMessage(OwnerConsoleBoardRow row) =>
        $"{row.GoalPrefix} belongs to {row.Project}. Detail and actions for other projects arrive in a later slice; open it with --project={(row.Project.Equals("home", StringComparison.OrdinalIgnoreCase) ? "default" : row.Project)} console.";

    private static IEnumerable<OwnerConsoleBoardProject> Ordered(IReadOnlyList<OwnerConsoleBoardProject> projects) =>
        projects.DistinctBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(project => project.Name.Equals("home", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static OwnerConsoleBoardRow Diagnostic(string name, Exception error) =>
        new("", "", $"{name}: state read failed: {error.Message.ReplaceLineEndings(" ")}", "", "", null, "", Dimmed: true)
        { Project = name, IsDiagnostic = true };

    private sealed class NoQuestions : IOwnerQuestionSource
    {
        public Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OwnerQuestion>>([]);
    }
    private sealed class NoLiveness : IConductorLiveness { public bool IsRunning() => false; }
    private sealed class ReadOnlyEpics(string path) : IOwnerGoalEpicLookup
    {
        public async Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken)
        {
            try { return (await PortfolioStore.OpenReadOnly(path).GetGoalMembershipAsync(goalId, cancellationToken))?.EpicTitle ?? ""; }
            catch (Exception ex) when (ex is not OperationCanceledException) { return ""; }
        }
    }
}
