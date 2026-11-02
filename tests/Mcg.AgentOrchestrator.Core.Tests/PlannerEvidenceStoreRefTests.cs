using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: parses local JSON and reads immutable guidance; no shared state or I/O.
public sealed class PlannerEvidenceStoreRefTests
{
    private const string ConductReference =
        "conduct-events:66767abd#2026-10-29T14:17:50Z/2026-10-29T14:36:04Z";

    [Fact]
    public void HistoricalRequestWithOnlyStoreRefDerivesStore()
    {
        var directive = ParseValid(Request());

        Assert.Equal("conduct-events", directive.PlannerEvidence!.Store);
        var reference = Assert.IsType<PlannerEvidenceStoreReference>(directive.StoreReference);
        Assert.Equal("conduct-events", reference.Kind);
        Assert.Equal("66767abd", reference.Locator);
        Assert.Equal("2026-10-29T14:17:50Z/2026-10-29T14:36:04Z", reference.Selector);
        Assert.Equal(reference, directive.PlannerEvidence.StoreReference);
        Assert.Contains("retrievable from store 'conduct-events'", directive.Question);
    }

    [Fact]
    public void MatchingExplicitStoreProducesEquivalentPayload()
    {
        var explicitRequest = Request();
        explicitRequest["store"] = "conduct-events";

        var explicitDirective = ParseValid(explicitRequest);
        var derivedDirective = ParseValid(Request());

        Assert.Equal("conduct-events", explicitDirective.PlannerEvidence!.Store);
        Assert.Equal(derivedDirective.PlannerEvidence, explicitDirective.PlannerEvidence);
        Assert.Equal(derivedDirective.Question, explicitDirective.Question);
    }

    [Theory]
    [InlineData("events")]
    [InlineData(".orchestrator/goal-events/66767abd.jsonl")]
    [InlineData("Conduct-Events")]
    public void DifferentExplicitStoreKeepsGivenStore(string store)
    {
        var request = Request();
        request["store"] = store;

        var directive = ParseValid(request);

        Assert.Equal(store, directive.PlannerEvidence!.Store);
        Assert.Equal("conduct-events", directive.StoreReference!.Kind);
        Assert.Contains($"retrievable from store '{store}'", directive.Question);
    }

    [Fact]
    public void StoreOnlyKeepsExistingPayloadAndQuestion()
    {
        var request = Request();
        request.Remove("store_ref");
        request["store"] = "events";

        var directive = ParseValid(request);

        Assert.Equal("events", directive.PlannerEvidence!.Store);
        Assert.Null(directive.StoreReference);
        Assert.Null(directive.PlannerEvidence.StoreReference);
        Assert.Equal("Planner evidence request for criterion 1: historical event. " +
            "Availability: retrievable from store 'events', which the worker cannot reach. " +
            "Reason: worker cannot reach the store", directive.Question);
    }

    [Fact]
    public void ContractOrchestratorRecordFormParsesWithoutStore()
    {
        var contract = string.Join("\n", AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Planner));
        Assert.Contains("For an orchestrator record, add store_ref as <kind>:<locator>[#<selector>].", contract);
        Assert.Contains("store_ref alone suffices; omitted store uses its kind.", contract);
        var request = Request();
        request["store_ref"] = "goal-events:66767abd#contains=feasibility";

        var directive = ParseValid(request);

        Assert.Equal("goal-events", directive.PlannerEvidence!.Store);
        Assert.Equal(new PlannerEvidenceStoreReference("historical-receipt", "goal-events", "66767abd",
            "contains=feasibility"), directive.StoreReference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(42)]
    [InlineData(false)]
    public void ExplicitInvalidStoreStillFailsValidation(object? store)
    {
        var request = Request();
        request["store"] = store;

        var parsed = Parse(request);

        Assert.Null(parsed.Directive);
        Assert.Equal("Malformed PLANNER_EVIDENCE_REQUEST: retrievable evidence must name a non-empty store.",
            parsed.Diagnostic);
    }

    private static Dictionary<string, object?> Request() => new()
    {
        ["criterion_index"] = 1,
        ["evidence_key"] = "historical-receipt",
        ["availability"] = "retrievable",
        ["needed"] = "historical event",
        ["reason"] = "worker cannot reach the store",
        ["store_ref"] = ConductReference
    };

    private static HumanInputDirective ParseValid(Dictionary<string, object?> request)
    {
        var parsed = Parse(request);
        Assert.Null(parsed.Diagnostic);
        return Assert.IsType<HumanInputDirective>(parsed.Directive);
    }

    private static HumanInputDirectiveParseResult Parse(Dictionary<string, object?> request) =>
        AgentOutputDirectives.ParseHumanInputRequest(
            "PLANNER_EVIDENCE_REQUEST: " + JsonSerializer.Serialize(request), AgentRole.Planner);
}
