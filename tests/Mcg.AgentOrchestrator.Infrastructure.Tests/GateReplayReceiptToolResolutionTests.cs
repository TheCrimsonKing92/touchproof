using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GateReplayReceiptToolResolutionTests
{
    [Fact]
    public void CaptureAndBindCopyAttemptToolDiagnostics()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows bare command resolution.");
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var environment = GateToolResolutionPreflightTests.CreateToolEnvironment(root);
            // Use a different valid SystemRoot identity to exercise both measured true and false.
            environment["SystemRoot"] = Path.Combine(root, "OtherWindows");
            var result = GateToolResolutionPreflight.EnsureToolsResolve(environment, root);
            using var scope = GateToolResolutionPreflight.EnterAttempt(result);
            var context = GateReplayReceiptTests.CreateContext(root);
            var executable = Path.Combine(root, "tools", "dotnet.exe");
            var startInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = root,
                CreateNoWindow = true
            };
            startInfo.Environment.Clear();
            startInfo.Environment["PATH"] = string.Empty;

            var launch = Assert.IsType<GateReplayReceipt.Launch>(GateReplayReceipt.CaptureLaunch(
                new AcceptanceProcessRequest { HeartbeatContext = context }, startInfo, true, [executable]));
            var receipt = Assert.IsType<GateReplayReceipt>(
                launch.Bind(context, Path.Combine(root, "stdout"), Path.Combine(root, "stderr")));
            var roundTrip = JsonSerializer.Deserialize<GateReplayReceipt>(JsonSerializer.Serialize(receipt));

            Assert.NotNull(roundTrip);
            Assert.Equal(environment["PATH"]!.Length, roundTrip.PathLength);
            Assert.False(roundTrip.SystemDirectoryOnPath);
            Assert.False(roundTrip.WindowsPowerShellDirectoryOnPath);
            Assert.True(roundTrip.GitDirectoryOnPath);
            Assert.True(roundTrip.DotnetDirectoryOnPath);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void LegacyReceiptWithoutToolDiagnosticsReadsAsUnknown()
    {
        const string json = """
            {
              "goalId": "old-goal",
              "candidateSha": "candidate",
              "observedMainSha": "main",
              "attemptId": "old-attempt",
              "checkIdentity": "old-check",
              "workingDirectory": "workspace",
              "executablePath": "tool",
              "executableSha256": "hash",
              "arguments": [],
              "expectedArtifacts": [],
              "environment": {},
              "requiresAcceptanceLease": false,
              "droppedEnvironmentNames": []
            }
            """;

        var receipt = JsonSerializer.Deserialize<GateReplayReceipt>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(receipt);
        Assert.Equal("old-attempt", receipt.AttemptId);
        Assert.Null(receipt.PathLength);
        Assert.Null(receipt.SystemDirectoryOnPath);
        Assert.Null(receipt.WindowsPowerShellDirectoryOnPath);
        Assert.Null(receipt.GitDirectoryOnPath);
        Assert.Null(receipt.DotnetDirectoryOnPath);
    }
}
