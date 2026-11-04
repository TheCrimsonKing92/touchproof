using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using static AttributedFailureDeveloperRouteTests;

// Every repository, database, artifact root and runner belongs to this fixture.
internal sealed class SoloAttributedFailureRouteFixture : IDisposable
{
    internal const string First = "Sample.Tests.SoloTests.Fails";
    internal const string Long = "Sample.Tests.SoloTests.Long";
    internal const string Project = "tests/Sample.Tests/Sample.Tests.csproj";
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-solo-route", Guid.NewGuid().ToString("N"));
    internal string Repo => Path.Combine(Root, "repo");
    internal AgentOrchestratorKernel Kernel { get; private set; } = new();
    internal Goal Goal { get; private set; }
    internal AcceptanceVerificationSummary Summary { get; private set; } = null!;
    internal SoloAcceptanceConsumptionContext Context { get; private set; } = null!;
    internal List<TaskId> Starts { get; } = [];
    internal int BaselineRuns { get; private set; }
    internal int CandidateRuns { get; private set; }
    internal Action? BeforeStart { get; set; }

    internal SoloAttributedFailureRouteFixture(bool committed = true)
    {
        Directory.CreateDirectory(Path.Combine(Repo, "config"));
        Directory.CreateDirectory(Path.Combine(Repo, "tests", "Sample.Tests"));
        File.WriteAllText(Path.Combine(Repo, Project), "<Project />");
        File.WriteAllText(Path.Combine(Repo, "tests/Sample.Tests/SoloTests.cs"), """
            namespace Sample.Tests;
            public class SoloTests {
                [Xunit.Fact] public void Fails() { }
                [Xunit.Fact] public void Long() { }
            }
            """);
        File.WriteAllText(Path.Combine(Repo, "config/acceptance-manifest.json"), """
            { "version": 1, "engine": {
              "slotCount": 1, "maxConcurrentShards": 1, "enforceStructuralCoverage": false,
              "mtpInvocations": [{
                "project": "tests/Sample.Tests/Sample.Tests.csproj",
                "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                "arguments": ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx", "--report-trx-filename", "{trxFileName}"]
              }] }, "checks": [
              { "name": "solo tests", "type": "dotnet-test", "runner": "mtp",
                "project": "tests/Sample.Tests/Sample.Tests.csproj", "arguments": [] }
            ], "forbiddenChangedPathGlobs": [] }
            """);
        Git("init", "-b", "main");
        Git("config", "user.name", "Solo Route Fixture");
        Git("config", "user.email", "solo@example.invalid");
        Git("add", ".");
        Git("commit", "-m", "Baseline");
        var main = Git("rev-parse", "HEAD");
        Git("checkout", "-b", "candidate");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "candidate");
        Git("add", "README.md");
        Git("commit", "-m", "Candidate");
        Context = new(Git("rev-parse", "HEAD"), main, Git("rev-parse", "HEAD^{tree}"), main);
        Goal = CompletedGoal(Kernel, committed: committed);
    }

    internal async Task Produce()
    {
        var verifier = new GoalAcceptanceVerifier(new GoalAcceptanceVerifierTestOverrides
        {
            BuildStorageRootForTests = new(Path.Combine(Root, "dotnet")),
            ShardPermitRootForTests = Path.Combine(Root, "permits"),
            PartitionVerdictWithinAttemptRerunEnabled = false,
            MainBaselineDiscoveryCacheEnabled = false
        }, (args, directory, _) =>
        {
            var results = Array.IndexOf(args, "--results-directory");
            if (results < 0) return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            var filename = Array.IndexOf(args, "--report-trx-filename");
            Assert.True(filename >= 0, "The fixture must execute the MTP TRX path.");
            var baseline = !Path.GetFullPath(directory).Equals(Path.GetFullPath(Repo), StringComparison.OrdinalIgnoreCase);
            if (baseline) BaselineRuns++; else CandidateRuns++;
            var method = Array.IndexOf(args, "--filter-method");
            string[] names = method < 0 ? [First, Long] : [args[method + 1].Trim('*')];
            Directory.CreateDirectory(args[results + 1]);
            WriteTrx(Path.Combine(args[results + 1], args[filename + 1]), baseline, names);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(baseline ? 0 : 1,
                baseline ? $"Passed! - Failed: 0, Passed: {names.Length}, Total: {names.Length}." :
                    $"Failed! - Failed: {names.Length}, Passed: 0, Total: {names.Length}."));
        });
        await using var owner = AcceptanceExecutionOwners.CreateAttempt(Repo, Goal.Id,
            options: new(RunId: "solo-attempt", ResultsPrefix: Path.Combine(Root, "results"), IntegrationBranch: "main"));
        var verification = await verifier.RunOwnedAsync(Repo, Goal.Id, ["README.md"], null, null, owner);
        Assert.False(verification.Passed);
        Assert.NotNull(verification.SoloFailureReceipt);
        Assert.Null(verification.SoloFailureReceipt.CaptureError);
        Assert.True(BaselineRuns > 0, "The real baseline attribution execution must have run.");
        Assert.True(CandidateRuns > 0);
        var failed = verification.Checks!.Where(check => !check.Passed && !check.Advisory).ToArray();
        Assert.All(failed.SelectMany(check => check.FailingTestAttributions ?? []),
            attr => Assert.Equal(AcceptanceTestFailureOrigin.Introduced, attr.Origin));
        Summary = AcceptanceVerificationSummary.FromVerification(verification, failed, failed.Select(check => check.Name).ToArray(),
            Context.CandidateSha, Context.MainSha, verification.TestResultPaths,
            failed.Select(check => new AcceptanceCheckAttribution(check.Name, AcceptanceFailureOrigin.Introduced,
                "fixture authoritative baseline is green")).ToArray(), null);
    }

    internal ConductorDriver Driver()
    {
        var driver = MakeDriver(
            getFacts: _ => new(true, false, false, false, false, false),
            getPreReviewEvidenceContext: _ => NoPreReviewContext(Context.CandidateSha),
            recordCriterionRetryFeedback: Kernel.RecordCriterionRetryFeedback,
            retryTaskWithCause: (goal, task, message, round, cause) => Kernel.RetryTaskAutomatically(goal, task, message, cause, retryRoundKind: round),
            dispatchAndStart: goal =>
            {
                BeforeStart?.Invoke();
                var task = goal.Tasks.Single();
                Starts.Add(task.Id);
                DispatchTask(Kernel, goal, task);
                var dispatch = task.LastDispatch!;
                Kernel.RecordTaskProcessStarted(goal.Id, task.Id, new(12345, dispatch.Command, dispatch.WorkingDirectory,
                    "stdout", "stderr", "exit", DateTimeOffset.UtcNow, null, null));
                return DispatchStartOutcome.Started();
            });
        driver.SoloAcceptanceContextReader = _ => Context;
        driver.CohortAttributionRevisionReader = _ => new(Context.CandidateSha, Context.MainSha);
        return driver;
    }

    internal void Complete(AcceptanceVerificationSummary? summary = null)
    {
        var driver = Driver();
        driver.BeginTick(Kernel, 1);
        var candidate = ConductorParallelAcceptanceCandidate.Create(Goal, 0, [], Context.CandidateSha, Context.MainSha);
        driver.CompleteParallelLandingAcceptance(candidate, ConductorAutonomyPolicy.Permissive, summary ?? Summary, out _);
        Assert.Equal(GoalStatus.AcceptanceFailed, Goal.Status);
        Assert.Empty(Starts);
        Assert.Equal(0, Goal.AutomaticAcceptanceRetryCount);
    }

    internal async Task Restart()
    {
        var databasePath = Path.Combine(Root, "state.db");
        _ = StateDbMigrations.EnsureUpToDate(databasePath);
        var repository = new SqliteOrchestratorStateRepository(databasePath);
        await repository.SaveAsync(Kernel);
        Kernel = await repository.LoadAsync();
        Goal = Kernel.GetGoal(Goal.Id);
    }

    internal void Record(AcceptanceVerificationSummary summary)
    {
        Kernel.RecordAcceptanceFailure(Goal.Id, summary.FailedChecks!, summary.BranchHeadSha, summary.MainHeadSha,
            summary.CheckAttributions, summary.BaselineAttestation, summary.AttemptId, summary.SoloFailureReceipt);
        if (Goal.Status == GoalStatus.Verified) Kernel.RouteRecordedAcceptanceFailure(Goal.Id, "Fixture solo failure.");
    }

    internal SoloAcceptanceFailureEvidence Proof() => JsonSerializer.Deserialize<SoloAcceptanceFailureEvidence>(
        File.ReadAllBytes(Summary.SoloFailureReceipt!.Evidence.Path))!;

    internal void ReplaceProof(Func<SoloAcceptanceFailureEvidence, SoloAcceptanceFailureEvidence> replace)
    {
        var receipt = Summary.SoloFailureReceipt!;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(replace(Proof()));
        File.WriteAllBytes(receipt.Evidence.Path, bytes);
        Summary = Summary with { SoloFailureReceipt = receipt with
            { Evidence = receipt.Evidence with { Sha256 = SoloAcceptanceFailureEvidenceWriter.Digest(bytes) } } };
    }

    private string Git(params string[] args)
    {
        var result = GitCli.Run(Repo, args);
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }

    private static void WriteTrx(string path, bool passed, string[] names)
    {
        new XDocument(new XElement("TestRun", new XAttribute("id", Guid.NewGuid()),
            new XElement("TestDefinitions", names.Select((name, i) => new XElement("UnitTest", new XAttribute("id", i),
                new XElement("TestMethod", new XAttribute("className", name[..name.LastIndexOf('.')]),
                    new XAttribute("name", name[(name.LastIndexOf('.') + 1)..]))))),
            new XElement("Results", names.Select((name, i) => new XElement("UnitTestResult", new XAttribute("testId", i),
                new XAttribute("testName", name), new XAttribute("outcome", passed ? "Passed" : "Failed"),
                passed ? null : new XElement("Output", new XElement("ErrorInfo", new XElement("Message",
                    name == First ? "exact first line\r\nsecond line must stay out" : new string('x', 2100) + "\nsecond line")))))),
            new XElement("ResultSummary", new XAttribute("outcome", passed ? "Completed" : "Failed"),
                new XElement("Counters", new XAttribute("total", names.Length), new XAttribute("executed", names.Length),
                    new XAttribute("passed", passed ? names.Length : 0), new XAttribute("failed", passed ? 0 : names.Length),
                    new XAttribute("notExecuted", 0))))).Save(path);
    }

    public void Dispose()
    {
        var ownedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mcg-solo-route")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(ownedParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture cleanup escaped its owned root.");
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, recursive: true);
    }
}
