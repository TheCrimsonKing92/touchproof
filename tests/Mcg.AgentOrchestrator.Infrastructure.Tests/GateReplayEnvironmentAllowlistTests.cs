using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GateReplayEnvironmentAllowlistTests
{
    [Theory]
    [InlineData("ghp_", 20)]
    [InlineData("GHO_", 20)]
    [InlineData("ghs_", 20)]
    [InlineData("github_pat_", 20)]
    [InlineData("sk-", 20)]
    [InlineData("AKIA", 16)]
    [InlineData("xoxb-fake", 0)]
    [InlineData("eyJfixture.part-two.signature", 0)]
    [InlineData("bEaReR synthetic", 0)]
    [InlineData("Basic synthetic", 0)]
    [InlineData("https://user:pass@invalid.example", 0)]
    [InlineData("-----BEGIN", 0)]
    [InlineData("flags dbPassword=synthetic", 0)]
    [InlineData("flags passwd=synthetic", 0)]
    [InlineData("flags client_secret=synthetic", 0)]
    [InlineData("flags access-token=synthetic", 0)]
    [InlineData("flags ApiKey=synthetic", 0)]
    [InlineData("flags API_KEY=synthetic", 0)]
    [InlineData("flags credential=synthetic", 0)]
    [InlineData("0123456789abcdef0123456789abcdef", 0)]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz123456+", 0)]
    public void SensitiveValuesStayOutOfPersistedReceiptAndReplay(string prefix, int fillerLength)
    {
        var sentinel = prefix + new string('A', fillerLength);
        var (receipt, json, validation) = Publish(TempRootApparatusLossReceiptStore.GateInvocationIdVariable, sentinel);
        Assert.False(receipt.Environment.ContainsKey(TempRootApparatusLossReceiptStore.GateInvocationIdVariable));
        Assert.Equal(TempRootApparatusLossReceiptStore.GateInvocationIdVariable, Assert.Single(receipt.DroppedEnvironmentNames));
        Assert.Equal(1, receipt.DroppedEnvironmentCount);
        Assert.DoesNotContain(JsonSerializer.Serialize(sentinel)[1..^1], json, StringComparison.Ordinal);
        Assert.Equal(GateReplayDiagnostic.Ready, validation.Diagnostic);
        var replay = Assert.IsType<GateReplayDescription>(validation.Replay);
        Assert.DoesNotContain(sentinel, replay.Command, StringComparison.Ordinal);
        AssertSkippedAmbientEntries(receipt, json, replay.Command);
    }

    [Theory]
    [InlineData(@"C:\long\path\0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("/tmp/long-directory/AbCdEfGhIjKlMnOpQrStUvWxYz123456789")]
    [InlineData("orchestrator")]
    [InlineData("orchestrator@example.invalid")]
    [InlineData("1.2.3-preview.4")]
    [InlineData("88bcd107d88d490bbda7544a4d7292b6-0-20261120123456789-a1b2030405060708090a0b0c0d0e0f00")]
    [InlineData("true")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("ghp_short")]
    public void OrdinaryValuesSurvivePersistenceAndReplay(string value)
    {
        var name = TempRootApparatusLossReceiptStore.GateInvocationIdVariable;
        var (receipt, _, validation) = Publish(name.ToLowerInvariant(), value);
        Assert.Equal(value, receipt.Environment[name]);
        Assert.Empty(receipt.DroppedEnvironmentNames);
        Assert.Equal(GateReplayDiagnostic.Ready, validation.Diagnostic);
        Assert.Contains($"['{name}'] = '{value}'", Assert.IsType<GateReplayDescription>(validation.Replay).Command, StringComparison.Ordinal);
    }

    [Fact]
    public void DroppedRequiredValuePreventsReplay()
    {
        const string sentinel = "Bearer synthetic-required";
        var (receipt, json, validation) = Publish("TMP", sentinel);
        Assert.False(receipt.Environment.ContainsKey("TMP"));
        Assert.Equal("TMP", Assert.Single(receipt.DroppedEnvironmentNames));
        Assert.DoesNotContain(sentinel, json, StringComparison.Ordinal);
        Assert.Equal(GateReplayDiagnostic.MissingRequiredEnvironment, validation.Diagnostic);
        Assert.Contains("TMP", validation.Message, StringComparison.Ordinal);
        Assert.Null(validation.Replay);
    }

    private static (GateReplayReceipt Receipt, string Json, GateReplayValidationResult Validation) Publish(string name, string value)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var context = GateReplayReceiptTests.CreateContext(root);
            var image = Path.Combine(root, "fixture.exe");
            File.WriteAllText(image, "fixture image");
            var startInfo = new ProcessStartInfo { FileName = image, WorkingDirectory = root };
            startInfo.Environment.Clear();
            startInfo.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = root;
            startInfo.Environment["TEMP"] = root;
            startInfo.Environment["TMP"] = root;
            startInfo.Environment["PATH"] = "ambient-path-sentinel";
            startInfo.Environment["PROVIDER_API_KEY"] = "ambient-credential-sentinel";
            startInfo.Environment[name] = value;

            var launch = Assert.IsType<GateReplayReceipt.Launch>(
                GateReplayReceipt.CaptureLaunch(new() { HeartbeatContext = context }, startInfo, directLaunch: true, [image]));
            var stdout = Path.Combine(root, "stdout");
            var stderr = Path.Combine(root, "stderr");
            File.WriteAllText(stdout, "");
            File.WriteAllText(stderr, "");
            var runtime = new GateHeartbeatRuntime(context, Environment.ProcessId, stdout, stderr,
                TimeSpan.Zero, replayLaunch: launch);
            runtime.WriteFinal("completed", null, 0);
            var snapshot = Assert.IsType<GateHeartbeatSnapshot>(GateHeartbeatArtifacts.TryReadSnapshot(context.HeartbeatPath));
            var receipt = Assert.IsType<GateReplayReceipt>(snapshot.ReplayReceipt);
            return (receipt, File.ReadAllText(context.HeartbeatPath),
                GateReplayReceiptValidator.Validate(receipt, GateReplayReceiptValidatorTests.MatchingFacts(receipt)));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static void AssertSkippedAmbientEntries(GateReplayReceipt receipt, string json, string command)
    {
        Assert.False(receipt.Environment.ContainsKey("PATH"));
        Assert.False(receipt.Environment.ContainsKey("PROVIDER_API_KEY"));
        foreach (var excluded in new[] { "ambient-path-sentinel", "ambient-credential-sentinel", "PROVIDER_API_KEY" })
        {
            Assert.DoesNotContain(excluded, json, StringComparison.Ordinal);
            Assert.DoesNotContain(excluded, command, StringComparison.Ordinal);
        }
    }
}
