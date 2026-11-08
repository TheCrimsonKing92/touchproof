using System.Collections.Frozen;

// Parallel-safe: immutable baseline data; no shared mutable test state.
internal static class OwnerConsoleSizeBaseline
{
    // File.ReadAllLines counts at the candidate head. Lower/remove rows as files shrink.
    // Raising a ceiling requires a one-line justification comment immediately above its row.
    internal static IReadOnlyDictionary<string, int> Files { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["OwnerConsoleBoardSource.cs"] = 289,
        ["OwnerConsoleEpicDialog.cs"] = 309,
        ["OwnerConsoleGateExecutionProjection.cs"] = 298,
        ["OwnerConsoleGateProgress.cs"] = 355,
        ["OwnerConsoleLoop.cs"] = 278,
        ["OwnerConsoleScreenController.cs"] = 288,
        ["OwnerConsoleSession.cs"] = 320,
        ["OwnerGoalChangeSummaryQuery.cs"] = 476
    }.AsReadOnly();

    // Syntax-node inclusive line spans at HEAD, including constructors and local functions.
    // Keys include enclosing types/methods; overloads share a key and are measured individually.
    // Remove a row once every overload is at most 60 lines. Additions require justification above the row.
    internal static IReadOnlySet<string> Methods { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "OwnerActivityDetailPresentation.cs::OwnerActivityDetailPresentation.Build",
        "OwnerActivityDiagnosticParser.cs::OwnerActivityDiagnosticParser.Parse",
        "OwnerActivityNarrator.cs::OwnerActivityNarrator.Narrate",
        "OwnerConsoleBindingRunner.cs::OwnerConsoleBindingRunner.RunAsync",
        "OwnerConsoleBoardSource.cs::OwnerConsoleBoardSource.ComposeAsync",
        "OwnerConsoleEpicDialog.cs::OwnerConsoleEpicDialog.HandleKeyAsync",
        "OwnerConsoleFullScreenHost.cs::OwnerConsoleFullScreenHost.RunCoreAsync",
        "OwnerConsoleGateExecutionProjection.cs::OwnerConsoleGateProgress.Reconstruct",
        "OwnerConsoleGateLayout.cs::OwnerConsoleGateLayout.Lines",
        "OwnerConsoleGateProgress.cs::OwnerConsoleGateProgress.Build",
        "OwnerConsoleGateProgress.cs::OwnerConsoleGateProgress.Build.Snapshot",
        "OwnerConsoleJsonlTail.cs::OwnerConsoleJsonlTail.Select",
        "OwnerConsoleKeyRouter.cs::OwnerConsoleKeyRouter.RouteAsync",
        "OwnerConsoleLineFitter.cs::OwnerConsoleLineFitter.Fit",
        "OwnerConsoleLoop.cs::OwnerConsoleLoop.RunAsync",
        "OwnerConsoleLoop.cs::OwnerConsoleLoop.RunAsync.RunStepAsync",
        "OwnerConsolePaneKeys.cs::OwnerConsolePaneKeys.HandleAsync",
        "OwnerConsoleScreenController.cs::OwnerConsoleScreenController.RunCommandAsync",
        "OwnerConsoleScreenController.cs::OwnerConsoleScreenController.RunCommandAsync.ExecuteAsync",
        "OwnerConsoleStageDescriber.cs::OwnerConsoleStageDescriber.Describe",
        "OwnerConsoleViewModelBuilder.cs::OwnerConsoleViewModelBuilder.BuildBoardAsync",
        "OwnerExperimentDecisionFlow.cs::OwnerExperimentDecisionFlow.RunAsync",
        "OwnerFleetDiagram.cs::OwnerFleetDiagram.Render",
        "OwnerGoalChangeSummaryQuery.cs::OwnerGoalChangeSummaryQuery.PlannedAsync",
        "OwnerGoalChangeSummaryQuery.cs::OwnerGoalChangeSummaryQuery.VerifiedAsync",
        "OwnerQuestionReadModel.cs::OwnerQuestionReadModel.ReadAsync",
        "OwnerQuestionResolutionReader.cs::OwnerQuestionResolutionReader.ListForGoalAsync"
    }.ToFrozenSet(StringComparer.Ordinal);
}
