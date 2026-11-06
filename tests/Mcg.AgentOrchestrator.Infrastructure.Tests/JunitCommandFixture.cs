using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;
using TestResult = Mcg.AgentOrchestrator.Infrastructure.TestResult;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: injected processes and clock, per-instance roots and owners, no environment writes.
internal sealed class JunitCommandFixture : GoalAcceptanceVerifierTestBase, IDisposable
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    internal string Root { get; }
    internal string ProjectRoot => Path.GetFullPath(Path.Combine(Root, Project));
    internal string Project { get; }
    internal Check Check { get; }
    internal GoalAcceptanceVerifier Verifier { get; }
    internal List<(string[] Arguments, string Root, CancellationToken Token)> Calls { get; } = [];
    internal List<string> ParsedReports { get; } = [];
    internal Action? WriteReports { get; set; }
    internal int ExitCode { get; set; }
    internal bool TimedOut { get; set; }
    internal Func<string, string>? TransformJson { get; set; }
    internal string? ProjectHomeDirectory { get; set; }

    internal JunitCommandFixture(string project = ".", string[]? reports = null)
    {
        Project = project;
        Check = new() { Name = "declared-test", Type = "junit-command", Command = "suite-runner",
            Arguments = ["--report"], Project = project, Reports = reports ?? ["reports/*.xml"], TimeoutMinutes = 1 };
        Root = CreateManifestWorkspace(JsonSerializer.Serialize(new { version = 1, checks = new[] { Check } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Directory.CreateDirectory(Path.Combine(Root, ".git"));
        Directory.CreateDirectory(ProjectRoot);
        var binary = Path.Combine(Root, "crosswalk.exe");
        File.WriteAllText(binary, "injected binary");
        var resolver = new CrosswalkExecutableResolver(binary, runVersionAsync: (_, _) =>
            Task.FromResult(new WorkerProcessRunResult(0, "fixture-version", "")));
        Verifier = new GoalAcceptanceVerifier(TestOverrides, (args, root, _, token) =>
        {
            Calls.Add((args, root, token));
            if (args[0] == Check.Command)
            {
                WriteReports?.Invoke();
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(ExitCode, "", TimedOut: TimedOut) { StandardOutput = "" });
            }
            Assert.Equal(binary, args[0]);
            Assert.Equal(new[] { "parse", "junit" }, args[1..3]);
            Assert.Equal(new[] { "--tool", "declared-test" }, args[4..]);
            var report = Directory.GetFiles(ProjectRoot, "*.xml", SearchOption.AllDirectories)
                .Single(path => args[3] == path);
            ParsedReports.Add(report);
            var tests = XDocument.Load(report).Descendants("testcase").Select(test => new
            {
                testId = test.Attribute("file")!.Value.Replace('\\', '/') + "::" + test.Attribute("name")!.Value,
                filePath = test.Attribute("file")!.Value,
                name = test.Attribute("name")!.Value,
                status = test.Element("skipped") is not null ? "skipped" : test.Element("failure") is null ? "passed" : "failed",
                message = test.Element("skipped") is { } skipped ? skipped.Attribute("message")?.Value : "original message",
                trace = "original trace", suite = new[] { "suite" }, duration = 12,
                retryAttempts = new[] { new { status = "passed", duration = 2 } },
                extra = new { seed = "seed", outputRef = "output", reportedBy = "fixture" }
            });
            var json = JsonSerializer.Serialize(new { reportFormat = "CTRF", schemaVersion = "mcg-test-result/1",
                results = new { tool = new { name = "declared-test" }, tests } },
                new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, json) { StandardOutput = TransformJson?.Invoke(json) ?? json });
        }, new FrozenClock()) { CrosswalkExecutable = resolver };
    }

    internal string WriteReport(string path, DateTimeOffset modified, params (string File, string Name, bool Fails)[] tests)
    {
        var full = Path.GetFullPath(Path.Combine(ProjectRoot, path));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        new XDocument(new XElement("testsuite", tests.Select(test => new XElement("testcase",
            new XAttribute("file", test.File), new XAttribute("name", test.Name),
            test.Fails ? new XElement("failure", "intentional failure") : null)))).Save(full);
        File.SetLastWriteTimeUtc(full, modified.UtcDateTime);
        return full;
    }

    internal async Task<AcceptanceCheckResult> RunAsync(Check? check = null, CancellationToken token = default)
    {
        await using var owner = Owner(token);
        return (await Verifier.RunInvocationForOwnerTestsAsync(owner, owner.CreateInvocation(Check.Name), check ?? Check, Root, token)).Result;
    }

    internal void SetContract(EnvironmentContract contract, string? directory = null)
    {
        directory ??= Path.Combine(Root, "config");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "acceptance-manifest.json"),
            JsonSerializer.Serialize(new { version = 1, environmentContract = contract.ToJson(), checks = new[] { Check } },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    internal void WriteSkippedReport(string? reason, bool includePassed = false, string name = "skipped")
    {
        var full = WriteReport($"reports/{name}.xml", Now,
            includePassed ? [($"src/{name}.case", name, false), ("src/passed.case", "passed", false)]
                : [($"src/{name}.case", name, false)]);
        var document = XDocument.Load(full);
        document.Descendants("testcase").First().Add(new XElement("skipped",
            reason is null ? null : new XAttribute("message", reason)));
        document.Save(full);
        File.SetLastWriteTimeUtc(full, Now.UtcDateTime);
    }

    internal AcceptanceAttemptExecutionOwner Owner(CancellationToken token = default) => new(
        new("junit-attempt", "goal", Root, "tree", "main", "commit",
            Path.Combine(Root, ".orchestrator", "acceptance-gate-attempts", "junit", "attempt-current"), null, Environment.ProcessId, null),
        new AcceptanceGateEngineSettings(), token, new(ProjectHomeDirectory: ProjectHomeDirectory));

    internal static IReadOnlyList<TestResult> ReadRows(AcceptanceCheckResult check)
    {
        var read = new NormalizedTestResultSource().Read(Assert.Single(check.NormalizedTestResultPaths!));
        Assert.Equal(TestResultReadStatus.Readable, read.Status);
        return read.Records;
    }

    public void Dispose() => DeleteDirectoryWithRetry(Root);

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
