using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: owned dictionaries/files and child environments; no parent environment mutation.
[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class GateDeclaredTestSettingsTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("declared-settings-").FullName;
    private const string Slots = "MCG_TEST_HOST_CAPACITY_SLOTS";
    private const string Ambient = "MCG_TEST_AMBIENT_ONLY";

    [Theory]
    [InlineData("FOO_BAR")]
    [InlineData("mcg_test_x")]
    [InlineData("MCG_TEST_x")]
    [InlineData("MCG_TEST_")]
    [InlineData("MCG_TEST_X\n")]
    [InlineData("MCG_TEST_X=Y")]
    [InlineData("MCG_TEST_SLOT_TIMING_FILE")]
    [InlineData("MCG_TEST_BUILD_OUTPUT_ROOT")]
    [InlineData("MCG_TEST_ONLY_ACCEPTANCE_LEGACY_START_THEN_ATTACH")]
    [InlineData("MCG_TEST_INPUT_X")]
    public void Manifest_RejectsInvalidOrReservedNamesAndNamesKey(string name)
    {
        foreach (var engine in new[] { "", ",\"engine\":{}" })
        {
            var json = "{\"checks\":[],\"testSettings\":{" + JsonSerializer.Serialize(name) + ":\"6\"}" + engine + "}";
            Assert.Contains(name, Assert.Throws<InvalidDataException>(() => AcceptanceGateEngineSettings.Parse(json)).Message);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public void Manifest_RejectsNonObjectSettings(string value)
    {
        foreach (var engine in new[] { "", ",\"engine\":{}" })
            Assert.Contains("testSettings", Assert.Throws<InvalidDataException>(() =>
                AcceptanceGateEngineSettings.Parse("{\"checks\":[],\"testSettings\":" + value + engine + "}")).Message);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("6")]
    [InlineData("true")]
    [InlineData("\"a\\u0000b\"")]
    [InlineData("\"a\\rb\"")]
    [InlineData("\"a\\nb\"")]
    public void Manifest_RejectsInvalidValuesAndNamesKey(string value)
    {
        foreach (var engine in new[] { "", ",\"engine\":{}" })
            Assert.Contains(Slots, Assert.Throws<InvalidDataException>(() => AcceptanceGateEngineSettings.Parse(
                "{\"checks\":[],\"testSettings\":{\"" + Slots + "\":" + value + "}" + engine + "}")).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Manifest_RejectsDuplicateKeys(bool engine)
    {
        var json = "{\"checks\":[],\"testSettings\":{\"" + Slots + "\":\"6\",\"" + Slots + "\":\"7\"}" +
            (engine ? ",\"engine\":{}" : "") + "}";
        Assert.Contains(Slots, Assert.Throws<InvalidDataException>(() => AcceptanceGateEngineSettings.Parse(json)).Message);
    }

    [Fact]
    public void Manifest_PreservesLiteralValuesAndOrdinalOrder()
    {
        var settings = Parse("""{"MCG_TEST_Z":" ","MCG_TEST_A":" $value='x'=6 \""}""");
        Assert.Equal(new[] { "MCG_TEST_A", "MCG_TEST_Z" }, settings.DeclaredTestSettings.Keys);
        Assert.Equal(" ", settings.DeclaredTestSettings["MCG_TEST_Z"]);
        Assert.Equal(" $value='x'=6 \"", settings.DeclaredTestSettings["MCG_TEST_A"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForCheck_SealsThenAppliesOnlyDeclaredSettingsAndPreservesLaneVariables(bool mtp)
    {
        var settings = Parse("{\"" + Slots + "\":\"6\"}");
        var request = new AcceptanceProcessRequest
        {
            ResolveBareCommand = false, EngineSettings = settings, GateInvocationId = "declared-settings-gate",
            ApparatusReceiptPath = Path.Combine(root, "apparatus.json"),
            ChildEnvironmentOverrides = new Dictionary<string, string?> { [Slots] = "override", ["UNRELATED_OVERRIDE"] = "kept" }
        };
        var arguments = Arguments(mtp);
        var start = AcceptanceShellProcessStartInfo.ForCheck(arguments, root, null, null, false, request, out _);
        Assert.Equal("6", start.Environment[Slots]);
        Assert.Equal("kept", start.Environment["UNRELATED_OVERRIDE"]);
        Assert.Equal(LaneSlotTimingReader.PathFor(Path.Combine(root, "results", "lane.trx")), start.Environment[LaneSlotTimingReader.VariableName]);
        Assert.Equal(root, start.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"]);
        Assert.False(string.IsNullOrEmpty(start.Environment["HOME"]));
        Assert.Equal(request.GateInvocationId, start.Environment[TempRootApparatusLossReceiptStore.GateInvocationIdVariable]);
        Assert.Equal(request.ApparatusReceiptPath, start.Environment[TempRootApparatusLossReceiptStore.ReceiptPathVariable]);

        var environment = new Dictionary<string, string?> { [Ambient] = "ambient", [Slots] = "99" };
        AcceptanceShellProcessStartInfo.ConfigureCheckEnvironment(environment, arguments, root, request);
        Assert.False(environment.ContainsKey(Ambient));
        Assert.Equal("6", environment[Slots]);
        Assert.True(AcceptanceHermeticEnvironment.IsHermeticVerificationEnvironmentVariable(Slots,
            declaredTestSettingNames: settings.DeclaredTestSettings.Keys));
        Assert.False(AcceptanceHermeticEnvironment.IsHermeticVerificationEnvironmentVariable(Ambient,
            declaredTestSettingNames: settings.DeclaredTestSettings.Keys));
        Assert.False(AcceptanceHermeticEnvironment.IsHermeticVerificationEnvironmentVariable(Slots.ToLowerInvariant(),
            declaredTestSettingNames: settings.DeclaredTestSettings.Keys));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Environment_NoDeclarationOrNonTestCheck_DropsAmbientSettings(bool test)
    {
        var environment = new Dictionary<string, string?> { [Ambient] = "ambient", [Slots] = "99" };
        var request = new AcceptanceProcessRequest { EngineSettings = test ? Parse("{}") : Parse("{\"" + Slots + "\":\"6\"}") };
        AcceptanceShellProcessStartInfo.ConfigureCheckEnvironment(environment, test ? Arguments(true) : ["dotnet", "build"], root, request);
        Assert.False(environment.ContainsKey(Ambient));
        Assert.False(environment.ContainsKey(Slots));
        Assert.False(AcceptanceHermeticEnvironment.IsHermeticVerificationEnvironmentVariable(Slots));
    }

    [Fact]
    public void LanePlan_RecordsSortedSettingsIncludingExplicitEmptyMap()
    {
        var settings = Parse("{\"MCG_TEST_Z\":\"last\",\"MCG_TEST_A\":\"first\"}");
        var plan = AcceptanceGateLanePlanPublisher.Classify("attempt", [], [], [], [],
            PolicyShardPlan.Full("test", new HashSet<string>()), DotnetShardDisposition.RunDotnetShards,
            [], [], [], [], settings.DeclaredTestSettings);
        using var document = JsonDocument.Parse(plan.ToJson());
        var recorded = document.RootElement.GetProperty("testSettings");
        Assert.Equal(new[] { "MCG_TEST_A", "MCG_TEST_Z" }, recorded.EnumerateObject().Select(p => p.Name));
        Assert.Equal("first", recorded.GetProperty("MCG_TEST_A").GetString());
        Assert.Equal("last", recorded.GetProperty("MCG_TEST_Z").GetString());
        using var empty = JsonDocument.Parse(new AcceptanceGateLanePlan("gate-lane-plan.v1", "attempt", [], []).ToJson());
        Assert.Empty(empty.RootElement.GetProperty("testSettings").EnumerateObject());
    }

    [Fact]
    public void EffectiveIdentity_BindsSettingsWithoutChangingEmptyIdentity()
    {
        var absent = AcceptanceGateEngineSettings.Parse("{\"checks\":[]}");
        var empty = Parse("{}");
        Assert.Equal(EffectiveAcceptanceManifestIdentity.Compute([], absent), EffectiveAcceptanceManifestIdentity.Compute([], empty));
        Assert.NotEqual(EffectiveAcceptanceManifestIdentity.Compute([], empty), EffectiveAcceptanceManifestIdentity.Compute([], Parse("{\"" + Slots + "\":\"6\"}")));
        Assert.Equal(EffectiveAcceptanceManifestIdentity.Compute([], Parse("{\"MCG_TEST_Z\":\"z\",\"MCG_TEST_A\":\"a\"}")),
            EffectiveAcceptanceManifestIdentity.Compute([], Parse("{\"MCG_TEST_A\":\"a\",\"MCG_TEST_Z\":\"z\"}")));
    }

    [Theory]
    [InlineData("lane", true)]
    [InlineData("lane", false)]
    [InlineData("build-check", true)]
    public async Task SshJob_CarriesDeclaredSettingsOnlyForTestLanes(string kind, bool declare)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var settings = Parse(declare ? "{\"" + Slots + "\":\"6\"}" : "{}");
        var request = FakeSshRemoteLaneTransport.Request with { Kind = kind, TestSettings = settings.DeclaredTestSettings };
        var submission = await fake.Executor().SubmitAsync(request, default);
        fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
        var copy = Assert.Single(fake.Calls.Where(call => call.Arguments.Contains("job.json")));
        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(copy.Directory, "job.json")));
        if (kind == "lane" && declare) Assert.Equal("6", job.RootElement.GetProperty("testSettings").GetProperty(Slots).GetString());
        else Assert.False(job.RootElement.TryGetProperty("testSettings", out _));
    }

    [Fact]
    public async Task Coordinator_GateAndFocusedRequestsUseSameSnapshot()
    {
        var entry = new RemoteLaneExecutorEntry("one", 60);
        var identity = new RemoteLaneCandidateIdentity("attempt", "goal", "commit", "tree", "main", "manifest");
        var executor = new FakeRemoteLaneExecutor();
        var settings = Parse("{\"" + Slots + "\":\"6\"}").DeclaredTestSettings;
        using var coordinator = new RemoteLaneCoordinator(new([entry], ["infrastructure tests: lane"], null),
            identity, root, null, executor, TimeProvider.System, TimeSpan.FromSeconds(1), null) { TestSettings = settings };
        Assert.Same(settings, coordinator.CreateRequest("focused", InfrastructureTestsProject, "FullyQualifiedName~Probe", identity, "one").TestSettings);
        Assert.Equal(entry, coordinator.TryClaimIdleExecutor());
        var outcome = await coordinator.RunRemoteAsync(new()
        {
            Name = "infrastructure tests: lane", Type = "dotnet-test", Project = InfrastructureTestsProject,
            Arguments = ["--filter", "FullyQualifiedName~Probe"]
        }, entry, RemoteLaneDeadline.Floor, RemoteLaneDeadline.Floor, default);
        Assert.Same(settings, Assert.Single(executor.Requests).TestSettings);
        Assert.Same(settings, outcome.Request.TestSettings);
        Assert.Null(outcome.Accepted);
    }

    [Theory]
    [InlineData("remote-first")]
    [InlineData("shadow")]
    public async Task FocusedEvidence_DeclaredSettingsReachSubmittedJobs(string mode)
    {
        using var scenario = new RemoteFirstFocusedEvidenceFixture.Scenario(new FocusedFixture(),
            """{"version":1,"checks":[],"testSettings":{"MCG_TEST_HOST_CAPACITY_SLOTS":"6"}}""");
        scenario.Configure(mode: mode, sample: 1);
        await using var owner = AcceptanceExecutionOwners.CreateFocusedVerification(scenario.Root, scenario.Goal,
            null, options: new(RunId: "declared-settings", ResultsPrefix: scenario.Prefix));
        var context = (IAcceptanceRunExecutionContext)owner;
        var settings = context.Settings.DeclaredTestSettings;
        Assert.Equal("6", settings[Slots]);
        var executor = scenario.CompletedExecutor(change: result => result with { TestSettings = settings });
        var check = RemoteFirstFocusedEvidenceFixture.FocusedCheck();
        AcceptanceCheckResult[] localResults = [new(check.Name, true, 0, "local-passed")];
        if (mode == "remote-first")
        {
            var pending = RemoteFirstFocusedEvidence.RunAsync(scenario.Overrides, scenario.Clock, scenario.Root,
                [check], scenario.Goal, context, false,
                _ => throw new InvalidOperationException("Declared-settings remote verdict must own the check."), default);
            await RemoteFirstFocusedEvidenceFixture.Event(pending, "remote-first settings verdict returned");
            Assert.Equal("remote-executor=fixture-executor", Assert.Single((await pending).Results).ResultSummary);
        }
        else
        {
            await using var shadow = RemoteFocusedEvidenceShadow.Start(scenario.Overrides, scenario.Clock,
                scenario.Root, [check], scenario.Goal, context, default);
            var pending = shadow.CompleteAsync(localResults);
            await RemoteFirstFocusedEvidenceFixture.Event(pending, "shadow settings verdict recorded");
            await pending;
        }

        var request = Assert.Single(executor.Requests);
        Assert.Same(settings, request.TestSettings);
        Assert.Equal("6", request.TestSettings![Slots]);
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, Assert.Single(scenario.Health()).Outcome);
    }

    private sealed class FocusedFixture : RemoteFirstFocusedEvidenceFixture { }

    [Fact]
    public void RemoteBinding_RequiresExactAppliedSettingsEcho()
    {
        var request = FakeSshRemoteLaneTransport.Request with { TestSettings = Parse("{\"" + Slots + "\":\"6\"}").DeclaredTestSettings };
        var fields = new Dictionary<string, object?>
        {
            ["executorId"] = request.ExecutorId, ["lane"] = request.Lane, ["filterHash"] = request.FilterHash,
            ["commitSha"] = request.VerifyingCommitSha, ["treeSha"] = request.CandidateTreeSha,
            ["mainSha"] = request.MainSha, ["manifestIdentity"] = request.ManifestIdentity
        };
        RemoteLaneResult Result() => new SshRemoteLaneStatus(JsonSerializer.SerializeToElement(fields)).Result(0, []);
        Assert.Equal(RemoteLaneOutcomeCode.BindingMismatchTestSettings, RemoteLaneCoordinator.BindingMismatch(request, Result()));
        fields["testSettings"] = new Dictionary<string, string> { [Slots] = "99" };
        Assert.Equal(RemoteLaneOutcomeCode.BindingMismatchTestSettings, RemoteLaneCoordinator.BindingMismatch(request, Result()));
        fields["testSettings"] = request.TestSettings;
        Assert.Null(RemoteLaneCoordinator.BindingMismatch(request, Result()));
        fields.Remove("testSettings");
        Assert.Null(RemoteLaneCoordinator.BindingMismatch(request with { TestSettings = null }, Result()));
    }

    [Fact]
    public void ExecutorJob_ForwardsSettingsToLaneWithoutCommandLineExpansion()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var output = Path.Combine(sandbox.Root, "job-settings.json");
        var tools = Directory.CreateDirectory(Path.Combine(sandbox.Root, "tools")).FullName;
        File.WriteAllText(Path.Combine(tools, "Invoke-ExecutorLane.ps1"), $$"""
            param($Sha,$Project,$Filter,$Lane,$RunRoot,$AttemptId,$ExecutorId,$FilterHash,$MainSha,$ManifestIdentity,$TestSettings)
            $TestSettings | ConvertTo-Json -Compress | Set-Content -LiteralPath '{{Quote(output)}}'
            """);
        var settings = Parse("""{"MCG_TEST_HOST_CAPACITY_SLOTS":" $value='x'|6 "}""").DeclaredTestSettings;
        var job = Path.Combine(sandbox.Root, "job.json");
        File.WriteAllText(job, JsonSerializer.Serialize(new { sha = "commit", project = "test.csproj", filter = "class", lane = "lane", testSettings = settings }));
        var script = Path.Combine(MtpTestRunnerScriptTests.RepositoryRoot(), "scripts", "executor", "tools", "Invoke-ExecutorJob.ps1");
        var start = sandbox.SandboxPowerShellStartInfo();
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-JobPath");
        start.ArgumentList.Add(job);
        start.ArgumentList.Add("-RunRoot");
        start.ArgumentList.Add(sandbox.Root);
        var result = MtpTestRunnerScriptTests.Run(start);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var observed = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(settings[Slots], observed.RootElement.GetProperty(Slots).GetString());
    }

    [Fact]
    public void RemoteRunner_SealsBeforeApplyingSettingsFileAndPreservesRunnerVariables()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var settingsFile = Path.Combine(sandbox.Root, "settings.json");
        File.WriteAllText(settingsFile, """{"MCG_TEST_HOST_CAPACITY_SLOTS":" 6 \""}""");
        var output = Path.Combine(sandbox.Root, "probe.json");
        var child = Path.Combine(sandbox.Root, "probe.ps1");
        File.WriteAllText(child, $$"""
            @{ slots = $env:MCG_TEST_HOST_CAPACITY_SLOTS; ambient = $env:MCG_TEST_AMBIENT_ONLY;
               repo = $env:MCG_ORCHESTRATOR_REPOSITORY_ROOT; profile = $env:HOME } |
                ConvertTo-Json -Compress | Set-Content -LiteralPath '{{Quote(output)}}'
            """);
        var start = sandbox.SandboxPowerShellStartInfo();
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($$"""
            $ErrorActionPreference='Stop'
            Import-Module '{{Quote(Path.Combine(sandbox.Root, "scripts", "MtpTestRunner.psm1"))}}' -Force
            & (Get-Module MtpTestRunner) {
                $root='{{Quote(sandbox.Root)}}'
                $settings = Read-MtpDeclaredTestSettingsFile -Path '{{Quote(settingsFile)}}'
                $env:MCG_TEST_HOST_CAPACITY_SLOTS='99'
                $env:MCG_TEST_AMBIENT_ONLY='must-strip'
                Set-MtpHermeticEnvironment -RepositoryRoot $root -WritableRoot $root -TestSettings $settings
                $run = Invoke-MtpAppHost -Executable 'powershell.exe' -Arguments @('powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File','{{Quote(child)}}') -OutputLog (Join-Path $root 'probe.log')
                if ($run.ExitCode -ne 0 -or -not $run.ExitConfirmed) { throw 'Probe host failed' }
            }
            """);
        var result = MtpTestRunnerScriptTests.Run(start);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var probe = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(" 6 \"", probe.RootElement.GetProperty("slots").GetString());
        Assert.Null(probe.RootElement.GetProperty("ambient").GetString());
        Assert.Equal(sandbox.Root, probe.RootElement.GetProperty("repo").GetString());
        Assert.Equal(Path.Combine(sandbox.Root, "profile"), probe.RootElement.GetProperty("profile").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"MCG_TEST_HOST_CAPACITY_SLOTS\":\"2026-11-04T01:02:03Z\"}")]
    [InlineData("""{"MCG_TEST_HOST_CAPACITY_SLOTS":" \"MCG_TEST_OTHER\": \"6\" ","MCG_TEST_OTHER":"value"}""")]
    public void RemoteSettingsReader_PreservesLiteralStringMapOnWindowsPowerShell(string json)
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var settingsFile = Path.Combine(sandbox.Root, "settings.json");
        File.WriteAllText(settingsFile, json);
        var start = sandbox.SandboxPowerShellStartInfo();
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($$"""
            $ErrorActionPreference='Stop'
            Import-Module '{{Quote(Path.Combine(sandbox.Root, "scripts", "MtpTestRunner.psm1"))}}' -Force
            & (Get-Module MtpTestRunner) {
                Read-MtpDeclaredTestSettingsFile -Path '{{Quote(settingsFile)}}' | ConvertTo-Json -Compress
            }
            """);
        var result = MtpTestRunnerScriptTests.Run(start);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Equal(JsonSerializer.Deserialize<Dictionary<string, string>>(json),
            JsonSerializer.Deserialize<Dictionary<string, string>>(result.Stdout.Trim()));
    }

    [Theory]
    [InlineData("{\"MCG_TEST_X\":\"6\",\"MCG_TEST_X\":\"7\"}", "MCG_TEST_X")]
    [InlineData("{\"MCG_TEST_X\":\"6\",\"MCG_TEST_\\u0058\":\"7\"}", "MCG_TEST_X")]
    [InlineData("{\"MCG_TEST_X\":6}", "MCG_TEST_X")]
    [InlineData("[{\"MCG_TEST_X\":\"6\"}]", "must be an object")]
    [InlineData("{\"MCG_TEST_X\":{\"nested\":\"6\"}}", "MCG_TEST_X")]
    public void RemoteSettingsReader_RejectsMalformedMapOnWindowsPowerShell(string json, string diagnostic)
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var settingsFile = Path.Combine(sandbox.Root, "settings.json");
        File.WriteAllText(settingsFile, json);
        var start = sandbox.SandboxPowerShellStartInfo();
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($$"""
            $ErrorActionPreference='Stop'
            Import-Module '{{Quote(Path.Combine(sandbox.Root, "scripts", "MtpTestRunner.psm1"))}}' -Force
            & (Get-Module MtpTestRunner) { Read-MtpDeclaredTestSettingsFile -Path '{{Quote(settingsFile)}}' }
            """);
        var result = MtpTestRunnerScriptTests.Run(start);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(diagnostic, result.Stdout + result.Stderr);
    }

    private string[] Arguments(bool mtp) => mtp
        ? ["dotnet", "test.dll", "--results-directory", "results", "--report-trx", "--report-trx-filename", "lane.trx"]
        : ["dotnet", "test", "--results-directory", "results", "--logger", "trx;LogFileName=lane.trx"];
    private static AcceptanceGateEngineSettings Parse(string map) => AcceptanceGateEngineSettings.Parse("{\"checks\":[],\"testSettings\":" + map + "}");
    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    public void Dispose() => Directory.Delete(root, recursive: true);
}
