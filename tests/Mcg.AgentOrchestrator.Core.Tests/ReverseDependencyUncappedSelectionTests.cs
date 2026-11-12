using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each case owns a synthetic source tree; no git, builds or shared environment.
public sealed class ReverseDependencyUncappedSelectionTests
{
    [Xunit.Fact]
    public void ThirtyChangedSources_EqualsUnionOfFiveFileGroups()
    {
        using var repository = new Repository();
        var changed = Enumerable.Range(0, 30).Select(index => $"src/Fixture/Source{index}.cs").ToArray();
        foreach (var index in Enumerable.Range(0, 30))
        {
            repository.Write(changed[index], $"public class Source{index} {{ }}");
            repository.Consumer($"Direct{index}Tests", $"Source{index}");
            if (index % 5 == 0)
            {
                repository.Write($"{Repository.Tests}/Helper{index}.cs",
                    $"public class Helper{index} {{ private Source{index} value; }}");
                repository.Consumer($"Indirect{index}Tests", $"Helper{index}");
            }
        }
        repository.Consumer("UnrelatedTests", "UnrelatedType");

        var combined = ReverseDependencyTestImpactReader.Read(repository.Root, changed);
        Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Resolved, combined.Outcome);
        Xunit.Assert.NotEmpty(combined.TestClassNames);
        var grouped = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var group in changed.Chunk(5))
        {
            var selection = ReverseDependencyTestImpactReader.Read(repository.Root, group);
            Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
            grouped.UnionWith(selection.TestClassNames);
        }
        Xunit.Assert.Equal(grouped, combined.TestClassNames);
        Xunit.Assert.Equal(36, combined.TestClassNames.Count);
        Xunit.Assert.DoesNotContain("UnrelatedTests", combined.TestClassNames);
    }

    [Xunit.Fact]
    public void TwoHundredSymbolFrontier_ResolvesAndReportsLargestFrontier()
    {
        using var repository = new Repository();
        repository.Write(Repository.Source, string.Join("\n", Enumerable.Range(0, 200)
            .Select(index => $"public class Symbol{index} {{ }}")));
        repository.Consumer("ConsumerTests", "Symbol199");

        var selection = ReverseDependencyTestImpactReader.Read(repository.Root, [Repository.Source]);

        Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Xunit.Assert.Equal(["ConsumerTests"], selection.TestClassNames);
        Xunit.Assert.Null(selection.DegradationKind);
        Xunit.Assert.Equal(200, selection.Headroom!.LargestFrontierSymbolCount);
    }

    [Xunit.Theory]
    [Xunit.InlineData(Repository.Source)]
    [Xunit.InlineData("src/Fixture/Other.cs")]
    public void EitherSameNamedDeclaration_SelectsEveryConsumer(string changed)
    {
        using var repository = new Repository();
        repository.Write(Repository.Source, "namespace First; public class SharedName { }");
        repository.Write("src/Fixture/Other.cs", "namespace Second; public class SharedName { }");
        repository.Consumer("FirstConsumerTests", "First.SharedName");
        repository.Consumer("SecondConsumerTests", "Second.SharedName");
        repository.Consumer("UnrelatedTests", "UnrelatedType");

        var selection = ReverseDependencyTestImpactReader.Read(repository.Root, [changed]);

        Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Xunit.Assert.Null(selection.DegradationKind);
        Xunit.Assert.Equal(["FirstConsumerTests", "SecondConsumerTests"], selection.TestClassNames);
    }

    [Xunit.Fact]
    public void EmptyChangedList_StillAbandons()
    {
        using var repository = new Repository();
        var selection = ReverseDependencyTestImpactReader.Read(repository.Root, []);
        Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, selection.Outcome);
        Xunit.Assert.Equal(ReverseDependencyDegradationKind.ChangedSourceFileCount, selection.DegradationKind);
        Xunit.Assert.Equal("Reverse-dependency selection requires at least one changed source file.", selection.Reason);
    }

    [Xunit.Fact]
    public void OneThousandAndOneSelectedClasses_PreservesCompleteSelection()
    {
        using var repository = new Repository();
        repository.Write(Repository.Source, "public class ChangedSource { }");
        repository.Write($"{Repository.Tests}/Consumers.cs", string.Join("\n", Enumerable.Range(0, 1001)
            .Select(index => $"public class Consumer{index}Tests {{ private ChangedSource value; " +
                "[Xunit.Fact] public void Runs() { } }")));

        var selection = ReverseDependencyTestImpactReader.Read(repository.Root, [Repository.Source]);

        Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Xunit.Assert.Null(selection.DegradationKind);
        Xunit.Assert.Equal(Enumerable.Range(0, 1001).Select(index => $"Consumer{index}Tests").Order(StringComparer.Ordinal),
            selection.TestClassNames);
        Xunit.Assert.Equal(1001, selection.Headroom!.SelectedTestClassCount);
    }

    [Xunit.Fact]
    public void EightThousandAndOneIndexedFiles_StillAbandonsAtDefaultCap()
    {
        using var repository = new Repository();
        foreach (var index in Enumerable.Range(0, 8001))
            repository.Write($"src/Fixture/Source{index}.cs", "");

        var selection = ReverseDependencyTestImpactReader.Read(repository.Root, ["src/Fixture/Source0.cs"]);

        Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, selection.Outcome);
        Xunit.Assert.Equal(ReverseDependencyDegradationKind.IndexedSourceBound, selection.DegradationKind);
        Xunit.Assert.Equal("Reverse-dependency indexing exceeded the 8000-source-file bound.", selection.Reason);
        Xunit.Assert.Equal(8001, selection.Headroom!.IndexedSourceFileCount);
    }

    private sealed class Repository : IDisposable
    {
        internal const string Tests = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests";
        internal const string Source = "src/Fixture/Changed.cs";
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("mcg-uncapped-selection-");
        internal string Root => _directory.FullName;

        internal Repository()
        {
            Write("src/Fixture/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write($"{Tests}/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                "<ProjectReference Include=\"../../src/Fixture/Fixture.csproj\" />" +
                "</ItemGroup></Project>");
        }

        internal void Consumer(string name, string symbol) => Write($"{Tests}/{name}.cs",
            $"public class {name} {{ private {symbol} value; [Xunit.Fact] public void Runs() {{ }} }}");

        internal void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
