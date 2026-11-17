using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceImpactChangedLineSeedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneTypeEdited_UsesMergeBaseAndCurrentSnapshot(bool advanceIntegration)
    {
        using var repository = new Repository();
        repository.Git("checkout", "-b", "candidate");
        repository.Write(Repository.Source, Repository.SourceText.Replace("=> 1", "=> 10", StringComparison.Ordinal));
        if (advanceIntegration)
        {
            repository.Commit();
            repository.Git("checkout", "main");
            repository.Write(Repository.Source, Repository.SourceText.Replace("=> 2", "=> 20", StringComparison.Ordinal));
            repository.Commit();
            repository.Git("checkout", "candidate");
        }

        var pathOnly = repository.Read();
        var selected = repository.Read("main");

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, pathOnly.Selection.Outcome);
        Assert.Equal(["ChangedTypeTests", "UntouchedTypeTests"], pathOnly.Selection.TestClassNames);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Selection.Outcome);
        Assert.Equal(["ChangedTypeTests"], selected.Selection.TestClassNames);
    }

    [Fact]
    public void UnresolvableBase_PreservesPathOnlySelection()
    {
        using var repository = new Repository();
        repository.Write(Repository.Source, Repository.SourceText.Replace("=> 1", "=> 10", StringComparison.Ordinal));

        var selected = repository.Read("missing-integration-branch");

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Selection.Outcome);
        Assert.Equal(["ChangedTypeTests", "UntouchedTypeTests"], selected.Selection.TestClassNames);
    }

    [Fact]
    public void DeletionAndAddition_PreserveWholeFileSelection()
    {
        using var repository = new Repository();
        repository.Write(Repository.Source, Repository.SourceText.Replace("=> 1", "=> 10", StringComparison.Ordinal)
            .Replace("    public int Value => 2;\n", "", StringComparison.Ordinal));

        var selected = repository.Read("main");

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Selection.Outcome);
        Assert.Equal(["ChangedTypeTests", "UntouchedTypeTests"], selected.Selection.TestClassNames);
    }

    private sealed class Repository : IDisposable
    {
        internal const string Source = "src/Fixture/Types.cs";
        internal const string SourceText = "internal class ChangedType\n{\n    public int Value => 1;\n}\n\ninternal class UntouchedType\n{\n    public int Value => 2;\n}\n";
        private const string TestProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        private string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-acceptance-line-seed-{Guid.NewGuid():N}");
        private AcceptanceGateEngineSettings Settings { get; }
        internal Repository()
        {
            Write("Mcg.AgentOrchestrator.sln", "");
            Write("src/Fixture/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write(TestProject,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../../src/Fixture/Fixture.csproj\" /></ItemGroup></Project>");
            const string manifest = """
                { "engine": { "laneTestSourceRoots": ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"] },
                  "checks": [{ "name": "infra", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj" }] }
                """;
            Write("config/acceptance-manifest.json", manifest);
            Settings = AcceptanceGateEngineSettings.Parse(manifest);
            Write(Source, SourceText);
            foreach (var name in new[] { "ChangedType", "UntouchedType" })
                Write($"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/{name}Tests.cs",
                    $"public class {name}Tests {{ [Fact] public void Works() {{ _ = new {name}(); }} }}");
            Git("init");
            Git("symbolic-ref", "HEAD", "refs/heads/main");
            Commit();
        }
        internal AcceptanceSelectedExecutionInput Read(string? branch = null) =>
            AcceptanceImpactSelectionReader.ReadLive(Root, [Source], Settings, branch);
        internal void Git(params string[] arguments)
        {
            var result = GitCli.Run(Root, ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                "-c", "core.autocrlf=false", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=.no-hooks", .. arguments]);
            Assert.True(result.ExitCode == 0, result.Output);
        }
        internal void Commit()
        {
            Git("add", ".");
            Git("commit", "-m", "Fixture snapshot");
        }
        internal void Write(string path, string text)
        {
            var absolute = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, text);
        }
        public void Dispose()
        {
            foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
    }
}
