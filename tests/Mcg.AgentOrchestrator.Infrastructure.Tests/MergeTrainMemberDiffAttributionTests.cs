using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each test owns its repository, worktrees, receipts and verifier sequence.
public sealed class MergeTrainMemberDiffAttributionTests(CohortCandidateTemplate template)
    : AcceptanceCohortWorkflowTests, IClassFixture<CohortCandidateTemplate>
{
    private const string ClassName = "GitProcessCompletionObserverTests";
    private const string Identity = ClassName + ".ReaderBatchProcess_Exit_ContributesOneObservation";
    private const string SourcePath = "tests/MemberContribution.cs";
    private const string FlakeIdentity = "UnchangedFlakeTests.PassesOnRerun";

    [Fact]
    public void MiddleMemberAddedClassFails_AttributesMiddleAndKeepsNewest() =>
        WithTrain("added", train => AssertMiddleAttributed(train));

    [Fact]
    public void ExistingClassChangedByOneMember_AttributesThatMember() =>
        WithTrain("modified", train => AssertMiddleAttributed(train));

    [Fact]
    public void BoundReceiptWithApparatusFailure_AttributesMiddleMember() =>
        WithTrain("added", train =>
        {
            Assert.True(AcceptanceCohortGateEvidence.HasContentBoundEvidence(
                train.Red.GateTestResultPaths, train.Red.GateEvidenceArtifacts));
            var confirmed = Assert.Single(train.Red.FailedCheckEvidence!, check => check.ExplainedByFatalTrx);
            Assert.Contains(Identity, confirmed.FatalTestIdentities);
            Assert.Contains("ApparatusTests.BuildSlotsBusy", confirmed.FatalTestIdentities);
            var workspace = GoalWorktrees.TryResolve(train.Repo, train.Goals[1].Id)!;
            Assert.Equal(train.Goals[1].Id, MergeTrainRedAttribution.TryAttribute(
                train.Red, workspace, train.Selection.BindMembers(), out var subjects)?.GoalId);
            Assert.Equal(ClassName, Assert.Single(subjects));
            AssertMiddleAttributed(train);
        }, withApparatus: true);

    [Fact]
    public void ClassFileChangedByTwoMembers_RetainsNewestDrop() =>
        WithTrain("shared", train =>
        {
            Assert.Contains(SourcePath, train.Selection.Members[0].LandingPaths);
            Assert.Contains(SourcePath, train.Selection.Members[1].LandingPaths);
            var workspace = GoalWorktrees.TryResolve(train.Repo, train.Goals[1].Id)!;
            Assert.Null(MergeTrainRedAttribution.TryAttribute(train.Red, workspace, train.Selection.BindMembers()));
            var ejection = Assert.Single(train.Result.Ejections);
            Assert.Equal(train.Goals[2].Id, ejection.GoalId);
            Assert.Equal(MergeTrainEjectionReason.RedNewestMember, ejection.Reason);
            // Passing survivors implicate the dropped newest member, even when ownership is ambiguous.
            var newest = train.Selection.Members[2];
            Assert.Equal(ConductorAcceptanceCohortAttributedMembers.Key(newest.GoalId, newest.CandidateRevision),
                Assert.Single(train.Driver.ReadTrainImplicatedMemberKeys()));
            Assert.Equal(train.Goals.Take(2).Select(goal => goal.Id), train.Result.Receipt!.Identity.Members.Select(member => member.GoalId));
        });

    [Fact]
    public void AttributedMiddleMember_RoutesDeveloperWithTrxAndSurvivorsReceiptExcludesIt() =>
        WithTrain("added", train =>
        {
            AssertMiddleAttributed(train);
            var culprit = train.Kernel.GetGoal(train.Goals[1].Id);
            Assert.Equal(GoalStatus.AcceptanceFailed, culprit.Status);
            var failure = Assert.IsType<AcceptanceFailureSummary>(culprit.LatestAcceptanceFailure);
            Assert.Equal(train.Selection.Members[1].CandidateRevision, failure.BranchHeadSha);
            var check = Assert.Single(failure.CheckAttributions!, item => item.CheckName == ClassName);
            Assert.StartsWith($"merge train RED attributed (ownership) receipt {train.Red.ReceiptId}:", check.Evidence);
            Assert.NotNull(AttributedFailureDeveloperRoute.ReadAcceptanceFailure(failure));
            Assert.Contains(train.Red.GateTestResultPaths, path => AcceptanceTrxFailureReader.Read(path).Failures.Any(row => row.TestName == Identity));
            Assert.All(train.Red.GateTestResultPaths, path => Assert.True(File.Exists(path)));
            Assert.All(new[] { 0, 2 }, index =>
            {
                var survivor = train.Kernel.GetGoal(train.Goals[index].Id);
                Assert.Equal(GoalStatus.Verified, survivor.Status);
                Assert.Null(survivor.LatestAcceptanceFailure);
            });
        });

    [Theory]
    [InlineData("legacy")]
    [InlineData("missing-trx")]
    [InlineData("tampered-verdict")]
    [InlineData("coverage-mismatch")]
    [InlineData("unobserved-identity")]
    public void UntrustedOrIncompleteCoverage_DoesNotDiscardOtherFailures(string scenario) =>
        WithTrain("added", train =>
        {
            var receipt = train.Red;
            if (scenario == "legacy") receipt = receipt with { FailedCheckEvidence = null };
            if (scenario == "missing-trx")
                receipt = receipt with { GateTestResultPaths = [.. receipt.GateTestResultPaths, Path.Combine(train.Repo, "missing.trx")] };
            if (scenario == "tampered-verdict")
                File.AppendAllText(receipt.GateEvidenceArtifacts.Single(artifact => artifact.Kind == "gate-verdict").Path, " ");
            if (scenario == "coverage-mismatch")
                receipt = receipt with { FailedCheckEvidence = [new("other check", true, [Identity])] };
            if (scenario == "unobserved-identity")
            {
                var store = new MergeTrainAcceptanceStore(Path.Combine(train.Repo, "unobserved.db"));
                receipt = store.SaveGateReceipt(receipt with
                {
                    FailedCheckEvidence = receipt.FailedCheckEvidence!.Select(check => check with
                    {
                        FatalTestIdentities = check.ExplainedByFatalTrx ? ["MissingTests.Fails"] : check.FatalTestIdentities
                    }).ToArray()
                });
            }
            var workspace = GoalWorktrees.TryResolve(train.Repo, train.Goals[1].Id)!;
            Assert.Null(MergeTrainRedAttribution.TryAttribute(receipt, workspace, train.Selection.BindMembers()));
        });

    private static void AssertMiddleAttributed(Scenario train)
    {
        Assert.Equal(2, train.Verifier.RunCount);
        Assert.Contains(SourcePath, train.Selection.Members[1].LandingPaths);
        Assert.DoesNotContain(SourcePath, train.Selection.Members[0].LandingPaths);
        Assert.DoesNotContain(SourcePath, train.Selection.Members[2].LandingPaths);
        var ejection = Assert.Single(train.Result.Ejections);
        Assert.Equal(train.Goals[1].Id, ejection.GoalId);
        Assert.Equal(MergeTrainEjectionReason.RedAttributedMember, ejection.Reason);
        var survivors = new[] { train.Selection.Members[0], train.Selection.Members[2] };
        var passed = Assert.IsType<MergeTrainReceipt>(train.Result.Receipt);
        Assert.Equal(MergeTrainGateOutcome.Passed, passed.Outcome);
        Assert.Equal(survivors.Select(member => member.GoalId), passed.Identity.Members.Select(member => member.GoalId));
        Assert.Equal(survivors.SelectMany(member => member.LandingPaths), train.Verifier.ChangedFiles[1]);
        Assert.DoesNotContain(SourcePath, train.Verifier.ChangedFiles[1]);
        var member = train.Selection.Members[1];
        Assert.Equal(ConductorAcceptanceCohortAttributedMembers.Key(member.GoalId, member.CandidateRevision),
            Assert.Single(train.Driver.ReadTrainImplicatedMemberKeys()));
    }

    private void WithTrain(string shape, Action<Scenario> assert, bool withApparatus = false)
    {
        var copy = template.Copy("member-diff-" + shape, 3, (repo, ids) =>
        {
            // Fixture Git ignores global config, while GoalWorktrees inherits the gate's
            // autocrlf=true. Pin both paths to LF before checking out baseline sources.
            RunGit(repo, "config", "core.autocrlf", "false");
            AddAcceptanceManifest(repo);
            Directory.CreateDirectory(Path.Combine(repo, "tests"));
            File.WriteAllText(Path.Combine(repo, "tests/UnchangedFlake.cs"), "public class UnchangedFlakeTests { }");
            if (shape != "added") File.WriteAllText(Path.Combine(repo, SourcePath), Source(false, false));
            RunGit(repo, "add", "tests");
            RunGit(repo, "commit", "-m", "Add baseline classes");
            for (var index = 0; index < ids.Length; index++)
            {
                var path = index == 1 || (shape == "shared" && index == 0) ? SourcePath : $"tests/Other{index}.cs";
                var contents = path == SourcePath ? Source(index == 0, index == 1) : $"public class Other{index}Tests {{ }}";
                _ = CreateWorktreeCandidate(repo, ids[index], path, contents);
            }
        });
        var cleanup = CreateIsolatedCleanupContext(copy.Repo);
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 3).Select(index =>
            {
                var goal = copy.CreateCompletedGoal(kernel, index, $"Member diff {index}");
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                {
                    AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
                });
                return goal;
            }).ToArray();
            var verifier = new SequenceAcceptanceVerifier([Red(copy.Repo, withApparatus), new(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("survivors", true, 0, null)],
                TestResultPaths: [WriteTrx(copy.Repo, "survivors.trx", Identity, true)])]);
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(copy.Repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var selection = new ConductorMergeTrainSelection(goals.Select(goal =>
                Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                    driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive)).Projection).ToArray());
            var result = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive, gateOnly: true);
            var store = new MergeTrainAcceptanceStore(Path.Combine(copy.Repo, ".orchestrator", "merge-train-acceptance.db"));
            var red = Assert.Single(store.ReadReceiptsSince(DateTimeOffset.MinValue), receipt => receipt.Outcome == MergeTrainGateOutcome.Failed);
            // Reproduce the missed ownership step: the full readable history includes
            // an unchanged class that passed its rerun, so raw-row ownership is null.
            var workspace = GoalWorktrees.TryResolve(copy.Repo, goals[1].Id)!;
            Assert.Single(new AcceptanceTestSourceResolver.Request(workspace).ResolveIdentity(Identity));
            Assert.Null(MergeTrainRedAttribution.TryAttribute(red.GateTestResultPaths, workspace, selection.BindMembers(), out _));
            assert(new(copy.Repo, kernel, goals, driver, selection, result, verifier, red));
            AssertNoMergeTrainWorkspaces(copy.Repo);
        }
        finally { DeleteDirectory(copy.Repo); }
    }

    private static string Source(bool first, bool middle) =>
        $"// oldest={first}\npublic class {ClassName} {{ }}\n" +
        string.Join('\n', Enumerable.Range(0, 20).Select(index => $"// unchanged {index}")) + $"\n// middle={middle}\n";

    private static AcceptanceVerificationResult Red(string repo, bool withApparatus)
    {
        var first = WriteTrx(repo, "balanced-3.trx", Identity, false);
        var rerun = WriteTrx(repo, "balanced-3-run-1.trx", Identity, false);
        if (withApparatus)
        {
            AddApparatusFailure(first);
            AddApparatusFailure(rerun);
        }
        var flake = WriteTrx(repo, "balanced-5.trx", FlakeIdentity, false);
        var flakeRerun = WriteTrx(repo, "balanced-5-run-1.trx", FlakeIdentity, true);
        return new(false, false, 1, "train failed", Checks:
        [
            new AcceptanceCheckResult("infrastructure tests: Balanced 3", false, 1, "confirmed failure", TestResultPaths: [first, rerun]),
            new AcceptanceCheckResult("infrastructure tests: Balanced 5", true, 0, "passed rerun", TestResultPaths: [flake, flakeRerun]),
            new AcceptanceCheckResult("infrastructure lane early stop", false, 1, "stopped after confirmed failure"),
            new AcceptanceCheckResult("infrastructure tests", false, 1, "combined result")
        ], TestResultPaths: [first, rerun, flake, flakeRerun]);
    }

    private static void AddApparatusFailure(string path)
    {
        var document = XDocument.Load(path);
        var ns = document.Root!.Name.Namespace;
        document.Descendants(ns + "TestDefinitions").Single().Add(
            new XElement(ns + "UnitTest", new XAttribute("id", "apparatus"),
                new XElement(ns + "TestMethod", new XAttribute("className", "ApparatusTests"),
                    new XAttribute("name", "BuildSlotsBusy"))));
        document.Descendants(ns + "Results").Single().Add(
            new XElement(ns + "UnitTestResult", new XAttribute("testId", "apparatus"),
                new XAttribute("testName", "BuildSlotsBusy"), new XAttribute("outcome", "Failed"),
                new XElement(ns + "Output", new XElement(ns + "ErrorInfo",
                    new XElement(ns + "Message", "DotnetBuildSlotsBusyException: Stable dotnet build slots busy")))));
        var counters = document.Descendants(ns + "Counters").Single();
        foreach (var name in new[] { "total", "executed", "failed" }) counters.SetAttributeValue(name, 2);
        document.Save(path);
        var read = AcceptanceTrxFailureReader.Read(path);
        Assert.Equal(AcceptanceTrxReadStatus.Readable, read.Status);
        Assert.Contains(read.Failures, failure => failure.TestName == "ApparatusTests.BuildSlotsBusy" &&
            ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is not null);
    }

    private static string WriteTrx(string repo, string name, string identity, bool passed)
    {
        var path = Path.Combine(repo, name);
        var split = identity.LastIndexOf('.');
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        new XDocument(new XElement(ns + "TestRun",
            new XElement(ns + "TestDefinitions", new XElement(ns + "UnitTest", new XAttribute("id", "test"),
                new XElement(ns + "TestMethod", new XAttribute("className", identity[..split]), new XAttribute("name", identity[(split + 1)..])))),
            new XElement(ns + "Results", new XElement(ns + "UnitTestResult", new XAttribute("testId", "test"),
                new XAttribute("testName", identity[(split + 1)..]), new XAttribute("outcome", passed ? "Passed" : "Failed"))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", passed ? "Completed" : "Failed"),
                new XElement(ns + "Counters", new XAttribute("total", 1), new XAttribute("executed", 1),
                    new XAttribute("passed", passed ? 1 : 0), new XAttribute("failed", passed ? 0 : 1))))).Save(path);
        Assert.Equal(AcceptanceTrxReadStatus.Readable, AcceptanceTrxFailureReader.Read(path).Status);
        if (!passed) Assert.Equal(identity, Assert.Single(AcceptanceTrxFailureReader.Read(path).Failures).TestName);
        return path;
    }

    private sealed record Scenario(string Repo, AgentOrchestratorKernel Kernel, Goal[] Goals, ConductorDriver Driver,
        ConductorMergeTrainSelection Selection, ConductorMergeTrainRunResult Result, SequenceAcceptanceVerifier Verifier, MergeTrainReceipt Red);
}
