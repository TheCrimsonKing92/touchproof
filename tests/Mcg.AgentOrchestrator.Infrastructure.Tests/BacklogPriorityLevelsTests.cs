using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BacklogPriorityLevelsTests
{
    [Theory]
    [InlineData("critical", "critical", 0)]
    [InlineData("Critical", "critical", 0)]
    [InlineData(" HIGH ", "high", 1)]
    [InlineData("normal", "normal", 2)]
    [InlineData(" low ", "low", 3)]
    public void Recognized_levels_are_canonical_and_ranked(string text, string expected, int rank)
    {
        Assert.True(BacklogPriorityLevels.TryParse(text, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(expected, BacklogPriorityLevels.ParseOrThrow(text));
        Assert.Equal(expected, BacklogPriorityLevels.Read(text));
        Assert.Equal(rank, BacklogPriorityLevels.Rank(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("urgent")]
    [InlineData("P1")]
    public void Invalid_stored_text_reads_as_normal_but_cannot_be_written(string? text)
    {
        Assert.False(BacklogPriorityLevels.TryParse(text, out _));
        Assert.Equal("normal", BacklogPriorityLevels.Read(text));
        Assert.Equal(2, BacklogPriorityLevels.Rank(text));
        if (text is null) return;
        var error = Assert.Throws<ArgumentException>(() => BacklogPriorityLevels.ParseOrThrow(text));
        foreach (var level in new[] { "critical", "high", "normal", "low" })
            Assert.Contains(level, error.Message);
    }
}
