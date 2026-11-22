using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: pure vocabulary translation with no shared state or I/O.
public sealed class OwnerActivityTextTests
{
    [Theory]
    [InlineData("waiting_for_approval", "waiting for your approval")]
    [InlineData(null, "a blocker needs review")]
    [InlineData("   ", "a blocker needs review")]
    [InlineData("question=Should we retry?", "Should we retry?")]
    [InlineData("reason=main_red", "main must be repaired before landing")]
    [InlineData("sticky_ticket", "persistent issue")]
    public void WaitingOn_CategoryQuestionOrMissingReason_ReturnsOwnerWording(
        string? value, string expected)
    {
        Assert.Equal(expected, OwnerActivityText.WaitingOn(value));
    }

    [Theory]
    [InlineData(null, "a blocker needs review")]
    [InlineData("Owner Review pending", "waiting for your approval")]
    [InlineData("HUMAN_INPUT", "waiting for your answer")]
    [InlineData("history_divergence", "the goal's history needs review")]
    [InlineData("slot_wait", "waiting for test or worker capacity")]
    [InlineData("main", "main must be repaired before landing")]
    [InlineData("gate_pending", "waiting for verification to finish")]
    [InlineData("cohort-tick", "a blocker needs review")]
    public void Blocker_CategoryOrUnknownReason_ReturnsOwnerWording(
        string? value, string expected)
    {
        Assert.Equal(expected, OwnerActivityText.Blocker(value));
    }

    [Theory]
    [InlineData(null, "fallback", "fallback")]
    [InlineData("   ", "fb", "fb")]
    [InlineData("  tick   eventKind  ", "fb", "step event type")]
    [InlineData("Receipt from CANARY", "", "proof from main check")]
    [InlineData("none", "", "no issue")]
    [InlineData("child result handoff cohort", "", "test result switch joint run")]
    [InlineData("ticket", "", "issue")]
    public void Words_InternalVocabularyOrBlankValue_ReturnsTranslationOrFallback(
        string? value, string fallback, string expected)
    {
        Assert.Equal(expected, OwnerActivityText.Words(value, fallback));
    }

    [Theory]
    [InlineData("TICK_END follows all per-tick work")]
    [InlineData("nonetheless")]
    [InlineData("ReceiptReader.Read")]
    [InlineData("a `tick` value")]
    [InlineData("`before tick after`")]
    [InlineData("tick. .tick _tick tick_ 2tick tick2")]
    [InlineData("\U00010400tick tick\U00010400 \U0001D7CEtick tick\U0001D7CE")]
    public void Words_TermInsideLargerWordOrIdentifier_IsUnchanged(string value)
    {
        Assert.Equal(value, OwnerActivityText.Words(value, ""));
    }

    [Theory]
    [InlineData("ticks", "steps")]
    [InlineData("CANARY", "main check")]
    [InlineData("receipt", "proof")]
    [InlineData("cohort", "joint run")]
    [InlineData("handoff", "switch")]
    [InlineData("none", "no issue")]
    [InlineData("handoffs", "switches")]
    [InlineData("[tickets], sticky!", "[issues], persistent!")]
    [InlineData("tick `before tick after` receipt `before cohort after` tick",
        "step `before tick after` proof `before cohort after` step")]
    [InlineData("tick` receipt", "tick` proof")]
    public void Words_StandaloneTermOrPlural_IsTranslated(string value, string expected)
    {
        Assert.Equal(expected, OwnerActivityText.Words(value, ""));
    }
}
