namespace Mcg.AgentOrchestrator.Core;

public enum ProviderConnectionResult
{
    Unknown, SignedIn, SignedOut, Expired, ToolMissing, TimedOut, Unsupported,
    MaterialPresent, MaterialUnavailable, WorkerSucceeded, AuthenticationFailed, WorkerFailed, OutputLimited
}

public enum ProviderConnectionContext { Host, WorkerCredentialMaterial, WorkerSandbox, WorkerUnspecified }
public enum ProviderConnectionSource { None, CodexLoginStatus, ClaudeAuthStatus, ClaudePreflight, RecordedInvocation }
public enum ProviderConnectionAuthMode { Unknown, OAuth, ApiKey }

/// <summary>An observation, never an authorization or a guarantee of future service acceptance.
/// All diagnostic text is derived from closed metadata; raw provider output has no place here.</summary>
public sealed record ProviderConnectionObservation(
    ProviderConnectionContext ExecutionContext,
    ProviderConnectionSource EvidenceSource,
    ProviderConnectionResult Result,
    ProviderConnectionAuthMode AuthenticationMode = ProviderConnectionAuthMode.Unknown,
    DateTimeOffset? ObservedAt = null,
    bool IsHistorical = false,
    double? AgeSeconds = null)
{
    public string Reason => Result switch
    {
        ProviderConnectionResult.SignedIn => "Host CLI reports signed in; worker service acceptance is separate.",
        ProviderConnectionResult.SignedOut => "Host CLI reports no sign-in.",
        ProviderConnectionResult.Expired => "Status explicitly reports expired credentials.",
        ProviderConnectionResult.ToolMissing => "The supported provider CLI was not found.",
        ProviderConnectionResult.TimedOut => "Status command exceeded the ten-second deadline.",
        ProviderConnectionResult.Unsupported => "No reliable supported status probe is available for this profile.",
        ProviderConnectionResult.MaterialPresent => "Selected Claude preflight material is present; expiry and service acceptance are unverified.",
        ProviderConnectionResult.MaterialUnavailable => "Selected Claude preflight material is unavailable; no alternative credential source was substituted.",
        ProviderConnectionResult.WorkerSucceeded => "An attributable completed worker invocation succeeded; this is historical evidence.",
        ProviderConnectionResult.AuthenticationFailed => "An attributable worker invocation reported authentication failure; expiry is undetermined.",
        ProviderConnectionResult.WorkerFailed => "An attributable worker invocation failed; authentication readiness is undetermined.",
        ProviderConnectionResult.OutputLimited => "Status output exceeded the capture limit; partial output was not interpreted.",
        _ => "Evidence is absent, unrecognized or insufficiently attributable; readiness is unknown."
    };

    public string NextAction => Result switch
    {
        ProviderConnectionResult.ToolMissing => "Install the provider CLI and repeat the explicit check.",
        ProviderConnectionResult.TimedOut => "Inspect the provider CLI locally, then repeat the explicit check.",
        ProviderConnectionResult.SignedOut or ProviderConnectionResult.Expired or ProviderConnectionResult.AuthenticationFailed =>
            "Review provider sign-in in the relevant context, then repeat the explicit check.",
        ProviderConnectionResult.MaterialUnavailable => "Review the selected Claude credential configuration and existing preflight.",
        ProviderConnectionResult.Unsupported => "Review the timestamped worker evidence or arrange a separately authorized worker invocation.",
        _ => "Compare observation contexts and timestamps; use recorded worker evidence for worker service acceptance."
    };

    public ProviderConnectionObservation AtReadTime(DateTimeOffset now) => this with
    {
        AgeSeconds = ObservedAt is { } at ? Math.Max(0, (now - at).TotalSeconds) : null
    };
}

public sealed record ProviderConnectionProfileReport(string ProfileId, string Provider,
    IReadOnlyList<ProviderConnectionObservation> Observations);

public sealed record ProviderConnectionReport(IReadOnlyList<ProviderConnectionProfileReport> Profiles);
