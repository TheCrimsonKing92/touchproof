using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

public sealed class CriterionEvidenceLegacySnapshotRestoreTests
{
    // Pre-supersession snapshot fields and numeric enum values. No current writer builds this input.
    private const string LegacySnapshot = """
        {
          "Goals": [{
            "Id": "b0896141a89b450dbd2a40d354308920",
            "Objective": "Legacy evidence",
            "Status": 1,
            "Tasks": [],
            "Timeline": [],
            "CreatedAt": "2026-10-01T00:00:00Z",
            "RefinedSpecVersions": [{
              "Version": 1,
              "RecordedAt": "2026-10-01T00:00:00Z",
              "BriefVersion": 1,
              "Spec": {
                "BehavioralContract": "Observe the behavior",
                "AcceptanceCriteria": ["Pending duty", "Satisfied duty", "Failed duty"],
                "VerificationClass": "RealWorldDependent",
                "Decisions": [],
                "OpenQuestions": [],
                "OperatorOwnedAcceptanceCriteria": ["Pending duty", "Satisfied duty", "Failed duty"]
              }
            }],
            "CriterionEvidenceObligations": [
              {
                "Id": "criterion-v1-0", "CriterionIndex": 0, "CriterionVersion": 1, "Criterion": "Pending duty",
                "Owner": "Operator", "State": 0, "RequiredScope": "operator observation",
                "Provenance": "original pending", "RecordedAt": "2026-10-01T00:00:00Z"
              },
              {
                "Id": "criterion-v1-1", "CriterionIndex": 1, "CriterionVersion": 1, "Criterion": "Satisfied duty",
                "Owner": "Operator", "State": 2, "RequiredScope": "operator observation",
                "Provenance": "original satisfied", "RecordedAt": "2026-10-01T00:00:00Z",
                "CandidateSha": "candidate", "ReceiptId": "passed-receipt", "Detail": "Observed"
              },
              {
                "Id": "criterion-v1-2", "CriterionIndex": 2, "CriterionVersion": 1, "Criterion": "Failed duty",
                "Owner": "Operator", "State": 1, "RequiredScope": "operator observation",
                "Provenance": "original failed", "RecordedAt": "2026-10-01T00:00:00Z",
                "CandidateSha": "candidate", "ReceiptId": "failed-receipt", "Detail": "Condition was absent"
              },
              {
                "Id": "legacy-malformed", "CriterionIndex": 0, "CriterionVersion": 1, "Criterion": "Pending duty",
                "Owner": "Unknown", "State": 3, "RequiredScope": "ownership mapping required",
                "Provenance": "original repair", "RecordedAt": "2026-10-01T00:00:00Z",
                "ReplacementObligationId": "criterion-v1-0", "ReplacementOwner": "Operator"
              }
            ]
          }],
          "HumanInputRequests": []
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreChangeSnapshot_RoundTrip_PreservesAllFourStates(bool stringStates)
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var json = stringStates
            ? LegacySnapshot.Replace("\"State\": 0", "\"State\": \"Pending\"", StringComparison.Ordinal)
                .Replace("\"State\": 1", "\"State\": \"Failed\"", StringComparison.Ordinal)
                .Replace("\"State\": 2", "\"State\": \"Satisfied\"", StringComparison.Ordinal)
                .Replace("\"State\": 3", "\"State\": \"Repaired\"", StringComparison.Ordinal)
            : LegacySnapshot;
        var original = JsonSerializer.Deserialize<OrchestratorSnapshot>(json, options)!;
        var kernel = AgentOrchestratorKernel.FromSnapshot(original);
        var written = JsonSerializer.Serialize(kernel.ExportSnapshot(), options);
        var restored = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(written, options)!);
        var obligations = Assert.Single(restored.Goals).CriterionEvidenceObligations;

        Assert.Collection(obligations,
            row => AssertState(row, "criterion-v1-0", CriterionEvidenceState.Pending, CriterionEvidenceOwner.Operator, "original pending"),
            row => AssertState(row, "criterion-v1-1", CriterionEvidenceState.Satisfied, CriterionEvidenceOwner.Operator, "original satisfied"),
            row => AssertState(row, "criterion-v1-2", CriterionEvidenceState.Failed, CriterionEvidenceOwner.Operator, "original failed"),
            row => AssertState(row, "legacy-malformed", CriterionEvidenceState.Repaired, CriterionEvidenceOwner.Unknown, "original repair"));
        Assert.Equal(Assert.Single(original.Goals).CriterionEvidenceObligations!, obligations);
    }

    private static void AssertState(CriterionEvidenceObligation row, string id,
        CriterionEvidenceState state, CriterionEvidenceOwner owner, string provenance)
    {
        Assert.Equal(id, row.Id);
        Assert.Equal(state, row.State);
        Assert.Equal(owner, row.Owner);
        Assert.Equal(provenance, row.Provenance);
    }
}
