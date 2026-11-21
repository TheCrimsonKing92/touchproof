using System.Collections.Immutable;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GateReplayReceiptValidatorTests
{
    [Fact]
    public void LegacyHeartbeat_MissingReceipt_ExplainsEnvironmentAndAuthority()
    {
        var snapshot = Assert.IsType<GateHeartbeatSnapshot>(JsonSerializer.Deserialize<GateHeartbeatSnapshot>(
            """{"phase":"verification-check","state":"completed","commandLine":"dotnet test"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("dotnet test", snapshot.CommandLine);
        Assert.Null(snapshot.ReplayReceipt);
        var result = GateReplayReceiptValidator.Validate(snapshot.ReplayReceipt, MatchingFacts(OrdinaryReceipt()));
        Assert.Equal(GateReplayDiagnostic.MissingReceipt, result.Diagnostic);
        Assert.Contains("environment", result.Message, StringComparison.Ordinal);
        Assert.Contains("authority", result.Message, StringComparison.Ordinal);
        Assert.Null(result.Replay);
        Assert.Null(result.Regate);
    }

    [Theory]
    [InlineData(GateReplayDiagnostic.CandidateShaMismatch)]
    [InlineData(GateReplayDiagnostic.ObservedMainShaMismatch)]
    [InlineData(GateReplayDiagnostic.ExecutablePathMismatch)]
    [InlineData(GateReplayDiagnostic.ExecutableHashMismatch)]
    [InlineData(GateReplayDiagnostic.MissingArtifact)]
    [InlineData(GateReplayDiagnostic.ArtifactPathMismatch)]
    public void ChangedObservationRefusesReplay(GateReplayDiagnostic expected)
    {
        var receipt = OrdinaryReceipt();
        var valid = MatchingFacts(receipt);
        var output = valid.Artifacts[0];
        var differentFacts = expected switch
        {
            GateReplayDiagnostic.CandidateShaMismatch => valid with { CandidateSha = new string('e', 40) },
            GateReplayDiagnostic.ObservedMainShaMismatch => valid with { ObservedMainSha = new string('e', 40) },
            GateReplayDiagnostic.ExecutablePathMismatch => valid with { ExecutablePath = valid.ExecutablePath + ".changed" },
            GateReplayDiagnostic.ExecutableHashMismatch => valid with { ExecutableSha256 = new string('e', 64) },
            GateReplayDiagnostic.MissingArtifact => valid with { Artifacts = [output with { Exists = false }, .. valid.Artifacts.Skip(1)] },
            GateReplayDiagnostic.ArtifactPathMismatch => valid with { Artifacts = [output with { Path = output.Path + ".changed" }, .. valid.Artifacts.Skip(1)] },
            _ => throw new ArgumentOutOfRangeException(nameof(expected))
        };
        Assert.Equal(GateReplayDiagnostic.Ready, GateReplayReceiptValidator.Validate(receipt, valid).Diagnostic);
        var result = GateReplayReceiptValidator.Validate(receipt, differentFacts);
        Assert.Equal(expected, result.Diagnostic);
        Assert.Null(result.Replay);
        Assert.Null(result.Regate);
    }

    [Fact]
    public void ArtifactMatchingIsOrdinalAndAccountsForMultiplePathsPerRole()
    {
        var receipt = OrdinaryReceipt();
        var facts = MatchingFacts(receipt);
        Assert.Equal(GateReplayDiagnostic.Ready,
            GateReplayReceiptValidator.Validate(receipt, facts with { Artifacts = facts.Artifacts.Reverse().ToArray() }).Diagnostic);

        var missing = facts with { Artifacts = [.. facts.Artifacts.SkipLast(1)] };
        Assert.Equal(GateReplayDiagnostic.MissingArtifact, GateReplayReceiptValidator.Validate(receipt, missing).Diagnostic);
        var output = facts.Artifacts[^1];
        var differentPath = Path.ChangeExtension(output.Path, ".TRX");
        var different = facts with { Artifacts = [.. missing.Artifacts, output with { Path = differentPath }] };
        var result = GateReplayReceiptValidator.Validate(receipt, different);
        Assert.Equal(GateReplayDiagnostic.ArtifactPathMismatch, result.Diagnostic);
        Assert.Contains(output.Path, result.Message, StringComparison.Ordinal);
        Assert.Contains(differentPath, result.Message, StringComparison.Ordinal);
        Assert.Null(result.Replay);
    }

    [Theory]
    [InlineData("MCG_ORCHESTRATOR_REPOSITORY_ROOT")]
    [InlineData("TEMP")]
    [InlineData("TMP")]
    public void MissingRequiredEnvironmentEntryRefusesReplay(string name)
    {
        var receipt = OrdinaryReceipt();
        var result = GateReplayReceiptValidator.Validate(receipt with { Environment = receipt.Environment.Remove(name) },
            MatchingFacts(receipt));
        Assert.Equal(GateReplayDiagnostic.MissingRequiredEnvironment, result.Diagnostic);
        Assert.Contains(name, result.Message, StringComparison.Ordinal);
        Assert.Null(result.Replay);
    }

    [Theory]
    [InlineData("PATH", "synthetic-nonallowlisted")]
    [InlineData("MCG_ACCEPTANCE_CHANGE_SCOPED", "Bearer synthetic-invalid")]
    public void UntrustedEnvironmentEntryRefusesReplayWithoutEchoingValue(string name, string value)
    {
        var receipt = OrdinaryReceipt();
        var result = GateReplayReceiptValidator.Validate(receipt with { Environment = receipt.Environment.Add(name, value) },
            MatchingFacts(receipt));
        Assert.Equal(GateReplayDiagnostic.InvalidEnvironment, result.Diagnostic);
        Assert.DoesNotContain(value, result.Message, StringComparison.Ordinal);
        Assert.Null(result.Replay);
    }

    [Fact]
    public void OrdinaryReplayRequiresWorkingDirectoryAndRendersEscapedArguments()
    {
        var receipt = OrdinaryReceipt();
        var facts = MatchingFacts(receipt);
        var missing = GateReplayReceiptValidator.Validate(receipt with { WorkingDirectory = "" }, facts);
        Assert.Equal(GateReplayDiagnostic.MissingWorkingDirectory, missing.Diagnostic);
        Assert.Null(missing.Replay);

        var result = GateReplayReceiptValidator.Validate(receipt, facts);
        Assert.Equal(GateReplayDiagnostic.Ready, result.Diagnostic);
        var replay = Assert.IsType<GateReplayDescription>(result.Replay);
        Assert.Equal("PowerShell 7+", replay.Shell);
        Assert.StartsWith($"Set-Location -LiteralPath '{receipt.WorkingDirectory.Replace("'", "''")}'", replay.Command, StringComparison.Ordinal);
        Assert.Equal(
            ["$launch.ArgumentList.Add('')", "$launch.ArgumentList.Add('two words')",
             "$launch.ArgumentList.Add('a''b')", "$launch.ArgumentList.Add('& $HOME | <')"],
            replay.Command.Split(Environment.NewLine).Where(line => line.StartsWith("$launch.ArgumentList.Add", StringComparison.Ordinal)));
    }

    [Fact]
    public void LeasedReceipt_ReturnsRegateRouteWithoutCommand()
    {
        var receipt = OrdinaryReceipt() with { RequiresAcceptanceLease = true };
        var result = GateReplayReceiptValidator.Validate(receipt, MatchingFacts(receipt));
        Assert.Equal(GateReplayDiagnostic.AcceptanceLeaseRequired, result.Diagnostic);
        Assert.Contains("acceptance lease", result.Message, StringComparison.Ordinal);
        Assert.Contains("operator-adjudicate-reopen-regate", result.Message, StringComparison.Ordinal);
        Assert.Contains("task number", result.Message, StringComparison.Ordinal);
        Assert.Equal(new GateReplayRegateRequest(GateReplayRegateRoute.OperatorAdjudicateReopenRegate,
            receipt.GoalId, receipt.AttemptId, receipt.CheckIdentity, receipt.CandidateSha, receipt.ObservedMainSha), result.Regate);
        Assert.Null(result.Replay);
    }

    [Fact]
    public void MissingLeaseBindingDoesNotDefaultToStandaloneReplay()
    {
        var receipt = OrdinaryReceipt() with { RequiresAcceptanceLease = null };
        var result = GateReplayReceiptValidator.Validate(receipt, MatchingFacts(receipt));
        Assert.Equal(GateReplayDiagnostic.InvalidReceipt, result.Diagnostic);
        Assert.Null(result.Replay);
    }

    internal static GateReplayFacts MatchingFacts(GateReplayReceipt receipt) =>
        new(receipt.CandidateSha, receipt.ObservedMainSha, receipt.ExecutablePath, receipt.ExecutableSha256,
            receipt.ExpectedArtifacts.Select(artifact => new GateReplayArtifactFact(artifact.Role, artifact.Path, true)).ToArray());

    private static GateReplayReceipt OrdinaryReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "working dir's fixture");
        return new("goal", new string('a', 40), new string('b', 40), "attempt", "check", root,
            Path.Combine(root, "fixture.exe"), new string('c', 64), ["", "two words", "a'b", "& $HOME | <"],
            [
                new(GateReplayArtifactRole.Stdout, Path.Combine(root, "stdout")),
                new(GateReplayArtifactRole.Stderr, Path.Combine(root, "stderr")),
                new(GateReplayArtifactRole.TestResults, Path.Combine(root, "first.trx")),
                new(GateReplayArtifactRole.TestResults, Path.Combine(root, "second.trx"))
            ],
            ImmutableDictionary<string, string>.Empty
                .Add("MCG_ORCHESTRATOR_REPOSITORY_ROOT", root).Add("TEMP", root).Add("TMP", root), false, []);
    }
}
