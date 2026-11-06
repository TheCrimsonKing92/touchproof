using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: private roots/owner, frozen clock and injected processes.
public sealed class CrosswalkGateLaunchCountTests : GoalAcceptanceVerifierTestBase
{
    [Fact]
    public async Task CrosswalkAndTwoReports_SharedResolver_UseFiveDirectInvocations()
    {
        var root = CreateManifestWorkspace("""{"version":1,"checks":[]}""");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var binary = Path.Combine(root, "crosswalk.exe");
            File.WriteAllText(binary, "fixture");
            var calls = new ConcurrentQueue<string[]>();
            var resolver = new CrosswalkExecutableResolver(binary, runVersionAsync: (request, _) =>
            {
                calls.Enqueue(request.ToArgumentVector());
                return Task.FromResult(new WorkerProcessRunResult(0, "v", ""));
            });
            var clock = new FrozenClock();
            var reports = new[] { Path.Combine(root, "reports", "one.xml"), Path.Combine(root, "reports", "two.xml") };
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _, _) =>
            {
                if (args[0] == "suite-runner")
                {
                    foreach (var report in reports)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                        File.WriteAllText(report, "<testsuite/>");
                        File.SetLastWriteTimeUtc(report, clock.GetUtcNow().UtcDateTime);
                    }
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "") { StandardOutput = "" });
                }
                calls.Enqueue(args);
                var json = args[1] == "list" ? CrosswalkAcceptanceFixture.List("id")
                    : CrosswalkAcceptanceFixture.Report((args[1] == "parse" ? Path.GetFileName(args[3]) : "id", "passed"));
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "") { StandardOutput = json });
            }, clock) { CrosswalkExecutable = resolver };
            await using var owner = new AcceptanceAttemptExecutionOwner(
                new("count-attempt", "goal", root, "tree", "main", "commit", Path.Combine(root, "receipts", "attempt"),
                    null, Environment.ProcessId, null), new AcceptanceGateEngineSettings());
            var crosswalk = new Check { Name = "crosswalk", Type = "crosswalk-test", Project = ".", TimeoutMinutes = 1 };
            var junit = new Check { Name = "junit", Type = "junit-command", Project = ".", Command = "suite-runner",
                Reports = ["reports/*.xml"], TimeoutMinutes = 1 };
            var first = await verifier.RunInvocationForOwnerTestsAsync(owner, owner.CreateInvocation(crosswalk.Name), crosswalk, root,
                CancellationToken.None);
            var second = await verifier.RunInvocationForOwnerTestsAsync(owner, owner.CreateInvocation(junit.Name), junit, root,
                CancellationToken.None);
            Assert.True(first.Result.Passed, first.Result.OutputTail);
            Assert.True(second.Result.Passed, second.Result.OutputTail);
            var vectors = calls.ToArray();
            Assert.Equal(5, vectors.Length);
            Assert.All(vectors, vector => Assert.Equal(binary, vector[0]));
            Assert.Equal(new[] { binary, "--version" }, Assert.Single(vectors, vector => vector[1] == "--version"));
            Assert.Equal(new[] { binary, "list", root }, Assert.Single(vectors, vector => vector[1] == "list"));
            Assert.Equal(new[] { binary, "run", root }, Assert.Single(vectors, vector => vector[1] == "run"));
            var parses = vectors.Where(vector => vector[1] == "parse").ToArray();
            Assert.Equal(2, parses.Length);
            Assert.Equal(reports.Order(), parses.Select(vector => vector[3]).Order());
            Assert.All(parses, vector => Assert.Equal(new[] { binary, "parse", "junit", vector[3], "--tool", "junit" }, vector));
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => JunitCommandFixture.Now;
    }
}
