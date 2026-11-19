using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleProviderConnectionRows
{
    internal static string ProfileId(string id) => JsonEncodedText.Encode(id).ToString();

    internal static bool HasHostApiKey(ProviderConnectionProfileReport report) => report.Observations.Any(o =>
        o.ExecutionContext == ProviderConnectionContext.Host && o.AuthenticationMode == ProviderConnectionAuthMode.ApiKey);

    internal static bool SharesCredentialSource(ProviderSignInTarget first, ProviderSignInTarget second) =>
        first.Kind == second.Kind ||
        (first.Kind is ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexLuna &&
         second.Kind is ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexLuna);

    internal static IEnumerable<string> Details(OwnerConsoleProviderConnection connection,
        IEnumerable<OwnerConsoleProviderConnection> profiles, DateTimeOffset now)
    {
        var report = connection.Report;
        foreach (var observation in report.Observations)
        {
            var historical = observation.IsHistorical ? " | historical" : "";
            yield return $"  {observation.ExecutionContext} / {observation.EvidenceSource} | {observation.AuthenticationMode} | {observation.Result}{historical}";
            var checkedAt = observation.ObservedAt is { } at
                ? FormattableString.Invariant($"{at.UtcDateTime:u} ({observation.AtReadTime(now).AgeSeconds:0}s ago)")
                : "unknown";
            yield return $"    Last checked: {checkedAt}";
            yield return $"    Next: {observation.NextAction}";
        }

        if (connection.SignIn is { } target)
        {
            var shared = profiles.Where(p => p.SignIn is { } other && SharesCredentialSource(target, other))
                .Select(p => ProfileId(p.Report.ProfileId)).ToArray();
            if (shared.Length > 1) yield return $"  Shared native sign-in: {string.Join(", ", shared)}";
        }
        if (HasHostApiKey(report))
            yield return "  Manage the API key in the provider configuration, then check connection.";
        else if (connection.SignIn is null)
            yield return "  Native sign-in is unsupported for this profile.";
        else if (report.Observations.Any(o => o.ExecutionContext == ProviderConnectionContext.WorkerCredentialMaterial &&
                     o.AuthenticationMode == ProviderConnectionAuthMode.ApiKey))
            yield return "  Worker preflight uses an API key. Manage that key in the provider configuration.";
    }
}
