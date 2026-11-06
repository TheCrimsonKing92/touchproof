using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private files and injected processes.
public sealed class CrosswalkDirectInvocationTests
{
    [Fact]
    public async Task ListAndRun_SeparateArguments_PreserveEveryId()
    {
        var directory = Directory.CreateTempSubdirectory("crosswalk vector '");
        try
        {
            var binary = Path.Combine(directory.FullName, "crosswalk.exe");
            File.WriteAllText(binary, "fixture");
            var resolver = new CrosswalkExecutableResolver(binary, runVersionAsync: (_, _) =>
                Task.FromResult(new WorkerProcessRunResult(0, "version", "")));
            var requests = new List<WorkerProcessDirectRunRequest>();
            var client = new CrosswalkClient(resolver, (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new WorkerProcessRunResult(0, request.Arguments[0] == "list" ? "[]"
                    : CrosswalkAcceptanceFixture.Report(), ""));
            });
            await client.ListAsync(directory.FullName, TimeSpan.FromSeconds(5), CancellationToken.None);
            await client.RunAsync(directory.FullName, ["it's quoted", "with space", "", "with space"],
                TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal(2, requests.Count);
            Assert.All(requests, request => Assert.Equal(binary, request.FileName));
            Assert.Equal(new[] { "list", directory.FullName }, requests[0].Arguments);
            Assert.Equal(new[] { "run", directory.FullName, "--id", "it's quoted", "--id", "with space",
                "--id", "", "--id", "with space" }, requests[1].Arguments);
            Assert.Null(typeof(WorkerProcessDirectRunRequest).GetProperty("Command"));
        }
        finally { directory.Delete(recursive: true); }
    }
}
