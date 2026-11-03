using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: unique roots, registry and databases; no process-global Console/environment writes.
public sealed class CliFleetAttentionTests
{
    private const string GoalIdValue = "11111111aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ItemIdValue = "same-opaque-item-id";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T12:00:00Z", CultureInfo.InvariantCulture);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task RegistryFleet_CollidingIds_PreservesThreeProjectBindings()
    {
        using var fixture = new Fixture();
        var alpha = fixture.Register("alpha");
        var beta = fixture.Register("beta");
        foreach (var workspace in new[] { fixture.Home, alpha, beta })
        {
            await fixture.SeedAsync(workspace);
            await fixture.ItemAsync(workspace, ItemIdValue, GoalIdValue, Now);
        }
        File.WriteAllText(fixture.Registry.CurrentProjectPath, "not-a-registered-project");
        var before = fixture.Snapshot();
        var child = fixture.Run("attention", "fleet", "--json");
        Assert.Equal(0, child.ExitCode);
        using var document = JsonDocument.Parse(child.StandardOutput);
        AssertEnvelope(document.RootElement);
        var payload = document.RootElement.GetProperty("payload");
        Assert.True(payload.GetProperty("complete").GetBoolean());
        Assert.Equal(3, payload.GetProperty("total").GetInt32());
        Assert.Equal(0, payload.GetProperty("omitted").GetInt32());
        var rows = payload.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "alpha", "beta", "home" }, rows.Select(row => row.GetProperty("project").GetString()));
        foreach (var row in rows)
        {
            Assert.Equal(ItemIdValue, row.GetProperty("itemId").GetString());
            Assert.Equal(GoalIdValue, row.GetProperty("goalId").GetString());
            Assert.Equal("collaboration-item", row.GetProperty("source").GetString());
            Assert.Equal("Clarification", row.GetProperty("kind").GetString());
            Assert.Equal(Now.UtcDateTime.ToString("O"), row.GetProperty("raisedAt").GetString());
            Assert.Equal("Subject\nOriginal question", row.GetProperty("question").GetString());
            var action = row.GetProperty("nextAction").GetString()!;
            var selection = OrchestratorProjectSelection.FromArgs(action.Split(' '), null);
            Assert.Equal(row.GetProperty("project").GetString() == "home" ? "default" : row.GetProperty("project").GetString(), selection.ProjectName);
            var command = OrchestratorTenantSelection.FromArgs(selection.CommandArgs, null).CommandArgs;
            Assert.Equal(new[] { "attention", "answer", GoalIdValue, ItemIdValue, "--text-file", "<path>" }, command);
            CliCommandHelp.ThrowIfInvalidFlags(command);
        }
        Assert.Equal(3, payload.GetProperty("projects").GetArrayLength());
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task Catalog_HomeAlias_UsesRecordedStoreAndDefaultCommand()
    {
        using var fixture = new Fixture();
        var alpha = fixture.Register("alpha");
        fixture.Registry.RecordDefaultStateLocation(new(fixture.Repository,
            Path.GetRelativePath(fixture.Data, alpha.OrchestratorDirectory), ".orchestrator.backup-fixture", Now));
        await fixture.SeedAsync(alpha);
        await fixture.ItemAsync(alpha, ItemIdValue, GoalIdValue, Now);
        var catalog = fixture.Catalog();
        Assert.Equal(catalog.Projects[0].Workspace!.SqliteStatePath, catalog.Projects[1].Workspace!.SqliteStatePath);
        var calls = 0;
        var result = await FleetAttentionReader.ReadAsync(catalog, 50, Now, read: async (workspace, now, token) =>
        {
            calls++;
            return await AttentionJsonReadContext.ReadExistingAsync(workspace, now, token);
        });
        Assert.Equal(1, calls);
        var project = Assert.Single(result.Projects);
        Assert.Equal("home", project.Project);
        Assert.Equal(new[] { "alpha" }, project.Aliases);
        Assert.Equal("home", Assert.Single(result.Items).Project);
        Assert.StartsWith("--project=default ", result.Items[0].NextAction);
    }

    [Theory]
    [InlineData("home", false)]
    [InlineData("home", true)]
    [InlineData("HOME", false)]
    [InlineData("HOME", true)]
    public async Task Cli_HomeRegistration_ReportsIncompleteAndKeepsHealthyRows(
        string registration, bool seedConflictingStore)
    {
        using var fixture = new Fixture();
        var conflicting = fixture.Register(registration);
        var beta = fixture.Register("beta");
        Assert.NotEqual(fixture.Home.SqliteStatePath, conflicting.SqliteStatePath);
        foreach (var workspace in new[] { fixture.Home, beta })
        {
            await fixture.SeedAsync(workspace);
            await fixture.ItemAsync(workspace, ItemIdValue, GoalIdValue, Now);
        }
        if (seedConflictingStore)
        {
            await fixture.SeedAsync(conflicting);
            await fixture.ItemAsync(conflicting, "conflicting-item", GoalIdValue, Now);
        }
        var before = fixture.Snapshot();
        var child = fixture.Run("attention", "fleet", "--json");
        Assert.Equal(1, child.ExitCode);
        using var document = JsonDocument.Parse(child.StandardOutput);
        AssertEnvelope(document.RootElement);
        var payload = document.RootElement.GetProperty("payload");
        Assert.False(payload.GetProperty("complete").GetBoolean());
        Assert.Equal(2, payload.GetProperty("total").GetInt32());
        Assert.Equal(new[] { "beta", "home" }, payload.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("project").GetString()));
        var error = Assert.Single(payload.GetProperty("errors").EnumerateArray());
        Assert.Equal("registry-unavailable", error.GetProperty("code").GetString());
        Assert.Contains($"Registered project '{registration}' conflicts", error.GetProperty("message").GetString());
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aliases_ReversedCatalog_StillReadsCanonicalWorkspaceOnce(bool reverse)
    {
        using var fixture = new Fixture();
        var shared = fixture.Register("alpha");
        AttentionWorkspaceEntry[] entries = [new("zulu", shared), new("Alpha", shared), new("alpha", shared)];
        if (reverse) Array.Reverse(entries);
        var calls = 0;
        var result = await FleetAttentionReader.ReadAsync(new(entries), 50, Now, read: (_, _, _) =>
        {
            calls++;
            return Task.FromResult(Context(new CollaborationItem(ItemIdValue, CollaborationItemType.Decision, GoalIdValue,
                CollaborationItemStatus.Raised, "Subject", "Reason", null, Now, null, null)));
        });
        Assert.Equal(1, calls);
        Assert.Equal("Alpha", Assert.Single(result.Projects).Project);
        Assert.Equal(new[] { "alpha", "zulu" }, result.Projects[0].Aliases);
        Assert.Equal("Alpha", Assert.Single(result.Items).Project);
    }

    [Fact]
    public async Task Registry_SameRepositoryDistinctStores_DoesNotMergeProjects()
    {
        using var fixture = new Fixture();
        var alpha = fixture.Register("alpha");
        var beta = fixture.Register("beta");
        Assert.Equal(alpha.RootDirectory, beta.RootDirectory);
        Assert.NotEqual(alpha.SqliteStatePath, beta.SqliteStatePath);
        var paths = new List<string>();
        var result = await FleetAttentionReader.ReadAsync(fixture.Catalog(), 50, Now, read: (workspace, _, _) =>
        {
            paths.Add(workspace.SqliteStatePath);
            return Task.FromResult(Context());
        });
        Assert.Equal(3, paths.Distinct().Count());
        Assert.Equal(new[] { "alpha", "beta", "home" }, result.Projects.Select(project => project.Project));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(50)]
    public async Task Ordering_OriginalTimesAndIdentityTies_LimitsAfterGlobalSort(int limit)
    {
        using var fixture = new Fixture();
        var alpha = fixture.Register("alpha");
        CollaborationItem Row(string id, string? goal, DateTimeOffset time) => new(id,
            CollaborationItemType.Decision, goal, CollaborationItemStatus.Raised, id, "reason", null, time, null, null);
        var wait = new HumanInputRequest(new HumanInputRequestId("wait-identity"), new GoalId(GoalIdValue),
            null, "Wait question", Now, resumeCommand: "answer wait-identity <answer>");
        var result = await FleetAttentionReader.ReadAsync(new([new("home", fixture.Home), new("alpha", alpha)]), limit, Now,
            read: (workspace, _, _) => Task.FromResult(workspace == alpha
                ? Context(Row("z", GoalIdValue, Now), Row("a", GoalIdValue, Now), Row("null-goal", null, Now)) with { Waits = [wait] }
                : Context(Row("unknown", null, DateTimeOffset.MinValue), Row("oldest", GoalIdValue, Now.AddDays(-1)),
                    Row("offset", GoalIdValue, Now.ToOffset(TimeSpan.FromHours(3))))));
        string[] expected = ["oldest", "null-goal", "a", "z", "wait-identity", "offset", "unknown"];
        Assert.Equal(expected.Take(limit), result.Items.Select(item => item.ItemId));
        Assert.Equal(7, result.Total);
        Assert.Equal(7 - Math.Min(limit, 7), result.Omitted);
        Assert.Equal(2, result.Projects.Count);
        Assert.True(result.Complete);
        if (limit == 50) Assert.Null(result.Items[^1].RaisedAt);
    }

    [Fact]
    public async Task Identity_SameIdAcrossSourcesAndGoals_DeduplicatesOnlyWholeTuple()
    {
        using var fixture = new Fixture();
        var wait = new HumanInputRequest(new HumanInputRequestId(ItemIdValue), new GoalId(GoalIdValue),
            null, "Question", Now);
        var item = new CollaborationItem(ItemIdValue, CollaborationItemType.Decision, GoalIdValue,
            CollaborationItemStatus.Raised, "Subject", "Reason", null, Now, null, null);
        var result = await FleetAttentionReader.ReadAsync(new([new("home", fixture.Home)]), 50, Now,
            read: (_, _, _) => Task.FromResult(Context(item, item, item with { GoalId = null }) with { Waits = [wait] }));
        Assert.Equal(3, result.Total);
        Assert.Equal(new[] { "collaboration-item", "collaboration-item", "human-wait" }, result.Items.Select(item => item.Source));
    }

    [Theory]
    [InlineData("11111111bbbbbbbbbbbbbbbbbbbbbbbb", "11111111bbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("wait-id-123$&tail", "\"wait-id-123$&tail\"")]
    public async Task HumanWait_DefaultAction_PreservesLiteralFullItemId(string id, string target)
    {
        using var fixture = new Fixture();
        var wait = new HumanInputRequest(new HumanInputRequestId(id), new GoalId(GoalIdValue),
            null, "Question", Now);
        Assert.Equal($"answer {id[..8]} <answer>", wait.ResumeCommand);
        var result = await FleetAttentionReader.ReadAsync(new([new("home", fixture.Home)]), 50, Now,
            read: (_, _, _) => Task.FromResult(Context() with { Waits = [wait] }));
        var row = Assert.Single(result.Items);
        Assert.Equal(id, row.ItemId);
        Assert.Equal($"--project=default --tenant=default answer {target} <answer>", row.NextAction);
    }

    [Theory]
    [InlineData("11111111bbbbbbbbbbbbbbbbbbbbbbbb", "11111111bbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("wait-id-123$&tail", "\"wait-id-123$&tail\"")]
    public async Task HumanWait_ClarificationAction_ExpandsGoalAndItemByPosition(
        string id, string target)
    {
        using var fixture = new Fixture();
        var wait = new HumanInputRequest(new HumanInputRequestId(id), new GoalId(GoalIdValue),
            null, "Question", Now, resumeCommand: $"attention answer {GoalIdValue[..8]} {id[..8]} --text-file <path>");
        var result = await FleetAttentionReader.ReadAsync(new([new("home", fixture.Home)]), 50, Now,
            read: (_, _, _) => Task.FromResult(Context() with { Waits = [wait] }));
        Assert.Equal($"--project=default --tenant=default attention answer {GoalIdValue} {target} --text-file <path>",
            Assert.Single(result.Items).NextAction);
    }

    [Fact]
    public async Task HumanWait_AnswerTextMatchesPrefix_DoesNotRewriteAnswer()
    {
        using var fixture = new Fixture();
        const string id = "11111111bbbbbbbbbbbbbbbbbbbbbbbb";
        var wait = new HumanInputRequest(new HumanInputRequestId(id), new GoalId(GoalIdValue),
            null, "Question", Now, resumeCommand: "answer 11111111 11111111");
        var result = await FleetAttentionReader.ReadAsync(new([new("home", fixture.Home)]), 50, Now,
            read: (_, _, _) => Task.FromResult(Context() with { Waits = [wait] }));
        Assert.Equal($"--project=default --tenant=default answer {id} 11111111",
            Assert.Single(result.Items).NextAction);
    }

    [Fact]
    public async Task LiveAttention_ResolvedParkedTerminalAndFutureEvidence_StayExcluded()
    {
        using var fixture = new Fixture();
        var kernel = new AgentOrchestratorKernel();
        var live = kernel.CreateGoal(new GoalId(GoalIdValue), "Live");
        var parked = kernel.CreateGoal(new GoalId("22222222aaaaaaaaaaaaaaaaaaaaaaaa"), "Parked");
        kernel.ParkGoal(parked.Id, "Deferred");
        var terminal = kernel.CreateGoal(new GoalId("33333333aaaaaaaaaaaaaaaaaaaaaaaa"), "Terminal");
        var snapshot = kernel.ExportSnapshot();
        await fixture.SeedAsync(fixture.Home, AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == terminal.Id.Value ? goal with { Status = GoalStatus.Completed } : goal).ToArray()
        }));
        await fixture.WaitAsync(new("approval-wait", live.Id.Value, null, "Approve landing", Now, HumanWaitKind.OperatorApproval));
        await fixture.WaitAsync(new("risk-wait", live.Id.Value, null, "Escalation", Now, HumanWaitKind.RiskReview));
        await fixture.WaitAsync(new("owner-wait", live.Id.Value, null, "Owner input", Now, HumanWaitKind.Other));
        await fixture.WaitAsync(new("resolved-wait", live.Id.Value, null, "Answered", Now, IsCompleted: true, Answer: "Done", AnsweredAt: Now));
        await fixture.WaitAsync(new("future-review", live.Id.Value, null, "Future Reviewer", Now,
            HumanWaitKind.ProspectiveAcceptanceEvidence, EvidenceOwner: "reviewer"));
        await fixture.WaitAsync(new("future-acceptance", live.Id.Value, null, "Future Acceptance", Now,
            HumanWaitKind.ProspectiveAcceptanceEvidence, EvidenceOwner: "acceptance-gate"));
        await fixture.ItemAsync(fixture.Home, "live-item", live.Id.Value, Now);
        await fixture.ItemAsync(fixture.Home, "parked-item", parked.Id.Value, Now);
        await fixture.ItemAsync(fixture.Home, "terminal-item", terminal.Id.Value, Now);
        await fixture.ItemAsync(fixture.Home, "resolved-item", live.Id.Value, Now, CollaborationItemStatus.Resolved);
        await fixture.WaitAsync(new("parked-wait", parked.Id.Value, null, "Parked", Now));
        await fixture.WaitAsync(new("terminal-wait", terminal.Id.Value, null, "Terminal", Now));
        var persistedWaits = await SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Home.SqliteStatePath)
            .ListOpenHumanInputRequestsAsync();
        Assert.Equal(new[] { "approval-wait", "future-acceptance", "future-review", "owner-wait", "parked-wait", "risk-wait", "terminal-wait" },
            persistedWaits.Select(wait => wait.Id).Order(StringComparer.Ordinal));
        Assert.Equal(HumanWaitKind.OperatorApproval, persistedWaits.Single(wait => wait.Id == "approval-wait").Kind);
        var result = await FleetAttentionReader.ReadAsync(fixture.Catalog(), 50, Now);
        Assert.True(result.Complete);
        Assert.Equal(new[] { "live-item", "approval-wait", "owner-wait", "risk-wait" }, result.Items.Select(item => item.ItemId));
    }

    [Fact]
    public async Task OwnerHold_OriginalIdentityAndTime_PreservesQualifiedAction()
    {
        using var fixture = new Fixture();
        var snapshot = new AgentOrchestratorKernel();
        snapshot.CreateGoal(new GoalId(GoalIdValue), "Owner hold");
        var state = snapshot.ExportSnapshot();
        await fixture.SeedAsync(fixture.Home, AgentOrchestratorKernel.FromSnapshot(state with
        {
            Goals = state.Goals.Select(goal => goal with
            {
                CurrentHold = new GoalHoldSnapshot("original-hold-identity", "author-owner-question",
                    "author-owner-question item=HumanInput:full-wait-id reason=owner question=Original owner question recommendation=Answer", Now.AddHours(-2))
            }).ToArray()
        }));
        var result = await FleetAttentionReader.ReadAsync(fixture.Catalog(), 50, Now);
        var row = Assert.Single(result.Items);
        Assert.Equal("owner-question", row.Source);
        Assert.Equal("original-hold-identity", row.ItemId);
        Assert.Equal("Original owner question", row.Question);
        Assert.Equal(Now.AddHours(-2).UtcDateTime.ToString("O"), row.RaisedAt);
        Assert.Equal("--project=default --tenant=default answer full-wait-id --text-file <path>", row.NextAction);
    }

    [Theory]
    [InlineData("author-owner-question")]
    [InlineData("steward-owner-question")]
    public async Task OwnerHold_MalformedMarker_ExpandsFallbackWithCollidingGoals(string holdState)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(fixture.Home);
        var workspace = fixture.Register("alpha");
        var kernel = new AgentOrchestratorKernel();
        var goalId = new GoalId(GoalIdValue);
        kernel.CreateGoal(goalId, "Owner hold");
        kernel.CreateGoal(new GoalId("11111111bbbbbbbbbbbbbbbbbbbbbbbb"), "Same prefix");
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == goalId.Value ? goal with
            {
                CurrentHold = new GoalHoldSnapshot("malformed-owner-hold", holdState,
                    "Malformed owner hold marker", Now)
            } : goal).ToArray()
        });
        var heldGoal = kernel.Goals.Single(goal => goal.Id == goalId);
        Assert.Equal("attention show 11111111", OwnerQuestionHoldBlocker.TryRead(heldGoal)!.NextAction);
        Assert.Throws<InvalidOperationException>(() => AttentionReadGoalSelection.Resolve(kernel, "11111111"));
        await fixture.SeedAsync(workspace, kernel);

        var result = await FleetAttentionReader.ReadAsync(fixture.Catalog(), 50, Now);

        Assert.True(result.Complete);
        var row = Assert.Single(result.Items);
        Assert.Equal("alpha", row.Project);
        Assert.Equal("owner-question", row.Source);
        Assert.Equal(GoalIdValue, row.GoalId);
        const string commandPrefix = "--project=alpha --tenant=default attention show ";
        Assert.Equal(commandPrefix + GoalIdValue, row.NextAction);
        Assert.Equal(goalId, AttentionReadGoalSelection.Resolve(kernel, row.NextAction![commandPrefix.Length..]).Id);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cli_MissingProject_RemainsVisibleWithHealthyRows(bool json, bool zero)
    {
        using var fixture = new Fixture();
        var missing = fixture.Register("missing");
        await fixture.SeedAsync(fixture.Home);
        await fixture.ItemAsync(fixture.Home, ItemIdValue, GoalIdValue, Now);
        Assert.False(Directory.Exists(missing.OrchestratorDirectory));
        var before = fixture.Snapshot();
        string[] args = ["attention", "fleet", "--limit", zero ? "0" : "50", .. (json ? new[] { "--json" } : Array.Empty<string>())];
        var child = fixture.Run(args);
        Assert.Equal(1, child.ExitCode);
        if (json)
        {
            using var document = JsonDocument.Parse(child.StandardOutput);
            AssertEnvelope(document.RootElement);
            var payload = document.RootElement.GetProperty("payload");
            Assert.False(payload.GetProperty("complete").GetBoolean());
            Assert.Equal(1, payload.GetProperty("total").GetInt32());
            Assert.Equal(zero ? 1 : 0, payload.GetProperty("omitted").GetInt32());
            Assert.Equal(zero ? 0 : 1, payload.GetProperty("items").GetArrayLength());
            var unavailable = payload.GetProperty("projects").EnumerateArray().Single(project => project.GetProperty("project").GetString() == "missing");
            Assert.Equal("unavailable", unavailable.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("itemCount").ValueKind);
            Assert.Equal("missing-store", unavailable.GetProperty("error").GetProperty("code").GetString());
        }
        else
        {
            Assert.Contains("project-unavailable: missing [missing-store]", child.StandardOutput);
            Assert.Contains(ItemIdValue, child.StandardOutput);
            Assert.Contains("complete=false", child.StandardOutput);
        }
        Assert.False(Directory.Exists(missing.OrchestratorDirectory));
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("state.db")]
    [InlineData("collaboration-items.db")]
    public async Task Cli_MissingStore_DoesNotCreateOrMigrate(string store)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(fixture.Home);
        File.Delete(Path.Combine(fixture.Home.OrchestratorDirectory, store));
        var before = fixture.Snapshot();
        var child = fixture.Run("attention", "fleet", "--json");
        Assert.Equal(1, child.ExitCode);
        Assert.Contains("missing-store", child.StandardOutput);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("state.db")]
    [InlineData("collaboration-items.db")]
    public async Task Cli_UnsupportedSchema_ReportsFailureWithoutChangingBytes(string store)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(fixture.Home);
        fixture.Sql(Path.Combine(fixture.Home.OrchestratorDirectory, store), store == "state.db"
            ? "UPDATE meta SET value = '999' WHERE key = 'schema_version'"
            : "UPDATE store_schema_versions SET version = 999");
        var before = fixture.Snapshot();
        var child = fixture.Run("attention", "fleet", "--json");
        Assert.Equal(1, child.ExitCode);
        Assert.Contains("unsupported-store", child.StandardOutput);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task Cli_EmptyFleet_PublishesCompleteEmptyVersionedPayload()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(fixture.Home);
        var child = fixture.Run("attention", "fleet", "--json");
        Assert.Equal(0, child.ExitCode);
        using var document = JsonDocument.Parse(child.StandardOutput);
        AssertEnvelope(document.RootElement);
        var payload = document.RootElement.GetProperty("payload");
        Assert.True(payload.GetProperty("complete").GetBoolean());
        Assert.Equal(0, payload.GetProperty("total").GetInt32());
        Assert.Equal(0, payload.GetProperty("omitted").GetInt32());
        Assert.Empty(payload.GetProperty("items").EnumerateArray());
        var project = Assert.Single(payload.GetProperty("projects").EnumerateArray());
        Assert.Equal("home", project.GetProperty("project").GetString());
        Assert.Equal("available", project.GetProperty("status").GetString());
        Assert.Equal(0, project.GetProperty("itemCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, project.GetProperty("error").ValueKind);
        Assert.Empty(project.GetProperty("aliases").EnumerateArray());
    }

    [Theory]
    [InlineData("--limit")]
    [InlineData("--limit", "-1")]
    [InlineData("--limit", "1.2")]
    [InlineData("--limit", "2147483648")]
    [InlineData("--limit", "1", "--limit", "2")]
    [InlineData("--json", "--json")]
    [InlineData("--unexpected")]
    [InlineData("extra")]
    public void Cli_InvalidArguments_RefusesBeforeCreatingStores(params string[] flags)
    {
        using var fixture = new Fixture();
        var before = fixture.Snapshot();
        string[] args = ["attention", "fleet", .. flags];
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(args));
        var child = fixture.Run(args);
        Assert.Equal(1, child.ExitCode);
        Assert.Contains(CliFleetAttentionQueryCommand.Usage, child.StandardError);
        Assert.False(Directory.Exists(fixture.Home.OrchestratorDirectory));
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task DefaultLimit_FiftyRows_StillCountsEveryObservedItem()
    {
        using var fixture = new Fixture();
        var options = CliFleetAttentionQueryCommand.Parse(["attention", "fleet"]);
        Assert.False(options.Json);
        Assert.Equal(50, options.Limit);
        var rows = Enumerable.Range(0, 55).Select(index => new CollaborationItem($"item-{index:D3}",
            CollaborationItemType.Decision, null, CollaborationItemStatus.Raised, "Question", "Reason", null,
            Now.AddMinutes(index), null, null)).ToArray();
        var result = await FleetAttentionReader.ReadAsync(new([new("home", fixture.Home)]), options.Limit, Now,
            read: (_, _, _) => Task.FromResult(Context(rows)));
        Assert.Equal(55, result.Total);
        Assert.Equal(5, result.Omitted);
        Assert.Equal(rows.Take(50).Select(item => item.Id), result.Items.Select(item => item.ItemId));
        var unknown = await FleetAttentionReader.ReadAsync(new([new("home", fixture.Home)]), 50, Now,
            read: (_, _, _) => Task.FromResult(Context(rows[0] with { RaisedAt = DateTimeOffset.MinValue })));
        using var document = JsonDocument.Parse(AgentReadEnvelope.Serialize("mcg.fleet-attention", 1, unknown, JsonOptions));
        var item = Assert.Single(document.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("goalId").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("raisedAt").ValueKind);
    }

    [Fact]
    public async Task AliasReadFailure_ReportsOneOutcomeWithAllAliases()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var result = await FleetAttentionReader.ReadAsync(new([new("zulu", fixture.Home), new("alpha", fixture.Home)]),
            50, Now, read: (_, _, _) =>
            {
                calls++;
                throw new UnauthorizedAccessException("Denied fixture store");
            });
        Assert.Equal(1, calls);
        Assert.False(result.Complete);
        var project = Assert.Single(result.Projects);
        Assert.Equal("alpha", project.Project);
        Assert.Equal(new[] { "zulu" }, project.Aliases);
        Assert.Equal("unreadable-store", project.Error!.Code);
        Assert.Null(project.ItemCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Cli_ConflictingExplicitStateBinding_RefusesBeforeReads()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(fixture.Home);
        var before = fixture.Snapshot();
        var child = fixture.Run("attention", "fleet", "--json", "--repository-root", fixture.Repository,
            "--expected-state-directory", Path.Combine(fixture.Root, "wrong-state"));
        Assert.Equal(1, child.ExitCode);
        Assert.Contains("differs from expected", child.StandardError);
        Assert.Equal("", child.StandardOutput);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task RegistryFailure_PreservesResolvedHomeAndReportsEnumerationError()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(fixture.Home);
        await fixture.ItemAsync(fixture.Home, ItemIdValue, GoalIdValue, Now);
        var home = fixture.Home;
        Directory.CreateDirectory(fixture.Registry.RegistryDirectory);
        File.WriteAllText(fixture.Registry.RegistryPath, "malformed registry");
        var output = new StringWriter();
        var exit = await CliFleetAttentionQueryCommand.ExecuteAsync(["attention", "fleet", "--json"],
            fixture.Repository, null, fixture.Registry, output, home);
        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(output.ToString());
        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal(ItemIdValue, Assert.Single(payload.GetProperty("items").EnumerateArray()).GetProperty("itemId").GetString());
        Assert.Equal("registry-unavailable", Assert.Single(payload.GetProperty("errors").EnumerateArray()).GetProperty("code").GetString());
        Assert.False(payload.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task Cancellation_AfterFirstRead_StopsWithoutReadingNextProject()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var catalog = new AttentionWorkspaceCatalog([new("a", fixture.Home), new("b", fixture.Register("beta"))]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FleetAttentionReader.ReadAsync(catalog, 0, Now,
            cancellation.Token, (_, _, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                calls++;
                cancellation.Cancel();
                return Task.FromResult(Context());
            }));
        Assert.Equal(1, calls);
        var output = new StringWriter();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CliFleetAttentionQueryCommand.ExecuteAsync(
            ["attention", "fleet", "--json"], fixture.Repository, null, fixture.Registry, output,
            cancellationToken: cancellation.Token));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task Cli_HeldStateWriter_ReadsCommittedAttentionWithoutWriterAccess()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(fixture.Home);
        await fixture.ItemAsync(fixture.Home, ItemIdValue, GoalIdValue, Now);
        var before = fixture.Snapshot();
        using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = fixture.Home.SqliteStatePath, Pooling = false }.ToString()))
        {
            writer.Open();
            using var begin = writer.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE; UPDATE meta SET value = value WHERE key = 'schema_version'";
            begin.ExecuteNonQuery();
            try
            {
                using var rival = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = fixture.Home.SqliteStatePath, Pooling = false, DefaultTimeout = 1 }.ToString());
                rival.Open();
                using var probe = rival.CreateCommand();
                probe.CommandText = "BEGIN IMMEDIATE";
                Assert.Equal(5, Assert.Throws<SqliteException>(() => probe.ExecuteNonQuery()).SqliteErrorCode);
                var child = fixture.Run("attention", "fleet", "--json");
                Assert.Equal(0, child.ExitCode);
                using var document = JsonDocument.Parse(child.StandardOutput);
                Assert.Equal(ItemIdValue, Assert.Single(document.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray()).GetProperty("itemId").GetString());
            }
            finally
            {
                using var rollback = writer.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                rollback.ExecuteNonQuery();
            }
        }
        // Hash only after SQLite releases its handles; the CLI still ran under the proven writer lock.
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task Failures_RegistryAndUnresolvedAliases_KeepIndependentOutcomes()
    {
        using var fixture = new Fixture();
        var result = await FleetAttentionReader.ReadAsync(new([new("home", fixture.Home),
            new("alpha", null, new UnauthorizedAccessException("Access denied")),
            new("beta", null, new UnauthorizedAccessException("Access denied"))], new IOException("Registry unavailable")),
            0, Now, read: (_, _, _) => Task.FromResult(Context()));
        Assert.False(result.Complete);
        Assert.Equal("registry-unavailable", Assert.Single(result.Errors).Code);
        Assert.Equal(3, result.Projects.Count);
        Assert.All(result.Projects.Where(project => project.Project != "home"), project =>
        {
            Assert.Empty(project.Aliases);
            Assert.Equal("unreadable-store", project.Error!.Code);
        });
    }

    private static AttentionJsonReadContext Context(params CollaborationItem[] items) => new([], items, [], [], []);

    private static void AssertEnvelope(JsonElement envelope)
    {
        Assert.Equal("mcg.fleet-attention", envelope.GetProperty("schema").GetString());
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.True(DateTimeOffset.TryParse(envelope.GetProperty("generatedAt").GetString(), out _));
        var payload = envelope.GetProperty("payload");
        Assert.Equal(new[] { "complete", "errors", "items", "omitted", "projects", "total" },
            payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        foreach (var row in payload.GetProperty("items").EnumerateArray())
            Assert.Equal(new[] { "goalId", "itemId", "kind", "nextAction", "project", "question", "raisedAt", "source" },
                row.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("fleet-attention-").FullName;
        internal string Repository => Path.Combine(Root, "repository");
        internal string Data => Path.Combine(Root, "data");
        internal OrchestratorProjectRegistry Registry { get; }
        internal OrchestratorWorkspace Home => OrchestratorWorkspace.ForDirectory(Repository, null, null, Registry);
        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Repository, ".git"));
            Registry = new(Path.Combine(Root, "registry"), Data);
        }
        internal OrchestratorWorkspace Register(string name)
        {
            Registry.CreateProject(name, Repository);
            return OrchestratorWorkspace.ForProject(name, Repository, dataRootDirectory: Data);
        }
        internal AttentionWorkspaceCatalog Catalog() => OwnerConsoleBoardSource.ReadWorkspaceCatalog(Repository, null, Registry);
        internal async Task SeedAsync(OrchestratorWorkspace workspace, AgentOrchestratorKernel? kernel = null)
        {
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            if (kernel is null)
            {
                kernel = new();
                kernel.CreateGoal(new GoalId(GoalIdValue), "Fixture goal");
            }
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        }
        internal Task WaitAsync(HumanInputRequestSnapshot wait)
        {
            Sql(Home.SqliteStatePath, "INSERT INTO human_input_requests (id, goal_id, snapshot_json) VALUES ($id, $goal, $json)",
                ("$id", wait.Id), ("$goal", wait.GoalId), ("$json", JsonSerializer.Serialize(wait, SnapshotOptions)));
            return Task.CompletedTask;
        }
        internal Task ItemAsync(OrchestratorWorkspace workspace, string id, string? goalId, DateTimeOffset at,
            CollaborationItemStatus status = CollaborationItemStatus.Raised)
        {
            Sql(Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db"),
                "INSERT INTO collaboration_items (id, type, goal_id, status, subject, body, correlation_key, raised_at) VALUES ($id, 'Clarification', $goal, $status, 'Subject', 'Original question', $key, $at)",
                ("$id", id), ("$goal", goalId), ("$status", status.ToString()), ("$key", "spec-clarification:" + id), ("$at", at.ToString("O")));
            return Task.CompletedTask;
        }
        internal void Sql(string path, string sql, params (string Name, string? Value)[] values)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in values) command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
            Assert.True(command.ExecuteNonQuery() > 0);
        }
        // SQLite may maintain read-side WAL/SHM coordination files; durable stores and registry bytes must be stable.
        internal string[] Snapshot() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(Root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        internal CliChildProcessResult Run(params string[] args)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Repository, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment.Clear();
            foreach (var key in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "DOTNET_ROOT", "ProgramFiles", "USERPROFILE", "LOCALAPPDATA" })
                if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
            start.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = Repository;
            start.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = Registry.RegistryDirectory;
            start.Environment["MCG_ORCHESTRATOR_DATA"] = Data;
            start.Environment["MCG_ORCHESTRATOR_HOME"] = Path.Combine(Root, "home");
            start.Environment["MCG_DOTNET_ISOLATED_ROOT"] = Path.Combine(Root, "dotnet");
            start.Environment["OLLAMA_BASE_URL"] = "http://127.0.0.1:1";
            start.ArgumentList.Add(typeof(OrchestratorWorkspace).Assembly.Location);
            foreach (var arg in args) start.ArgumentList.Add(arg);
            return CliChildProcessRunner.Run(start);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
