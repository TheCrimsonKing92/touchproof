using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class PreTesterReverseDependencySelectionReadCountTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RealSelectionOpensSourcesOnceAndReportsEveryPart(bool balancedLanes, bool coreSource)
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        fixture.Test("SecondConsumerTests", "AppContract");
        fixture.Test("ThirdConsumerTests", "AppContract");
        for (var index = 0; index < 197; index++) fixture.Test($"Filler{index:D3}Tests");
        var changedSource = PreTesterReverseDependencySelectionFixture.Source;
        var changedType = "AppContract";
        if (coreSource)
        {
            changedSource = "src/Mcg.AgentOrchestrator.Core/CoreContract.cs";
            changedType = "CoreContract";
            fixture.Write(changedSource, "public class CoreContract {}");
            foreach (var name in new[] { "ConsumerTests", "SecondConsumerTests", "ThirdConsumerTests" })
                fixture.Test(name, changedType);
        }
        fixture.WithCostHistory(balancedLanes ? """
            , "laneTestSourceRoots": ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"],
              "balancedLanePool": { "laneCount": 2 }
            """ : "");
        // Include multibyte characters and a BOM: physical bytes cannot be inferred from string.Length.
        File.WriteAllText(Path.Combine(fixture.Root, PreTesterReverseDependencySelectionFixture.TestRoot + "Filler000Tests.cs"),
            "// café λ\npublic class Filler000Tests { [Fact] public void Works() {} }", new System.Text.UTF8Encoding(true));
        var sources = Directory.GetFiles(Path.Combine(fixture.Root, PreTesterReverseDependencySelectionFixture.TestRoot), "*.cs");
        Assert.Equal(200, sources.Length);
        Assert.Equal(3, sources.Count(path => File.ReadAllText(path).Contains(changedType, StringComparison.Ordinal)));

        // Two independent invocations also prove that the handoff does not survive a call.
        for (var invocation = 0; invocation < 2; invocation++)
        {
            var reads = new ConcurrentQueue<(string Part, string Path, long Bytes)>();
            var lines = new List<string>();
            var actual = SelectionFileReadProbe.Observe((part, path, bytes) => reads.Enqueue((part, path, bytes)), () =>
                PreTesterReverseDependencySelections.Select(fixture.Root,
                    [changedSource], lines.Add));

            Assert.Equal(["ConsumerTests", "SecondConsumerTests", "ThirdConsumerTests"],
                actual.Selections.Select(item => item.TestClass));
            Assert.Equal(3, actual.Provenance.Count);
            Assert.Empty(lines);
            var resolution = reads.Where(item => item.Part == "resolve-class-names").ToArray();
            Assert.InRange(resolution.Length, 1, 20);
            Assert.DoesNotContain(resolution, item => item.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
            var testSources = reads.Where(item => item.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                Path.GetRelativePath(fixture.Root, item.Path).Replace('\\', '/').StartsWith("tests/", StringComparison.Ordinal)).ToArray();
            var identity = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            Assert.Equal(coreSource ? 201 : 200, testSources.Length);
            Assert.All(testSources.GroupBy(item => Path.GetFullPath(item.Path), identity), group => Assert.Single(group));
            Assert.Equal(200, testSources.Count(item => item.Part == "read-selection"));
            Assert.Equal(coreSource ? 1 : 0, testSources.Count(item => item.Part == "evaluate-cost-rule"));
            Assert.All(reads, item => Assert.Equal(new FileInfo(item.Path).Length, item.Bytes));

            var parts = new[] { "read-selection", "resolve-class-names", "evaluate-cost-rule" };
            Assert.All(reads, item => Assert.Contains(item.Part, parts));
            foreach (var part in parts)
            {
                var measured = reads.Where(item => item.Part == part).ToArray();
                Assert.NotEmpty(measured);
                output.WriteLine($"invocation={invocation} part={part} files_opened={measured.Length} bytes_read={measured.Sum(item => item.Bytes)}");
            }
            Assert.Contains(reads, item => item.Part == "evaluate-cost-rule" && item.Path.EndsWith(".attempt.json"));
            Assert.Contains(reads, item => item.Part == "evaluate-cost-rule" && item.Path.EndsWith(".result.json"));
            Assert.Contains(reads, item => item.Part == "evaluate-cost-rule" && item.Path.EndsWith("acceptance-class-duration-history.json"));
            var byPart = reads.GroupBy(item => item.Part).ToArray();
            Assert.Equal("read-selection", byPart.MaxBy(group => group.Count())!.Key);
            Assert.Equal("read-selection", byPart.MaxBy(group => group.Sum(item => item.Bytes))!.Key);
            output.WriteLine("dominant_by_files=read-selection dominant_by_bytes=read-selection; counts do not attribute CPU");
        }
    }
}
