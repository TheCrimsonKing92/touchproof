using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe integration tests: each case owns a temporary database, prompt root and seeded worktree.
public sealed class LinkedBacklogPointerTests : WorkerDispatchTestSupport
{
    private const string FileName = "linked-backlog-record.md";
    private const string PointerIdentity = "goal/linked-backlog-pointer.md";
    private const string RecordIdentity = "context/linked-backlog-record.md";
    private const string BodySentinel = "full body available only by pulling the record";

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task PlannerGetsPointerAndOnDemandRecord(bool typed)
    {
        var fixture = await CreateFixture();
        var linked = await fixture.Store.AddAsync("Related item title");
        await fixture.Store.LinkAsync(fixture.Item.Id, linked.Id, BacklogLinkKind.Related);
        await fixture.Store.AppendNoteAsync(fixture.Item.Id, "first note sentinel");
        var updated = await fixture.Store.AppendNoteAsync(fixture.Item.Id, "second note sentinel");

        var dispatch = Prepare(fixture, typed);
        var prompt = File.ReadAllText(dispatch.PromptPath);
        var pointer = ReadPointer(prompt, typed);
        Assert.InRange(Encoding.UTF8.GetByteCount(pointer), 1, 1_000);
        Assert.Contains(fixture.Item.Id, pointer, StringComparison.Ordinal);
        Assert.Contains(fixture.Item.Title, pointer, StringComparison.Ordinal);
        Assert.Contains(fixture.Item.Status.ToString(), pointer, StringComparison.Ordinal);
        Assert.Contains($"{linked.Id} | Related | {linked.Title}", pointer, StringComparison.Ordinal);
        Assert.Contains("Read linked-backlog-record.md", pointer, StringComparison.Ordinal);
        Assert.Contains("before planning", pointer, StringComparison.Ordinal);
        Assert.DoesNotContain(BodySentinel, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("first note sentinel", prompt, StringComparison.Ordinal);
        var record = ReadRecord(fixture);
        Assert.Contains(BodySentinel, record, StringComparison.Ordinal);
        Assert.Contains(fixture.Item.Id, record, StringComparison.Ordinal);
        Assert.Contains(fixture.Item.Status.ToString(), record, StringComparison.Ordinal);
        var firstNote = record.IndexOf("first note sentinel", StringComparison.Ordinal);
        var secondNote = record.IndexOf("second note sentinel", StringComparison.Ordinal);
        Assert.True(firstNote >= 0 && secondNote > firstNote);
        Assert.All(updated.Notes, note => Assert.Contains(
            note.CreatedAt.ToString("O", CultureInfo.InvariantCulture), record, StringComparison.Ordinal));
        Assert.EndsWith("Omitted 0 notes and 0 body characters.\n", record, StringComparison.Ordinal);
        Assert.Contains($"{FileName}: full linked backlog record; delivery mode OnDemandFile",
            File.ReadAllText(Path.Combine(fixture.ContextDirectory, "manifest.md")), StringComparison.Ordinal);
        using var registry = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(fixture.ContextDirectory, "artifact-registry.json")));
        var entry = Assert.Single(registry.RootElement.GetProperty("artifacts").EnumerateArray(),
            artifact => artifact.GetProperty("path").GetString() == FileName);
        Assert.Equal(["Planner"], entry.GetProperty("roleVisibility").EnumerateArray().Select(role => role.GetString()));

        if (typed)
        {
            var receipt = dispatch.Task.LastDispatch!.ContextPackageReceipt!;
            var section = Assert.Single(receipt.Sections, section => section.LogicalIdentity == RecordIdentity);
            Assert.Equal(ContextDeliveryMode.OnDemandFile, section.DeliveryMode);
            Assert.Equal([AgentRole.Planner], section.RoleVisibility);
            Assert.Equal(Encoding.UTF8.GetByteCount(record), section.ByteCount);
            Assert.True(receipt.OnDemandArtifactBytes >= section.ByteCount);
            Assert.Equal(receipt.Sections.Where(section => section.DeliveryMode is
                ContextDeliveryMode.InlineFull or ContextDeliveryMode.MandatoryFile).Sum(section => section.ByteCount),
                receipt.DeliveredArtifactBytes);
            Assert.Equal(receipt.Sections.Sum(section => section.ByteCount),
                receipt.DeliveredArtifactBytes + receipt.OnDemandArtifactBytes);
            Assert.Equal(record, File.ReadAllText(Path.Combine(fixture.WorkingDirectory, section.MandatoryRelativePath!)));
            Assert.Contains($"ON-DEMAND ATTESTATION: identity={RecordIdentity};", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain($"INLINE FULL: identity={RecordIdentity};", prompt, StringComparison.Ordinal);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task UnicodeTitleIsBoundedWithMarkerInBothArtifacts(bool typed)
    {
        var title = string.Concat(Enumerable.Repeat("😀é", 600));
        var fixture = await CreateFixture(title: title);

        var dispatch = Prepare(fixture, typed);
        var pointer = ReadPointer(File.ReadAllText(dispatch.PromptPath), typed);
        var record = ReadRecord(fixture);
        Assert.InRange(Encoding.UTF8.GetByteCount(pointer), 1, 1_000);
        var fileTitle = Assert.Single(record.Split('\n'), line => line.StartsWith("Title: ", StringComparison.Ordinal))[7..];
        Assert.InRange(Encoding.UTF8.GetByteCount(fileTitle), 1, 1_000);
        Assert.Contains("... [title truncated]", pointer, StringComparison.Ordinal);
        Assert.EndsWith("... [title truncated]", fileTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFFFD", pointer + record, StringComparison.Ordinal);
        Assert.InRange(File.ReadAllBytes(fixture.RecordPath).Length, 1, 20_000);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, true)]
    [Xunit.InlineData(false, true)]
    public async Task OldestNotesAreDroppedBeforeBodyIsCut(bool typed, bool oversizedBody)
    {
        var body = oversizedBody ? string.Concat(Enumerable.Repeat("😀\r\né", 10_000)) : BodySentinel;
        var fixture = await CreateFixture(body: body);
        await fixture.Store.AppendNoteAsync(fixture.Item.Id, "oldest note " + new string('x', 21_000));
        await fixture.Store.AppendNoteAsync(fixture.Item.Id, "newest note sentinel");

        Prepare(fixture, typed);
        var record = ReadRecord(fixture);
        Assert.InRange(File.ReadAllBytes(fixture.RecordPath).Length, 1, 20_000);
        Assert.DoesNotContain("oldest note", record, StringComparison.Ordinal);
        var omission = Regex.Match(record, @"Omitted (\d+) notes and (\d+) body characters\.\n\z");
        Assert.True(omission.Success, record[^Math.Min(100, record.Length)..]);
        Assert.Equal(oversizedBody ? "2" : "1", omission.Groups[1].Value);
        if (oversizedBody)
        {
            Assert.DoesNotContain("newest note sentinel", record, StringComparison.Ordinal);
            var bodyStart = record.IndexOf("## Body\n", StringComparison.Ordinal) + "## Body\n".Length;
            var bodyEnd = record.IndexOf("\n\n## Notes\n", bodyStart, StringComparison.Ordinal);
            var retainedBody = record[bodyStart..bodyEnd];
            // Each original CRLF becomes one LF; recover the original prefix's scalar count.
            var retainedOriginalScalars = retainedBody.EnumerateRunes().Count() + retainedBody.Count(c => c == '\n');
            Assert.Equal(body.EnumerateRunes().Count() - retainedOriginalScalars,
                int.Parse(omission.Groups[2].Value, CultureInfo.InvariantCulture));
            Assert.DoesNotContain("\uFFFD", record, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(BodySentinel, record, StringComparison.Ordinal);
            Assert.Contains("newest note sentinel", record, StringComparison.Ordinal);
            Assert.Equal("0", omission.Groups[2].Value);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, "no-link")]
    [Xunit.InlineData(false, "no-link")]
    [Xunit.InlineData(true, "missing-item")]
    [Xunit.InlineData(false, "missing-item")]
    [Xunit.InlineData(true, "missing-database")]
    [Xunit.InlineData(false, "missing-database")]
    [Xunit.InlineData(true, "directory")]
    [Xunit.InlineData(false, "directory")]
    [Xunit.InlineData(true, "corrupt")]
    [Xunit.InlineData(false, "corrupt")]
    public async Task UnavailableEnrichmentDoesNotFailDispatch(bool typed, string condition)
    {
        var fixture = await CreateFixture(linkGoal: condition != "no-link");
        var storePath = fixture.StorePath;
        if (condition == "missing-item")
            fixture.Kernel.SetGoalSourceBacklogItemId(fixture.Goal.Id, new string('f', 32));
        if (condition == "missing-database")
            storePath = Path.Combine(fixture.Root, "absent.db");
        if (condition == "directory")
            storePath = fixture.Root;
        if (condition == "corrupt")
        {
            storePath = Path.Combine(fixture.Root, "corrupt.db");
            File.WriteAllText(storePath, "This is not a SQLite database.");
        }

        var dispatch = Prepare(fixture, typed, storePath: storePath);
        AssertAbsent(fixture, dispatch);
        if (condition == "missing-database")
            Assert.False(File.Exists(storePath));
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, AgentRole.Developer)]
    [Xunit.InlineData(false, AgentRole.Developer)]
    [Xunit.InlineData(true, AgentRole.Reviewer)]
    [Xunit.InlineData(false, AgentRole.Reviewer)]
    [Xunit.InlineData(true, AgentRole.Tester)]
    [Xunit.InlineData(false, AgentRole.Tester)]
    [Xunit.InlineData(true, AgentRole.Researcher)]
    [Xunit.InlineData(false, AgentRole.Researcher)]
    public async Task OtherRolesReceiveNeitherArtifact(bool typed, AgentRole role)
    {
        var fixture = await CreateFixture(role: role);
        AssertAbsent(fixture, Prepare(fixture, typed));
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task RefreshReadsCurrentStoreAndRemovesStaleCopies(bool typed)
    {
        var fixture = await CreateFixture();
        Prepare(fixture, typed);
        Assert.Contains(BodySentinel, ReadRecord(fixture), StringComparison.Ordinal);
        await fixture.Store.UpdateAsync(fixture.Item.Id, new BacklogItemUpdate(Body: "fresh dispatch body"));
        Prepare(fixture, typed);
        Assert.Contains("fresh dispatch body", ReadRecord(fixture), StringComparison.Ordinal);
        Assert.DoesNotContain(BodySentinel, ReadRecord(fixture), StringComparison.Ordinal);

        fixture.Kernel.SetGoalSourceBacklogItemId(fixture.Goal.Id, new string('f', 32));
        AssertAbsent(fixture, Prepare(fixture, typed));
        Assert.Empty(Directory.EnumerateFiles(fixture.ContextDirectory, FileName, SearchOption.AllDirectories));
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task DeveloperAfterPlannerCannotReadStaleRecord(bool typed)
    {
        var fixture = await CreateFixture();
        Prepare(fixture, typed);
        Assert.True(File.Exists(fixture.RecordPath));
        var developer = fixture.Kernel.AddTask(fixture.Goal.Id, AgentRole.Developer,
            "Implement scoped change.", AgentCatalog.Default().Agents);

        var dispatch = Prepare(fixture, typed, task: developer);
        AssertAbsent(fixture, dispatch);
        Assert.Empty(Directory.EnumerateFiles(fixture.ContextDirectory, FileName, SearchOption.AllDirectories));
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task LinksKeepTheirOrderAndReportTrailingOmissions(bool typed)
    {
        var fixture = await CreateFixture();
        var first = await fixture.Store.AddAsync("first linked title");
        var oversized = await fixture.Store.AddAsync(new string('z', 1_100));
        var last = await fixture.Store.AddAsync("last linked title");
        await fixture.Store.LinkAsync(fixture.Item.Id, first.Id, BacklogLinkKind.Related);
        await fixture.Store.LinkAsync(fixture.Item.Id, oversized.Id, BacklogLinkKind.Related);
        await fixture.Store.LinkAsync(fixture.Item.Id, last.Id, BacklogLinkKind.Related);
        // Use actual stored order rather than relying on insertion order from the store.
        var item = (await fixture.Store.GetByExactIdAsync(fixture.Item.Id))!;
        var titles = new Dictionary<string, string> { [first.Id] = first.Title, [oversized.Id] = oversized.Title, [last.Id] = last.Title };
        var dispatch = Prepare(fixture, typed);
        var pointer = ReadPointer(File.ReadAllText(dispatch.PromptPath), typed);
        var oversizedIndex = item.Links.ToList().FindIndex(link => link.OtherId(item.Id) == oversized.Id);
        Assert.Contains($"Omitted {item.Links.Count - oversizedIndex} links.", pointer, StringComparison.Ordinal);
        foreach (var link in item.Links.Take(oversizedIndex))
            Assert.Contains($"{link.OtherId(item.Id)} | {link.Kind} | {titles[link.OtherId(item.Id)]}", pointer, StringComparison.Ordinal);
        foreach (var link in item.Links.Skip(oversizedIndex))
            Assert.DoesNotContain(link.OtherId(item.Id), pointer, StringComparison.Ordinal);
        Assert.InRange(Encoding.UTF8.GetByteCount(pointer), 1, 1_000);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task RecordWriteFailureOmitsPointerAndKeepsDispatchWorking(bool typed)
    {
        var fixture = await CreateFixture();
        Directory.CreateDirectory(fixture.RecordPath);
        AssertAbsent(fixture, Prepare(fixture, typed));
    }

    private static WorkerProfileDispatchResult Prepare(Fixture fixture, bool typed,
        string? storePath = null, TaskSpec? task = null)
    {
        task ??= fixture.Task;
        var reviewer = task.RequiredRole == AgentRole.Reviewer;
        return WorkerProfileDispatcher.PrepareTask(fixture.Kernel, fixture.Goal, task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(fixture.Root, "prompts"), fixture.WorkingDirectory, DateTimeOffset.UtcNow,
            providerName: typed ? "OpenAI" : null,
            modelName: typed ? AgentCatalog.OpenAiSolSubscriptionModelAlias : null,
            allowPendingRecordedDispatchRefresh: true,
            reviewerScopeChangedFiles: reviewer ? [] : null,
            reviewerScopeMergeBase: reviewer ? fixture.Head : null,
            reviewerScopeTotalChangedFileCount: reviewer ? 0 : null,
            reviewerMergeTreeClean: reviewer ? true : null,
            reviewerMergeTreeConflictPaths: reviewer ? [] : null,
            reviewerMergeTreeTotalConflictPathCount: reviewer ? 0 : null,
            backlogStorePath: storePath ?? fixture.StorePath);
    }

    private static async Task<Fixture> CreateFixture(string title = "Linked source item",
        string body = BodySentinel, bool linkGoal = true, AgentRole role = AgentRole.Planner)
    {
        var root = CreateTempDirectory();
        var workingDirectory = CreateSeededDispatchRepository();
        Assert.True(InfrastructureTestSupport.TryReadGitHeadInProcess(workingDirectory, out var head));
        Assert.False(string.IsNullOrWhiteSpace(head));
        var storePath = Path.Combine(root, "backlog.db");
        var store = new BacklogStore(storePath);
        var item = await store.AddAsync(title, body);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Use supplied context for the scoped task.", role);
        var goal = kernel.CreateGoal("Improve a small repository feature.", [task]);
        if (linkGoal)
            kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return new Fixture(root, workingDirectory, storePath, store, item, kernel, goal, task, head!);
    }

    private static string ReadRecord(Fixture fixture)
    {
        var bytes = File.ReadAllBytes(fixture.RecordPath);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        var text = new UTF8Encoding(false, true).GetString(bytes);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        return text;
    }

    private static string ReadPointer(string prompt, bool typed)
    {
        if (typed)
            return WorkerContextInlinePayloadFixture.ReadText(prompt, PointerIdentity);
        var start = prompt.IndexOf("## Linked backlog item\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "Planner prompt did not contain the backlog pointer.");
        return prompt[start..];
    }

    private static void AssertAbsent(Fixture fixture, WorkerProfileDispatchResult dispatch)
    {
        var prompt = File.ReadAllText(dispatch.PromptPath);
        Assert.DoesNotContain("## Linked backlog item", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(PointerIdentity, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(RecordIdentity, prompt, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.RecordPath));
        Assert.DoesNotContain(FileName, File.ReadAllText(Path.Combine(fixture.ContextDirectory, "manifest.md")), StringComparison.Ordinal);
        Assert.DoesNotContain(FileName, File.ReadAllText(Path.Combine(fixture.ContextDirectory, "artifact-registry.json")), StringComparison.Ordinal);
        if (dispatch.Task.LastDispatch?.ContextPackageReceipt is { } receipt)
            Assert.DoesNotContain(receipt.Sections, section => section.LogicalIdentity is PointerIdentity or RecordIdentity);
    }

    private sealed record Fixture(string Root, string WorkingDirectory, string StorePath,
        BacklogStore Store, BacklogItem Item, AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, string Head)
    {
        internal string ContextDirectory => Path.Combine(WorkingDirectory, ".orchestrator-context", Goal.Id.Value);
        internal string RecordPath => Path.Combine(ContextDirectory, FileName);
    }
}
