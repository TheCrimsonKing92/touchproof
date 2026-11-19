using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleProviderConnectionRows;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleProviderRowModel(
    (ProviderKind Kind, string? ProfileId) Key,
    IReadOnlyList<OwnerConsoleProviderConnection> Members,
    IReadOnlyList<AgentRole> Roles,
    OwnerConsoleProviderConnection Representative,
    ProviderConnectionObservation? SignInEvidence)
{
    internal static (OwnerConsoleProviderRowModel[] Rows, string[] NotInUse) Build(
        IReadOnlyList<OwnerConsoleProviderConnection> connections, IReadOnlyList<AgentDefinition> roster)
    {
        var listedNames = connections.Select(connection => LunaLaneNames.NormalizeProfileName(connection.Report.ProfileId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var routes = roster.Where(agent => agent.Status != AgentStatus.Offline)
            .Select(agent => (Agent: agent, Subscription: SubscriptionRoute(agent, listedNames))).ToArray();
        var providers = WorkerProviderCatalog.Default();
        var unused = new List<string>();
        var inUse = new List<(OwnerConsoleProviderConnection Connection, ProviderKind Kind, AgentRole[] Roles)>();
        foreach (var connection in connections)
        {
            var profileName = LunaLaneNames.NormalizeProfileName(connection.Report.ProfileId);
            var roles = routes.Where(route => route.Subscription is { } subscription
                    ? StringComparer.OrdinalIgnoreCase.Equals(subscription, profileName)
                    : AgentExecutionPolicies.AllowsApi(route.Agent.ExecutionPolicy) &&
                      StringComparer.OrdinalIgnoreCase.Equals(route.Agent.Model.ProviderName, connection.Report.Provider))
                .Select(route => route.Agent.Role).Distinct().ToArray();
            if (roles.Length == 0)
            {
                unused.Add(connection.Report.ProfileId);
                continue;
            }

            var kind = connection.SignIn?.Kind ?? providers.ResolveProfile(profileName).Identity.Kind;
            inUse.Add((connection, kind, roles));
        }

        var rows = inUse.GroupBy(item => GroupKey(item.Kind, item.Connection.Report.ProfileId)).Select(group =>
        {
            var members = group.Select(item => item.Connection).ToArray();
            var representative = members.FirstOrDefault(member => member.SignIn is not null && !HasHostApiKey(member.Report))
                ?? members[0];
            return new OwnerConsoleProviderRowModel(group.Key, members,
                group.SelectMany(item => item.Roles).Distinct().Order().ToArray(), representative,
                SelectSignInEvidence(members.SelectMany(member => member.Report.Observations)));
        }).ToArray();
        return (rows, unused.ToArray());
    }

    private static string? SubscriptionRoute(AgentDefinition agent, HashSet<string> listedNames) =>
        AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy) &&
        LunaLaneNames.NormalizeProfileName(agent.Subscription?.WorkerProfileName) is { } profile &&
        listedNames.Contains(profile) ? profile : null;

    private static (ProviderKind Kind, string? ProfileId) GroupKey(ProviderKind kind, string profileId) =>
        kind == ProviderKind.Unknown ? (kind, profileId) :
        (SharesCredentialSource(kind, ProviderKind.OpenAICodexCli) ? ProviderKind.OpenAICodexCli : kind, null);

    internal static ProviderConnectionObservation? SelectSignInEvidence(IEnumerable<ProviderConnectionObservation> observations) =>
        observations.Where(observation => !observation.IsHistorical &&
                observation.ExecutionContext == ProviderConnectionContext.Host &&
                observation.AuthenticationMode != ProviderConnectionAuthMode.ApiKey &&
                observation.Result is ProviderConnectionResult.SignedIn or ProviderConnectionResult.SignedOut or ProviderConnectionResult.Expired)
            .OrderByDescending(observation => observation.ObservedAt)
            .ThenBy(observation => observation.Result switch
            {
                ProviderConnectionResult.Expired => 0,
                ProviderConnectionResult.SignedOut => 1,
                _ => 2
            })
            .FirstOrDefault();
}
