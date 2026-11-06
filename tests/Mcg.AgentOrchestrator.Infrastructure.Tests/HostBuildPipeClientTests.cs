using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: per-test temp root, GUID pipe names and injected time for busy decisions.
public sealed class HostBuildPipeClientTests : IDisposable
{
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();
    private const string Invocation = "client-test-invocation";

    [Fact]
    public async Task Success_PrintsContractAndSendsProjectSelection()
    {
        var result = await ExchangeAsync(JsonSerializer.Serialize(Response(WorkerBuildStructuralStatus.Passed)));
        Assert.Equal(0, result.Exit);
        Assert.Equal(new[]
        {
            $"worker_build_invocation={Invocation}; outcome=0; host diagnostic",
            "compiler_exit=0; structural_status=Passed; verification_exit=0; structural diagnostic",
            "structural_elapsed_ms=unavailable"
        }, result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Empty(result.Error);
        Assert.Equal(1, result.Request.Version);
        Assert.True(Guid.TryParseExact(result.Request.RequestKey, "N", out _));
        Assert.Equal(new[] { "src/Feature.csproj", "tests/Feature.Tests.csproj" }, result.Request.Projects);
        Assert.Equal("Release", result.Request.Configuration);
    }

    [Fact]
    public async Task CompilerFailure_PrintsNumericWireOutcomeAndCompilerExit()
    {
        var response = Response(WorkerBuildStructuralStatus.Unavailable) with
        {
            Outcome = WorkerBuildOutcome.CompilerFailure, CompilerExitCode = 17
        };
        var result = await ExchangeAsync(JsonSerializer.Serialize(response));
        Assert.Equal(1, result.Exit);
        Assert.Contains("outcome=1;", result.Output);
        Assert.Contains("compiler_exit=17; structural_status=Unavailable; verification_exit=1;", result.Output);
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData("Passed", "Passed", 0, "structural diagnostic")]
    [InlineData("NotApplicable", "NotApplicable", 0, "structural diagnostic")]
    [InlineData("Violation", "Violation", 1, "structural diagnostic")]
    [InlineData("Unavailable", "Unavailable", 1, "structural diagnostic")]
    [InlineData("legacy", "Unavailable", 0, "Legacy host response has no structural evidence.")]
    [InlineData("malformed", "Unavailable", 1, "Malformed structural evidence in host response.")]
    [InlineData("wrong-type", "Unavailable", 1, "Malformed structural evidence in host response.")]
    [InlineData("wrong-invocation", "Unavailable", 1, "Malformed structural evidence in host response.")]
    public async Task StructuralEvidence_UsesResponseVerificationRules(
        string scenario, string status, int exit, string diagnostic)
    {
        var response = Response(Enum.TryParse<WorkerBuildStructuralStatus>(scenario, out var parsed)
            ? parsed : WorkerBuildStructuralStatus.Passed);
        if (scenario == "legacy") response = response with { StructuralCheck = null };
        if (scenario == "malformed") response = response with { StructuralCheck = response.StructuralCheck! with { TreeDigest = "" } };
        if (scenario == "wrong-invocation") response = response with { StructuralCheck = response.StructuralCheck! with { InvocationId = "other" } };
        var wire = JsonNode.Parse(JsonSerializer.Serialize(response))!;
        if (scenario == "wrong-type") wire["StructuralCheck"] = "not a check";
        var result = await ExchangeAsync(wire.ToJsonString());
        Assert.Equal(exit, result.Exit);
        Assert.Contains($"compiler_exit=0; structural_status={status}; verification_exit={exit}; {diagnostic}", result.Output);
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData(false, "unavailable")]
    [InlineData(true, "")]
    public async Task LegacyResponse_PreservesAbsentAndNullCompilerExit(bool present, string expected)
    {
        var wire = JsonNode.Parse(JsonSerializer.Serialize(Response(WorkerBuildStructuralStatus.Passed) with { StructuralCheck = null }))!;
        if (present) wire["CompilerExitCode"] = null;
        else wire.AsObject().Remove("CompilerExitCode");
        wire["Outcome"] = "Success"; // Existing script fake hosts serialize string outcomes.
        var result = await ExchangeAsync(wire.ToJsonString());
        Assert.Equal(0, result.Exit);
        Assert.Contains("outcome=Success;", result.Output);
        Assert.Contains($"compiler_exit={expected}; structural_status=Unavailable; verification_exit=0;", result.Output);
    }

    [Fact]
    public void BusyBudget_ReportsFirstWaitAndTerminalBusyUsingInjectedTime()
    {
        var name = WriteDescriptor();
        var time = new ManualTime();
        var opener = new BusyOpener(time);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = new WorkerBuildRequestClient(output, error, time, opener).Run(_root, Request(), 3);
        Assert.Equal(1, exit);
        Assert.Empty(output.ToString());
        Assert.Equal(new[]
        {
            $"worker_build_channel_wait pipe=\"\\\\.\\pipe\\{name}\" busy_timeout_seconds=3",
            $"worker_build_channel_busy pipe=\"\\\\.\\pipe\\{name}\" waited_seconds=3"
        }, error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(1, opener.Opens);
        Assert.Equal(1, opener.Waits);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("pipe")]
    [InlineData("missing")]
    public void UnsupportedDescriptor_RejectsBeforeOpeningPipe(string scenario)
    {
        WriteDescriptor(scenario == "version" ? 2 : 1, scenario == "pipe" ? "untrusted-pipe" : null);
        if (scenario == "missing") File.Delete(Path.Combine(_root, WorkerBuildRequestChannel.DescriptorRelativePath));
        var opener = new BusyOpener(new ManualTime());
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, new WorkerBuildRequestClient(output, error, pipeOpener: opener).Run(_root, Request(), 1));
        Assert.Equal(0, opener.Opens);
        Assert.Empty(output.ToString());
        Assert.False(string.IsNullOrWhiteSpace(error.ToString()));
        if (scenario != "missing") Assert.Equal("Unsupported managed worker build endpoint descriptor." + Environment.NewLine, error.ToString());
    }

    [Fact]
    public async Task HostEof_FailsWithoutClaimingAnInvocation()
    {
        var result = await ExchangeAsync(null);
        Assert.Equal(1, result.Exit);
        Assert.Empty(result.Output);
        Assert.Equal("Managed worker build host closed without a terminal response." + Environment.NewLine, result.Error);
    }

    internal static WorkerBuildResponse Response(WorkerBuildStructuralStatus status) =>
        new(Invocation, WorkerBuildOutcome.Success, "host diagnostic", 0,
            new(status, "structural diagnostic", Invocation, "dispatch", "tree", "scope", null, "policy"));

    private static WorkerBuildRequest Request() =>
        new(1, Guid.NewGuid().ToString("N"), ["src/Feature.csproj", "tests/Feature.Tests.csproj"], "Release");

    private string WriteDescriptor(int version = 1, string? pipe = null)
    {
        var name = pipe ?? "mcg-worker-build-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(_root, ".orchestrator-context"));
        File.WriteAllText(Path.Combine(_root, WorkerBuildRequestChannel.DescriptorRelativePath),
            JsonSerializer.Serialize(new { version, pipe = name }));
        return name;
    }

    private async Task<(int Exit, string Output, string Error, WorkerBuildRequest Request)> ExchangeAsync(string? response)
    {
        var name = WriteDescriptor();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var guard = new CancellationTokenSource(TestHangGuard.HangSafetyBound);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var accept = server.WaitForConnectionAsync(guard.Token);
        var client = Task.Run(() => new WorkerBuildRequestClient(output, error).Run(_root, Request(), 120));
        try
        {
            await accept;
            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
            var request = JsonSerializer.Deserialize<WorkerBuildRequest>((await reader.ReadLineAsync(guard.Token))!)!;
            if (response is not null)
            {
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true);
                await writer.WriteLineAsync(response);
                await writer.FlushAsync(guard.Token);
            }
            else server.Disconnect();
            var exit = await client.WaitAsync(guard.Token);
            return (exit, output.ToString(), error.ToString(), request);
        }
        finally
        {
            server.Dispose();
            await client.WaitAsync(TestHangGuard.HangSafetyBound);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }

    private sealed class BusyOpener(ManualTime time) : WorkerBuildRequestClient.IPipeOpener
    {
        internal int Opens, Waits;
        public Stream? TryOpen(string path, out int error) { Opens++; error = 231; return null; }
        public bool WaitForAvailability(string path, uint milliseconds, out int error)
        {
            Waits++;
            time.Ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
            error = 121;
            return false;
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
