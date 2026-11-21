using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliModelsCommand
{
    public const string Usage = "Usage: models [refresh|declare <provider>/<model> [--profile <name>]|undeclare <provider>/<model> [--profile <name>]]";

    public static void Execute(IReadOnlyList<string> args, CliExecutionContext context)
    {
        var path = context.Workspace.ModelInventoryPath;
        if (args.Count == 1)
        {
            Print(ModelInventoryStore.Load(path), context.Agents);
            return;
        }

        var declarationsPath = context.Workspace.ModelDeclarationsPath;
        var operation = args[1].ToLowerInvariant();
        if (operation == "refresh" && args.Count == 2)
        {
            using var client = ProviderHttpClientFactory.CreateAnthropicClient();
            var sources = new ModelInventorySources(context.Workspace.ExecutionDirectory, client);
            var inventory = ModelInventoryRefresher.RefreshAsync(path, context.WorkerProfiles,
                ModelInventoryStore.LoadDeclarations(declarationsPath), sources.ListAsync, sources.ProbeAsync)
                .GetAwaiter().GetResult();
            Print(inventory, context.Agents);
            return;
        }

        if (operation is not ("declare" or "undeclare") || args.Count < 3 ||
            !InventoryModelAssignment.TrySplitModel(args[2], out var provider, out var model))
            throw new ArgumentException(Usage);

        var options = ReadSelectionOptions(args, 3, "--profile");
        var profile = options.GetValueOrDefault("--profile");
        if (operation == "declare")
        {
            var match = ModelInventoryRefresher.SupportedProfiles(context.WorkerProfiles).FirstOrDefault(binding =>
                ModelInventory.Same(binding.Provider, provider) &&
                (profile is null || ModelInventory.Same(binding.Profile.Name, profile)));
            if (match.Profile is null)
                throw new ArgumentException($"No configured inventory profile matches '{provider}'{(profile is null ? "" : $" on '{profile}'")}.");
            provider = match.Provider;
            if (profile is not null) profile = match.Profile.Name;
        }

        ModelInventoryStore.UpdateDeclarations(declarationsPath, declarations =>
        {
            var remaining = declarations.Where(entry =>
                !(ModelInventory.Same(entry.Provider, provider) && ModelInventory.Same(entry.Model, model) &&
                  string.Equals(entry.Profile, profile, StringComparison.OrdinalIgnoreCase)));
            return (operation == "declare" ? remaining.Append(new(provider, model, profile)) : remaining).ToArray();
        });
        Console.WriteLine($"Model declaration {operation} recorded for {provider}/{model}. Run models refresh to update the inventory.");
    }

    internal static Dictionary<string, string> ReadSelectionOptions(
        IReadOnlyList<string> args, int start, params string[] allowed)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = start; index < args.Count; index++)
        {
            var parts = args[index].Split('=', 2);
            var flag = parts[0];
            if (!allowed.Contains(flag, StringComparer.OrdinalIgnoreCase))
                throw new InventoryAssignmentException(InventoryAssignmentHoldCode.ConflictingSelection,
                    $"Unexpected argument '{args[index]}'. Select either a roster agent or an inventory model.");
            var value = parts.Length == 2 ? parts[1] : ++index < args.Count ? args[index] : "";
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) ||
                !options.TryAdd(flag, value))
                throw new InventoryAssignmentException(InventoryAssignmentHoldCode.InvalidSelection,
                    $"Supply one value for {flag}.");
        }
        return options;
    }

    public static void RequireRosterMembership(
        string inventoryPath, AgentDefinition agent, IReadOnlyList<string> args)
    {
        if (CliCommandHandlers.HasCliConfirmation(args, "--allow-unlisted")) return;
        var inventory = ModelInventoryStore.Load(inventoryPath);
        string?[] models = [agent.Model.ModelName, CliCommandHandlers.GetFlagValue(args, "--complex-model"),
            CliCommandHandlers.GetFlagValue(args, "--subscription-model")];
        foreach (var model in models.OfType<string>())
        {
            if (!inventory.Find(agent.Model.ProviderName, model).Any())
                throw new InventoryAssignmentException(InventoryAssignmentHoldCode.ModelNotInInventory,
                    $"AGENT_ADD_HOLD code=ModelNotInInventory model='{agent.Model.ProviderName}/{model}'. Run models refresh or use --allow-unlisted.");
        }
    }

    private static void Print(ModelInventory inventory, IReadOnlyList<AgentDefinition> agents)
    {
        foreach (var entry in inventory.Entries)
        {
            var seats = agents.Where(agent =>
                    Matches(agent.Model.ProviderName, agent.Model.ModelName, entry) ||
                    agent.Subscription?.ModelAlias is { } alias && Matches(agent.Model.ProviderName, alias, entry) ||
                    agent.ComplexModel is { } complex && Matches(complex.ProviderName, complex.ModelName, entry))
                .Select(agent => $"{agent.Role}/{agent.Id.Value}");
            Console.WriteLine($"{entry.Provider}/{entry.Model} profile={entry.Profile} source={entry.Source} discovered={entry.DiscoveredAt:O} " +
                $"probe={entry.ProbeStatus} seats=[{string.Join(", ", seats)}]" +
                (entry.SourceTimestamp is { } timestamp ? $" source-timestamp={timestamp:O}" : "") +
                (entry.ProbeDetail is null ? "" : $" detail={entry.ProbeDetail}"));
        }

        foreach (var source in inventory.Sources)
            Console.WriteLine($"{source.Provider} profile={source.Profile} source={source.Source} status={source.Status}" +
                (source.Detail is null ? "" : $" detail={source.Detail}"));

        if (inventory.Entries.Count == 0)
            Console.WriteLine("No models in the inventory. Run models refresh to discover models.");
    }

    private static bool Matches(string provider, string model, ModelInventoryEntry entry) =>
        ModelInventory.Same(entry.Provider, provider) && ModelInventory.Same(entry.Model, model);
}
