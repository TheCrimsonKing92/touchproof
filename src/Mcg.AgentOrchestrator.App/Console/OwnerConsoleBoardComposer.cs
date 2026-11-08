using System.Collections.Immutable;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleBoardComposer
{
    internal static async Task<OwnerConsoleViewModel> ComposeAsync(string launchProject,
        OwnerConsoleProjectScope scope, Func<IReadOnlyList<OwnerConsoleBoardProject>> catalog,
        TimeProvider clock, OwnerConsoleBoardProject? launchWorkspace, OwnerConsoleViewModel own,
        CancellationToken token)
    {
        var rows = new List<OwnerConsoleBoardRow>();
        var resolved = new List<string>();
        OwnerConsoleBoardProject[] projects = [];
        var stores = new Dictionary<string, SqliteOrchestratorStateRepository>(StringComparer.OrdinalIgnoreCase);
        var onboarding = new Dictionary<string, ProjectOnboardingState>(StringComparer.OrdinalIgnoreCase);
        try
        {
            projects = OwnerConsoleBoardSource.Ordered(catalog()).ToArray();
            await ReadProjectRowsAsync(launchProject, scope, clock, launchWorkspace, own, token,
                rows, resolved, projects, stores, onboarding);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            resolved.Add(launchProject);
            rows.AddRange(own.Board.Select(row => row with { Project = launchProject }));
            rows.Add(OwnerConsoleBoardSource.Diagnostic("registry", ex));
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
            sectionRows = await ComposeSectionsAsync(launchProject, clock, launchWorkspace, token,
                rows, projects, stores, onboarding, sectionOrder);
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

    private static async Task ReadProjectRowsAsync(string launchProject, OwnerConsoleProjectScope scope,
        TimeProvider clock, OwnerConsoleBoardProject? launchWorkspace, OwnerConsoleViewModel own,
        CancellationToken token, List<OwnerConsoleBoardRow> rows, List<string> resolved,
        OwnerConsoleBoardProject[] projects, Dictionary<string, SqliteOrchestratorStateRepository> stores,
        Dictionary<string, ProjectOnboardingState> onboarding)
    {
        foreach (var name in scope.IsAll ? projects.Select(project => project.Name) : scope.Names)
        {
            token.ThrowIfCancellationRequested();
            var project = FindProject(projects, name, launchProject, launchWorkspace);
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
                if (project is null || !OwnerConsoleBoardSource.Exists(project.StatePath))
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
                rows.Add(OwnerConsoleBoardSource.Diagnostic(name, ex));
            }
        }
    }

    private static async Task<ImmutableArray<OwnerConsoleBoardRow>> ComposeSectionsAsync(string launchProject,
        TimeProvider clock, OwnerConsoleBoardProject? launchWorkspace, CancellationToken token,
        List<OwnerConsoleBoardRow> rows, OwnerConsoleBoardProject[] projects,
        Dictionary<string, SqliteOrchestratorStateRepository> stores,
        Dictionary<string, ProjectOnboardingState> onboarding, IReadOnlyList<string> sectionOrder)
    {
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
                var project = FindProject(projects, name, launchProject, launchWorkspace) ?? throw new IOException("state database was not found");
                DateTimeOffset? landed = null;
                if (!stores.TryGetValue(name, out var state) && OwnerConsoleBoardSource.Exists(project.StatePath))
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
            { sections.Add(new(name, OwnerConsoleBoardSource.Diagnostic(name, ex), [])); }
        }
        return OwnerConsoleBoardSection.Flatten(sections);
    }

    private static OwnerConsoleBoardProject? FindProject(OwnerConsoleBoardProject[] projects, string name,
        string launchProject, OwnerConsoleBoardProject? launchWorkspace) =>
        projects.FirstOrDefault(project => project.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
        (name.Equals(launchProject, StringComparison.OrdinalIgnoreCase) ? launchWorkspace : null);

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
