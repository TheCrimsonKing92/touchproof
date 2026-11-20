using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe. Each case uses its own roster and inventory files.
public sealed class AgentAddInventoryMembershipTests
{
    [Fact]
    public void AgentAddRequiresMembershipUnlessExplicitlyAllowedAndPreservesLegacySeats()
    {
        const string roster = """
            {"Agents":[
              {"Id":{"Value":"legacy-dev"},"Name":"Existing Developer","Role":"Developer","Status":"Available",
               "Model":{"ProviderName":"Ollama","ModelName":"local-dev","Capabilities":"Text","SubscriptionMode":"LocalBridge"}},
              {"Id":{"Value":"legacy-tester"},"Name":"Existing Tester","Role":"Tester","Status":"Busy",
               "Model":{"ProviderName":"Ollama","ModelName":"local-test","Capabilities":"Text","SubscriptionMode":"LocalBridge"}},
              {"Id":{"Value":"legacy-review"},"Name":"Existing Reviewer","Role":"Reviewer","Status":"Offline",
               "Model":{"ProviderName":"Ollama","ModelName":"local-review","Capabilities":"Text","SubscriptionMode":"LocalBridge"}}
            ]}
            """;
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        File.WriteAllText(workspace.AgentCatalogPath, roster);
        var kernel = new AgentOrchestratorKernel();
        var profiles = WorkerProfileCatalog.Default();
        var agents = AgentCatalogStore.Load(workspace.AgentCatalogPath).Agents;
        Goal? currentGoal = null;
        Assert.Collection(agents,
            agent => AssertSeat(agent, "Existing Developer", AgentRole.Developer, "local-dev", AgentStatus.Available),
            agent => AssertSeat(agent, "Existing Tester", AgentRole.Tester, "local-test", AgentStatus.Busy),
            agent => AssertSeat(agent, "Existing Reviewer", AgentRole.Reviewer, "local-review", AgentStatus.Offline));

        string[] add = ["agent-add", "Developer", "Anthropic", "claude-haiku-5-5", "New Developer"];
        var hold = Assert.Throws<InventoryAssignmentException>(() => Execute(add));
        Assert.Equal(InventoryAssignmentHoldCode.ModelNotInInventory, hold.Code);
        Assert.Equal(roster, File.ReadAllText(workspace.AgentCatalogPath));
        Assert.True(agents.Count == 3);
        Execute([.. add, "--allow-unlisted"]);
        var saved = AgentCatalogStore.Load(workspace.AgentCatalogPath).Agents;
        Assert.Contains(saved, agent => agent.Name == "New Developer" && agent.Model.ModelName == "claude-haiku-5-5");
        Assert.Equal(agents.Select(agent => agent.Id), saved.Select(agent => agent.Id));
        Assert.False(File.Exists(workspace.ModelInventoryPath));

        ModelInventoryStore.Save(workspace.ModelInventoryPath, new([
            new("Anthropic", "claude-fixture", "claude-cli", ModelDiscoverySource.DeclarationProbe,
                DateTimeOffset.UnixEpoch, ProbeStatus: ModelProbeStatus.Failed)], []));
        Execute(["agent-add", "Developer", "anthropic", "CLAUDE-FIXTURE", "Listed Developer"]);
        Assert.Contains(AgentCatalogStore.Load(workspace.AgentCatalogPath).Agents, agent => agent.Name == "Listed Developer");

        bool Execute(string[] args) => CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.NormalizeArgs(args), kernel, workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal);
    }

    private static void AssertSeat(AgentDefinition seat, string name, AgentRole role, string model, AgentStatus status) =>
        Assert.Equal((name, role, model, status), (seat.Name, seat.Role, seat.Model.ModelName, seat.Status));
}
