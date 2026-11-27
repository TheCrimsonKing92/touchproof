using Mcg.AgentOrchestrator.Core;

// Parallel-safe: in-memory kernels and deterministic dispatch receipts.
public sealed class ProviderHoldHistoryEvictionBoundTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T00:00:00Z");
    private static readonly string[] Providers = ["xAI", "Anthropic", "OpenAI"];
    private const string Model = "claude-sonnet-4-6";

    [Fact]
    public void Evict_UnrelatedBinding_HasConstantWorkAcrossHistorySizes()
    {
        var small = MeasureEviction(2_000);
        var large = MeasureEviction(4_000);
        Assert.Equal(small, large);
        Assert.InRange(large.BindingEvaluations, 1, 4);
        Assert.InRange(large.GoalVisits, 1, 12);

        static (int BindingEvaluations, int GoalVisits) MeasureEviction(int retainedCount)
        {
            var kernel = new AgentOrchestratorKernel();
            var sources = Enumerable.Range(0, retainedCount).Select(index =>
                Source($"old-{index:D4}", [$"provider-{index / 2:D4}"], 1 + index % 2)).ToArray();
            kernel.ProviderHoldHistory.Retain(sources, []);
            Assert.Equal(retainedCount, kernel.ProviderHoldHistory.Retained.Count);

            var leaving = Source("leaving", ["new-provider"], 5);
            kernel.ReplaceGoalWithSnapshot(leaving.ToSnapshot());
            Assert.Equal(1, kernel.EvictTerminalGoalAggregates([leaving.Id]));

            var work = kernel.ProviderHoldHistory.LastRetentionWork;
            Assert.Equal(retainedCount + 1, kernel.ProviderHoldHistory.Retained.Count);
            Assert.Contains(kernel.ProviderHoldHistory.Retained, goal => goal.Id == leaving.Id);
            return work;
        }
    }

    [Fact]
    public void Retain_SharedBinding_MatchesLegacyAndEvaluatesOnlyThatBinding()
    {
        var evaluated = new List<string>();
        var actual = CountProviders(evaluated);
        var legacy = new ProviderHoldHistoryLegacyRetain();
        var sources = Providers.Select((provider, index) => Source($"old-{index}", [provider], 0)).ToArray();
        actual.Retain(sources, []);
        legacy.Retain(sources, []);
        evaluated.Clear();

        var leaving = Source("recovery", [Providers[0]], 5, success: true);
        actual.Retain(leaving, []);
        legacy.Retain(leaving, []);

        Assert.Equal([Providers[0]], evaluated.Distinct());
        Assert.Equal(evaluated.Count, actual.LastRetentionWork.BindingEvaluations);
        Assert.DoesNotContain(actual.Retained, goal => goal.Id == sources[0].Id);
        AssertEquivalent(legacy, actual, [], [.. sources, leaving]);
    }

    [Fact]
    public void Retain_SharedGoal_ReachesSecondBindingAndPreservesLegacyRetention()
    {
        var evaluated = new List<string>();
        var actual = CountProviders(evaluated);
        var legacy = new ProviderHoldHistoryLegacyRetain();
        var bridge = Source("bridge", [Providers[0], Providers[1]], 1);
        Goal[] sources = [bridge, Source("other", [Providers[2]], 1), Source("second", [Providers[1]], 5)];
        actual.Retain(sources, []);
        legacy.Retain(sources, []);
        evaluated.Clear();

        var leaving = Source("recovery", [Providers[0]], 10, success: true);
        actual.Retain(leaving, []);
        legacy.Retain(leaving, []);

        Assert.Equal(Providers.Take(2).Order(), evaluated.Distinct().Order());
        Assert.Contains(actual.Retained, goal => goal.Id == bridge.Id);
        AssertEquivalent(legacy, actual, [], [.. sources, leaving]);
    }

    [Fact]
    public void Retain_DisplayCollision_PreservesLegacyMarkerResolutionAcrossRecovery()
    {
        const string credential = "/profiles/example";
        var unscoped = Source("unscoped", ["Anthropic"], 1, credential: $"{credential}|model={Model}");
        var scoped = Source("scoped", ["Anthropic"], 2, credential: credential, model: Model);
        var binding = new ProviderHoldBinding("Anthropic", credential, Model);
        var marker = Source("marker", [], 0);
        AppendReset(marker, binding, useIdentity: false);
        Goal[] sources = [unscoped, scoped, marker];
        Assert.Equal(2, ProviderHoldHistory.ExhaustedBindings(sources).Count);
        Assert.Single(ProviderHoldHistory.ExhaustedBindings(sources).Select(candidate => candidate.Key).Distinct());

        var legacy = new ProviderHoldHistoryLegacyRetain();
        var actual = new ProviderHoldHistory();
        actual.Replace(sources);
        legacy.Replace(sources);

        var recovery = Source("recovery", ["Anthropic"], 5, success: true, credential: credential, model: Model);
        actual.Retain(recovery, []);
        legacy.Retain(recovery, []);

        AssertEquivalent(legacy, actual, [], [.. sources, recovery]);
    }

    [Fact]
    public void Retain_FixedSeedWorkingChangesAndReingestion_MatchLegacy()
    {
        var random = new Random(71832);
        for (var trial = 0; trial < 32; trial++)
        {
            var actual = new ProviderHoldHistory();
            var legacy = new ProviderHoldHistoryLegacyRetain();
            var all = new List<Goal>();
            var working = new List<Goal> { Generate("first") };
            for (var step = 0; step < 8; step++)
            {
                var leaving = new List<Goal> { Generate($"step-{step}-a"), Generate($"step-{step}-b") };
                if (step % 2 == 0)
                {
                    leaving.AddRange(working);
                    working = [Generate($"step-{step}-working")];
                    if (legacy.Retained.FirstOrDefault() is { } item) working.Add(Generate(item.Id.Value));
                }
                else
                {
                    var provider = Providers[random.Next(Providers.Length)];
                    AppendReset(working[0], new(provider, null, null), useIdentity: step % 4 == 1);
                    working.Reverse();
                }
                all.AddRange(working);
                all.AddRange(leaving);
                actual.Retain(leaving, working);
                legacy.Retain(leaving, working);
                AssertEquivalent(legacy, actual, working, all);
            }
        }

        Goal Generate(string id)
        {
            var provider = Providers[random.Next(Providers.Length)];
            var scoped = provider == "Anthropic" && random.Next(2) == 0;
            return Source(id, random.Next(3) == 0 ? [provider, Providers[(Array.IndexOf(Providers, provider) + 1) % 3]] : [provider],
                random.Next(1, 30), success: random.Next(3) == 0, model: scoped ? Model : null);
        }
    }

    private static ProviderHoldHistory CountProviders(List<string> providers) => new((scope, provider, credential) =>
    {
        providers.Add(provider);
        return ProviderHoldHistory.Answer(scope, provider, credential);
    });

    private static void AssertEquivalent(ProviderHoldHistoryLegacyRetain legacy, ProviderHoldHistory actual,
        IReadOnlyCollection<Goal> working, IReadOnlyCollection<Goal> all)
    {
        Assert.Equal(legacy.Retained.Select(goal => goal.Id), actual.Retained.Select(goal => goal.Id));
        Assert.Equal(legacy.Scope(working).Select(goal => goal.Id), actual.Scope(working).Select(goal => goal.Id));
        foreach (var binding in ProviderHoldHistory.ExhaustedBindings(all))
            Assert.Equal(Answer(legacy.Scope(working), binding), Answer(actual.Scope(working), binding));

        static ProviderBudgetHoldSchedule? Answer(IReadOnlyCollection<Goal> scope, ProviderHoldBinding binding) =>
            ProviderLimitSignals.TryGetExactHold(scope, binding, out var hold)
                ? ProviderBudgetHoldSchedules.Evaluate(scope, hold, hold.PlacedAt) : null;
    }

    private static Goal Source(string id, string[] providers, int minute, bool success = false,
        string? credential = null, string? model = null)
    {
        var tasks = providers.Select((provider, index) =>
        {
            var task = new TaskSpec(new($"{id}-task-{index}"), "Provider observation", AgentRole.Tester);
            var at = Start.AddMinutes(minute);
            task.RecordDispatch(new("fixture", "fixture", @"C:\fixture", at, ProviderName: provider,
                ClaudeCredentialSourceDirectory: credential, ModelName: model));
            task.RecordVerification(new("fixture", @"C:\fixture", success ? 0 : 1,
                !success && model is not null ? "You've hit your sonnet limit" : "",
                !success && model is null ? "Provider budget exhausted." : "", at.AddMinutes(1),
                ProviderFailureKind: success ? ProviderFailureKind.Unknown : ProviderFailureKind.BudgetExhausted,
                DispatchStartedAt: at, CompletionVerdictVerifiedSuccess: success));
            return task;
        }).ToArray();
        var goal = tasks.Length == 0
            ? Goal.CreateMetadataOnlyTerminal(new(new GoalId(id), GoalStatus.Completed, "Provider marker"))
            : new Goal(new(id), "Provider evidence", tasks);
        goal.SetStatus(GoalStatus.Completed);
        return goal;
    }

    private static void AppendReset(Goal goal, ProviderHoldBinding binding, bool useIdentity)
    {
        var marker = ProviderBudgetHoldSchedules.FormatReset(new(binding.Key, Start.AddHours(3), $"reset-{goal.Id.Value}")
            { BindingIdentity = useIdentity ? binding.ScopedKey : null });
        goal.Append(new(goal.Id, null, ProgressKind.TaskNote, marker, Start.AddHours(1)));
    }
}
