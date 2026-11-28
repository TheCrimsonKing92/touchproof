using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerContextRendererDispatchPathTests(Xunit.ITestOutputHelper output) : WorkerDispatchTestSupport
{
    private static readonly DateTimeOffset HistoryFixtureTime = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
    private const string OpenHistoryId = "history-open-000";
    private const string OpenHistoryDescription = "ACTIONABLE_DESCRIPTION: preserve the candidate guard and verify its evidence.";
    private const string HistoryReceiptId = "history-open-receipt";

    public static IEnumerable<object[]> HistoryConsumers() =>
        new[] { AgentRole.Researcher, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer }
            // Typed packages supply history to every role; legacy actionable retry feedback
            // is delivered only to Developer, Tester and Reviewer.
            .SelectMany(role => new[] { true, false }
                .Where(typed => typed || role is AgentRole.Developer or AgentRole.Tester or AgentRole.Reviewer)
                .Select(typed => new object[] { role, typed }));

    [Xunit.Theory]
    [Xunit.MemberData(nameof(HistoryConsumers))]
    public void LongResolvedHistory_EmitsPerRoleDispatcherMeasurements(AgentRole role, bool typed)
    {
        var fixture = CreateHistoryFixture(role);
        var before = JsonSerializer.Serialize(fixture.Kernel.ExportSnapshot());
        var historyBefore = JsonSerializer.Serialize(fixture.Goal.Tasks.Select(task => task.VerificationHistory));
        var projection = ReviewFindingContextProjector.Project(
            fixture.Goal, fixture.Task, fixture.HeadCommit, fixture.HeadCommit);
        using var canonical = JsonDocument.Parse(projection.LedgerBytes);
        Assert.Equal(fixture.HeadCommit, canonical.RootElement.GetProperty("current_candidate_sha").GetString());
        Assert.Equal(OpenHistoryId, Assert.Single(canonical.RootElement.GetProperty("findings").EnumerateArray())
            .GetProperty("stable_id").GetString());
        Assert.Equal(3, projection.RoundBodies.Count);

        var prepared = PrepareHistory(fixture, typed);
        // Read the emitted file, after rendering, source append and input-budget handling.
        // This is one revision's measurement, never a synthetic before/after comparison.
        var prompt = File.ReadAllText(prepared.PromptPath);
        Assert.Null(fixture.Task.LastProcess);
        Assert.Equal(historyBefore, JsonSerializer.Serialize(fixture.Goal.Tasks.Select(task => task.VerificationHistory)));
        AssertActionableHistory(prompt, OpenHistoryId, OpenHistoryDescription);
        AssertRoleInstructions(fixture, prompt);
        var resolvedDescriptions = ResolvedHistoryFindings().Select(finding => finding.Description).ToArray();
        Assert.All(resolvedDescriptions, description => Assert.DoesNotContain(description, prompt, StringComparison.Ordinal));
        var resolvedReferences = ResolvedHistoryFindings().Select(finding => finding.StableId).ToArray();
        var lines = prompt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var resolvedReferenceCharacters = lines
            .Where(line => resolvedReferences.Any(id => line.Contains(id, StringComparison.Ordinal)))
            .Sum(line => line.Length);
        if (role is AgentRole.Tester or AgentRole.Reviewer)
        {
            Assert.Contains("RESOLVED_CARRIED count=64", prompt, StringComparison.Ordinal);
            Assert.True(resolvedReferenceCharacters > 0);
        }
        var actionableCharacters = lines.Where(line =>
                line.Contains(OpenHistoryId, StringComparison.Ordinal) ||
                line.Contains(HistoryReceiptId, StringComparison.Ordinal))
            .Sum(line => line.Length);
        Assert.True(actionableCharacters > 0);
        if (typed)
        {
            var receipt = prepared.Task.LastDispatch!.ContextPackageReceipt!;
            Assert.Equal(prompt.Length, receipt.RenderedPromptCharacters);
            var history = Assert.Single(receipt.Sections, section =>
                section.LogicalIdentity == "goal/review-finding-history.json");
            Assert.Equal(WorkerContextArtifact.Hash(projection.LedgerBytes), history.ContentHash);
            Assert.Equal(Encoding.UTF8.GetString(projection.LedgerBytes),
                RecoverInlineText(prompt, "goal/review-finding-history.json"));
            AssertHistoryFiles(fixture, projection, prompt);
        }
        else
        {
            Assert.Null(prepared.Task.LastDispatch!.ContextPackageReceipt);
            Assert.False(Directory.Exists(Path.Combine(fixture.WorkingDirectory,
                ".orchestrator-context", fixture.Goal.Id.Value, "authoritative", fixture.Task.Id.Value, "typed")));
            // The legacy path must supply actionable content even without a typed ledger reference.
            Assert.Contains("evidence_index:", prompt, StringComparison.Ordinal);
            Assert.Contains("tests/History.Tests.csproj:HistoryGuardTests", prompt, StringComparison.Ordinal);
        }

        output.WriteLine("REVIEW_HISTORY_PROMPT_MEASUREMENT " + JsonSerializer.Serialize(new
        {
            contract = 1,
            role = role.ToString(),
            delivery = typed ? "typed" : "legacy",
            fixture = "64-proved-resolved-one-open-v1",
            inputSha256 = WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes(before)),
            candidate = fixture.HeadCommit,
            workingDirectory = fixture.WorkingDirectory,
            workingDirectoryCharacters = fixture.WorkingDirectory.Length,
            promptCharacters = prompt.Length,
            estimatedTokens = WorkerPromptInputBudget.CountTokens(prompt),
            estimator = "WorkerPromptInputBudget.CountTokens",
            resolvedDescriptionCharacters = resolvedDescriptions.Sum(value => CountOccurrences(prompt, value) * value.Length),
            resolvedReferenceCharacters,
            actionableCharacters,
            inlineLedgerCharacters = typed ? RecoverInlineText(prompt, "goal/review-finding-history.json").Length : 0,
            authoritativeHistoryBodyBytes = projection.RoundBodies.Sum(body => body.Bytes.Length),
            // Carried anchors are measured, not assumed removable: they authorize reopening.
            resolvedReferenceClassification = "carried-anchor-instructions-require-baseline-assessment"
        }));
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Tester)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void ReopenedAndUnprovedFindings_RemainActionableInDispatcher(AgentRole role)
    {
        var fixture = CreateHistoryFixture(role);
        var reopened = ResolvedHistoryFindings()[0] with
        {
            Description = "REOPENED_DESCRIPTION: the candidate guard regressed."
        };
        var unproved = new ReviewFinding("history-unproved", ReviewFindingState.Resolved,
            new ReviewFindingLocation("src/Unproved.cs", "Unproved.Run"), "UNPROVED_DESCRIPTION: closure needs evidence.");
        RecordHistoryRound(fixture, fixture.Task, [reopened, unproved], 4, touched: [reopened.Location]);
        fixture.Kernel.RetryTaskWithAuthoritativeFeedback(fixture.Goal.Id, fixture.Task.Id,
            "Preserve every actionable finding and its evidence.", RetryCause.NewTestFinding, invalidateDownstream: false);

        var prepared = PrepareHistory(fixture, typed: true);
        var prompt = File.ReadAllText(prepared.PromptPath);
        using var ledger = JsonDocument.Parse(RecoverInlineText(prompt, "goal/review-finding-history.json"));
        var findings = ledger.RootElement.GetProperty("findings").EnumerateArray().ToArray();
        Assert.Equal(3, findings.Length);
        foreach (var expected in new[] { reopened, unproved })
        {
            var entry = Assert.Single(findings, finding => finding.GetProperty("stable_id").GetString() == expected.StableId);
            Assert.Equal(expected.State.ToString(), entry.GetProperty("state").GetString());
            Assert.Equal(expected.Description, entry.GetProperty("description").GetString());
            Assert.Contains(expected.Description, prompt, StringComparison.Ordinal);
        }
        AssertActionableHistory(prompt, OpenHistoryId, OpenHistoryDescription);
        AssertRoleInstructions(fixture, prompt);
        var brief = RecoverInlineText(prompt, "brief/current.md");
        var source = fixture.Kernel.BuildTaskBriefSource(fixture.Goal.Id, fixture.Task.Id);
        var convergence = Assert.Single(source.Segments, segment => segment.ConvergenceChangedFiles is not null);
        Assert.Contains("ROUND_DIFF_TOUCHED_ANCHORS", brief, StringComparison.Ordinal);
        Assert.Contains("omission leaves the finding open", brief, StringComparison.Ordinal);
        Assert.All(convergence.Lines.Where(line => !string.IsNullOrWhiteSpace(line)),
            line => Assert.Contains(line, brief, StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ConflictingHistoryIdentity_FailsDispatcherPreparation()
    {
        var fixture = CreateHistoryFixture(AgentRole.Developer);
        var reviewer = fixture.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var request = new FindingEvidenceRequest([new FindingEvidenceSelection("tests/History.Tests.csproj", "HistoryGuardTests")]);
        RecordHistoryRound(fixture, reviewer, [], 4,
            receipts: [new FindingEvidenceReceipt(HistoryReceiptId, fixture.HeadCommit, request, true, true, "CONFLICTING_RECEIPT_BODY")]);

        var error = Assert.Throws<WorkerContextPreparationException>(() => PrepareHistory(fixture, typed: true));

        Assert.Equal("evidence-identity-conflict", error.Reason);
        Assert.Equal("goal/review-finding-history.json", error.Identity.Value);
        Assert.Null(fixture.Task.LastDispatch);
    }

    [Xunit.Fact]
    public void ContractRepair_DispatcherPreservesCompleteEnvelope()
    {
        var fixture = CreateDispatchFixture(DispatchPath.CompactRepair);
        var projection = ReviewFindingContextProjector.Project(
            fixture.Goal, fixture.Task, fixture.HeadCommit, fixture.HeadCommit);
        Assert.NotNull(projection.ContractRepairEnvelopeBytes);

        var prepared = PrepareHistory(fixture, typed: true, dispatchedAt: DateTimeOffset.UtcNow);
        var prompt = File.ReadAllText(prepared.PromptPath);
        var delivered = RecoverInlineText(prompt, "task/review-contract-repair-envelope.json");

        Assert.Equal(Encoding.UTF8.GetString(projection.ContractRepairEnvelopeBytes), delivered);
        using var envelope = JsonDocument.Parse(delivered);
        Assert.Equal(fixture.HeadCommit, envelope.RootElement.GetProperty("candidate_sha").GetString());
        Assert.Equal("stable-render", Assert.Single(envelope.RootElement.GetProperty("findings").EnumerateArray())
            .GetProperty("stable_id").GetString());
        Assert.NotEmpty(envelope.RootElement.GetProperty("required_output_schema").EnumerateArray());
        Assert.NotEmpty(envelope.RootElement.GetProperty("immutable_references").EnumerateArray());
        Assert.Null(fixture.Task.LastProcess);
    }

    [Xunit.Theory]
    [Xunit.InlineData("drop-actionable")]
    [Xunit.InlineData("corrupt-evidence")]
    [Xunit.InlineData("repeat-resolved")]
    [Xunit.InlineData("drop-role-instructions")]
    public void HistoryPreservationGuard_RejectsDamagedDispatcherPrompt(string mutation)
    {
        var fixture = CreateHistoryFixture(AgentRole.Developer);
        var prepared = PrepareHistory(fixture, typed: true);
        var prompt = File.ReadAllText(prepared.PromptPath);
        var resolvedDescription = ResolvedHistoryFindings()[0].Description;
        AssertHistoryPreservation(fixture, prompt, resolvedDescription);
        var damaged = mutation switch
        {
            "drop-actionable" => prompt.Replace(OpenHistoryId, "removed-finding", StringComparison.Ordinal),
            "corrupt-evidence" => prompt.Replace(HistoryReceiptId, "corrupted-receipt", StringComparison.Ordinal),
            "repeat-resolved" => prompt + Environment.NewLine + resolvedDescription,
            "drop-role-instructions" => prompt.Replace("## Developer Requirements", "removed-instructions", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        Assert.NotEqual(prompt, damaged);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertHistoryPreservation(fixture, damaged, resolvedDescription));
        output.WriteLine($"REVIEW_HISTORY_NEGATIVE_CONTROL mutation={mutation}; boundary=emitted-dispatcher-prompt; original=accepted; damaged=rejected");
    }

    private static void AssertHistoryPreservation(DispatchFixture fixture, string prompt, string resolvedDescription)
    {
        AssertActionableHistory(prompt, OpenHistoryId, OpenHistoryDescription);
        AssertRoleInstructions(fixture, prompt);
        Assert.DoesNotContain(resolvedDescription, prompt, StringComparison.Ordinal);
    }

    private static void AssertActionableHistory(string prompt, string stableId, string description)
    {
        Assert.Contains(stableId, prompt, StringComparison.Ordinal);
        Assert.Contains(description, prompt, StringComparison.Ordinal);
        Assert.Contains(HistoryReceiptId, prompt, StringComparison.Ordinal);
        Assert.Contains("Preserve every actionable finding and its evidence.", prompt, StringComparison.Ordinal);
    }

    private static DispatchFixture CreateHistoryFixture(AgentRole role)
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        var headCommit = InitializeRepository(workingDirectory, deterministic: true);
        var kernel = new AgentOrchestratorKernel(new HistoryClock());
        var task = new TaskSpec(new TaskId("history-consumer"), "Inspect the candidate guard.", role);
        var historyTask = role is AgentRole.Tester or AgentRole.Reviewer
            ? task
            : new TaskSpec(new TaskId("history-reviewer"), "Inspect the candidate guard.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal(new GoalId("review-history-measurement"),
            "Preserve the candidate guard and actionable evidence.", historyTask == task ? [task] : [task, historyTask]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var fixture = new DispatchFixture(root, workingDirectory, kernel, goal, task, headCommit);
        var resolved = ResolvedHistoryFindings();
        RecordHistoryRound(fixture, historyTask, resolved, 1);
        RecordHistoryRound(fixture, historyTask,
            resolved.Select(finding => finding with { State = ReviewFindingState.Resolved }).ToArray(), 2,
            touched: resolved.Select(finding => finding.Location).ToArray());
        var request = new FindingEvidenceRequest([new FindingEvidenceSelection("tests/History.Tests.csproj", "HistoryGuardTests")]);
        var open = new ReviewFinding(OpenHistoryId, ReviewFindingState.Open,
            new ReviewFindingLocation("src/Guard.cs", "Guard.Run"), OpenHistoryDescription,
            FindingSeverity.Blocking, FindingCategory.Correctness, request, new FindingEvidenceOutcome(true, HistoryReceiptId));
        RecordHistoryRound(fixture, historyTask, [open], 3,
            receipts: [new FindingEvidenceReceipt(HistoryReceiptId, headCommit, request, true, true, "OPEN_EVIDENCE_BODY")]);
        kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, task.Id, "Preserve every actionable finding and its evidence.",
            RetryCause.NewTestFinding, invalidateDownstream: false);

        // Assert replayed state before measuring delivery or submitting a reopening round.
        // The merged ledger alone cannot prove that the earlier worker rounds were replayed.
        var source = kernel.BuildTaskBriefSource(goal.Id, historyTask.Id);
        var convergence = Assert.Single(source.Segments, segment => segment.ConvergenceChangedFiles is not null);
        Assert.Contains("RESOLVED_CARRIED count=64", convergence.Lines);
        Assert.All(resolved, finding => Assert.Contains(
            $"- {finding.StableId} | severity={finding.Severity.ToString().ToLowerInvariant()} | {finding.Location} | carry forward; do not re-review unless this exact anchor was touched.",
            convergence.Lines));
        return fixture;
    }

    private static ReviewFinding[] ResolvedHistoryFindings() => Enumerable.Range(0, 64)
        .Select(index => new ReviewFinding($"history-resolved-{index:D3}", ReviewFindingState.Open,
            new ReviewFindingLocation($"src/Resolved{index:D3}.cs", $"Resolved{index:D3}.Run"),
            $"RESOLVED_DESCRIPTION_{index:D3}: " + new string('r', 180)))
        .ToArray();

    private static void RecordHistoryRound(DispatchFixture fixture, TaskSpec owner,
        IReadOnlyList<ReviewFinding> findings, int minute,
        IReadOnlyList<ReviewFindingLocation>? touched = null,
        IReadOnlyList<FindingEvidenceReceipt>? receipts = null)
    {
        // Convergence replays the reported round, while the projector consumes the merged ledger.
        // Supply both contracts so the fixture exercises the same delivery boundaries as a worker.
        var workerOutput = string.Join(Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - deterministic fixture",
            "blockers: none",
            $"findings: {JsonSerializer.Serialize(findings)}",
            "touched_anchors: []",
            $"verdict: {(findings.Any(finding => finding.State == ReviewFindingState.Open) ? "needs-work" : "pass")}",
            "END_WORKER_RESULT");
        var outputDirectory = Path.Combine(fixture.Root, "history-output");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, $"{owner.Id.Value}-{minute}.out.log");
        File.WriteAllText(outputPath, workerOutput);
        fixture.Kernel.RecordTaskVerification(fixture.Goal.Id, owner.Id, new TaskVerificationRecord(
            "review", fixture.WorkingDirectory, 0, workerOutput, string.Empty,
            HistoryFixtureTime.AddMinutes(minute), StandardOutputPath: outputPath, ReviewFindingTouchedAnchors: touched ?? [],
            ReviewedCommit: fixture.HeadCommit, MergedReviewFindings: findings,
            FindingEvidenceReceipts: receipts ?? [], FullStandardOutput: workerOutput, FullStandardError: string.Empty));
    }

    private static WorkerProfileDispatchResult PrepareHistory(
        DispatchFixture fixture, bool typed, DateTimeOffset? dispatchedAt = null) =>
        WorkerProfileDispatcher.PrepareTask(fixture.Kernel, fixture.Goal, fixture.Task,
            typed ? FakeHarnessProfile() : new WorkerProfile("claude-cli", "claude -p {promptPath}"),
            Path.Combine(fixture.Root, "prompts"), fixture.WorkingDirectory, dispatchedAt ?? HistoryFixtureTime.AddHours(1),
            providerName: typed ? "OpenAI" : "Anthropic",
            modelName: typed ? AgentCatalog.OpenAiSolSubscriptionModelAlias : "claude-opus-4-6",
            reviewerScopeChangedFiles: fixture.Task.RequiredRole == AgentRole.Reviewer ? [] : null,
            reviewerScopeMergeBase: fixture.Task.RequiredRole == AgentRole.Reviewer ? fixture.HeadCommit : null,
            reviewerScopeTotalChangedFileCount: fixture.Task.RequiredRole == AgentRole.Reviewer ? 0 : null,
            reviewerMergeTreeClean: fixture.Task.RequiredRole == AgentRole.Reviewer ? true : null,
            reviewerMergeTreeConflictPaths: fixture.Task.RequiredRole == AgentRole.Reviewer ? [] : null,
            reviewerMergeTreeTotalConflictPathCount: fixture.Task.RequiredRole == AgentRole.Reviewer ? 0 : null);

    private static void AssertRoleInstructions(DispatchFixture fixture, string prompt)
    {
        var context = Path.Combine(fixture.WorkingDirectory, ".orchestrator-context", fixture.Goal.Id.Value);
        var source = fixture.Kernel.BuildTaskBriefSource(fixture.Goal.Id, fixture.Task.Id,
            workingDirectory: fixture.WorkingDirectory, contextDirectory: context,
            roleRequirementStack: ProjectStackResolver.GuidanceStackFor(fixture.WorkingDirectory, targetHome: null));
        var required = Assert.Single(source.Segments, segment =>
            segment.Lines.Contains($"## {fixture.Task.RequiredRole} Requirements"));
        Assert.All(required.Lines.Where(line => !string.IsNullOrWhiteSpace(line)),
            line => Assert.Contains(line, prompt, StringComparison.Ordinal));
    }

    private static void AssertHistoryFiles(DispatchFixture fixture, ReviewFindingContextProjection projection, string prompt)
    {
        var receipt = fixture.Task.LastDispatch!.ContextPackageReceipt!;
        foreach (var body in projection.RoundBodies.Concat(projection.ReceiptBodies))
        {
            var section = Assert.Single(receipt.Sections, item => item.LogicalIdentity == body.LogicalIdentity);
            Assert.Equal(body.Sha256, section.ContentHash);
            var relativePath = $".orchestrator-context/{fixture.Goal.Id.Value}/authoritative/{fixture.Task.Id.Value}/typed/{body.LogicalIdentity}";
            var bytes = File.ReadAllBytes(Path.Combine(fixture.WorkingDirectory, relativePath));
            Assert.Equal(body.Bytes, bytes);
            Assert.Equal(body.Sha256, WorkerContextArtifact.Hash(bytes));
            if (projection.ActiveBodyHashes.Contains(body.Sha256))
            {
                Assert.Equal(ContextDeliveryMode.OnDemandFile, section.DeliveryMode);
                Assert.Contains(body.LogicalIdentity, prompt, StringComparison.Ordinal);
                Assert.Contains(relativePath, prompt, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(ContextDeliveryMode.HistoricalFile, section.DeliveryMode);
                Assert.DoesNotContain($"identity={body.LogicalIdentity};", prompt, StringComparison.Ordinal);
            }
        }
    }

    private sealed class HistoryClock : IClock
    {
        public DateTimeOffset UtcNow => HistoryFixtureTime;
    }

    public enum DispatchPath
    {
        Initial,
        RetryFeedback,
        CompactRepair
    }

    [Xunit.Theory]
    [Xunit.InlineData(DispatchPath.Initial)]
    [Xunit.InlineData(DispatchPath.RetryFeedback)]
    [Xunit.InlineData(DispatchPath.CompactRepair)]
    public void RealDispatcherRendersEveryRequiredArtifactOnceWithoutGeneratedBoundaries(DispatchPath path)
    {
        var fixture = CreateDispatchFixture(path);

        var prepared = WorkerProfileDispatcher.PrepareTask(
            fixture.Kernel,
            fixture.Goal,
            fixture.Task,
            FakeHarnessProfile(),
            Path.Combine(fixture.Root, "prompts"),
            fixture.WorkingDirectory,
            DateTimeOffset.UtcNow,
            providerName: "OpenAI",
            modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias);

        var prompt = File.ReadAllText(prepared.PromptPath);
        var receipt = prepared.Task.LastDispatch!.ContextPackageReceipt!;
        var expectedIdentities = ExpectedRequiredArtifactIdentities(fixture, path);
        Assert.Equal(
            expectedIdentities,
            receipt.Sections.Select(section => section.LogicalIdentity).ToHashSet(StringComparer.Ordinal));
        foreach (var expectedIdentity in expectedIdentities)
        {
            var section = Assert.Single(receipt.Sections, candidate =>
                candidate.LogicalIdentity.Equals(expectedIdentity, StringComparison.Ordinal));
            Assert.Equal(
                section.DeliveryMode == ContextDeliveryMode.HistoricalFile ? 0 : 1,
                CountOccurrences(prompt, $"identity={expectedIdentity};"));
        }

        Assert.DoesNotContain("WORKER_CONTEXT_TYPED_PROJECTION_START:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("WORKER_CONTEXT_TYPED_PROJECTION_END:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("WORKER_CONTEXT_TYPED_PROJECTION_LITERAL:", prompt, StringComparison.Ordinal);
        if (path != DispatchPath.Initial)
        {
            Assert.NotNull(receipt.RetryFeedbackPromptReceipt);
            Assert.Equal(
                WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes("changed retry feedback")),
                receipt.RetryFeedbackPromptReceipt!.AcceptedFeedbackSha256);
        }

        Assert.Equal(
            path == DispatchPath.CompactRepair
                ? ReviewFindingHistoryProjectionMode.ContractRepair
                : path == DispatchPath.RetryFeedback
                    ? ReviewFindingHistoryProjectionMode.FullInspection
                    : null,
            receipt.ReviewFindingProjectionMode);
    }

    [Xunit.Fact]
    public void ForbiddenRoleArtifactIsAbsentFromRenderedPrompt()
    {
        var fixture = CreateDispatchFixture(DispatchPath.Initial);

        var prepared = WorkerProfileDispatcher.PrepareTask(
            fixture.Kernel,
            fixture.Goal,
            fixture.Task,
            FakeHarnessProfile(),
            Path.Combine(fixture.Root, "prompts"),
            fixture.WorkingDirectory,
            DateTimeOffset.UtcNow,
            providerName: "OpenAI",
            modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias);

        var contextDirectory = Path.Combine(
            fixture.WorkingDirectory,
            ".orchestrator-context",
            fixture.Goal.Id.Value);
        Assert.True(File.Exists(Path.Combine(contextDirectory, "deterministic-verification.md")));
        Assert.DoesNotContain(
            "context/deterministic-verification.md",
            File.ReadAllText(prepared.PromptPath),
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MandatoryArtifactWithMismatchedStoredHashIsRejectedBeforeRendering()
    {
        var root = CreateTempDirectory();
        var relativePath = ".orchestrator-context/test/mandatory.md";
        var materializedPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(materializedPath)!);
        File.WriteAllText(materializedPath, "corrupted bytes");
        var identity = new LogicalArtifactIdentity("context/mandatory.md");
        var expectedBytes = Encoding.UTF8.GetBytes("expected bytes");
        var artifact = WorkerContextArtifact.Create(
            identity,
            ContextArtifactKind.RegisteredContext,
            authoritativeBytes: null,
            [AgentRole.Developer],
            ContextDeliveryMode.MandatoryFile,
            ContextContractVersion.V1,
            relativePath,
            expectedContentHash: WorkerContextArtifact.Hash(expectedBytes),
            authoritativeByteCount: expectedBytes.Length);
        var package = new WorkerContextPackage(
            "ctxpkg-negative-control",
            ContextContractVersion.V1,
            AgentRole.Developer,
            [artifact]);
        var source = new TaskBriefSource(
            GoalId.New(),
            TaskId.New(),
            AgentRole.Developer,
            "negative control",
            [
                TaskBriefSegment.Fixed(["## Instructions"])
                    with { RoleVisibility = [AgentRole.Developer] },
                TaskBriefSegment.Projected(identity.Value, ["mandatory evidence"])
                    with { RoleVisibility = [AgentRole.Developer] }
            ],
            [new TaskBriefBudgetDecision(identity, TaskBriefBudgetDisposition.Full)]);

        var error = Assert.Throws<WorkerContextPreparationException>(() =>
            WorkerContextRenderer.Render(
                source,
                package,
                root,
                WorkerContextDeliveryPolicy.Initial));

        Assert.Equal("typed-identity-hash-mismatch", error.Reason);
    }

    [Xunit.Fact]
    public void ChangedRetryFeedbackItemAppearsInRetryPromptAndPassesAttestation()
    {
        var fixture = CreateDispatchFixture(DispatchPath.RetryFeedback);

        var prepared = WorkerProfileDispatcher.PrepareTask(
            fixture.Kernel,
            fixture.Goal,
            fixture.Task,
            FakeHarnessProfile(),
            Path.Combine(fixture.Root, "prompts"),
            fixture.WorkingDirectory,
            DateTimeOffset.UtcNow,
            providerName: "OpenAI",
            modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias);

        var prompt = File.ReadAllText(prepared.PromptPath);
        var receipt = prepared.Task.LastDispatch!.ContextPackageReceipt!;
        Assert.NotNull(receipt.RetryFeedbackPromptReceipt);
        var header = AssertBriefArtifact(prompt, receipt, "brief/header-residual.md", "Decision context:");
        var current = AssertBriefArtifact(prompt, receipt, "brief/current.md", "## Instructions");
        var feedback = RecoverInlineText(prompt, "task/criterion-retry-feedback.json");
        Assert.Equal(new[] { "changed retry feedback" }, JsonSerializer.Deserialize<string[]>(feedback));
        var timelineSection = Assert.Single(receipt.Sections, section => section.LogicalIdentity == "goal/timeline.json");
        Assert.Equal(ContextDeliveryMode.OnDemandFile, timelineSection.DeliveryMode);
        var timelineBytes = File.ReadAllBytes(Path.Combine(fixture.WorkingDirectory, timelineSection.MandatoryRelativePath!));
        Assert.Equal(WorkerContextArtifact.Hash(timelineBytes), timelineSection.ContentHash);
        var timeline = Encoding.UTF8.GetString(timelineBytes);
        foreach (var message in new[] { "earlier retry feedback", "changed retry feedback" })
        {
            Assert.Contains(message, timeline, StringComparison.Ordinal);
            Assert.DoesNotContain(message, header, StringComparison.Ordinal);
            Assert.DoesNotContain(message, current, StringComparison.Ordinal);
        }
        var feedbackSection = Assert.Single(receipt.Sections,
            section => section.LogicalIdentity == "task/criterion-retry-feedback.json");
        Assert.Equal(WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes(feedback)), feedbackSection.ContentHash);
        Assert.Equal(
            WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes("changed retry feedback")),
            receipt.RetryFeedbackPromptReceipt!.AcceptedFeedbackSha256);
        Assert.Equal(
            WorkerContextArtifact.Hash(File.ReadAllBytes(prepared.PromptPath)),
            receipt.RetryFeedbackPromptReceipt.GeneratedPromptSha256);
    }

    [Xunit.Fact]
    public void UserTextContainingLiteralProjectionMarkerSurvivesRenderingUnchanged()
    {
        const string literal = "<!-- WORKER_CONTEXT_TYPED_PROJECTION_START:user/literal.md -->";
        var fixture = CreateDispatchFixture(DispatchPath.Initial, literal);

        var prepared = WorkerProfileDispatcher.PrepareTask(
            fixture.Kernel,
            fixture.Goal,
            fixture.Task,
            FakeHarnessProfile(),
            Path.Combine(fixture.Root, "prompts"),
            fixture.WorkingDirectory,
            DateTimeOffset.UtcNow,
            providerName: "OpenAI",
            modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias);

        var prompt = File.ReadAllText(prepared.PromptPath);
        Assert.Contains(literal, RecoverInlineText(prompt, "goal/objective.md"), StringComparison.Ordinal);
        Assert.DoesNotContain("WORKER_CONTEXT_TYPED_PROJECTION_LITERAL:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("WORKER_CONTEXT_TYPED_PROJECTION_END:", prompt, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    [Xunit.InlineData(AgentRole.Planner)]
    public void TypedDispatchPromptContainsNoProjectionMarkers(AgentRole role)
    {
        var fixture = CreateDispatchFixture(DispatchPath.Initial, role: role);
        var prepared = WorkerProfileDispatcher.PrepareTask(
            fixture.Kernel, fixture.Goal, fixture.Task, FakeHarnessProfile(),
            Path.Combine(fixture.Root, "prompts"), fixture.WorkingDirectory, DateTimeOffset.UtcNow,
            providerName: "OpenAI", modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias,
            reviewerScopeChangedFiles: role == AgentRole.Reviewer ? [] : null,
            reviewerScopeMergeBase: role == AgentRole.Reviewer ? fixture.HeadCommit : null,
            reviewerScopeTotalChangedFileCount: role == AgentRole.Reviewer ? 0 : null,
            reviewerMergeTreeClean: role == AgentRole.Reviewer ? true : null,
            reviewerMergeTreeConflictPaths: role == AgentRole.Reviewer ? [] : null,
            reviewerMergeTreeTotalConflictPathCount: role == AgentRole.Reviewer ? 0 : null);

        var receipt = Assert.IsType<WorkerContextPackageReceipt>(prepared.Task.LastDispatch!.ContextPackageReceipt);
        var prompt = File.ReadAllText(prepared.PromptPath);
        Assert.StartsWith("ctxpkg-v1-sha256:", receipt.SemanticPackageId, StringComparison.Ordinal);
        Assert.DoesNotContain("WORKER_CONTEXT_TYPED_PROJECTION_START:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("WORKER_CONTEXT_TYPED_PROJECTION_END:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("WORKER_CONTEXT_TYPED_PROJECTION_LITERAL:", prompt, StringComparison.Ordinal);
        AssertBriefArtifact(prompt, receipt, "brief/header-residual.md", "Decision context:");
        AssertBriefArtifact(prompt, receipt, "brief/current.md", "## Instructions");
    }

    [Xunit.Fact]
    public void SourceContainsNoLegacyProjectionIngressOrBoundaryEmission()
    {
        var sourceRoot = Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "src");
        var files = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(sourceRoot, path).Split(Path.DirectorySeparatorChar)
                .Any(component => component is "bin" or "obj"));
        var forbidden = new System.Text.RegularExpressions.Regex(
            @"\b(WorkerContextProjectionBoundary|ParseLegacyMarkedTextV1|LegacyMarkedTextProjection|emitTypedSourceBoundaries|measureWithTypedSourceBoundaries|includeTypedSourceBoundaries|legacyIngressProgressRecorder)\b|\bProjectLegacyMarkedTextV1\(\s*[^\s)]");
        var matches = files.Where(path => forbidden.IsMatch(File.ReadAllText(path))).ToArray();
        Assert.Empty(matches);
    }

    [Xunit.Fact]
    public void BuildContextPackageRequiresTypedSourceBeforeReadingRegistry()
    {
        var task = new TaskSpec(TaskId.New(), "Render typed worker context.", AgentRole.Developer);
        var goal = new AgentOrchestratorKernel().CreateGoal("Render worker context.", [task]);
        var error = Assert.Throws<ArgumentNullException>(() => WorkerProfileDispatcher.BuildContextPackage(
            goal, task, "unused", "unused", null!));
        Assert.Equal("typedSource", error.ParamName);
    }

    private static string AssertBriefArtifact(
        string prompt, WorkerContextPackageReceipt receipt, string identity, string expectedText)
    {
        var section = Assert.Single(receipt.Sections, item => item.LogicalIdentity == identity);
        Assert.Equal(ContextContractVersion.V1.Value, section.ContractVersion);
        var text = RecoverInlineText(prompt, identity);
        Assert.Contains(expectedText, text, StringComparison.Ordinal);
        Assert.Equal(WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes(text)), section.ContentHash);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), section.ByteCount);
        return text;
    }

    private static DispatchFixture CreateDispatchFixture(
        DispatchPath path,
        string? objectiveSuffix = null,
        AgentRole role = AgentRole.Developer)
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        var headCommit = InitializeRepository(workingDirectory);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Render typed worker context.", role);
        var objective = "Render the worker context exactly once." +
                        (objectiveSuffix is null ? string.Empty : Environment.NewLine + objectiveSuffix);
        var goal = kernel.CreateGoal(objective, [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        if (path == DispatchPath.RetryFeedback)
        {
            kernel.RetryTaskWithAuthoritativeFeedback(
                goal.Id, task.Id, "earlier retry feedback", RetryCause.ContractClarification);
        }
        if (path != DispatchPath.Initial)
        {
            var location = new ReviewFindingLocation("src/Worker.cs", "Worker.Render");
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                new TaskVerificationRecord(
                    "review",
                    workingDirectory,
                    1,
                    "review requires retry",
                    string.Empty,
                    DateTimeOffset.UtcNow,
                    ReviewFindingTouchedAnchors: [location],
                    ReviewedCommit: headCommit,
                    MergedReviewFindings:
                    [
                        new ReviewFinding("stable-render", ReviewFindingState.Open, location, "render once")
                    ],
                    ReviewFindingContractViolation: path == DispatchPath.CompactRepair
                        ? new ReviewFindingContractViolation("schema-invalid", "missing fields")
                        : null,
                    FullStandardOutput: "review requires retry",
                    FullStandardError: string.Empty));
            kernel.RetryTaskWithAuthoritativeFeedback(
                goal.Id,
                task.Id,
                "changed retry feedback",
                RetryCause.ContractClarification,
                retryRoundKind: path == DispatchPath.CompactRepair
                    ? RetryRoundKind.Mechanical
                    : RetryRoundKind.Standard);
        }

        return new DispatchFixture(root, workingDirectory, kernel, goal, task, headCommit);
    }

    private static WorkerProfile FakeHarnessProfile() =>
        new("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}");

    private static HashSet<string> ExpectedRequiredArtifactIdentities(
        DispatchFixture fixture,
        DispatchPath path)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "goal/objective.md",
            "task/description.md",
            "task/metadata.json",
            "task/criterion-retry-feedback.json",
            "goal/timeline.json",
            "brief/header-residual.md",
            "brief/current.md",
            "context/manifest.v1.json"
        };
        var contextDirectory = Path.Combine(
            fixture.WorkingDirectory,
            ".orchestrator-context",
            fixture.Goal.Id.Value);
        using var registry = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(contextDirectory, "artifact-registry.json")));
        foreach (var artifact in registry.RootElement.GetProperty("artifacts").EnumerateArray())
        {
            var pathValue = artifact.GetProperty("path").GetString()!;
            var visible = artifact.GetProperty("roleVisibility").EnumerateArray()
                .Select(item => item.GetString())
                .Contains(fixture.Task.RequiredRole.ToString(), StringComparer.Ordinal);
            if (visible && WorkerProfileDispatcher.DeliverableRegistryArtifactPaths.Contains(
                    pathValue,
                    StringComparer.Ordinal))
            {
                expected.Add($"context/{pathValue}");
            }
        }

        if (path == DispatchPath.Initial)
        {
            return expected;
        }

        var projection = ReviewFindingContextProjector.Project(
            fixture.Goal,
            fixture.Task,
            fixture.HeadCommit,
            fixture.HeadCommit);
        expected.Add("goal/review-finding-history.json");
        expected.UnionWith(projection.RoundBodies.Concat(projection.ReceiptBodies)
            .Select(body => body.LogicalIdentity));
        if (fixture.Task.LastVerification is { } lastVerification)
        {
            expected.Add("task/last-verification/stdout");
            if (lastVerification.AuthoritativeStandardError is not null)
            {
                expected.Add("task/last-verification/stderr");
            }
        }

        if (path != DispatchPath.CompactRepair)
        {
            return expected;
        }

        Assert.Equal(ReviewFindingHistoryProjectionMode.ContractRepair, projection.Metrics.Mode);
        expected.Add("task/review-contract-repair-envelope.json");
        return expected;
    }

    private static string InitializeRepository(string workingDirectory, bool deterministic = false)
    {
        AssertGitSucceeded(GitCli.Run(workingDirectory, "init", "--initial-branch=main"));
        File.WriteAllText(Path.Combine(workingDirectory, "fixture.txt"), "worker context fixture");
        AssertGitSucceeded(GitCli.Run(workingDirectory, "add", "fixture.txt"));
        AssertGitSucceeded(GitCli.RunWithEnvironment(
            workingDirectory,
            deterministic ? new Dictionary<string, string>
            {
                ["GIT_AUTHOR_DATE"] = "2026-08-01T00:00:00Z",
                ["GIT_COMMITTER_DATE"] = "2026-08-01T00:00:00Z"
            } : new Dictionary<string, string>(),
            "-c", "user.name=Worker Context Tests",
            "-c", "user.email=worker-context-tests@example.invalid",
            "commit", "--quiet", "-m", "fixture"));
        var head = GitCli.Run(workingDirectory, "rev-parse", "HEAD");
        AssertGitSucceeded(head);
        return head.Output.Trim();
    }

    private static void AssertGitSucceeded(GitCli.GitResult result) =>
        Assert.True(
            result.Succeeded && !result.DrainTimedOut,
            $"git fixture command failed: exit={result.ExitCode}; stderr={result.Error}");

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var cursor = 0;
        while ((cursor = content.IndexOf(value, cursor, StringComparison.Ordinal)) >= 0)
        {
            count++;
            cursor += value.Length;
        }

        return count;
    }

    private static string RecoverInlineText(string prompt, string identity)
    {
        var header = $"INLINE FULL: identity={identity};";
        var headerStart = prompt.IndexOf(header, StringComparison.Ordinal);
        Assert.True(headerStart >= 0, $"Inline artifact '{identity}' was not rendered.");
        var payloadStart = prompt.IndexOf('\n', headerStart);
        Assert.True(payloadStart >= 0, $"Inline artifact '{identity}' has no payload.");
        return WorkerContextInlinePayloadFixture.ReadText(prompt, identity);
    }

    private sealed record DispatchFixture(
        string Root,
        string WorkingDirectory,
        AgentOrchestratorKernel Kernel,
        Goal Goal,
        TaskSpec Task,
        string HeadCommit);
}
