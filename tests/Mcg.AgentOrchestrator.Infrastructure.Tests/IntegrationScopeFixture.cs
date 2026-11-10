using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Shared arrangement only: all production calls and assertions remain in the owning tests.
internal sealed class IntegrationScopeFixture : IDisposable
{
    internal ConductorConflictIntegrationRecordTests.Fixture Base { get; } = new();
    internal Goal Goal => Base.Goal;
    internal AgentOrchestratorKernel Kernel => Base.Kernel;
    internal string RoundId
    {
        get
        {
            using var document = JsonDocument.Parse(Base.Entry.Detail!);
            return document.RootElement.GetProperty("RoundId").GetString()!;
        }
    }
    internal SqliteOperatorIntentStore Store { get; }
    internal OperatorIntentCoordinator Intents { get; }
    internal ConductorConflictIntegration Owner { get; }

    internal IntegrationScopeFixture()
    {
        Assert.True(Base.Coordinator.TryRecord(Goal).Started);
        var workspace = OrchestratorWorkspace.ForDirectory(Base.GitFixture.Repository);
        Store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        Owner = Reload();
        Intents = Coordinator();
    }

    internal ConductorConflictIntegration Reload(AgentOrchestratorKernel? kernel = null) => new(kernel ?? Kernel, Base.GitFixture.Repository, "main", Base.Retry,
        goal => new RatchetConflictDispatchIntegration(Kernel, Base.GitFixture.Repository, "main",
            Path.Combine(Base.GitFixture.Repository, "events.log")).IntegrateMainBeforeDeveloperDispatch(goal),
        _ => new(true, 0, "fixture build passed"));

    internal OperatorIntentCoordinator Coordinator(AgentOrchestratorKernel? kernel = null)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(Base.GitFixture.Repository);
        return new(Store, decisions: CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
            goalStateVersionResolver: _ => 0) { IntegrationScopeOwner = _ => Reload(kernel) };
    }

    internal OperatorIntentRecord Intent(IntegrationScopeAction action = IntegrationScopeAction.Approve,
        string[]? paths = null, string? approvalId = null)
    {
        var payload = new IntegrationScopeAuthorization(Goal.Id.Value, RoundId, Base.GitFixture.OriginalHead,
            Base.GitFixture.MainHead, action, paths ?? (action == IntegrationScopeAction.Approve ? ["src/Other.cs"] : []),
            "Preserve adjacent behavior", approvalId);
        var id = Guid.NewGuid().ToString("N");
        return new(id, id, OperatorIntentVerbs.IntegrationScope, Goal.Id.Value, null,
            JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "operator", "cli", "local-process", DateTimeOffset.UtcNow);
    }

    internal async Task<OperatorIntentRecord> Apply(OperatorIntentRecord intent)
    {
        var stored = await Store.EnqueueAsync(intent);
        Intents.ExecutePending(Kernel, Goal);
        Intents.CompletePersisted([Goal.Id]);
        return (await Store.GetAsync(stored.Id))!;
    }

    public void Dispose() => Base.Dispose();
}
