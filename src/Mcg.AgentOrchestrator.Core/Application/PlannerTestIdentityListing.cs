namespace Mcg.AgentOrchestrator.Core;

// Both strings are opaque runner values. Only the provider knows their encoding.
public sealed record RunnerTestIdentity(string Identity, string Name);

// Per-kernel configuration for the two complementary Planner selector authorities.
internal sealed class PlannerEvidenceSources
{
    internal Func<Goal, TaskSpec, IReadOnlyList<DeclaredGateCheck>>? DeclaredChecks { get; private set; }
    internal Func<Goal, TaskSpec, IReadOnlyList<RunnerTestIdentity>?>? TestIdentities { get; private set; }

    internal void SetDeclaredChecks(Func<Goal, TaskSpec, IReadOnlyList<DeclaredGateCheck>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        DeclaredChecks = source;
    }

    internal void SetTestIdentities(Func<Goal, TaskSpec, IReadOnlyList<RunnerTestIdentity>?> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        TestIdentities = source;
    }

    internal string? Refusal(IReadOnlyList<string> selectors, Goal goal, TaskSpec task) =>
        PlannerCommandSelectorValidation.Refusal(selectors, goal, task, DeclaredChecks,
            PlannerTestIdentityListing.Snapshot(selectors, goal, task, TestIdentities));
}

internal static class PlannerTestIdentityListing
{
    private static Func<Goal, TaskSpec, IReadOnlyList<RunnerTestIdentity>?>? defaultSource;

    internal static void InstallDefaultSource(Func<Goal, TaskSpec, IReadOnlyList<RunnerTestIdentity>?> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Interlocked.CompareExchange(ref defaultSource, source, null) is not null)
            throw new InvalidOperationException("The test identity listing source is already installed.");
    }

    // A snapshot belongs to one planning round, and must be acquired outside kernel locks.
    internal static IReadOnlyList<RunnerTestIdentity>? Snapshot(IEnumerable<string> selectors,
        Goal goal, TaskSpec task, Func<Goal, TaskSpec, IReadOnlyList<RunnerTestIdentity>?>? source)
    {
        if (!PlannerCommandSelectorValidation.NeedsListing(selectors)) return null;
        try
        {
            var listing = (source ?? Volatile.Read(ref defaultSource))?.Invoke(goal, task);
            return listing is { Count: > 0 } ? listing.ToArray() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
