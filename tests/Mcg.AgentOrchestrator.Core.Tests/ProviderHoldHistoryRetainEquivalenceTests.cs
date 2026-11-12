using Mcg.AgentOrchestrator.Core;

// Parallel-safe: generated in-memory goals and a fixed seed, with no external resources.
public sealed class ProviderHoldHistoryRetainEquivalenceTests
{
    private const int Seed = 730165;
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T00:00:00Z");
    private static readonly string[] Providers = ["xAI", "OpenAI", "Anthropic"];
    private static readonly string?[] Credentials = [null, @"C:\login-a", @"C:\login-b"];

    [Fact]
    public void Retain_FixedSeed256Scopes_MatchesLegacyAcrossSuccessiveCalls()
    {
        var random = new Random(Seed);
        var seenProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCredentials = new HashSet<string?>();
        var seenOutcomes = new HashSet<int>();
        var sawEmpty = false;
        var sawAll = false;
        var sawSubset = false;
        var reingested = 0;

        for (var scopeIndex = 0; scopeIndex < 256; scopeIndex++)
        {
            var shape = scopeIndex % 8;
            var count = random.Next(shape == 3 ? 2 : 1, 9);
            Goal[] working = shape is 2 or 3 ? [] : Enumerable.Range(0, random.Next(4))
                .Select(index => GenerateGoal($"{scopeIndex:D3}-working-{index}", shape == 1 ? 0 : null)).ToArray();
            var leaving = Enumerable.Range(0, count).Select(index =>
                GenerateGoal($"{scopeIndex:D3}-leaving-{index}",
                    shape == 1 ? 0 : shape == 2 || shape == 3 && index == 0 ? 1 : shape == 3 ? 0 : null)).ToArray();
            var reference = new ProviderHoldHistoryReferenceRetain();
            var actual = new ProviderHoldHistory();
            reference.Retain(leaving, working);
            actual.Retain(leaving, working);
            AssertSameResults(reference, actual, working, scopeIndex, 1);
            sawEmpty |= reference.Retained.Count == 0;
            sawAll |= reference.Retained.Count == leaving.Length;
            sawSubset |= reference.Retained.Count > 0 && reference.Retained.Count < leaving.Length;

            // Move an old working goal out and sometimes replace a retained goal by a new working copy.
            var nextWorking = working.Skip(1).ToList();
            if (scopeIndex % 2 == 0 && reference.Retained.FirstOrDefault() is { } retained)
            {
                nextWorking.Add(GenerateGoal(retained.Id.Value, null));
                reingested++;
            }
            var nextLeaving = Enumerable.Range(0, random.Next(1, 5))
                .Select(index => GenerateGoal($"{scopeIndex:D3}-next-{index}", null)).ToList();
            nextLeaving.AddRange(working.Take(1));
            nextLeaving.AddRange(nextWorking.Take(1)); // Leaving input can also contain a working id.
            reference.Retain(nextLeaving, nextWorking);
            actual.Retain(nextLeaving, nextWorking);
            AssertSameResults(reference, actual, nextWorking, scopeIndex, 2);
        }

        Assert.True(sawEmpty, "Corpus must include an empty retained set.");
        Assert.True(sawAll, "Corpus must include a fully retained candidate set.");
        Assert.True(sawSubset, "Corpus must include a strict retained subset.");
        Assert.True(reingested > 0, "Corpus must replace retained ids with working copies.");
        Assert.All(Providers, provider => Assert.Contains(provider, seenProviders));
        Assert.All(Credentials, credential => Assert.Contains(credential, seenCredentials));
        Assert.All(new[] { 0, 1, 2, 3 }, outcome => Assert.Contains(outcome, seenOutcomes));

        Goal GenerateGoal(string id, int? forcedOutcome)
        {
            var tasks = Enumerable.Range(0, forcedOutcome is null ? random.Next(1, 4) : 1).Select(taskIndex =>
            {
                var task = new TaskSpec(new($"{id}-task-{taskIndex}"), "Generated observation", AgentRole.Tester);
                var minute = random.Next(120);
                var dispatchCount = forcedOutcome is null ? random.Next(1, 4) : 1;
                for (var dispatchIndex = 0; dispatchIndex < dispatchCount; dispatchIndex++)
                {
                    var provider = forcedOutcome is null ? Providers[random.Next(Providers.Length)] : Providers[0];
                    var credential = forcedOutcome is null ? Credentials[random.Next(Credentials.Length)] : null;
                    seenProviders.Add(provider);
                    seenCredentials.Add(credential);
                    if (forcedOutcome is null && random.Next(4) == 0)
                    {
                        provider = $" {provider.ToUpperInvariant()} ";
                        if (credential is not null) credential = $" {credential.ToUpperInvariant()} ";
                    }
                    var at = Start.AddMinutes(minute + dispatchIndex * 3);
                    var fallback = forcedOutcome is null && random.Next(4) == 0;
                    task.RecordDispatch(new(fallback ? provider : "fixture", "fixture", @"C:\fixture", at,
                        ProviderName: fallback ? null : provider, ClaudeCredentialSourceDirectory: credential));
                    // 0 = unverified dispatch, 1 = exhaustion, 2 = verified recovery, 3 = other failure.
                    var outcome = forcedOutcome ?? random.Next(4);
                    seenOutcomes.Add(outcome);
                    if (outcome == 0) continue;
                    task.RecordVerification(new("fixture", @"C:\fixture", outcome == 2 ? 0 : 1, "",
                        outcome == 1 ? "Provider budget exhausted." : "", at.AddMinutes(1),
                        ProviderFailureKind: outcome == 1 ? ProviderFailureKind.BudgetExhausted
                            : outcome == 3 ? ProviderFailureKind.AuthenticationRequired : ProviderFailureKind.Unknown,
                        DispatchStartedAt: at, CompletionVerdictVerifiedSuccess: outcome == 2));
                }
                return task;
            }).ToArray();
            return new(new(id), "Generated retention scope", tasks);
        }
    }

    private static void AssertSameResults(ProviderHoldHistoryReferenceRetain reference, ProviderHoldHistory actual,
        IReadOnlyCollection<Goal> working, int scopeIndex, int call)
    {
        var expectedIds = reference.Retained.Select(goal => goal.Id.Value).ToArray();
        var actualIds = actual.Retained.Select(goal => goal.Id.Value).ToArray();
        Assert.True(expectedIds.SequenceEqual(actualIds),
            $"seed={Seed}, scope={scopeIndex}, call={call}: expected [{string.Join(",", expectedIds)}], actual [{string.Join(",", actualIds)}]");
        Assert.Equal(reference.Scope(working), actual.Scope(working));
    }
}
