using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: patterns live in a unique test directory; source reads use the verified candidate root.
public sealed class AdoptedBuildLeakScannerTests
{
    internal static string GenericPath => Path.Combine(VerifiedRepositoryRoot.Find(), AdoptedBuildLeakScanner.GenericPatternPath);
    internal static string Token => "gh" + "p_" + new string('a', 36);

    [Fact]
    public void AddedFamilies_ReturnOnlyFileAndLine()
    {
        using var patterns = new PatternFixture();
        var samples = new[] { Token, "github_pa" + "t_" + new string('b', 60),
            "s" + "k-" + new string('c', 40), "AKI" + "A" + new string('D', 16),
            "ASI" + "A" + new string('E', 16), "-----BEGIN RSA PRIVATE KE" + "Y-----",
            "xox" + "b-" + new string('1', 24), "xox" + "p-" + new string('2', 24),
            patterns.Home, patterns.Mail };
        var result = patterns.Scanner.Scan(Diff("sample.txt", samples, 12));
        Assert.Equal(samples.Length, result.AddedLines);
        Assert.Equal(Enumerable.Range(12, samples.Length), result.Matches.Select(hit => hit.Line));
        Assert.All(result.Matches, hit => Assert.Equal("sample.txt", hit.File));
        var report = JsonSerializer.Serialize(result);
        Assert.All(samples, sample => Assert.DoesNotContain(sample, report));
        Assert.Equal(new[] { "File", "Line" }, typeof(AdoptedBuildLeakScanner.Location)
            .GetProperties().Select(property => property.Name).OrderBy(name => name));
    }

    [Fact]
    public void RemovedAndContextMatches_AreIgnoredAndLineNumbersAdvance()
    {
        using var patterns = new PatternFixture();
        var result = patterns.Scanner.Scan($"diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n" +
            $"@@ -5,2 +5,2 @@\n-{Token}\n {Token}\n+{Token}\n");
        Assert.Equal(new AdoptedBuildLeakScanner.Location("a.txt", 6), Assert.Single(result.Matches));
        Assert.Equal(1, result.AddedLines);
        Assert.Empty(patterns.Scanner.Scan(Diff("clean.txt", ["clean", "task-description"])).Matches);
        Assert.Empty(patterns.Scanner.Scan($"--- a/a\n+++ b/a\n@@ -1,1 +1,0 @@\n-{Token}\n").Matches);
    }

    [Fact]
    public void PatternFileAndTestSources_DoNotMatchThemselves()
    {
        using var patterns = new PatternFixture();
        var root = VerifiedRepositoryRoot.Find();
        var paths = new[] { GenericPath,
            Path.Combine(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AdoptedBuildLeakScannerTests.cs"),
            Path.Combine(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AdoptedBuildPushServiceTests.cs"),
            Path.Combine(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AdoptedBuildPushFixture.cs") };
        foreach (var path in paths)
            Assert.Empty(patterns.Scanner.Scan(Diff(Path.GetFileName(path), File.ReadAllLines(path))).Matches);
    }

    [Fact]
    public void MissingOwnerFile_IsExplicitAndMalformedPatternsFail()
    {
        using var patterns = new PatternFixture();
        File.Delete(patterns.OwnerPath);
        Assert.False(new AdoptedBuildLeakScanner(GenericPath, patterns.OwnerPath).OwnerPatternsPresent);
        File.WriteAllText(patterns.OwnerPath, "{}");
        Assert.Throws<FormatException>(() => new AdoptedBuildLeakScanner(GenericPath, patterns.OwnerPath));
    }

    internal static string Diff(string path, IEnumerable<string> lines, int start = 1)
    {
        var values = lines.ToArray();
        return $"diff --git a/{path} b/{path}\n--- /dev/null\n+++ b/{path}\n@@ -0,0 +{start},{values.Length} @@\n" +
            string.Concat(values.Select(line => "+" + line + "\n"));
    }

    private sealed class PatternFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("adopted-patterns-").FullName;
        internal string Home { get; } = "C:" + "\\Users\\" + "fixture-person";
        internal string Mail { get; } = "fixture-person" + "@example.test";
        internal string OwnerPath => AdoptedBuildLeakScanner.OwnerPatternPath(_root);
        internal AdoptedBuildLeakScanner Scanner { get; }
        internal PatternFixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OwnerPath)!);
            File.WriteAllText(OwnerPath, JsonSerializer.Serialize(new Dictionary<string, string>
                { ["home"] = Regex.Escape(Home), ["mail"] = Regex.Escape(Mail) }));
            Scanner = new(GenericPath, OwnerPath);
            Assert.True(Scanner.OwnerPatternsPresent);
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
