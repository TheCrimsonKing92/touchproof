using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every row owns its repository, worktrees, stores and bisection runner.
public sealed class MergeTrainStackFrameAttributionTests : AcceptanceCohortWorkflowTests
{
    private const string Subject = "src/Mcg.AgentOrchestrator.Infrastructure/Thrower.cs";
    private const string TestFile = "tests/UnchangedTests.cs";
    private const string Identity = "Ns.UnchangedTests.Fails";
    private static readonly string[] MemberPaths =
    [
        "tests/Mcg.AgentOrchestrator.Core.Tests/OldestTests.cs",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MiddleTests.cs",
        "tests/Mcg.AgentOrchestrator.Dashboard.Tests/NewestTests.cs"
    ];

    [Theory]
    [InlineData("unique", "stack-frame")]
    [InlineData("unchanged", "bisection")]
    [InlineData("conflict", "ownership")]
    [InlineData("agreement", "ownership")]
    [InlineData("assertion", "bisection")]
    public void MergeTrainPrefixBisectionTrainTestsStackFrame(string mode, string source)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            Write(repo, TestFile, "namespace Ns; public class UnchangedTests { public void Fails() { } }");
            Write(repo, Subject, "public class Thrower\n{\n}\n");
            RunGit(repo, "add", "src", "tests");
            RunGit(repo, "commit", "-m", "Add unchanged test and throw site");
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 3).Select(index =>
            {
                var goal = CreateCompletedGoal(kernel, $"Stack member {index}", repo);
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
                CreateWorktreeCandidate(repo, goal.Id, MemberPaths[index],
                    $"namespace Ns; public class {Path.GetFileNameWithoutExtension(MemberPaths[index])} {{ }}");
                if (index == 0)
                    CreateWorktreeCandidate(repo, goal.Id, Subject,
                        "public class Thrower\n{\n    public int Value => throw new System.Exception();\n}\n");
                if ((index == 1 && mode == "conflict") || (index == 0 && mode == "agreement"))
                    CreateWorktreeCandidate(repo, goal.Id, TestFile,
                        "namespace Ns; public class UnchangedTests { public void Fails() { } } // member owns test");
                return goal;
            }).ToArray();
            var stack = "   at System.Framework.Invoke()\n" +
                (mode == "assertion" ? $"   at Ns.UnchangedTests.Fails() in /_/repo/{TestFile}:line 1\n" : "") +
                $"   at Thrower.get_Value() in /_/foreign/{Subject}:line {(mode == "unchanged" ? 1 : 3)}\n" +
                $"   at MiddleTests.Invoke() in /_/foreign/{MemberPaths[1]}:line 1";
            var trx = Red(repo, stack);
            var red = new AcceptanceVerificationResult(false, false, 1, "train failed",
                Checks: [new AcceptanceCheckResult("train", false, 1, "train failed")], TestResultPaths: [trx]);
            var verifier = new SequenceAcceptanceVerifier([red, new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("remainder", true, 0, null)],
                TestResultPaths: [WritePassingTrx(repo, "pass.trx")])]);
            var calls = 0;
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks)
            {
                MergeTrainBisectionRunner = (prefix, _, tests, _) =>
                {
                    calls++;
                    return MergeTrainPrefixBisectionTests.Execution(tests, prefix.Count > 0, prefix.Count);
                }
            };
            var selection = ProjectTrainSelection(driver, goals);
            if (mode != "conflict" && mode != "agreement")
                Assert.All(selection.Members, member => Assert.DoesNotContain(TestFile, member.LandingPaths));
            var result = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive, gateOnly: true);
            var ejection = Assert.Single(result.Ejections);
            Assert.Equal(goals[mode == "conflict" ? 1 : 0].Id, ejection.GoalId);
            Assert.NotEqual(goals[2].Id, ejection.GoalId);
            Assert.Equal(MergeTrainEjectionReason.RedAttributedMember, ejection.Reason);
            if (source == "bisection")
            {
                Assert.True(calls > 0);
                Assert.Contains("bisection=", ejection.Detail);
            }
            else Assert.Equal(0, calls);
            var events = File.ReadAllLines(OrchestratorWorkspace.ForDirectory(repo).ConductEventsLogPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record => record.EventKind == "train-attribution").ToArray();
            Assert.NotEmpty(events);
            Assert.All(events, record => Assert.Contains("attribution=" + source, record.Detail));
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally { DeleteDirectory(repo); }
    }

    private static void Write(string repo, string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repo, path))!);
        File.WriteAllText(Path.Combine(repo, path), value);
    }

    [Theory]
    [InlineData("shifted", true)]
    [InlineData("two-owners", false)]
    [InlineData("unmapped", false)]
    [InlineData("unchanged", false)]
    [InlineData("partial-trx", false)]
    [InlineData("missing-head", false)]
    [InlineData("rerun-green", true)]
    [InlineData("confirmed-apparatus", true)]
    [InlineData("missing-confirmed-failure", false)]
    public void LineHistoryMapsOffsetsAndDeclinesIncompleteOrSharedOwnership(string mode, bool expected)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            Write(repo, Subject, "public class Thrower\n{\n}\n");
            RunGit(repo, "add", Subject);
            RunGit(repo, "commit", "-m", "Base source");
            var main = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
            Write(repo, Subject, "public class Thrower\n{\n    public int Value => throw new System.Exception();\n}\n");
            RunGit(repo, "add", Subject);
            RunGit(repo, "commit", "-m", "First member adds throw line");
            var first = Binding('a', RunGitOutput(repo, "rev-parse", "HEAD").Trim(), Subject);
            var updated = mode == "two-owners"
                ? "public class Thrower\n{\n    public int Value => throw new System.InvalidOperationException();\n}\n"
                : "// inserted by another member\npublic class Thrower\n{\n    public int Value => throw new System.Exception();\n}\n";
            Write(repo, Subject, updated);
            RunGit(repo, "add", Subject);
            RunGit(repo, "commit", "-m", "Second member changes source");
            var second = Binding('b', RunGitOutput(repo, "rev-parse", "HEAD").Trim(), Subject);
            Write(repo, "docs/third.md", "Third member");
            RunGit(repo, "add", "docs/third.md");
            RunGit(repo, "commit", "-m", "Third member");
            var third = Binding('c', RunGitOutput(repo, "rev-parse", "HEAD").Trim(), "docs/third.md");
            if (mode == "unmapped")
            {
                Write(repo, Subject, updated.Replace("new System.Exception()", "new System.InvalidOperationException()", StringComparison.Ordinal));
                RunGit(repo, "add", Subject);
                RunGit(repo, "commit", "-m", "Unmapped post-member change");
            }
            if (mode == "missing-head") second = second with { RebasedTrainHeadRevision = null };
            var line = mode == "unchanged" ? 2 : mode == "two-owners" ? 3 : 4;
            var trx = Red(repo, $"   at Thrower.get_Value() in Z:/foreign/{Subject}:line {line}");
            IReadOnlySet<string>? confirmed = null;
            if (mode is "rerun-green" or "missing-confirmed-failure" or "confirmed-apparatus")
            {
                var document = XDocument.Load(trx);
                var results = document.Descendants().Single(element => element.Name.LocalName == "Results");
                results.Add(new XElement(results.Name.Namespace + "UnitTestResult",
                    new XAttribute("testName", "Ns.RerunGreenTests.Fails"), new XAttribute("outcome", "Failed"),
                    mode == "confirmed-apparatus" ? new XElement(results.Name.Namespace + "Output",
                        new XElement(results.Name.Namespace + "ErrorInfo", new XElement(results.Name.Namespace + "Message",
                            "DotnetBuildSlotsBusyException: Stable dotnet build slots busy"))) : null));
                document.Save(trx);
                confirmed = new HashSet<string>(StringComparer.Ordinal)
                    { mode == "rerun-green" ? Identity : "Ns.MissingTests.Fails" };
                if (mode == "confirmed-apparatus")
                    confirmed = new HashSet<string>(StringComparer.Ordinal) { Identity, "Ns.RerunGreenTests.Fails" };
            }
            string[] paths = mode == "partial-trx" ? [trx, Path.Combine(repo, "missing.trx")] : [trx];
            var result = MergeTrainStackFrameAttribution.TryAttribute(paths, repo, [first, second, third], main, out var subjects, confirmed);
            Assert.Equal(expected ? first.GoalId : (GoalId?)null, result?.GoalId);
            if (expected) Assert.Contains(Subject + ":4", Assert.Single(subjects));
            else Assert.Empty(subjects);
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StackReachConflict_PreservesExistingImpactThenPathOrder(bool impact)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            RepositoryTestReachTests.RepositoryFixture.Populate(repo);
            var subject = impact ? "src/Mcg.AgentOrchestrator.Core/StackOnly.cs" : Subject;
            const string test = RepositoryTestReachTests.RepositoryFixture.TestDirectory + "/UnchangedTests.cs";
            const string other = RepositoryTestReachTests.RepositoryFixture.Other;
            Write(repo, subject, "public class StackOnly\n{\n}\n");
            Write(repo, test, "namespace Ns; public class UnchangedTests { private OtherSource other; " +
                "[Xunit.Fact] public void Fails() { } }");
            RunGit(repo, "add", "src", "tests");
            RunGit(repo, "commit", "-m", "Base for conflicting attribution evidence");
            var main = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
            Write(repo, subject, "public class StackOnly\n{\n    public int Value => 1;\n}\n");
            RunGit(repo, "add", subject);
            RunGit(repo, "commit", "-m", "First member changes stack line");
            var first = Binding('a', RunGitOutput(repo, "rev-parse", "HEAD").Trim(), subject);
            Write(repo, other, "public class OtherSource { public int Value => 1; }");
            RunGit(repo, "add", other);
            RunGit(repo, "commit", "-m", "Second member changes reached source");
            var second = Binding('b', RunGitOutput(repo, "rev-parse", "HEAD").Trim(), other);
            Write(repo, "docs/third.md", "Third member");
            RunGit(repo, "add", "docs/third.md");
            RunGit(repo, "commit", "-m", "Third member");
            var third = Binding('c', RunGitOutput(repo, "rev-parse", "HEAD").Trim(), "docs/third.md");
            MergeTrainMemberBinding[] members = [first, second, third];
            string[] paths = [Red(repo, $"   at StackOnly.get_Value() in /_/repo/{subject}:line 3")];
            var receipt = new MergeTrainReceipt("red", MergeTrainIdentity.Create(
                members, main, third.RebasedTrainHeadRevision!, "manifest"),
                MergeTrainGateOutcome.Failed, DateTimeOffset.UtcNow, 0, ["train"], 1, paths);

            Assert.Null(MergeTrainRedAttribution.TryAttribute(paths, repo, members, out _));
            Assert.Same(first, MergeTrainStackFrameAttribution.TryAttribute(paths, repo, members, main, out _));
            Assert.Same(second, MergeTrainReachAttribution.TryAttribute(paths, repo, members, out _));
            Assert.Equal(impact ? second.GoalId : (GoalId?)null,
                MergeTrainImpactAttribution.TryAttribute(paths, repo, members, out _)?.GoalId);
            Assert.Same(first, MergeTrainPathAttribution.TryAttribute(paths, repo, members, out _));
            Assert.Same(impact ? second : first, MergeTrainFallbackAttribution.TryAttribute(
                paths, repo, members, 0, out var subjects, out var source, receipt));
            Assert.Equal(impact ? "impact" : "path", source);
            Assert.NotEmpty(subjects);
        }
        finally { DeleteDirectory(repo); }
    }

    private static MergeTrainMemberBinding Binding(char id, string revision, string path) => new(
        new GoalId(new string(id, 32)), revision, revision, [path], [$"member:{id}"], ChangeRiskTier.Behavior,
        ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected", revision);

    private static string Red(string repo, string stack)
    {
        var path = Path.Combine(repo, "red.trx");
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results",
            new XElement(ns + "UnitTestResult", new XAttribute("testName", Identity), new XAttribute("outcome", "Failed"),
                new XElement(ns + "Output", new XElement(ns + "ErrorInfo",
                    new XElement(ns + "Message", "train failure"), new XElement(ns + "StackTrace", stack))))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", "Failed"), new XElement(ns + "Counters",
                new XAttribute("total", 1), new XAttribute("executed", 1), new XAttribute("passed", 0), new XAttribute("failed", 1))))).Save(path);
        return path;
    }
}
