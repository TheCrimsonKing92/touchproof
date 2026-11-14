namespace Mcg.AgentOrchestrator.Core;

/// <summary>Typed event data is authoritative; historical events retain their message parser.</summary>
public static class PreTesterEvidenceEntryReader
{
    public static PreTesterEvidenceEntry? Read(ProgressEvent evt) =>
        evt.PreTesterEvidence ?? ReadLegacy(evt.Message);

    public static PreTesterEvidenceEntry? ReadLegacy(string message) =>
        PreTesterEvidenceIndexLines.Parse(message);
}
