using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using static DotnetBuildEnvironmentManagerTests;

public sealed class ArtifactCustodyPreservedTakeoverTests : DotnetBuildEnvironmentManagerRootedTestBase
{
    private const string EvidenceFileName = "retained.trx";

    [Xunit.Fact]
    public void Preserved_directory_refuses_live_foreign_custody()
    {
        var environment = CreateArtifacts();
        SeedCustody(environment, "other-attempt");
        var request = CreateContext(environment, "new-attempt");

        AssertRefusedWithoutArtifactChanges(environment, request);
    }

    [Xunit.Fact]
    public void Preserved_directory_refreshes_same_attempt_custody()
    {
        var environment = CreateArtifacts();
        SeedCustody(environment, "same-attempt");
        var request = CreateContext(environment, "same-attempt");

        using var lease = Acquire(environment, request);

        AssertEvidencePreserved(environment);
        AssertCustody(environment, request);
    }

    [Xunit.Fact]
    public void Preserved_directory_replaces_terminal_attempt_custody()
    {
        var environment = CreateArtifacts();
        SeedCustody(environment, "finished-attempt", outcome: 2);
        var request = CreateContext(environment, "new-attempt");

        using var lease = Acquire(environment, request);

        AssertEvidencePreserved(environment);
        AssertCustody(environment, request);
    }

    [Xunit.Fact]
    public void Preserved_directory_writes_absent_custody()
    {
        var environment = CreateArtifacts();
        var request = CreateContext(environment, "new-attempt");
        Assert.False(File.Exists(AcceptanceAttemptArtifactCustody.MarkerPath(environment.ArtifactsPath)));

        using var lease = Acquire(environment, request);

        AssertEvidencePreserved(environment);
        AssertCustody(environment, request);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void Clean_directory_refuses_live_custody_before_wipe(bool supplyCustodyContext)
    {
        var environment = CreateArtifacts(matchingOwner: false);
        SeedCustody(environment, "other-attempt");
        var request = supplyCustodyContext ? CreateContext(environment, "new-attempt") : null;

        AssertRefusedWithoutArtifactChanges(environment, request);
    }

    [Xunit.Fact]
    public void Clean_directory_without_custody_wipes_then_writes_new_custody()
    {
        var environment = CreateArtifacts(matchingOwner: false);
        var request = CreateContext(environment, "new-attempt");

        using var lease = Acquire(environment, request);

        Assert.False(File.Exists(Path.Combine(environment.ArtifactsPath, EvidenceFileName)));
        AssertCustody(environment, request);
    }

    private DotnetBuildEnvironment CreateArtifacts(bool matchingOwner = true)
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        Directory.CreateDirectory(environment.ArtifactsPath);
        File.WriteAllText(Path.Combine(environment.ArtifactsPath, EvidenceFileName), "receipt");
        if (matchingOwner)
        {
            File.WriteAllText(
                OwnerMarkerPath(environment),
                JsonSerializer.Serialize(new { ownerToken = environment.SlotOwnerToken }));
        }
        else
        {
            WriteForeignOwnerMarker(environment.ArtifactsPath);
        }

        return environment;
    }

    private static AcceptanceAttemptArtifactCustodyContext CreateContext(
        DotnetBuildEnvironment environment,
        string attemptId,
        int outcome = 0,
        string name = "request")
    {
        var hint = Path.Combine(environment.RootPath, $"{attemptId}-{name}.attempt.json");
        WriteAttemptMetadata(hint, attemptId, outcome, Environment.ProcessId);
        return new AcceptanceAttemptArtifactCustodyContext(attemptId, hint, Environment.ProcessId);
    }

    private static void SeedCustody(DotnetBuildEnvironment environment, string attemptId, int outcome = 0)
    {
        var context = CreateContext(environment, attemptId, outcome, name: "prior");
        AcceptanceAttemptArtifactCustody.Write(
            environment.ArtifactsPath, context.AttemptId, context.LivenessCheckHint, context.OwnerProcessId);
    }

    private static DotnetBuildEnvironmentLease Acquire(
        DotnetBuildEnvironment environment,
        AcceptanceAttemptArtifactCustodyContext? context) =>
        DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(
            environment,
            timeProvider: TimeProvider.System,
            sleep: _ => { },
            artifactCustody: context,
            timeout: TimeSpan.Zero);

    private static void AssertRefusedWithoutArtifactChanges(
        DotnetBuildEnvironment environment,
        AcceptanceAttemptArtifactCustodyContext? request)
    {
        var custodyPath = AcceptanceAttemptArtifactCustody.MarkerPath(environment.ArtifactsPath);
        var custodyBefore = File.ReadAllBytes(custodyPath);
        var ownerBefore = File.ReadAllBytes(OwnerMarkerPath(environment));
        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var exception = Assert.Throws<DotnetBuildSlotsBusyException>(() =>
            {
                using var lease = Acquire(environment, request);
            });
            Assert.Equal(environment.LeaseId, exception.SlotsBusy.WantedBy);
        });

        Assert.Equal(custodyBefore, File.ReadAllBytes(custodyPath));
        Assert.Equal(ownerBefore, File.ReadAllBytes(OwnerMarkerPath(environment)));
        AssertEvidencePreserved(environment);
        Assert.Contains("source=acceptance-attempt-custody", output, StringComparison.Ordinal);
        Assert.Contains("SLOTS_BUSY ", output, StringComparison.Ordinal);
    }

    private static void AssertCustody(
        DotnetBuildEnvironment environment,
        AcceptanceAttemptArtifactCustodyContext request)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(AcceptanceAttemptArtifactCustody.MarkerPath(environment.ArtifactsPath)));
        Assert.Equal(request.AttemptId, document.RootElement.GetProperty("attemptId").GetString());
        Assert.Equal(request.LivenessCheckHint, document.RootElement.GetProperty("livenessCheckHint").GetString());
        Assert.Equal(request.OwnerProcessId, document.RootElement.GetProperty("ownerProcessId").GetInt32());
    }

    private static void AssertEvidencePreserved(DotnetBuildEnvironment environment) =>
        Assert.Equal("receipt", File.ReadAllText(Path.Combine(environment.ArtifactsPath, EvidenceFileName)));

    private static string OwnerMarkerPath(DotnetBuildEnvironment environment) =>
        Path.Combine(environment.ArtifactsPath, ".mcg-artifacts-owner.json");
}
