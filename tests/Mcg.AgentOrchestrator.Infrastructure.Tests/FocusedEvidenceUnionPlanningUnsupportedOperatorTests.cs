using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;
using static FocusedEvidenceUnionPlanningSupport;

public sealed class FocusedEvidenceUnionPlanningUnsupportedOperatorTests
{
    [Xunit.Theory]
    [Xunit.InlineData("FullyQualifiedName~A&FullyQualifiedName~B")]
    [Xunit.InlineData("FullyQualifiedName~A|Category!=Local")]
    [Xunit.InlineData("FullyQualifiedName~A||FullyQualifiedName~B")]
    [Xunit.InlineData("FullyQualifiedName~A|Name~Method")]
    public void UnsupportedShapes_RejectBeforeLaunch(string request)
        => FocusedEvidenceUnionPlanningSupport.UnsupportedShapes_RejectBeforeLaunch(request);
}
