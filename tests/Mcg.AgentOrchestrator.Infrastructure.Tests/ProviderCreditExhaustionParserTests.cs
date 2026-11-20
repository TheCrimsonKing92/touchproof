using Mcg.AgentOrchestrator.Core;

// Parallel-safe: parser inputs and results are entirely in memory.
public sealed class ProviderCreditExhaustionParserTests
{
    private const string ReportedMonthlySpendRefusal =
        "You've hit your monthly spend limit \u00B7 raise it at claude.ai/settings/usage?from=cc_cli_limit_message";

    [Xunit.Theory]
    [Xunit.InlineData("Credit balance is too low")]
    [Xunit.InlineData("Your credit balance is too low to access the Anthropic API.")]
    [Xunit.InlineData("API Error: 400 {\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Your credit balance is too low to access the Anthropic API.\"}}")]
    [Xunit.InlineData("cReDiT bAlAnCe Is ToO lOw")]
    [Xunit.InlineData("api error: 400 YOUR CREDIT BALANCE IS TOO LOW")]
    [Xunit.InlineData("You're out of usage credits. Switch to another model, or manage usage credits at https://claude.ai/settings/usage?from=cc_cli_limit_message, to continue.")]
    [Xunit.InlineData("You\u2019re out of usage credits. Switch to another model, or manage usage credits at https://claude.ai/settings/usage?from=cc_cli_limit_message, to continue.")]
    [Xunit.InlineData(ReportedMonthlySpendRefusal)]
    [Xunit.InlineData("You've hit your monthly spend limit")]
    [Xunit.InlineData("You\u2019ve hit your monthly spend limit")]
    [Xunit.InlineData("you've hit your monthly spend limit")]
    [Xunit.InlineData("You've hit your monthly spend limit. Raise it at https://example.test/usage")]
    public void FailedCreditRefusal_ReturnsExactLine(string output)
    {
        var matched = ProviderLimitEvidenceParser.TryGetCliCreditExhaustionLine(1, [output], out var line);

        Assert.True(matched);
        Assert.Equal(output, line);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0, "Credit balance is too low")]
    [Xunit.InlineData(0, "Your credit balance is too low to access the Anthropic API.")]
    [Xunit.InlineData(0, "API Error: 400 Your credit balance is too low")]
    [Xunit.InlineData(1, "Credit balance is too low\nI implemented the change.")]
    [Xunit.InlineData(1, "Credit balance is too low\nWORKER_RESULT:\nblockers: none\nEND_WORKER_RESULT")]
    [Xunit.InlineData(1, "  Credit balance is too low")]
    [Xunit.InlineData(1, "\tCredit balance is too low")]
    [Xunit.InlineData(1, "Credit balance is too low.cs:12: echoed source")]
    [Xunit.InlineData(1, "src/Worker.cs:12: Credit balance is too low")]
    [Xunit.InlineData(1, "API Error: 401 Unauthorized")]
    [Xunit.InlineData(1, "API Error: 401 Your credit balance is too low")]
    [Xunit.InlineData(1, "API Error: 400 invalid request")]
    [Xunit.InlineData(0, "You're out of usage credits. Switch to another model, or manage usage credits at https://claude.ai/settings/usage?from=cc_cli_limit_message, to continue.")]
    [Xunit.InlineData(0, "You\u2019re out of usage credits. Switch to another model, or manage usage credits at https://claude.ai/settings/usage?from=cc_cli_limit_message, to continue.")]
    [Xunit.InlineData(1, "You're out of usage credits. Switch to another model, or manage usage credits at https://claude.ai/settings/usage?from=cc_cli_limit_message, to continue.\nI implemented the change.")]
    [Xunit.InlineData(1, "You\u2019re out of usage credits. Switch to another model, or manage usage credits at https://claude.ai/settings/usage?from=cc_cli_limit_message, to continue.\nI implemented the change.")]
    [Xunit.InlineData(1, "  You're out of usage credits. Switch to another model, or manage usage credits at https://claude.ai/settings/usage?from=cc_cli_limit_message, to continue.")]
    [Xunit.InlineData(1, "  You\u2019re out of usage credits. Switch to another model, or manage usage credits at https://claude.ai/settings/usage?from=cc_cli_limit_message, to continue.")]
    [Xunit.InlineData(1, "You're out of usage credits.cs:12: echoed source")]
    [Xunit.InlineData(1, "You\u2019re out of usage credits.cs:12: echoed source")]
    [Xunit.InlineData(0, ReportedMonthlySpendRefusal)]
    [Xunit.InlineData(1, ReportedMonthlySpendRefusal + "\nI implemented the change.")]
    [Xunit.InlineData(1, "  " + ReportedMonthlySpendRefusal)]
    [Xunit.InlineData(1, "The fixture mentions " + ReportedMonthlySpendRefusal)]
    [Xunit.InlineData(1, "You've hit your monthly spend limit.cs:12: echoed source")]
    [Xunit.InlineData(1, "You've hit your weekly limit \u00B7 resets Oct 7, 12pm (America/Chicago)")]
    [Xunit.InlineData(1, "")]
    [Xunit.InlineData(1, " \n\t")]
    public void SuccessfulOrMixedOutput_RejectsCreditEvidence(int exitCode, string output)
    {
        var matched = ProviderLimitEvidenceParser.TryGetCliCreditExhaustionLine(
            exitCode, output.Split('\n'), out var line);

        Assert.False(matched);
        Assert.Equal(string.Empty, line);
    }

    [Xunit.Fact]
    public void MultipleRefusalsWithBlanks_ReturnsFirstExactLine()
    {
        const string first = "Your credit balance is too low to access the Anthropic API.";

        var matched = ProviderLimitEvidenceParser.TryGetCliCreditExhaustionLine(
            1, ["", "  ", first, "Credit balance is too low", ""], out var line);

        Assert.True(matched);
        Assert.Equal(first, line);
    }
}
