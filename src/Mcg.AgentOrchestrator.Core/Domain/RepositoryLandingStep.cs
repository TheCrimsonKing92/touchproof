namespace Mcg.AgentOrchestrator.Core;

/// <summary>Write-ahead identity of one repository mutation; null LandedTip means its outcome is unknown.</summary>
public sealed record RepositoryLandingStep(string Key, string Root, string Branch, string PreTip,
    string CandidateTip, string? LandedTip = null);
