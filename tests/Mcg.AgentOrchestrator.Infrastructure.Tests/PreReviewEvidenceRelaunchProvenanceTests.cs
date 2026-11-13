using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class PreReviewEvidenceRelaunchProvenanceTests
{
    [Fact]
    public void FirstAttempt_StoredWithoutProvenance_ReadsBackWithNull()
    {
        using var harness = new PreReviewEvidenceRelaunchProvenanceLaunchPathTests.Harness();
        var first = harness.Start();
        using var stored = JsonDocument.Parse(File.ReadAllText(first.MetadataPath));
        Assert.False(stored.RootElement.TryGetProperty("relaunchProvenance", out _));
        var read = harness.Coordinator.TryReadLatest(first.GoalId);
        Assert.NotNull(read);
        Assert.Equal(first.AttemptId, read.AttemptId);
        Assert.Null(read.RelaunchProvenance);
    }

    [Theory]
    [InlineData(0, "reconciled-fallthrough")]
    [InlineData(1, "regate-supersession")]
    [InlineData(2, "terminal-without-run-replaced")]
    [InlineData(3, "stale-replaced")]
    [InlineData(4, "did-not-run-retry")]
    public void Trigger_StoredLabel_RoundTrips(int value, string label)
    {
        var trigger = (PreReviewEvidenceRelaunchTrigger)value;
        var json = JsonSerializer.Serialize(trigger);
        Assert.Equal($"\"{label}\"", json);
        Assert.Equal(trigger, JsonSerializer.Deserialize<PreReviewEvidenceRelaunchTrigger>(json));
    }

    [Theory]
    [InlineData(0, "other")]
    [InlineData(1, "test-failure")]
    [InlineData(2, "timeout")]
    [InlineData(3, "apparatus-failure")]
    [InlineData(4, "passed")]
    [InlineData(5, "did-not-run")]
    public void PriorResultClass_StoredLabel_RoundTrips(int value, string label)
    {
        var resultClass = (PreReviewEvidencePriorResultClass)value;
        var json = JsonSerializer.Serialize(resultClass);
        Assert.Equal($"\"{label}\"", json);
        Assert.Equal(resultClass, JsonSerializer.Deserialize<PreReviewEvidencePriorResultClass>(json));
    }

    [Theory]
    [InlineData("\"future-value\"")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    public void UnknownLabels_ReadWithCompatibilityFallback(string json)
    {
        Assert.Equal(PreReviewEvidenceRelaunchTrigger.ReconciledFallthrough,
            JsonSerializer.Deserialize<PreReviewEvidenceRelaunchTrigger>(json));
        Assert.Equal(PreReviewEvidencePriorResultClass.Other,
            JsonSerializer.Deserialize<PreReviewEvidencePriorResultClass>(json));
    }
}
