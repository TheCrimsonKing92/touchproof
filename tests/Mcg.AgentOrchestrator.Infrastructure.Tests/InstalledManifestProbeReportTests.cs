using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

// Pure substituted probes; no tool processes or shared environment mutation.
public sealed class InstalledManifestProbeReportTests
{
    [Fact]
    public void Lines_MixedOutcomes_ReportsFailuresInOrderAndContinuesAfterException()
    {
        var tools = new EnvironmentToolRequirement[]
        {
            new("passing", "ok", ["--version"]),
            new("missing", "absent", ["version", "--verbose"]),
            new("throwing", "broken", []),
            new("nonzero", "fails", ["--version"]),
            new("slow", "waits", [])
        };
        var runner = new Runner(tool => tool.Name switch
        {
            "passing" => Result(tool, true, "pass"),
            "missing" => Result(tool, false, "not-found"),
            "throwing" => Task.FromException<EnvironmentProbeResult>(new IOException("probe failed")),
            "nonzero" => Result(tool, false, "failed"),
            _ => Result(tool, false, "timeout")
        });
        var root = Path.GetTempPath();

        var lines = InstalledManifestProbeReport.Lines(Manifest(tools), root, runner);

        Assert.Equal(new[]
        {
            "ProbeUnresolved missing probe=absent version --verbose outcome=not-found",
            "ProbeUnresolved throwing probe=broken outcome=error",
            "ProbeUnresolved nonzero probe=fails --version outcome=failed",
            "ProbeUnresolved slow probe=waits outcome=timeout"
        }, lines);
        Assert.Equal(tools.Select(tool => tool.Name), runner.Calls.Select(call => call.Tool.Name));
        var environment = runner.Calls[0].Environment;
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), environment.Variables["PATH"]);
        Assert.All(runner.Calls, call =>
        {
            Assert.Same(environment, call.Environment);
            Assert.Equal(root, call.Directory);
        });
    }

    [Fact]
    public void Lines_EmbeddedNewlines_EscapesDisplayAndPreservesLiteralArguments()
    {
        var runner = new Runner(tool => Result(tool, false, "not-found"));

        var lines = InstalledManifestProbeReport.Lines(Manifest([
            new("tool\r\nname", "my\r\ncommand", ["first argument", "second\r\nargument"])
        ]), Path.GetTempPath(), runner);

        Assert.Equal("ProbeUnresolved tool\\r\\nname probe=my\\r\\ncommand first argument second\\r\\nargument outcome=not-found",
            Assert.Single(lines));
        var call = Assert.Single(runner.Calls);
        Assert.Equal("my\r\ncommand", call.Tool.Command);
        Assert.Equal(new[] { "first argument", "second\r\nargument" }, call.Tool.Arguments);
    }

    [Fact]
    public void Lines_AllPassing_ExecutesEveryProbeWithoutReportLines()
    {
        var runner = new Runner(tool => Result(tool, true, "pass"));

        var lines = InstalledManifestProbeReport.Lines(Manifest([
            new("one", "first", []), new("two", "second", [])
        ]), Path.GetTempPath(), runner);

        Assert.Empty(lines);
        Assert.Equal(new[] { "one", "two" }, runner.Calls.Select(call => call.Tool.Name));
    }

    [Theory]
    [InlineData("{\"environmentContract\":{\"version\":1,\"tools\":[]}}")]
    [InlineData("not json")]
    [InlineData("{\"environmentContract\":{\"version\":2,\"tools\":[]}}")]
    public void Lines_EmptyOrUnparseableContract_DoesNotProbeOrReport(string manifest)
    {
        var runner = new Runner(tool => Result(tool, false, "not-found"));

        Assert.Empty(InstalledManifestProbeReport.Lines(manifest, Path.GetTempPath(), runner));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Lines_CancelledProbe_PropagatesCancellationWithoutProbingRemainingTools()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new Runner(_ => Task.FromCanceled<EnvironmentProbeResult>(cancellation.Token));

        var exception = Assert.ThrowsAny<OperationCanceledException>(() => InstalledManifestProbeReport.Lines(
            Manifest([new("one", "first", []), new("two", "second", [])]),
            Path.GetTempPath(), runner, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(cancellation.Token, Assert.Single(runner.Calls).CancellationToken);
    }

    private static string Manifest(IReadOnlyList<EnvironmentToolRequirement> tools) =>
        JsonSerializer.Serialize(new { environmentContract = new EnvironmentContract(1, tools).ToJson() });

    private static Task<EnvironmentProbeResult> Result(EnvironmentToolRequirement tool, bool passed, string outcome) =>
        Task.FromResult(new EnvironmentProbeResult(tool, passed, passed ? 0 : null, "", outcome));

    private sealed class Runner(Func<EnvironmentToolRequirement, Task<EnvironmentProbeResult>> probe) : IEnvironmentProbeRunner
    {
        internal List<(EnvironmentToolRequirement Tool, ConductorChildEnvironment Environment, string Directory,
            CancellationToken CancellationToken)> Calls { get; } = [];

        public Task<EnvironmentProbeResult> RunAsync(EnvironmentToolRequirement tool,
            ConductorChildEnvironment environment, string workingDirectory, CancellationToken cancellationToken = default)
        {
            Calls.Add((tool, environment, workingDirectory, cancellationToken));
            return probe(tool);
        }
    }
}
