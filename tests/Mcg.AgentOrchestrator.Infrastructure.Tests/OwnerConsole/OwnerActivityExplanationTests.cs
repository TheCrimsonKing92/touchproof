using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: pure presentation of test-owned activity and decision values.
public sealed class OwnerActivityExplanationTests
{
    [Fact]
    public void ExplainWithFields_Landing_PreservesSentenceReasonAndNextStep()
    {
        var source = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "loop-relaunch", "11111111",
            "LOOP_RELAUNCH_SCHEDULED tick=7");
        var item = OwnerActivityNarrator.Item(source, "T", [source], "Landed: T (11111111)",
            "The work passed its checks and was added to main.", "The conductor will continue with the remaining work.");

        var detail = OwnerActivityExplanation.ExplainWithFields(item);

        var sentence = $"{source.Timestamp.ToLocalTime():HH:mm:ss} Landed: T (11111111)";
        Assert.StartsWith("What happened: " + sentence + "\n\n" +
            "Reason: The work passed its checks and was added to main.\n\n" +
            "Next step: The conductor will continue with the remaining work.\n\n", detail.Text);
        Assert.Equal(sentence, detail.Fields.Fields.Single(field => field.Key == "phrase").Value);
        Assert.Equal("The work passed its checks and was added to main.",
            detail.Fields.Fields.Single(field => field.Key == "why").Value);
        Assert.Equal("The conductor will continue with the remaining work.",
            detail.Fields.Fields.Single(field => field.Key == "next").Value);
        Assert.Equal(detail.Text, OwnerActivityExplanation.Explain(item));
    }
}
