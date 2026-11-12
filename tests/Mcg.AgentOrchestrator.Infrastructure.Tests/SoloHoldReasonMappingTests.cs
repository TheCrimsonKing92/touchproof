using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: immutable reason constants and pure mapping.
public sealed class SoloHoldReasonMappingTests
{
    [Fact]
    public void EveryDeclaredReason_HasMechanismOwnerAndNextAction()
    {
        var reasons = typeof(SoloHoldReasons).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)).ToArray();
        Assert.NotEmpty(reasons);
        foreach (var field in reasons)
        {
            var reason = Assert.IsType<string>(field.GetRawConstantValue());
            var mapping = SoloHoldNextAction.For(reason);
            foreach (var value in new[] { mapping.Mechanism, mapping.Owner, mapping.NextAction })
            {
                Assert.False(string.IsNullOrWhiteSpace(value), field.Name);
                Assert.NotEqual("unmapped", value);
            }
        }
    }

    [Theory]
    [InlineData(SoloHoldReasons.EvidenceInvalid)]
    [InlineData(SoloHoldReasons.CandidateNotIntroduced)]
    [InlineData(SoloHoldReasons.CaptureUnavailable)]
    public void DetailFamilies_MapWithDiagnosticSuffix(string reason)
    {
        var mapping = SoloHoldNextAction.For(reason + ": diagnostic detail");
        Assert.Equal("operator", mapping.Owner);
        Assert.NotEqual("unmapped", mapping.Mechanism);
        Assert.NotEqual("unmapped", mapping.NextAction);
    }

    [Fact]
    public void UnknownReason_RemainsExplicitlyUnmapped()
    {
        Assert.Equal(("unmapped", "unmapped", "unmapped"), SoloHoldNextAction.For("unknown-solo-reason"));
    }
}
