using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: injected process runner; the real provider is never invoked.
public sealed class ProviderHoldAvailabilityProbeTests
{
    [Theory]
    [InlineData(0, "AVAILABLE", "", false, "available")]
    [InlineData(0, "", "", false, "failed")]
    [InlineData(0, "UNAVAILABLE", "", false, "failed")]
    [InlineData(0, "The prompt says AVAILABLE", "", false, "failed")]
    [InlineData(1, "", "API error (status 402 Payment Required): Grok Build usage balance exhausted", false, "exhausted")]
    [InlineData(1, "", "connection refused", false, "failed")]
    [InlineData(0, "AVAILABLE", "", true, "failed")]
    public async Task MinimalRequest_IsBoundedAndRequiresPositiveEvidence(int exit, string stdout, string stderr, bool timeout, string expected)
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        var row = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        var calls = 0;
        string? directory = null;
        var probe = new ProviderHoldAvailabilityProbe(WorkerProfileCatalog.Default(), (request, _) =>
        {
            calls++;
            directory = request.WorkingDirectory;
            Assert.Equal(OrchestratorTempRoot.GetPurposeDirectory("mcg-provider-check"), Path.GetDirectoryName(directory));
            Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
            Assert.True(request.RequireOwnedProcessGroup);
            Assert.Contains("grok --prompt-file", request.Command);
            Assert.Contains("--max-turns 1", request.Command);
            Assert.DoesNotContain("--max-turns 32", request.Command);
            Assert.Contains("'grok-4.7'", request.Command);
            Assert.Contains("'plan'", request.Command);
            Assert.False(Directory.Exists(Path.Combine(directory, ".git")));
            Assert.Contains("Reply with exactly AVAILABLE", File.ReadAllText(Assert.Single(Directory.GetFiles(directory))));
            return Task.FromResult(new WorkerProcessRunResult(exit, stdout, stderr, timeout));
        });
        var result = await probe.CheckAsync(row.Hold, fixture.Source.Tasks.Single().LastDispatch!, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(1, calls);
        Assert.NotNull(directory);
        Assert.False(Directory.Exists(directory));
    }
}
