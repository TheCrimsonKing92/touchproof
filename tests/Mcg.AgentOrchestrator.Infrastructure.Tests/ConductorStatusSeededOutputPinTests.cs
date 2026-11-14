// Parallel-safe: private workspace/databases, injected clock/probes and local writers.
public sealed class ConductorStatusSeededOutputPinTests
{
    [Fact]
    public async Task SeededStatus_PreservesExactHumanAndJsonRendering()
    {
        using var fixture = new ConductorStatusSeededFixture();
        await fixture.Seed();
        Assert.Equal(ExpectedHuman, fixture.Status());
        Assert.Equal(ExpectedJson + Environment.NewLine, fixture.Status(json: true));
    }

    // Authored from main's unchanged CliConductorStatusReader, ConductorStatusAgentReadView,
    // ProviderUsageHoldJson and ProviderUsageHoldView, and the existing human/blocker/hold pins.
    // These literals are independent of the candidate reader and are never captured from it.
    private static string ExpectedHuman => string.Join(Environment.NewLine,
        "Conductor: stopped",
        "Since: unavailable",
        "Supervisor build: unavailable",
        "Loop build: unavailable",
        "Generation: unavailable (no supervisor build recorded)",
        "Latest tick: unavailable",
        "Non-terminal goals: 5",
        "  aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa | Draft | role=Developer",
        "    clarification: id=seeded-scope hold=awaiting-clarification raised=2026-10-17T11:07:00.0000000+00:00 answer=attention answer aaaaaaaa seeded-scope <answer>",
        "  bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb | Active | role=Developer",
        "  cccccccccccccccccccccccccccccccc | Verified | role=Developer",
        "    landing-blocker: landing-completion/criterion-evidence-hold since=2026-10-17T11:07:00.0000000+00:00 next=evidence-owner verb=criterion-evidence-record detail=operator evidence outstanding",
        "  dddddddddddddddddddddddddddddddd | WaitingForHuman | role=Developer",
        "  eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee | Draft | role=Developer",
        "Pending clarifications: 1; human waits: 0",
        "Most recent landing: unavailable",
        "Worker adoption: unavailable",
        "Pending operator intents: unavailable (intent database missing)",
        "Provider usage holds: 1",
        "  xai::<provider-default> | attempt=1 | next_probe=2026-10-17T11:38:00.0000000+00:00 | reset_source=backoff | last_probe=none | source=11111111/11111111",
        "    diagnosis: latest check: none at 2026-10-17T11:08:00.0000000+00:00; last confirmed: exhausted at 2026-10-17T11:08:00.0000000+00:00 (age 60s); balance remains unverified until a successful availability check; historical exhaustion: 2026-10-17T11:08:00.0000000+00:00 (verification:2026-10-17T11:08:00.0000000+00:00)",
        "Owner directives: 0 active", "");

    // JSON intentionally omits the human-only landing-blocker/clarification detail fields,
    // exactly as main's agent-read envelope does; their aggregate count remains in goals.
    private const string ExpectedJson = """
        {"schema":"mcg.conductor-status","schemaVersion":1,"generatedAt":"2026-10-17T11:09:00.0000000Z","payload":{"conductor":{"state":"stopped","pid":null},"since":{"kind":"unavailable","at":null,"reason":null,"previous":null},"supervisorBuild":null,"loopBuild":{"commit":null,"source":"unavailable"},"generation":{"state":"no-build","build":null,"main":null},"latestTick":null,"goals":{"available":true,"nonTerminal":[{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","status":"Draft","role":"Developer"},{"id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","status":"Active","role":"Developer"},{"id":"cccccccccccccccccccccccccccccccc","status":"Verified","role":"Developer"},{"id":"dddddddddddddddddddddddddddddddd","status":"WaitingForHuman","role":"Developer"},{"id":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","status":"Draft","role":"Developer"}],"pendingClarifications":1,"humanWaits":0},"mostRecentLanding":null,"workerAdoption":null,"pendingIntents":{"state":"unavailable","reason":"intent database missing","goals":[]},"providerHolds":[{"bindingKey":"xai::\u003Cprovider-default\u003E","provider":"xAI","sourceGoalId":"11111111111111111111111111111111","sourceTaskId":"11111111111111111111111111111111","placedAt":"2026-10-17T11:08:00.0000000\u002B00:00","evidence":"verification:2026-10-17T11:08:00.0000000\u002B00:00","scheduleAvailable":true,"attempt":1,"nextProbeAt":"2026-10-17T11:38:00.0000000\u002B00:00","resetSource":"backoff","resetAt":null,"probeInFlight":false,"admittedGoalId":null,"admittedTaskId":null,"lastProbe":null,"resetCommand":"provider-reset xai::\u003Cprovider-default\u003E \u003CISO-8601 instant\u003E --text-file \u003Cpath\u003E","lastProbeAt":"2026-10-17T11:08:00+00:00","lastActionableFailure":"BudgetExhausted","lastActionableFailureAt":"2026-10-17T11:08:00+00:00","diagnosis":"latest check: none at 2026-10-17T11:08:00.0000000\u002B00:00; last confirmed: exhausted at 2026-10-17T11:08:00.0000000\u002B00:00 (age 60s); balance remains unverified until a successful availability check; historical exhaustion: 2026-10-17T11:08:00.0000000\u002B00:00 (verification:2026-10-17T11:08:00.0000000\u002B00:00)","resetIntentId":null}]}}
        """;
}
