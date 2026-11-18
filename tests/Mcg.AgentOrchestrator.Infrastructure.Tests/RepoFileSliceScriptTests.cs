using System.Diagnostics;
using System.Runtime.CompilerServices;

// Parallel-safe: each test owns its copied real script, temporary files and child process.
public sealed class RepoFileSliceScriptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PositionalWindow_MoreLinesRemain_AppendsExactTruncation(bool finalNewline)
    {
        using var fixture = new Fixture();
        var lines = Enumerable.Range(1, 200).Select(n => $"source {n}").ToArray();
        var path = fixture.WriteSource(lines, finalNewline);

        var result = fixture.Run(path, "5", "10");

        AssertSuccess(result);
        Assert.Equal(Enumerable.Range(5, 10).Select(n => $"{n,5}: source {n}")
            .Append("Truncated: last line shown 14; total lines 200."), OutputLines(result));
    }

    [Theory]
    [InlineData(191, 10)]
    [InlineData(195, 10)]
    [InlineData(201, 10)]
    public void PositionalWindow_ReachesOrPassesEnd_HasNoTruncation(int start, int count)
    {
        using var fixture = new Fixture();
        var path = fixture.WriteSource(Enumerable.Range(1, 200).Select(n => $"source {n}"));

        var result = fixture.Run(path, start.ToString(), count.ToString());

        AssertSuccess(result);
        Assert.Equal(Enumerable.Range(start, Math.Max(0, 201 - start))
            .Select(n => $"{n,5}: source {n}"), OutputLines(result));
        Assert.DoesNotContain("Truncated:", result.Stdout);
    }

    [Fact]
    public void PositionalWindow_EmptyFile_HasNoOutput()
    {
        using var fixture = new Fixture();
        var path = fixture.WriteSource([]);

        var result = fixture.Run(path);

        AssertSuccess(result);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public void Count_Omitted_UsesEightyLineDefault()
    {
        using var fixture = new Fixture();
        var path = fixture.WriteSource(Enumerable.Range(1, 81).Select(n => $"source {n}"));

        var result = fixture.Run(path);

        AssertSuccess(result);
        Assert.Equal(Enumerable.Range(1, 80).Select(n => $"{n,5}: source {n}")
            .Append("Truncated: last line shown 80; total lines 81."), OutputLines(result));
    }

    [Fact]
    public void SymbolName_MultipleMatches_StartsAtFirstLiteralCaseSensitiveMatch()
    {
        using var fixture = new Fixture();
        var path = fixture.WriteSource(["header", "target[0]", "Target0", "prefix Target[0] suffix", "Target[0]"]);

        var result = fixture.Run(path, "5", "1", "-SymbolName", "Target[0]");

        AssertSuccess(result);
        Assert.Equal(["    4: prefix Target[0] suffix", "Truncated: last line shown 4; total lines 5."],
            OutputLines(result));
    }

    [Fact]
    public void SymbolName_NoMatch_FailsWithNameAndNoFallbackOutput()
    {
        using var fixture = new Fixture();
        var path = fixture.WriteSource(["present"]);

        var result = fixture.Run(path, "-SymbolName", "Missing[0]");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Symbol not found: Missing[0]", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void SymbolName_Blank_FailsWithParameterName(string symbol)
    {
        using var fixture = new Fixture();
        var path = fixture.WriteSource(["source"]);

        var result = fixture.Run(path, "-SymbolName", symbol);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SymbolName", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(501)]
    public void Count_WithSymbol_PreservesFiveHundredLineCap(int count)
    {
        using var fixture = new Fixture();
        var path = fixture.WriteSource(["Target"]);

        var result = fixture.Run(path, "-SymbolName", "Target", "-Count", count.ToString());

        if (count == 500)
        {
            AssertSuccess(result);
            Assert.Equal(["    1: Target"], OutputLines(result));
        }
        else
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Count", result.Stderr);
            Assert.Contains("500", result.Stderr);
            Assert.Empty(result.Stdout);
        }
    }

    [Fact]
    public void Path_OutsideRepository_RefusesRead()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Container, "repo-other.txt");
        File.WriteAllText(outside, "outside content");

        var result = fixture.Run(outside);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Refusing to read file outside repository root", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public void CodexGuidance_FileReads_RequiresLocateThenBoundedWindows()
    {
        var guidance = File.ReadAllText(Path.Combine(RepositoryRoot(), "AGENTS.md"));

        Assert.Contains("rg -n \"<name-or-pattern>\" <path>", guidance);
        Assert.Contains("then `.\\scripts\\Invoke-RepoScript.ps1 scripts\\Show-RepoFileSlice.ps1 <path> <line> <count>`", guidance);
        Assert.Contains("already known to contain fewer than 80 lines", guidance);
        Assert.Contains("Truncated: last line shown N; total lines T.", guidance);
        Assert.Contains("request the next window only when the task needs more context", guidance);
    }

    private static void AssertSuccess(TestChildProcessCapture.Result result)
    {
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Empty(result.Stderr);
    }

    private static string[] OutputLines(TestChildProcessCapture.Result result) =>
        result.Stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private sealed class Fixture : IDisposable
    {
        public string Container { get; } = Path.Combine(Path.GetTempPath(), "repo-file-slice-" + Guid.NewGuid().ToString("N"));
        private string Root => Path.Combine(Container, "repo");
        private string Script => Path.Combine(Root, "scripts", "Show-RepoFileSlice.ps1");

        public Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Script)!);
            try
            {
                File.Copy(Path.Combine(RepositoryRoot(), "scripts", "Show-RepoFileSlice.ps1"), Script);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public string WriteSource(IEnumerable<string> lines, bool finalNewline = false)
        {
            File.WriteAllText(Path.Combine(Root, "source.txt"), string.Join("\n", lines) + (finalNewline ? "\n" : ""));
            return "source.txt";
        }

        public TestChildProcessCapture.Result Run(params string[] arguments)
        {
            var info = new ProcessStartInfo
            {
                FileName = TestPowerShell.Executable, WorkingDirectory = Root,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.Environment.Clear();
            foreach (var key in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "HOME", "ProgramFiles", "PSModulePath" })
                if (Environment.GetEnvironmentVariable(key) is { } value) info.Environment[key] = value;
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script }.Concat(arguments))
                info.ArgumentList.Add(argument);
            // Hang-only bound: the owned script process must exit after reading its fixture.
            return TestChildProcessCapture.Run(info, hangBound: TimeSpan.FromSeconds(60));
        }

        public void Dispose() => Directory.Delete(Container, recursive: true);
    }

    private static string RepositoryRoot([CallerFilePath] string source = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var root)) return root;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(source)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root missing.");
    }
}
