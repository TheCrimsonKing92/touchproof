using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private files, an injected process seam and no environment writes.
public sealed class CrosswalkBatchRenderedLengthTests
{
    [Fact]
    public async Task LargeSelection_CmdRendering_PreservesIdsWithinLimit()
    {
        var directory = Directory.CreateTempSubdirectory("crosswalk-batches-");
        try
        {
            var root = directory.FullName;
            var binary = Path.Combine(root, "crosswalk.exe");
            File.WriteAllText(binary, "fixture");
            var resolver = new CrosswalkExecutableResolver(binary, runVersionAsync: (_, _) =>
                Task.FromResult(new WorkerProcessRunResult(0, "v", "")));
            var calls = new List<string[]>();
            var client = new CrosswalkClient(resolver, (request, _) =>
            {
                calls.Add(request.ToArgumentVector());
                return Task.FromResult(new WorkerProcessRunResult(0, CrosswalkAcceptanceFixture.Report(
                    request.Arguments.Skip(2).Chunk(2).Select(pair => (pair[1], "passed")).ToArray()), ""));
            });
            var ids = Enumerable.Range(0, 300).Select(i => $"suite's \"case {i}\" " + new string('x', 50)).ToArray();
            var all = new[] { binary, "run", root }.Concat(ids.SelectMany(id => new[] { "--id", id })).ToArray();
            Assert.True(CrosswalkClient.CmdRenderedLength(all) > CrosswalkAcceptanceCheck.RunCommandLineLimit);
            var outcome = await new CrosswalkBatchedRunner(client, CrosswalkAcceptanceCheck.RunCommandLineLimit)
                .RunAsync(root, resolver.Resolve().Identity!, ids, () => TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.Equal("detected", outcome.Status);
            Assert.True(calls.Count > 1);
            Assert.Equal(ids, calls.SelectMany(call => call.Skip(3).Chunk(2).Select(pair => pair[1])));
            Assert.All(calls, call =>
            {
                var rendered = string.Join(' ', call.Select(argument => "\"" + argument.Replace("\"", "\\\"") + "\""));
                Assert.Equal(rendered.Length, CrosswalkClient.CmdRenderedLength(call));
                Assert.True(rendered.Length < CrosswalkAcceptanceCheck.RunCommandLineLimit);
                if (OperatingSystem.IsWindows())
                {
                    var info = AcceptanceShellProcessStartInfo.Build(call, root, new string('o', 260), new string('e', 260),
                        true, null, false, out _);
                    Assert.True(info.Arguments.Length <= 8191);
                }
            });
            calls.Clear();
            var oversized = await new CrosswalkBatchedRunner(client, CrosswalkAcceptanceCheck.RunCommandLineLimit)
                .RunAsync(root, resolver.Resolve().Identity!, [new string('x', 7500)], () => TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.Equal("start-failed", oversized.Status);
            Assert.Equal(CrosswalkDiagnosticCategory.StartFailed, Assert.Single(oversized.Diagnostics).Category);
            Assert.Empty(calls);
        }
        finally { directory.Delete(recursive: true); }
    }
}
