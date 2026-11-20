using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection("GateReplayEnvironment")]
public sealed class GateReplayEnvironmentAllowlistTests
{
    [CollectionDefinition("GateReplayEnvironment", DisableParallelization = true)]
    [ProcessLocalTestCollection("Ambient environment seeding is restored within the test process; all replay artifacts use private temporary roots.")]
    public sealed class SerialCollection;

    [Fact]
    public void InheritedInputsSurviveGateCompositionButStayOutOfPersistedReplay()
    {
        const string sensitiveName = "DOTNET_GATE_REPLAY_TEST_SECRET";
        const string sensitiveValue = "Bearer synthetic-ambient-gate-replay";
        const string ordinaryName = "DOTNET_GATE_REPLAY_TEST_MARKER";
        const string ordinaryValue = "ordinary-ambient-gate-replay";

        var (receipt, json, validation) = Publish(startInfo =>
        {
            var previousSensitive = Environment.GetEnvironmentVariable(sensitiveName);
            var previousOrdinary = Environment.GetEnvironmentVariable(ordinaryName);
            IDictionary<string, string?> environment;
            try
            {
                Environment.SetEnvironmentVariable(sensitiveName, sensitiveValue);
                Environment.SetEnvironmentVariable(ordinaryName, ordinaryValue);
                environment = startInfo.Environment;
            }
            finally
            {
                Environment.SetEnvironmentVariable(sensitiveName, previousSensitive);
                Environment.SetEnvironmentVariable(ordinaryName, previousOrdinary);
            }

            var root = startInfo.WorkingDirectory;
            AcceptanceShellProcessStartInfo.ConfigureCheckEnvironment(environment, [startInfo.FileName], root,
                new AcceptanceProcessRequest
                {
                    GateInvocationId = "ambient-replay-fixture",
                    ChildEnvironmentOverrides = new Dictionary<string, string?> { ["TEMP"] = root, ["TMP"] = root }
                });

            Assert.Equal(sensitiveValue, environment[sensitiveName]);
            Assert.Equal(ordinaryValue, environment[ordinaryName]);
        });

        Assert.Equal(GateReplayDiagnostic.Ready, validation.Diagnostic);
        var replay = Assert.IsType<GateReplayDescription>(validation.Replay);
        Assert.False(receipt.Environment.ContainsKey(sensitiveName));
        Assert.False(receipt.Environment.ContainsKey(ordinaryName));
        foreach (var excluded in new[] { sensitiveName, sensitiveValue, ordinaryName, ordinaryValue })
        {
            Assert.DoesNotContain(excluded, json, StringComparison.Ordinal);
            Assert.DoesNotContain(excluded, replay.Command, StringComparison.Ordinal);
        }
    }

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

    private static (GateReplayReceipt Receipt, string Json, GateReplayValidationResult Validation) Publish(string name, string value) =>
        Publish(startInfo =>
        {
            startInfo.Environment.Clear();
            startInfo.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = startInfo.WorkingDirectory;
            startInfo.Environment["TEMP"] = startInfo.WorkingDirectory;
            startInfo.Environment["TMP"] = startInfo.WorkingDirectory;
            startInfo.Environment["PATH"] = "ambient-path-sentinel";
            startInfo.Environment["PROVIDER_API_KEY"] = "ambient-credential-sentinel";
            startInfo.Environment[name] = value;
        });

    private static (GateReplayReceipt Receipt, string Json, GateReplayValidationResult Validation) Publish(
        Action<ProcessStartInfo> configureEnvironment)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var context = GateReplayReceiptTests.CreateContext(root);
            var image = Path.Combine(root, "fixture.exe");
            File.WriteAllText(image, "fixture image");
            var startInfo = new ProcessStartInfo
            {
                FileName = image,
                WorkingDirectory = root,
                CreateNoWindow = true
            };
            configureEnvironment(startInfo);

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
