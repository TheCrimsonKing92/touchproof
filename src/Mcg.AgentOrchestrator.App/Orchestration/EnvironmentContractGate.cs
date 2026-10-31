using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Owns prerequisite admission and its self-clearing operator hold, independently of worker recovery.</summary>
internal sealed class EnvironmentContractGate
{
    private readonly AgentOrchestratorKernel _kernel;
    private readonly string _project;
    private readonly string _directory;
    private readonly string? _projectHomeDirectory;
    private readonly Func<EnvironmentContract?> _loadContract;
    private readonly Func<bool, ConductorChildEnvironment> _environment;
    private readonly Func<Goal, ConductorAutonomyPolicy?, IReadOnlyList<EnvironmentProbeContext>>? _workerEnvironments;
    private readonly EnvironmentProbeCache _cache;
    private readonly Action<string, string?, string> _writeEvent;
    private readonly TimeProvider _time;
    private readonly string _buildIdentity;

    internal EnvironmentContractGate(AgentOrchestratorKernel kernel, string project, string directory,
        Func<EnvironmentContract?> loadContract, IEnvironmentProbeRunner? runner = null,
        Func<bool, ConductorChildEnvironment>? environment = null, Action<string, string?, string>? writeEvent = null,
        TimeProvider? timeProvider = null,
        Func<Goal, ConductorAutonomyPolicy?, IReadOnlyList<EnvironmentProbeContext>>? workerEnvironments = null,
        string? projectHomeDirectory = null)
    {
        _kernel = kernel;
        _project = project;
        _directory = directory;
        _projectHomeDirectory = projectHomeDirectory;
        _loadContract = loadContract;
        _environment = environment ?? CaptureEnvironment;
        _workerEnvironments = workerEnvironments;
        _time = timeProvider ?? TimeProvider.System;
        _cache = new(runner ?? new ProcessEnvironmentProbeRunner(), _time);
        _writeEvent = writeEvent ?? ((_, _, _) => { });
        _buildIdentity = typeof(ConductorDriver).Module.ModuleVersionId.ToString("N");
    }

    internal static EnvironmentContractGate ForWorkspace(AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents, WorkerProfileCatalog profiles)
    {
        var writer = new ConductEventLogWriter(workspace.ConductEventsLogPath);
        return new(kernel, workspace.ProjectHomeDirectoryOrNull ?? workspace.ExecutionDirectory, workspace.ExecutionDirectory,
            () => LoadWorkspaceContract(workspace),
            writeEvent: (kind, goal, detail) => writer.AppendRequired(kind, goal, detail),
            workerEnvironments: (goal, policy) => WorkerEnvironmentProbeContexts.Capture(workspace, goal, agents, profiles, policy),
            projectHomeDirectory: workspace.ProjectHomeDirectoryOrNull);
    }

    private static EnvironmentContract? LoadWorkspaceContract(OrchestratorWorkspace workspace)
    {
        var contract = EnvironmentContract.Load(workspace.ExecutionDirectory, workspace.ProjectHomeDirectoryOrNull);
        // Gate hosts resolve this installed binary before scrubbing their child environment too.
        var crosswalk = Path.Combine(OrchestratorDataRoot.Resolve().RootDirectory, "tools", "crosswalk", "crosswalk.exe");
        return contract is null ? null : contract with
        { Tools = contract.Tools.Select(tool => tool.Command == "crosswalk" ? tool with { Command = crosswalk } : tool).ToArray() };
    }

    internal void ProbeAtStart(IEnumerable<Goal> goals)
    {
        var members = goals.Where(goal => goal.Status is not (GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded or GoalStatus.Parked)).ToArray();
        var (results, fingerprint) = Probe(false, members.FirstOrDefault());
        foreach (var pass in results.Where(result => result.Passed))
            Emit("environment-probe-pass", null, pass.Tool.Name, fingerprint, "pass");
        foreach (var goal in members)
        {
            var observation = goal == members[0] ? (results, fingerprint) : Probe(false, goal);
            Observe(goal, observation.Item1, observation.Item2);
        }
    }

    internal ConductorAdvanceResult? Check(Goal goal, ConductorAutonomyPolicy policy, GoalLifecycleState state, bool forGate = false)
    {
        var (results, fingerprint) = Probe(forGate, goal, policy);
        var hold = Observe(goal, results, fingerprint);
        if (hold is null) return null;
        var reason = $"Environment hold: {hold.Tool}; probe={hold.Command}; rule={hold.Rule}; exit={hold.ExitCode?.ToString() ?? hold.Outcome}; output={hold.OutputTail}";
        var decision = new EnvironmentContractDecision(hold.Project, hold.Tool, fingerprint, reason);
        return new(goal.Id.Value, goal.Id.Value[..8], policy.Name,
            new ConductorAdvanceOutcome.Held(state, reason, hold.Identity)
            { Owner = ConductorHoldOwner.Environment, Decision = decision.ToRecord() });
    }

    internal IReadOnlyDictionary<string, ConductorAdvanceResult>? CheckMany(IEnumerable<Goal> goals, ConductorAutonomyPolicy policy)
    {
        var members = goals.ToArray();
        var holds = members.Select(goal => Check(goal, policy, GoalLifecycleState.Verifying, true)).OfType<ConductorAdvanceResult>().ToArray();
        if (holds.Length == 0) return null;
        // A group cannot launch when any member's project is held.
        return members.ToDictionary(goal => goal.Id.Value, goal => holds.FirstOrDefault(hold => hold.GoalId == goal.Id.Value)
            ?? new ConductorAdvanceResult(goal.Id.Value, goal.Id.Value[..8], policy.Name,
                new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verifying, "Environment hold prevents grouped gate start.")
                { Owner = ConductorHoldOwner.Environment,
                    Decision = new EnvironmentContractDecision(_project, "group", "group", "Environment hold prevents grouped gate start.").ToRecord() }));
    }

    private (IReadOnlyList<EnvironmentProbeResult> Results, string Fingerprint) Probe(bool forGate,
        Goal? goal = null, ConductorAutonomyPolicy? policy = null)
    {
        try
        {
            var contract = _loadContract() ?? new EnvironmentContract(1, []);
            contract.Validate();
            var contexts = !forGate && goal is not null && _workerEnvironments is not null
                ? _workerEnvironments(goal, policy)
                : [new EnvironmentProbeContext(_directory, _environment(forGate))];
            (IReadOnlyList<EnvironmentProbeResult>, string) last = ([], "empty-contract");
            foreach (var context in contexts)
            {
                var fingerprint = EnvironmentFingerprint.Create(_buildIdentity, contract, context.Environment, context.WorkingDirectory);
                var results = _cache.Probe(contract, fingerprint, context.Environment, context.WorkingDirectory);
                last = (results, fingerprint);
                if (results.Any(result => !result.Passed)) return last;
            }
            return last;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return ([new(new("contract", "manifest", []), false, null, ex.Message, "invalid-contract")], "invalid-contract");
        }
    }

    private EnvironmentHold? Observe(Goal goal, IReadOnlyList<EnvironmentProbeResult> results, string fingerprint)
    {
        if (results.FirstOrDefault(result => !result.Passed) is { } failure)
        {
            var hold = new EnvironmentHold(_project, failure.Tool.Name, failure.Tool.ProbeCommand, failure.Tool.Rule,
                failure.ExitCode, failure.OutputTail, fingerprint, failure.Outcome);
            var observation = _kernel.ObserveGoalHold(goal.Id, EnvironmentHold.State, hold.Format(), _time.GetUtcNow(), TimeSpan.MaxValue, hold.Identity);
            if (observation.StateChanged)
            {
                _kernel.RecordGoalPolicyDecision(goal.Id, "environment-hold " + hold.Format());
                Emit("environment-hold", goal.Id.Value, hold.Tool, fingerprint, failure.Outcome);
            }
            return hold;
        }
        if (EnvironmentHold.TryRead(goal) is { } previous && _kernel.ClearGoalHold(goal.Id))
        {
            _kernel.RecordGoalPolicyDecision(goal.Id, $"environment-hold-cleared tool={previous.Tool} fingerprint={fingerprint} outcome=pass");
            Emit("environment-hold-cleared", goal.Id.Value, previous.Tool, fingerprint, "pass");
        }
        return null;
    }

    private ConductorChildEnvironment CaptureEnvironment(bool forGate)
    {
        var environment = ConductorChildEnvironment.Capture();
        if (!forGate) return environment;
        var variables = environment.Variables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(variables, _directory,
            toolHomes: AcceptanceGateEngineSettings.Load(_directory, _projectHomeDirectory).DeclaredToolHomes);
        return new(variables);
    }

    private void Emit(string kind, string? goal, string tool, string fingerprint, string outcome) =>
        _writeEvent(kind, goal, JsonSerializer.Serialize(new { project = _project, tool, fingerprint, outcome }));
}
