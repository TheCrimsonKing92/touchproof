using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;
using static FocusedEvidenceUnionPlanningSupport;

public sealed class FocusedEvidenceUnionPlanningMalformedRequestTests
{
    [Xunit.Theory]
    [Xunit.InlineData("(FullyQualifiedName~A")]
    [Xunit.InlineData("FullyQualifiedName~A)")]
    [Xunit.InlineData("FullyQualifiedName~A&")]
    [Xunit.InlineData("FullyQualifiedName~MtpTestRunnerScriptTests.DoesNotExist")]
    [Xunit.InlineData("FullyQualifiedName~MtpTestRunnerScriptTests&FullyQualifiedName!~MtpTestRunnerScriptTests.BooleanGrammar_PreservesRepresentableOperatorsAndRejectsLoss")]
    public void UnsupportedShapes_RejectBeforeLaunch(string request)
        => FocusedEvidenceUnionPlanningSupport.UnsupportedShapes_RejectBeforeLaunch(request);
}
