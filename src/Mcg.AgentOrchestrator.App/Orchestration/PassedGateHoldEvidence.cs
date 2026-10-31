using Mcg.AgentOrchestrator.Core;
using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PassedGateHoldEvidence
{
    private const string FactName = "landing-obligation-details";

    internal static EvidenceManifest Include(EvidenceManifest manifest, PassedGateHold? hold) => hold is null
        ? manifest : EvidenceManifest.Create(manifest.Entries.Append(new EvidenceManifestEntry(
            "passed-gate-hold:" + hold.AttemptId,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hold.Describe()))))).ToArray());

    internal static PolicyDecisionRecord Attach(PolicyDecisionRecord record, string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? record : record with
        { Facts = record.Facts.Append(new PolicyDecisionFact(FactName, detail)).ToArray() };

    internal static string Detail(PolicyDecisionRecord? record, string reason) =>
        record?.Facts.FirstOrDefault(fact => fact.Name == FactName)?.Value is { } detail
            ? reason + "\n" + detail : reason;
}
