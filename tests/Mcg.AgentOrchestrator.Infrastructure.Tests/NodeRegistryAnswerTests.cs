using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = NodeProjectDiscoveryAdapterTests.Fixture;

// Parallel-safe: validation is pure and each discovery test owns its file-only fixture.
public sealed class NodeRegistryAnswerTests
{
    private const string RegistryKey = "environment/node-registry-access";

    [Theory]
    [InlineData("yes", "yes")]
    [InlineData("YES", "yes")]
    [InlineData("no", "no")]
    [InlineData("No", "no")]
    public void Validate_RegistryAccess_NormalizesYesOrNo(string value, string expected)
    {
        Assert.True(OwnerAnswerValidator.HasRule(RegistryKey));
        Assert.Equal(expected, OwnerAnswerValidator.Validate(RegistryKey, value));
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("")]
    [InlineData(" yes")]
    [InlineData("yes ")]
    [InlineData("true")]
    public void Validate_RegistryAccessOtherValue_ThrowsArgumentException(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(RegistryKey, value));

        Assert.Contains(RegistryKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("requires yes or no", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("has no answer rule", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_UnrelatedUnknownKey_ListsRegistryAccessKey()
    {
        var error = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate("units/x/isTest", "yes"));

        Assert.Contains("has no answer rule in this slice", error.Message, StringComparison.Ordinal);
        Assert.Contains(RegistryKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    public void Discover_RegistryAnswer_ClosesQuestionOnlyWhenRecorded(string value)
    {
        using var fixture = new Fixture();
        var model = fixture.Discover();
        var answersDirectory = Path.Combine(fixture.Root, ".orchestrator");
        var missingAnswers = OwnerAnswerFile.Read(answersDirectory);
        Assert.Empty(missingAnswers);
        var unanswered = OwnerAnswerApplier.Apply(model, missingAnswers);
        Assert.Contains(unanswered.OpenQuestions, question => question.FactKey == RegistryKey);

        OwnerAnswerFile.Write(answersDirectory,
            [new OwnerAnswer(RegistryKey, OwnerAnswerValidator.Validate(RegistryKey, value), "fixture", DateTimeOffset.UnixEpoch)]);
        var recordedAnswers = OwnerAnswerFile.Read(answersDirectory);
        Assert.Equal(value, Assert.Single(recordedAnswers).Value);
        var answered = OwnerAnswerApplier.Apply(model, recordedAnswers);

        Assert.Empty(answered.StaleAnswers);
        var fact = Assert.Single(answered.AnsweredFacts);
        Assert.Equal(RegistryKey, fact.FactKey);
        Assert.Equal(value, fact.Fact.Value);
        Assert.DoesNotContain(answered.OpenQuestions, question => question.FactKey == RegistryKey);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == RegistryKey);
    }
}
