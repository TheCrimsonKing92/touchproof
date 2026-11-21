using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

internal enum InventoryAssignmentHoldCode
{
    ModelNotInInventory,
    AmbiguousProfile,
    ModelIneligible,
    UnsupportedEffort,
    ConflictingSelection,
    InvalidSelection
}

internal sealed class InventoryAssignmentException(InventoryAssignmentHoldCode code, string message)
    : ArgumentException(message)
{
    public InventoryAssignmentHoldCode Code { get; } = code;
}

internal static class InventoryModelAssignmentResolver
{
    public static AgentDefinition Resolve(
        string selection, string? profile, string? effort, AgentRole role,
        ModelInventory inventory, WorkerProfileCatalog profiles)
    {
        if (!InventoryModelAssignment.TrySplitModel(selection, out var provider, out var model))
            throw new InventoryAssignmentException(InventoryAssignmentHoldCode.InvalidSelection,
                $"Expected provider/model, received '{selection}'.");
        var candidates = inventory.Find(provider, model)
            .Where(entry => profile is null || ModelInventory.Same(entry.Profile, profile)).ToArray();
        if (candidates.Length == 0)
            throw new InventoryAssignmentException(InventoryAssignmentHoldCode.ModelNotInInventory,
                $"'{selection}'{(profile is null ? "" : $" on '{profile}'")} is absent from the model inventory.");
        if (candidates.Length > 1)
            throw new InventoryAssignmentException(InventoryAssignmentHoldCode.AmbiguousProfile,
                $"'{selection}' matches profiles {string.Join(", ", candidates.Select(entry => entry.Profile))}. Specify --profile.");
        var entry = candidates[0];
        var worker = RunnableProfile(entry, profiles);
        if (worker is null)
            throw new InventoryAssignmentException(InventoryAssignmentHoldCode.ModelIneligible,
                $"'{entry.Provider}/{entry.Model}' on '{entry.Profile}' has no current discovery or probe evidence for a runnable profile.");

        effort ??= AgentCatalog.DefaultSubscriptionReasoningEffort(entry.Provider, entry.Model);
        if (!SupportsEffort(entry.Provider, worker, effort))
            throw new InventoryAssignmentException(InventoryAssignmentHoldCode.UnsupportedEffort,
                $"Effort '{effort}' is unsupported on '{entry.Profile}'.");

        return new InventoryModelAssignment(entry.Provider, entry.Model, entry.Profile, effort, role).ToDispatchAgent();
    }

    public static bool SupportsEffort(string provider, WorkerProfile profile, string? effort) =>
        effort is null || (WorkerProfileDiagnostics.UsesSubscriptionReasoningPlaceholder(profile.CommandTemplate) &&
            (ModelInventory.Same(provider, "Anthropic")
                ? ClaudeCliEffortPolicy.IsSupported(effort)
                : effort is "none" or "minimal" or "low" or "medium" or "high" or "xhigh"));

    public static WorkerProfile? RunnableProfile(ModelInventoryEntry entry, WorkerProfileCatalog profiles) =>
        entry.Eligible
            ? ModelInventoryRefresher.SupportedProfiles(profiles)
                .Where(binding => ModelInventory.Same(binding.Profile.Name, entry.Profile) &&
                    ModelInventory.Same(binding.Provider, entry.Provider))
                .Select(binding => binding.Profile)
                .FirstOrDefault()
            : null;
}
