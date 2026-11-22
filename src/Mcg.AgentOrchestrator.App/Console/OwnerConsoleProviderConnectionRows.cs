using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleProviderConnectionRows
{
    internal const string BrowseKey = "Up/Down provider";
    internal const string CheckKey = "c check";
    internal const string SignInKey = "s sign in";
    internal const string DeviceCodeKey = "d device code";
    internal const string ReturnKey = "t return";
    internal const string StopWaitingKey = "x stop waiting";
    internal const string ReloadKey = "r reload";
    internal const string ExpandUnusedKey = "Enter expand Not in use";
    internal const string CollapseUnusedKey = "Enter collapse Not in use";
    private const string TabNavigation = "Left/Right tab  Esc/q back";

    internal static IReadOnlyList<string?> HintActions { get; } =
        Array.AsReadOnly<string?>([null, CheckKey, SignInKey, ReturnKey]);

    internal static string FormatUnusedHeaderHint(bool expanded) =>
        $"{(expanded ? CollapseUnusedKey : ExpandUnusedKey)}  {ReloadKey}  {BrowseKey}  {TabNavigation}";

    internal static string FormatKeyHint(string? action, bool deviceCodeAvailable, bool unused = false)
    {
        if (action is null) return $"{ReloadKey}  {TabNavigation}";
        var related = action switch
        {
            ReturnKey => "  " + StopWaitingKey,
            SignInKey when deviceCodeAvailable => "  " + DeviceCodeKey,
            _ => ""
        };
        var check = action == CheckKey ? "" : "  " + CheckKey;
        var collapse = unused ? "  " + CollapseUnusedKey : "";
        return $"{action}{related}{check}{collapse}  {ReloadKey}  {BrowseKey}  {TabNavigation}";
    }

    internal static string ProfileId(string id) => JsonEncodedText.Encode(id).ToString();

    internal static bool HasHostApiKey(ProviderConnectionProfileReport report) => report.Observations.Any(o =>
        o.ExecutionContext == ProviderConnectionContext.Host && o.AuthenticationMode == ProviderConnectionAuthMode.ApiKey);

    internal static bool SharesCredentialSource(ProviderSignInTarget first, ProviderSignInTarget second) =>
        SharesCredentialSource(first.Kind, second.Kind);

    internal static bool SharesCredentialSource(ProviderKind first, ProviderKind second) =>
        first == second ||
        (first is ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexLuna &&
         second is ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexLuna);

    internal static string Summary(OwnerConsoleProviderRowModel row, TimeProvider clock, string action)
    {
        var report = row.Representative.Report;
        var provider = report.Provider.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? ProfileId(report.ProfileId) : report.Provider;
        return Summary(provider, row.SignInEvidence, $" | used by: {string.Join(", ", row.Roles)}", clock, action);
    }

    internal static string UnusedSummary(OwnerConsoleProviderConnection connection,
        ProviderConnectionObservation? evidence, TimeProvider clock, string action) =>
        Summary($"  {ProfileId(connection.Report.ProfileId)}", evidence, "", clock, action);

    private static string Summary(string label, ProviderConnectionObservation? evidence, string usage, TimeProvider clock, string action)
    {
        var signedIn = evidence?.Result switch
        {
            ProviderConnectionResult.SignedIn => "yes",
            ProviderConnectionResult.SignedOut => "no",
            ProviderConnectionResult.Expired => "expired",
            _ => "not checked"
        };
        var confirmed = evidence?.ObservedAt is { } at
            ? $"{TimeZoneInfo.ConvertTime(at, clock.LocalTimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} ({Age(clock.GetUtcNow() - at)})"
            : "not checked";
        return $"{label} | signed in: {signedIn}{usage} | confirmed: {confirmed} | {action}";
    }

    private static string Age(TimeSpan age) => age.TotalMinutes switch
    {
        < 1 => "less than 1 min ago",
        < 60 => FormattableString.Invariant($"{(int)age.TotalMinutes} min ago"),
        < 1440 => FormattableString.Invariant($"{(int)age.TotalHours} hr ago"),
        < 2880 => "1 day ago",
        _ => FormattableString.Invariant($"{(int)age.TotalDays} days ago")
    };

    internal static IEnumerable<string> Details(OwnerConsoleProviderRowModel row, bool canSignIn, bool canReturn)
    {
        yield return $"  Profiles: {string.Join(", ", row.Members.Select(member => ProfileId(member.Report.ProfileId)))}";
        if (row.Members.Count > 1)
            yield return $"  Actions use {ProfileId(row.Representative.Report.ProfileId)}.";

        foreach (var line in Details(row.Representative, row.Members.SelectMany(member => member.Report.Observations),
            row.Key.Kind, row.SignInEvidence, canSignIn, canReturn)) yield return line;
    }

    internal static IEnumerable<string> UnusedDetails(OwnerConsoleProviderConnection connection,
        ProviderConnectionObservation? evidence, bool canSignIn, bool canReturn) =>
        Details(connection, connection.Report.Observations, ProviderKind.Unknown, evidence, canSignIn, canReturn);

    private static IEnumerable<string> Details(OwnerConsoleProviderConnection connection,
        IEnumerable<ProviderConnectionObservation> observations, ProviderKind kind,
        ProviderConnectionObservation? evidence, bool canSignIn, bool canReturn)
    {
        foreach (var sentence in observations.Select(Describe).Distinct())
            yield return $"  {sentence}";

        var hostKey = observations.Any(o => o.ExecutionContext == ProviderConnectionContext.Host &&
            o.AuthenticationMode == ProviderConnectionAuthMode.ApiKey) &&
            (connection.SignIn is null || HasHostApiKey(connection.Report));
        if (hostKey)
            yield return "  Using API key from the host; sign-in would not change what workers use";
        else if (connection.SignIn is null)
            yield return $"  {LaunchCommandExplanation(kind, connection.Report.ProfileId)}";

        var next = canReturn ? "Press t after returning from sign-in." :
            hostKey ? "Review the API key in provider configuration, then press c to check." :
            canSignIn && evidence?.Result is not ProviderConnectionResult.SignedIn
                ? "Press s to open the provider's sign-in." : "Press c to check the connection.";
        yield return $"  Next: {next}";
    }

    private static string LaunchCommandExplanation(ProviderKind kind, string profileId)
    {
        var cli = kind switch
        {
            ProviderKind.OpenAICodexCli => "codex",
            ProviderKind.AnthropicClaudeCli => "claude",
            ProviderKind.XaiGrokCli => "grok",
            _ => null
        };
        var profile = ProfileId(profileId);
        return cli is null
            ? $"The {profile} profile has no native sign-in command. Review its launch command in the worker profile settings."
            : $"The {profile} profile does not launch the provider's own command line. Set its launch command to start with {cli}.";
    }

    internal static string Describe(ProviderConnectionObservation observation)
    {
        var location = observation.ExecutionContext == ProviderConnectionContext.Host ? "on the host" : "in a worker";
        var result = observation.Result switch
        {
            ProviderConnectionResult.SignedIn => "The provider reported a sign-in",
            ProviderConnectionResult.SignedOut => "The provider reported no sign-in",
            ProviderConnectionResult.Expired => "The provider reported expired credentials",
            ProviderConnectionResult.ToolMissing => "The provider command could not be found",
            ProviderConnectionResult.TimedOut => "The status check timed out",
            ProviderConnectionResult.OutputLimited => "The status check produced too much output to read",
            ProviderConnectionResult.Unsupported => "An automatic status check is unavailable",
            ProviderConnectionResult.MaterialPresent when observation.AuthenticationMode == ProviderConnectionAuthMode.ApiKey =>
                "An API key was present",
            ProviderConnectionResult.MaterialPresent => "Sign-in material was present",
            ProviderConnectionResult.MaterialUnavailable => "Sign-in material was unavailable",
            ProviderConnectionResult.WorkerSucceeded => "The provider call succeeded",
            ProviderConnectionResult.AuthenticationFailed => "The provider call could not authenticate",
            ProviderConnectionResult.WorkerFailed => "The provider call failed",
            _ when observation.ObservedAt is not null => "The check could not determine readiness",
            _ => "There is no readiness evidence yet"
        };
        var historical = observation.IsHistorical && observation.Result != ProviderConnectionResult.Unknown ? " (previous run)" : "";
        return $"{result} {location}{historical}.";
    }

    internal static string? ProbeFailureText(OwnerConsoleProviderConnection connection) =>
        ProbeFailure(connection) is { } failure ? Describe(failure) : null;

    internal static OwnerConsoleProviderConnection MergeCheckAfterReload(
        OwnerConsoleProviderConnection listed, OwnerConsoleProviderConnection checkedConnection) => listed with
    {
        Report = listed.Report with
        {
            Observations = listed.Report.Observations.Concat(checkedConnection.Report.Observations)
                .GroupBy(observation => observation.EvidenceSource == ProviderConnectionSource.RecordedInvocation
                    ? ProviderConnectionContext.WorkerUnspecified : observation.ExecutionContext)
                .Select(group => group.MaxBy(observation => observation.ObservedAt)!).ToArray()
        }
    };

    internal static OwnerConsoleProviderConnection RetainHostObservation(
        OwnerConsoleProviderConnection current, OwnerConsoleProviderConnection result)
    {
        if (ProbeFailure(result) is not { } failure || ProbeFailure(current) is not null) return result;
        var previous = current.Report.Observations.FirstOrDefault(o =>
            o.ExecutionContext == ProviderConnectionContext.Host && o.ObservedAt is not null &&
            o.Result != ProviderConnectionResult.Unknown);
        return previous is null ? result : result with
        {
            Report = result.Report with { Observations = result.Report.Observations.Select(o => o == failure ? previous : o).ToArray() }
        };
    }

    private static ProviderConnectionObservation? ProbeFailure(OwnerConsoleProviderConnection connection) =>
        connection.Report.Observations.FirstOrDefault(o => o.ExecutionContext == ProviderConnectionContext.Host &&
            o.Result is ProviderConnectionResult.ToolMissing or ProviderConnectionResult.TimedOut or ProviderConnectionResult.OutputLimited);
}
