using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml.Linq;
using Xunit;

// Parallel-safe: each invocation owns its corpus, child environment, and temporary git repository.
[Collection("IsolatedProcessSpawning")]
public sealed class TestCostRankingScriptTests
{
    private static readonly DateTime Epoch = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MixedCorpus_RejectsWholeReceiptBeforeAggregationOrLatestFinishSelection()
    {
        using var fixture = new RankingFixture();
        fixture.Write("valid.trx", Receipt("2026-01-05T00:00:00Z", Result("Alpha", "Passed", "00:00:02")));
        fixture.Write("late-invalid.trx", Receipt("2026-02-05T00:00:00Z",
            Result("PartialMustNotAppear", "Failed", "01:00:00"), Result("Bad", "Passed", "bad")));
        fixture.Write("broken.trx", "<broken");

        var result = fixture.Run();

        AssertSuccess(result);
        Assert.Equal(ExpectedAlpha, Normalize(result.Stdout));
        Assert.Equal("Receipts: examined=3 out-of-window=0 malformed=2 qualified=1\n" +
            "Exclusions: metadataOutOfWindow=0 finishOutOfWindow=0 malformedXml=1 missingFinish=0 " +
            "unparseableTimestamps=0 missingResults=0 missingTestName=0 unknownOutcome=0 invalidDuration=1",
            Normalize(result.Stderr));
    }

    [Theory]
    [InlineData("xml", "malformedXml")]
    [InlineData("finish-missing", "missingFinish")]
    [InlineData("finish-invalid", "unparseableTimestamps")]
    [InlineData("results", "missingResults")]
    [InlineData("name", "missingTestName")]
    [InlineData("outcome", "unknownOutcome")]
    [InlineData("duration", "invalidDuration")]
    [InlineData("duration-missing", "invalidDuration")]
    [InlineData("duration-negative", "invalidDuration")]
    public void MalformedOnly_IsExplicitSuccessfulEmptyResult(string kind, string reason)
    {
        using var fixture = new RankingFixture();
        var validResult = Result("Alpha", "Passed", "00:00:02");
        var xml = kind switch
        {
            "xml" => "<broken",
            "finish-missing" => "<TestRun><Times/><Results/></TestRun>",
            "finish-invalid" => Receipt("invalid", validResult),
            "results" => Receipt("2026-01-05T00:00:00Z"),
            "name" => Receipt("2026-01-05T00:00:00Z", Result("", "Passed", "00:00:01")),
            "outcome" => Receipt("2026-01-05T00:00:00Z", Result("Alpha", "Unknown", "00:00:01")),
            "duration" => Receipt("2026-01-05T00:00:00Z", Result("Alpha", "Passed", "invalid")),
            "duration-missing" => Receipt("2026-01-05T00:00:00Z", Result("Alpha", "Passed", "")),
            "duration-negative" => Receipt("2026-01-05T00:00:00Z", Result("Alpha", "Passed", "-00:00:01")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        fixture.Write("invalid.trx", xml);

        var result = fixture.Run();

        AssertSuccess(result);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("Receipts: examined=1 out-of-window=0 malformed=1 qualified=0", result.Stderr);
        Assert.Contains($"{reason}=1", result.Stderr);
        Assert.Contains("No qualified TRX receipts.", result.Stderr);
        Assert.Equal(3, result.Stderr.Split("=1", StringSplitOptions.None).Length - 1); // examined, malformed, primary reason
    }

    [Fact]
    public void MetadataWindow_PrecedesOpening_AndIncludesExactLowerBoundary()
    {
        using var fixture = new RankingFixture();
        fixture.Write("newest.trx", Receipt("2026-01-05T00:00:00Z", Result("Alpha", "Passed", "00:00:02")));
        fixture.Write("boundary.trx", "<broken", Epoch.AddHours(-24));
        var old = fixture.Write("old.trx", "<broken", Epoch.AddHours(-24).AddSeconds(-1));
        // Opening this receipt would fail, proving metadata rejection happens before content access.
        using var locked = File.Open(old, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = fixture.Run();

        AssertSuccess(result);
        Assert.Equal(ExpectedAlpha, Normalize(result.Stdout));
        Assert.Contains("examined=3 out-of-window=1 malformed=1 qualified=1", result.Stderr);
        Assert.Contains("metadataOutOfWindow=1 finishOutOfWindow=0 malformedXml=1", result.Stderr);
    }

    [Fact]
    public void RetimestampedHistory_RetainsEmbeddedWindowAndBoundary()
    {
        using var fixture = new RankingFixture();
        fixture.Write("newest.trx", Receipt("2026-01-05T00:00:00Z", Result("Alpha", "Passed", "00:00:02")));
        fixture.Write("boundary.trx", Receipt("2026-01-04T00:00:00Z", Result("Boundary", "Passed", "00:00:01")));
        fixture.Write("historical.trx", Receipt("2026-01-03T23:59:59Z", Result("TooOld", "Failed", "01:00:00")));

        var result = fixture.Run();

        AssertSuccess(result);
        Assert.Contains("\"Boundary\"", result.Stdout);
        Assert.DoesNotContain("TooOld", result.Stdout);
        Assert.Contains("examined=3 out-of-window=1 malformed=0 qualified=2", result.Stderr);
        Assert.Contains("metadataOutOfWindow=0 finishOutOfWindow=1", result.Stderr);
    }

    [Fact]
    public void EmptyRoot_ReportsZeroCountsWithoutCsv()
    {
        using var fixture = new RankingFixture();
        var result = fixture.Run();
        AssertSuccess(result);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("examined=0 out-of-window=0 malformed=0 qualified=0", result.Stderr);
        Assert.Contains("No qualified TRX receipts.", result.Stderr);
    }

    [Theory]
    [InlineData("-WindowHours", "0")]
    [InlineData("-Top", "-1")]
    [InlineData("-InputRoot", "missing-root")]
    public void InvalidInvocation_RemainsNonzero(string parameter, string value)
    {
        using var fixture = new RankingFixture();
        var result = fixture.Run(parameter, value);
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(result.Stderr);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.DoesNotContain("No qualified TRX receipts.", result.Stderr);
        if (parameter == "-InputRoot") Assert.Contains("TRX input root does not exist:", result.Stderr);
    }

    [Fact]
    public void EligibleReceiptAccessFailure_IsNotReportedAsMalformedSuccess()
    {
        using var fixture = new RankingFixture();
        var path = fixture.Write("locked.trx", "<broken");
        using var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = fixture.Run();
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(result.Stderr);
        Assert.DoesNotContain("malformedXml=1", result.Stderr);
        Assert.DoesNotContain("No qualified TRX receipts.", result.Stderr);
    }

    [Fact]
    public void ExistingFixtureContract_IsByteStable_AndTopLimitsBothRankings()
    {
        using var fixture = new RankingFixture();
        fixture.CopyFixtures();
        var contract = fixture.RunScript("Test-TestCostRanking.ps1");
        AssertSuccess(contract);
        Assert.Contains("PASS: recursive TRX cost/failure ranking", contract.Stdout);

        fixture.InputRoot = Path.Combine(fixture.Root, "scripts", "fixtures", "test-cost-ranking");
        var first = fixture.Run("-Top", "1");
        var second = fixture.Run("-Top", "1");
        AssertSuccess(first);
        AssertSuccess(second);
        Assert.Equal(first.Stdout, second.Stdout);
        Assert.Equal(first.Stderr, second.Stderr);
        Assert.Equal("\"ranking_kind\",\"rank\",\"test_name\",\"execution_count\",\"failure_count\",\"failure_rate\",\"total_seconds\",\"maximum_seconds\"\n" +
            "\"cost\",\"1\",\"Alpha\",\"2\",\"1\",\"0.500000\",\"5.000000\",\"3.000000\"\n" +
            "\"failure\",\"1\",\"Alpha\",\"2\",\"1\",\"0.500000\",\"5.000000\",\"3.000000\"", Normalize(first.Stdout));
    }

    [Fact]
    public void LinkedWorktreeExecution_PreservesStatusInventoryHashesAndMtimes()
    {
        using var fixture = new RankingFixture();
        fixture.Write("valid.trx", Receipt("2026-01-05T00:00:00Z", Result("Alpha", "Passed", "00:00:02")));
        fixture.Write("broken.trx", "<broken");
        fixture.Git("init", "--quiet");
        fixture.Git("add", ".");
        fixture.Git("-c", "user.name=Ranking Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "fixture");
        var linked = Path.Combine(fixture.Root, "linked");
        fixture.Git("worktree", "add", "--quiet", "--detach", linked, "HEAD");
        Assert.True(File.Exists(Path.Combine(linked, ".git")), "Must execute in a linked worktree.");
        foreach (var path in Directory.EnumerateFiles(linked, "*.trx", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(path, Epoch);
        var beforeStatus = fixture.GitAt(linked, "status", "--porcelain=v1", "--untracked-files=all");
        Assert.Equal(string.Empty, beforeStatus);
        var before = Snapshot(linked);

        var result = fixture.RunAt(linked);

        AssertSuccess(result);
        Assert.Contains("examined=2 out-of-window=0 malformed=1 qualified=1", result.Stderr);
        Assert.Equal(ExpectedAlpha, Normalize(result.Stdout));
        Assert.Equal(beforeStatus, fixture.GitAt(linked, "status", "--porcelain=v1", "--untracked-files=all"));
        Assert.Equal(before, Snapshot(linked));
    }

    private const string ExpectedAlpha = "\"ranking_kind\",\"rank\",\"test_name\",\"execution_count\",\"failure_count\",\"failure_rate\",\"total_seconds\",\"maximum_seconds\"\n" +
        "\"cost\",\"1\",\"Alpha\",\"1\",\"0\",\"0.000000\",\"2.000000\",\"2.000000\"\n" +
        "\"failure\",\"1\",\"Alpha\",\"1\",\"0\",\"0.000000\",\"2.000000\",\"2.000000\"";

    private static string Normalize(string value) => value.Replace("\r\n", "\n").TrimEnd('\r', '\n');
    private static void AssertSuccess(CommandResult result) => Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
    private static string[] Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => $"{Path.GetRelativePath(root, path)}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}|{File.GetLastWriteTimeUtc(path).Ticks}").ToArray();
    private static XElement Result(string name, string outcome, string duration) => new("UnitTestResult",
        new XAttribute("testName", name), new XAttribute("outcome", outcome), new XAttribute("duration", duration));
    private static string Receipt(string finish, params XElement[] results) => new XElement("TestRun",
        new XElement("Times", new XAttribute("finish", finish)), new XElement("Results", results)).ToString();

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);

    private sealed class RankingFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ranking-" + Guid.NewGuid().ToString("N"));
        public string InputRoot { get; set; }
        private readonly string _sourceRoot = VerifiedRepositoryRoot.Find();

        public RankingFixture()
        {
            InputRoot = Path.Combine(Root, "receipts");
            Directory.CreateDirectory(InputRoot);
            Directory.CreateDirectory(Path.Combine(Root, "scripts"));
            foreach (var script in new[] { "Get-TestCostRanking.ps1", "Test-TestCostRanking.ps1" })
                File.Copy(Path.Combine(_sourceRoot, "scripts", script), Path.Combine(Root, "scripts", script));
        }

        public string Write(string name, string content, DateTime? mtime = null)
        {
            var path = Path.Combine(InputRoot, name);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, mtime ?? Epoch);
            return path;
        }

        public void CopyFixtures()
        {
            foreach (var name in new[] { "test-cost-ranking", "test-cost-ranking-invalid" })
            {
                var source = Path.Combine(_sourceRoot, "scripts", "fixtures", name);
                foreach (var path in Directory.EnumerateFiles(source, "*.trx", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(Root, "scripts", "fixtures", name, Path.GetRelativePath(source, path));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(path, target);
                    File.SetLastWriteTimeUtc(target, Epoch);
                }
            }
        }

        public CommandResult Run(params string[] extra) => RunScript("Get-TestCostRanking.ps1",
            extra.Contains("-InputRoot", StringComparer.OrdinalIgnoreCase) ? extra : ["-InputRoot", InputRoot, .. extra]);
        public CommandResult RunAt(string root) => Execute(root, "pwsh", ["-NoProfile", "-File", Path.Combine(root, "scripts", "Get-TestCostRanking.ps1"), "-InputRoot", Path.Combine(root, "receipts")]);
        public CommandResult RunScript(string name, params string[] arguments) => Execute(Root, "pwsh", ["-NoProfile", "-File", Path.Combine(Root, "scripts", name), .. arguments]);
        public string Git(params string[] arguments) => GitAt(Root, arguments);
        public string GitAt(string root, params string[] arguments)
        {
            var result = Execute(root, "git", arguments);
            AssertSuccess(result);
            return result.Stdout;
        }

        private static CommandResult Execute(string root, string executable, string[] arguments)
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment.Clear();
            foreach (var name in new[] { "PATH", "SYSTEMROOT", "WINDIR" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            start.Environment["TEMP"] = Path.GetTempPath();
            start.Environment["TMP"] = Path.GetTempPath();
            start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not launch {executable}");
            var output = ScriptFixtureProcessRunner.AssertExited(process, $"{executable} did not finish the ranking fixture command.");
            return new(process.ExitCode, output.Stdout, output.Stderr);
        }

        public void Dispose()
        {
            foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
    }
}
